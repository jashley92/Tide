using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Actions;
using Tide.Application.Articles;
using Tide.Application.Briefs;
using Tide.Application.Org;
using Tide.Application.Settings;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Infrastructure.Persistence;
using Tide.Web.Components.Pages;
using Tide.Web.Components.Pages.Admin;
using static Tide.IntegrationTests.ComponentHarness;
using static Tide.IntegrationTests.PresentHarness;

namespace Tide.IntegrationTests;

/// <summary>
/// The meeting's "Actions that need review" slide: every open action the edition may show that is overdue, escalated, or
/// has gone too long without a person updating it (Open and In progress after two business days, Blocked after five, or
/// the organization's limits), each once, most pressing first, each saying why it's there. The same for the presenter and
/// everyone following; nothing above the meeting's ceiling is named or counted; none on the company-wide edition. Its own
/// app, so the slide holds only this class's actions, and each test retires its own when it ends.
/// </summary>
public class PresentActionsReviewTests(TideApp app) : IClassFixture<TideApp>
{
    private static readonly SignIn Owner = new("dev:review-owner", "Rhea Owner", "ActionOwner");
    private static readonly SignIn Visitor = new("dev:review-visitor", "Vic Visitor", "Visitor");

    private OrgClock Clock => app.Services.GetRequiredService<OrgClock>();

    private DateOnly Back(DateOnly from, int workingDays)
    {
        var day = from;
        for (var i = 0; i < workingDays; i++) day = Clock.Days.Previous(day);
        return day;
    }

    // Midday on a day, in the organization's zone.
    private DateTimeOffset Noon(DateOnly day) => Clock.StartOfDay(day).AddHours(12);

    // A past brief's day with one CLEAR article in it, and a team (no members: an action someone holds is still theirs).
    private async Task<(DateOnly Day, Guid Article, Guid Team)> Brief(int daysBack, string titled)
    {
        var day = Back(Clock.CurrentBriefDate, daysBack);
        await using var scope = SignedIn(app, SignIn.Analyst);
        var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
        var briefs = scope.ServiceProvider.GetRequiredService<BriefService>();
        var brief = await briefs.GetOrStartAsync(day);
        var article = await articles.CreateAsync($"{titled} {daysBack}", "Vendor", null, TlpLevel.Clear);
        await briefs.AddArticleAsync(brief.Id, article);
        var team = await scope.ServiceProvider.GetRequiredService<TeamService>().AddAsync($"Review team {daysBack}", null, null);
        return (day, article, team);
    }

    private readonly List<int> _made = [];

    /// <summary>An action held by the tester at <paramref name="status"/>, whose history ends on <paramref name="lastUpdated"/>:
    /// with <paramref name="lastLine"/> a line of the tester's, with <paramref name="comment"/> a comment by Sam, last; with
    /// <paramref name="system"/>, a line by TIDE (the ladder's) on that day after it; <paramref name="escalated"/> flags it
    /// escalated for its due date.</summary>
    private async Task<int> Action(Guid team, string title, ActionStatus status, DateOnly lastUpdated, Guid? article = null, DateOnly? due = null,
        Priority priority = Priority.P3, bool comment = false, string? lastLine = null, DateOnly? system = null, bool escalated = false)
    {
        await using var scope = SignedIn(app, SignIn.Analyst);
        var actions = scope.ServiceProvider.GetRequiredService<ActionService>();
        var number = await actions.CreateAsync(new ActionForm
        {
            Title = title, Kind = ActionKind.Other, Priority = priority, TeamId = team, ArticleId = article, AssigneeId = "dev:tester",
            AssigneeName = "Tester", DueDate = due,
        });
        _made.Add(number);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var id = await db.Actions.Where(a => a.Number == number).Select(a => a.Id).SingleAsync();
        var at = Noon(lastUpdated);
        await db.Actions.Where(a => a.Id == id).ExecuteUpdateAsync(u => u.SetProperty(a => a.Status, status).SetProperty(a => a.CreatedAtUtc, at.AddHours(-1))
            .SetProperty(a => a.EscalatedForDue, escalated ? due : null));
        await db.ActionEvents.Where(e => e.ActionId == id).ExecuteUpdateAsync(u => u.SetProperty(e => e.AtUtc, at.AddHours(-1)));
        if (lastLine is not null) db.ActionEvents.Add(new ActionEvent { ActionId = id, AtUtc = at, ByName = "Tester", Text = lastLine });
        if (comment) db.ActionComments.Add(new ActionComment { ActionId = id, AtUtc = at, AuthorId = "dev:sam", AuthorName = "Sam", Text = "Still waiting." });
        if (system is { } d)
            db.ActionEvents.Add(new ActionEvent { ActionId = id, AtUtc = Noon(d), ByName = ReminderService.EscalatedBy, Text = "Escalated, 2 business days late: flagged escalated." });
        await db.SaveChangesAsync();
        return number;
    }

