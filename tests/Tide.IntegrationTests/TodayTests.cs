using System.Net;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Org;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Infrastructure.Persistence;
using Tide.Web.Components.Pages;

namespace Tide.IntegrationTests;

/// <summary>Today, the start page: today's brief as the analyst, action owner and reader each get it, and the other days
/// at /briefs/{day}. The sample data has today's brief with three articles, one of them TLP:AMBER.</summary>
public class TodayTests(TideApp app) : IClassFixture<TideApp>
{
    private DateOnly Today => app.Services.GetRequiredService<OrgClock>().CurrentBriefDate;

    private static int At(string html, string text)
    {
        var at = html.IndexOf(text, StringComparison.Ordinal);
        at.Should().BeGreaterThanOrEqualTo(0, $"the page should contain {text}");
        return at;
    }

    [Fact]
    public async Task AnAnalyst_GetsTodaysBrief_AsTheStartPage_WithEveryPart()
    {
        var html = await app.CreateClient().GetStringAsync("/");

        html.Should().Contain("<title>Today · TIDE</title>").And.Contain($"Today, {Today:dddd, MMMM d, yyyy}");
        // The step strip, the running order (draggable, with Move up and down, remove and Add article), the numbers'
        // home, the edition check and the meeting, then My actions, the rotation and what was written lately.
        html.Should().Contain("aria-label=\"Brief progress\"").And.Contain("aria-current=\"step\"");
        html.Should().Contain("id=\"running-order-h\"").And.Contain("draggable=\"true\"")
            .And.Contain("data-move=\"up\"").And.Contain("data-move=\"down\"").And.Contain("off this brief")
            .And.Contain("Add article").And.Contain("New article");
        html.Should().Contain("id=\"edition-check-h\"").And.Contain("id=\"meeting-h\"").And.Contain("Presenting analyst")
            .And.Contain("Title slide line (optional)").And.Contain("meets at");
        html.Should().Contain("id=\"my-actions-h\"").And.Contain("id=\"coming-up-h\"").And.Contain("id=\"recent-h\"");
        html.Should().NotContain("id=\"team-queue-h\"", "an analyst on no team has no team queue");
        // The preview of both editions stays on the brief's own address, linked from here; no tabs on Today.
        html.Should().Contain($"briefs/{Today:yyyy-MM-dd}?tab=preview").And.Contain($"briefs/{Today:yyyy-MM-dd}?tab=team")
            .And.Contain($"briefs/{Today:yyyy-MM-dd}?tab=company").And.NotContain("aria-label=\"Brief\"").And.NotContain("role=\"tabpanel\"");
        html.Should().Contain("Mark ready").And.Contain($"href=\"present/{Today:yyyy-MM-dd}\"").And.Contain("Export indicators");
    }

    [Fact]
    public async Task TheStepStrip_CountsTheFeedAndTheRunningOrder()
    {
        int newItems, onBrief, needWork;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            newItems = await db.FeedItems.CountAsync(i => i.Status == FeedItemStatus.New && !i.Source!.IsRetired);
            var brief = await Tide.Application.Briefs.BriefService.Load(db.Briefs.AsNoTracking()).SingleAsync(b => b.BriefDate == Today);
            onBrief = brief.Items.Count;
            needWork = brief.Items.Count(i => Tide.Application.Articles.ArticleChecks.For(i.Article!).Any(c => c.Blocking));
        }
        newItems.Should().BePositive();

        var html = await app.CreateClient().GetStringAsync("/");

