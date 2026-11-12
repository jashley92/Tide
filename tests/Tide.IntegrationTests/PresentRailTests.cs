using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Abstractions;
using Tide.Application.Actions;
using Tide.Application.Articles;
using Tide.Application.Briefs;
using Tide.Application.Metrics;
using Tide.Application.Org;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Infrastructure.Persistence;
using Tide.Web.Components.Pages;
using Tide.Web.Components.Pages.Admin;
using static Tide.IntegrationTests.ComponentHarness;
using static Tide.IntegrationTests.PresentHarness;

namespace Tide.IntegrationTests;

/// <summary>
/// The presenter's rail: what's theirs to do on each slide, in one place beside the slide (Mark discussed, the hands,
/// everyone's view, the article's actions and a note, the day's loose ends, what's left before ending), and never in a
/// follower's page, whatever the follower may do elsewhere. Its own app, since metrics are global.
/// </summary>
public class PresentRailTests(TideApp app) : IClassFixture<TideApp>
{
    private static readonly SignIn Owner = new("dev:rail-owner", "Olive Owner", "ActionOwner");
    private static readonly SignIn Robin = new("dev:rail-robin", "Robin Analyst", "Analyst");

    /// <summary>What's only in the rail, by the words or names it uses.</summary>
    private static readonly string[] RailOnly =
    [
        "present-rail", "Mark discussed", "Bring everyone here", "Lock to me", "Hand presenting to", "aria-label=\"Kind for:",
        "aria-label=\"New action title\"", "Still current", "number missing", "Not raised yet", "Your note", "Clear all", "Take it",
    ];

    private async Task<DateOnly> Day(int daysBack)
    {
        var day = await TwoArticleBrief(app, daysBack, "Rail");
        await using var scope = SignedIn(app, SignIn.Analyst);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var brief = await db.Briefs.Include(b => b.Items).ThenInclude(i => i.Article).SingleAsync(b => b.BriefDate == day);
        brief.TeamNote = "Payroll lures are up again.";
        brief.TeamNoteReviewedAtUtc = null;
        foreach (var item in brief.Items) item.Article!.RecommendedActions = "- Patch the gateways\n- Ask the vendor for the fixed build";
        await db.SaveChangesAsync();
        var metrics = scope.ServiceProvider.GetRequiredService<MetricsService>();
        if (!(await metrics.GetDayAsync(day)).Groups.Any(g => g.Group.Name == "Rail numbers"))
        {
            var g = await metrics.AddGroupAsync("Rail numbers");
            await metrics.SaveMetricAsync(new MetricForm { GroupId = g, Name = "Phish reported", Direction = MetricDirection.LowerIsBetter });
        }
        return day;
    }

    /// <summary>Where the rail's markup is in the page, from its &lt;aside&gt; to its close (null with no rail).</summary>
    private static Range? Rail(string html)
    {
        const string Close = "</aside>";
        var at = html.IndexOf("<aside id=\"present-rail\"", StringComparison.Ordinal);
        return at < 0 ? null : at..(html.IndexOf(Close, at, StringComparison.Ordinal) + Close.Length);
    }

    private static string RailOf(string html)
    {
        var rail = Rail(html);
        rail.Should().NotBeNull("the presenter's rail is open");
        return html[rail!.Value];
    }

    private static string WithoutRail(string html) =>
        Rail(html) is { } rail ? html[..rail.Start] + html[rail.End..] : html;

    [Fact]
    public async Task AFollowersPage_HasNoneOfTheRail_OnAnySlide_EvenForSomeoneWhoCouldPresent()
    {
        var day = await Day(211);
        await using var pat = await Open(app, day);
        await Do(pat, p => p.StartMeeting());
        await using var owner = await Open(app, day, Owner);
        await Do(owner, p => p.JoinIfLive());
        await using var robin = await Open(app, day, Robin);
        await Do(robin, p => p.JoinIfLive());
        await Do(owner, p => p.ToggleHand());

        for (var slide = 0; slide < 12; slide++)
        {
            foreach (var follower in new[] { owner, robin })
            {
                await Do(follower, p => p.Follow());
                await Do(follower, p => p.ToggleRail());   // a follower's ] does nothing
                var theirs = await follower.Html();
                foreach (var word in RailOnly) theirs.Should().NotContain(word, $"a follower's page, slide {slide + 1}, has none of the rail");
            }
            var mine = await pat.Html();
            if (mine.Contains("<h2>That's the brief</h2>", StringComparison.Ordinal)) break;
            await pat.Click(p => p.OnPresentKey("next"));
        }
        EndMeeting(app, day);
    }

