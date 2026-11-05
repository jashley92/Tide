using System.Reflection;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Http.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Tide.Domain.Enums;
using Tide.Web.Components.Pages.Admin;
using Tide.Web.Services;

namespace Tide.IntegrationTests;

/// <summary>A SignalR connection's transport, as SignalR's own connection sets it.</summary>
public sealed class FakeTransportFeature(HttpTransportType transport) : IHttpTransportFeature
{
    public HttpTransportType TransportType => transport;
}

/// <summary>A hub connection carrying <see cref="FakeTransportFeature"/> (or no transport at all).</summary>
public sealed class FakeHubCallerContext : HubCallerContext
{
    private readonly FeatureCollection _features = new();

    public FakeHubCallerContext(HttpTransportType? transport = null)
    {
        if (transport is { } t) _features.Set<IHttpTransportFeature>(new FakeTransportFeature(t));
    }

    public override string ConnectionId => "test-connection";
    public override string? UserIdentifier => null;
    public override ClaimsPrincipal? User => null;
    public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
    public override IFeatureCollection Features => _features;
    public override CancellationToken ConnectionAborted => CancellationToken.None;
    public override void Abort() { /* nothing to abort */ }
}

public sealed class TestHub : Hub
{
    public static void Ping() { /* invoked only through the filter */ }
}

/// <summary>The transport pieces on their own: names, the hub filter, the per-circuit handler and the counts.</summary>
public class ConnectionTransportUnitTests
{
    private static readonly MethodInfo Ping = typeof(TestHub).GetMethod(nameof(TestHub.Ping))!;