        var strip = html[At(html, "aria-label=\"Brief progress\"")..At(html, "id=\"running-order-h\"")];
        strip.Should().Contain($"{newItems} new in the feed").And.Contain("href=\"triage\"");
        strip.Should().Contain(needWork > 0 ? $"{needWork} of {onBrief} need work" : $"{onBrief} of {onBrief} ready");
        strip.Should().Contain("Not marked ready").And.Contain("Meets ").And.Contain("2 editions");
        strip.Should().Contain("Mark the brief ready", "Order is the button that marks it ready");
    }

    [Fact]
    public async Task TheEditionCheck_LeavesTheAmberArticleOutOfTheCompanyWideEdition()
    {
        var html = await app.CreateClient().GetStringAsync("/");

        var check = html[At(html, "id=\"edition-check-h\"")..At(html, "id=\"meeting-h\"")];
        var team = check[At(check, "Team edition")..At(check, "Company-wide edition")];
        var company = check[At(check, "Company-wide edition")..];
        team.Should().Contain("3 of 3 articles").And.NotContain("The article is TLP:AMBER");
        company.Should().Contain("2 of 3 articles").And.Contain(">Left out<").And.Contain("The article is TLP:AMBER");
    }

    [Fact]
    public async Task TheEditionCheck_ListsOnlyTheExceptions_NotEveryArticleAgain()
    {
        var html = await app.CreateClient().GetStringAsync("/");

        var check = html[At(html, "id=\"edition-check-h\"")..At(html, "id=\"meeting-h\"")];
        var team = check[At(check, "Team edition")..At(check, "Company-wide edition")];
        var company = check[At(check, "Company-wide edition")..];
        team.Should().Contain("3 of 3 articles").And.Contain("All 3 articles go out whole.")
            .And.NotContain("href=\"articles/", "an article that goes out whole is named by the running order, not again here");
        company.Should().Contain("2 of 3 articles").And.NotContain("go out whole")
            .And.NotContain("Never in this edition", "the standing rule is the note under the check");
        System.Text.RegularExpressions.Regex.Count(company, "href=\"articles/")
            .Should().BePositive("the AMBER article is left out").And.BeLessThan(3, "only what is cut or warned about is listed");
        System.Text.RegularExpressions.Regex.Count(company, "is-included")
            .Should().BeLessThanOrEqualTo(System.Text.RegularExpressions.Regex.Count(company, "tide-ed-warn"), "an article that goes out whole is listed only with a warning");
        check.Should().Contain("never carries the numbers, indicators or actions");
    }

    [Fact]
    public async Task TheTriageStep_CountsTheNewItemsOnKev()
    {
        int expected;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var source = await db.Sources.FirstAsync(s => !s.IsRetired);
            db.CveRecords.Add(new CveRecord { CveId = "CVE-2026-7701", IsKev = true });
            db.FeedItems.Add(new FeedItem { SourceId = source.Id, Title = "Exploited edge device flaw", Url = "https://news.example.com/kev-step", UrlHash = "kev-step", FetchedAtUtc = DateTimeOffset.UtcNow, Cves = "CVE-2026-7701, CVE-2026-7702" });
            db.FeedItems.Add(new FeedItem { SourceId = source.Id, Title = "Same flaw, already dismissed", Url = "https://news.example.com/kev-step-2", UrlHash = "kev-step-2", FetchedAtUtc = DateTimeOffset.UtcNow, Cves = "CVE-2026-7701", Status = FeedItemStatus.Dismissed });
            await db.SaveChangesAsync();
            var kev = (await db.CveRecords.Where(c => c.IsKev).Select(c => c.CveId).ToListAsync()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            expected = (await db.FeedItems.Where(i => i.Status == FeedItemStatus.New && !i.Source!.IsRetired).ToListAsync()).Count(i => i.CveList.Any(kev.Contains));
        }
        expected.Should().BePositive();

        var html = await app.CreateClient().GetStringAsync("/");

        var strip = html[At(html, "aria-label=\"Brief progress\"")..At(html, "id=\"running-order-h\"")];
        strip.Should().MatchRegex($@"\d+ new in the feed &#xB7; {expected} on KEV<");

        // Another day's brief keeps the feed's count, as it always had, without the KEV count: that is today's work.
        var past = WorkingDays.Weekdays.Previous(WorkingDays.Weekdays.Previous(WorkingDays.Weekdays.Previous(WorkingDays.Weekdays.Previous(Today))));
        await using (var scope = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst))
            await scope.ServiceProvider.GetRequiredService<Tide.Application.Briefs.BriefService>().GetOrStartAsync(past);
        html = await app.CreateClient().GetStringAsync($"/briefs/{past:yyyy-MM-dd}");
        strip = html[At(html, "aria-label=\"Brief progress\"")..At(html, "id=\"running-order-h\"")];
        strip.Should().MatchRegex(@"\d+ new in the feed<").And.NotContain("on KEV");
    }

    [Fact]
    public async Task TodaysBriefAddress_RedirectsToToday_ButItsPreviewStays()
    {
        var client = app.CreateClient(new() { AllowAutoRedirect = false });

        var res = await client.GetAsync($"/briefs/{Today:yyyy-MM-dd}");
        res.StatusCode.Should().Be(HttpStatusCode.Redirect);
        new Uri(new Uri("http://localhost/"), res.Headers.Location!).AbsolutePath.Should().Be("/");

        foreach (var tab in new[] { "preview", "team", "company" })
        {
            var page = await client.GetAsync($"/briefs/{Today:yyyy-MM-dd}?tab={tab}");
            page.StatusCode.Should().Be(HttpStatusCode.OK, $"?tab={tab} stays on the brief's address");
            (await page.Content.ReadAsStringAsync()).Should().Contain("What the security teams see").And.Contain("What the company sees")
                .And.Contain("aria-label=\"Brief\"");
        }
    }

    [Fact]
    public async Task APastDay_KeepsItsAddress_WithTabs_AndASentOneIsReadOnly()
    {
        var sentDay = WorkingDays.Weekdays.Previous(WorkingDays.Weekdays.Previous(Today));
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var brief = new Brief { BriefDate = sentDay, AnalystId = "system", AnalystName = "Sample data", Status = BriefStatus.Sent, Intro = "Quiet." };
            brief.Items.Add(new BriefItem { ArticleId = (await db.Articles.FirstAsync()).Id, SortOrder = 0 });
            db.Briefs.Add(brief);
            await db.SaveChangesAsync();
        }
        var client = app.CreateClient(new() { AllowAutoRedirect = false });

        var res = await client.GetAsync($"/briefs/{sentDay:yyyy-MM-dd}");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await res.Content.ReadAsStringAsync();
        html.Should().Contain($"Brief for {sentDay:dddd, MMMM d, yyyy}").And.Contain("aria-label=\"Brief\"", "other days keep their tabs")
            .And.Contain("role=\"tabpanel\" id=\"brief-panel\"")
            .And.Contain("This brief has been sent").And.Contain("aria-label=\"Brief progress\"").And.Contain("id=\"edition-check-h\"");
        html.Should().NotContain("data-move=\"up\"").And.NotContain("draggable=\"true\"").And.NotContain("Add article")
            .And.NotContain("Mark ready").And.NotContain("Presenting analyst", "a sent brief's meeting is read, not edited");
        html.Should().Contain("Quiet.").And.Contain("href>Today's</a>", "Today's link goes to the start page");
        html.Should().NotContain("id=\"my-actions-h\"", "My actions and the rotation are Today's");

        var empty = await client.GetAsync($"/briefs/{WorkingDays.Weekdays.Previous(sentDay):yyyy-MM-dd}");
        (await empty.Content.ReadAsStringAsync()).Should().Contain("There&#x27;s no brief for this day yet.").And.Contain("Start the brief");
    }

    [Fact]
    public void MarkReady_AsksFirst_AboutWarningsAndMissingNumbers()
    {
        BriefPage.ReadyWarning(0, 0, false).Should().BeNull();
        BriefPage.ReadyWarning(2, 0, false).Should().Be("2 warnings on the running order.");
        BriefPage.ReadyWarning(1, 1, false).Should().Be("1 warning on the running order and 1 number not entered.");
        BriefPage.ReadyWarning(0, 3, false).Should().Be("3 numbers not entered.");
        BriefPage.ReadyWarning(2, 3, true).Should().BeNull("a blocking gap is refused with its own reason");
    }
}