    [Fact]
    public async Task TheRail_HoldsThePresentersControls_SlideBySlide_AndTheSlideNoneOfThem()
    {
        var day = await Day(212);
        await using var pat = await Open(app, day);
        await Do(pat, p => p.StartMeeting());

        // The title: what's not ready, and what comes first. (The window has room: the rail is docked, open.)
        var html = await pat.Html();
        var rail = RailOf(html);
        rail.Should().Contain("before you start").And.Contain("Ready to present?").And.Contain("2 articles in order")
            .And.Contain("Opening note not updated").And.Contain(">Still current</button>").And.Contain("number missing");

        // The opening note: its Still current and Edit, in the rail, not on the slide.
        await pat.Click(p => p.OnPresentKey("next"));
        html = await pat.Html();
        RailOf(html).Should().Contain("The note").And.Contain(">Edit</button>").And.Contain("Up next");
        WithoutRail(html).Should().Contain("Payroll lures are up again.").And.NotContain("Still current");

        // The numbers: the missing one's box.
        await pat.Click(p => p.OnPresentKey("next"));
        html = await pat.Html();
        RailOf(html).Should().Contain("aria-label=\"Phish reported for");
        WithoutRail(html).Should().Contain("Rail numbers").And.NotContain("aria-label=\"Phish reported for");

        // An article: its actions and the lines to raise, the hands, a note, everyone's view, Mark discussed.
        await pat.Click(p => p.OnPresentKey("last"));
        await pat.Click(p => p.OnPresentKey("prev"));
        await pat.Click(p => p.OnPresentKey("prev"));
        html = await pat.Html();
        rail = RailOf(html);
        rail.Should().Contain("Actions on this article").And.Contain("aria-label=\"Kind for: Patch the gateways\"").And.Contain("aria-label=\"New action title\"")
            .And.Contain("Hands").And.Contain("Your note").And.Contain("Everyone").And.Contain("Lock to me").And.Contain("Mark discussed");
        var slide = WithoutRail(html);
        slide.Should().Contain("Patch the gateways", "the room still reads the recommendations").And.NotContain("Mark discussed")
            .And.NotContain("aria-label=\"Kind for:").And.NotContain("Lock to me").And.NotContain("aria-label=\"New action title\"");

        // The wrap-up: what's left before ending, and End.
        await pat.Click(p => p.OnPresentKey("last"));
        html = await pat.Html();
        RailOf(html).Should().Contain("Before you end").And.Contain("Not raised yet").And.Contain("Not discussed").And.Contain("End the meeting");

        // ] closes it again.
        await pat.Click(p => p.OnPresentKey("rail"));
        (await pat.Html()).Should().NotContain("<aside id=\"present-rail\"");
        EndMeeting(app, day);
    }

    [Fact]
    public async Task WithNoMeeting_TheRailIsTheirsWhoCanPresent_AndNotAnActionOwners()
    {
        var day = await Day(213);
        await using var pat = await Open(app, day);
        RailOf(await pat.Html()).Should().Contain("Ready to present?");
        await using var owner = await Open(app, day, Owner);
        await Do(owner, p => p.ToggleRail());
        (await owner.Html()).Should().NotContain("present-rail");
    }

    // The first article's slide: the one before the second's, which is before the wrap-up.
    private static async Task FirstArticle(Rendered<Present> r)
    {
        await r.Click(p => p.OnPresentKey("last"));
        await r.Click(p => p.OnPresentKey("prev"));
        await r.Click(p => p.OnPresentKey("prev"));
    }

    [Fact]
    public async Task OverTheSlide_TheRailIsModal_BehindAScrim_ThatClosesIt()
    {
        var day = await Day(214);
        await using var pat = await Open(app, day);
        await Do(pat, p => p.StartMeeting());
        await pat.Click(p => p.OnRailRoom(false));   // a narrower window: shut, then opened over the slide
        await Do(pat, p => p.ToggleRail());
        var html = await pat.Html();
        RailOf(html).Should().Contain("role=\"dialog\"").And.Contain("aria-modal=\"true\"");
        html.Should().Contain("class=\"tide-present-scrim\"");
        html.Should().Contain("<header class=\"tide-present-top\" inert").And.Contain("<footer class=\"tide-present-bottom\" inert");
        html.Should().MatchRegex("<section class=\"tide-slide\"[^>]* inert");

        await Do(pat, p => p.CloseRailFromScrim());
        html = await pat.Html();
        html.Should().NotContain("<aside id=\"present-rail\"").And.NotContain("tide-present-scrim").And.NotContain(" inert");

        // Docked, nothing is held: the page beside it works as ever.
        await pat.Click(p => p.OnRailRoom(true));
        html = await pat.Html();
        RailOf(html).Should().NotContain("aria-modal");
        html.Should().NotContain("tide-present-scrim").And.NotContain(" inert");
        EndMeeting(app, day);
    }

