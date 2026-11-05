using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace DicomLogAnalyser.Web.Security;

public static class Tokens
{
    public static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static bool FixedTimeEquals(string? a, string? b)
    {
        if (a is null || b is null) return false;
        return CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(a), System.Text.Encoding.UTF8.GetBytes(b));
    }
}

/// <summary>
/// Local mode: one-time, short-lived tokens that open a browser session without a password. A token is handed only to
/// the browser started by this process (or shown in its own console) and is consumed on first use.
/// </summary>
public sealed class LaunchTokenService(TimeProvider time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);
    private const int MaxOutstanding = 5;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _tokens = new(StringComparer.Ordinal);

    public string Issue()
    {
        Purge();
        while (_tokens.Count >= MaxOutstanding)
        {
            var oldest = _tokens.OrderBy(t => t.Value).First().Key;
            _tokens.TryRemove(oldest, out _);
        }

        var token = Tokens.NewToken();
        _tokens[token] = time.GetUtcNow() + Lifetime;
        return token;
    }

    public bool Redeem(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 128) return false;
        Purge();
        return _tokens.TryRemove(token, out var expires) && expires > time.GetUtcNow();
    }

    private void Purge()
    {
        var now = time.GetUtcNow();
        foreach (var kv in _tokens.Where(kv => kv.Value <= now).ToList()) _tokens.TryRemove(kv.Key, out _);
    }
}

public sealed class BrowserSession(string id, string csrfToken, string identity, bool isAdmin, DateTimeOffset created)
{
    /// <summary>Administrator (configure, import, export, delete) or viewer (overview, search, event detail).</summary>
    public bool IsAdmin { get; } = isAdmin;

    public string Id { get; } = id;
    public string CsrfToken { get; } = csrfToken;
    public string Identity { get; } = identity;
    public DateTimeOffset Created { get; } = created;
    public DateTimeOffset LastSeen { get; set; } = created;
}

/// <summary>Server-side sessions held in memory only; they end when the application stops.</summary>
public sealed class SessionService(TimeProvider time, TimeSpan idleTimeout)
{
    public static readonly TimeSpan AbsoluteLifetime = TimeSpan.FromHours(12);
    private const int MaxSessions = 256;
    private readonly ConcurrentDictionary<string, BrowserSession> _sessions = new(StringComparer.Ordinal);

    public BrowserSession Create(string identity, bool isAdmin)
    {
        Purge();
        while (_sessions.Count >= MaxSessions)
        {
            var oldest = _sessions.Values.OrderBy(s => s.LastSeen).First();
            _sessions.TryRemove(oldest.Id, out _);
        }

        var session = new BrowserSession(Tokens.NewToken(), Tokens.NewToken(), identity, isAdmin, time.GetUtcNow());
        _sessions[session.Id] = session;
        return session;
    }

    public BrowserSession? Validate(string? id, string identity)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 128 || !_sessions.TryGetValue(id, out var session)) return null;
        var now = time.GetUtcNow();
        if (now - session.LastSeen > idleTimeout || now - session.Created > AbsoluteLifetime || !string.Equals(session.Identity, identity, StringComparison.Ordinal))
        {
            _sessions.TryRemove(id, out _);
            return null;
        }

        session.LastSeen = now;
        return session;
    }

    public void End(string id) => _sessions.TryRemove(id, out _);

    private void Purge()
    {
        var now = time.GetUtcNow();
        foreach (var s in _sessions.Values.Where(s => now - s.LastSeen > idleTimeout || now - s.Created > AbsoluteLifetime).ToList())
            _sessions.TryRemove(s.Id, out _);
    }
}
