using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DicomLogAnalyser.Web;
using DicomLogAnalyser.Web.Security;

namespace DicomLogAnalyser.Tests;

/// <summary>Runs the real host on a random loopback port and exercises the HTTP security controls.</summary>
public sealed class WebSecurityTests : IAsyncLifetime
{
    private readonly TempDir _temp = new();
    private WebApplication _app = null!;
    private int _port;
    private HttpClient _http = null!;

    private string Base => $"http://127.0.0.1:{_port}";

    private string _picked = "";

    private sealed class FakePicker(string path) : DicomLogAnalyser.Web.Platform.IFolderPicker
    {
        public bool IsSupported => true;

        public Task<DirectoryInfo?> PickAsync(CancellationToken ct) => Task.FromResult<DirectoryInfo?>(new DirectoryInfo(path));
    }

    public async Task InitializeAsync()
    {
        var options = new AppOptions { Mode = AppMode.Local, Port = 0, DataDirectory = _temp.Sub("data"), OpenBrowser = false };
        _picked = _temp.Sub("picked-by-dialog");
        _app = AppHost.Build(options, logging: l => l.ClearProviders(), folderPicker: new FakePicker(_picked));
        _port = await AppHost.StartAsync(_app);
        _http = new HttpClient(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false });
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _temp.Dispose();
    }

    private async Task<(string Cookie, string Csrf)> SignIn()
    {
        var token = _app.Services.GetRequiredService<LaunchTokenService>().Issue();
        using var res = await _http.GetAsync(new Uri(Base + "/launch?t=" + token));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        var setCookie = res.Headers.GetValues("Set-Cookie").Single();
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", setCookie, StringComparison.OrdinalIgnoreCase);
        var cookie = setCookie.Split(';')[0];
        using var req = new HttpRequestMessage(HttpMethod.Get, Base + "/api/session");
        req.Headers.Add("Cookie", cookie);
        using var s = await _http.SendAsync(req);
        var json = await s.Content.ReadFromJsonAsync<JsonElement>();
        return (cookie, json.GetProperty("csrfToken").GetString()!);
    }

    [Fact]
    public async Task Exam_languages_and_flags_are_served_from_the_allow_list_only()
    {
        using var langs = await _http.GetAsync(new Uri(Base + "/exam-i18n.json"));
        Assert.Equal(HttpStatusCode.OK, langs.StatusCode);
        Assert.StartsWith("application/json", langs.Content.Headers.ContentType!.ToString(), StringComparison.Ordinal);
        var doc = await langs.Content.ReadFromJsonAsync<JsonElement>();
        var order = doc.GetProperty("order").EnumerateArray().Select(e => e.GetString()!).ToList();
        Assert.Equal(["en", "es", "pt", "fr", "lt", "hu", "it", "el", "tr", "hr", "de"], order);

        var english = doc.GetProperty("languages").GetProperty("en");
        var placeholder = new System.Text.RegularExpressions.Regex(@"\{[a-z]+\}");
        foreach (var code in order)
        {
            var lang = doc.GetProperty("languages").GetProperty(code);
            foreach (var key in english.EnumerateObject())
            {
                var text = lang.GetProperty(key.Name).GetString()!;
                Assert.False(string.IsNullOrWhiteSpace(text), $"{code}.{key.Name} is empty");
                var expected = placeholder.Matches(key.Value.GetString()!).Select(m => m.Value).Order().ToList();
                Assert.Equal(expected, placeholder.Matches(text).Select(m => m.Value).Order().ToList());
            }

            using var flag = await _http.GetAsync(new Uri(Base + "/flags/" + lang.GetProperty("flag").GetString() + ".svg"));
            Assert.Equal(HttpStatusCode.OK, flag.StatusCode);
            Assert.Equal("image/svg+xml", flag.Content.Headers.ContentType!.MediaType);
            Assert.StartsWith("<svg", (await flag.Content.ReadAsStringAsync()).TrimStart(), StringComparison.Ordinal);
        }

        using (var unknown = await _http.GetAsync(new Uri(Base + "/flags/xx.svg"))) Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        using (var traversal = await _http.GetAsync(new Uri(Base + "/flags/..%2Fapp.js"))) Assert.Equal(HttpStatusCode.NotFound, traversal.StatusCode);
        using var post = new HttpRequestMessage(HttpMethod.Post, Base + "/flags/gb.svg");
        post.Headers.Add("Origin", Base);
        using (var res = await _http.SendAsync(post)) Assert.Equal(HttpStatusCode.MethodNotAllowed, res.StatusCode);
    }

    [Fact]
    public async Task Api_requires_a_session_and_launch_tokens_are_single_use()
    {
        using (var res = await _http.GetAsync(new Uri(Base + "/api/overview"))) Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        var token = _app.Services.GetRequiredService<LaunchTokenService>().Issue();
        using (var first = await _http.GetAsync(new Uri(Base + "/launch?t=" + token))) Assert.Equal("/", first.Headers.Location!.OriginalString);
        using (var again = await _http.GetAsync(new Uri(Base + "/launch?t=" + token))) Assert.Equal("/?expired=1", again.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task File_choice_api_lists_files_and_refuses_more_than_five_days()
    {
        var (cookie, csrf) = await SignIn();
        var logs = _temp.Sub("daily");
        for (var d = 1; d <= 7; d++)
        {
            var p = Path.Combine(logs, $"g{d}.log");
            File.WriteAllText(p, $"INF 2026.11.0{d} 10:00:00 SPS - day {d}\n");
            File.SetLastWriteTimeUtc(p, new DateTime(2026, 11, d, 12, 0, 0, DateTimeKind.Utc));
        }

        async Task<HttpResponseMessage> Send(HttpMethod m, string path, object? body = null)
        {
            using var req = new HttpRequestMessage(m, Base + path);
            req.Headers.Add("Cookie", cookie);
            if (m != HttpMethod.Get)
            {
                req.Headers.Add("Origin", Base);
                req.Headers.Add(SecurityMiddleware.CsrfHeader, csrf);
                req.Content = JsonContent.Create(body);
            }

            return await _http.SendAsync(req);
        }

        using var add = await Send(HttpMethod.Post, "/api/sources", new { label = "Daily", path = logs, timeZone = "Europe/Lisbon" });
        var id = (await add.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
        using var list = await Send(HttpMethod.Get, $"/api/sources/{id}/files");
        var body = await list.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(5, body.GetProperty("maxDays").GetInt32());
        Assert.Equal(7, body.GetProperty("files").GetArrayLength());
        Assert.Equal(5, body.GetProperty("files").EnumerateArray().Count(f => f.GetProperty("selected").GetBoolean()));

        using var tooMany = await Send(HttpMethod.Put, $"/api/sources/{id}/files", new { files = new[] { "g1.log", "g6.log" } });
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
        Assert.Contains("6 calendar days", await tooMany.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        using var outside = await Send(HttpMethod.Put, $"/api/sources/{id}/files", new { files = new[] { "../../etc/passwd" } });
        Assert.Equal(HttpStatusCode.BadRequest, outside.StatusCode);
        using var ok = await Send(HttpMethod.Put, $"/api/sources/{id}/files", new { files = new[] { "g1.log", "g5.log" } });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var saved = _app.Services.GetRequiredService<DicomLogAnalyser.Core.Sources.SourceRepository>().Get(id)!;
        Assert.Equal(["g1.log", "g5.log"], saved.SelectedFiles!.ToArray());
    }

    [Fact]
    public async Task Folder_dialog_and_folder_browser_return_folders()
    {
        var (cookie, csrf) = await SignIn();
        async Task<JsonElement> Call(HttpMethod m, string path, HttpStatusCode expected = HttpStatusCode.OK)
        {
            using var req = new HttpRequestMessage(m, Base + path);
            req.Headers.Add("Cookie", cookie);
            if (m != HttpMethod.Get)
            {
                req.Headers.Add("Origin", Base);
                req.Headers.Add(SecurityMiddleware.CsrfHeader, csrf);
                req.Content = JsonContent.Create(new { });
            }

            using var res = await _http.SendAsync(req);
            Assert.Equal(expected, res.StatusCode);
            return await res.Content.ReadFromJsonAsync<JsonElement>();
        }

        Assert.True((await Call(HttpMethod.Get, "/api/session")).GetProperty("folderDialog").GetBoolean());
        Assert.Equal(_picked, (await Call(HttpMethod.Post, "/api/folders/pick")).GetProperty("path").GetString());

        var roots = await Call(HttpMethod.Get, "/api/folders");
        Assert.True(roots.GetProperty("folders").GetArrayLength() > 0);
        var parent = _temp.Sub("browse");
        _temp.Sub("browse/Gateway");
        _temp.Sub("browse/Router");
        File.WriteAllText(Path.Combine(parent, "x.log"), "x");
        var listing = await Call(HttpMethod.Get, "/api/folders?path=" + Uri.EscapeDataString(parent));
        Assert.Equal(["Gateway", "Router"], listing.GetProperty("folders").EnumerateArray().Select(f => f.GetProperty("name").GetString()!).ToArray());
        Assert.Equal(1, listing.GetProperty("files").GetInt32());
        await Call(HttpMethod.Get, "/api/folders?path=relative/path", HttpStatusCode.BadRequest);
        await Call(HttpMethod.Get, "/api/folders?path=" + Uri.EscapeDataString(Path.Combine(parent, "missing")), HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Wrong_host_header_is_refused()
    {
        var (cookie, _) = await SignIn();
        using var req = new HttpRequestMessage(HttpMethod.Get, Base + "/api/overview");
        req.Headers.Host = "attacker.example:" + _port;
        req.Headers.Add("Cookie", cookie);
        using var res = await _http.SendAsync(req);
        Assert.Equal(HttpStatusCode.MisdirectedRequest, res.StatusCode);
    }

    [Fact]
    public async Task State_changes_need_csrf_token_and_same_origin()
    {
        var (cookie, csrf) = await SignIn();
        async Task<HttpStatusCode> Post(string? token, string? origin, string? fetchSite = null)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, Base + "/api/scan") { Content = JsonContent.Create(new { }) };
            req.Headers.Add("Cookie", cookie);
            if (token is not null) req.Headers.Add(SecurityMiddleware.CsrfHeader, token);
            if (origin is not null) req.Headers.Add("Origin", origin);
            if (fetchSite is not null) req.Headers.Add("Sec-Fetch-Site", fetchSite);
            using var res = await _http.SendAsync(req);
            return res.StatusCode;
        }

        Assert.Equal(HttpStatusCode.Forbidden, await Post(null, Base));
        Assert.Equal(HttpStatusCode.Forbidden, await Post("wrong", Base));
        Assert.Equal(HttpStatusCode.Forbidden, await Post(csrf, null));
        Assert.Equal(HttpStatusCode.Forbidden, await Post(csrf, "http://evil.example"));
        Assert.Equal(HttpStatusCode.Forbidden, await Post(csrf, Base, "cross-site"));
        Assert.Equal(HttpStatusCode.Forbidden, await Post(csrf, Base, "same-site"));
        Assert.Equal(HttpStatusCode.Accepted, await Post(csrf, Base, "same-origin"));
    }

    [Fact]
    public async Task Security_headers_are_present_and_preflight_is_denied()
    {
        using var res = await _http.GetAsync(new Uri(Base + "/"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("default-src 'none'", res.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
        Assert.Equal("DENY", res.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("no-store", res.Headers.CacheControl!.ToString());
        using var pre = new HttpRequestMessage(HttpMethod.Options, Base + "/api/scan");
        using var preRes = await _http.SendAsync(pre);
        Assert.Equal(HttpStatusCode.Forbidden, preRes.StatusCode);
        Assert.False(preRes.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Unknown_paths_are_not_served()
    {
        using var res = await _http.GetAsync(new Uri(Base + "/../../etc/passwd"));
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task End_to_end_add_source_scan_search_and_detail()
    {
        var (cookie, csrf) = await SignIn();
        var logs = _temp.Sub("logs");
        File.Copy(IndexFixture.Fixture("dicom-router/DicomRouter20261027.log"), Path.Combine(logs, "DicomRouter20261027.log"));
        using (var req = new HttpRequestMessage(HttpMethod.Post, Base + "/api/sources") { Content = JsonContent.Create(new { label = "Router", path = logs, scanNow = true }) })
        {
            req.Headers.Add("Cookie", cookie);
            req.Headers.Add("Origin", Base);
            req.Headers.Add(SecurityMiddleware.CsrfHeader, csrf);
            using var res = await _http.SendAsync(req);
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        }

        JsonElement search = default;
        for (var i = 0; i < 50; i++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, Base + "/api/search?q=" + Uri.EscapeDataString("2.25.271828182845904523536028747135266249") + "&minLevel=error");
            req.Headers.Add("Cookie", cookie);
            using var res = await _http.SendAsync(req);
            search = await res.Content.ReadFromJsonAsync<JsonElement>();
            if (search.GetProperty("matchCount").GetInt64() > 0) break;
            await Task.Delay(100);
        }

        Assert.Equal(1, search.GetProperty("matchCount").GetInt64());
        var id = search.GetProperty("items")[0].GetProperty("id").GetInt64();
        using var dreq = new HttpRequestMessage(HttpMethod.Get, Base + "/api/events/" + id);
        dreq.Headers.Add("Cookie", cookie);
        using var dres = await _http.SendAsync(dreq);
        var detail = await dres.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("verified", detail.GetProperty("evidenceStatus").GetString());
        Assert.Equal(3, detail.GetProperty("event").GetProperty("lineStart").GetInt64());
        Assert.Equal(8, detail.GetProperty("event").GetProperty("lineEnd").GetInt64());
    }

    [Fact]
    public async Task Bundle_upload_rejects_traversal_and_leaves_no_files()
    {
        var (cookie, csrf) = await SignIn();
        var ms = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            using var w = new StreamWriter(zip.CreateEntry("../../evil.log").Open());
            w.Write("x");
        }

        ms.Position = 0;
        using var req = new HttpRequestMessage(HttpMethod.Post, Base + "/api/bundles?label=Test&name=b.zip") { Content = new StreamContent(ms) };
        req.Headers.Add("Cookie", cookie);
        req.Headers.Add("Origin", Base);
        req.Headers.Add(SecurityMiddleware.CsrfHeader, csrf);
        using var res = await _http.SendAsync(req);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var data = Path.Combine(_temp.Path, "data");
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(data, "tmp")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(data, "bundles")));
    }
}

/// <summary>Central mode: identity comes only from the trusted proxy and must be on the allow list.</summary>
public sealed class ServerModeTests : IAsyncLifetime
{
    private readonly TempDir _temp = new();
    private WebApplication _app = null!;
    private int _port;
    private readonly HttpClient _http = new(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false });

    private async Task Start(string trusted)
    {
        var options = new AppOptions
        {
            Mode = AppMode.Server,
            Port = 0,
            ListenAddress = IPAddress.Loopback,
            DataDirectory = _temp.Sub("data"),
            OpenBrowser = false,
            PublicOrigin = new Uri("https://dicomlogs.example.internal"),
            TrustedProxies = [System.Net.IPNetwork.Parse(trusted)],
            AllowedEmailDomains = new HashSet<string>(["example.org"], StringComparer.OrdinalIgnoreCase),
            AdminUsers = new HashSet<string>(["admin@example.org"], StringComparer.OrdinalIgnoreCase),
            SourceRoots = [_temp.Sub("sources")],
        };
        _app = AppHost.Build(options, logging: l => l.ClearProviders());
        _port = await AppHost.StartAsync(_app);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        _http.Dispose();
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _temp.Dispose();
    }

    private async Task<HttpResponseMessage> Get(string path, string? identity)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{_port}{path}");
        req.Headers.Host = "dicomlogs.example.internal";
        if (identity is not null) req.Headers.Add("X-Forwarded-Email", identity);
        return await _http.SendAsync(req);
    }

    [Fact]
    public async Task Allowed_identity_from_trusted_proxy_gets_a_secure_session()
    {
        await Start("127.0.0.1/32");
        using var res = await Get("/api/session", "a.person@example.org");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var cookie = res.Headers.GetValues("Set-Cookie").Single();
        Assert.StartsWith("__Host-dla_session=", cookie, StringComparison.Ordinal);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("max-age=31536000", res.Headers.GetValues("Strict-Transport-Security").Single());
    }

    [Fact]
    public async Task Missing_or_disallowed_identity_is_refused()
    {
        await Start("127.0.0.1/32");
        using (var none = await Get("/api/session", null)) Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);
        using (var other = await Get("/api/session", "intruder@elsewhere.example")) Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);
    }

    [Fact]
    public async Task Identity_header_from_an_untrusted_peer_is_ignored()
    {
        await Start("10.99.0.0/16");
        using var res = await Get("/api/session", "a.person@example.org");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        using var health = await Get("/healthz", null);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    private async Task<HttpResponseMessage> Send(HttpMethod method, string path, string identity, object? body = null)
    {
        using var first = await Get("/api/session", identity);
        var cookie = first.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        var csrf = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("csrfToken").GetString()!;
        var req = new HttpRequestMessage(method, $"http://127.0.0.1:{_port}{path}");
        req.Headers.Host = "dicomlogs.example.internal";
        req.Headers.Add("X-Forwarded-Email", identity);
        req.Headers.Add("Cookie", cookie);
        if (method != HttpMethod.Get)
        {
            req.Headers.Add("Origin", "https://dicomlogs.example.internal");
            req.Headers.Add(SecurityMiddleware.CsrfHeader, csrf);
            req.Content = JsonContent.Create(body ?? new { confirm = "no", path = "/x", label = "x" });
        }

        return await _http.SendAsync(req);
    }

    [Theory]
    [InlineData("POST", "/api/scan")]
    [InlineData("POST", "/api/scan/cancel")]
    [InlineData("POST", "/api/sources")]
    [InlineData("POST", "/api/sources/validate")]
    [InlineData("PUT", "/api/sources/1")]
    [InlineData("DELETE", "/api/sources/1")]
    [InlineData("POST", "/api/bundles?label=x")]
    [InlineData("GET", "/api/export.csv")]
    [InlineData("GET", "/api/sources/1/files")]
    [InlineData("PUT", "/api/sources/1/files")]
    [InlineData("GET", "/api/maintenance")]
    [InlineData("POST", "/api/maintenance/clean-temp")]
    [InlineData("POST", "/api/maintenance/purge-index")]
    [InlineData("POST", "/api/maintenance/fresh-session")]
    [InlineData("POST", "/api/folders/pick")]
    [InlineData("GET", "/api/folders")]
    [InlineData("GET", "/api/maintenance/patterns.csv")]
    public async Task Viewers_cannot_change_export_or_administer(string method, string path)
    {
        await Start("127.0.0.1/32");
        using var res = await Send(new HttpMethod(method), path, "viewer@example.org");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Equal("admin_required", (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        var audit = _app.Services.GetRequiredService<DicomLogAnalyser.Core.Diagnostics.AuditLog>().Recent(5);
        Assert.Contains(audit, a => a.Action == "access_denied" && a.Actor == "viewer@example.org");
    }

    [Fact]
    public async Task Viewers_can_search_and_see_sources_without_paths_admins_can_administer()
    {
        await Start("127.0.0.1/32");
        _app.Services.GetRequiredService<DicomLogAnalyser.Core.Sources.SourceRepository>().Add(new DicomLogAnalyser.Core.Sources.SourceDefinition
        {
            Label = "Gateway",
            Kind = "folder",
            Path = "/sources/secret-share-name",
        });
        using (var s = await Get("/api/session", "viewer@example.org"))
            Assert.Equal("viewer", (await s.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("role").GetString());
        using (var search = await Send(HttpMethod.Get, "/api/search?q=anything", "viewer@example.org")) Assert.Equal(HttpStatusCode.OK, search.StatusCode);
        using (var ov = await Send(HttpMethod.Get, "/api/overview", "viewer@example.org")) Assert.Equal(HttpStatusCode.OK, ov.StatusCode);
        using (var src = await Send(HttpMethod.Get, "/api/sources", "viewer@example.org"))
        {
            var body = await src.Content.ReadAsStringAsync();
            Assert.Contains("Gateway", body, StringComparison.Ordinal);
            Assert.DoesNotContain("secret-share-name", body, StringComparison.Ordinal);
        }

        using (var src = await Send(HttpMethod.Get, "/api/sources", "admin@example.org"))
            Assert.Contains("secret-share-name", await src.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        using (var scan = await Send(HttpMethod.Post, "/api/scan", "admin@example.org", new { })) Assert.Equal(HttpStatusCode.Accepted, scan.StatusCode);
        using (var m = await Send(HttpMethod.Get, "/api/maintenance", "admin@example.org")) Assert.Equal(HttpStatusCode.OK, m.StatusCode);
    }

    [Fact]
    public async Task Central_mode_folder_browser_is_confined_to_mounted_sources_and_has_no_dialog()
    {
        await Start("127.0.0.1/32");
        var roots = _app.Services.GetRequiredService<AppOptions>().SourceRoots[0];
        Directory.CreateDirectory(Path.Combine(roots, "site-a"));
        using (var s = await Send(HttpMethod.Get, "/api/session", "admin@example.org"))
            Assert.False((await s.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("folderDialog").GetBoolean());
        using (var top = await Send(HttpMethod.Get, "/api/folders", "admin@example.org"))
            Assert.Equal(roots, (await top.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("folders")[0].GetProperty("path").GetString());
        using (var inside = await Send(HttpMethod.Get, "/api/folders?path=" + Uri.EscapeDataString(roots), "admin@example.org"))
        {
            var body = await inside.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("site-a", body.GetProperty("folders")[0].GetProperty("name").GetString());
            Assert.Equal(JsonValueKind.Null, body.GetProperty("parent").ValueKind);
        }

        using (var outside = await Send(HttpMethod.Get, "/api/folders?path=" + Uri.EscapeDataString(_temp.Path), "admin@example.org"))
            Assert.Equal(HttpStatusCode.Forbidden, outside.StatusCode);
        using (var pick = await Send(HttpMethod.Post, "/api/folders/pick", "admin@example.org", new { }))
            Assert.Equal(HttpStatusCode.Conflict, pick.StatusCode);
    }

    [Fact]
    public async Task Session_is_bound_to_the_identity()
    {
        await Start("127.0.0.1/32");
        using var first = await Get("/api/session", "a.person@example.org");
        var cookie = first.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        using var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{_port}/api/scan") { Content = JsonContent.Create(new { }) };
        req.Headers.Host = "dicomlogs.example.internal";
        req.Headers.Add("X-Forwarded-Email", "b.person@example.org");
        req.Headers.Add("Cookie", cookie);
        req.Headers.Add("Origin", "https://dicomlogs.example.internal");
        using var res = await _http.SendAsync(req);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }
}
