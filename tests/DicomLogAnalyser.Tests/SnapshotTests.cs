using DicomLogAnalyser.Core.Indexing;

namespace DicomLogAnalyser.Tests;

/// <summary>A search reads one snapshot: its count always agrees with its events while a scan is committing.</summary>
public class SnapshotTests
{
    [Fact]
    public async Task Count_and_events_agree_while_new_lines_are_indexed()
    {
        using var f = new IndexFixture();
        var log = Path.Combine(f.LogDir, "Gateway.log");
        File.WriteAllText(log, "INF 2026.11.04 08:00:00 SPS - start\r\n");
        var source = f.AddSource("Europe/Lisbon");
        f.Ingestor.Scan(source, null, CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        var writer = Task.Run(() =>
        {
            for (var i = 0; !cts.IsCancellationRequested; i++)
            {
                File.AppendAllText(log, $"INF 2026.11.04 08:{i / 60 % 60:00}:{i % 60:00} SPS - Receiving SYNSNAP{i}\r\nINF 2026.11.04 09:00:00 SPS - tail\r\n");
                f.Ingestor.Scan(f.Sources.Get(source.Id)!, null, CancellationToken.None);
            }
        });

        var checks = 0;
        while (!writer.IsCompleted)
        {
            var r = f.Search.Search(new SearchRequest { Text = "SYNSNAP", Limit = 500 });
            Assert.Equal(Math.Min(r.MatchCount, 500), r.Items.Count);
            checks++;
        }

        await writer;
        Assert.True(checks > 20, $"only {checks} searches ran");
    }
}
