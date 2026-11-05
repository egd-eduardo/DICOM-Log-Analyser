using System.Globalization;
using System.Text;
using DicomLogAnalyser.Core.Model;
using DicomLogAnalyser.Core.Sources;
using Microsoft.Data.Sqlite;

namespace DicomLogAnalyser.Core.Indexing;

public sealed record SearchRequest
{
    public string? Text { get; init; }
    public EventLevel? MinLevel { get; init; }
    public IReadOnlyList<long>? SourceIds { get; init; }
    public string? Category { get; init; }
    public long? FromMs { get; init; }
    public long? ToMs { get; init; }
    public bool ExceptionsOnly { get; init; }
    public bool IncludeUnparsed { get; init; } = true;
    public bool OldestFirst { get; init; }
    public int Limit { get; init; } = 100;
    public string? Cursor { get; init; }
}

public sealed record EventSummary(long Id, long SourceId, string SourceLabel, string File, long LineStart, long LineEnd, long? Timestamp, bool UtcKnown,
    string? TimestampText, EventLevel Level, string LevelText, EventKind Kind, string Category, string? Component, string Message, bool HasDetail, bool HasException);

public sealed record SearchResponse(IReadOnlyList<EventSummary> Items, string? NextCursor, long MatchCount, bool MatchCountCapped,
    IReadOnlyDictionary<string, long> LevelCounts, IReadOnlyList<string> CoverageNotes, bool QueryIgnored);

public sealed record EventDetail(EventSummary Event, string? Detail, long ByteStart, long ByteEnd, string SourceKind, string SourcePath, string? Format,
    string FileStatus, long? PreviousId, long? NextId, string? OriginalText, string EvidenceStatus);

public sealed class SearchService(IndexDatabase db)
{
    public const int MaxLimit = 500;
    public const int CountCap = 200_000;
    public const int MaxEvidenceBytes = 1024 * 1024;
    private const string SummaryColumns = "e.id, e.source_id, s.label, f.rel_path, e.line_start, e.line_end, e.ts, e.utc_known, e.ts_text, e.level, e.level_text, e.kind, e.category, e.component, substr(e.message, 1, 400), e.detail IS NOT NULL, e.has_exception";

    public SearchResponse Search(SearchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var query = FtsQuery.Parse(request.Text);
        var fts = query.Match;
        var queryIgnored = !string.IsNullOrWhiteSpace(request.Text) && query.IsEmpty && query.ShortExcluded.Count == 0;
        using var c = db.Open();

        // One read snapshot for the page, the counts and the coverage notes: a scan committing between these queries
        // would otherwise show a count that disagrees with the listed events (WAL readers see each new commit).
        using var snapshot = ReadSnapshot.Begin(c);
        var (where, args) = BuildWhere(request, query);
        var limit = Math.Clamp(request.Limit, 1, MaxLimit);
        var desc = !request.OldestFirst;

        var pageWhere = where;
        var pageArgs = new List<(string, object?)>(args);
        if (TryParseCursor(request.Cursor, out var cTs, out var cId))
        {
            pageWhere += desc ? " AND (e.sort_ts < $cts OR (e.sort_ts = $cts AND e.id < $cid))" : " AND (e.sort_ts > $cts OR (e.sort_ts = $cts AND e.id > $cid))";
            pageArgs.Add(("$cts", cTs));
            pageArgs.Add(("$cid", cId));
        }

        var items = new List<EventSummary>();
        var order = desc ? "e.sort_ts DESC, e.id DESC" : "e.sort_ts ASC, e.id ASC";
        long lastSort = 0;
        using (var cmd = Command(c, $"SELECT {SummaryColumns}, e.sort_ts FROM {From(fts)} CROSS JOIN files f ON f.id=e.file_id CROSS JOIN sources s ON s.id=e.source_id WHERE {pageWhere} ORDER BY {order} LIMIT $limit", pageArgs))
        {
            cmd.Parameters.AddWithValue("$limit", limit + 1);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (items.Count == limit) break;
                items.Add(ReadSummary(r));
                lastSort = r.GetInt64(17);
            }
        }

