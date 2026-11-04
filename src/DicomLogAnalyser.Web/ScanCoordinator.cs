using System.Globalization;
using System.Threading.Channels;
using DicomLogAnalyser.Core.Bundles;
using DicomLogAnalyser.Core.Diagnostics;
using DicomLogAnalyser.Core.Indexing;
using DicomLogAnalyser.Core.Sources;

namespace DicomLogAnalyser.Web;

public sealed record ScanStatus(string State, string? Operation, string? SourceLabel, int FilesDone, int FilesTotal, long BytesRead, long EventsAdded,
    string? StartedUtc, string? FinishedUtc, string? LastOutcome, int Queued);

/// <summary>
/// Runs index-changing work one item at a time (scans, source deletion, purge) so there is a single writer.
/// Optional periodic scans are configured with DLA_SCAN_INTERVAL_MINUTES.
/// </summary>
public sealed class ScanCoordinator(
    Ingestor ingestor, SourceRepository sources, IndexDatabase db, BundleImporter bundles, AuditLog audit, AppOptions options, TimeProvider time,
    ILogger<ScanCoordinator> logger) : BackgroundService
{
    private readonly Channel<WorkItem> _queue = Channel.CreateBounded<WorkItem>(new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly Lock _lock = new();
    private ScanStatus _status = new("idle", null, null, 0, 0, 0, 0, null, null, null, 0);
    private CancellationTokenSource? _current;
    private int _queued;

    private abstract record WorkItem(string Actor);

    private sealed record ScanWork(string Actor, IReadOnlyList<long>? SourceIds, bool Live = false) : WorkItem(Actor);

    public const string LiveOperation = "Live update";
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, byte> _pendingLive = new();

    private sealed record DeleteWork(string Actor, long SourceId) : WorkItem(Actor);

    private sealed record PurgeWork(string Actor) : WorkItem(Actor);

    private sealed record ReindexWork(string Actor, long SourceId) : WorkItem(Actor);

    /// <summary>Test seam: replaces the ingestor call for one source.</summary>
    internal Func<SourceDefinition, IProgress<ScanProgress>, CancellationToken, ScanSummary>? ScanOverride { get; set; }

    public ScanStatus Status
    {
        get
        {
            lock (_lock) return _status with { Queued = Volatile.Read(ref _queued) };
        }
    }

    public bool EnqueueScan(string actor, IReadOnlyList<long>? sourceIds) => Enqueue(new ScanWork(actor, sourceIds));

    /// <summary>Queues a quiet incremental scan for new lines, unless one for this source is already waiting.</summary>
    public bool EnqueueLiveScan(long sourceId)
    {
        if (!_pendingLive.TryAdd(sourceId, 0)) return false;
        if (Enqueue(new ScanWork("live", [sourceId], Live: true))) return true;
        _pendingLive.TryRemove(sourceId, out _);
        return false;
    }

    public bool EnqueueDelete(string actor, long sourceId) => Enqueue(new DeleteWork(actor, sourceId));

    public bool EnqueuePurge(string actor) => Enqueue(new PurgeWork(actor));

    /// <summary>Clears a source's index and rescans it, after any running work (never concurrently with a scan).</summary>
    public bool EnqueueReindex(string actor, long sourceId) => Enqueue(new ReindexWork(actor, sourceId));

    public void CancelCurrent()
    {
        lock (_lock) _current?.Cancel();
    }

    private bool Enqueue(WorkItem item)
    {
        if (!_queue.Writer.TryWrite(item)) return false;
        Interlocked.Increment(ref _queued);
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = options.ScanIntervalMinutes > 0 ? new PeriodicTimer(TimeSpan.FromMinutes(options.ScanIntervalMinutes), time) : null;
        var periodic = timer is null ? Task.CompletedTask : SchedulePeriodic(timer, stoppingToken);
        await foreach (var item in _queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            Interlocked.Decrement(ref _queued);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            lock (_lock) _current = cts;
            try
            {
                await Task.Run(() => Execute(item, cts.Token), cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                Finish("Cancelled. Files already processed keep their indexed events; the next scan continues where this one stopped.");
                audit.Write(item.Actor, "scan_cancelled");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var reference = SafeError.NewReference();
                logger.LogError("Background operation failed: {Type} (ref {Reference}).", SafeError.Describe(ex), reference);
                Finish("Failed with an unexpected error (reference " + reference + ").");
            }
            finally
            {
                lock (_lock) _current = null;
            }
        }

        await periodic.ConfigureAwait(false);
    }

    private async Task SchedulePeriodic(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false)) EnqueueScan("scheduler", null);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Execute(WorkItem item, CancellationToken ct)
    {
        switch (item)
        {
            case ScanWork scan:
                RunScan(scan, ct);
                break;
            case DeleteWork delete:
                Begin("Deleting source");
                var source = sources.Get(delete.SourceId);
                if (source is null)
                {
                    Finish("The source no longer exists.");
                    return;
                }

                sources.Delete(source.Id);
                if (source.Kind == SourceKinds.Bundle) bundles.Delete(source.Path);
                using (var c = db.Open()) IndexDatabase.Compact(c);
                audit.Write(delete.Actor, source.Kind == SourceKinds.Bundle ? "bundle_deleted" : "source_deleted", new Dictionary<string, object?> { ["sourceId"] = source.Id });
                Finish("Source and its indexed events deleted" + (source.Kind == SourceKinds.Bundle ? ", including the imported files." : "."));
                break;
            case ReindexWork reindex:
                Begin("Re-indexing source");
                if (sources.Get(reindex.SourceId) is not { } target)
                {
                    Finish("The source no longer exists.");
                    return;
                }

                sources.ResetIndex(target.Id);
                if (target.Enabled) RunScan(new ScanWork(reindex.Actor, [target.Id]), ct);
                else Finish("The source's index was cleared; enable it and scan to rebuild it.");
                break;
            case PurgeWork purge:
                Begin("Purging index");
                db.PurgeEvents();
                audit.Write(purge.Actor, "index_purged");
                Finish("All indexed events were deleted. Source settings were kept; scan again to rebuild the index.");
                break;
        }
    }

    private void RunScan(ScanWork work, CancellationToken ct)
    {
        if (work.Live)
        {
            RunLiveScan(work, ct);
            return;
        }

        Begin("Scanning");
        var list = sources.List().Where(s => s.Enabled && (work.SourceIds is null || work.SourceIds.Contains(s.Id))).ToList();
        var progress = new Progress(this);
        var outcomes = new List<string>();
        long totalEvents = 0;
        foreach (var source in list)
        {
            ct.ThrowIfCancellationRequested();
            lock (_lock) _status = _status with { SourceLabel = source.Label, FilesDone = 0, FilesTotal = 0 };
            var summary = ScanOne(source, progress, ct);
            totalEvents += summary.EventsAdded;
            outcomes.Add($"{source.Label}: {summary.Status} ({summary.EventsAdded.ToString("N0", CultureInfo.InvariantCulture)} events, {summary.FilesChanged} changed file(s))");
            audit.Write(work.Actor, "scan", new Dictionary<string, object?>
            {
                ["sourceId"] = source.Id,
                ["status"] = summary.Status,
                ["files"] = summary.FilesSeen,
                ["changed"] = summary.FilesChanged,
                ["events"] = summary.EventsAdded,
                ["unreadable"] = summary.FilesUnreadable,
                ["unrecognised"] = summary.FilesUnrecognised,
                ["missing"] = summary.FilesMissing,
            });
            logger.LogInformation("Scan of source {SourceId} finished: {Status}, {Files} files, {Events} events.", source.Id, summary.Status, summary.FilesSeen, summary.EventsAdded);
        }

        Finish(list.Count == 0 ? "No enabled sources to scan." : string.Join(" · ", outcomes));
    }

    /// <summary>Live update: no audit entry, no change to the last manual outcome, diagnostics only when lines were added.</summary>
    private void RunLiveScan(ScanWork work, CancellationToken ct)
    {
        var id = work.SourceIds![0];
        _pendingLive.TryRemove(id, out _);
        if (sources.Get(id) is not { Enabled: true } source) return;
        var previous = Status.LastOutcome;
        Begin(LiveOperation);
        var summary = ScanOne(source, new Progress(this), ct);
        if (summary.EventsAdded > 0) logger.LogDebug("Live update of source {SourceId}: {Events} events.", id, summary.EventsAdded);
        Finish(previous ?? "");
    }

    /// <summary>
    /// Scans one source. A transient lock is retried once; any other error is recorded on that source (with a
    /// reference and, for storage errors, the numeric SQLite code) and the remaining sources are still scanned.
    /// </summary>
    private ScanSummary ScanOne(SourceDefinition source, IProgress<ScanProgress> progress, CancellationToken ct)
    {
        ScanSummary Run() => ScanOverride is { } f ? f(source, progress, ct) : ingestor.Scan(source, progress, ct);
        try
        {
            try
            {
                return Run();
            }
            catch (Exception ex) when (SafeError.IsTransientStorageError(ex))
            {
                // Committed work is kept; the retry continues from the last committed offset of each file.
                logger.LogWarning("Scan of source {SourceId} hit a transient storage lock ({Type}); retrying once.", source.Id, SafeError.Describe(ex));
                ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(2));
                ct.ThrowIfCancellationRequested();
                return Run();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var reference = SafeError.NewReference();
            logger.LogError("Scan of source {SourceId} failed: {Type} (ref {Reference}).", source.Id, SafeError.Describe(ex), reference);
            var message = "The scan stopped with an unexpected error (reference " + reference + "). Events indexed before the error are kept; scan again to continue.";
            try
            {
                sources.RecordScan(source.Id, "failed", message);
            }
            catch (Exception recordEx) when (recordEx is not OperationCanceledException)
            {
                logger.LogError("Could not record the failed scan of source {SourceId}: {Type}.", source.Id, SafeError.Describe(recordEx));
            }

            return new ScanSummary { Status = "failed", Message = message };
        }
    }

    private void Begin(string operation)
    {
        lock (_lock) _status = new ScanStatus("running", operation, null, 0, 0, 0, 0, Now(), null, _status.LastOutcome, 0);
    }

    private void Finish(string outcome)
    {
        lock (_lock) _status = _status with { State = "idle", FinishedUtc = Now(), LastOutcome = outcome };
    }

    private string Now() => time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);

    private sealed class Progress(ScanCoordinator owner) : IProgress<ScanProgress>
    {
        public void Report(ScanProgress value)
        {
            lock (owner._lock)
            {
                owner._status = owner._status with
                {
                    SourceLabel = value.SourceLabel,
                    FilesDone = value.FilesDone,
                    FilesTotal = value.FilesTotal,
                    BytesRead = value.BytesRead,
                    EventsAdded = value.EventsAdded,
                };
            }
        }
    }
}