public class ActionOwnerTodayTests(ActionOwnerApp app) : IClassFixture<ActionOwnerApp>
{
    [Fact]
    public async Task AnActionOwner_GetsTheirTeamsQueueFirst_AndTodaysBriefAsAReadOnlySummary()
    {
        var res = await app.CreateClient(new() { AllowAutoRedirect = false }).GetAsync("/");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = System.Net.WebUtility.HtmlDecode(await res.Content.ReadAsStringAsync());
        List<string> titles;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var today = scope.ServiceProvider.GetRequiredService<OrgClock>().CurrentBriefDate;
            var brief = await Tide.Application.Briefs.BriefService.Load(db.Briefs.AsNoTracking()).SingleAsync(b => b.BriefDate == today);
            titles = brief.Items.OrderBy(i => i.SortOrder).Select(i => i.Article!.Title).ToList();
        }

        // The queue leads (this sign-in is on no team, so it says so), then the brief: its state and its headlines.
        html.IndexOf("id=\"team-queue-h\"", StringComparison.Ordinal).Should()
            .BePositive().And.BeLessThan(html.IndexOf("id=\"todays-brief-h\"", StringComparison.Ordinal), "the queue comes first");
        html.Should().Contain("You're not on a team.").And.Contain("href=\"actions?view=myteams\"");
        var summary = html[html.IndexOf("id=\"todays-brief-h\"", StringComparison.Ordinal)..html.IndexOf("id=\"meeting-h\"", StringComparison.Ordinal)];
        summary.Should().Contain("Draft").And.Contain("?tab=preview\">Read it</a>");
        foreach (var t in titles) summary.Should().Contain($"<li>{t}</li>");

