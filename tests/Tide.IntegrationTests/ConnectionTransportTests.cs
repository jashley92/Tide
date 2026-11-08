using System.Reflection;
using System.Security.Claims;
using AwesomeAssertions;
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
    /// <summary>Named as Blazor's hub method that starts a circuit.</summary>
    public static void StartCircuit() { /* invoked only through the filter */ }

    /// <summary>Named as Blazor's hub method that hands a page's components over, which brings a new circuit's connection up.</summary>
    public static void UpdateRootComponents() { /* invoked only through the filter */ }

    /// <summary>Any other hub method (a browser event, a JS interop call).</summary>
    public static void Ping() { /* invoked only through the filter */ }
}

/// <summary>The transport pieces on their own: names, the hub filter, the circuit's transport and the counts.</summary>
public class ConnectionTransportUnitTests
{
    private static readonly MethodInfo StartCircuit = typeof(TestHub).GetMethod(nameof(TestHub.StartCircuit))!;
    private static readonly MethodInfo Ping = typeof(TestHub).GetMethod(nameof(TestHub.Ping))!;

    /// <summary>
    /// Runs <paramref name="inside"/> as Blazor does: within the hub method that starts a circuit (or <paramref name="method"/>)
    /// on a connection over <paramref name="transport"/>, through <paramref name="filter"/> (a new one by default).
    /// </summary>
    internal static async Task InHubMethod(HttpTransportType? transport, Func<Task> inside, TransportHubFilter? filter = null, MethodInfo? method = null)
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var context = new HubInvocationContext(new FakeHubCallerContext(transport), services, new TestHub(), method ?? StartCircuit, []);
        await (filter ?? new TransportHubFilter(new TransportCounts())).InvokeMethodAsync(context, async _ => { await inside(); return null; });
    }

    /// <summary>A connection over <paramref name="transport"/> opening (and, unless <paramref name="close"/> is false, closing) through <paramref name="filter"/>.</summary>
    internal static async Task Connection(TransportHubFilter filter, HttpTransportType? transport, bool close = true)
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var lifetime = new HubLifetimeContext(new FakeHubCallerContext(transport), services, new TestHub());
        await filter.OnConnectedAsync(lifetime, _ => Task.CompletedTask);
        if (close) await filter.OnDisconnectedAsync(lifetime, null, (_, _) => Task.CompletedTask);
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
    public async Task TheFilter_MakesTheTransportAmbient_OnlyWhileACircuitMethodRuns()
    {
        HttpTransportType? seen = null;
        await InHubMethod(HttpTransportType.ServerSentEvents, () => { seen = TransportHubFilter.Current; return Task.CompletedTask; });
        seen.Should().Be(HttpTransportType.ServerSentEvents);
        TransportHubFilter.Current.Should().BeNull("it's put back once the method returns");

        seen = HttpTransportType.LongPolling;
        await InHubMethod(HttpTransportType.WebSockets, () => { seen = TransportHubFilter.Current; return Task.CompletedTask; }, method: Ping);
        seen.Should().BeNull("any other hub method passes straight through");
    }

    [Fact]
    public void TheCircuitMethods_AreBlazorsHubMethods()
    {
        var hub = typeof(CircuitHandler).Assembly.GetType("Microsoft.AspNetCore.Components.Server.ComponentHub", throwOnError: true)!;
        var methods = hub.GetMethods(BindingFlags.Public | BindingFlags.Instance).Select(m => m.Name).ToHashSet();
        TransportHubFilter.CircuitMethods.Should().BeSubsetOf(methods, "a rename in Blazor would leave circuits without their transport");
        TransportHubFilter.CircuitMethods.Should().Contain(["StartCircuit", "UpdateRootComponents", "ConnectCircuit"]);
    }

    [Fact]
    public async Task ANewCircuit_KeepsTheTransport_ItsFirstUpdateRootComponentsRanOver()
    {
        // Blazor calls a new circuit's OnConnectionUpAsync from its first UpdateRootComponents (the page handing over its
        // components), not from StartCircuit: with only StartCircuit's transport ambient, every live page showed "Unknown".
        var circuit = new PresenceCircuit(new Presence());
        await InHubMethod(HttpTransportType.LongPolling, () => circuit.OnConnectionUpAsync(null!, CancellationToken.None),
            method: typeof(TestHub).GetMethod(nameof(TestHub.UpdateRootComponents)));
        circuit.Transport.Should().Be(HttpTransportType.LongPolling);
    }

    [Fact]
    public async Task EachConnection_IsCounted_ByItsTransport_AndLeavesTheOpenCountWhenItCloses()
    {
        var counts = new TransportCounts();
        var filter = new TransportHubFilter(counts);
        await Connection(filter, HttpTransportType.LongPolling, close: false);
        Count(counts, HttpTransportType.LongPolling).Should().Be((1, 1));

        // The page reconnects over WebSockets: a new connection, the old one closed.
        await Connection(filter, HttpTransportType.WebSockets, close: false);
        using (var services = new ServiceCollection().BuildServiceProvider())
        {
            await filter.OnDisconnectedAsync(new HubLifetimeContext(new FakeHubCallerContext(HttpTransportType.LongPolling), services, new TestHub()),
                null, (_, _) => Task.CompletedTask);
        }
        Count(counts, HttpTransportType.LongPolling).Should().Be((1, 0));
        Count(counts, HttpTransportType.WebSockets).Should().Be((1, 1));

        // An unknown transport isn't counted, and nothing goes below zero.
        await Connection(filter, null);
        await Connection(filter, HttpTransportType.ServerSentEvents);
        counts.Closed(HttpTransportType.ServerSentEvents);
        Count(counts, HttpTransportType.ServerSentEvents).Should().Be((1, 0));
        counts.Snapshot().Sum(c => c.SinceStart).Should().Be(3);
    }

    [Fact]
    public async Task TheCircuit_KeepsTheTransport_ItLastConnectedOver()
    {
        var circuit = new PresenceCircuit(new Presence());
        circuit.Transport.Should().BeNull("outside a circuit it isn't known");
        await InHubMethod(HttpTransportType.LongPolling, () => circuit.OnConnectionUpAsync(null!, CancellationToken.None));
        circuit.Transport.Should().Be(HttpTransportType.LongPolling);

        await InHubMethod(HttpTransportType.WebSockets, () => circuit.OnConnectionUpAsync(null!, CancellationToken.None));
        circuit.Transport.Should().Be(HttpTransportType.WebSockets, "a reconnect over another transport");

        circuit.Connected(null);
        circuit.Transport.Should().Be(HttpTransportType.WebSockets, "an unknown one is ignored");
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
    /// <summary>The <see cref="TransportHubFilter"/> on the app's Blazor hub.</summary>
    private TransportHubFilter HubFilter()
    {
        var hub = typeof(CircuitHandler).Assembly.GetType("Microsoft.AspNetCore.Components.Server.ComponentHub", throwOnError: true)!;
        var optionsType = typeof(IOptions<>).MakeGenericType(typeof(HubOptions<>).MakeGenericType(hub));
        var options = (HubOptions)optionsType.GetProperty("Value")!.GetValue(app.Services.GetRequiredService(optionsType))!;
        var filters = (IEnumerable<IHubFilter>?)typeof(HubOptions).GetProperty("HubFilters", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(options);
        filters.Should().NotBeNull();
        return filters!.OfType<TransportHubFilter>().Single();
    }

    [Fact]
    public async Task TheFilter_IsOnBlazorsHub_CountsInTheAppsCounts_AndTheCircuitsHandlerKeepsTheTransport()
    {
        var filter = HubFilter();
        var counts = app.Services.GetRequiredService<TransportCounts>();
        counts.Should().BeSameAs(app.Services.GetRequiredService<TransportCounts>());
        var before = ConnectionTransportUnitTests.Count(counts, HttpTransportType.ServerSentEvents).SinceStart;
        await ConnectionTransportUnitTests.Connection(filter, HttpTransportType.ServerSentEvents);
        ConnectionTransportUnitTests.Count(counts, HttpTransportType.ServerSentEvents).SinceStart.Should().Be(before + 1, "the hub's filter counts in the app's counts");

        using var scope = app.Services.CreateScope();
        var circuit = scope.ServiceProvider.GetRequiredService<PresenceCircuit>();
        scope.ServiceProvider.GetServices<CircuitHandler>().Should().Contain(circuit, "the page's circuit is the one whose transport is kept");
    }

    [Fact]
    public async Task Diagnostics_ShowsLongPolling_AsAttention_WithTheFix_AndTheCounts()
    {
        using var scope = app.Services.CreateScope();
        Audit.ServiceScopes.SignIn(scope, Permission.Administer);
        var filter = HubFilter();
        await ConnectionTransportUnitTests.Connection(filter, HttpTransportType.LongPolling, close: false);
        var circuit = scope.ServiceProvider.GetRequiredService<PresenceCircuit>();
        await ConnectionTransportUnitTests.InHubMethod(HttpTransportType.LongPolling, () => { circuit.Connected(TransportHubFilter.Current); return Task.CompletedTask; }, filter);

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
        var circuit = page.Scope.ServiceProvider.GetRequiredService<PresenceCircuit>();
        await ConnectionTransportUnitTests.InHubMethod(HttpTransportType.LongPolling,
            () => { circuit.Connected(TransportHubFilter.Current); return Task.CompletedTask; }, HubFilter());
        var refresh = typeof(DiagnosticsAdmin).GetMethod("Refresh", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await page.Click(c => (Task)refresh.Invoke(c, null)!);
        // As the button's own event would, render again once it's done.
        var render = typeof(Microsoft.AspNetCore.Components.ComponentBase).GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await page.Renderer.Dispatcher.InvokeAsync(() => render.Invoke(page.Component, null));
        html = await page.HtmlOnce("Long polling, not WebSockets");
        html.Should().Contain("Long polling, not WebSockets").And.Contain("Install-WindowsFeature Web-WebSockets");
    }
}