    /// <summary>Runs <paramref name="inside"/> as Blazor does: within a hub method on a connection over <paramref name="transport"/>.</summary>
    internal static async Task InHubMethod(HttpTransportType? transport, Func<Task> inside)
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var context = new HubInvocationContext(new FakeHubCallerContext(transport), services, new TestHub(), Ping, []);
        await new TransportHubFilter().InvokeMethodAsync(context, async _ => { await inside(); return null; });
    }

    [Theory]
    [InlineData(HttpTransportType.WebSockets, "WebSockets")]
    [InlineData(HttpTransportType.ServerSentEvents, "Server-Sent Events")]
    [InlineData(HttpTransportType.LongPolling, "Long polling")]
    [InlineData(HttpTransportType.None, "Unknown")]
    [InlineData(null, "Unknown")]
    public void EachTransport_HasItsName(HttpTransportType? transport, string name) => ConnectionTransport.Name(transport).Should().Be(name);

    [Fact]
    public void TheTransport_IsReadFromTheConnectionsFeature()
    {
        ConnectionTransport.Of(new FakeHubCallerContext(HttpTransportType.LongPolling)).Should().Be(HttpTransportType.LongPolling);
        ConnectionTransport.Of(new FakeHubCallerContext(HttpTransportType.None)).Should().BeNull();
        ConnectionTransport.Of(new FakeHubCallerContext()).Should().BeNull("a connection without the feature");
        ConnectionTransport.Of(null).Should().BeNull();
    }

    [Fact]
    public async Task TheFilter_MakesTheTransportAmbient_OnlyWhileTheMethodRuns()
    {
        HttpTransportType? seen = null;
        await InHubMethod(HttpTransportType.ServerSentEvents, () => { seen = TransportHubFilter.Current; return Task.CompletedTask; });
        seen.Should().Be(HttpTransportType.ServerSentEvents);
        TransportHubFilter.Current.Should().BeNull("it's put back once the method returns");

        using var services = new ServiceCollection().BuildServiceProvider();
        var lifetime = new HubLifetimeContext(new FakeHubCallerContext(HttpTransportType.WebSockets), services, new TestHub());
        await new TransportHubFilter().OnConnectedAsync(lifetime, _ => { seen = TransportHubFilter.Current; return Task.CompletedTask; });
        seen.Should().Be(HttpTransportType.WebSockets);
        TransportHubFilter.Current.Should().BeNull();
    }

    [Fact]
    public async Task ACircuit_IsCountedOnce_ByTheTransportItConnectedOver_AndLeavesTheOpenCountWhenItCloses()
    {
        var counts = new TransportCounts();
        var circuit = new CircuitTransport(counts);
        await InHubMethod(HttpTransportType.LongPolling, () => circuit.OnConnectionUpAsync(null!, CancellationToken.None));
        circuit.Current.Should().Be(HttpTransportType.LongPolling);
        Count(counts, HttpTransportType.LongPolling).Should().Be((1, 1));

        // A reconnect over WebSockets: open on WebSockets now, still one circuit since the start.
        await InHubMethod(HttpTransportType.WebSockets, () => circuit.OnConnectionUpAsync(null!, CancellationToken.None));
        circuit.Current.Should().Be(HttpTransportType.WebSockets);
        Count(counts, HttpTransportType.LongPolling).Should().Be((1, 0));
        Count(counts, HttpTransportType.WebSockets).Should().Be((0, 1));

        // The same transport again changes nothing; an unknown one is ignored.
        circuit.Connected(HttpTransportType.WebSockets);
        circuit.Connected(null);
        circuit.Current.Should().Be(HttpTransportType.WebSockets);
        Count(counts, HttpTransportType.WebSockets).Should().Be((0, 1));

        await circuit.OnCircuitClosedAsync(null!, CancellationToken.None);
        Count(counts, HttpTransportType.WebSockets).Should().Be((0, 0));
        circuit.Closed();   // twice: nothing below zero
        counts.Closed(HttpTransportType.ServerSentEvents);
        counts.Snapshot().Should().OnlyContain(c => c.OpenNow == 0);
    }

    [Fact]
    public void TheCounts_ListEveryTransport_WebSocketsFirst()
    {
        var counts = new TransportCounts();
        counts.Opened(HttpTransportType.ServerSentEvents);
        counts.Snapshot().Select(c => (c.Name, c.SinceStart, c.OpenNow)).Should().Equal(
            ("WebSockets", 0L, 0L), ("Server-Sent Events", 1L, 1L), ("Long polling", 0L, 0L));
    }

    [Fact]
    public void YourConnection_IsOk_OnWebSockets_Attention_OnAFallback_AndInfo_WhenNotKnown()
    {
        var none = new TransportCounts().Snapshot();
        var ws = SystemDiagnostics.ConnectionChecks(new ConnectionInfo(HttpTransportType.WebSockets, none)).ToList();
        ws.Should().ContainSingle().Which.Should().Be(new HealthItem("Your connection", CheckLevel.Ok, "WebSockets."));

        var polling = SystemDiagnostics.ConnectionChecks(new ConnectionInfo(HttpTransportType.LongPolling, none)).Single();
        polling.Level.Should().Be(CheckLevel.Attention);
        polling.Detail.Should().StartWith("Long polling, not WebSockets").And.EndWith(ConnectionTransport.Fix);
        new ConnectionInfo(HttpTransportType.ServerSentEvents, none).NeedsFix.Should().BeTrue();

        var unknown = new ConnectionInfo(null, none);
        SystemDiagnostics.ConnectionChecks(unknown).Single().Level.Should().Be(CheckLevel.Info);
        unknown.CurrentName.Should().Be("Unknown");
        unknown.NeedsFix.Should().BeFalse();
    }

    [Fact]
    public void EveryonesConnections_AreAttention_WhenAnyFellBack()
    {
        var counts = new TransportCounts();
        counts.Opened(HttpTransportType.WebSockets);
        counts.Opened(HttpTransportType.WebSockets);
        var allWs = new ConnectionInfo(HttpTransportType.WebSockets, counts.Snapshot());
        SystemDiagnostics.ConnectionChecks(allWs).Last().Should().Be(
            new HealthItem("Everyone's connections", CheckLevel.Ok, "All 2 since the app started used WebSockets."));
        allWs.NeedsFix.Should().BeFalse();

        counts.Opened(HttpTransportType.LongPolling);
        counts.Opened(HttpTransportType.ServerSentEvents);
        var mixed = new ConnectionInfo(HttpTransportType.WebSockets, counts.Snapshot());
        mixed.SinceStart.Should().Be(4);
        mixed.FellBack.Should().Be(2);
        mixed.NeedsFix.Should().BeTrue();
        var everyone = SystemDiagnostics.ConnectionChecks(mixed).Last();
        everyone.Level.Should().Be(CheckLevel.Attention);
        everyone.Detail.Should().Be($"2 of 4 since the app started didn't use WebSockets (1 server-sent events, 1 long polling). {ConnectionTransport.Fix}");
    }

    internal static (long SinceStart, long OpenNow) Count(TransportCounts counts, HttpTransportType transport)
    {
        var c = counts.Snapshot().Single(x => x.Transport == transport);
        return (c.SinceStart, c.OpenNow);
    }
}

