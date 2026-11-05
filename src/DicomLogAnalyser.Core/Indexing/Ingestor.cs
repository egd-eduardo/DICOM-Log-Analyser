using System.Globalization;
using System.Security.Cryptography;
using DicomLogAnalyser.Core.IO;
using DicomLogAnalyser.Core.Model;
using DicomLogAnalyser.Core.Parsing;
using DicomLogAnalyser.Core.Sources;
using Microsoft.Data.Sqlite;

namespace DicomLogAnalyser.Core.Indexing;

public sealed record ScanProgress(string SourceLabel, int FilesDone, int FilesTotal, long BytesRead, long EventsAdded);

public sealed class ScanSummary
{
    public string Status { get; set; } = "ok";
    public string? Message { get; set; }
    public int FilesSeen { get; set; }
    public int FilesChanged { get; set; }
    public int FilesUnreadable { get; set; }
    public int FilesUnrecognised { get; set; }
    public int FilesMissing { get; set; }
    public int FilesRenamed { get; set; }
    public int FilesReset { get; set; }
    public long EventsAdded { get; set; }
    public long BytesRead { get; set; }
    public int FilesNotSelected { get; set; }
    public int FilesNew { get; set; }
}

/// <summary>
/// Incremental, read-only ingestion of one source into the index.
/// <para>Each file keeps a committed offset: everything before it is indexed and final. The last record of a file is
/// always provisional (a stack trace may still be written, or the final line may be partial), so it is indexed for
/// searching but re-parsed on the next scan. A shrinking file (truncation) or a changed first block (replacement) causes
/// the file to be re-indexed from the start; a file that reappears under a new name with the same first block (rotation
/// by rename) keeps its index.</para>
/// </summary>
public sealed class Ingestor(IndexDatabase db, SourceRepository sources, TimeProvider time)
{
    public const int FingerprintBytes = 4096;
    public const int SniffLines = 200;
    private const int CheckpointEvents = 20_000;

    public ScanSummary Scan(SourceDefinition source, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        var summary = new ScanSummary();
        var tz = ResolveTimeZone(source.TimeZone);
        var hint = FormatRegistry.Find(source.FormatHint);

        if (!Directory.Exists(source.Path))
        {
            summary.Status = "unreachable";
            summary.Message = "The folder was not reachable during the scan. Previously indexed events are kept; recent events from this source may be missing.";
            sources.RecordScan(source.Id, summary.Status, summary.Message);
            return summary;
        }

        var enumerator = new FileEnumerator(source.Path, source.Recursive, PathValidator.SplitPatterns(source.IncludePatterns));
        List<FileInfo> files;
        try
        {
            files = enumerator.Enumerate().ToList();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            summary.Status = "unreachable";
            summary.Message = ex is UnauthorizedAccessException ? "Access to the folder was denied." : "The folder could not be listed (I/O error).";
            sources.RecordScan(source.Id, summary.Status, summary.Message);
            return summary;
        }

        var known = LoadFileStates(source.Id);
        var present = files.ToDictionary(f => Relative(source.Path, f.FullName), f => f, StringComparer.Ordinal);
        var renames = new List<(string Old, string New)>();
        DetectRenames(source.Id, known, present, summary, renames);
        if (source.Kind == SourceKinds.Folder) present = ApplySelection(source, tz, known, present, renames, summary);

        var total = present.Count;
        var done = 0;
        foreach (var (rel, file) in present.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            known.TryGetValue(rel, out var state);
            state ??= InsertFileState(source.Id, rel);
            ProcessFile(source, state, file, tz, hint, summary, progress, done, total, ct);
            summary.FilesSeen++;
            done++;
            progress?.Report(new ScanProgress(source.Label, done, total, summary.BytesRead, summary.EventsAdded));
        }

        foreach (var (rel, state) in known)
        {
            if (present.ContainsKey(rel)) continue;
            summary.FilesMissing++;
            if (state.Status != "missing") SetStatus(state.Id, "missing", "The file is no longer in the source (rotated out or deleted). Its indexed events are kept.");
        }

        var problems = new List<string>();
        if (summary.FilesUnreadable > 0) problems.Add($"{summary.FilesUnreadable} file(s) could not be read");
        if (summary.FilesUnrecognised > 0) problems.Add($"{summary.FilesUnrecognised} file(s) are not in a recognised log format");
        if (enumerator.InaccessibleDirectories > 0) problems.Add($"{enumerator.InaccessibleDirectories} subfolder(s) were not accessible");
        if (enumerator.Truncated) problems.Add($"only the first {FileEnumerator.MaxFiles} files were scanned");
        if (problems.Count > 0)
        {
            summary.Status = "warnings";
            summary.Message = string.Join("; ", problems) + ".";
        }

        sources.RecordScan(source.Id, summary.Status, summary.Message);
        db.MarkChanged();
        if (summary.EventsAdded > 0)
        {
            try
            {
                using var c = db.Open();
                IndexDatabase.Exec(c, "PRAGMA optimize;");
            }
            catch (SqliteException)
            {
                // Query-planner statistics are an optimisation only; the scan itself has completed.
            }
        }

        return summary;
    }