    // Each test's actions are cancelled when it ends, so the next test's slide holds only its own.
    private async Task Retire()
    {
        await using var scope = SignedIn(app, SignIn.Analyst);
        var made = _made.ToList();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Actions.Where(a => made.Contains(a.Number))
            .ExecuteUpdateAsync(u => u.SetProperty(a => a.Status, ActionStatus.Cancelled));
        _made.Clear();
    }

    private async Task SetLimits(int open, int blocked)
    {
        await using var scope = SignedIn(app, SignIn.Analyst);
        await scope.ServiceProvider.GetRequiredService<OperationalSettingsService>().SaveAsync(new Dictionary<string, string?>
        {
            [OperationalSettings.StaleOpenDays] = open.ToString(System.Globalization.CultureInfo.InvariantCulture),
            [OperationalSettings.StaleBlockedDays] = blocked.ToString(System.Globalization.CultureInfo.InvariantCulture),
        });
    }

    private async Task<OpenActionsReview> Review(DateOnly day, TlpLevel ceiling = TlpLevel.AmberStrict)
    {
        await using var scope = SignedIn(app, SignIn.Analyst);
        return await scope.ServiceProvider.GetRequiredService<ActionService>().MeetingActionsAsync(day, ceiling);
    }

    private const string Table = "<table class=\"tide-slide-open\"";

    /// <summary>The title slide and the five after it, the HTML of each, stepping through them as the presenter.</summary>
    private static async Task<List<string>> LeadSlides(Rendered<Present> r)
    {
        List<string> slides = [await r.Html()];
        for (var i = 0; i < 5; i++)
        {
            await r.Click(p => p.OnPresentKey("next"));
            slides.Add(await r.Html());
        }
        return slides;
    }

    /// <summary>Steps on from the title to the slide, and stays there.</summary>
    private static async Task<string> ToSlide(Rendered<Present> r)
    {
        var html = await r.Html();
        for (var i = 0; i < 6 && !html.Contains(Table, StringComparison.Ordinal); i++)
        {
            await r.Click(p => p.OnPresentKey("next"));
            html = await r.Html();
        }
        html.Should().Contain(Table);
        return html;
    }

    /// <summary>The slide's content, from its heading to the line under its table, as read.</summary>
    private static string Slide(string html)
    {
        var at = html.IndexOf("<h2>Actions that need review</h2>", StringComparison.Ordinal);
        at.Should().BeGreaterThan(0, "the slide is shown, by its title");
        var end = html.IndexOf("</table>", at, StringComparison.Ordinal);
        var line = html.IndexOf("</div>", end, StringComparison.Ordinal);
        return System.Net.WebUtility.HtmlDecode(html[at..line]);
    }

    private static string Row(string slide, string title)
    {
        var at = slide.IndexOf(title, StringComparison.Ordinal);
        at.Should().BeGreaterThan(0, $"{title} is listed");
        var start = slide.LastIndexOf("<tr", at, StringComparison.Ordinal);
        return slide[start..slide.IndexOf("</tr>", at, StringComparison.Ordinal)];
    }

