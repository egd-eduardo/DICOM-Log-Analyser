using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DicomLogAnalyser.Core.Indexing;
using DicomLogAnalyser.Core.IO;
using DicomLogAnalyser.Core.Model;
using DicomLogAnalyser.Core.Sources;
using DicomLogAnalyser.Web;
using DicomLogAnalyser.Web.Security;

namespace DicomLogAnalyser.Tests;

public class TailReaderTests
{
    [Fact]
    public void Shows_the_last_lines_and_holds_back_a_partial_line()
    {
        using var t = new TempDir();
        var path = Path.Combine(t.Path, "live.log");
        File.WriteAllText(path, string.Concat(Enumerable.Range(1, 10).Select(i => $"INF 2026.11.03 10:00:{i:00} SPS - line {i}\r\n")) + "INF 2026.11.03 10:00:11 SPS - half");
        var first = TailReader.ReadLast(path, 3);
        Assert.Equal(["line 8", "line 9", "line 10"], first.Lines.Select(l => l.Text[^7..].Trim()).Select(s => s.Replace("- ", "", StringComparison.Ordinal)).ToArray());
        Assert.True(first.HeadTruncated);
        Assert.All(first.Lines, l => Assert.Equal(EventLevel.Info, l.Level));

        // The half-written line is delivered once its line break arrives; nothing is repeated.
        Assert.Empty(TailReader.ReadFrom(path, first.Offset, 3).Lines);
        File.AppendAllText(path, " done\nERROR 2026.11.03 10:00:12 SCU - failed\n   at X.Y()\n");
        var next = TailReader.ReadFrom(path, first.Offset, 3);
        Assert.Equal(3, next.Lines.Count);
        Assert.EndsWith("half done", next.Lines[0].Text, StringComparison.Ordinal);
        Assert.Equal(EventLevel.Error, next.Lines[1].Level);
        Assert.Null(next.Lines[2].Level);
        Assert.Equal(new FileInfo(path).Length, next.Offset);
    }

    [Fact]
    public void Truncation_or_replacement_restarts_from_the_end()
    {
        using var t = new TempDir();
        var path = Path.Combine(t.Path, "live.log");
        File.WriteAllText(path, "INF 2026.11.03 10:00:00 SPS - old line that is long enough\n");
        var a = TailReader.ReadLast(path, 10);
        File.WriteAllText(path, "INF 2026.11.03 11:00:00 SPS - new\n");
        var b = TailReader.ReadFrom(path, a.Offset, 10);
        Assert.True(b.Reset);
        Assert.Single(b.Lines);
    }

    [Fact]
    public void Decodes_utf8_by_default_with_latin1_fallback_bom_and_crlf()
    {
        using var t = new TempDir();
        var path = Path.Combine(t.Path, "enc.log");
        var utf8 = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("INF 2026.11.03 10:00:00 SPS - Ação São João\r\n")).ToArray();
        var latin1 = Encoding.Latin1.GetBytes("INF 2026.11.03 10:00:01 SPS - Café\r\n");
        File.WriteAllBytes(path, [.. utf8, .. latin1]);
        var lines = TailReader.ReadLast(path, 10).Lines.Select(l => l.Text).ToArray();
        Assert.Equal("INF 2026.11.03 10:00:00 SPS - Ação São João", lines[0]);
        Assert.Equal("INF 2026.11.03 10:00:01 SPS - Café", lines[1]);
    }
}