    [Fact]
    public async Task Assign_OffersOnlyTheTeamsMembers_WhomTheServiceTakes_AndSaysSoWhenItHasNone()
    {
        var day = await Day(215);
        int withMembers, without;
        await using (var scope = SignedIn(app, SignIn.Analyst))
        {
            var sp = scope.ServiceProvider;
            await sp.GetRequiredService<IUserDirectory>().TouchAsync("dev:rail-bo", "Bo Outsider", null, null, "ActionOwner");
            var teams = sp.GetRequiredService<TeamService>();
            var staffed = await teams.AddAsync("Rail staffed team", null, null);
            await teams.AddMemberAsync(staffed, "dev:rail-asa", "Asa Assignee");
            var empty = await teams.AddAsync("Rail empty team", null, null);
            var first = (await sp.GetRequiredService<BriefService>().GetAsync(day))!.Items.OrderBy(i => i.SortOrder).First().ArticleId;
            var actions = sp.GetRequiredService<ActionService>();
            withMembers = await actions.QuickAddAsync(first, "Rail assign staffed", ActionKind.IocSweep, staffed, null, day);
            without = await actions.QuickAddAsync(first, "Rail assign empty", ActionKind.IocSweep, empty, null, day);
        }
        await using var pat = await Open(app, day);
        await FirstArticle(pat);
        var rail = RailOf(await pat.Html());
        var staffedRow = rail[rail.IndexOf($"aria-label=\"Assign A-{withMembers}\"", StringComparison.Ordinal)..];
        staffedRow = staffedRow[..staffedRow.IndexOf("</select>", StringComparison.Ordinal)];
        staffedRow.Should().Contain(">Asa Assignee</option>").And.NotContain("Bo Outsider", "AssignAsync takes only the team's members")
            .And.NotContain("Everyone else");
        rail.Should().Contain("No one is on Rail empty team yet").And.Contain("href=\"admin/teams\"", "an administrator can add them");

        // What it offers, the service takes.
        ActionItem a;
        await using (var scope = SignedIn(app, SignIn.Analyst))
            a = (await scope.ServiceProvider.GetRequiredService<ActionService>().GetAsync(withMembers))!.Action;
        await Do(pat, p => p.Assign(new EditionAction(a.Number, a.Title, a.Team!.Name, a.DueDate, a.Status, a.Id, a.TeamId), "dev:rail-asa"));
        await using (var scope = SignedIn(app, SignIn.Analyst))
            (await scope.ServiceProvider.GetRequiredService<ActionService>().GetAsync(withMembers))!.Action.AssigneeName.Should().Be("Asa Assignee");
        rail.Should().Contain($"aria-label=\"Assign A-{without}\"");
    }

    [Fact]
    public async Task TheWrapUp_HasOneEnd_InTheRail()
    {
        var day = await Day(216);
        await using var pat = await Open(app, day);
        await Do(pat, p => p.StartMeeting());
        await pat.Click(p => p.OnPresentKey("last"));
        var rail = RailOf(await pat.Html());
        System.Text.RegularExpressions.Regex.Matches(rail, "End the meeting</button>").Count.Should().Be(1);
        EndMeeting(app, day);
    }

    [Fact]
    public async Task TopBarEnd_WithTheRailShut_OpensItOnTheWrapUp_AndNeverEndsUnseen()
    {
        var day = await Day(219);
        await using var pat = await Open(app, day);
        await Do(pat, p => p.StartMeeting());
        await pat.Click(p => p.OnRailRoom(false));   // a laptop below the docking width: the rail is shut

        // Articles not discussed yet: End brings the wrap-up's choices forward, in the rail, and ends nothing.
        await Do(pat, p => p.EndFromTop());
        var rail = RailOf(await pat.Html());
        rail.Should().Contain("Not discussed (2)").And.Contain("End the meeting</button>");
        app.Services.GetRequiredService<Tide.Web.Services.LiveMeetings>().Get(day).Should().NotBeNull("nothing was ended");

        // Shut again on the wrap-up: End opens it again rather than ending past choices no one can see.
        await Do(pat, p => p.ToggleRail());
        (await pat.Html()).Should().NotContain("<aside id=\"present-rail\"");
        await Do(pat, p => p.EndFromTop());
        RailOf(await pat.Html()).Should().Contain("End the meeting</button>");
        app.Services.GetRequiredService<Tide.Web.Services.LiveMeetings>().Get(day).Should().NotBeNull("the choices weren't on screen");

        // With them on screen, End ends the meeting as the rail's own End does.
        await Do(pat, p => p.EndFromTop());
        app.Services.GetRequiredService<Tide.Web.Services.LiveMeetings>().Get(day).Should().BeNull();
    }

