namespace DicomLogAnalyser.Web.Security;

/// <summary>Values known only after the server has bound its port (local mode) or fixed by configuration (server mode).</summary>
public sealed class AppRuntime(AppOptions options)
{
    private int _port;

    public AppOptions Options { get; } = options;

    public string LocalActor { get; } = "local:" + Environment.UserName;

    public int Port => _port;

    /// <summary>Exact Host header value accepted.</summary>
    public string HostHeader => Options.Mode == AppMode.Server ? Options.PublicOrigin!.Authority : $"127.0.0.1:{_port}";

    /// <summary>Exact Origin header value accepted on state-changing requests.</summary>
    public string Origin => Options.Mode == AppMode.Server ? Options.Origin : $"http://127.0.0.1:{_port}";

    public void SetPort(int port) => _port = port;
}
