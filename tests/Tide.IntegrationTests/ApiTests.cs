using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Actions;
using Tide.Application.Api;
using Tide.Application.Articles;
using Tide.Application.Security;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.IntegrationTests;

/// <summary>EP-75's services: tokens, the read model and webhooks.</summary>
public class ApiServiceTests : IDisposable
{
    private TestDb _db = new();

    public ApiServiceTests() => _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task AToken_IsStoredOnlyAsAHash_AndStopsWhenRevokedOrExpired()
    {
        var issued = await _db.ApiTokens().IssueAsync("XSOAR playbooks", ["actions:read", "nonsense", ApiScopes.ActionsWrite], TlpLevel.Green, 30);

        issued.Token.Should().StartWith(ApiTokenService.Prefix).And.HaveLength(ApiTokenService.Prefix.Length + 43);
        await using (var ctx = _db.Context())
        {
            var row = await ctx.ApiTokens.SingleAsync();
            row.Hash.Should().Be(ApiTokenService.Hash(issued.Token)).And.NotContain(issued.Token[ApiTokenService.Prefix.Length..]);
            row.Scopes.Should().Be("actions:read actions:write", "unknown scopes are dropped");
        }
        var caller = await _db.ApiTokens().AuthenticateAsync(issued.Token);
        caller!.Name.Should().Be("XSOAR playbooks");
        (await _db.ApiTokens(new ShiftedClock(TimeSpan.FromDays(31))).AuthenticateAsync(issued.Token)).Should().BeNull("it expired");
        (await _db.ApiTokens().AuthenticateAsync(issued.Token + "x")).Should().BeNull();
        await _db.ApiTokens().RevokeAsync(issued.Id);
        (await _db.ApiTokens().AuthenticateAsync(issued.Token)).Should().BeNull("it was revoked");
        (await _db.ApiTokens().ListAsync()).Single().Calls.Should().Be(1);

        _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst };
        await FluentActions.Invoking(() => _db.ApiTokens().IssueAsync("x", [ApiScopes.ActionsRead], TlpLevel.Clear, 1)).Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task ABrief_IsCutAsTheTeamEditionIs_AtTheLowerCeiling()
    {
        var id = await _db.Articles().CreateAsync("VPN flaws chained", "Vendor", "https://vendor.example.com/a");
        var form = ArticleForm.From((await _db.Articles().GetAsync(id))!);
        form.Bluf = "Patch the gateways.";
        form.Bullets = [new BulletForm { Text = "public detail", Tlp = TlpLevel.Clear }, new BulletForm { Text = "our gateways", Tlp = TlpLevel.Amber }];
        await _db.Articles().SaveAsync(form);
        var day = new DateOnly(2026, 10, 26);
        var brief = await _db.Briefs().GetOrStartAsync(day);
        await _db.Briefs().AddArticleAsync(brief.Id, id);

        var green = await _db.Api().BriefAsync(day, TlpLevel.Green);
        var team = await _db.Api().BriefAsync(day, null);

        green!.Articles.Single().Bullets.Select(b => b.Text).Should().Equal("public detail");
        green.Articles.Single().WithheldParts.Should().Be(1);
        green.Ceiling.Should().Be("TLP:GREEN");
        team!.Articles.Single().Bullets.Should().HaveCount(2, "a person reads the team edition at its own ceiling");
        (await _db.Api().BriefAsync(day.AddDays(1), null)).Should().BeNull();
    }

