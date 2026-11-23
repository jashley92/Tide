using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.Extensions.Options;
using Tide.Application.Operations;

namespace Tide.Web.Services;

/// <summary>
/// Feeds <see cref="PerformanceMonitor"/> the duration of every HTTP request, by route, from ASP.NET Core's own meter
/// (<c>http.server.request.duration</c>) through a <see cref="MeterListener"/>: nothing is exported, and no query string
/// is read. The Blazor connection itself (<c>/_blazor</c>) is left out: it lasts as long as the page is open.
/// </summary>
public sealed class PerformanceListener(PerformanceMonitor monitor) : IHostedService, IDisposable
{
    private const string HostingMeter = "Microsoft.AspNetCore.Hosting";
    private const string RequestDuration = "http.server.request.duration";
    private MeterListener? _listener;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _listener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == HostingMeter && instrument.Name == RequestDuration) listener.EnableMeasurementEvents(instrument);
            },
        };
        _listener.SetMeasurementEventCallback<double>(OnDuration);
        _listener.Start();
        return Task.CompletedTask;
    }

    // The duration is in seconds; the route is the endpoint's template ("/api/v1/actions/{id}"), or none for a static file.
    private void OnDuration(Instrument instrument, double seconds, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        string? route = null;
        foreach (var t in tags)
            if (t.Key == "http.route") route = t.Value as string;
        if (route is not null && route.Contains("_blazor", StringComparison.Ordinal)) return;
        monitor.Record(TimedKind.Request, route ?? "(static file)", seconds * 1000);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose() => _listener?.Dispose();
}

/// <summary>
/// Times every event on an open page (a click, a keystroke the page handles, a navigation) for
/// <see cref="PerformanceMonitor"/>, by the page it's on. One that takes longer than
/// <see cref="DiagnosticsOptions.SlowEventMs"/> (1,000 by default; 0 turns the warning off) is logged as a Warning with
/// event id <see cref="PerformanceMonitor.SlowPageEventId"/> and the page's path, without its query string (a search's
/// words are in it) and with ids shown as <c>{id}</c>. Open pages are counted where Diagnostics already counts them
/// (Connections).
/// </summary>
public sealed partial class PerformanceCircuitHandler(PerformanceMonitor monitor, NavigationManager nav, IOptionsMonitor<DiagnosticsOptions> options,
    ILogger<PerformanceCircuitHandler> log) : CircuitHandler
{
    // The page's name for the address it was last worked out for: it changes only on a navigation.
    private string? _uri;
    private string _page = "(starting)";

    public override Func<CircuitInboundActivityContext, Task> CreateInboundActivityHandler(Func<CircuitInboundActivityContext, Task> next) =>
        async context =>
        {
            var started = Stopwatch.GetTimestamp();
            try { await next(context); }
            finally
            {
                var ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                var page = Page();
                monitor.Record(TimedKind.PageEvent, page, ms);
                var threshold = options.CurrentValue.SlowEventMs;
                if (threshold > 0 && ms >= threshold)
                {
                    monitor.SlowEvent(ms, page);
                    log.LogWarning(new EventId(PerformanceMonitor.SlowPageEventId, "SlowPageEvent"), "An event on {Page} took {Ms:N0} ms (over {Threshold:N0} ms).",
                        page, ms, threshold);
                }
            }
        };

    // The page's path from the app's base, ids replaced; before the circuit knows its address, "(starting)".
    private string Page()
    {
        try
        {
            if (!ReferenceEquals(nav.Uri, _uri)) (_uri, _page) = (nav.Uri, PageOf(nav.ToBaseRelativePath(nav.Uri)));
            return _page;
        }
        catch (InvalidOperationException) { return "(starting)"; }
    }

    /// <summary>A path as the panel and the log name it: no query or fragment, a GUID or a number as <c>{id}</c>.</summary>
    internal static string PageOf(string relative)
    {
        var path = relative.Split('?', '#')[0].Trim('/');
        return "/" + string.Join('/', path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(s => IdRx().IsMatch(s) ? "{id}" : s));
    }

    [GeneratedRegex(@"^(\d+|[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}|A-\d+)$")]
    private static partial Regex IdRx();
}