    public static TimeZoneInfo? ResolveTimeZone(string? id) => TimeZones.Resolve(id);

    private void ProcessFile(SourceDefinition source, FileState state, FileInfo file, TimeZoneInfo? tz, ILogFormat? hint, ScanSummary summary,
        IProgress<ScanProgress>? progress, int done, int total, CancellationToken ct)
    {
        FileStream stream;
        try
        {
            stream = LineReader.OpenReadOnly(file.FullName);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            summary.FilesUnreadable++;
            SetStatus(state.Id, "unreadable", ex is UnauthorizedAccessException ? "Access denied." : "The file could not be opened (locked or I/O error).");
            return;
        }

        try
        {
            ProcessOpenFile(source, state, file, stream, tz, hint, summary, progress, done, total, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The file was truncated, rotated or locked while it was being read. Work committed so far is kept and
            // the next scan continues from the last committed offset (or re-indexes the file if it was replaced).
            summary.FilesUnreadable++;
            SetStatus(state.Id, "unreadable", "The file changed or became unreadable while it was being read; it is retried at the next scan.");
        }
        finally
        {
            stream.Dispose();
        }
    }

    private void ProcessOpenFile(SourceDefinition source, FileState state, FileInfo file, FileStream stream, TimeZoneInfo? tz, ILogFormat? hint, ScanSummary summary,
        IProgress<ScanProgress>? progress, int done, int total, CancellationToken ct)
    {
        var size = stream.Length;
        var mtime = new DateTimeOffset(file.LastWriteTimeUtc).ToUnixTimeMilliseconds();
        var reset = false;
        if (state.FingerprintLength > 0)
        {
            if (size < state.CommittedOffset || size < state.FingerprintLength) reset = true;
            else if (!Fingerprint(stream, state.FingerprintLength).AsSpan().SequenceEqual(state.FingerprintHash)) reset = true;
        }

        if (!reset && state.FingerprintLength > 0 && size == state.Size && mtime == state.Mtime && state.Status is "ok" or "missing")
        {
            if (state.Status == "missing") SetStatus(state.Id, "ok", null);
            return;
        }

        if (!reset && state.Status == "unrecognised" && size == state.Size && mtime == state.Mtime)
        {
            summary.FilesUnrecognised++;
            return;
        }

        if (reset) summary.FilesReset++;
        var format = reset || state.Format is null ? null : FormatRegistry.Find(state.Format);
        var offset = reset ? 0 : state.CommittedOffset;
        var line = reset ? 1 : state.CommittedLine;
        if (format is null)
        {
            offset = 0;
            line = 1;
            format = hint ?? Sniff(stream);
            if (format is null)
            {
                summary.FilesUnrecognised++;
                ClearFile(state.Id);
                var fpLen = (int)Math.Min(size, FingerprintBytes);
                SaveFileState(state.Id, null, size, mtime, fpLen, Fingerprint(stream, fpLen), 0, 1, "unrecognised",
                    size == 0 ? "The file is empty." : "No line matched a supported log format (B3DWEB Gateway, DICOM Router) in the first lines of the file.");
                return;
            }
        }

        summary.FilesChanged++;
        var fingerprintLength = (int)Math.Min(size, FingerprintBytes);
        var fingerprint = Fingerprint(stream, fingerprintLength);
        IndexFrom(source, state.Id, stream, format, tz, offset, line, size, mtime, fingerprintLength, fingerprint, reset || offset == 0, summary, progress, done, total, ct);
    }

    private void IndexFrom(SourceDefinition source, long fileId, FileStream stream, ILogFormat format, TimeZoneInfo? tz, long offset, long line, long size, long mtime,
        int fpLen, byte[] fp, bool fromStart, ScanSummary summary, IProgress<ScanProgress>? progress, int done, int total, CancellationToken ct)
    {
        using var c = db.Open();
        var tx = c.BeginTransaction();
        try
        {
            using (var del = c.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = fromStart ? "DELETE FROM events WHERE file_id=$f" : "DELETE FROM events WHERE file_id=$f AND byte_start >= $o";
                del.Parameters.AddWithValue("$f", fileId);
                del.Parameters.AddWithValue("$o", offset);
                del.ExecuteNonQuery();
            }

            long lastTs = fromStart ? 0 : IndexDatabase.Scalar<long>(c, "SELECT sort_ts FROM events WHERE file_id=$f AND byte_start < $o ORDER BY byte_start DESC LIMIT 1", tx, ("$f", fileId), ("$o", offset));
            var insert = new EventInserter(c, tx);
            try
            {
                var assembler = new RecordAssembler(format, tz);
                var startPos = offset;
                using var reader = new LineReader(stream, offset, line);
                long pendingSince = 0;
                long lastLine = line - 1;
                while (reader.ReadLine() is { } l)
                {
                    lastLine = l.LineNumber;
                    var closed = assembler.Add(l);
                    if (closed is null) continue;
                    lastTs = Write(insert, closed, source.Id, fileId, lastTs);
                    summary.EventsAdded++;
                    if (++pendingSince < CheckpointEvents) continue;

                    // Checkpoint: everything before the open record is final.
                    var open = assembler.Pending;
                    var co = open?.ByteStart ?? reader.Position;
                    var cl = open?.LineStart ?? lastLine + 1;
                    SaveFileState(fileId, format.Id, size, mtime, fpLen, fp, co, cl, "partial", "Indexing in progress.", c, tx);
                    tx.Commit();
                    tx.Dispose();
                    ct.ThrowIfCancellationRequested();
                    tx = c.BeginTransaction();
                    insert.Dispose();
                    insert = new EventInserter(c, tx);
                    pendingSince = 0;
                    progress?.Report(new ScanProgress(source.Label, done, total, summary.BytesRead + (reader.Position - startPos), summary.EventsAdded));
                }

                var last = assembler.Close();
                long committedOffset;
                long committedLine;
                if (last is not null)
                {
                    Write(insert, last, source.Id, fileId, lastTs);
                    summary.EventsAdded++;
                    committedOffset = last.ByteStart;
                    committedLine = last.LineStart;
                }
                else
                {
                    committedOffset = reader.Position;
                    committedLine = lastLine + 1;
                }

                summary.BytesRead += reader.Position - startPos;
                SaveFileState(fileId, format.Id, reader.Position, mtime, fpLen, fp, committedOffset, committedLine, "ok", null, c, tx);
                using (var upd = c.CreateCommand())
                {
                    upd.Transaction = tx;
                    upd.CommandText = "UPDATE files SET unparsed_lines=(SELECT ifnull(sum(line_end-line_start+1),0) FROM events WHERE file_id=$f AND kind=1) WHERE id=$f";
                    upd.Parameters.AddWithValue("$f", fileId);
                    upd.ExecuteNonQuery();
                }

                tx.Commit();
            }
            finally
            {
                insert.Dispose();
            }
        }
        finally
        {
            tx.Dispose();
        }
    }

    private static long Write(EventInserter insert, LogEvent e, long sourceId, long fileId, long lastTs)
    {
        e.SourceId = sourceId;
        e.FileId = fileId;
        var sortTs = e.Timestamp ?? lastTs;
        insert.Insert(e, sortTs);
        return sortTs;
    }

    private static ILogFormat? Sniff(FileStream stream)
    {
        stream.Seek(0, SeekOrigin.Begin);
        var sample = new List<string>(SniffLines);
        using (var reader = new LineReader(stream, 0, 1))
        {
            while (sample.Count < SniffLines && reader.ReadLine() is { } l)
            {
                if (l.Text.Contains('\0', StringComparison.Ordinal)) return null;
                sample.Add(l.Text);
            }
        }

        stream.Seek(0, SeekOrigin.Begin);
        return FormatRegistry.Detect(sample);
    }

    internal static byte[] Fingerprint(Stream stream, int length)
    {
        stream.Seek(0, SeekOrigin.Begin);
        var buffer = new byte[length];
        stream.ReadExactly(buffer, 0, length);
        stream.Seek(0, SeekOrigin.Begin);
        return SHA256.HashData(buffer);
    }

    /// <summary>
    /// Rotation by rename: a known file that disappeared, or whose path now holds different content, and a new path
    /// whose first block matches its fingerprint are the same file. Its index moves to the new path.
    /// </summary>
    /// <summary>
    /// Folder sources analyse only the chosen files (at most <see cref="FileSelection.MaxDays"/> days, see
    /// <see cref="FileSelection"/>). Without a saved choice, the most recent days are chosen and saved. Files that are
    /// no longer chosen are removed from the index; new files wait for the user's choice.
    /// </summary>
    private Dictionary<string, FileInfo> ApplySelection(SourceDefinition source, TimeZoneInfo? tz, Dictionary<string, FileState> known,
        Dictionary<string, FileInfo> present, List<(string Old, string New)> renames, ScanSummary summary)
    {
        var zone = tz ?? TimeZoneInfo.Local;
        IReadOnlyList<string> selected;
        HashSet<string> seen;
        if (source.SelectedFiles is null)
        {
            selected = FileSelection.Default(present.Select(p => FileSelection.Candidate(source.Path, p.Value, zone)).ToList());
            sources.SetSelection(source.Id, selected, present.Keys);
            seen = new HashSet<string>(present.Keys, StringComparer.Ordinal);
        }
        else
        {
            selected = source.SelectedFiles;
            seen = new HashSet<string>(source.SeenFiles ?? (IEnumerable<string>)present.Keys, StringComparer.Ordinal);
            // Log rotation: the renamed file keeps its indexed content (its new name is added to the choice) and the chosen
            // name stays chosen, so the file the application recreates under that name continues to be analysed.
            var renamed = selected.Concat(renames.Where(r => selected.Contains(r.Old, StringComparer.Ordinal)).Select(r => r.New))
                .Distinct(StringComparer.Ordinal).ToList();
            if (!renamed.SequenceEqual(selected, StringComparer.Ordinal))
            {
                sources.RenameSelected(source.Id, renamed);
                selected = renamed;
            }
        }

        var chosen = new HashSet<string>(selected, StringComparer.Ordinal);
        foreach (var (rel, state) in known.Where(k => !chosen.Contains(k.Key)).ToList())
        {
            DeleteFile(state.Id);
            known.Remove(rel);
        }

        var unselected = present.Where(p => !chosen.Contains(p.Key)).ToList();
        summary.FilesNotSelected = unselected.Count;
        // New = not in the folder when the choice was made (an unticked file that keeps growing is not "new").
        summary.FilesNew = unselected.Count(p => !seen.Contains(p.Key));
        sources.RecordFileCounts(source.Id, summary.FilesNotSelected, summary.FilesNew);
        return present.Where(p => chosen.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
    }

    private void DeleteFile(long fileId)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM events WHERE file_id=$f; DELETE FROM files WHERE id=$f;";
            cmd.Parameters.AddWithValue("$f", fileId);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    private void DetectRenames(long sourceId, Dictionary<string, FileState> known, Dictionary<string, FileInfo> present, ScanSummary summary,
        List<(string Old, string New)> renames)
    {
        var candidates = new List<FileState>();
        foreach (var (rel, state) in known)
        {
            if (state.FingerprintLength == 0) continue;
            if (!present.TryGetValue(rel, out var file))
            {
                candidates.Add(state);
                continue;
            }

            if (file.Length < state.FingerprintLength || !PrefixMatches(file, state)) candidates.Add(state);
        }

        if (candidates.Count == 0) return;
        foreach (var (rel, file) in present.Where(p => !known.ContainsKey(p.Key)).ToList())
        {
            var match = candidates.FirstOrDefault(c => !c.Renamed && file.Length >= c.FingerprintLength && file.Length >= c.CommittedOffset && PrefixMatches(file, c));
            if (match is null) continue;
            using (var c = db.Open())
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "UPDATE files SET rel_path=$p WHERE id=$id AND source_id=$s";
                cmd.Parameters.AddWithValue("$p", rel);
                cmd.Parameters.AddWithValue("$id", match.Id);
                cmd.Parameters.AddWithValue("$s", sourceId);
                cmd.ExecuteNonQuery();
            }

            match.Renamed = true;
            renames.Add((match.RelPath, rel));
            // The old path, if it still exists, now holds a new file and gets a fresh state.
            known.Remove(match.RelPath);
            known[rel] = match with { RelPath = rel, Renamed = false };
            summary.FilesRenamed++;
        }
    }

    private static bool PrefixMatches(FileInfo file, FileState state)
    {
        try
        {
            using var s = LineReader.OpenReadOnly(file.FullName);
            return s.Length >= state.FingerprintLength && Fingerprint(s, state.FingerprintLength).AsSpan().SequenceEqual(state.FingerprintHash);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return true; // Treated as unchanged here; the read error is reported when the file itself is processed.
        }
    }

    private static string Relative(string root, string full) => Path.GetRelativePath(root, full).Replace('\\', '/');

    private sealed record FileState(long Id, string RelPath, string? Format, long Size, long Mtime, int FingerprintLength, byte[] FingerprintHash,
        long CommittedOffset, long CommittedLine, string Status)
    {
        public bool Renamed { get; set; }
    }

    private Dictionary<string, FileState> LoadFileStates(long sourceId)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, rel_path, format, size, mtime_utc, fp_len, fp_hash, committed_offset, committed_line, status FROM files WHERE source_id=$s";
        cmd.Parameters.AddWithValue("$s", sourceId);
        using var r = cmd.ExecuteReader();
        var result = new Dictionary<string, FileState>(StringComparer.Ordinal);
        while (r.Read())
        {
            var state = new FileState(r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetInt64(3), r.GetInt64(4), r.GetInt32(5),
                r.IsDBNull(6) ? [] : (byte[])r.GetValue(6), r.GetInt64(7), r.GetInt64(8), r.GetString(9));
            result[state.RelPath] = state;
        }

        return result;
    }

