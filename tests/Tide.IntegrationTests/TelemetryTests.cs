using System.Diagnostics;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Tide.Web.Services;

namespace Tide.IntegrationTests;

/// <summary>
/// The OTLP exporter is off unless an endpoint is set, exports metrics only unless traces are asked for, and a trace
/// carries no query string.
/// </summary>
public class TelemetryTests
{
    private static IConfiguration Config(params (string, string?)[] pairs) =>
        new ConfigurationBuilder().AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Item1, p.Item2))).Build();

    [Fact]
    public void Nothing_is_added_without_an_endpoint_and_traces_only_when_asked()
    {
        var none = new ServiceCollection();
        Telemetry.Add(none, Config(), "1.0").Should().BeFalse();
        none.Should().BeEmpty();
        Telemetry.Add(new ServiceCollection(), Config((Telemetry.EndpointKey, "not a url")), "1.0").Should().BeFalse();

        var metrics = new ServiceCollection().AddLogging();
        Telemetry.Add(metrics, Config((Telemetry.EndpointKey, "http://collector.example:4317")), "1.0").Should().BeTrue();
        using (var sp = metrics.BuildServiceProvider())
        {
            sp.GetService<MeterProvider>().Should().NotBeNull();
            sp.GetService<TracerProvider>().Should().BeNull("metrics only by default");
        }

        var traces = new ServiceCollection().AddLogging();
        Telemetry.Add(traces, Config((Telemetry.EndpointKey, "http://collector.example:4318"), ("Telemetry:Protocol", "http"), ("Telemetry:Traces", "true")), "1.0").Should().BeTrue();
        using (var sp = traces.BuildServiceProvider()) sp.GetService<TracerProvider>().Should().NotBeNull();
    }

    [Fact]
    public void A_request_span_loses_its_query_string()
    {
        using var activity = new Activity("request");
        activity.SetTag("url.query", "q=evil.example.net");
        activity.SetTag("url.full", "https://tide.example.org/search?q=evil.example.net");
        activity.SetTag("http.target", "/search?q=evil.example.net");
        Telemetry.Scrub(activity);
        activity.GetTagItem("url.query").Should().BeNull();
        activity.GetTagItem("url.full").Should().Be("https://tide.example.org/search");
        activity.GetTagItem("http.target").Should().Be("/search");
    }
}