    [Fact]
    public async Task ActionsFromAnArticleAboveTheTokensCeiling_AreWithheld()
    {
        var red = await _db.Articles().CreateAsync("Intrusion at a partner", "Partner", "https://partner.example.org/r");
        var form = ArticleForm.From((await _db.Articles().GetAsync(red))!);
        form.Tlp = TlpLevel.Red;
        await _db.Articles().SaveAsync(form);
        var team = await _db.Teams().AddAsync("Network", null, null);
        var secret = await _db.Actions().CreateAsync(new ActionForm { Title = "Block 203.0.113.7 from the partner's report", TeamId = team, ArticleId = red });
        var plain = await _db.Actions().CreateAsync(new ActionForm { Title = "Patch the gateways", TeamId = team });

        (await _db.Api().ActionsAsync(ActionView.Open, TlpLevel.Green)).Select(a => a.Number).Should().Equal(plain);
        (await _db.Api().ActionAsync(secret, TlpLevel.Green)).Should().BeNull("it's answered as not there");
        (await _db.Api().UpdateActionAsync(secret, new ApiActionUpdate("InProgress", null, null), TlpLevel.Green)).Should().BeNull();
        (await _db.Api().ActionAsync(secret, null))!.Status.Should().Be("Open", "the refused change wasn't made");
        (await _db.Api().ActionsAsync(ActionView.Open, null)).Select(a => a.Number).Should().BeEquivalentTo([secret, plain], "a person sees what the Actions page shows");
        (await _db.Api().ActionAsync(secret, TlpLevel.Red)).Should().BeNull("a RED token is still held to the team edition's ceiling, as its brief is");
    }

