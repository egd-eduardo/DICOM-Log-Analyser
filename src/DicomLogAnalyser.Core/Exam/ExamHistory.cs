using System.Text.RegularExpressions;
using DicomLogAnalyser.Core.Indexing;
using DicomLogAnalyser.Core.Model;

namespace DicomLogAnalyser.Core.Exam;

/// <summary>What one log entry means for an exam (study).</summary>
public enum Milestone
{
    Other,
    Received,
    Queued,
    SendAttempt,
    SendFailed,
    SendSucceeded,
    Retry,
    Duplicate,
    Query,
    Retrieve,
}

public sealed record ExamEntry(EventSummary Event, Milestone Milestone, string? SeriesUid, string? Destination, bool IsUpdate);

public sealed record SeriesArrival(string SeriesUid, long FirstTs, long LastTs, int Receipts, bool IsUpdate);

public sealed record DestinationSends(string Destination, int Attempts, int Resends, int Failures, string LastResult, long? LastResultTs, long? LastResultEventId);

/// <summary>
/// Facts of an exam's history. Times are milliseconds (UTC when <see cref="UtcKnown"/>, otherwise source wall-clock
/// time). Event ids point to the evidence. The narrative is rendered by the client in the chosen language.
/// </summary>
public sealed record ExamHistory(
    string Query,
    int Entries,
    int Sources,
    bool UtcKnown,
    bool SeriesKnown,
    long? FirstReceivedTs,
    long? FirstReceivedEventId,
    string? FirstReceivedSource,
    bool FirstReceivedEstimated,
    int Updates,
    IReadOnlyList<long> UpdateTimes,
    IReadOnlyList<SeriesArrival> Series,
    IReadOnlyList<DestinationSends> Destinations,
    int TotalResends,
    int TotalFailures,
    long? FinalSeriesTs,
    long? FinalSeriesEventId,
    long? LastEntryTs,
    long? LastEntryEventId,
    long? FirstQueuedTs,
    long? PriorActivityTs,
    IReadOnlyList<string> RelatedStudyUids,
    IReadOnlyList<ExamEntry> Timeline,
    bool Truncated,
    IReadOnlyList<string> CoverageNotes);

/// <summary>
/// Builds the history of one exam from the indexed logs, using the definitions agreed with the user:
/// <list type="bullet">
/// <item><b>Entered the queue</b>: the first time the study was received.</item>
/// <item><b>Updated</b>: a new series arrived after the first receipt (in a later arrival wave).</item>
/// <item><b>Resent</b>: repeated sends of the same series (per destination).</item>
/// <item><b>Final</b>: the last series arrival; no further arrivals after it.</item>
/// </list>
/// Log entries are classified by their wording (generic English verbs used by DICOM software). The classification is
/// heuristic: every statement links to the entries it is based on.
/// </summary>
public sealed partial class ExamHistoryService(SearchService search)
{
    public const int MaxEntries = 5000;
    public const int MaxRelatedStudies = 10;

    /// <summary>Arrivals closer than this belong to the same wave (one transfer); a later wave with a new series is an update.</summary>
    public static readonly TimeSpan WaveGap = TimeSpan.FromMinutes(2);

