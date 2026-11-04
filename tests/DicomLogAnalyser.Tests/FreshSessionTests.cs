using System.Text;
using DicomLogAnalyser.Core.Diagnostics;
using DicomLogAnalyser.Core.Indexing;
using DicomLogAnalyser.Core.Sources;
using DicomLogAnalyser.Core.Storage;
using DicomLogAnalyser.Web;

namespace DicomLogAnalyser.Tests;

/// <summary>Fresh session on demand: clear index, bundles, temporary files and memory, then analyse the newest files again.</summary>
public sealed class FreshSessionTests : IAsyncLifetime
{
    private readonly TempDir _temp = new();
    private WebApplication _app = null!;

    private ScanCoordinator Coordinator => _app.Services.GetRequiredService<ScanCoordinator>();
    private SourceRepository Sources => _app.Services.GetRequiredService<SourceRepository>();
    private AppPaths Paths => _app.Services.GetRequiredService<AppPaths>();

    public async Task InitializeAsync()
    {
        _app = AppHost.Build(new AppOptions { Mode = AppMode.Local, Port = 0, DataDirectory = _temp.Sub("data"), OpenBrowser = false, LiveIntervalSeconds = 0 },
            logging: l => l.ClearProviders());
        await AppHost.StartAsync(_app);
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _temp.Dispose();
    }

    private long FolderWithDays(int days)
    {
        var dir = _temp.Sub("folder");
        for (var d = 1; d <= days; d++)
        {
            var p = Path.Combine(dir, $"g{d:00}.log");
            File.WriteAllText(p, $"INF 2026.11.{d:00} 10:00:00 SPS - day {d}\n");
            File.SetLastWriteTimeUtc(p, new DateTime(2026, 11, d, 12, 0, 0, DateTimeKind.Utc));
        }

        return Sources.Add(new SourceDefinition { Label = "Gateway", Kind = SourceKinds.Folder, Path = dir, TimeZone = "Europe/Lisbon" });
    }

    private async Task<string> BundleWith(string token)
    {
        var importer = _app.Services.GetRequiredService<Core.Bundles.BundleImporter>();
        using var upload = new MemoryStream(Encoding.UTF8.GetBytes($"INF 2026.11.03 10:00:00 SPS - Study received Accession={token}\n"));
        var result = await importer.ImportAsync(upload, "remote.log", CancellationToken.None);
        Sources.Add(new SourceDefinition { Label = "Remote", Kind = SourceKinds.Bundle, Path = result.FilesDirectory });
        return result.FilesDirectory;
    }

    private async Task Run(Action enqueue)
    {
        enqueue();
        for (var i = 0; i < 400; i++)
        {
            await Task.Delay(25);
            var s = Coordinator.Status;
            if (s.State == "idle" && s.Queued == 0 && s.FinishedUtc is not null) return;
        }

        Assert.Fail("The queue did not finish.");
    }

    private long Events()
    {
        using var c = _app.Services.GetRequiredService<IndexDatabase>().Open();
        return IndexDatabase.Scalar<long>(c, "SELECT count(*) FROM events");
    }

    [Fact]
    public async Task Default_fresh_session_keeps_folders_resets_the_choice_and_analyses_again()
    {
        var folder = FolderWithDays(7);
        var bundleDir = await BundleWith("SYNFRESH0001");
        await Run(() => Coordinator.EnqueueScan("test", null));
        Sources.SetSelection(folder, ["g03.log"]);
        await Run(() => Coordinator.EnqueueScan("test", [folder]));
        Assert.Equal(2, Events());
        File.WriteAllText(Path.Combine(Paths.TempDirectory, "leftover.part"), "x");

        await Run(() => Coordinator.EnqueueFreshSession("test", new FreshSessionOptions()));

        var sources = Sources.List();
        var kept = Assert.Single(sources);
        Assert.Equal("Europe/Lisbon", kept.TimeZone);
        Assert.Equal(["g03.log", "g04.log", "g05.log", "g06.log", "g07.log"], kept.SelectedFiles!.ToArray());
        Assert.Equal(5, Events());
        Assert.False(Directory.Exists(Path.GetDirectoryName(bundleDir)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Paths.TempDirectory));
        var audit = _app.Services.GetRequiredService<AuditLog>().Recent(100);
        Assert.Contains(audit, a => a.Action == "fresh_session");
        Assert.Contains(audit, a => a.Action == "scan" && a.Actor == "test");
    }

    [Fact]
    public async Task Removed_content_is_gone_from_the_database_files()
    {
        await BundleWith("SYNFRESHSECRET42");
        await Run(() => Coordinator.EnqueueScan("test", null));
        Assert.Equal(1, Events());
        Assert.True(FilesContain("SYNFRESHSECRET42"));

        await Run(() => Coordinator.EnqueueFreshSession("test", new FreshSessionOptions(ScanAgain: false)));
        Assert.Equal(0, Events());
        Assert.Empty(Sources.List());
        Assert.False(FilesContain("SYNFRESHSECRET42"));
    }

    [Fact]
    public async Task Options_remove_folders_and_the_activity_record_and_skip_the_scan()
    {
        FolderWithDays(3);
        await Run(() => Coordinator.EnqueueScan("test", null));
        await Run(() => Coordinator.EnqueueFreshSession("test", new FreshSessionOptions(KeepFolderSources: false, ClearActivityRecord: true, ScanAgain: false)));
        Assert.Empty(Sources.List());
        Assert.Equal(0, Events());
        var audit = _app.Services.GetRequiredService<AuditLog>().Recent(100);
        Assert.Equal("fresh_session", Assert.Single(audit).Action);
    }

    /// <summary>Searches the raw bytes of the index database and its write-ahead log.</summary>
    private bool FilesContain(string token)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var needle = Encoding.UTF8.GetBytes(token);
        foreach (var f in new[] { Paths.DatabaseFile, Paths.DatabaseFile + "-wal" })
        {
            if (!File.Exists(f)) continue;
            using var s = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bytes = new byte[s.Length];
            s.ReadExactly(bytes);
            if (bytes.AsSpan().IndexOf(needle) >= 0) return true;
        }

        return false;
    }
}
