using System.Net;
using System.Net.Sockets;
using System.Text;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Tide.Application.Intake;
using Tide.Application.Security;
using Tide.Application.Settings;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Infrastructure.Notifications;

namespace Tide.IntegrationTests;

/// <summary>
/// The User-Agent outbound requests carry, against a small server on loopback that records it: the global setting for
/// everything, a source's own choice for its feed and for reading its articles.
/// </summary>
public sealed class UserAgentTests : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly List<(string Path, List<string> Agents)> _seen = [];
    private readonly Task _serving;
    private readonly TestDb _db = new();
    private readonly IntakeOptions _options = new() { Enabled = true, MinPollMinutes = 5 };
    private readonly OutboundHttp _http;

    private static readonly string Article = "<html><body><article>" + string.Concat(Enumerable.Range(0, 6).Select(i =>
        $"<p>Attackers chained two flaws in a VPN gateway, CVE-2026-1281 among them, from gateway {i}, and the vendor shipped fixes for both of them.</p>")) +
        "</article></body></html>";

    public UserAgentTests()
    {
        _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };
        _listener.Start();
        _serving = ServeAsync();
        // Loopback allowed: it's the test server.
        _http = new OutboundHttp(new OutboundCircuit(), new StaticOptionsMonitor<IntakeOptions>(_options), NullLogger<OutboundHttp>.Instance, _ => null);
    }

    private string Base => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";

    private string Feed => $"""
        <?xml version="1.0"?>
        <rss version="2.0"><channel><title>Test</title><link>{Base}/</link><description>Test</description>
        <item><title>Gateway flaws chained</title><link>{Base}/article</link><guid>{Base}/article</guid>
        <pubDate>{DateTimeOffset.UtcNow:R}</pubDate><description>Two flaws chained.</description></item>
        </channel></rss>
        """;

    private async Task ServeAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                await using var stream = client.GetStream();
                var reader = new StreamReader(stream, Encoding.ASCII);
                var path = (await reader.ReadLineAsync())?.Split(' ')[1] ?? "/";
                var agents = new List<string>();
                string? line;
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
                    if (line.StartsWith("User-Agent:", StringComparison.OrdinalIgnoreCase)) agents.Add(line["User-Agent:".Length..].Trim());
                lock (_seen) _seen.Add((path, agents));
                var (type, body) = path switch
                {
                    "/feed" => ("application/rss+xml", Feed),
                    "/article" => ("text/html", Article),
                    _ => ("text/plain", "ok"),
                };
                var bytes = Encoding.UTF8.GetBytes(body);
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: {type}\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"), _stop.Token);
                await stream.WriteAsync(bytes, _stop.Token);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException or IOException) { }
    }

    // What each request to the path carried: one User-Agent each.
    private List<string> AgentsFor(string path)
    {
        lock (_seen)
        {
            var hits = _seen.Where(s => s.Path == path).ToList();
            hits.Should().NotBeEmpty($"{path} was asked for");
            hits.Should().OnlyContain(s => s.Agents.Count == 1, "a request carries exactly one User-Agent");
            return [.. hits.Select(s => s.Agents[0])];
        }
    }

    // Adds a feed source with the given choice, polls it, then reads its one item as triage does.
    private async Task PullAndRead(UserAgentChoice choice, string? custom = null)
    {
        var intake = _db.Intake(_options, http: _http);
        var id = await intake.SaveSourceAsync(new SourceForm { Name = "Test feed", Url = Base + "/feed", PollMinutes = 30, UserAgent = choice, CustomUserAgent = custom });
        var polled = await intake.PollNowAsync(id);
        polled.Error.Should().BeNull();
        polled.New.Should().Be(1);
        await using var ctx = _db.Context();
        var item = ctx.FeedItems.Single(i => i.SourceId == id);
        (await _db.Reading(_options, http: _http).ReadAsync(item.Id)).HasText.Should().BeTrue();
    }

    [Fact]
    public async Task ASourceThatInherits_UsesTheGlobalChoice_ForItsPullAndItsArticles()
    {
        _options.UserAgent = UserAgentChoice.Custom;
        _options.CustomUserAgent = "Acme-CTI/2.0 (+https://cti.example.org)";

        await PullAndRead(UserAgentChoice.Inherit);

        AgentsFor("/feed").Should().Equal("Acme-CTI/2.0 (+https://cti.example.org)");
        AgentsFor("/article").Should().Equal("Acme-CTI/2.0 (+https://cti.example.org)");
    }

    [Fact]
    public async Task ASourcesOwnChoice_IsUsedForItsPullAndItsArticles()
    {
        _options.UserAgent = UserAgentChoice.Tide;

        await PullAndRead(UserAgentChoice.Browser);

        AgentsFor("/feed").Should().Equal(UserAgents.Browser);
        AgentsFor("/article").Should().Equal(UserAgents.Browser);
    }

    [Fact]
    public async Task ASourcesCustomString_IsUsed()
    {
        await PullAndRead(UserAgentChoice.Custom, "  FeedReader/3.1  ");

        AgentsFor("/feed").Should().Equal("FeedReader/3.1");
        AgentsFor("/article").Should().Equal("FeedReader/3.1");
    }

    [Fact]
    public async Task EverythingElse_UsesTheGlobal_TidesOwnByDefault_AndAChangeAppliesAtOnce()
    {
        // As the CVE and KEV lookups, the update check and the assistant ask: no source, so no choice of their own.
        (await _http.GetAsync(Base + "/kev")).Status.Should().Be(FetchStatus.Ok);
        AgentsFor("/kev").Should().Equal(UserAgents.Tide);
        UserAgents.Tide.Should().StartWith("TIDE/").And.EndWith("(+https://github.com/jashley92/Tide)").And.NotStartWith("TIDE/1.0 ");

        _options.UserAgent = UserAgentChoice.Browser;   // a saved setting, as the live options monitor shows it
        (await _http.GetAsync(Base + "/epss")).Status.Should().Be(FetchStatus.Ok);
        AgentsFor("/epss").Should().Equal(UserAgents.Browser);
    }

    [Fact]
    public async Task TheChosenAgent_ReplacesOneACallerSent_NeverASecondValue()
    {
        await _http.GetAsync(Base + "/nvd", new OutboundRequest(Headers: new Dictionary<string, string> { ["User-Agent"] = "Other/1.0", ["Accept"] = "application/json" }));

        AgentsFor("/nvd").Should().Equal(UserAgents.Tide);
    }

    [Fact]
    public async Task AnInvalidAgentOnARequest_IsNeverSent()
    {
        await _http.GetAsync(Base + "/bad", new OutboundRequest(UserAgent: "evil/1.0\r\nX-Injected: yes"));

        AgentsFor("/bad").Should().Equal(UserAgents.Tide);
    }

    [Fact]
    public async Task ASourcesChoice_IsChecked_AndAudited()
    {
        var intake = _db.Intake(_options, http: _http);
        Func<Task> injected = () => intake.SaveSourceAsync(new SourceForm { Name = "Bad", Url = Base + "/feed", PollMinutes = 30, UserAgent = UserAgentChoice.Custom, CustomUserAgent = "a\r\nX-Injected: 1" });
        await injected.Should().ThrowAsync<ArgumentException>();
        Func<Task> blank = () => intake.SaveSourceAsync(new SourceForm { Name = "Blank", Url = Base + "/feed", PollMinutes = 30, UserAgent = UserAgentChoice.Custom, CustomUserAgent = " " });
        await blank.Should().ThrowAsync<ArgumentException>();
        Func<Task> undefined = () => intake.SaveSourceAsync(new SourceForm { Name = "Odd", Url = Base + "/feed", PollMinutes = 30, UserAgent = (UserAgentChoice)9 });
        await undefined.Should().ThrowAsync<ArgumentException>();

        var id = await intake.SaveSourceAsync(new SourceForm { Name = "Wire", Url = Base + "/feed", PollMinutes = 30 });
        await using (var ctx = _db.Context()) ctx.Sources.Single(s => s.Id == id).UserAgent.Should().Be(UserAgentChoice.Inherit);
        var form = SourceForm.From((await intake.SourcesAsync()).Single(s => s.Id == id));
        form.UserAgent = UserAgentChoice.Custom;
        form.CustomUserAgent = "Wire-Reader/1.0";
        await intake.SaveSourceAsync(form);

        await using var after = _db.Context();
        var saved = after.Sources.Single(s => s.Id == id);
        (saved.UserAgent, saved.CustomUserAgent).Should().Be((UserAgentChoice.Custom, "Wire-Reader/1.0"));
        after.AuditLog.Where(a => a.EntityType == nameof(Source) && a.EntityKey == id.ToString() && a.Action == "Modified")
            .Should().Contain(a => a.ChangesJson!.Contains("UserAgent") && a.ChangesJson.Contains("Wire-Reader/1.0"));

        // Back to inheriting: the custom string goes with the choice.
        form = SourceForm.From(saved);
        form.UserAgent = UserAgentChoice.Inherit;
        await intake.SaveSourceAsync(form);
        await using var last = _db.Context();
        last.Sources.Single(s => s.Id == id).CustomUserAgent.Should().BeNull();

        _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst };
        Func<Task> analyst = () => intake.SaveSourceAsync(form);
        await analyst.Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task TheGlobalSetting_IsSaved_AppliesAtOnce_AndIsAudited()
    {
        using var rig = new SettingsRig();
        rig.Now<IntakeOptions>().UserAgent.Should().Be(UserAgentChoice.Tide);
        UserAgents.Global(rig.Now<IntakeOptions>()).Should().Be(UserAgents.Tide);

        await rig.Get<OperationalSettingsService>().SaveAsync(new Dictionary<string, string?>
        {
            [OperationalSettings.UserAgent] = "Custom",
            [OperationalSettings.CustomUserAgent] = " Acme-CTI/2.0 ",
        });

        UserAgents.Global(rig.Now<IntakeOptions>()).Should().Be("Acme-CTI/2.0");
        await using var ctx = rig.Db.Context();
        ctx.Settings.Single(s => s.Key == OperationalSettings.CustomUserAgent).Value.Should().Be("Acme-CTI/2.0");
        ctx.AuditLog.Should().Contain(a => a.EntityType == nameof(AppSetting) && a.EntityKey == OperationalSettings.UserAgent);
        ctx.AuditLog.Should().Contain(a => a.EntityType == nameof(AppSetting) && a.EntityKey == OperationalSettings.CustomUserAgent);

        Func<Task> injected = () => rig.Save(OperationalSettings.CustomUserAgent, "a\r\nX-Injected: 1");
        await injected.Should().ThrowAsync<ArgumentException>();
        Func<Task> inherit = () => rig.Save(OperationalSettings.UserAgent, "Inherit");
        await inherit.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task TheConfigurationBundle_CarriesASourcesChoice_AndRefusesABadOne()
    {
        using var from = new SettingsRig();
        await from.Get<IntakeService>().SaveSourceAsync(new SourceForm { Name = "Wire", Url = "https://wire.example.org/feed.xml", PollMinutes = 30,
            UserAgent = UserAgentChoice.Custom, CustomUserAgent = "Wire-Reader/1.0" });
        await from.Get<IntakeService>().SaveSourceAsync(new SourceForm { Name = "Plain", Url = "https://plain.example.org/feed.xml", PollMinutes = 30 });
        var bundle = await from.Get<Tide.Application.Operations.ConfigBundleService>().ExportAsync();
        bundle.Sources.Single(s => s.Name == "Wire").Should().Match<Tide.Application.Operations.BundleSource>(s => s.UserAgent == "Custom" && s.CustomUserAgent == "Wire-Reader/1.0");
        bundle.Sources.Single(s => s.Name == "Plain").UserAgent.Should().BeNull("inheriting is left out, as in bundles made before");

        using var to = new SettingsRig();
        var bad = bundle.Sources.Single(s => s.Name == "Plain") with { Name = "Bad", CustomUserAgent = "a\r\nX: y", UserAgent = "Custom" };
        var done = await to.Get<Tide.Application.Operations.ConfigBundleService>().PreviewAndImportAsync(bundle with { Sources = [.. bundle.Sources, bad] });
        done.Changes.Single(c => c.Item == "Bad").Kind.Should().Be(Tide.Application.Operations.BundleChangeKind.Skipped);
        await using var ctx = to.Db.Context();
        ctx.Sources.Single(s => s.Name == "Wire").Should().Match<Source>(s => s.UserAgent == UserAgentChoice.Custom && s.CustomUserAgent == "Wire-Reader/1.0");
        ctx.Sources.Single(s => s.Name == "Plain").UserAgent.Should().Be(UserAgentChoice.Inherit);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        try { await _serving; } catch (OperationCanceledException) { }
        _http.Dispose();
        _db.Dispose();
        _stop.Dispose();
    }
}
