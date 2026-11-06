using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Http.Connections.Features;
using Microsoft.AspNetCore.SignalR;

namespace Tide.Web.Services;

/// <summary>
/// How each open page talks to the server. Blazor Server keeps one SignalR connection per page (a circuit); SignalR uses
/// a WebSocket when it can, and otherwise falls back, without saying so, to Server-Sent Events or long polling: one
/// request per keystroke, which is what an IIS host without its WebSocket Protocol feature (or a proxy that drops the
/// Upgrade header) gets. Administration › System › Diagnostics shows the admin's own and everyone's.
/// </summary>
public static class ConnectionTransport
{
    /// <summary>
    /// What to do when a connection isn't on WebSockets: word for word what Get-TideWebSocketFixText in
    /// deploy\lib\TideWebSockets.ps1 returns (a test holds the two equal), which the deploy scripts and docs say too.
    /// </summary>
    public const string Fix = "Install the IIS WebSocket Protocol feature (Install-WindowsFeature Web-WebSockets; on Windows 10/11 "
        + "Enable-WindowsOptionalFeature -Online -FeatureName IIS-WebSockets -All), and check any reverse proxy or load "
        + "balancer passes the Upgrade and Connection headers.";

    /// <summary>The transports in the order they're shown: the one wanted first.</summary>
    public static readonly IReadOnlyList<HttpTransportType> Shown =
        [HttpTransportType.WebSockets, HttpTransportType.ServerSentEvents, HttpTransportType.LongPolling];

    public static string Name(HttpTransportType? transport) => transport switch
    {
        HttpTransportType.WebSockets => "WebSockets",
        HttpTransportType.ServerSentEvents => "Server-Sent Events",
        HttpTransportType.LongPolling => "Long polling",
        _ => "Unknown",
    };

    /// <summary>The transport of a SignalR connection, read from the feature SignalR sets on it; null when it has none.</summary>
    public static HttpTransportType? Of(HubCallerContext? context) =>
        context?.Features.Get<IHttpTransportFeature>()?.TransportType is { } t && t != HttpTransportType.None ? t : null;
}

/// <summary>
/// A hub filter on Blazor's hub. It counts each connection in <see cref="TransportCounts"/> by its transport while it's
/// open, and while a hub method that starts or reconnects a circuit runs, it makes the connection's transport ambient, so
/// <see cref="PresenceCircuit"/>, whose <c>OnConnectionUpAsync</c> Blazor calls from inside that method, can keep it.
/// </summary>
public sealed class TransportHubFilter(TransportCounts counts) : IHubFilter
{
    /// <summary>
    /// Blazor's hub methods that start, reconnect or resume a circuit. Every other call passes straight through. A new
    /// circuit's <c>OnConnectionUpAsync</c> runs in its first <c>UpdateRootComponents</c> (when the page hands over its
    /// components), not in <c>StartCircuit</c>, which only creates the circuit.
    /// </summary>
    internal static readonly IReadOnlySet<string> CircuitMethods = new HashSet<string>(StringComparer.Ordinal)
    {
        "StartCircuit", "UpdateRootComponents", "ConnectCircuit", "ResumeCircuit",
    };

    private static readonly AsyncLocal<HttpTransportType?> Ambient = new();

    /// <summary>The transport of the hub connection whose circuit method is running here, or null outside one.</summary>
    public static HttpTransportType? Current => Ambient.Value;

    public ValueTask<object?> InvokeMethodAsync(HubInvocationContext invocationContext, Func<HubInvocationContext, ValueTask<object?>> next) =>
        CircuitMethods.Contains(invocationContext.HubMethodName) ? WithTransportAsync(invocationContext, next) : next(invocationContext);

    private static async ValueTask<object?> WithTransportAsync(HubInvocationContext invocationContext, Func<HubInvocationContext, ValueTask<object?>> next)
    {
        var before = Ambient.Value;
        Ambient.Value = ConnectionTransport.Of(invocationContext.Context);
        try { return await next(invocationContext); }
        finally { Ambient.Value = before; }
    }

    // SignalR calls OnDisconnectedAsync only for a connection whose OnConnectedAsync succeeded, so the two pair up.
    public async Task OnConnectedAsync(HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
    {
        await next(context);
        if (ConnectionTransport.Of(context.Context) is { } t) counts.Opened(t);
    }

    public async Task OnDisconnectedAsync(HubLifetimeContext context, Exception? exception, Func<HubLifetimeContext, Exception?, Task> next)
    {
        try { await next(context, exception); }
        finally
        {
            if (ConnectionTransport.Of(context.Context) is { } t) counts.Closed(t);
        }
    }
}

/// <summary>One transport's connections: how many have opened over it since the app started, and how many are open on it now.</summary>
public sealed record TransportCount(HttpTransportType Transport, string Name, long SinceStart, long OpenNow);

/// <summary>
/// Counts Blazor's connections by transport since the app started, for Diagnostics: whether other people are falling
/// back. Each connection counts once; a page that reconnects opens a new one, which counts again.
/// </summary>
public sealed class TransportCounts
{
    private readonly Lock _gate = new();
    private readonly Dictionary<HttpTransportType, (long SinceStart, long OpenNow)> _counts = [];

    /// <summary>A connection opened over <paramref name="transport"/>.</summary>
    public void Opened(HttpTransportType transport)
    {
        lock (_gate)
        {
            var (since, open) = _counts.GetValueOrDefault(transport);
            _counts[transport] = (since + 1, open + 1);
        }
    }

    /// <summary>A connection on <paramref name="transport"/> closed.</summary>
    public void Closed(HttpTransportType transport)
    {
        lock (_gate)
        {
            var (since, open) = _counts.GetValueOrDefault(transport);
            _counts[transport] = (since, Math.Max(0, open - 1));
        }
    }

    /// <summary>Every transport, in <see cref="ConnectionTransport.Shown"/> order, with zeros for one no connection used.</summary>
    public IReadOnlyList<TransportCount> Snapshot()
    {
        lock (_gate)
            return [.. ConnectionTransport.Shown.Select(t =>
            {
                var (since, open) = _counts.GetValueOrDefault(t);
                return new TransportCount(t, ConnectionTransport.Name(t), since, open);
            })];
    }
}
