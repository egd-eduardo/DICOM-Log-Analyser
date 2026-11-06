using System.Globalization;
using System.Net;

namespace DicomLogAnalyser.Web;

public enum AppMode
{
    Local,
    Server,
}

/// <summary>
/// Start-up configuration from command-line arguments and DLA_* environment variables. Nothing else (no
/// appsettings.json, no ASPNETCORE_URLS) can change how or where the server listens.
/// </summary>
public sealed class AppOptions
{
    public AppMode Mode { get; init; } = AppMode.Local;
    public int Port { get; init; }
    public string DataDirectory { get; init; } = "";
    public bool OpenBrowser { get; init; } = true;
    public bool HealthCheck { get; init; }

    // Central (server) mode only.
    public IPAddress ListenAddress { get; init; } = IPAddress.Loopback;
    public Uri? PublicOrigin { get; init; }
    public IReadOnlyList<System.Net.IPNetwork> TrustedProxies { get; init; } = [];
    public string IdentityHeader { get; init; } = "X-Forwarded-Email";
    public IReadOnlySet<string> AllowedUsers { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlySet<string> AllowedEmailDomains { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Central mode: identities with the administrator role. Everyone else on the allow list is a viewer.</summary>
    public IReadOnlySet<string> AdminUsers { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<string> SourceRoots { get; init; } = [];

    public long MaxUploadBytes { get; init; } = 1024L * 1024 * 1024;
    public int ScanIntervalMinutes { get; init; }

    /// <summary>Seconds between checks of chosen files for new lines (live indexing); 0 turns it off.</summary>
    public int LiveIntervalSeconds { get; init; } = 5;

    /// <summary>Read limit for folders on network shares, in megabytes per second (0 = no limit). Default 2 MB/s.</summary>
    public int NetworkReadLimitMb { get; init; } = 2;
    public TimeSpan SessionIdle { get; init; } = TimeSpan.FromMinutes(30);

    public string Origin => Mode == AppMode.Server ? PublicOrigin!.GetLeftPart(UriPartial.Authority) : "";

    public static AppOptions Parse(string[] args, Func<string, string?> env)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(env);
        string? Arg(string name)
        {
            var i = Array.IndexOf(args, name);
            if (i < 0) return null;
            if (i + 1 >= args.Length) throw new ArgumentException($"Missing value for {name}.");
            return args[i + 1];
        }

        static List<string> List(string? value) =>
            (value ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        var modeText = Arg("--mode") ?? env("DLA_MODE") ?? "local";
        var mode = modeText.ToLowerInvariant() switch
        {
            "local" => AppMode.Local,
            "server" or "central" => AppMode.Server,
            _ => throw new ArgumentException("--mode must be 'local' or 'server'."),
        };

        var portText = Arg("--port") ?? env("DLA_PORT");
        var port = mode == AppMode.Server ? 8080 : 0;
        if (portText is not null && (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port) || port is < 0 or > 65535))
            throw new ArgumentException("The port must be a number between 0 and 65535.");

        var data = Arg("--data-dir") ?? env("DLA_DATA_DIR");
        var options = new AppOptions
        {
            Mode = mode,
            Port = port,
            DataDirectory = string.IsNullOrWhiteSpace(data) ? Core.Storage.AppPaths.DefaultLocalRoot() : data,
            OpenBrowser = mode == AppMode.Local && !args.Contains("--no-browser"),
            HealthCheck = args.Contains("--healthcheck"),
            ListenAddress = IPAddress.Parse(env("DLA_LISTEN_ADDRESS") ?? (mode == AppMode.Server ? "0.0.0.0" : "127.0.0.1")),
            PublicOrigin = env("DLA_PUBLIC_ORIGIN") is { Length: > 0 } o ? new Uri(o, UriKind.Absolute) : null,
            TrustedProxies = List(env("DLA_TRUSTED_PROXIES")).Select(System.Net.IPNetwork.Parse).ToList(),
            IdentityHeader = env("DLA_IDENTITY_HEADER") is { Length: > 0 } h ? h : "X-Forwarded-Email",
            AllowedUsers = new HashSet<string>(List(env("DLA_ALLOWED_USERS")), StringComparer.OrdinalIgnoreCase),
            AllowedEmailDomains = new HashSet<string>(List(env("DLA_ALLOWED_EMAIL_DOMAINS")).Select(d => d.TrimStart('@')), StringComparer.OrdinalIgnoreCase),
            AdminUsers = new HashSet<string>(List(env("DLA_ADMIN_USERS")), StringComparer.OrdinalIgnoreCase),
            SourceRoots = List(env("DLA_SOURCE_ROOTS")),
            MaxUploadBytes = (env("DLA_MAX_UPLOAD_MB") is { Length: > 0 } mb ? long.Parse(mb, NumberStyles.None, CultureInfo.InvariantCulture) : 1024) * 1024 * 1024,
            ScanIntervalMinutes = env("DLA_SCAN_INTERVAL_MINUTES") is { Length: > 0 } si ? int.Parse(si, NumberStyles.None, CultureInfo.InvariantCulture) : 0,
            NetworkReadLimitMb = (Arg("--network-read-limit") ?? env("DLA_NETWORK_READ_LIMIT_MB")) is { Length: > 0 } nr ? int.Parse(nr, NumberStyles.None, CultureInfo.InvariantCulture) : 2,
            LiveIntervalSeconds = (Arg("--live-interval") ?? env("DLA_LIVE_INTERVAL_SECONDS")) is { Length: > 0 } li ? int.Parse(li, NumberStyles.None, CultureInfo.InvariantCulture) : 5,
            SessionIdle = TimeSpan.FromMinutes(env("DLA_SESSION_IDLE_MINUTES") is { Length: > 0 } idle ? int.Parse(idle, NumberStyles.None, CultureInfo.InvariantCulture) : 30),
        };
        return options;
    }

    /// <summary>Returns configuration problems; the application refuses to start while any exist.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (MaxUploadBytes <= 0) problems.Add("DLA_MAX_UPLOAD_MB must be positive.");
        if (ScanIntervalMinutes is < 0 or > 10080) problems.Add("DLA_SCAN_INTERVAL_MINUTES must be between 0 (off) and 10080.");
        if (NetworkReadLimitMb is < 0 or > 10_000) problems.Add("The network read limit must be between 0 (no limit) and 10000 MB/s.");
        if (LiveIntervalSeconds is not 0 and (< 2 or > 3600)) problems.Add("The live update interval must be 0 (off) or between 2 and 3600 seconds.");
        if (SessionIdle <= TimeSpan.Zero || SessionIdle > TimeSpan.FromHours(12)) problems.Add("DLA_SESSION_IDLE_MINUTES must be between 1 and 720.");
        if (Mode == AppMode.Local)
        {
            if (!IPAddress.IsLoopback(ListenAddress)) problems.Add("Local mode only listens on the loopback address (127.0.0.1).");
            return problems;
        }

        // Central mode: there is no anonymous or password mode. Identity comes from the access proxy only.
        if (PublicOrigin is null || PublicOrigin.Scheme != Uri.UriSchemeHttps || PublicOrigin.AbsolutePath != "/" || !string.IsNullOrEmpty(PublicOrigin.Query))
            problems.Add("DLA_PUBLIC_ORIGIN must be the https:// address users open, without a path (for example https://dicomlogs.example.internal).");
        if (TrustedProxies.Count == 0)
            problems.Add("DLA_TRUSTED_PROXIES must list the address range of the identity-aware proxy (for example 172.30.0.0/24).");
        if (TrustedProxies.Any(n => n.PrefixLength == 0))
            problems.Add("DLA_TRUSTED_PROXIES must not trust every address (/0).");
        if (AllowedUsers.Count == 0 && AllowedEmailDomains.Count == 0)
            problems.Add("Set DLA_ALLOWED_USERS and/or DLA_ALLOWED_EMAIL_DOMAINS: access is never granted to every authenticated identity by default.");
        if (AllowedUsers.Contains("*") || AllowedEmailDomains.Contains("*"))
            problems.Add("Wildcards are not accepted in DLA_ALLOWED_USERS or DLA_ALLOWED_EMAIL_DOMAINS.");
        if (AdminUsers.Count == 0)
            problems.Add("DLA_ADMIN_USERS must list at least one administrator (e-mail address). Other allowed users can only view and search.");
        if (AdminUsers.Any(a => a.Contains('*', StringComparison.Ordinal) || !a.Contains('@', StringComparison.Ordinal)))
            problems.Add("DLA_ADMIN_USERS must contain full e-mail addresses; wildcards and domains are not accepted.");
        else if (AdminUsers.Any(a => !IsAllowedUser(a)))
            problems.Add("Every DLA_ADMIN_USERS entry must also be allowed by DLA_ALLOWED_USERS or DLA_ALLOWED_EMAIL_DOMAINS.");
        if (SourceRoots.Count == 0)
            problems.Add("DLA_SOURCE_ROOTS must list the read-only mount points for log folders (for example /sources).");
        if (SourceRoots.Any(r => !Path.IsPathFullyQualified(r)))
            problems.Add("DLA_SOURCE_ROOTS entries must be absolute paths.");
        if (string.IsNullOrWhiteSpace(IdentityHeader) || IdentityHeader.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch == '-')))
            problems.Add("DLA_IDENTITY_HEADER must be a header name such as X-Forwarded-Email.");
        return problems;
    }

    /// <summary>Local mode: the person at this computer administers their own installation.</summary>
    public bool IsAdmin(string identity) => Mode == AppMode.Local || AdminUsers.Contains(identity);

    public bool IsAllowedUser(string identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (AllowedUsers.Contains(identity)) return true;
        var at = identity.LastIndexOf('@');
        return at > 0 && at < identity.Length - 1 && AllowedEmailDomains.Contains(identity[(at + 1)..]);
    }
}
