using System.Diagnostics;
using System.Text.RegularExpressions;
using DicomLogAnalyser.Core.Model;

namespace DicomLogAnalyser.Core.Indexing;

/// <summary>The kind of DICOM activity a log message describes, judged from its wording.</summary>
public enum ActivityKind
{
    None = 0,
    Association,
    Query,
    Retrieve,
    Received,
    Sent,
}

/// <summary>One time slot of the activity chart. Times are milliseconds since the Unix epoch (UTC when the source's time zone is known).</summary>
public sealed record ActivityBucket(long StartMs, long Events, long Errors, long Warnings, long Received, long Sent, long Queries, long Retrieves, long Associations);

/// <summary>A stretch of at least an hour in which no line at all was logged, inside the chart period.</summary>
public sealed record SilentPeriod(long FromMs, long ToMs);

public sealed record ActivityTotals(long Events, long Errors, long Warnings, long Received, long Sent, long Queries, long Retrieves, long Associations);

public sealed record Activity(
    long? FromMs,
    long? ToMs,
    long BucketMs,
    IReadOnlyList<ActivityBucket> Buckets,
    ActivityTotals Totals,
    IReadOnlyList<SilentPeriod> Silent,
    bool LocalTimes,
    double Seconds);

/// <summary>
/// Classifies log messages into DICOM activities by their wording (associations, queries, retrievals, studies or images
/// received and sent). Responses and acknowledgements (-RSP, -AC, -RJ) are not counted. The result counts log lines
/// about an activity, not exchanges: many services log several lines per query or transfer, so the numbers show trends
/// and comparisons over time rather than exact study counts. It never changes the stored events.
/// </summary>
public static partial class ActivityClassifier
{
    [GeneratedRegex(@"-(?:RSP|AC|RJ)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 250)]
    private static partial Regex Response();

    /// <summary>Only the acceptance is counted: the request and its PDU dump are logged for the same association too.</summary>
    [GeneratedRegex(@"\bassociation\b[^.;|]{0,40}?\b(?:accepted|established|opened)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 250)]
    private static partial Regex Association();

    /// <summary>A C-STORE being sent, also when it is a sub-operation of a C-MOVE.</summary>
    [GeneratedRegex(@"\bC-?STORE(?:-RQ)?\b[^.;|]{0,60}?\b(?:send(?:ing)?|sent|to)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 250)]
    private static partial Regex StoreSent();

    [GeneratedRegex(@"\bC-?FIND\b|Information Model\s*-\s*FIND\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 250)]
    private static partial Regex Query();

    [GeneratedRegex(@"\bC-?(?:MOVE|GET)\b|Information Model\s*-\s*(?:MOVE|GET)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 250)]
    private static partial Regex Retrieve();

    [GeneratedRegex(@"\b(?:receiv(?:ed|ing)|incoming|store request from|C-?STORE(?:-RQ)?\s+(?:request\s+)?from)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 250)]
    private static partial Regex Received();

    [GeneratedRegex(@"\b(?:sent|sending|forward(?:ing|ed)|upload(?:ing|ed)|routed to|routing to|transferr(?:ed|ing) to|C-?STORE(?:-RQ)?)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 250)]
    private static partial Regex Sent();

    public static ActivityKind Classify(string? message)
    {
        if (string.IsNullOrEmpty(message)) return ActivityKind.None;
        var m = message.Length > 400 ? message[..400] : message;
        try
        {
            if (Response().IsMatch(m)) return ActivityKind.None;
            if (Association().IsMatch(m)) return ActivityKind.Association;
            if (Query().IsMatch(m)) return ActivityKind.Query;
            if (StoreSent().IsMatch(m)) return ActivityKind.Sent;
            if (Retrieve().IsMatch(m)) return ActivityKind.Retrieve;
            if (Received().IsMatch(m)) return ActivityKind.Received;
            if (Sent().IsMatch(m)) return ActivityKind.Sent;
        }
        catch (RegexMatchTimeoutException)
        {
            // Pathological text: not counted as an activity.
        }

        return ActivityKind.None;
    }
}

/// <summary>
/// Activity over time for the Overview chart: events, errors, warnings and DICOM activities per time slot. The period
/// ends at the newest indexed event (not at "now"), so older logs chosen for analysis still show a meaningful chart.
/// </summary>
public sealed class ActivityService(IndexDatabase db)
{
    /// <summary>Slot sizes tried in order; the first giving at most <see cref="MaxBuckets"/> slots is used.</summary>
    private static readonly long[] BucketSizes =
    [
        5 * Minute, 10 * Minute, 15 * Minute, 30 * Minute, Hour, 2 * Hour, 3 * Hour, 6 * Hour, 12 * Hour, Day, 2 * Day, 7 * Day, 14 * Day, 30 * Day,
    ];

    private const long Minute = 60_000;
    private const long Hour = 60 * Minute;
    private const long Day = 24 * Hour;
    public const int MaxBuckets = 100;
    public const int MaxAets = 50;

    private static bool Matches(Regex matcher, string message)
    {
        try
        {
            return matcher.IsMatch(message);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private readonly Lock _lock = new();
    private readonly Dictionary<(int?, long?, string), (long Version, Activity Value)> _cache = [];

    /// <summary>
    /// Builds the chart. With <paramref name="aets"/>, only lines naming one of those AE titles (as a whole word) are
    /// counted, and silent periods are not reported, as a quiet modality is not a logging gap.
    /// </summary>
    public Activity Build(int? hours, long? sourceId, IReadOnlyCollection<string>? aets = null)
    {
        if (aets is { Count: > MaxAets } || aets?.Any(a => !EquipmentService.IsValidAet(a)) == true) throw new ArgumentException("Invalid AE title list.", nameof(aets));
        var aetKey = aets is { Count: > 0 } ? string.Join(',', aets.Order(StringComparer.OrdinalIgnoreCase)) : "";
        if (hours is < 1 or > 24 * 366) throw new ArgumentOutOfRangeException(nameof(hours));
        var version = db.DataVersion;
        lock (_lock)
        {
            if (_cache.TryGetValue((hours, sourceId, aetKey), out var cached) && cached.Version == version) return cached.Value;
        }

        var value = Compute(hours, sourceId, aets is { Count: > 0 } ? aets : null);
        lock (_lock)
        {
            if (_cache.Count > 32) _cache.Clear();
            _cache[(hours, sourceId, aetKey)] = (version, value);
        }

        return value;
    }

    private Activity Compute(int? hours, long? sourceId, IReadOnlyCollection<string>? aets)
    {
        var clock = Stopwatch.StartNew();
        using var c = db.Open();
        using var snapshot = ReadSnapshot.Begin(c);
        var filter = sourceId is null ? "" : " AND source_id=$s";

        long? first = null, last = null;
        var localTimes = false;
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT min(ts), max(ts), ifnull(max(utc_known=0),0) FROM events WHERE ts IS NOT NULL" + filter;
            if (sourceId is { } s) cmd.Parameters.AddWithValue("$s", s);
            using var r = cmd.ExecuteReader();
            if (r.Read() && !r.IsDBNull(0))
            {
                first = r.GetInt64(0);
                last = r.GetInt64(1);
                localTimes = r.GetInt64(2) != 0;
            }
        }

        var empty = new ActivityTotals(0, 0, 0, 0, 0, 0, 0, 0);
        if (first is null || last is null) return new Activity(null, null, Hour, [], empty, [], false, clock.Elapsed.TotalSeconds);

        var end = last.Value;
        var start = hours is { } h ? Math.Max(first.Value, end - (h * Hour) + 1) : first.Value;
        var span = hours is { } hh ? hh * Hour : Math.Max(end - start + 1, Hour);
        var size = BucketSizes.FirstOrDefault(b => (span + b - 1) / b <= MaxBuckets, BucketSizes[^1]);
        var lastSlot = Math.DivRem(end, size, out _);
        // Start at the slot of the first event when the logs cover less than the period.
        var count = (int)Math.Min(Math.Min(MaxBuckets, (span + size - 1) / size), lastSlot - (first.Value / size) + 1);
        var firstSlot = lastSlot - count + 1;
        var from = firstSlot * size;
        var to = (lastSlot + 1) * size;

        var matcher = aets is null ? null : EquipmentService.AetMatcher(aets);
        var events = new long[count];
        var errors = new long[count];
        var warnings = new long[count];
        var kinds = new long[6, count];
        using (var cmd = c.CreateCommand())
        {
            var aetFilter = "";
            if (aets is not null)
            {
                // LIKE (case-insensitive for ASCII) narrows the rows; the whole-word check below decides.
                var parts = new List<string>();
                var k = 0;
                foreach (var aet in aets)
                {
                    parts.Add($"message LIKE $a{k} ESCAPE '\\'");
                    cmd.Parameters.AddWithValue($"$a{k++}", FtsQuery.LikePattern(aet));
                }

                aetFilter = " AND (" + string.Join(" OR ", parts) + ")";
            }

            cmd.CommandText = $"SELECT ts, level, kind, substr(message,1,{(aets is null ? 400 : 2000)}) FROM events WHERE sort_ts >= $from AND sort_ts < $to AND ts IS NOT NULL" + filter + aetFilter;
            cmd.Parameters.AddWithValue("$from", from);
            cmd.Parameters.AddWithValue("$to", to);
            if (sourceId is { } s) cmd.Parameters.AddWithValue("$s", s);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var ts = r.GetInt64(0);
                if (ts < from || ts >= to) continue;
                if (matcher is not null && !Matches(matcher, r.GetString(3))) continue;
                var i = (int)((ts / size) - firstSlot);
                events[i]++;
                var level = (EventLevel)r.GetInt32(1);
                if (level >= EventLevel.Error) errors[i]++;
                else if (level == EventLevel.Warning) warnings[i]++;
                if (r.GetInt32(2) != (int)EventKind.Record) continue;
                var kind = ActivityClassifier.Classify(r.GetString(3));
                if (kind != ActivityKind.None) kinds[(int)kind, i]++;
            }
        }

        var buckets = new List<ActivityBucket>(count);
        for (var i = 0; i < count; i++)
        {
            buckets.Add(new ActivityBucket((firstSlot + i) * size, events[i], errors[i], warnings[i],
                kinds[(int)ActivityKind.Received, i], kinds[(int)ActivityKind.Sent, i], kinds[(int)ActivityKind.Query, i],
                kinds[(int)ActivityKind.Retrieve, i], kinds[(int)ActivityKind.Association, i]));
        }

        var totals = new ActivityTotals(buckets.Sum(b => b.Events), buckets.Sum(b => b.Errors), buckets.Sum(b => b.Warnings), buckets.Sum(b => b.Received),
            buckets.Sum(b => b.Sent), buckets.Sum(b => b.Queries), buckets.Sum(b => b.Retrieves), buckets.Sum(b => b.Associations));
        return new Activity(from, to, size, buckets, totals, aets is null ? SilentPeriods(buckets, size, first.Value) : [], localTimes, clock.Elapsed.TotalSeconds);
    }

    /// <summary>Runs of empty slots lasting at least an hour, after the first logged event and before the last.</summary>
    internal static List<SilentPeriod> SilentPeriods(IReadOnlyList<ActivityBucket> buckets, long size, long firstEventMs)
    {
        var result = new List<SilentPeriod>();
        var i = 0;
        while (i < buckets.Count)
        {
            if (buckets[i].Events > 0 || buckets[i].StartMs + size <= firstEventMs)
            {
                i++;
                continue;
            }

            var j = i;
            while (j < buckets.Count && buckets[j].Events == 0) j++;
            var fromMs = buckets[i].StartMs;
            var toMs = j < buckets.Count ? buckets[j].StartMs : buckets[^1].StartMs + size;
            if (j < buckets.Count && toMs - fromMs >= Hour) result.Add(new SilentPeriod(fromMs, toMs));
            i = j;
        }

        return result;
    }
}
