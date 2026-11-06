using System.Diagnostics;

namespace DicomLogAnalyser.Core.IO;

/// <summary>
/// Keeps the average read rate of a scan at or below a limit by pausing between batches of lines. Used for network
/// shares, so that reading logs over Wi-Fi or a VPN leaves bandwidth for video calls and other work. A limit of 0
/// means no pacing.
/// </summary>
public sealed class ReadPacer(long bytesPerSecond)
{
    private const double MaxPauseSeconds = 0.5;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public long BytesPerSecond { get; } = Math.Max(0, bytesPerSecond);

    public bool Active => BytesPerSecond > 0;

    /// <summary>Total time spent waiting, for diagnostics and tests.</summary>
    public TimeSpan Waited { get; private set; }

    /// <summary>Pauses while <paramref name="bytesRead"/> (since this pacer started) is ahead of the allowed rate.</summary>
    public void Pace(long bytesRead, CancellationToken ct)
    {
        if (!Active) return;
        while (true)
        {
            var ahead = ((double)bytesRead / BytesPerSecond) - _clock.Elapsed.TotalSeconds;
            if (ahead <= 0.02) return;
            var pause = TimeSpan.FromSeconds(Math.Min(ahead, MaxPauseSeconds));
            Waited += pause;
            if (ct.WaitHandle.WaitOne(pause)) ct.ThrowIfCancellationRequested();
        }
    }
}
