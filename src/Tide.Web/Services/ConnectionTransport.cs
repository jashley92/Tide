using Microsoft.AspNetCore.Components.Server.Circuits;
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
    /// <summary>What to do when a circuit isn't on WebSockets: the deploy scripts and docs/INSTALL.md say the same.</summary>
    public const string Fix = "Install the IIS WebSocket Protocol feature (Install-WindowsFeature Web-WebSockets) and check any reverse "
        + "proxy or load balancer passes the Upgrade and Connection headers.";

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
/// A hub filter on Blazor's hub: while one of its methods runs (starting or reconnecting a circuit among them), the
/// connection's transport is ambient, so <see cref="CircuitTransport"/>, which Blazor calls from inside them, can read it.
/// </summary>
public sealed class TransportHubFilter : IHubFilter
{
    private static readonly AsyncLocal<HttpTransportType?> Ambient = new();

    /// <summary>The transport of the hub connection whose method is running here, or null outside one.</summary>
    public static HttpTransportType? Current => Ambient.Value;

    public async ValueTask<object?> InvokeMethodAsync(HubInvocationContext invocationContext, Func<HubInvocationContext, ValueTask<object?>> next)
    {
        var before = Ambient.Value;
        Ambient.Value = ConnectionTransport.Of(invocationContext.Context);
        try { return await next(invocationContext); }
        finally { Ambient.Value = before; }
    }

    public async Task OnConnectedAsync(HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
    {
        var before = Ambient.Value;
        Ambient.Value = ConnectionTransport.Of(context.Context);
        try { await next(context); }
        finally { Ambient.Value = before; }
    }
}

/// <summary>One transport's circuits: how many have connected over it since the app started, and how many are open on it now.</summary>
public sealed record TransportCount(HttpTransportType Transport, string Name, long SinceStart, long OpenNow);

/// <summary>Counts circuits by transport since the app started, for Diagnostics: whether other people are falling back.</summary>
public sealed class TransportCounts
{
    private readonly Lock _gate = new();
    private readonly Dictionary<HttpTransportType, (long SinceStart, long OpenNow)> _counts = [];

    /// <summary>A circuit connected for the first time over <paramref name="transport"/>.</summary>
    public void Opened(HttpTransportType transport)
    {
        lock (_gate)
        {
            var (since, open) = _counts.GetValueOrDefault(transport);
            _counts[transport] = (since + 1, open + 1);
        }
    }

    /// <summary>An open circuit reconnected over another transport: it's open on the new one now (and still counted once).</summary>
    public void Moved(HttpTransportType from, HttpTransportType to)
    {
        if (from == to) return;
        lock (_gate)
        {
            Close(from);
            var (since, open) = _counts.GetValueOrDefault(to);
            _counts[to] = (since, open + 1);
        }
    }

    /// <summary>A circuit on <paramref name="transport"/> closed.</summary>
    public void Closed(HttpTransportType transport)
    {
        lock (_gate) Close(transport);
    }

    private void Close(HttpTransportType transport)
    {
        var (since, open) = _counts.GetValueOrDefault(transport);
        _counts[transport] = (since, Math.Max(0, open - 1));
    }

    /// <summary>Every transport, in <see cref="ConnectionTransport.Shown"/> order, with zeros for one no circuit used.</summary>
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

/// <summary>
/// The transport this circuit is on, for the page that asks (scoped: one per circuit, as the pages' services are), and
/// its count in <see cref="TransportCounts"/>. Blazor calls <see cref="OnConnectionUpAsync"/> from inside the hub method
/// that started or reconnected the circuit, where <see cref="TransportHubFilter"/> has made the transport ambient.
/// </summary>
public sealed class CircuitTransport(TransportCounts counts) : CircuitHandler
{
    private HttpTransportType? _counted;

    /// <summary>The transport this circuit connected over last, or null outside a circuit (a prerender, a test's scope).</summary>
    public HttpTransportType? Current { get; private set; }

    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        Connected(TransportHubFilter.Current);
        return Task.CompletedTask;
    }

    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        Closed();
        return Task.CompletedTask;
    }

    /// <summary>The circuit (re)connected over <paramref name="transport"/>; nothing when it isn't known.</summary>
    public void Connected(HttpTransportType? transport)
    {
        if (transport is not { } t) return;
        Current = t;
        if (_counted is { } before) counts.Moved(before, t);
        else counts.Opened(t);
        _counted = t;
    }

    /// <summary>The circuit closed: it no longer counts as open.</summary>
    public void Closed()
    {
        if (_counted is { } t) counts.Closed(t);
        _counted = null;
    }
}