/// <summary>The same in the running app: wired into Blazor's hub and the circuit's services, and shown on Diagnostics.</summary>
public class ConnectionTransportAppTests(TideApp app) : IClassFixture<TideApp>
{
    [Fact]
    public void TheFilter_IsOnBlazorsHub_AndTheCircuitsHandlerIsItsTransport()
    {
        var hub = typeof(CircuitHandler).Assembly.GetType("Microsoft.AspNetCore.Components.Server.ComponentHub", throwOnError: true)!;
        var optionsType = typeof(IOptions<>).MakeGenericType(typeof(HubOptions<>).MakeGenericType(hub));
        var options = (HubOptions)optionsType.GetProperty("Value")!.GetValue(app.Services.GetRequiredService(optionsType))!;
        var filters = (IEnumerable<IHubFilter>?)typeof(HubOptions).GetProperty("HubFilters", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(options);
        filters.Should().NotBeNull().And.ContainSingle(f => f is TransportHubFilter);

        using var scope = app.Services.CreateScope();
        var transport = scope.ServiceProvider.GetRequiredService<CircuitTransport>();
        scope.ServiceProvider.GetServices<CircuitHandler>().Should().Contain(transport, "the page's circuit is the one counted");
        app.Services.GetRequiredService<TransportCounts>().Should().BeSameAs(app.Services.GetRequiredService<TransportCounts>());
    }

    [Fact]
    public async Task Diagnostics_ShowsLongPolling_AsAttention_WithTheFix_AndTheCounts()
    {
        using var scope = app.Services.CreateScope();
        Audit.ServiceScopes.SignIn(scope, Permission.Administer);
        var circuit = scope.ServiceProvider.GetRequiredService<CircuitTransport>();
        await ConnectionTransportUnitTests.InHubMethod(HttpTransportType.LongPolling, () => circuit.OnConnectionUpAsync(null!, CancellationToken.None));

        var report = await scope.ServiceProvider.GetRequiredService<SystemDiagnostics>().RunAsync();
        report.Connection.Current.Should().Be(HttpTransportType.LongPolling);
        report.Connection.Counts.Single(c => c.Transport == HttpTransportType.LongPolling).SinceStart.Should().BePositive();
        report.Checks.Should().Contain(c => c.Label == "Your connection" && c.Level == CheckLevel.Attention && c.Detail.Contains("Install-WindowsFeature Web-WebSockets"));
        report.Checks.Should().Contain(c => c.Label == "Everyone's connections" && c.Level == CheckLevel.Attention);
        report.Checks[1].Label.Should().Be("Your connection", "it's listed just after the database");
    }

    [Fact]
    public async Task Diagnostics_Outside_A_Circuit_SaysItIsntKnownYet()
    {
        using var scope = app.Services.CreateScope();
        Audit.ServiceScopes.SignIn(scope, Permission.Administer);
        var report = await scope.ServiceProvider.GetRequiredService<SystemDiagnostics>().RunAsync();
        report.Connection.Current.Should().BeNull();
        report.Checks.Should().Contain(c => c.Label == "Your connection" && c.Level == CheckLevel.Info);
    }

    [Fact]
    public async Task ThePage_ShowsThisPagesTransport_AndEachTransportsCounts()
    {
        await using var page = await ComponentHarness.RenderAsync<DiagnosticsAdmin>(app, "/admin/system");
        var html = await page.HtmlOnce("Connections");
        html.Should().Contain("Connections").And.Contain("Server-Sent Events").And.Contain("since the app started");

        // The page's circuit connects over long polling; Check again shows it, with the fix.
        await ConnectionTransportUnitTests.InHubMethod(HttpTransportType.LongPolling,
            () => page.Scope.ServiceProvider.GetRequiredService<CircuitTransport>().OnConnectionUpAsync(null!, CancellationToken.None));
        var refresh = typeof(DiagnosticsAdmin).GetMethod("Refresh", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await page.Click(c => (Task)refresh.Invoke(c, null)!);
        // As the button's own event would, render again once it's done.
        var render = typeof(Microsoft.AspNetCore.Components.ComponentBase).GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await page.Renderer.Dispatcher.InvokeAsync(() => render.Invoke(page.Component, null));
        html = await page.HtmlOnce("Long polling, not WebSockets");
        html.Should().Contain("Long polling, not WebSockets").And.Contain("Install-WindowsFeature Web-WebSockets");
    }
}
