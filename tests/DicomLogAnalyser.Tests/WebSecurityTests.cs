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

    public async Task InitializeAsync()
    {
        var options = new AppOptions { Mode = AppMode.Local, Port = 0, DataDirectory = _temp.Sub("data"), OpenBrowser = false };
        _app = AppHost.Build(options, logging: l => l.ClearProviders());
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
    public async Task Api_requires_a_session_and_launch_tokens_are_single_use()
    {
        using (var res = await _http.GetAsync(new Uri(Base + "/api/overview"))) Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        var token = _app.Services.GetRequiredService<LaunchTokenService>().Issue();
        using (var first = await _http.GetAsync(new Uri(Base + "/launch?t=" + token))) Assert.Equal("/", first.Headers.Location!.OriginalString);
        using (var again = await _http.GetAsync(new Uri(Base + "/launch?t=" + token))) Assert.Equal("/?expired=1", again.Headers.Location!.OriginalString);
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
