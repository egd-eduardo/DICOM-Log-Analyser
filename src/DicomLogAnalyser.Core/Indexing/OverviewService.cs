using System.Globalization;
using System.Text.RegularExpressions;
using DicomLogAnalyser.Core.Model;

namespace DicomLogAnalyser.Core.Indexing;

public sealed record SourceOverview(long Id, string Label, string Kind, bool Enabled, string? LastScanUtc, string? LastScanStatus, string? LastScanMessage,
    long Events, long Errors, long Warnings, long? FirstTs, long? LastTs, bool HasUnzonedTimes, long Files, long ProblemFiles, long UnparsedLines,
    long UnselectedFiles, long NewFiles);

public sealed record CategoryCount(string Category, long Events, long Errors, long Warnings);

public sealed record DayCount(string Day, long Errors, long Warnings);

public sealed record ErrorSignature(string Signature, string Category, long Count, long LastSortTs, long ExampleId);

public sealed record ProblemFile(long SourceId, string SourceLabel, string File, string Status, string? Message, long UnparsedLines, string? Format);

public sealed record Overview(long Events, long Errors, long Warnings, long Unparsed, long Exceptions, IReadOnlyList<SourceOverview> Sources,
    IReadOnlyList<CategoryCount> Categories, IReadOnlyList<DayCount> ErrorsPerDay, IReadOnlyList<ErrorSignature> TopErrors, IReadOnlyList<ProblemFile> ProblemFiles);

public sealed partial class OverviewService(IndexDatabase db)
{
    private readonly Lock _lock = new();
    private (long Version, Overview Value)? _cache;

    /// <summary>Returns the overview, recomputed only when the indexed data has changed.</summary>
    public Overview Build()
    {
        var version = db.DataVersion;
        lock (_lock)
        {
            if (_cache is { } cached && cached.Version == version) return cached.Value;
        }

        var value = Compute();
        lock (_lock) _cache = (version, value);
        return value;
    }

    private Overview Compute()
    {
        using var c = db.Open();
        long events = 0, errors = 0, warnings = 0, unparsed = 0, exceptions = 0;
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT count(*), ifnull(sum(level>=5),0), ifnull(sum(level=4),0), ifnull(sum(kind=1),0), ifnull(sum(has_exception),0) FROM events";
            using var r = cmd.ExecuteReader();
            if (r.Read())
            {
                events = r.GetInt64(0);
                errors = r.GetInt64(1);
                warnings = r.GetInt64(2);
                unparsed = r.GetInt64(3);
                exceptions = r.GetInt64(4);
            }
        }