    [Fact]
    public async Task OneSlide_EachActionOnce_MostPressingFirst_EachSayingWhyItsThere()
    {
        var (day, article, team) = await Brief(70, "Review order");
        try
        {
            await Action(team, "Review quiet sweep", ActionStatus.InProgress, Back(day, 2), lastLine: "Ticked \"Run the sweep\".");
            var lateQuiet = await Action(team, "Review late and quiet", ActionStatus.Open, Back(day, 4), article, due: Back(day, 1), comment: true);
            await Action(team, "Review other overdue", ActionStatus.Open, day, due: Back(day, 3));
            await Action(team, "Review blocked vendor", ActionStatus.Blocked, Back(day, 6), lastLine: "Status: Open → Blocked: waiting on the vendor.");
            await Action(team, "Review P1 overdue", ActionStatus.Open, day, due: Back(day, 1), priority: Priority.P1);
            await Action(team, "Review escalated", ActionStatus.Open, day, due: day.AddDays(3), escalated: true);
            await Action(team, "Fresh in progress", ActionStatus.InProgress, Back(day, 1));
            await Action(team, "Fresh blocked", ActionStatus.Blocked, Back(day, 3));
            await Action(team, "Quiet but tracked", ActionStatus.Tracking, Back(day, 10));

            await using var pat = await Open(app, day);
            (await pat.Html()).Should().Contain("6 actions that need review", "the title slide says what comes first");
            var slides = await LeadSlides(pat);
            slides.Count(h => h.Contains(Table, StringComparison.Ordinal)).Should().Be(1, "one slide, not two");
            var at = slides.FindIndex(h => h.Contains(Table, StringComparison.Ordinal));
            at.Should().BeGreaterThan(0, "after the title slide");
            slides[at + 1].Should().Contain("Review order 70", "before the brief's articles");

            var slide = Slide(slides[at]);
            string[] order = ["Review escalated", "Review P1 overdue", "Review blocked vendor", "Review other overdue", "Review late and quiet", "Review quiet sweep"];
            order.Select(t => slide.IndexOf(t, StringComparison.Ordinal)).Should().BeInAscendingOrder("escalated, P1/P2 overdue, quiet blocked, other overdue, quiet");
            foreach (var t in order) slide.Split($">{t}<").Length.Should().Be(2, $"{t} is listed once");
            Row(slide, "Review escalated").Should().Contain("escalated").And.Contain("Ask for an update");
            Row(slide, "Review blocked vendor").Should().Contain("quiet 6 business days (Blocked)").And.Contain("last: moved to Blocked by Tester")
                .And.Contain("Ask what it's waiting on");
            Row(slide, "Review late and quiet").Should().Contain($"A-{lateQuiet}").And.Contain("· Tester").And.Contain("Open").And.Contain("day late")
                .And.Contain("quiet 4 business days").And.Contain("last: comment by Sam").And.Contain("Ask for an update");
            Row(slide, "Review quiet sweep").Should().Contain("In progress").And.Contain("quiet 2 business days").And.Contain("last: checklist tick by Tester");
            slide.Should().NotContain("Fresh in progress").And.NotContain("Fresh blocked").And.NotContain("Quiet but tracked")
                .And.Contain("6 of 6 shown: overdue, escalated, or with no update for 2 business days (5 when blocked).")
                .And.NotContain("more under Actions");
        }
        finally { await Retire(); }
    }

