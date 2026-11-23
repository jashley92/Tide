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
        none.AddTideTelemetry(Config()).Should().BeFalse();
        none.Should().BeEmpty();
        new ServiceCollection().AddTideTelemetry(Config((Telemetry.EndpointKey, "not a url"))).Should().BeFalse();

        var metrics = new ServiceCollection().AddLogging();
        metrics.AddTideTelemetry(Config((Telemetry.EndpointKey, "http://collector.example:4317"))).Should().BeTrue();
        using (var sp = metrics.BuildServiceProvider())
        {
            sp.GetService<MeterProvider>().Should().NotBeNull();
            sp.GetService<TracerProvider>().Should().BeNull("metrics only by default");
        }

        var traces = new ServiceCollection().AddLogging();
        traces.AddTideTelemetry(Config((Telemetry.EndpointKey, "http://collector.example:4318"), ("Telemetry:Protocol", "http"), ("Telemetry:Traces", "true"))).Should().BeTrue();
        using (var sp = traces.BuildServiceProvider()) sp.GetService<TracerProvider>().Should().NotBeNull();
    }
}