    private FileState InsertFileState(long sourceId, string rel)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO files (source_id, rel_path, status) VALUES ($s, $p, 'pending'); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$s", sourceId);
        cmd.Parameters.AddWithValue("$p", rel);
        var id = (long)cmd.ExecuteScalar()!;
        return new FileState(id, rel, null, 0, 0, 0, [], 0, 1, "pending");
    }

    private void ClearFile(long fileId)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM events WHERE file_id=$f";
        cmd.Parameters.AddWithValue("$f", fileId);
        cmd.ExecuteNonQuery();
    }

    private void SetStatus(long fileId, string status, string? message)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE files SET status=$st, status_message=$m, last_scanned_utc=$t WHERE id=$f";
        cmd.Parameters.AddWithValue("$st", status);
        cmd.Parameters.AddWithValue("$m", (object?)message ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$t", time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$f", fileId);
        cmd.ExecuteNonQuery();
    }

    private void SaveFileState(long fileId, string? format, long size, long mtime, int fpLen, byte[] fp, long committedOffset, long committedLine,
        string status, string? message, SqliteConnection? c = null, SqliteTransaction? tx = null)
    {
        var own = c is null;
        c ??= db.Open();
        try
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE files SET format=$fmt, size=$size, mtime_utc=$mt, fp_len=$fl, fp_hash=$fh, committed_offset=$co, committed_line=$cl,
                    status=$st, status_message=$m, last_scanned_utc=$t WHERE id=$f
                """;
            cmd.Parameters.AddWithValue("$fmt", (object?)format ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$size", size);
            cmd.Parameters.AddWithValue("$mt", mtime);
            cmd.Parameters.AddWithValue("$fl", fpLen);
            cmd.Parameters.AddWithValue("$fh", fp);
            cmd.Parameters.AddWithValue("$co", committedOffset);
            cmd.Parameters.AddWithValue("$cl", committedLine);
            cmd.Parameters.AddWithValue("$st", status);
            cmd.Parameters.AddWithValue("$m", (object?)message ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$t", time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$f", fileId);
            cmd.ExecuteNonQuery();
        }
        finally
        {
            if (own) c.Dispose();
        }
    }

    /// <summary>Prepared insert statement reused for every event of a transaction.</summary>
    private sealed class EventInserter : IDisposable
    {
        private readonly SqliteCommand _cmd;
        private readonly SqliteParameter[] _p;

        public EventInserter(SqliteConnection c, SqliteTransaction tx)
        {
            _cmd = c.CreateCommand();
            _cmd.Transaction = tx;
            _cmd.CommandText = """
                INSERT INTO events (file_id, source_id, line_start, line_end, byte_start, byte_end, ts, sort_ts, utc_known, ts_text, level, level_text, kind,
                    category, component, message, detail, has_exception)
                VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17,$18)
                """;
            _p = new SqliteParameter[18];
            for (var i = 0; i < _p.Length; i++)
            {
                var p = _cmd.CreateParameter();
                p.ParameterName = "$" + (i + 1).ToString(CultureInfo.InvariantCulture);
                p.Value = DBNull.Value;
                _cmd.Parameters.Add(p);
                _p[i] = p;
            }

            _cmd.Prepare();
        }

        public void Insert(LogEvent e, long sortTs)
        {
            _p[0].Value = e.FileId;
            _p[1].Value = e.SourceId;
            _p[2].Value = e.LineStart;
            _p[3].Value = e.LineEnd;
            _p[4].Value = e.ByteStart;
            _p[5].Value = e.ByteEnd;
            _p[6].Value = (object?)e.Timestamp ?? DBNull.Value;
            _p[7].Value = sortTs;
            _p[8].Value = e.UtcKnown ? 1 : 0;
            _p[9].Value = (object?)e.TimestampText ?? DBNull.Value;
            _p[10].Value = (int)e.Level;
            _p[11].Value = e.LevelText;
            _p[12].Value = (int)e.Kind;
            _p[13].Value = e.Category;
            _p[14].Value = (object?)e.Component ?? DBNull.Value;
            _p[15].Value = e.Message;
            _p[16].Value = (object?)e.Detail ?? DBNull.Value;
            _p[17].Value = e.HasException ? 1 : 0;
            _cmd.ExecuteNonQuery();
        }

        public void Dispose() => _cmd.Dispose();
    }
}