    [Fact]
    public async Task TheLaddersLines_AreNotUpdates_AndTheActionTableCountsFromTheSameUpdate()
    {
        var (day, _, team) = await Brief(71, "Review ladder");
        try
        {
            var number = await Action(team, "Quiet but escalated", ActionStatus.Open, Back(day, 3), system: day);
            var row = (await Review(day)).Rows.Should().ContainSingle(o => o.Row.Number == number).Subject;
            (row.Stale, row.QuietDays).Should().Be((true, 3), "TIDE's escalation line on the day isn't an update");
            row.LastUpdate.Should().Be("raised by Tester");
            Clock.DayOf(row.Row.LastActivityUtc!.Value).Should().Be(Back(day, 3), "the action table's quiet text counts from the same update");

            // Saved later than its history says it was raised (an import, the demo): its history counts, not the save.
            await using (var scope = SignedIn(app, SignIn.Analyst))
                await scope.ServiceProvider.GetRequiredService<AppDbContext>().Actions.Where(a => a.Number == number)
                    .ExecuteUpdateAsync(u => u.SetProperty(a => a.CreatedAtUtc, DateTimeOffset.UtcNow));
            (await Review(day)).Rows.Single(o => o.Row.Number == number).QuietDays.Should().Be(3);
        }
        finally { await Retire(); }
    }

    [Fact]
    public async Task TheRowsAreCapped_TheRestCounted_AndNothingAboveTheCeilingIsNamedOrCounted()
    {
        var (day, _, team) = await Brief(72, "Review many");
        try
        {
            for (var i = 1; i <= 9; i++) await Action(team, $"Review many {i:00}", ActionStatus.Open, Back(day, 2 + i));
            Guid red;
            await using (var scope = SignedIn(app, SignIn.Analyst))
                red = await scope.ServiceProvider.GetRequiredService<ArticleService>().CreateAsync("Review RED report", "Partner", null, TlpLevel.Red);
            await Action(team, "Review red rotation", ActionStatus.Open, Back(day, 20), red, due: Back(day, 5));

            var review = await Review(day);
            (review.Rows.Count, review.Overdue).Should().Be((ActionService.OutstandingSize, 9));
            (await Review(day, TlpLevel.Red)).Overdue.Should().Be(10, "at RED it's in the edition");

            await using var pat = await Open(app, day);
            var slide = Slide(await ToSlide(pat));
            slide.Should().Contain("Review many 09").And.NotContain("Review many 01", "the least quiet is the one left off")
                .And.NotContain("Review red rotation").And.NotContain("Review RED report");
            slide.Should().Contain("8 of 9 shown").And.Contain("<a href=\"actions\">1 more under Actions</a>.");
            slide.Should().Contain("<button", "the presenter opens an action from its row");
        }
        finally { await Retire(); }
    }

    [Fact]
    public async Task AFollower_SeesTheSameSlide_WithNoneOfTheRail_AndAVisitor_HasNoLinksOut()
    {
        var (day, _, team) = await Brief(73, "Review room");
        try
        {
            for (var i = 1; i <= 9; i++) await Action(team, $"Review room {i:00}", ActionStatus.Open, Back(day, 2 + i));
            await using var pat = await Open(app, day);
            await Do(pat, p => p.StartMeeting());
            await using var owner = await Open(app, day, Owner);
            await Do(owner, p => p.JoinIfLive());
            await using var vic = await RenderAsync<Present>(app, $"/present/{day:yyyy-MM-dd}", new() { ["Day"] = $"{day:yyyy-MM-dd}" }, Visitor);
            await vic.Click(async p => { await p.GoLive(); await p.OnPresentKey("render"); });

            var mine = Slide(await ToSlide(pat));
            await Do(owner, p => p.Follow());
            await vic.Click(async p => { await p.Follow(); await p.OnPresentKey("render"); });

            var theirs = await owner.Html();
            theirs.Should().NotContain("id=\"present-rail\"");
            Slide(theirs).Should().Contain("Review room 09").And.Contain("1 more under Actions").And.Contain("quiet 10 business days").And.Contain("· Tester");
            mine.Should().Contain("Review room 09");

            var visitors = await vic.Html();
            var seen = Slide(visitors);
            seen.Should().Contain("Review room 09").And.Contain("1 more under Actions").And.Contain("quiet 10 business days");
            seen.Should().NotContain("<a ", "a Visitor gets no links out").And.NotContain("<button");
            visitors.Should().NotContain("id=\"present-rail\"");
            EndMeeting(app, day);
        }
        finally { await Retire(); }
    }