/// <summary>Live view API and near-real-time indexing on the running host.</summary>
public sealed class LiveHostTests : IAsyncLifetime
{
    private readonly TempDir _temp = new();
    private WebApplication _app = null!;
    private int _port;
    private readonly HttpClient _http = new(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false });
    private string _cookie = "";

    public async Task InitializeAsync()
    {
        _app = AppHost.Build(new AppOptions { Mode = AppMode.Local, Port = 0, DataDirectory = _temp.Sub("data"), OpenBrowser = false, LiveIntervalSeconds = 0 },
            logging: l => l.ClearProviders());
        _port = await AppHost.StartAsync(_app);
        var token = _app.Services.GetRequiredService<LaunchTokenService>().Issue();
        using var res = await _http.GetAsync(new Uri($"http://127.0.0.1:{_port}/launch?t={token}"));
        _cookie = res.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _temp.Dispose();
    }

    private async Task<HttpResponseMessage> Get(string path)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{_port}{path}");
        req.Headers.Add("Cookie", _cookie);
        return await _http.SendAsync(req);
    }

    private (long Id, string Dir) AddFolder(params string[] files)
    {
        var dir = _temp.Sub("logs-" + Guid.NewGuid().ToString("N")[..6]);
        foreach (var f in files) File.WriteAllText(Path.Combine(dir, f), "INF 2026.11.03 10:00:00 SPS - start\n");
        var repo = _app.Services.GetRequiredService<SourceRepository>();
        var id = repo.Add(new SourceDefinition { Label = "Live", Kind = SourceKinds.Folder, Path = dir, TimeZone = "Europe/Lisbon" });
        return (id, dir);
    }

    [Fact]
    public async Task Only_chosen_files_can_be_viewed_live()
    {
        var (id, dir) = AddFolder("a.log", "b.log");
        _app.Services.GetRequiredService<SourceRepository>().SetSelection(id, ["a.log"], ["a.log", "b.log"]);
        File.WriteAllText(Path.Combine(_temp.Path, "secret.log"), "x\n");

        using (var ok = await Get($"/api/tail?source={id}&file=a.log&lines=10"))
        {
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            var body = await ok.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("info", body.GetProperty("lines")[0].GetProperty("l").GetString());
        }

        foreach (var file in new[] { "b.log", "../secret.log", "..%2Fsecret.log", "/etc/passwd" })
        {
            using var res = await Get($"/api/tail?source={id}&file={file}");
            Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        }

        using var list = await Get($"/api/tail/files?source={id}");
        Assert.Equal(1, (await list.Content.ReadFromJsonAsync<JsonElement>()).GetArrayLength());
    }

    [Fact]
    public async Task Find_in_files_reads_only_chosen_files_without_the_index()
    {
        var (id, dir) = AddFolder("a.log", "b.log");
        _app.Services.GetRequiredService<SourceRepository>().SetSelection(id, ["a.log"], ["a.log", "b.log"]);
        File.AppendAllText(Path.Combine(dir, "a.log"), "INF 2026.11.03 10:00:01 SCP - accessionNumber=ACC000777 Associação\n");
        File.AppendAllText(Path.Combine(dir, "b.log"), "INF 2026.11.03 10:00:01 SCP - accessionNumber=ACC000777 unchosen\n");

        using (var res = await Get($"/api/find?q=acc000777+associacao&sources={id}"))
        {
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            var body = await res.Content.ReadFromJsonAsync<JsonElement>();
            var hit = Assert.Single(body.GetProperty("hits").EnumerateArray());
            Assert.Equal("a.log", hit.GetProperty("file").GetString());
            Assert.Equal(2, hit.GetProperty("line").GetInt64());
            Assert.Single(hit.GetProperty("before").EnumerateArray());
            Assert.Equal(1, body.GetProperty("filesSearched").GetInt32());
        }

        using (var none = await Get("/api/find?q=ACC000777+unchosen"))
            Assert.Empty((await none.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("hits").EnumerateArray());

        foreach (var bad in new[] { "/api/find?q=", "/api/find?q=%20%20", "/api/find?q=x&sources=abc", "/api/find?q=" + new string('x', 501) })
        {
            using var res = await Get(bad);
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        }
    }

    [Fact]
    public async Task Activity_endpoint_validates_its_parameters()
    {
        AddFolder("a.log");
        using (var ok = await Get("/api/activity?hours=168"))
        {
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            var body = await ok.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(JsonValueKind.Array, body.GetProperty("buckets").ValueKind);
            Assert.Equal(JsonValueKind.Object, body.GetProperty("totals").ValueKind);
        }

        foreach (var bad in new[] { "/api/activity?hours=0", "/api/activity?hours=-1", "/api/activity?hours=x", "/api/activity?hours=99999", "/api/activity?source=abc" })
        {
            using var res = await Get(bad);
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        }
    }

    [Fact]
    public void Find_targets_skip_files_outside_the_source_folder()
    {
        var (id, _) = AddFolder("a.log");
        var source = _app.Services.GetRequiredService<SourceRepository>().Get(id)! with { SelectedFiles = ["a.log", "../outside.log"] };
        File.WriteAllText(Path.Combine(_temp.Path, "outside.log"), "x\n");
        var (targets, _) = DicomLogAnalyser.Web.Api.Endpoints.FindTargets([source]);
        Assert.Equal(["a.log"], targets.Select(t => t.RelPath));
    }

    [Fact]
    public async Task Growing_chosen_file_is_indexed_without_a_manual_scan()
    {
        var (id, dir) = AddFolder("live.log");
        var coordinator = _app.Services.GetRequiredService<ScanCoordinator>();
        var watcher = _app.Services.GetRequiredService<LiveIndexer>();
        var target = coordinator.Completed + 1;
        coordinator.EnqueueScan("test", [id]);
        await WaitFor(() => coordinator.Completed >= target && Count() == 1);

        // First check after start-up catches up (one quiet live scan); wait until it has run.
        target = coordinator.Completed + watcher.CheckOnce();
        await WaitFor(() => coordinator.Completed >= target && coordinator.Status.Queued == 0);

        // New lines are picked up without a manual scan. (If a live scan for this folder were still waiting, the
        // watcher would rightly not queue another one; that queued scan reads the new lines.)
        File.AppendAllText(Path.Combine(dir, "live.log"), "ERROR 2026.11.03 10:00:05 SCU - C-STORE failed SYNACC0042\n");
        Assert.Equal(1, watcher.CheckOnce());
        await WaitFor(() => Count() == 2);
        await WaitFor(() => coordinator.Status.Queued == 0 && coordinator.Status.State == "idle");
        Assert.Equal(0, watcher.CheckOnce());
        var hit = _app.Services.GetRequiredService<SearchService>().Search(new SearchRequest { Text = "SYNACC0042" });
        Assert.Equal(1, hit.MatchCount);
        // Live updates do not flood the activity record.
        Assert.DoesNotContain(_app.Services.GetRequiredService<DicomLogAnalyser.Core.Diagnostics.AuditLog>().Recent(50), a => a.Actor == "live");
    }

    [Fact]
    public async Task A_change_while_a_scan_is_waiting_is_covered_by_that_scan()
    {
        var (id, dir) = AddFolder("live.log");
        var coordinator = _app.Services.GetRequiredService<ScanCoordinator>();
        var watcher = _app.Services.GetRequiredService<LiveIndexer>();
        var ingestor = _app.Services.GetRequiredService<Ingestor>();
        using var gate = new ManualResetEventSlim();
        coordinator.ScanOverride = (s, p, ct) =>
        {
            gate.Wait(TimeSpan.FromSeconds(10), ct);
            return ingestor.Scan(_app.Services.GetRequiredService<SourceRepository>().Get(s.Id)!, p, ct);
        };

        // A manual scan is running (held at the gate); the watcher queues one live scan behind it.
        var target = coordinator.Completed + 2;
        coordinator.EnqueueScan("test", [id]);
        _app.Services.GetRequiredService<SourceRepository>().SetSelection(id, ["live.log"], ["live.log"]);
        Assert.Equal(1, watcher.CheckOnce());

        // The file grows while that live scan is still waiting: no second scan is queued...
        File.AppendAllText(Path.Combine(dir, "live.log"), "ERROR 2026.11.03 10:00:05 SCU - C-STORE failed SYNACC0099\n");
        Assert.Equal(0, watcher.CheckOnce());

        // ...and the waiting scan, which reads the file when it runs, indexes the new line.
        gate.Set();
        await WaitFor(() => coordinator.Completed >= target);
        Assert.Equal(2, Count());
        Assert.Equal(1, _app.Services.GetRequiredService<SearchService>().Search(new SearchRequest { Text = "SYNACC0099" }).MatchCount);
    }

    private long Count()
    {
        using var c = _app.Services.GetRequiredService<IndexDatabase>().Open();
        return IndexDatabase.Scalar<long>(c, "SELECT count(*) FROM events");
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(25);
        Assert.True(condition());
    }

}