        // None of the producers' parts: the steps, the running order, the numbers, the edition check, My actions.
        html.Should().NotContain("aria-label=\"Brief progress\"").And.NotContain("id=\"running-order-h\"")
            .And.NotContain("id=\"edition-check-h\"").And.NotContain("id=\"my-actions-h\"");
        html.Should().NotContain("data-move=\"up\"").And.NotContain("draggable=\"true\"").And.NotContain("off this brief")
            .And.NotContain("Add article").And.NotContain("Mark ready").And.NotContain("Presenting analyst")
            .And.NotContain("href=\"triage\"", "they don't triage");
        html.Should().Contain("id=\"meeting-h\"").And.Contain("id=\"coming-up-h\"").And.Contain("id=\"recent-h\"");
    }
}

/// <summary>
/// Today's team queue, for whoever works actions: the open actions of their teams that are theirs or nobody's, theirs
/// first, with Take it. An action owner gets it first; an analyst on a team keeps the whole brief, with it beside.
/// </summary>
public class TeamQueueTodayTests(TideApp app) : IClassFixture<TideApp>
{
    private static readonly ComponentHarness.SignIn Owner = new("dev:queue-owner", "Queue Owner", "ActionOwner");
    private static readonly ComponentHarness.SignIn Robin = new("dev:queue-robin", "Robin", "Analyst");
    private static readonly ComponentHarness.SignIn Colleague = new("dev:queue-colleague", "Colleague", "ActionOwner");

    private static int At(string html, string text)
    {
        var at = html.IndexOf(text, StringComparison.Ordinal);
        at.Should().BeGreaterThanOrEqualTo(0, $"the page should contain {text}");
        return at;
    }