    [Fact]
    public async Task TheNote_EditedFromTheRail_NeverOverwritesAColleaguesNewerOne()
    {
        var day = await Day(220);
        await using var pat = await Open(app, day);
        await pat.Click(p => p.OnPresentKey("next"));
        await Do(pat, p => p.EditOpeningNote());
        Guid briefId;
        await using (var scope = SignedIn(app, Robin))
        {
            var briefs = scope.ServiceProvider.GetRequiredService<BriefService>();
            briefId = (await briefs.GetAsync(day))!.Id;
            await briefs.SetTeamNoteAsync(briefId, "Robin's newer note.");
        }
        await Do(pat, p => p.OnChangedElsewhere([new Change(ChangeFeed.Brief(briefId), Robin.UserId, Robin.Name, Guid.NewGuid())]));
        RailOf(await pat.Html()).Should().Contain("Robin Analyst saved this team&#x27;s opening note while you were editing.");

        await Do(pat, p => p.SaveOpeningNote("The presenter's older edit."));
        await using (var scope = SignedIn(app, SignIn.Analyst))
            (await scope.ServiceProvider.GetRequiredService<BriefService>().GetAsync(day))!.TeamNote.Should().Be("Robin's newer note.", "the save is refused");

        // Reload: theirs in the box, and saving from it goes through.
        await Do(pat, p => p.EditOpeningNote());
        RailOf(await pat.Html()).Should().Contain("Robin&#x27;s newer note.</textarea>").And.NotContain("while you were editing");
        await Do(pat, p => p.SaveOpeningNote("Robin's newer note, and one more line."));
        await using (var scope = SignedIn(app, SignIn.Analyst))
            (await scope.ServiceProvider.GetRequiredService<BriefService>().GetAsync(day))!.TeamNote.Should().Be("Robin's newer note, and one more line.");
    }

    [Fact]
    public async Task TheNote_KeepsAnEdit_OnceCurrent_AndTheNumbers_SayNothing_OnceNoneIsMissing()
    {
        var day = await Day(217);
        await using var pat = await Open(app, day);
        await pat.Click(p => p.OnPresentKey("next"));
        await Do(pat, p => p.KeepOpeningNote());
        RailOf(await pat.Html()).Should().Contain(">Edit</button>", "the note can still be changed once it's current").And.NotContain("Still current");

        await pat.Click(p => p.OnPresentKey("next"));
        RailOf(await pat.Html()).Should().Contain("rail-numbers-h");
        Guid metric;
        await using (var scope = SignedIn(app, SignIn.Analyst))
            metric = (await scope.ServiceProvider.GetRequiredService<MetricsService>().GetDayAsync(day)).Groups
                .Single(g => g.Group.Name == "Rail numbers").Tiles.Single().Metric.Id;
        await Do(pat, p => p.EnterMissingNumber(metric, "4"));
        RailOf(await pat.Html()).Should().NotContain("rail-numbers-h", "no empty heading with nothing to enter");
    }

    [Fact]
    public async Task HandOff_WaitsForItsButton()
    {
        var day = await Day(218);
        await using var pat = await Open(app, day);
        await Do(pat, p => p.StartMeeting());
        await using var robin = await Open(app, day, Robin);
        await Do(robin, p => p.JoinIfLive());
        await Do(pat, p => p.ChooseHandOff(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = Robin.UserId }));
        app.Services.GetRequiredService<Tide.Web.Services.LiveMeetings>().Get(day)!.PresenterName.Should().Be("Tester", "choosing isn't handing off");
        RailOf(await pat.Html()).Should().Contain(">Hand off</button>");
        await Do(pat, p => p.ConfirmHandOff());
        app.Services.GetRequiredService<Tide.Web.Services.LiveMeetings>().Get(day)!.PresenterName.Should().Be("Robin Analyst");
        EndMeeting(app, day);
    }
}