    [Fact]
    public async Task TheCompanyWideEdition_HasNoSuchSlide()
    {
        var (day, _, team) = await Brief(74, "Review public");
        try
        {
            await Action(team, "Review public action", ActionStatus.Open, Back(day, 4), due: Back(day, 2));
            await using var pat = await Open(app, day);
            await Do(pat, p => p.SetEdition(new ChangeEventArgs { Value = Edition.Public.ToString() }));
            foreach (var html in await LeadSlides(pat))
                html.Should().NotContain(Table).And.NotContain("Review public action").And.NotContain("need review");
        }
        finally { await Retire(); }
    }

    [Fact]
    public async Task NothingToReview_NoSlide()
    {
        var (day, _, team) = await Brief(75, "Review none");
        try
        {
            await Action(team, "Review none fresh", ActionStatus.Open, day, due: day.AddDays(5));
            await using var pat = await Open(app, day);
            foreach (var html in await LeadSlides(pat)) html.Should().NotContain(Table);
        }
        finally { await Retire(); }
    }

    [Fact]
    public async Task TheLimitsChanging_WhileTheSlideIsOpen_ReadsItsRowsAgain_ForThePresenterAndEveryoneFollowing()
    {
        var (day, _, team) = await Brief(77, "Review change");
        try
        {
            await Action(team, "Review change quiet", ActionStatus.Open, Back(day, 2));
            await Action(team, "Review change overdue", ActionStatus.Open, day, due: Back(day, 2));
            await using var pat = await Open(app, day);
            await Do(pat, p => p.StartMeeting());
            await using var owner = await Open(app, day, Owner);
            await Do(owner, p => p.JoinIfLive());
            Slide(await ToSlide(pat)).Should().Contain("Review change quiet").And.Contain("2 of 2 shown");
            await Do(owner, p => p.Follow());
            Slide(await owner.Html()).Should().Contain("Review change quiet");

            await SetLimits(3, 6);
            foreach (var r in new[] { pat, owner })
            {
                var slide = Slide(await r.HtmlOnce("1 of 1 shown"));
                slide.Should().NotContain("Review change quiet", "2 business days is no longer too long").And.Contain("Review change overdue")
                    .And.Contain("1 of 1 shown: overdue, escalated, or with no update for 3 business days (6 when blocked).");
            }
            EndMeeting(app, day);
        }
        finally
        {
            await SetLimits(StaleLimits.DefaultOpenDays, StaleLimits.DefaultBlockedDays);
            await Retire();
        }
    }

    [Fact]
    public async Task ThePresentersOwnChange_FromTheSlide_ReadsItsRowsAgain()
    {
        var (day, _, team) = await Brief(78, "Review own");
        try
        {
            var blocked = await Action(team, "Review own blocked", ActionStatus.Blocked, Back(day, 6));
            await Action(team, "Review own overdue", ActionStatus.Open, day, due: Back(day, 2));
            await using var pat = await Open(app, day);
            await Do(pat, p => p.StartMeeting());
            Slide(await ToSlide(pat)).Should().Contain("Review own blocked").And.Contain("2 of 2 shown");
            await Do(pat, p => p.ShowAction(blocked));

            // The action card's status move (PresentAction saves it, then tells the page): it was updated just now.
            await using (var scope = SignedIn(app, SignIn.Analyst))
            {
                var actions = scope.ServiceProvider.GetRequiredService<ActionService>();
                await actions.SetStatusAsync((await actions.GetAsync(blocked))!.Action.Id, ActionStatus.InProgress, ActionOutcome.None, null);
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var id = await db.Actions.Where(a => a.Number == blocked).Select(a => a.Id).SingleAsync();
                await db.ActionEvents.Where(e => e.ActionId == id && e.Text.StartsWith("Status:")).ExecuteUpdateAsync(u => u.SetProperty(e => e.AtUtc, Noon(day)));
            }
            await Do(pat, p => p.OnActionChanged());
            var slide = Slide(await pat.Html());
            slide.Should().NotContain(">Review own blocked<", "moved on the brief's day, it no longer needs review").And.Contain("1 of 1 shown");
            EndMeeting(app, day);
        }
        finally { await Retire(); }
    }