    // A team with the three of them on it, and its actions: an overdue unassigned one, the owner's, Robin's and the colleague's.
    private async Task<string> TeamWithActions(string name)
    {
        await using var scope = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst);
        var teams = scope.ServiceProvider.GetRequiredService<Tide.Application.Actions.TeamService>();
        var actions = scope.ServiceProvider.GetRequiredService<Tide.Application.Actions.ActionService>();
        var team = await teams.AddAsync(name, null, null);
        foreach (var who in new[] { Owner, Robin, Colleague }) await teams.AddMemberAsync(team, who.UserId, who.Name);
        var today = actions.Today;
        async Task Add(string title, ComponentHarness.SignIn? to, DateOnly? due, ActionKind kind = ActionKind.Block) =>
            await actions.CreateAsync(new Tide.Application.Actions.ActionForm
            {
                Title = $"{name}: {title}", Kind = kind, TeamId = team, AssigneeId = to?.UserId, AssigneeName = to?.Name, DueDate = due,
            });
        await Add("nobody's, overdue", null, today.AddDays(-3), ActionKind.IocSweep);
        await Add("the owner's", Owner, today.AddDays(5));
        await Add("Robin's", Robin, today.AddDays(2));
        await Add("the colleague's", Colleague, today.AddDays(-1));
        return name;
    }

    [Fact]
    public async Task AnActionOwner_GetsTheirTeamsQueueFirst_TheirsThenUnassigned_WithKindLatenessAndTakeIt()
    {
        var team = await TeamWithActions("Queue Team A");

        await using var r = await ComponentHarness.RenderAsync<Home>(app, "/", null, Owner);
        var html = System.Net.WebUtility.HtmlDecode(await r.Html());

        var queue = html[At(html, "id=\"team-queue-h\"")..At(html, "id=\"todays-brief-h\"")];
        At(queue, $"{team}: the owner's").Should().BeLessThan(At(queue, $"{team}: nobody's, overdue"), "theirs come first");
        queue.Should().NotContain($"{team}: the colleague's", "a colleague's is in hand").And.NotContain($"{team}: Robin's");
        queue.Should().Contain("IOC sweep · Queue Team A · Unassigned").And.Contain("<span class=\"tide-queue-late\">3 days late</span>")
            .And.Contain(">Take it</button>").And.MatchRegex(@"\d+ yours and \d+ unassigned\.","the other test in this class adds a team the owner is on too")
            .And.Contain("href=\"actions?view=myteams\">My teams</a>");
        html.Should().NotContain("aria-label=\"Brief progress\"").And.NotContain("id=\"edition-check-h\"");
        System.Text.RegularExpressions.Regex.Replace(queue, "<[^>]+>", "").Should().Contain("Unassigned · 3 days late", "a space before the dot, which never starts a line");
    }

    [Fact]
    public async Task AnAnalystOnATeam_KeepsTheWholeBrief_WithOneActionsCardBesideIt()
    {
        var team = await TeamWithActions("Queue Team B");

        await using var r = await ComponentHarness.RenderAsync<Home>(app, "/", null, Robin);
        var html = System.Net.WebUtility.HtmlDecode(await r.Html());

        html.Should().Contain("aria-label=\"Brief progress\"").And.Contain("id=\"running-order-h\"").And.Contain("Add article")
            .And.NotContain("id=\"todays-brief-h\"");
        At(html, "id=\"edition-check-h\"").Should().BeLessThan(At(html, "id=\"actions-card-h\""));
        html.Should().NotContain("id=\"my-actions-h\"").And.NotContain("id=\"team-queue-h\"", "one card, not the same rows listed twice");
        var card = html[At(html, "id=\"actions-card-h\"")..At(html, "id=\"coming-up-h\"")];
        card.Should().Contain("aria-pressed=\"true\">Mine").And.Contain(">Team, unassigned <span class=\"tide-count\">")
            .And.Contain($"{team}: Robin's").And.NotContain($"{team}: the owner's");
    }
}

public class ActionOwnerSendTests(ActionOwnerApp app) : IClassFixture<ActionOwnerApp>
{
    [Fact]
    public async Task AnActionOwner_GetsNoSendButton_AndCantPlanASend()
    {
        var html = await app.CreateClient().GetStringAsync("/");
        html.Should().NotContain("aria-label=\"Brief progress\"", "their Today has no step strip").And.NotContain("Send a test copy, then each edition")
            .And.NotContain("?tab=distribute\">Send<");

        var today = app.Services.GetRequiredService<OrgClock>().CurrentBriefDate;
        await using var r = await ComponentHarness.RenderAsync<Tide.Web.Components.Shared.DistributePanel>(app, "/", new() { ["Date"] = today },
            new ComponentHarness.SignIn("dev:owner", "Owner", "ActionOwner"));
        (await r.Html()).Should().NotContain("Send a test copy to me").And.NotContain("bi bi-send-check").And.Contain("Send log");
    }
}

public class ReaderTodayTests(ReaderApp app) : IClassFixture<ReaderApp>
{
    [Fact]
    public async Task AReader_IsSentToThePublishedArchive()
    {
        var res = await app.CreateClient(new() { AllowAutoRedirect = false }).GetAsync("/");
        res.StatusCode.Should().Be(HttpStatusCode.Redirect);
        new Uri(new Uri("http://localhost/"), res.Headers.Location!).AbsolutePath.Should().Be("/published");
    }
}