    [Fact]
    public async Task AProposedItem_LandsInNew_UnderASourceNamedForTheCaller()
    {
        var id = await _db.Api().ProposeAsync(new ApiProposal("Suspicious sign-ins from 203.0.113.9, CVE-2026-4242", "https://siem.example.org/alerts/77", "Rule 12 fired."));

        await using var ctx = _db.Context();
        var item = await ctx.FeedItems.Include(i => i.Source).SingleAsync(i => i.Id == id);
        item.Status.Should().Be(FeedItemStatus.New);
        item.Cves.Should().Be("CVE-2026-4242");
        item.Source!.Kind.Should().Be(SourceKind.Api);
        item.Source.Enabled.Should().BeFalse("it's never polled");
        (await _db.Api().ProposeAsync(new ApiProposal("Again", "https://siem.example.org/alerts/77", null))).Should().BeNull("the same address is one item");
        await FluentActions.Invoking(() => _db.Api().ProposeAsync(new ApiProposal("", "ftp://x", null))).Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Webhooks_PostWhatHappened_Signed_AndTryAgainAfterAFailure()
    {
        var added = await _db.Webhooks().AddAsync("https://xsoar.example.org/hooks/tide", [WebhookService.ActionRaised, WebhookService.BriefSent]);
        await FluentActions.Invoking(() => _db.Webhooks().AddAsync("http://plain.example.org", [WebhookService.ActionRaised])).Should().ThrowAsync<ArgumentException>();
        var team = await _db.Teams().AddAsync("Network", null, null);
        var number = await _db.Actions().CreateAsync(new ActionForm { Title = "Patch the gateways", TeamId = team, Priority = Priority.P1 });

        _db.Posts.Status = 503;
        var later = new ShiftedClock(WebhookService.SettleDelay);
        (await _db.Webhooks(later).DeliverDueAsync()).Should().Be(1);
        (await _db.Webhooks().ListAsync()).Single().LastError.Should().Contain("503");

        _db.Posts.Status = 200;
        await _db.Webhooks(later).DeliverDueAsync();
        _db.Posts.Posted.Should().HaveCount(2, "the failed post was tried again");
        var (url, body, headers) = _db.Posts.Posted[^1];
        url.Should().Be("https://xsoar.example.org/hooks/tide");
        headers["X-Tide-Event"].Should().Be(WebhookService.ActionRaised);
        headers["X-Tide-Signature"].Should().Be(WebhookService.Sign(added.Secret, headers["X-Tide-Timestamp"], body));
        var json = JsonDocument.Parse(body).RootElement;
        json.GetProperty("data").GetProperty("label").GetString().Should().Be($"A-{number}");
        json.GetProperty("data").GetProperty("url").GetString().Should().Be($"https://tide.example.org/actions/{number}");

        await _db.Webhooks(later).DeliverDueAsync();
        _db.Posts.Posted.Should().HaveCount(2, "nothing new happened");
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task Overdue_IsPostedOncePerDay(string provider)
    {
        _db = _db.On(provider);
        await _db.Webhooks().AddAsync("https://xsoar.example.org/hooks/tide", [WebhookService.ActionOverdue]);
        var team = await _db.Teams().AddAsync("Network", null, null);
        await _db.Actions().CreateAsync(new ActionForm { Title = "Late", TeamId = team, DueDate = _db.Clock.Today.AddDays(-1) });

        await _db.Webhooks().DeliverDueAsync();
        await _db.Webhooks().DeliverDueAsync();

        _db.Posts.Posted.Should().ContainSingle().Which.Headers["X-Tide-Event"].Should().Be(WebhookService.ActionOverdue);
    }

    [Fact]
    public async Task AWebhookSwitchedOffAndOnDuringARun_KeepsTheRestartedWatermark()
    {
        var hook = await _db.Webhooks().AddAsync("https://xsoar.example.org/hooks/tide", [WebhookService.ActionRaised]);
        var team = await _db.Teams().AddAsync("Network", null, null);
        await _db.Actions().CreateAsync(new ActionForm { Title = "Patch the gateways", TeamId = team });
        _db.Posts.During = async () =>
        {
            _db.Posts.During = null;
            await _db.Webhooks().SetEnabledAsync(hook.Id, false);
            await _db.Webhooks().SetEnabledAsync(hook.Id, true);
        };

        (await _db.Webhooks(new ShiftedClock(TimeSpan.FromMinutes(5))).DeliverDueAsync()).Should().Be(1);

        await using var db = _db.Context();
        var row = await db.Webhooks.AsNoTracking().SingleAsync();
        row.SentUpToUtc.Should().BeBefore(DateTimeOffset.UtcNow.AddMinutes(1), "the run's watermark (five minutes on) would undo the restart");
        row.LastStatus.Should().Be(200, "the post's result is still recorded");
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task Webhooks_LeaveOutActionsAboveTheTeamCeiling_AndMarkEachWithItsArticlesTlp(string provider)
    {
        _db = _db.On(provider);
        await _db.Webhooks().AddAsync("https://xsoar.example.org/hooks/tide", [WebhookService.ActionRaised, WebhookService.ActionOverdue]);
        var team = await _db.Teams().AddAsync("Network", null, null);
        var red = await _db.Articles().CreateAsync("Partner RED report", "Partner", null, TlpLevel.Red);
        var amber = await _db.Articles().CreateAsync("ISAC note", "ISAC", null, TlpLevel.Amber);
        var yesterday = _db.Clock.Today.AddDays(-1);
        await _db.Actions().CreateAsync(new ActionForm { Title = "Block 203.0.113.7", TeamId = team, ArticleId = red, DueDate = yesterday });
        var partner = await _db.Actions().CreateAsync(new ActionForm { Title = "Reset partner keys", TeamId = team, ArticleId = amber, DueDate = yesterday });
        var plain = await _db.Actions().CreateAsync(new ActionForm { Title = "Rotate the VPN cert", TeamId = team });

        // The job has no user: it reads the ceiling without one. The team edition's default is AMBER+STRICT.
        _db.User.Roles = new HashSet<AppRole>();
        await _db.Webhooks(new ShiftedClock(WebhookService.SettleDelay)).DeliverDueAsync();
        var posts = _db.Posts.Posted.Select(p => (Event: p.Headers["X-Tide-Event"], Data: JsonDocument.Parse(p.Body).RootElement.GetProperty("data"))).ToList();
        posts.Should().NotContain(p => p.Data.GetProperty("title").GetString()!.Contains("203.0.113.7"), "its article is above the team edition's ceiling");
        posts.Where(p => p.Event == WebhookService.ActionRaised).Select(p => p.Data.GetProperty("number").GetInt32()).Should().BeEquivalentTo(new[] { partner, plain });
        posts.Where(p => p.Event == WebhookService.ActionOverdue).Should().ContainSingle()
            .Which.Data.GetProperty("marking").GetString().Should().Be("TLP:AMBER");
        posts.Single(p => p.Data.GetProperty("number").GetInt32() == plain).Data.GetProperty("marking").ValueKind.Should().Be(JsonValueKind.Null);

        // With a RED team ceiling, an action raised from the RED article goes, marked RED.
        _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };
        await _db.Settings().SaveAsync(TlpLevel.Green, TlpLevel.Red);
        var redAction = await _db.Actions().CreateAsync(new ActionForm { Title = "Sweep for the RED indicators", TeamId = team, ArticleId = red });
        _db.Posts.Posted.Clear();
        await _db.Webhooks(new ShiftedClock(WebhookService.SettleDelay)).DeliverDueAsync();
        var data = JsonDocument.Parse(_db.Posts.Posted.Single().Body).RootElement.GetProperty("data");
        data.GetProperty("number").GetInt32().Should().Be(redAction);
        data.GetProperty("marking").GetString().Should().Be("TLP:RED");
    }
}

/// <summary>EP-75 over HTTP: a bearer token signs in, held to its scopes; people sign in as before.</summary>
public class ApiHttpTests(TideApp app) : IClassFixture<TideApp>
{
    private async Task<string> Token(params string[] scopes)
    {
        await using var scope = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst);
        return (await scope.ServiceProvider.GetRequiredService<ApiTokenService>().IssueAsync("Test playbook", scopes, TlpLevel.Green, 1)).Token;
    }

    private HttpClient Client(string? token)
    {
        var client = app.CreateClient();
        if (token is not null) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<int> NewAction(string title)
    {
        await using var scope = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst);
        var team = (await scope.ServiceProvider.GetRequiredService<TeamService>().ListAsync())[0].Id;
        return await scope.ServiceProvider.GetRequiredService<ActionService>().CreateAsync(new ActionForm { Title = title, TeamId = team });
    }

    [Fact]
    public async Task AToken_ReadsAndChangesActions_AsItsScopesAllow()
    {
        var number = await NewAction("api-http: block the domain");
        var client = Client(await Token(ApiScopes.ActionsRead, ApiScopes.ActionsWrite));

        var list = await client.GetFromJsonAsync<List<ApiAction>>("/api/v1/actions");
        list.Should().Contain(a => a.Number == number);

        var patched = await client.PatchAsJsonAsync($"/api/v1/actions/{number}", new { status = "InProgress", note = "XSOAR incident 88213 started." });
        patched.StatusCode.Should().Be(HttpStatusCode.OK);
        (await patched.Content.ReadFromJsonAsync<ApiAction>())!.Status.Should().Be("InProgress");
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Tide.Infrastructure.Persistence.AppDbContext>();
        var action = await db.Actions.SingleAsync(a => a.Number == number);
        action.ModifiedBy.Should().StartWith("api:", "the change is audited under the token");
        (await db.ActionEvents.Where(e => e.ActionId == action.Id).Select(e => e.ByName).ToListAsync()).Should().Contain("Test playbook", "its history names the caller");

        (await client.PatchAsJsonAsync($"/api/v1/actions/{number}", new { status = "Sideways" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.GetAsync("/api/v1/actions/999999")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AScopeNotHeld_IsRefused_AndABadTokenIsntSignedIn()
    {
        var reader = Client(await Token(ApiScopes.ActionsRead));

        var refused = await reader.GetAsync("/api/v1/metrics");
        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await refused.Content.ReadAsStringAsync()).Should().Contain("metrics:read");
        (await reader.PatchAsJsonAsync("/api/v1/actions/1", new { status = "Done" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Client("tide_pat_not-a-real-token-at-all").GetAsync("/api/v1/actions")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ThePageAndTheDescription_AreServed()
    {
        var openapi = await Client(await Token(ApiScopes.StackRead)).GetStringAsync("/api/v1/openapi.json");
        JsonDocument.Parse(openapi).RootElement.GetProperty("paths").EnumerateObject().Select(p => p.Name).Should().Contain("/actions/{number}");

        await using var page = await ComponentHarness.RenderAsync<Tide.Web.Components.Pages.Admin.ApiAdmin>(app, "/admin/api");
        var html = await page.Html();
        html.Should().Contain("API access").And.Contain("actions:write").And.Contain("Issue the token").And.Contain("Add the webhook");
    }
}

/// <summary>EP-75's other endpoints and webhook upkeep, over HTTP and through the services.</summary>
public class ApiMoreTests(TideApp app) : IClassFixture<TideApp>
{
    private async Task<HttpClient> Client(params string[] scopes)
    {
        await using var scope = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst);
        var token = (await scope.ServiceProvider.GetRequiredService<ApiTokenService>().IssueAsync("More tests", scopes, TlpLevel.Amber, 1)).Token;
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task Metrics_AreReadAndEnteredByKey()
    {
        await using (var scope = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst))
        {
            var metrics = scope.ServiceProvider.GetRequiredService<Tide.Application.Metrics.MetricsService>();
            var group = await metrics.AddGroupAsync("api-more group");
            await metrics.SaveMetricAsync(new Tide.Application.Metrics.MetricForm { GroupId = group, Name = "api-more phishing reports" });
        }
        var client = await Client(ApiScopes.MetricsRead, ApiScopes.MetricsWrite);

        var before = await client.GetFromJsonAsync<ApiMetrics>("/api/v1/metrics");
        var key = before!.Metrics.Single(m => m.Name == "api-more phishing reports").Key;
        var day = Tide.Application.Org.WorkingDays.Weekdays.Previous(DateOnly.FromDateTime(DateTime.UtcNow.Date).AddDays(-2)).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        (await client.PostAsJsonAsync("/api/v1/metrics", new { key, day, value = 42, note = "from the SIEM" })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        var after = await client.GetFromJsonAsync<ApiMetrics>($"/api/v1/metrics?from={day}&to={day}");
        after!.Values.Should().Contain(v => v.Key == key && v.Value == 42);

        (await client.PostAsJsonAsync("/api/v1/metrics", new { key = "no-such-metric", day, value = 1 })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.PostAsJsonAsync("/api/v1/metrics", new { key })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.GetAsync("/api/v1/metrics?from=yesterday")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Stack_Triage_AndBriefs_AnswerAsDescribed()
    {
        await using (var scope = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst))
            await scope.ServiceProvider.GetRequiredService<Tide.Application.Stack.StackService>().SaveAsync(new Tide.Application.Stack.StackForm { Name = "api-more GitLab", Notes = "Two hosts." });
        var client = await Client(ApiScopes.StackRead, ApiScopes.TriagePropose, ApiScopes.BriefsRead, ApiScopes.ActionsRead);

        var stack = await client.GetFromJsonAsync<List<ApiProduct>>("/api/v1/stack");
        stack.Should().Contain(p => p.Name == "api-more GitLab" && p.Notes == "Two hosts.", "the token reads up to AMBER, so the notes come");

        var proposal = new { title = "api-more alert", url = "https://siem.example.org/alerts/api-more" };
        var created = await client.PostAsJsonAsync("/api/v1/triage", proposal);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        // The triage page's address, not a relative one that resolves under /api/v1.
        created.Headers.Location!.OriginalString.Should().MatchRegex("^/triage\\?at=[0-9a-f-]{36}$");
        (await client.PostAsJsonAsync("/api/v1/triage", proposal)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PostAsJsonAsync("/api/v1/triage", new { title = "x" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await client.GetAsync("/api/v1/briefs/1999-01-04")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.GetAsync("/api/v1/briefs/not-a-day")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.GetAsync("/api/v1/actions?view=mine")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.GetAsync("/api/v1/actions?view=overdue")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task TheServersSecretsAndPoster_RoundTripAndPost()
    {
        var secrets = app.Services.GetRequiredService<ISecretProtector>();
        var kept = secrets.Protect("s3cret");
        kept.Should().NotContain("s3cret");
        secrets.Unprotect(kept).Should().Be("s3cret");
        secrets.Unprotect("not protected").Should().BeNull();

        var handler = new Answering(HttpStatusCode.Accepted);
        var poster = new Tide.Web.Api.HttpWebhookPoster(new OneClient(handler));
        (await poster.PostAsync("https://hooks.example.org/x", "{}", new Dictionary<string, string> { ["X-Tide-Event"] = "ping" })).Should().Be((202, (string?)null));
        handler.Last!.Headers.GetValues("X-Tide-Event").Should().Equal("ping");
        handler.Status = HttpStatusCode.Redirect;
        (await poster.PostAsync("https://hooks.example.org/x", "{}", new Dictionary<string, string>())).Error.Should().Contain("302");
    }

    private sealed class Answering(HttpStatusCode status) : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = status;
        public HttpRequestMessage? Last { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Last = request;
            return Task.FromResult(new HttpResponseMessage(Status));
        }
    }

    private sealed class OneClient(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}

/// <summary>Webhook upkeep: switching off and on, removing, testing, and a secret that can't be read.</summary>
public class WebhookUpkeepTests : IDisposable
{
    private readonly TestDb _db = new();

    public WebhookUpkeepTests() => _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task AWebhook_IsTested_SwitchedOffAndOn_AndRemoved()
    {
        var hook = await _db.Webhooks().AddAsync("https://hooks.example.org/tide", [WebhookService.BriefSent]);
        await FluentActions.Invoking(() => _db.Webhooks().AddAsync("https://hooks.example.org/tide", [])).Should().ThrowAsync<ArgumentException>();

        (await _db.Webhooks().TestAsync(hook.Id)).Should().Be((200, (string?)null));
        _db.Posts.Posted.Single().Headers["X-Tide-Event"].Should().Be("ping");

        await _db.Webhooks().SetEnabledAsync(hook.Id, false);
        (await _db.Webhooks().ListAsync()).Single().Enabled.Should().BeFalse();
        await _db.Webhooks().SetEnabledAsync(hook.Id, true);
        await _db.Webhooks().RemoveAsync(hook.Id);
        (await _db.Webhooks().ListAsync()).Should().BeEmpty();
        await FluentActions.Invoking(() => _db.Webhooks().RemoveAsync(hook.Id)).Should().ThrowAsync<KeyNotFoundException>();

        _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst };
        await FluentActions.Invoking(() => _db.Webhooks().ListAsync()).Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task ASecretThatCantBeRead_IsReported_NotSent()
    {
        var hook = await _db.Webhooks().AddAsync("https://hooks.example.org/tide", [WebhookService.ActionRaised]);
        await using (var ctx = _db.Context())
        {
            (await ctx.Webhooks.SingleAsync()).Secret = "garbled";
            await ctx.SaveChangesAsync();
        }

        var (status, error) = await _db.Webhooks().TestAsync(hook.Id);

        status.Should().BeNull();
        error.Should().Contain("can't be read");
        _db.Posts.Posted.Should().BeEmpty();
    }
}

/// <summary>Webhook delivery over time: a backlog drains, a brief still going isn't lost, and a run isn't audited.</summary>
public class WebhookDeliveryTests : IDisposable
{
    private TestDb _db = new();

    public WebhookDeliveryTests() => _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };

    public void Dispose() => _db.Dispose();

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task ABacklog_DrainsOldestFirst_AHundredARun_EachEventOnce(string provider)
    {
        _db = _db.On(provider);
        await _db.Webhooks().AddAsync("https://xsoar.example.org/hooks/tide", [WebhookService.ActionRaised]);
        var team = await _db.Teams().AddAsync("Network", null, null);
        var numbers = new List<int>();
        for (var i = 0; i < 250; i++) numbers.Add(await _db.Actions().CreateAsync(new ActionForm { Title = $"Block host {i}", TeamId = team }));
        var later = new ShiftedClock(TimeSpan.FromMinutes(1));

        var runs = new List<int>();
        for (var i = 0; i < 4; i++) runs.Add(await _db.Webhooks(later).DeliverDueAsync());

        runs.Should().Equal(100, 100, 50, 0);
        var ids = _db.Posts.Posted.Select(p => p.Headers["X-Tide-Event-Id"]).ToList();
        ids.Should().OnlyHaveUniqueItems().And.HaveCount(250);
        _db.Posts.Posted.Select(p => JsonDocument.Parse(p.Body).RootElement.GetProperty("data").GetProperty("number").GetInt32())
            .Should().Equal(numbers, "oldest first");
        JsonDocument.Parse(_db.Posts.Posted[0].Body).RootElement.GetProperty("id").GetString().Should().Be(ids[0]);
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task AnEvent_IsPostedOnceItsThirtySecondsOld_AndTheWatermarkWaitsForIt(string provider)
    {
        _db = _db.On(provider);
        await _db.Webhooks().AddAsync("https://xsoar.example.org/hooks/tide", [WebhookService.ActionRaised]);
        var team = await _db.Teams().AddAsync("Network", null, null);
        var number = await _db.Actions().CreateAsync(new ActionForm { Title = "Block host", TeamId = team });
        var raised = (await _db.Actions().GetAsync(number))!.Action.CreatedAtUtc;
        var clock = new TestClock(raised + WebhookService.SettleDelay - TimeSpan.FromSeconds(1));

        (await _db.Webhooks(clock).DeliverDueAsync()).Should().Be(0, "a save that recent may still be committing");
        await using (var ctx = _db.Context())
            (await ctx.Webhooks.AsNoTracking().SingleAsync()).SentUpToUtc.Should().BeBefore(raised, "the watermark stops short of it");

        clock.Now = raised + WebhookService.SettleDelay;
        (await _db.Webhooks(clock).DeliverDueAsync()).Should().Be(1);
        (await _db.Webhooks(clock).DeliverDueAsync()).Should().Be(0);
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task AFailure_StopsTheRun_AndTheNextPicksUpWhereItStopped(string provider)
    {
        _db = _db.On(provider);
        await _db.Webhooks().AddAsync("https://xsoar.example.org/hooks/tide", [WebhookService.ActionRaised]);
        var team = await _db.Teams().AddAsync("Network", null, null);
        for (var i = 0; i < 3; i++) await _db.Actions().CreateAsync(new ActionForm { Title = $"Block host {i}", TeamId = team });
        var later = new ShiftedClock(TimeSpan.FromMinutes(1));

        _db.Posts.Status = 503;
        (await _db.Webhooks(later).DeliverDueAsync()).Should().Be(1);
        _db.Posts.Status = 200;
        (await _db.Webhooks(later).DeliverDueAsync()).Should().Be(3);

        var ids = _db.Posts.Posted.Select(p => p.Headers["X-Tide-Event-Id"]).ToList();
        ids[1].Should().Be(ids[0], "the failed event is posted again under the same id");
        ids.Skip(1).Should().OnlyHaveUniqueItems();
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task ABriefStillGoing_IsPostedOnceItsSent(string provider)
    {
        _db = _db.On(provider);
        await _db.Webhooks().AddAsync("https://xsoar.example.org/hooks/tide", [WebhookService.BriefSent]);
        var send = new BriefSend
        {
            Edition = Edition.Team, Status = SendStatus.Sending, Ceiling = TlpLevel.Green, Marking = TlpLevel.Green, Subject = "Daily brief",
            Recipients = "soc@example.org", RecipientCount = 1, SentAtUtc = DateTimeOffset.UtcNow, SentById = "x", SentByName = "X",
            EmailHtml = "<p>brief</p>", WebHtml = "<p>brief</p>",
        };
        await using (var ctx = _db.Context())
        {
            var brief = new Brief { BriefDate = _db.Clock.Today, AnalystName = "Robin" };
            ctx.Briefs.Add(brief);
            send.BriefId = brief.Id;
            send.BriefDate = brief.BriefDate;
            ctx.BriefSends.Add(send);
            await ctx.SaveChangesAsync();
        }
        var later = new ShiftedClock(WebhookService.SettleDelay);

        (await _db.Webhooks(later).DeliverDueAsync()).Should().Be(0, "it's still with the relay");
        await using (var ctx = _db.Context())
            await ctx.BriefSends.Where(s => s.Id == send.Id).ExecuteUpdateAsync(u => u.SetProperty(s => s.Status, SendStatus.Sent));
        (await _db.Webhooks(later).DeliverDueAsync()).Should().Be(1);
        (await _db.Webhooks(later).DeliverDueAsync()).Should().Be(0);

        _db.Posts.Posted.Single().Headers["X-Tide-Event-Id"].Should().Be($"{WebhookService.BriefSent}:{send.Id}");
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task ASendMarkedSent_AfterItMayHaveGone_IsPostedWhenItsMarked(string provider)
    {
        _db = _db.On(provider);
        await _db.Webhooks().AddAsync("https://xsoar.example.org/hooks/tide", [WebhookService.BriefSent]);
        var send = new BriefSend
        {
            Edition = Edition.Team, Status = SendStatus.Sending, Ceiling = TlpLevel.Green, Marking = TlpLevel.Green, Subject = "Daily brief",
            Recipients = "soc@example.org", RecipientCount = 1, SentAtUtc = DateTimeOffset.UtcNow.AddMinutes(-20), SentById = "x", SentByName = "X",
            EmailHtml = "<p>brief</p>", WebHtml = "<p>brief</p>",
        };
        await using (var ctx = _db.Context())
        {
            var brief = new Brief { BriefDate = _db.Clock.Today, AnalystName = "Robin" };
            ctx.Briefs.Add(brief);
            send.BriefId = brief.Id;
            send.BriefDate = brief.BriefDate;
            ctx.BriefSends.Add(send);
            await ctx.SaveChangesAsync();
        }
        (await _db.Webhooks().DeliverDueAsync()).Should().Be(0, "left sending past the in-flight time, it may or may not have gone");

        await _db.Distribution().SettleAsync(send.Id, went: true);   // someone asked a recipient: it went

        var later = new ShiftedClock(WebhookService.SettleDelay);
        (await _db.Webhooks(later).DeliverDueAsync()).Should().Be(1, "the watermark had passed its time, but not when it was marked");
        (await _db.Webhooks(later).DeliverDueAsync()).Should().Be(0);
        _db.Posts.Posted.Single().Headers["X-Tide-Event-Id"].Should().Be($"{WebhookService.BriefSent}:{send.Id}");
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task ARun_WritesNoAuditRow_AndTheSecretIsntInTheTrail(string provider)
    {
        _db = _db.On(provider);
        var added = await _db.Webhooks().AddAsync("https://xsoar.example.org/hooks/tide", [WebhookService.ActionRaised]);
        var team = await _db.Teams().AddAsync("Network", null, null);
        await _db.Actions().CreateAsync(new ActionForm { Title = "Patch the gateways", TeamId = team });

        for (var i = 1; i <= 3; i++) await _db.Webhooks(new ShiftedClock(TimeSpan.FromMinutes(i))).DeliverDueAsync();
        await _db.Webhooks().TestAsync(added.Id);

        _db.Posts.Posted.Should().HaveCount(2);
        (await _db.Webhooks().ListAsync()).Single().LastStatus.Should().Be(200);
        await using var ctx = _db.Context();
        var trail = await ctx.AuditLog.Where(e => e.EntityType == nameof(Webhook)).ToListAsync();
        trail.Should().ContainSingle().Which.Action.Should().Be("Added");
        trail[0].ChangesJson.Should().NotContain(added.Secret);
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task SwitchedBackOn_ItDoesntSendWhatWentOverdueWhileOff(string provider)
    {
        _db = _db.On(provider);
        var hook = await _db.Webhooks().AddAsync("https://xsoar.example.org/hooks/tide", [WebhookService.ActionOverdue]);
        var team = await _db.Teams().AddAsync("Network", null, null);
        await _db.Webhooks().DeliverDueAsync();
        await _db.Webhooks().SetEnabledAsync(hook.Id, false);
        await using (var ctx = _db.Context())
            await ctx.Webhooks.ExecuteUpdateAsync(u => u.SetProperty(w => w.OverdueSentFor, _db.Clock.Today.AddDays(-10)));
        await _db.Actions().CreateAsync(new ActionForm { Title = "Late while off", TeamId = team, DueDate = _db.Clock.Today.AddDays(-5) });

        await _db.Webhooks().SetEnabledAsync(hook.Id, true);
        await _db.Webhooks().DeliverDueAsync();

        _db.Posts.Posted.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task OverdueDays_AreEachPostedOnce(string provider)
    {
        _db = _db.On(provider);
        await _db.Webhooks().AddAsync("https://xsoar.example.org/hooks/tide", [WebhookService.ActionOverdue]);
        var team = await _db.Teams().AddAsync("Network", null, null);
        await using (var ctx = _db.Context())
            await ctx.Webhooks.ExecuteUpdateAsync(u => u.SetProperty(w => w.OverdueSentFor, _db.Clock.Today.AddDays(-4)));
        await _db.Actions().CreateAsync(new ActionForm { Title = "Late", TeamId = team, DueDate = _db.Clock.Today.AddDays(-3) });
        await _db.Actions().CreateAsync(new ActionForm { Title = "Later", TeamId = team, DueDate = _db.Clock.Today.AddDays(-1) });

        (await _db.Webhooks().DeliverDueAsync()).Should().Be(2);
        (await _db.Webhooks().DeliverDueAsync()).Should().Be(0);
    }
}
