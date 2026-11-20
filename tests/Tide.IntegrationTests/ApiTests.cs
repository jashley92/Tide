using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
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
        var json = JsonElement.Parse(body);
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
        var posts = _db.Posts.Posted.Select(p => (Event: p.Headers["X-Tide-Event"], Data: JsonElement.Parse(p.Body).GetProperty("data"))).ToList();
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
        var data = JsonElement.Parse(_db.Posts.Posted.Single().Body).GetProperty("data");
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
        JsonElement.Parse(openapi).GetProperty("paths").EnumerateObject().Select(p => p.Name).Should().Contain("/actions/{number}");

        await using var page = await ComponentHarness.RenderAsync<Tide.Web.Components.Pages.Admin.ApiAdmin>(app, "/admin/api");
        var html = await page.Html();
        html.Should().Contain("API access").And.Contain("actions:write").And.Contain("Issue the token").And.Contain("Add the webhook");
    }
}

/// <summary>The API access page when another administrator has already removed what it lists: it says so and carries
/// on, keeping a token or secret still on show (neither can be shown again).</summary>
public class ApiAdminStaleRowTests(TideApp app) : IClassFixture<TideApp>
{
    private static IReadOnlyList<Tide.Web.Services.ToastService.Toast> Toasts<T>(ComponentHarness.Rendered<T> r) =>
        r.Scope.ServiceProvider.GetRequiredService<Tide.Web.Services.ToastService>().Toasts;

    // The page as drawn now: a method called straight, not from a click, doesn't render it again on its own.
    private static async Task<string> Drawn<T>(ComponentHarness.Rendered<T> r) where T : Microsoft.AspNetCore.Components.ComponentBase
    {
        await r.Renderer.Dispatcher.InvokeAsync(() => typeof(Microsoft.AspNetCore.Components.ComponentBase)
            .GetMethod("StateHasChanged", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(r.Component, null));
        return await r.Html();
    }

    private async Task RemovedElsewhere(Guid hook)
    {
        await using var scope = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst);
        await scope.ServiceProvider.GetRequiredService<WebhookService>().RemoveAsync(hook);
    }

    // The confirm dialog's button, pressed on the renderer's thread: the action carries on in the flow that asked.
    private static async Task Confirmed<T>(ComponentHarness.Rendered<T> r, Func<T, Task> press, string asked, Func<T, Tide.Web.Components.Shared.ConfirmDialog> dialog)
    {
        await r.Press(press);
        (await r.HtmlOnce(asked)).Should().Contain(asked);
        await r.Renderer.Dispatcher.InvokeAsync(() => dialog(r.Component).Confirm());
    }

    [Fact]
    public async Task AWebhookRemovedBySomeoneElse_IsSaidInAToast_AndThePageAndItsSecretStay()
    {
        await using var r = await ComponentHarness.RenderAsync<Tide.Web.Components.Pages.Admin.ApiAdmin>(app, "/admin/api");
        const string url = "https://hooks.example.org/stale-row";
        typeof(Tide.Web.Components.Pages.Admin.ApiAdmin).GetField("_hookUrl", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(r.Component, url);
        await r.Click(c => c.AddHook());
        (await Drawn(r)).Should().Contain($"The signing secret for {url}");
        WebhookRow hook;
        await using (var scope = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst))
            hook = (await scope.ServiceProvider.GetRequiredService<WebhookService>().ListAsync()).Single(h => h.Url == url);
        await RemovedElsewhere(hook.Id);

        await r.Click(c => c.Test(hook));
        await r.Click(c => c.Enable(hook, false));
        await Confirmed(r, c => c.Remove(hook), "Remove the webhook?", c => c._confirm);
        (await r.Until(_ => Toasts(r).Count(t => t.Message == $"The webhook for {url} was already removed.") == 3))
            .Should().BeTrue(string.Join("; ", Toasts(r).Select(t => t.Message)));

        Toasts(r).Should().NotContain(t => t.IsError);
        var html = await Drawn(r);
        html.Should().Contain($"The signing secret for {url}", "the secret still on show isn't lost")
            .And.NotContain($"<td class=\"tide-data small\">{url}</td>", "the list is read again");
    }