/// <summary>Today's Send step: the Send dialog, with a test copy first and each edition's own confirmation.</summary>
public class SendFromTodayTests(TideApp app) : IClassFixture<TideApp>
{
    // A day whose brief can be sent now: today's, or on a weekend the last business day's (a brief isn't sent early).
    private DateOnly Today => TestDb.SendDayFor(app.Services.GetRequiredService<OrgClock>());

    private async Task<ComponentHarness.Rendered<Tide.Web.Components.Shared.DistributePanel>> Dialog()
    {
        await using (var scope = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst)) await Tide.IntegrationTests.Audit.WebSeed.SendableDayAsync(scope.ServiceProvider);
        var r = await ComponentHarness.RenderAsync<Tide.Web.Components.Shared.DistributePanel>(app, "/", new() { ["Date"] = Today });
        r.Component.SendWithoutMeeting(true);
        return r;
    }

    /// <summary>The panel's distribution service, as the person it rendered for (a test's own flow doesn't carry the sign-in).</summary>
    internal static Tide.Application.Distribution.DistributionService SignedIn<T>(ComponentHarness.Rendered<T> r) where T : Microsoft.AspNetCore.Components.IComponent
    {
        r.Scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Http.IHttpContextAccessor>().HttpContext = r.Signedin;
        return r.Scope.ServiceProvider.GetRequiredService<Tide.Application.Distribution.DistributionService>();
    }

    [Fact]
    public async Task TheSendStep_OpensTheDialog_WithTheTestCopyFirst_ThenBothEditionsConfirmations()
    {
        await using (var scope = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst)) await Tide.IntegrationTests.Audit.WebSeed.SendableDayAsync(scope.ServiceProvider);
        await using var r = await ComponentHarness.RenderAsync<BriefPage>(app, "/", new() { ["Day"] = $"{Today:yyyy-MM-dd}", ["AsToday"] = true });
        var page = await r.Html();
        page.Should().Contain("title=\"Send a test copy, then each edition\"").And.NotContain("tide-modal");

        await r.Press(p => p.AskSend());
        var html = await r.HtmlOnce("Preview the company-wide edition as readers get it");
        var dialog = html[html.IndexOf("tide-modal-scrim", StringComparison.Ordinal)..];
        dialog.Should().Contain("role=\"dialog\"").And.Contain(">Send the brief</h2>").And.Contain(">Close</button>")
            .And.NotContain("alertdialog").And.NotContain("tide-email-frame", "the dialog previews with the edition, not the email frame");
        var test = dialog.IndexOf("Send a test copy to me", StringComparison.Ordinal);
        var team = dialog.IndexOf("id=\"send-team-h\"", StringComparison.Ordinal);
        var company = dialog.IndexOf("id=\"send-public-h\"", StringComparison.Ordinal);
        test.Should().BePositive().And.BeLessThan(team);
        team.Should().BeLessThan(company);
        foreach (var edition in new[] { dialog[team..company], dialog[company..] })
            edition.Should().Contain("Marked <span").And.Contain("Subject: <strong>").And.Contain("Checked version <span class=\"tide-recipients\"")
                .And.Contain("tide-send-preview").And.Contain("tide-edition-banner").And.Contain("bi bi-send-check");
        dialog[team..company].Should().Contain("Preview the security team edition as readers get it");
    }

    [Fact]
    public async Task TheTestCopy_GoesForBothEditions_ToMe_AndIsLogged()
    {
        await using var r = await Dialog();
        await r.Click(d => d.SendTests());
        var html = await r.Html();
        html.Should().Contain("Test copies of both editions went to tester@example.com.");
        html.Should().Contain("<td>Team <span class=\"tide-badge").And.Contain("<td>Company-wide <span class=\"tide-badge")
            .And.NotContain("Team<span class=\"tide-badge", "the log's edition and its test badge read apart");
        var sends = await SignedIn(r).SendsAsync(Today);
        sends.Where(s => s.IsTest).Select(s => s.Edition).Should().Contain([Edition.Team, Edition.Public]);
        sends.Should().NotContain(s => !s.IsTest, "a test copy isn't a send");
    }

    [Fact]
    public async Task ARefusedSend_ReReadsThePlan_AndSaysSoBesideTheEdition()
    {
        await using var r = await Dialog();
        var distribution = SignedIn(r);
        var stale = (await distribution.PlanAsync(Today, Edition.Team)) with { Fingerprint = "changed since" };

        await r.Click(d => { d.Tick(Edition.Team, true); return Task.CompletedTask; });
        Task<bool>? sent = null;
        await r.Renderer.Dispatcher.InvokeAsync(() => { sent = r.Pressed(d => d.SendNow(stale)) as Task<bool>; });
        (await sent!).Should().BeFalse("a refused send keeps the dialog open");
        var html = await r.Html();
        html.Should().Contain("changed since you confirmed").And.Contain("shown again as it is now");
        (await distribution.SendsAsync(Today)).Should().NotContain(s => !s.IsTest, "nothing went");
        r.Component.Ticked(Edition.Team).Should().BeFalse("the plan was read again, so it's checked again");
    }
}