        string? next = null;
        if (items.Count == limit) next = string.Create(CultureInfo.InvariantCulture, $"{lastSort}:{items[^1].Id}");

        var levelCounts = new Dictionary<string, long>(StringComparer.Ordinal);
        long total = 0;
        using (var cmd = Command(c, $"SELECT level, count(*) FROM (SELECT e.level FROM {From(fts)} WHERE {where} LIMIT {CountCap + 1}) GROUP BY level", args))
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                var level = (EventLevel)r.GetInt32(0);
                var n = r.GetInt64(1);
                levelCounts[level.ToString().ToLowerInvariant()] = n;
                total += n;
            }
        }

        var capped = total > CountCap;
        return new SearchResponse(items, next, Math.Min(total, CountCap), capped, levelCounts, CoverageNotes(c, request), queryIgnored);
    }

    /// <summary>Streams matching events (oldest first) for CSV export, up to <paramref name="max"/> rows.</summary>
    public IEnumerable<EventSummary> Export(SearchRequest request, int max)
    {
        ArgumentNullException.ThrowIfNull(request);
        var query = FtsQuery.Parse(request.Text);
        var fts = query.Match;
        using var c = db.Open();
        var (where, args) = BuildWhere(request, query);
        using var cmd = Command(c, $"SELECT {SummaryColumns}, e.sort_ts FROM {From(fts)} CROSS JOIN files f ON f.id=e.file_id CROSS JOIN sources s ON s.id=e.source_id WHERE {where} ORDER BY e.sort_ts ASC, e.id ASC LIMIT $limit", args);
        cmd.Parameters.AddWithValue("$limit", max);
        using var r = cmd.ExecuteReader();
        while (r.Read()) yield return ReadSummary(r);
    }

    public EventDetail? Get(long id)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"""
            SELECT {SummaryColumns}, e.detail, e.byte_start, e.byte_end, s.kind, s.path, f.format, f.status, f.fp_len, f.fp_hash, e.file_id, e.message
            FROM events e JOIN files f ON f.id=e.file_id JOIN sources s ON s.id=e.source_id WHERE e.id=$id
            """;
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        var summary = ReadSummary(r) with { Message = r.GetString(27) };
        var detail = r.IsDBNull(17) ? null : r.GetString(17);
        long byteStart = r.GetInt64(18), byteEnd = r.GetInt64(19);
        var kind = r.GetString(20);
        var root = r.GetString(21);
        var format = r.IsDBNull(22) ? null : r.GetString(22);
        var status = r.GetString(23);
        var fpLen = r.GetInt32(24);
        var fp = r.IsDBNull(25) ? [] : (byte[])r.GetValue(25);
        var fileId = r.GetInt64(26);
        r.Close();

        var prev = IndexDatabase.Scalar<long?>(c, "SELECT id FROM events WHERE file_id=$f AND byte_start < $b ORDER BY byte_start DESC LIMIT 1", null, ("$f", fileId), ("$b", byteStart));
        var next = IndexDatabase.Scalar<long?>(c, "SELECT id FROM events WHERE file_id=$f AND byte_start > $b ORDER BY byte_start ASC LIMIT 1", null, ("$f", fileId), ("$b", byteStart));
        var (original, evidence) = ReadEvidence(Path.Combine(root, summary.File), byteStart, byteEnd, fpLen, fp);
        return new EventDetail(summary, detail, byteStart, byteEnd, kind, root, format, status, prev == 0 ? null : prev, next == 0 ? null : next, original, evidence);
    }

    /// <summary>Re-reads the original bytes from the source file, read-only, and states whether the file still matches.</summary>
    internal static (string? Text, string Status) ReadEvidence(string path, long start, long end, int fpLen, byte[] fp)
    {
        if (end - start > MaxEvidenceBytes) return (null, "too_large");
        try
        {
            using var s = IO.LineReader.OpenReadOnly(path);
            if (s.Length < end || s.Length < fpLen) return (null, "file_changed");
            if (fpLen > 0 && !Ingestor.Fingerprint(s, fpLen).AsSpan().SequenceEqual(fp)) return (null, "file_changed");
            s.Seek(start, SeekOrigin.Begin);
            var buffer = new byte[end - start];
            s.ReadExactly(buffer);
            var text = IO.LineReader.Decode(buffer).TrimEnd('\r', '\n');
            if (start == 0 && text.Length > 0 && text[0] == '﻿') text = text[1..];
            return (text, "verified");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, "file_unavailable");
        }
    }

    private static List<string> CoverageNotes(SqliteConnection c, SearchRequest request)
    {
        var notes = new List<string>();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT s.id, s.label, s.enabled, s.last_scan_status, s.last_scan_utc,
                (SELECT min(sort_ts) FROM events e WHERE e.source_id=s.id),
                (SELECT max(sort_ts) FROM events e WHERE e.source_id=s.id),
                (SELECT count(*) FROM files f WHERE f.source_id=s.id AND f.status IN ('unreadable','missing','unrecognised')),
                s.time_zone,
                CASE WHEN s.time_zone IS NULL AND EXISTS (SELECT 1 FROM files f WHERE f.source_id=s.id AND f.format='b3dweb-gateway') THEN 1 END,
                s.unselected_files, s.new_files
            FROM sources s ORDER BY s.label
            """;
        using var r = cmd.ExecuteReader();
        var any = false;
        while (r.Read())
        {
            any = true;
            var id = r.GetInt64(0);
            if (request.SourceIds is { Count: > 0 } ids && !ids.Contains(id)) continue;
            var label = r.GetString(1);
            if (r.GetInt64(2) == 0) notes.Add($"\"{label}\" is disabled and was not scanned recently; its events may be outdated.");
            if (r.IsDBNull(4)) notes.Add($"\"{label}\" has never been scanned, so none of its events are searchable yet.");
            else if (!r.IsDBNull(3) && r.GetString(3) == "unreachable") notes.Add($"\"{label}\" was unreachable at its last scan; recent events from it may be missing.");
            else if (!r.IsDBNull(3) && r.GetString(3) == "failed") notes.Add($"\"{label}\": the last scan stopped with an error; events after that point may be missing.");
            if (r.GetInt64(11) > 0)
                notes.Add($"\"{label}\" has {r.GetInt64(11)} new file(s) that are not chosen for analysis (at most 5 days of files per folder); choose them under Sources, Files if needed.");
            else if (r.GetInt64(10) > 0)
                notes.Add($"Only the chosen files of \"{label}\" are analysed (at most 5 days per folder); {r.GetInt64(10)} other file(s) in the folder are not.");
            if (r.GetInt64(7) > 0) notes.Add($"\"{label}\" has {r.GetInt64(7)} file(s) that are unreadable, missing or unrecognised.");
            if (!r.IsDBNull(9)) notes.Add($"\"{label}\" logs local times without a time zone and none is configured: its times are shown as written and may be offset from other sources.");
            if (!r.IsDBNull(5) && (request.FromMs is not null || request.ToMs is not null))
            {
                var min = r.GetInt64(5);
                var max = r.GetInt64(6);
                if ((request.ToMs is { } to && to < min) || (request.FromMs is { } from && from > max))
                    notes.Add($"\"{label}\" has no indexed events in the selected time range (it covers {Fmt(min)} to {Fmt(max)}).");
                else if ((request.FromMs is { } f2 && f2 < min) || (request.ToMs is { } t2 && t2 > max))
                    notes.Add($"\"{label}\" only partly covers the selected time range (indexed events from {Fmt(min)} to {Fmt(max)}).");
            }
        }

        if (!any) notes.Add("No sources are configured yet.");
        return notes;
    }

    private static string Fmt(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static (string Where, List<(string, object?)> Args) BuildWhere(SearchRequest q, TextQuery text)
    {
        var sb = new StringBuilder("1=1");
        var args = new List<(string, object?)>();
        if (text.Match is not null)
        {
            sb.Append(" AND events_fts MATCH $fts");
            args.Add(("$fts", text.Match));
        }

        // Terms shorter than 3 characters cannot use the trigram index: plain substring scan (case-insensitive for ASCII).
        for (var i = 0; i < text.ShortIncluded.Count; i++)
        {
            var name = "$si" + i.ToString(CultureInfo.InvariantCulture);
            sb.Append(CultureInfo.InvariantCulture, $" AND (e.message LIKE {name} ESCAPE '\\' OR ifnull(e.detail,'') LIKE {name} ESCAPE '\\' OR e.category LIKE {name} ESCAPE '\\' OR ifnull(e.component,'') LIKE {name} ESCAPE '\\')");
            args.Add((name, FtsQuery.LikePattern(text.ShortIncluded[i])));
        }

        for (var i = 0; i < text.ShortExcluded.Count; i++)
        {
            var name = "$so" + i.ToString(CultureInfo.InvariantCulture);
            sb.Append(CultureInfo.InvariantCulture, $" AND NOT (e.message LIKE {name} ESCAPE '\\' OR ifnull(e.detail,'') LIKE {name} ESCAPE '\\' OR e.category LIKE {name} ESCAPE '\\' OR ifnull(e.component,'') LIKE {name} ESCAPE '\\')");
            args.Add((name, FtsQuery.LikePattern(text.ShortExcluded[i])));
        }

        if (q.MinLevel is { } lvl)
        {
            sb.Append(" AND e.level >= $lvl");
            args.Add(("$lvl", (int)lvl));
        }

        if (q.SourceIds is { Count: > 0 } ids)
        {
            var names = new List<string>();
            for (var i = 0; i < Math.Min(ids.Count, 100); i++)
            {
                names.Add("$s" + i.ToString(CultureInfo.InvariantCulture));
                args.Add((names[^1], ids[i]));
            }

            sb.Append(" AND e.source_id IN (").Append(string.Join(',', names)).Append(')');
        }

        if (!string.IsNullOrEmpty(q.Category))
        {
            sb.Append(" AND e.category = $cat");
            args.Add(("$cat", q.Category));
        }

        if (q.FromMs is { } from)
        {
            sb.Append(" AND e.sort_ts >= $from");
            args.Add(("$from", from));
        }

        if (q.ToMs is { } to)
        {
            sb.Append(" AND e.sort_ts <= $to");
            args.Add(("$to", to));
        }

        if (q.ExceptionsOnly) sb.Append(" AND e.has_exception = 1");
        if (!q.IncludeUnparsed) sb.Append(" AND e.kind = 0");
        return (sb.ToString(), args);
    }

    /// <summary>With text, start from the full-text matches (CROSS JOIN fixes the join order); otherwise scan by time.</summary>
    private static string From(string? fts) => fts is null ? "events e" : "events_fts CROSS JOIN events e ON e.id = events_fts.rowid";

    private static SqliteCommand Command(SqliteConnection c, string sql, IEnumerable<(string Name, object? Value)> args)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    private static bool TryParseCursor(string? cursor, out long ts, out long id)
    {
        ts = 0;
        id = 0;
        if (string.IsNullOrEmpty(cursor) || cursor.Length > 64) return false;
        var parts = cursor.Split(':');
        return parts.Length == 2
               && long.TryParse(parts[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out ts)
               && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out id);
    }

    private static EventSummary ReadSummary(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetInt64(4), r.GetInt64(5),
        r.IsDBNull(6) ? null : r.GetInt64(6), r.GetInt64(7) != 0, r.IsDBNull(8) ? null : r.GetString(8),
        (EventLevel)r.GetInt32(9), r.GetString(10), (EventKind)r.GetInt32(11), r.GetString(12), r.IsDBNull(13) ? null : r.GetString(13),
        r.GetString(14), r.GetInt64(15) != 0, r.GetInt64(16) != 0);
}

/// <summary>A deferred read transaction: every statement until disposal reads the same committed state of the index.</summary>
internal sealed class ReadSnapshot : IDisposable
{
    private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;

    private ReadSnapshot(Microsoft.Data.Sqlite.SqliteConnection connection) => _connection = connection;

    public static ReadSnapshot Begin(Microsoft.Data.Sqlite.SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "BEGIN DEFERRED";
        cmd.ExecuteNonQuery();
        return new ReadSnapshot(connection);
    }

    public void Dispose()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "COMMIT";
        cmd.ExecuteNonQuery();
    }
}
