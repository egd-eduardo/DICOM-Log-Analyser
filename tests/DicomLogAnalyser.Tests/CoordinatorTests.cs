using DicomLogAnalyser.Core.Indexing;
using DicomLogAnalyser.Core.Sources;
using DicomLogAnalyser.Web;
using Microsoft.Data.Sqlite;

namespace DicomLogAnalyser.Tests;

/// <summary>Background queue behaviour: failures stay isolated to one source and are visible, never silent.</summary>
public sealed class CoordinatorTests : IAsyncLifetime
{
    private readonly TempDir _temp = new();
    private WebApplication _app = null!;

    private ScanCoordinator Coordinator => _app.Services.GetRequiredService<ScanCoordinator>();
    private SourceRepository Sources => _app.Services.GetRequiredService<SourceRepository>();
    private Ingestor Ingestor => _app.Services.GetRequiredService<Ingestor>();

    public async Task InitializeAsync()
    {
        _app = AppHost.Build(new AppOptions { Mode = AppMode.Local, Port = 0, DataDirectory = _temp.Sub("data"), OpenBrowser = false }, logging: l => l.ClearProviders());
        await AppHost.StartAsync(_app);
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        SqliteConnection.ClearAllPools();
        _temp.Dispose();
    }

    private long AddSource(string name, string fixture, string? tz = null)
    {
        var dir = _temp.Sub(name);
        File.Copy(IndexFixture.Fixture(fixture), Path.Combine(dir, Path.GetFileName(fixture)));
        return Sources.Add(new SourceDefinition { Label = name, Kind = SourceKinds.Folder, Path = dir, TimeZone = tz });
    }

    private async Task WaitIdle()
    {
        for (var i = 0; i < 200; i++)
        {
            var s = Coordinator.Status;
            if (s.State == "idle" && s.Queued == 0 && s.FinishedUtc is not null) return;
            await Task.Delay(25);
        }

        Assert.Fail("The background queue did not finish.");
    }

    [Fact]
    public async Task A_failing_source_is_recorded_as_failed_and_other_sources_are_still_scanned()
    {
        var bad = AddSource("bad", "b3dweb-gateway/Gateway.log");
        var good = AddSource("good", "dicom-router/DicomRouter20261027.log");
        Coordinator.ScanOverride = (s, p, ct) => s.Id == bad ? throw new SqliteException("constraint failed", 19) : Ingestor.Scan(s, p, ct);
        Assert.True(Coordinator.EnqueueScan("test", null));
        await WaitIdle();

        var b = Sources.Get(bad)!;
        Assert.Equal("failed", b.LastScanStatus);
        Assert.Contains("reference", b.LastScanMessage, StringComparison.Ordinal);
        Assert.NotNull(b.LastScanUtc);
        Assert.Equal("ok", Sources.Get(good)!.LastScanStatus);
        Assert.Contains("failed", Coordinator.Status.LastOutcome, StringComparison.Ordinal);

        var notes = _app.Services.GetRequiredService<SearchService>().Search(new SearchRequest { Text = "anything" }).CoverageNotes;
        Assert.Contains(notes, n => n.Contains("stopped with an error", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_transient_lock_is_retried_once()
    {
        var id = AddSource("busy", "dicom-router/DicomRouter20261027.log");
        var calls = 0;
        Coordinator.ScanOverride = (s, p, ct) => ++calls == 1 ? throw new SqliteException("database is locked", 5) : Ingestor.Scan(s, p, ct);
        Coordinator.EnqueueScan("test", [id]);
        await WaitIdle();
        Assert.Equal(2, calls);
        Assert.Equal("ok", Sources.Get(id)!.LastScanStatus);
    }

    [Fact]
    public async Task Time_zone_change_is_reindexed_by_the_queue_after_a_running_scan()
    {
        var id = AddSource("gw", "b3dweb-gateway/Gateway.log");
        var gate = new ManualResetEventSlim();
        Coordinator.ScanOverride = (s, p, ct) =>
        {
            gate.Wait(TimeSpan.FromSeconds(10), ct);
            return Ingestor.Scan(Sources.Get(s.Id)!, p, ct);
        };
        Coordinator.EnqueueScan("test", [id]);
        await Task.Delay(100);
        Assert.Equal("running", Coordinator.Status.State);

        // Saved while the first scan is still running: the reset must wait for it, not race it.
        Sources.Update(Sources.Get(id)! with { TimeZone = "America/New_York" });
        Assert.True(Coordinator.EnqueueReindex("test", id));
        gate.Set();
        await WaitIdle();

        using var c = _app.Services.GetRequiredService<IndexDatabase>().Open();
        Assert.Equal(10, IndexDatabase.Scalar<long>(c, "SELECT count(*) FROM events WHERE utc_known=1"));
        Assert.Equal(0, IndexDatabase.Scalar<long>(c, "SELECT count(*) FROM events WHERE utc_known=0"));
        // 09:40:29 New York (EST, UTC-5 on 3 November) is 14:40:29 UTC.
        Assert.Equal(1, IndexDatabase.Scalar<long>(c, "SELECT count(*) FROM events WHERE ts_text LIKE '2026-11-03 09:40:29%' AND ts = $t", null,
            ("$t", new DateTimeOffset(2026, 11, 3, 14, 40, 29, TimeSpan.Zero).ToUnixTimeMilliseconds())));
    }
}
