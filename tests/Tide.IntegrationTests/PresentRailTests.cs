using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Articles;
using Tide.Application.Briefs;
using Tide.Application.Metrics;
using Tide.Application.Org;
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
}
