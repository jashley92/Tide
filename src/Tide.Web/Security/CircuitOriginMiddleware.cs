using Microsoft.Extensions.Options;
using Tide.Application.Distribution;

namespace Tide.Web.Security;

/// <summary>
/// Refuses a Blazor circuit request (<c>/_blazor</c>: negotiate, the WebSocket upgrade, long polling) whose
/// <c>Origin</c> names another site. The browser sends the signed-in user's Windows credentials with any request a
/// page makes, including one a hostile page makes to the circuit hub (cross-site WebSocket hijacking); starting a
/// circuit still needs the data-protected component markers from a page the other site can't read, but the connection
/// shouldn't be offered at all. A request with no <c>Origin</c> (a same-site GET, a non-browser client) passes.
/// <para>
/// The origin may be the request's own host, or the address readers reach the app at (<c>App:BaseUrl</c>), which is
/// what the browser sees behind a reverse proxy that forwards to another host name. The scheme isn't compared, so a
/// proxy that ends TLS still matches.
/// </para>
/// </summary>
public sealed class CircuitOriginMiddleware
{
    private readonly RequestDelegate _next;
    private readonly string? _baseAuthority;
    private readonly ILogger _log;

    public CircuitOriginMiddleware(RequestDelegate next, IOptions<AppOptions> app, ILoggerFactory loggers)
    {
        _next = next;
        _baseAuthority = Uri.TryCreate(app.Value.BaseUrl, UriKind.Absolute, out var u) ? u.Authority : null;
        _log = loggers.CreateLogger(AmbientUser.AccessLogCategory);
    }

    public Task Invoke(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/_blazor")) return _next(context);
        var origin = context.Request.Headers.Origin.ToString();
        if (origin.Length == 0 || IsAllowed(origin, context.Request.Host.Value ?? "", _baseAuthority)) return _next(context);

        // Before authentication, so there's no user to name; the request scope carries the request id.
        _log.LogWarning("Refused a circuit request to {Path} from origin {Origin}", context.Request.Path.Value, origin);
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }

    /// <summary>Whether <paramref name="origin"/> (as the header carries it) is this app: its authority is the request's
    /// host or the configured base address's.</summary>
    public static bool IsAllowed(string origin, string requestHost, string? baseAuthority)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var o) || o.Scheme is not ("http" or "https")) return false;
        return Same(o, requestHost) || (baseAuthority is not null && Same(o, baseAuthority));
    }

    // "host:port" with a default port left off, as a Host header or Uri.Authority writes it.
    private static bool Same(Uri origin, string authority)
    {
        if (!Uri.TryCreate("http://" + authority, UriKind.Absolute, out var a)) return false;
        if (!string.Equals(origin.Host, a.Host, StringComparison.OrdinalIgnoreCase)) return false;
        // A default port written out ("https://x:443") is the same as one left off.
        return origin.Port == a.Port || (origin.IsDefaultPort && a.IsDefaultPort) || (origin.IsDefaultPort && a.Port is 80 or 443) || (a.IsDefaultPort && origin.Port is 80 or 443);
    }
}
