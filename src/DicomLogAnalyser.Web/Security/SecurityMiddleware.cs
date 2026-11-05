using System.Net;

using DicomLogAnalyser.Core.Diagnostics;
using DicomLogAnalyser.Web.Api;

namespace DicomLogAnalyser.Web.Security;

/// <summary>
/// Request guard for both modes.
/// <list type="number">
/// <item>Host header must match exactly (defeats DNS rebinding and host-header attacks).</item>
/// <item>Fetch Metadata and Origin must indicate a same-origin request (defeats cross-site requests, including other
/// local web servers on 127.0.0.1); CORS pre-flights are never granted.</item>
/// <item>Identity: local mode uses a session started by a one-time launch link (no password); server mode trusts the
/// identity header only when the connection comes from the configured access proxy, and only for allowed users.</item>
/// <item>State-changing API calls need the per-session CSRF token and an exact Origin.</item>
/// </list>
/// </summary>
public sealed class SecurityMiddleware(RequestDelegate next, AppRuntime runtime, SessionService sessions, LaunchTokenService launchTokens, AuditLog audit,
    ILogger<SecurityMiddleware> logger)
{
    public const string CsrfHeader = "X-CSRF-Token";
    public const string LocalCookie = "dla_session";
    public const string ServerCookie = "__Host-dla_session";
    internal const string SessionItem = "dla.session";

    private static readonly IReadOnlySet<string> StaticPaths = EmbeddedUi.Paths;

    private string CookieName => runtime.Options.Mode == AppMode.Server ? ServerCookie : LocalCookie;

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        AddSecurityHeaders(context.Response, runtime.Options.Mode == AppMode.Server);
        var request = context.Request;
        var path = request.Path.Value ?? "/";

        // Liveness probe for Docker: reveals nothing, needs no identity, and is answered on any Host.
        if (path == "/healthz" && HttpMethods.IsGet(request.Method))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        if (!string.Equals(request.Host.Value, runtime.HostHeader, StringComparison.OrdinalIgnoreCase))
        {
            await Reject(context, StatusCodes.Status421MisdirectedRequest, "invalid_host").ConfigureAwait(false);
            return;
        }

        var fetchSite = request.Headers["Sec-Fetch-Site"].ToString();
        var origin = request.Headers.Origin.ToString();
        var isUnsafe = !(HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method));
        if (HttpMethods.IsOptions(request.Method))
        {
            await Reject(context, StatusCodes.Status403Forbidden, "preflight_denied").ConfigureAwait(false);
            return;
        }

        if (fetchSite.Length > 0 && fetchSite is not ("same-origin" or "none"))
        {
            var isNavigation = request.Headers["Sec-Fetch-Mode"] == "navigate" && !isUnsafe && path is "/" or "/index.html";
            if (!isNavigation)
            {
                await Reject(context, StatusCodes.Status403Forbidden, "cross_site_request").ConfigureAwait(false);
                return;
            }
        }

        if (origin.Length > 0 && !string.Equals(origin, runtime.Origin, StringComparison.Ordinal))
        {
            await Reject(context, StatusCodes.Status403Forbidden, "invalid_origin").ConfigureAwait(false);
            return;
        }

        // Identity.
        string identity;
        if (runtime.Options.Mode == AppMode.Server)
        {
            var remote = context.Connection.RemoteIpAddress;
            if (remote is null || !runtime.Options.TrustedProxies.Any(n => n.Contains(remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote)))
            {
                logger.LogWarning("Request refused: connection did not come from the trusted access proxy.");
                await Reject(context, StatusCodes.Status403Forbidden, "untrusted_proxy").ConfigureAwait(false);
                return;
            }

            var values = request.Headers[runtime.Options.IdentityHeader];
            identity = values.Count == 1 ? values[0]!.Trim() : "";
            if (identity.Length is 0 or > 320 || identity.Any(char.IsControl))
            {
                await Reject(context, StatusCodes.Status401Unauthorized, "no_identity").ConfigureAwait(false);
                return;
            }

            if (!runtime.Options.IsAllowedUser(identity))
            {
                logger.LogWarning("Request refused: authenticated identity is not on the allow list.");
                await Reject(context, StatusCodes.Status403Forbidden, "user_not_allowed").ConfigureAwait(false);
                return;
            }
        }
        else
        {
            identity = runtime.LocalActor;
            if (path == "/launch" && HttpMethods.IsGet(request.Method))
            {
                if (!launchTokens.Redeem(request.Query["t"].ToString()))
                {
                    context.Response.Redirect("/?expired=1");
                    return;
                }

                StartSession(context, identity);
                context.Response.Redirect("/");
                return;
            }
        }

        if (StaticPaths.Contains(path))
        {
            if (isUnsafe)
            {
                await Reject(context, StatusCodes.Status405MethodNotAllowed, "method_not_allowed").ConfigureAwait(false);
                return;
            }

            await next(context).ConfigureAwait(false);
            return;
        }

        if (!path.StartsWith("/api/", StringComparison.Ordinal))
        {
            await Reject(context, StatusCodes.Status404NotFound, "not_found").ConfigureAwait(false);
            return;
        }

        var session = sessions.Validate(request.Cookies[CookieName], identity);
        if (session is null)
        {
            // Server mode: the access proxy has already authenticated the user, so a safe request may start a session.
            if (runtime.Options.Mode == AppMode.Server && !isUnsafe)
            {
                session = StartSession(context, identity);
            }
            else
            {
                await Reject(context, StatusCodes.Status401Unauthorized, "not_authenticated").ConfigureAwait(false);
                return;
            }
        }

        if (isUnsafe && (!string.Equals(origin, runtime.Origin, StringComparison.Ordinal) || !Tokens.FixedTimeEquals(request.Headers[CsrfHeader].ToString(), session.CsrfToken)))
        {
            await Reject(context, StatusCodes.Status403Forbidden, "csrf_failed").ConfigureAwait(false);
            return;
        }

        // Role check before any request body is read or bound.
        if (!session.IsAdmin && context.GetEndpoint()?.Metadata.GetMetadata<AdminOnlyAttribute>() is not null)
        {
            audit.Write(session.Identity, "access_denied", new Dictionary<string, object?>
            {
                ["method"] = request.Method,
                ["endpoint"] = (context.GetEndpoint() as Microsoft.AspNetCore.Routing.RouteEndpoint)?.RoutePattern.RawText,
            });
            await Reject(context, StatusCodes.Status403Forbidden, "admin_required").ConfigureAwait(false);
            return;
        }

        context.Items[SessionItem] = session;
        await next(context).ConfigureAwait(false);
    }

    private BrowserSession StartSession(HttpContext context, string identity)
    {
        var session = sessions.Create(identity, runtime.Options.IsAdmin(identity));
        context.Response.Cookies.Append(CookieName, session.Id, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Secure = runtime.Options.Mode == AppMode.Server,
            Path = "/",
            IsEssential = true,
        });
        return session;
    }

    internal static void AddSecurityHeaders(HttpResponse response, bool https)
    {
        var h = response.Headers;
        h["Content-Security-Policy"] = "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; form-action 'none'; frame-ancestors 'none'; base-uri 'none'";
        h["X-Content-Type-Options"] = "nosniff";
        h["X-Frame-Options"] = "DENY";
        h["Referrer-Policy"] = "no-referrer";
        h["Cross-Origin-Opener-Policy"] = "same-origin";
        h["Cross-Origin-Resource-Policy"] = "same-origin";
        h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), usb=(), serial=(), hid=(), bluetooth=(), payment=()";
        h["Cache-Control"] = "no-store";
        if (https) h["Strict-Transport-Security"] = "max-age=31536000";
    }

    private static Task Reject(HttpContext context, int status, string code)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsync("{\"error\":\"" + code + "\"}");
    }

    internal static BrowserSession CurrentSession(HttpContext context) => (BrowserSession)context.Items[SessionItem]!;

    internal static bool IsLoopback(IPAddress? address) => address is not null && IPAddress.IsLoopback(address);
}