    [GeneratedRegex(@"\b(?:fail(?:ed|ure|s)?|error|timed?\s?out|refused|rejected|unable|could not|cannot|aborted)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 250)]
    private static partial Regex FailureWords();

    [GeneratedRegex(@"\b(?:succeeded|success(?:ful(?:ly)?)?|completed?|done|delivered|accepted by)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 250)]
    private static partial Regex SuccessWords();

    [GeneratedRegex(@"\b(?:send(?:ing)?|sent|forward(?:ing|ed)?|c-store to|c-move to|upload(?:ing|ed)?|push(?:ing|ed)?|transfer(?:ring|red)? to|export(?:ing|ed)?|routing to|routed to)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 250)]
    private static partial Regex SendWords();

    [GeneratedRegex(@"\b(?:retry|retrying|retried|resend(?:ing)?|re-send|resent|re-sent|requeu(?:e|ed|ing)|re-queu(?:e|ed))\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 250)]
    private static partial Regex RetryWords();

    [GeneratedRegex(@"\b(?:receiv(?:ed|ing)|incoming|arrived|c-store (?:request )?(?:from|received)|stored\b|store request from)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 250)]
    private static partial Regex ReceiveWords();

    [GeneratedRegex(@"\b(?:queued|enqueued|added to (?:the )?queue|waiting in queue)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 250)]
    private static partial Regex QueueWords();

    [GeneratedRegex(@"\bduplicate\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 250)]
    private static partial Regex DuplicateWords();

    /// <summary>Queries (C-FIND) about the exam: not a receipt of images.</summary>
    [GeneratedRegex(@"\b(?:C-?FIND\b|Information Model\s*-\s*FIND\b|FIND\s*-\s*(?:STUDY|SERIES|PATIENT|IMAGE)\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 250)]
    private static partial Regex QueryWords();

    /// <summary>Retrieve requests (C-MOVE, C-GET) for the exam, as opposed to the C-STORE that delivers it.</summary>
    [GeneratedRegex(@"\b(?:C-?MOVE|C-?GET)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 250)]
    private static partial Regex RetrieveWords();

    /// <summary>A job placed in a send or upload queue.</summary>
    [GeneratedRegex(@"\b(?:adding\s+(?:upload|send|transfer)\s+job|(?:upload|send|transfer)\s+job\s+added)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 250)]
    private static partial Regex QueueJobWords();

    /// <summary>Uploads to the cloud, which do not name a DICOM destination.</summary>
    [GeneratedRegex(@"\b(?:beg(?:in)?\s+upload|end\s+upload|upload(?:ing|ed)?)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 250)]
    private static partial Regex UploadWords();

    [GeneratedRegex(@"\bC-?STORE(?:-RQ)?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 250)]
    private static partial Regex StoreWords();

    /// <summary>Zero counters such as "0 Fail", "0 Warn" or "Warnings: 0", which report the absence of a failure.</summary>
    [GeneratedRegex(@"\b0\s+(?:fail\w*|warn\w*|errors?)\b|\b(?:fail\w*|warnings?|errors?|retries)\s*[:=]?\s*0\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 250)]
    private static partial Regex ZeroCounters();

    [GeneratedRegex(@"\b(?:series(?:\s*instance)?(?:\s*uid)?|SeUID)\s*[-=:]?\s*(\d+(?:\.\d+){2,})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 250)]
    private static partial Regex SeriesUid();

    [GeneratedRegex(@"\b(?:study(?:\s*instance)?(?:\s*uid)?|StUID)\s*[-=:]?\s*(\d+(?:\.\d+){2,})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 250)]
    private static partial Regex StudyUid();

    [GeneratedRegex(@"\b(?:Dest(?:ination)?\s+AET|destination|to)\s+(?:destination\s+|AE\s+|AET\s+|node\s+)?([A-Za-z][A-Za-z0-9_\-]{1,63})", RegexOptions.CultureInvariant, 250)]
    private static partial Regex DestinationName();

    /// <summary>Destination shown for uploads to the cloud, which name no DICOM destination.</summary>
    public const string UploadDestination = "Cloud upload";

    private static readonly HashSet<string> NotDestinations = new(["be", "the", "a", "an", "send", "upload", "server", "queue"], StringComparer.OrdinalIgnoreCase);

    public ExamHistory Build(string query, IReadOnlyList<long>? sourceIds = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var request = new SearchRequest { Text = query, SourceIds = sourceIds, IncludeUnparsed = false };
        var byId = new Dictionary<long, EventSummary>();
        var truncated = Collect(request, byId, query.Trim());

        // Join entries that name only the study UID (for example DICOM Router lines) to the exam.
        var studies = byId.Values.SelectMany(e => StudyUid().Matches(e.Message).Select(m => m.Groups[1].Value))
            .Where(u => !query.Contains(u, StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).Take(MaxRelatedStudies).ToList();
        foreach (var uid in studies) truncated |= Collect(request with { Text = "\"" + uid + "\"" }, byId, uid);

        var events = byId.Values.OrderBy(e => e.Timestamp ?? long.MinValue).ThenBy(e => e.Id).ToList();
        var notes = search.Search(new SearchRequest { Text = query, SourceIds = sourceIds, Limit = 1 }).CoverageNotes;
        return Analyse(query, events, studies, truncated, notes);
    }

    private bool Collect(SearchRequest request, Dictionary<long, EventSummary> into, string identifier)
    {
        // An identifier must match exactly: SYNEXAM01 must not pull in SYNEXAM010, nor a UID a longer UID.
        var exact = identifier.Any(char.IsWhiteSpace)
            ? null
            : new Regex("(?<![A-Za-z0-9.])" + Regex.Escape(identifier) + "(?![A-Za-z0-9]|\\.\\d)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
        var count = 0;
        foreach (var e in search.Export(request, MaxEntries))
        {
            count++;
            if (exact is not null && !exact.IsMatch(e.Message)) continue;
            into.TryAdd(e.Id, e);
        }

        return count >= MaxEntries || into.Count >= MaxEntries;
    }

    internal static ExamHistory Analyse(string query, List<EventSummary> events, IReadOnlyList<string> studies, bool truncated, IReadOnlyList<string> notes)
    {
        var entries = events.Select(e => Classify(e)).ToList();
        var received = entries.Where(e => e.Milestone == Milestone.Received && e.Event.Timestamp is not null).ToList();
        var seriesKnown = received.Any(e => e.SeriesUid is not null);

        // Arrival waves: receipts closer than WaveGap belong together; the first wave is the initial arrival.
        var waveOf = new Dictionary<long, int>();
        var wave = 0;
        long? previous = null;
        foreach (var r in received)
        {
            var ts = r.Event.Timestamp!.Value;
            if (previous is { } p && ts - p > WaveGap.TotalMilliseconds) wave++;
            waveOf[r.Event.Id] = wave;
            previous = ts;
        }

        var seenSeries = new HashSet<string>(StringComparer.Ordinal);
        var updateTimes = new List<long>();
        var updateEvents = new HashSet<long>();
        var lastUpdateWave = 0;
        foreach (var r in received)
        {
            var w = waveOf[r.Event.Id];
            var isNewSeries = r.SeriesUid is null ? !seriesKnown : seenSeries.Add(r.SeriesUid);
            if (w > 0 && isNewSeries && w != lastUpdateWave)
            {
                updateTimes.Add(r.Event.Timestamp!.Value);
                updateEvents.Add(r.Event.Id);
                lastUpdateWave = w;
            }
        }

        var series = received.Where(r => r.SeriesUid is not null).GroupBy(r => r.SeriesUid!)
            .Select(g => new SeriesArrival(g.Key, g.Min(x => x.Event.Timestamp!.Value), g.Max(x => x.Event.Timestamp!.Value), g.Count(),
                waveOf[g.OrderBy(x => x.Event.Timestamp).First().Event.Id] > 0))
            .OrderBy(s => s.FirstTs).ToList();

        // Sends per destination; a resend is any further send result for the same series (or the study, when the
        // log does not name the series) to the same destination.
        var destinations = new List<DestinationSends>();
        foreach (var g in entries.Where(e => e.Milestone is Milestone.SendFailed or Milestone.SendSucceeded or Milestone.SendAttempt)
                     .GroupBy(e => e.Destination ?? "(unnamed destination)").OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var results = g.Where(e => e.Milestone is Milestone.SendFailed or Milestone.SendSucceeded).ToList();
            var counted = results.Count > 0 ? results : g.ToList();
            // Without a series UID, a send after an update carries the new series, so it is not a resend.
            var resends = counted.GroupBy(e => e.SeriesUid ?? "(study)#" + updateTimes.Count(u => u <= (e.Event.Timestamp ?? long.MinValue)))
                .Sum(s => Math.Max(0, s.Count() - 1));
            var last = results.LastOrDefault();
            destinations.Add(new DestinationSends(g.Key, counted.Count, resends, results.Count(e => e.Milestone == Milestone.SendFailed),
                last is null ? "unknown" : last.Milestone == Milestone.SendSucceeded ? "success" : "failure", last?.Event.Timestamp, last?.Event.Id));
        }

        var firstReceived = received.FirstOrDefault();
        var first = firstReceived?.Event ?? events.FirstOrDefault(e => e.Timestamp is not null);
        var finalArrival = received.LastOrDefault();
        var lastEntry = events.LastOrDefault(e => e.Timestamp is not null);
        var timeline = entries.Select(e => e with { IsUpdate = updateEvents.Contains(e.Event.Id) }).ToList();
        var firstQueued = entries.FirstOrDefault(e => e.Milestone == Milestone.Queued && e.Event.Timestamp is not null)?.Event.Timestamp;

        // Queries, retrievals or sends before the first receipt: the exam existed before the indexed period.
        long? prior = null;
        if (firstReceived?.Event.Timestamp is { } firstTs)
        {
            prior = entries.Where(e => e.Event.Timestamp < firstTs && e.Milestone is Milestone.Query or Milestone.Retrieve or Milestone.SendAttempt
                    or Milestone.SendFailed or Milestone.SendSucceeded)
                .Select(e => e.Event.Timestamp).Min();
        }

        return new ExamHistory(
            query,
            events.Count,
            events.Select(e => e.SourceId).Distinct().Count(),
            events.Where(e => e.Timestamp is not null).All(e => e.UtcKnown),
            seriesKnown,
            first?.Timestamp,
            first?.Id,
            first?.SourceLabel,
            firstReceived is null && first is not null,
            updateTimes.Count,
            updateTimes,
            series,
            destinations,
            destinations.Sum(d => d.Resends),
            destinations.Sum(d => d.Failures),
            finalArrival?.Event.Timestamp,
            finalArrival?.Event.Id,
            lastEntry?.Timestamp,
            lastEntry?.Id,
            firstQueued,
            prior,
            studies,
            timeline,
            truncated,
            notes);
    }

    /// <summary>Assigns a milestone from the entry's wording and level, and extracts series UID and destination.</summary>
    public static ExamEntry Classify(EventSummary e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var text = e.Message;
        Milestone milestone;
        try
        {
            // "0 Fail", "Warnings: 0" and similar counters report that nothing failed.
            var wording = ZeroCounters().Replace(text, " ");
            var store = StoreWords().IsMatch(wording);
            var send = store || SendWords().IsMatch(wording);
            var failed = e.Level >= EventLevel.Error || FailureWords().IsMatch(wording);
            if (DuplicateWords().IsMatch(wording)) milestone = Milestone.Duplicate;
            else if (RetryWords().IsMatch(wording)) milestone = Milestone.Retry;
            else if (QueryWords().IsMatch(wording)) milestone = Milestone.Query;
            else if (QueueJobWords().IsMatch(wording)) milestone = Milestone.Queued;
            else if (send && failed) milestone = Milestone.SendFailed;
            else if (send && SuccessWords().IsMatch(wording)) milestone = Milestone.SendSucceeded;
            else if (store || (send && !RetrieveWords().IsMatch(wording))) milestone = Milestone.SendAttempt;
            else if (RetrieveWords().IsMatch(wording)) milestone = Milestone.Retrieve;
            else if (ReceiveWords().IsMatch(wording) && !failed) milestone = Milestone.Received;
            else if (QueueWords().IsMatch(wording)) milestone = Milestone.Queued;
            else milestone = Milestone.Other;
        }
        catch (RegexMatchTimeoutException)
        {
            milestone = Milestone.Other;
        }

        string? seriesUid = null;
        string? destination = null;
        try
        {
            var s = SeriesUid().Match(text);
            if (s.Success) seriesUid = s.Groups[1].Value;
            if (milestone is Milestone.SendAttempt or Milestone.SendFailed or Milestone.SendSucceeded or Milestone.Retry)
            {
                if (!StoreWords().IsMatch(text) && UploadWords().IsMatch(text))
                {
                    destination = UploadDestination;
                }
                else
                {
                    foreach (Match d in DestinationName().Matches(text))
                    {
                        if (NotDestinations.Contains(d.Groups[1].Value)) continue;
                        destination = d.Groups[1].Value;
                        break;
                    }
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
        }

        return new ExamEntry(e, milestone, seriesUid, destination, false);
    }
}