        var sources = new List<SourceOverview>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = """
                SELECT s.id, s.label, s.kind, s.enabled, s.last_scan_utc, s.last_scan_status, s.last_scan_message,
                    ifnull(a.n,0), ifnull(a.err,0), ifnull(a.wrn,0), a.first_ts, a.last_ts,
                    CASE WHEN s.time_zone IS NULL AND EXISTS (SELECT 1 FROM files f WHERE f.source_id=s.id AND f.format='b3dweb-gateway') THEN 1 ELSE 0 END,
                    (SELECT count(*) FROM files f WHERE f.source_id=s.id),
                    (SELECT count(*) FROM files f WHERE f.source_id=s.id AND f.status IN ('unreadable','missing','unrecognised')),
                    (SELECT ifnull(sum(unparsed_lines),0) FROM files f WHERE f.source_id=s.id),
                    s.unselected_files, s.new_files
                FROM sources s LEFT JOIN (
                    SELECT source_id, count(*) n, sum(level>=5) err, sum(level=4) wrn, min(ts) first_ts, max(ts) last_ts, 0 unzoned
                    FROM events GROUP BY source_id) a ON a.source_id=s.id
                ORDER BY s.kind, s.label COLLATE NOCASE
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                sources.Add(new SourceOverview(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt64(3) != 0,
                    r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6),
                    r.GetInt64(7), r.GetInt64(8), r.GetInt64(9), r.IsDBNull(10) ? null : r.GetInt64(10), r.IsDBNull(11) ? null : r.GetInt64(11),
                    r.GetInt64(12) != 0, r.GetInt64(13), r.GetInt64(14), r.GetInt64(15), r.GetInt64(16), r.GetInt64(17)));
            }
        }

        var categories = new List<CategoryCount>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT category, count(*), sum(level>=5), sum(level=4) FROM events WHERE kind=0 GROUP BY category ORDER BY sum(level>=5) DESC, count(*) DESC LIMIT 30";
            using var r = cmd.ExecuteReader();
            while (r.Read()) categories.Add(new CategoryCount(r.GetString(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3)));
        }

        var days = new List<DayCount>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT ts/86400000 d, sum(level>=5), sum(level=4) FROM events WHERE level>=4 AND ts IS NOT NULL GROUP BY d ORDER BY d DESC LIMIT 60";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var day = DateTime.UnixEpoch.AddDays(r.GetInt64(0)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                days.Add(new DayCount(day, r.GetInt64(1), r.GetInt64(2)));
            }
        }

        days.Reverse();

        var signatures = new Dictionary<(string, string), (long Count, long Last, long Id)>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT substr(message,1,300), category, sort_ts, id FROM events WHERE level>=5 ORDER BY sort_ts DESC LIMIT 50000";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var key = (Signature(r.GetString(0)), r.GetString(1));
                signatures[key] = signatures.TryGetValue(key, out var v) ? (v.Count + 1, v.Last, v.Id) : (1, r.GetInt64(2), r.GetInt64(3));
            }
        }

        var top = signatures.OrderByDescending(kv => kv.Value.Count).Take(15)
            .Select(kv => new ErrorSignature(kv.Key.Item1, kv.Key.Item2, kv.Value.Count, kv.Value.Last, kv.Value.Id)).ToList();

        var problems = new List<ProblemFile>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = """
                SELECT f.source_id, s.label, f.rel_path, f.status, f.status_message, f.unparsed_lines, f.format FROM files f JOIN sources s ON s.id=f.source_id
                WHERE f.status NOT IN ('ok') OR f.unparsed_lines > 0
                ORDER BY CASE f.status WHEN 'unreadable' THEN 0 WHEN 'unrecognised' THEN 1 WHEN 'missing' THEN 3 ELSE 2 END, f.unparsed_lines DESC LIMIT 200
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                problems.Add(new ProblemFile(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.GetInt64(5),
                    r.IsDBNull(6) ? null : r.GetString(6)));
            }
        }

        return new Overview(events, errors, warnings, unparsed, exceptions, sources, categories, days, top, problems);
    }

    [GeneratedRegex(@"\b\d+(?:\.\d+){2,}\b", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex UidOrIp();

    [GeneratedRegex(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex Guid();

    [GeneratedRegex(@"\S*\^\S*", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex PersonName();

    [GeneratedRegex(@"[A-Za-z]*\d[\w\-]*", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex Numberish();

    [GeneratedRegex(@"(['""])[^'""]{0,200}\1", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex Quoted();

    /// <summary>
    /// Groups similar error messages by masking variable parts: DICOM UIDs and IP addresses, GUIDs, person names
    /// (DICOM PN with ^), quoted values and any token containing a digit (IDs, accession numbers, ports, counts).
    /// </summary>
    public static string Signature(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        try
        {
            var s = UidOrIp().Replace(message, "<uid/ip>");
            s = Guid().Replace(s, "<guid>");
            s = PersonName().Replace(s, "<name>");
            s = Quoted().Replace(s, "'…'");
            s = Numberish().Replace(s, "#");
            s = string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            return s.Length > 160 ? s[..160] + "…" : s;
        }
        catch (RegexMatchTimeoutException)
        {
            return "(message too complex to group)";
        }
    }

    public static string LevelName(EventLevel level) => level.ToString().ToLowerInvariant();
}