/// <summary>A real send from Today's dialog, in its own app: the step strip then says one edition is left.</summary>
public class SendFromTodayRealSendTests(TideApp app) : IClassFixture<TideApp>
{
    [Fact]
    public async Task OneEditionSent_LeavesTheOtherToGo_InTheDialogAndTheStrip()
    {
        DateOnly today;
        await using (var scope = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst))
        {
            await scope.ServiceProvider.GetRequiredService<Tide.Application.Settings.DistributionSettings>()
                .SaveRecipientsAsync(Edition.Team, "cti@example.org; soc@example.org");
            today = await Tide.IntegrationTests.Audit.WebSeed.SendableDayAsync(scope.ServiceProvider);
            // The meeting was held: a brief goes after it.
            var briefs = scope.ServiceProvider.GetRequiredService<Tide.Application.Briefs.BriefService>();
            await briefs.MarkPresentedAsync((await briefs.GetOrStartAsync(today)).Id);
        }

        await using (var r = await ComponentHarness.RenderAsync<Tide.Web.Components.Shared.DistributePanel>(app, "/", new() { ["Date"] = today }))
        {
            var plan = await SendFromTodayTests.SignedIn(r).PlanAsync(today, Edition.Team);
            string TeamSend(string html) => html[html.IndexOf("id=\"send-team-h\"", StringComparison.Ordinal)..html.IndexOf("id=\"send-public-h\"", StringComparison.Ordinal)];
            const string held = "class=\"btn btn-primary btn-sm\" disabled";

            // "Send to 2" waits for the tick, which is a labelled checkbox naming the marking and the list.
            var before = TeamSend(await r.Html());
            before.Should().Contain("<label class=\"form-check-label small\" for=\"send-team-checked\">")
                .And.Contain($"I&#x27;ve checked the marking ({Tide.Domain.Enums.Tlp.Label(plan.Marking)}) and the recipients (2 addresses on the security team edition list)")
                .And.Contain(held);
            await r.Click(d => { d.Tick(Edition.Team, true); return Task.CompletedTask; });
            TeamSend(await r.Html()).Should().NotContain(held);

            // A re-read plan clears the tick, so what's confirmed is what's shown now.
            await r.Click(d => d.Replan(Edition.Team));
            r.Component.Ticked(Edition.Team).Should().BeFalse();
            await r.Click(d => { d.Tick(Edition.Team, true); return Task.CompletedTask; });
            Task<bool>? sent = null;
            await r.Renderer.Dispatcher.InvokeAsync(() => { sent = r.Pressed(d => d.SendNow(plan)) as Task<bool>; });
            (await sent!).Should().BeTrue();
            var html = await r.Html();
            html.Should().Contain("Sent to 2 recipients.").And.Contain("Already sent", "the team edition's confirmation now says it went");
            r.Component.Ticked(Edition.Team).Should().BeFalse("a send clears the tick");
            TeamSend(html).Should().Contain(held);
        }

        var page = await app.CreateClient().GetStringAsync($"/briefs/{today:yyyy-MM-dd}");
        page.Should().Contain("1 of 2 sent");
    }
}