    [Fact]
    public async Task EveryRow_SaysItsLastUpdate_AndTheTitleSlide_CountsEveryActionThatNeedsReview()
    {
        var (day, _, team) = await Brief(79, "Review last");
        try
        {
            for (var i = 1; i <= 9; i++) await Action(team, $"Review last {i:00}", ActionStatus.Open, day, due: Back(day, i), comment: i == 1);
            await Action(team, "Review last escalated", ActionStatus.Open, day, due: day.AddDays(3), escalated: true);
            await using var pat = await Open(app, day);
            (await pat.Html()).Should().Contain("10 actions that need review", "all that qualify, not the 8 shown");
            var html = await ToSlide(pat);
            var slide = Slide(html);
            var rows = slide.Split("<tr").Skip(2).Select(r => r[..r.IndexOf("</tr>", StringComparison.Ordinal)]).ToList();
            rows.Should().HaveCount(ActionService.OutstandingSize);
            rows.Should().OnlyContain(r => r.Contains("last: ") && r.Contains(" by "), "each row says what its last update was, and who");
            Row(slide, "Review last escalated").Should().Contain("last: raised by Tester");
            rows.Should().NotContain(r => r.Contains("no update for"), "one form for every row");
        }
        finally { await Retire(); }
    }

    [Fact]
    public async Task ALimitThatIsntAWholeNumber_IsRefused_NotRoundedDown()
    {
        await using var page = await RenderAsync<OrganizationAdmin>(app, "/admin/organization", @as: SignIn.Analyst);
        await page.Click(p => { p.StaleOpen = "2.5"; p.StaleBlocked = "6"; return p.Save(); });
        Clock.StaleLimits.Should().Be(StaleLimits.Default, "2.5 isn't saved as 2, and nothing else in the save is either");
        OperationalSettings.Problem(OperationalSettings.StaleOpenDays, "2.5").Should().Contain("whole number from 1 to 30");
    }

    [Fact]
    public async Task AnAdministrator_SetsTheLimits_InOrganizationSettings_AndTheyCountAtOnce_Audited()
    {
        var (day, _, team) = await Brief(76, "Review limits");
        try
        {
            await Action(team, "Review limits blocked", ActionStatus.Blocked, Back(day, 3));
            await Action(team, "Review limits open", ActionStatus.Open, Back(day, 2));
            async Task<IReadOnlyList<string>> Listed() => [.. (await Review(day)).Rows.Select(o => o.Row.Title)];
            (await Listed()).Should().Equal("Review limits open");

            await using var page = await RenderAsync<OrganizationAdmin>(app, "/admin/organization", @as: SignIn.Analyst);
            var html = await page.Html();
            html.Should().Contain("id=\"org-stale-open\"").And.Contain("id=\"org-stale-blocked\"").And.Contain("min=\"1\"").And.Contain("max=\"30\"")
                .And.Contain("Actions that need review");
            await page.Click(p => { p.StaleOpen = "3"; p.StaleBlocked = "3"; return p.Save(); });
            Clock.StaleLimits.Should().Be(new StaleLimits(3, 3), "at once, as the organization's other settings");
            (await Listed()).Should().Equal("Review limits blocked");

            await using (var scope = SignedIn(app, SignIn.Analyst))
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                (await db.AuditLog.AnyAsync(a => a.EntityType == nameof(AppSetting) && a.EntityKey == OperationalSettings.StaleBlockedDays)).Should().BeTrue();
            }

            await page.Click(p => { p.StaleOpen = "0"; return p.Save(); });
            Clock.StaleLimits.Should().Be(new StaleLimits(3, 3), "0 isn't saved");
        }
        finally
        {
            await SetLimits(StaleLimits.DefaultOpenDays, StaleLimits.DefaultBlockedDays);
            await Retire();
        }
    }
}
