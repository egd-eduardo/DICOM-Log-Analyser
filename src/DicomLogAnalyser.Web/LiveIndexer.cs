using DicomLogAnalyser.Core.Sources;

namespace DicomLogAnalyser.Web;

/// <summary>
/// Near-real-time indexing: every few seconds, checks the size and modification time of the chosen files of enabled
/// folder sources (metadata only, no reading) and queues an incremental scan for a source whose files changed. Works on
/// local disks and network shares alike (no reliance on file-system notifications, which shares do not deliver
/// reliably). Bundles never change and are not watched.
/// </summary>
public sealed class LiveIndexer(SourceRepository sources, ScanCoordinator coordinator, AppOptions options, TimeProvider time, ILogger<LiveIndexer> logger) : BackgroundService
{
    private readonly Dictionary<long, Dictionary<string, (long Size, DateTime Modified)>> _seen = [];
    private readonly Lock _lock = new();
    private long _generation;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.LiveIntervalSeconds <= 0) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.LiveIntervalSeconds), time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false)) CheckOnce();
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Returns the number of sources queued for a live update.</summary>
    internal int CheckOnce()
    {
        var queued = 0;
        lock (_lock)
        {
            // A fresh session forgets what was seen, so the next check queues every source again.
            if (coordinator.Generation != _generation)
            {
                _seen.Clear();
                _generation = coordinator.Generation;
            }

            try
            {
                var live = sources.List().Where(s => s.Enabled && s.Kind == SourceKinds.Folder && s.SelectedFiles is not null).ToList();
                foreach (var gone in _seen.Keys.Except(live.Select(s => s.Id)).ToList()) _seen.Remove(gone);
                foreach (var source in live)
                {
                    var now = new Dictionary<string, (long, DateTime)>(StringComparer.Ordinal);
                    foreach (var rel in source.SelectedFiles!)
                    {
                        var info = new FileInfo(Path.Combine(source.Path, rel));
                        if (info.Exists) now[rel] = (info.Length, info.LastWriteTimeUtc);
                    }

                    var changed = !_seen.TryGetValue(source.Id, out var before) || before.Count != now.Count || now.Any(kv => !before.TryGetValue(kv.Key, out var b) || b != kv.Value);
                    _seen[source.Id] = now;
                    // The first check after start-up also catches up on lines written while the application was closed.
                    if (changed && coordinator.EnqueueLiveScan(source.Id)) queued++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogDebug("Live check skipped: {Type}.", Core.Diagnostics.SafeError.Describe(ex));
            }
        }

        return queued;
    }
}
