using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Tide.Web.Services;

/// <summary>
/// An OpenTelemetry (OTLP) exporter for a site that watches TIDE with its own stack (Grafana, SCOM, Splunk), off unless
/// <c>Telemetry:OtlpEndpoint</c> is set (decided 2026-11-21: opt-in; metrics only by default; traces sampled and
/// scrubbed). Metrics are the built-in ones: request durations by route, Kestrel's connections, the .NET runtime (memory,
/// GC, thread pool). Traces only when <c>Telemetry:Traces</c> is true: ASP.NET Core requests, a share of them
/// (<c>Telemetry:TraceSampleRatio</c>, 0.05), with no query string (a search's words are in one) and no database
/// statements (none are instrumented).
/// </summary>
public static class Telemetry
{
    public const string EndpointKey = "Telemetry:OtlpEndpoint";

    /// <summary>The meters exported: requests, connections and the runtime; nothing of TIDE's own data.</summary>
    public static readonly string[] Meters = ["Microsoft.AspNetCore.Hosting", "Microsoft.AspNetCore.Server.Kestrel", "System.Runtime"];

    /// <summary>Adds the exporter when an endpoint is set; returns whether it did.</summary>
    public static bool AddTideTelemetry(this IServiceCollection services, IConfiguration config)
    {
        var version = Tide.Application.Operations.AppVersionInfo.Display;
        if (!Uri.TryCreate(config[EndpointKey], UriKind.Absolute, out var endpoint)) return false;
        var protocol = string.Equals(config["Telemetry:Protocol"], "http", StringComparison.OrdinalIgnoreCase) ? OtlpExportProtocol.HttpProtobuf : OtlpExportProtocol.Grpc;
        void Exporter(OtlpExporterOptions o)
        {
            o.Endpoint = endpoint;
            o.Protocol = protocol;
            if (config["Telemetry:Headers"] is { Length: > 0 } headers) o.Headers = headers;   // e.g. an API key, "x-api-key=..."
        }
        var otel = services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService("TIDE", serviceVersion: version, serviceInstanceId: Environment.MachineName))
            .WithMetrics(m => m.AddMeter(Meters).AddOtlpExporter(Exporter));
        if (config.GetValue("Telemetry:Traces", false))
        {
            var ratio = Math.Clamp(config.GetValue("Telemetry:TraceSampleRatio", 0.05), 0, 1);
            otel.WithTracing(t => t
                .SetSampler(new ParentBasedSampler(new TraceIdRatioBasedSampler(ratio)))
                .AddAspNetCoreInstrumentation(o =>
                {
                    // The Blazor connection is one request for as long as a page is open: not a trace worth sending.
                    o.Filter = http => !http.Request.Path.StartsWithSegments("/_blazor") && !http.Request.Path.StartsWithSegments("/health");
                    // The instrumentation already redacts query values; dropping the tag also hides the parameters' names, and
                    // holds if its redaction is turned off by environment variable.
                    o.EnrichWithHttpRequest = (activity, _) => activity.SetTag("url.query", null);
                })
                .AddOtlpExporter(Exporter));
        }
        return true;
    }
}
