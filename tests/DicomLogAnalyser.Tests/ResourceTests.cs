using System.Diagnostics;
using DicomLogAnalyser.Core.Indexing;
using DicomLogAnalyser.Core.IO;
using DicomLogAnalyser.Core.Sources;
using DicomLogAnalyser.Web;

namespace DicomLogAnalyser.Tests;

/// <summary>Sharing the computer: paced reading from network shares and background priority for scans.</summary>
public class ResourceTests
{
    [Fact]
    public void Pacer_waits_when_reading_ahead_of_the_limit_and_honours_cancellation()
    {
        var off = new ReadPacer(0);
        off.Pace(long.MaxValue / 2, CancellationToken.None);
        Assert.False(off.Active);

        var pacer = new ReadPacer(1024 * 1024);
        var sw = Stopwatch.StartNew();
        pacer.Pace(400 * 1024, CancellationToken.None);
        Assert.InRange(sw.Elapsed.TotalSeconds, 0.3, 3);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        Assert.ThrowsAny<OperationCanceledException>(() => new ReadPacer(1024).Pace(1024 * 1024, cts.Token));
    }

    [Fact]
    public void Network_share_is_read_at_the_limit_and_local_folders_are_not_paced()
    {
        static (ScanSummary Summary, double Seconds) Scan(bool network)
        {
            using var f = new IndexFixture();
            var line = "INF 2026.11.04 10:00:00 SPS - Receiving (1) ... PID SYNPID1, ANR SYNA1REP, padding padding padding padding padding\r\n";
            File.WriteAllText(Path.Combine(f.LogDir, "Gateway.log"), string.Concat(Enumerable.Repeat(line, 20_000)));
            var ingestor = new Ingestor(f.Db, f.Sources, TimeProvider.System) { NetworkReadBytesPerSecond = 1024 * 1024, IsNetworkPath = _ => network };
            var sw = Stopwatch.StartNew();
            var summary = ingestor.Scan(f.AddSource("Europe/Lisbon"), null, CancellationToken.None);
            return (summary, sw.Elapsed.TotalSeconds);
        }

        var size = 20_000 * 120 / (1024.0 * 1024); // about 2.3 MB
        var share = Scan(network: true);
        Assert.True(share.Summary.ReadLimited);
        Assert.Equal(20_000, share.Summary.EventsAdded);
        Assert.True(share.Seconds >= size * 0.8, $"{share.Seconds:F2} s for {size:F1} MB at 1 MB/s");

        var local = Scan(network: false);
        Assert.False(local.Summary.ReadLimited);
        Assert.True(local.Seconds < share.Seconds);
    }

    [Fact]
    public void Local_temporary_folder_is_not_a_network_share()
    {
        using var t = new TempDir();
        Assert.False(NetworkVolumes.IsNetworkPath(t.Path));
        Assert.False(NetworkVolumes.IsNetworkPath(""));
        if (OperatingSystem.IsWindows()) Assert.True(NetworkVolumes.IsNetworkPath(@"\\server.example.org\logs"));
    }

    [Theory]
    [InlineData(null, 2)]
    [InlineData("0", 0)]
    [InlineData("8", 8)]
    public void Network_read_limit_option(string? value, int expected)
    {
        var args = value is null ? Array.Empty<string>() : ["--network-read-limit", value];
        Assert.Equal(expected, AppOptions.Parse(args, _ => null).NetworkReadLimitMb);
    }
}