    [Fact]
    public async Task ATokenGoneBeforeItsRevoked_IsSaidInAToast_AndThePageStays()
    {
        Guid id;
        await using (var scope = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst))
            id = (await scope.ServiceProvider.GetRequiredService<ApiTokenService>().IssueAsync("Stale row token", [ApiScopes.ActionsRead], TlpLevel.Green, 1)).Id;
        await using var r = await ComponentHarness.RenderAsync<Tide.Web.Components.Pages.Admin.ApiAdmin>(app, "/admin/api");
        ApiTokenRow token;
        await using (var scope = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst))
        {
            token = (await scope.ServiceProvider.GetRequiredService<ApiTokenService>().ListAsync()).Single(t => t.Id == id);
            // Tokens aren't deleted by the app; a restore or a hand-cleaned database can still take one away.
            var db = scope.ServiceProvider.GetRequiredService<Tide.Infrastructure.Persistence.AppDbContext>();
            await db.ApiTokens.Where(t => t.Id == id).ExecuteDeleteAsync();
        }

        await Confirmed(r, c => c.Revoke(token), "Revoke the token for Stale row token?", c => c._confirm);
        (await r.Until(_ => Toasts(r).Any(t => t.Message == "The token for Stale row token no longer exists."))).Should().BeTrue(string.Join("; ", Toasts(r).Select(t => t.Message)));
        (await Drawn(r)).Should().NotContain("Stale row token</strong>");
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
        var secrets = app.Services.GetRequiredService<Tide.Application.Abstractions.ISecretProtector>();
        var kept = secrets.Protect(WebhookService.SecretPurpose, "s3cret");
        kept.Should().NotContain("s3cret");
        secrets.Unprotect(WebhookService.SecretPurpose, kept).Should().Be("s3cret");
        secrets.Unprotect(WebhookService.SecretPurpose, "not protected").Should().BeNull();
        secrets.Unprotect("Tide.Secrets.NvdApiKey.v1", kept).Should().BeNull("one purpose never opens another's");

        var handler = new Answering(HttpStatusCode.Accepted);
        var poster = new Tide.Web.Api.HttpWebhookPoster(new OneClient(handler), new StaticOptionsMonitor<WebhookOptions>(new WebhookOptions()));
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
        _db.Posts.Posted.Select(p => JsonElement.Parse(p.Body).GetProperty("data").GetProperty("number").GetInt32())
            .Should().Equal(numbers, "oldest first");
        JsonElement.Parse(_db.Posts.Posted[0].Body).GetProperty("id").GetString().Should().Be(ids[0]);
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task ABacklog_GoesOutOverTheRuns_OldestFirst_NoneSkippedOrSentTwice_EvenWhereARunsLimitFallsInsideOneMoment(string provider)
    {
        _db = _db.On(provider);
        await _db.Webhooks().AddAsync("https://xsoar.example.org/hooks/tide", [WebhookService.ActionRaised, WebhookService.BriefSent]);
        var team = await _db.Teams().AddAsync("Network", null, null);
        DateTimeOffset from;
        await using (var db = _db.Context()) from = (await db.Webhooks.AsNoTracking().SingleAsync()).SentUpToUtc;

        // 250 actions raised after the watermark. 96–110 share one moment, across the first run's limit of 100, and
        // 205–215 another, across the second's; a brief sent at the first moment goes with it.
        DateTimeOffset At(int n) => from.AddSeconds(1) + TimeSpan.FromMilliseconds(n is >= 96 and <= 110 ? 96 : n is >= 205 and <= 215 ? 205 : n);
        await using (var db = _db.Context())
        {
            for (var n = 1; n <= 250; n++) db.Actions.Add(new ActionItem { Number = n, Title = $"Action {n}", TeamId = team });
            var brief = new Brief { BriefDate = _db.Clock.Today, AnalystName = "Robin" };
            db.Briefs.Add(brief);
            db.BriefSends.Add(new BriefSend { BriefId = brief.Id, BriefDate = brief.BriefDate, Edition = Edition.Team, Status = SendStatus.Sent, SentAtUtc = At(100), EmailHtml = "team", WebHtml = "team" });
            await db.SaveChangesAsync();
        }
        await using (var db = _db.Context())
            for (var n = 1; n <= 250; n++)
            {
                var at = At(n);
                await db.Actions.Where(a => a.Number == n).ExecuteUpdateAsync(u => u.SetProperty(a => a.CreatedAtUtc, at));
            }

        var later = new ShiftedClock(TimeSpan.FromMinutes(5));
        var runs = new List<int>();
        for (var i = 0; i < 4; i++) runs.Add(await _db.Webhooks(later).DeliverDueAsync());

        runs.Should().Equal([111, 105, 35, 0], "a run stops at 100 only between moments: the first at 110 and the brief, the second at 215");
        var sent = _db.Posts.Posted.Select(p => JsonElement.Parse(p.Body))
            .Select(j => j.GetProperty("event").GetString() == WebhookService.BriefSent ? "brief" : j.GetProperty("data").GetProperty("label").GetString())
            .ToList();
        sent.Should().Equal(Enumerable.Range(1, 110).Select(n => $"A-{n}").Append("brief").Concat(Enumerable.Range(111, 140).Select(n => $"A-{n}")),
            "each goes once, oldest first, and those stamped at one moment in the order they were raised");
        await using (var db = _db.Context())
            (await db.Webhooks.AsNoTracking().SingleAsync()).SentUpToUtc.Should().BeCloseTo(later.GetUtcNow() - WebhookService.SettleDelay, TimeSpan.FromSeconds(5),
                "with the backlog sent, the watermark moves on to the run's time");
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task AFailureInsideOneMoment_TheNextRunGoesOnFromThePostThatFailed_SkippingNoneAndSendingNoneTwice(string provider)
    {
        _db = _db.On(provider);
        await _db.Webhooks().AddAsync("https://xsoar.example.org/hooks/tide", [WebhookService.ActionRaised]);
        var team = await _db.Teams().AddAsync("Network", null, null);
        DateTimeOffset from;
        await using (var db = _db.Context()) from = (await db.Webhooks.AsNoTracking().SingleAsync()).SentUpToUtc;
        await using (var db = _db.Context())
        {
            for (var n = 1; n <= 105; n++) db.Actions.Add(new ActionItem { Number = n, Title = $"Action {n}", TeamId = team });
            await db.SaveChangesAsync();
        }
        // 96–105 share one moment, across the limit of 100.
        await using (var db = _db.Context())
            for (var n = 1; n <= 105; n++)
            {
                var at = from.AddSeconds(1) + TimeSpan.FromMilliseconds(Math.Min(n, 96));
                await db.Actions.Where(a => a.Number == n).ExecuteUpdateAsync(u => u.SetProperty(a => a.CreatedAtUtc, at));
            }
        var delivered = new List<int>();
        _db.Posts.During = () =>
        {
            var number = JsonElement.Parse(_db.Posts.Posted[^1].Body).GetProperty("data").GetProperty("number").GetInt32();
            _db.Posts.Status = _db.Posts.Posted.Count == 99 ? 503 : 200;   // the fourth of that moment's ten
            if (_db.Posts.Status == 200) delivered.Add(number);
            return Task.CompletedTask;
        };
        var later = new ShiftedClock(TimeSpan.FromMinutes(5));

        (await _db.Webhooks(later).DeliverDueAsync()).Should().Be(99);
        (await _db.Webhooks(later).DeliverDueAsync()).Should().Be(7, "the moment's cursor kept the three that went before the failure");
        (await _db.Webhooks(later).DeliverDueAsync()).Should().Be(0);

        delivered.Distinct().Should().Equal(Enumerable.Range(1, 105), "none is skipped");
        delivered.Should().HaveCount(105, "none goes twice");
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task TwoHooks_AtDifferentWatermarks_EachGetsTheBriefSendsAfterItsOwn(string provider)
    {
        _db = _db.On(provider);
        var behind = await _db.Webhooks().AddAsync("https://behind.example.org/hooks/tide", [WebhookService.BriefSent]);
        var ahead = await _db.Webhooks().AddAsync("https://ahead.example.org/hooks/tide", [WebhookService.BriefSent]);
        var start = DateTimeOffset.UtcNow.AddMinutes(-10);
        BriefSend Send(string subject, DateTimeOffset at) => new()
        {
            Edition = Edition.Team, Status = SendStatus.Sent, Ceiling = TlpLevel.Green, Marking = TlpLevel.Green, Subject = subject,
            Recipients = "soc@example.org", RecipientCount = 1, SentAtUtc = at, SentById = "x", SentByName = "X", EmailHtml = "<p>brief</p>", WebHtml = "<p>brief</p>",
        };
        await using (var ctx = _db.Context())
        {
            var brief = new Brief { BriefDate = _db.Clock.Today, AnalystName = "Robin" };
            ctx.Briefs.Add(brief);
            foreach (var send in new[] { Send("Before both", start.AddMinutes(1)), Send("Between", start.AddMinutes(3)), Send("After both", start.AddMinutes(5)) })
            {
                send.BriefId = brief.Id;
                send.BriefDate = brief.BriefDate;
                ctx.BriefSends.Add(send);
            }
            await ctx.SaveChangesAsync();
        }
        // One hook's watermark is before "Between", the other's after it.
        await using (var ctx = _db.Context())
        {
            var (old, recent) = (start.AddMinutes(2), start.AddMinutes(4));
            await ctx.Webhooks.Where(w => w.Id == behind.Id).ExecuteUpdateAsync(u => u.SetProperty(w => w.SentUpToUtc, old));
            await ctx.Webhooks.Where(w => w.Id == ahead.Id).ExecuteUpdateAsync(u => u.SetProperty(w => w.SentUpToUtc, recent));
        }

        await _db.Webhooks().DeliverDueAsync();
        await _db.Webhooks().DeliverDueAsync();

        string[] To(string host) => [.. _db.Posts.Posted.Where(p => p.Url.Contains(host, StringComparison.Ordinal))
            .Select(p => JsonElement.Parse(p.Body).GetProperty("data").GetProperty("subject").GetString()!)];
        To("behind.example.org").Should().Equal(["Between", "After both"], "the hook further behind still gets what's between the two watermarks");
        To("ahead.example.org").Should().Equal(["After both"], "nothing from before its own watermark");
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

    // A receiver that takes 14 seconds a post, just under the poster's 15-second limit: the run's clock moves on by that
    // much for each post to an address on that host.
    private void SlowAt(string host, TestClock clock) => _db.Posts.During = () =>
    {
        if (_db.Posts.Posted[^1].Url.Contains(host, StringComparison.Ordinal)) clock.Now += TimeSpan.FromSeconds(14);
        return Task.CompletedTask;
    };

    private static int Number(string body) => JsonElement.Parse(body).GetProperty("data").GetProperty("number").GetInt32();

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task ASlowReceiver_StopsAtTheRunsTimeBudget_EvenInsideAMoment_AndTheNextRunGoesOn_NoneSkippedOrSentTwice(string provider)
    {
        _db = _db.On(provider);
        await _db.Webhooks().AddAsync("https://slow.example.org/hooks/tide", [WebhookService.ActionRaised]);
        var team = await _db.Teams().AddAsync("Network", null, null);
        DateTimeOffset from;
        await using (var db = _db.Context()) from = (await db.Webhooks.AsNoTracking().SingleAsync()).SentUpToUtc;
        await using (var db = _db.Context())
        {
            for (var n = 1; n <= 50; n++) db.Actions.Add(new ActionItem { Number = n, Title = $"Action {n}", TeamId = team });
            await db.SaveChangesAsync();
        }
        // 20–25 share one moment, across where the budget runs out (after the 22nd post at 14 seconds each).
        await using (var db = _db.Context())
            for (var n = 1; n <= 50; n++)
            {
                var at = from.AddSeconds(1) + TimeSpan.FromMilliseconds(n is >= 20 and <= 25 ? 20 : n);
                await db.Actions.Where(a => a.Number == n).ExecuteUpdateAsync(u => u.SetProperty(a => a.CreatedAtUtc, at));
            }
        var clock = new TestClock(DateTimeOffset.UtcNow.AddMinutes(5));
        SlowAt("slow.example.org", clock);

        var runs = new List<int>();
        for (var i = 0; i < 4; i++)
        {
            var started = clock.Now;
            runs.Add(await _db.Webhooks(clock).DeliverDueAsync());
            (clock.Now - started).Should().BeLessThan(WebhookService.RunBudget + TimeSpan.FromSeconds(15),
                "a run keeps to its budget, overrunning by one post at most");
            clock.Now += TimeSpan.FromMinutes(1);
        }

        runs.Should().Equal([22, 22, 6, 0], "the first run's budget ran out inside the moment 20–25, and the next went on from 23");
        _db.Posts.Posted.Select(p => Number(p.Body)).Should().Equal(Enumerable.Range(1, 50), "each once, oldest first");
    }

    // Actions raised in one save: every one stamped at the same moment.
    private async Task<List<int>> RaisedAtOneMoment(int count, DateOnly? due = null)
    {
        var team = await _db.Teams().AddAsync("Network", null, null);
        DateTimeOffset from;
        await using (var db = _db.Context()) from = (await db.Webhooks.AsNoTracking().SingleAsync()).SentUpToUtc;
        await using (var db = _db.Context())
        {
            for (var n = 1; n <= count; n++) db.Actions.Add(new ActionItem { Number = n, Title = $"Action {n}", TeamId = team, DueDate = due });
            await db.SaveChangesAsync();
        }
        var at = from.AddSeconds(1);
        await using (var db = _db.Context())
            await db.Actions.ExecuteUpdateAsync(u => u.SetProperty(a => a.CreatedAtUtc, at));
        return [.. Enumerable.Range(1, count)];
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task ALargeOverdueDay_ToASlowReceiver_KeepsToTheBudget_AndGoesOnInsideTheDay_NoneSkippedOrSentTwice(string provider)
    {
        _db = _db.On(provider);
        await _db.Webhooks().AddAsync("https://slow.example.org/hooks/tide", [WebhookService.ActionOverdue]);
        var numbers = await RaisedAtOneMoment(150, _db.Clock.Today.AddDays(-1));
        var clock = new TestClock(DateTimeOffset.UtcNow.AddMinutes(5));
        SlowAt("slow.example.org", clock);

        var runs = new List<int>();
        for (var i = 0; i < 8; i++)
        {
            var started = clock.Now;
            runs.Add(await _db.Webhooks(clock).DeliverDueAsync());
            (clock.Now - started).Should().BeLessThan(WebhookService.RunBudget + TimeSpan.FromSeconds(15), "the day's 150 don't hold one run past its budget");
            clock.Now += TimeSpan.FromMinutes(1);
        }

        runs.Should().Equal([22, 22, 22, 22, 22, 22, 18, 0]);
        _db.Posts.Posted.Select(p => p.Headers["X-Tide-Event-Id"]).Should().OnlyHaveUniqueItems();
        _db.Posts.Posted.Select(p => Number(p.Body)).Should().Equal(numbers, "each once, by number");
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task ALargeOverdueDay_GoesOutAHundredARun_GoingOnInsideTheDay(string provider)
    {
        _db = _db.On(provider);
        await _db.Webhooks().AddAsync("https://xsoar.example.org/hooks/tide", [WebhookService.ActionOverdue]);
        var numbers = await RaisedAtOneMoment(150, _db.Clock.Today.AddDays(-1));

        var runs = new List<int>();
        for (var i = 0; i < 3; i++) runs.Add(await _db.Webhooks().DeliverDueAsync());

        runs.Should().Equal(100, 50, 0);
        _db.Posts.Posted.Select(p => Number(p.Body)).Should().Equal(numbers, "each once, by number");
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task AFailure_OnTheLastPostBeforeTheBudgetRanOut_TheNextRunGoesOnFromIt_NoneSkippedOrSentTwice(string provider)
    {
        _db = _db.On(provider);
        await _db.Webhooks().AddAsync("https://slow.example.org/hooks/tide", [WebhookService.ActionRaised]);
        var numbers = await RaisedAtOneMoment(30);
        var clock = new TestClock(DateTimeOffset.UtcNow.AddMinutes(5));
        var delivered = new List<int>();
        _db.Posts.During = () =>
        {
            clock.Now += TimeSpan.FromSeconds(14);
            // The 22nd post, the last the budget allows, fails.
            _db.Posts.Status = _db.Posts.Posted.Count == 22 ? 503 : 200;
            if (_db.Posts.Status == 200) delivered.Add(Number(_db.Posts.Posted[^1].Body));
            return Task.CompletedTask;
        };

        (await _db.Webhooks(clock).DeliverDueAsync()).Should().Be(22);
        clock.Now += TimeSpan.FromMinutes(1);
        (await _db.Webhooks(clock).DeliverDueAsync()).Should().Be(9, "from the one that failed to the moment's end");
        clock.Now += TimeSpan.FromMinutes(1);
        (await _db.Webhooks(clock).DeliverDueAsync()).Should().Be(0);

        delivered.Should().Equal(numbers, "each once, in order");
    }

    private async Task<DateTimeOffset> TheMoment()
    {
        await using var db = _db.Context();
        return (await db.Actions.AsNoTracking().OrderBy(a => a.Number).FirstAsync()).CreatedAtUtc;
    }

    private static string EventOf(string body)
    {
        var json = JsonElement.Parse(body);
        return json.GetProperty("event").GetString() == WebhookService.BriefSent ? json.GetProperty("id").GetString()! : $"A-{Number(body)}";
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task AMomentOfActionsAndBriefSends_StoppedAmongTheSends_TheNextRunGoesOnFromTheNextSend(string provider)
    {
        _db = _db.On(provider);
        await _db.Webhooks().AddAsync("https://slow.example.org/hooks/tide", [WebhookService.ActionRaised, WebhookService.BriefSent]);
        await RaisedAtOneMoment(21);
        var at = await TheMoment();
        var sendIds = new List<Guid>();
        await using (var ctx = _db.Context())
        {
            var brief = new Brief { BriefDate = _db.Clock.Today, AnalystName = "Robin" };
            ctx.Briefs.Add(brief);
            for (var i = 0; i < 3; i++)
            {
                var send = new BriefSend
                {
                    BriefId = brief.Id, BriefDate = brief.BriefDate, Edition = Edition.Team, Status = SendStatus.Sent, Ceiling = TlpLevel.Green,
                    Marking = TlpLevel.Green, Subject = $"Brief {i}", Recipients = "soc@example.org", RecipientCount = 1, SentAtUtc = at, SentById = "x",
                    SentByName = "X", EmailHtml = "<p>brief</p>", WebHtml = "<p>brief</p>",
                };
                ctx.BriefSends.Add(send);
                sendIds.Add(send.Id);
            }
            await ctx.SaveChangesAsync();
        }
        var clock = new TestClock(DateTimeOffset.UtcNow.AddMinutes(5));
        SlowAt("slow.example.org", clock);

        var runs = new List<int>();
        for (var i = 0; i < 3; i++)
        {
            runs.Add(await _db.Webhooks(clock).DeliverDueAsync());
            clock.Now += TimeSpan.FromMinutes(1);
        }

        runs.Should().Equal([22, 2, 0], "the budget ran out after the moment's 21 actions and its first send");
        _db.Posts.Posted.Select(p => EventOf(p.Body)).Should().Equal(Enumerable.Range(1, 21).Select(n => $"A-{n}")
            .Concat(sendIds.Select(id => id.ToString("N")).Order(StringComparer.Ordinal).Select(n => $"{WebhookService.BriefSent}:{Guid.ParseExact(n, "N")}")),
            "each once: the actions by number, then the sends in their fixed order");
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task AnOverdueFailure_PartwayThroughADay_TheNextRunGoesOnFromIt_NoneSkippedOrSentTwice(string provider)
    {
        _db = _db.On(provider);
        await _db.Webhooks().AddAsync("https://xsoar.example.org/hooks/tide", [WebhookService.ActionOverdue]);
        var numbers = await RaisedAtOneMoment(30, _db.Clock.Today.AddDays(-1));
        var delivered = new List<int>();
        _db.Posts.During = () =>
        {
            _db.Posts.Status = _db.Posts.Posted.Count == 10 ? 503 : 200;   // the day's tenth
            if (_db.Posts.Status == 200) delivered.Add(Number(_db.Posts.Posted[^1].Body));
            return Task.CompletedTask;
        };

        (await _db.Webhooks().DeliverDueAsync()).Should().Be(10);
        (await _db.Webhooks().DeliverDueAsync()).Should().Be(21, "from the tenth, the one that failed");
        (await _db.Webhooks().DeliverDueAsync()).Should().Be(0);

        delivered.Should().Equal(numbers, "each once, by number");
    }

    [Fact]
    public async Task SwitchingAWebhookBackOn_ClearsItsCursors()
    {
        var hook = await _db.Webhooks().AddAsync("https://xsoar.example.org/hooks/tide", [WebhookService.ActionRaised, WebhookService.ActionOverdue]);
        await using (var ctx = _db.Context())
            await ctx.Webhooks.ExecuteUpdateAsync(u => u.SetProperty(w => w.MomentCursor, "638000000000000000|A0000000007").SetProperty(w => w.OverdueCursor, "2026-11-18|7"));

        await _db.Webhooks().SetEnabledAsync(hook.Id, false);
        await _db.Webhooks().SetEnabledAsync(hook.Id, true);

        await using (var ctx = _db.Context())
        {
            var row = await ctx.Webhooks.AsNoTracking().SingleAsync();
            row.MomentCursor.Should().BeNull("it starts from now: a cursor into what came before means nothing");
            row.OverdueCursor.Should().BeNull();
        }
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task AMomentCursor_ThatDoesntRead_SendsTheMomentFromItsStart_SkippingNone(string provider)
    {
        _db = _db.On(provider);
        await _db.Webhooks().AddAsync("https://xsoar.example.org/hooks/tide", [WebhookService.ActionRaised]);
        var numbers = await RaisedAtOneMoment(5);
        var ticks = (await TheMoment()).UtcTicks;
        async Task Cursor(string place)
        {
            await using var ctx = _db.Context();
            await ctx.Webhooks.ExecuteUpdateAsync(u => u.SetProperty(w => w.MomentCursor, $"{ticks}|{place}"));
        }
        var later = new ShiftedClock(TimeSpan.FromMinutes(5));

        await Cursor("garbage");
        (await _db.Webhooks(later).DeliverDueAsync()).Should().Be(5, "a place it can't read would sort after the moment's events and skip them all");
        _db.Posts.Posted.Select(p => Number(p.Body)).Should().Equal(numbers);
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task AMomentCursor_GoesOnFromTheEventAfterIt(string provider)
    {
        _db = _db.On(provider);
        await _db.Webhooks().AddAsync("https://xsoar.example.org/hooks/tide", [WebhookService.ActionRaised]);
        await RaisedAtOneMoment(5);
        var ticks = (await TheMoment()).UtcTicks;
        await using (var ctx = _db.Context())
            await ctx.Webhooks.ExecuteUpdateAsync(u => u.SetProperty(w => w.MomentCursor, $"{ticks}|A0000000002"));

        (await _db.Webhooks(new ShiftedClock(TimeSpan.FromMinutes(5))).DeliverDueAsync()).Should().Be(3);
        _db.Posts.Posted.Select(p => Number(p.Body)).Should().Equal(3, 4, 5);
    }

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task ASlowReceiver_DoesntHoldUpAnotherHook_ItStillGetsItsPostsInTheSameRun(string provider)
    {
        _db = _db.On(provider);
        await _db.Webhooks().AddAsync("https://slow.example.org/hooks/tide", [WebhookService.ActionRaised]);
        await _db.Webhooks().AddAsync("https://quick.example.org/hooks/tide", [WebhookService.ActionRaised]);
        var team = await _db.Teams().AddAsync("Network", null, null);
        for (var i = 0; i < 50; i++) await _db.Actions().CreateAsync(new ActionForm { Title = $"Block host {i}", TeamId = team });
        var clock = new TestClock(DateTimeOffset.UtcNow.AddMinutes(5));
        SlowAt("slow.example.org", clock);
        int[] To(string host) => [.. _db.Posts.Posted.Where(p => p.Url.Contains(host, StringComparison.Ordinal)).Select(p => Number(p.Body))];

        var started = clock.Now;
        await _db.Webhooks(clock).DeliverDueAsync();

        To("quick.example.org").Should().HaveCount(50, "the slow hook had only its share of the run");
        To("slow.example.org").Should().NotBeEmpty().And.HaveCountLessThan(50, "it stopped at the budget");
        (clock.Now - started).Should().BeLessThan(WebhookService.RunBudget + TimeSpan.FromSeconds(15));

        for (var i = 0; i < 6; i++)
        {
            clock.Now += TimeSpan.FromMinutes(1);
            await _db.Webhooks(clock).DeliverDueAsync();
        }
        To("slow.example.org").Should().Equal(To("quick.example.org"), "the slow hook gets every one in the end, each once, in the same order");
        To("quick.example.org").Should().OnlyHaveUniqueItems();
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
