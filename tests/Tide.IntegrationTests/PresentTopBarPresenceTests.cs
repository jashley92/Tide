using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Tide.Web.Components.Pages;
using Tide.Web.Services;
using static Tide.IntegrationTests.ComponentHarness;
using static Tide.IntegrationTests.PresentHarness;

namespace Tide.IntegrationTests;

/// <summary>Who is in a live meeting and whose hand is up show in the present page's top bar, for the presenter and for
/// everyone following alike; the footer keeps the slides, the time and the presenter's other controls.</summary>
public class PresentTopBarPresenceTests(TideApp app) : IClassFixture<TideApp>
{
    private static readonly SignIn Owner = new("dev:topbar-owner", "Olive Owner", "ActionOwner");
    private static readonly SignIn Robin = new("dev:topbar-robin", "Robin Analyst", "Analyst");

    private LiveMeetings Meetings => app.Services.GetRequiredService<LiveMeetings>();

    /// <summary>The top bar's markup.</summary>
    private static string Top(string html) => Between(html, "<header class=\"tide-present-top\">", "</header>");

    /// <summary>The footer's markup.</summary>
    private static string Footer(string html) => Between(html, "<footer class=\"tide-present-bottom\"", "</footer>");

    /// <summary>The top bar's group of who is here and whose hand is up.</summary>
    private static string People(string html) => Between(Top(html), "<div class=\"tide-present-people\"", "<div class=\"tide-present-tools\">");

    private static string Between(string html, string start, string end)
    {
        var from = html.IndexOf(start, StringComparison.Ordinal);
        from.Should().BeGreaterThanOrEqualTo(0, $"the page has {start}");
        var to = html.IndexOf(end, from, StringComparison.Ordinal);
        to.Should().BeGreaterThan(from, $"{start} ends");
        return html[from..to];
    }

    private static void FooterHasNoneOfIt(string html)
    {
        var footer = Footer(html);
        footer.Should().NotContain("tide-presence").And.NotContain("tide-hands").And.NotContain("Raise hand").And.NotContain("Lower hand")
            .And.NotContain("&#x27;s hand").And.NotContain("Clear all").And.NotContain("In the meeting:");
        footer.Should().Contain("tide-progress", "the slides stay in the footer");
    }

    [Fact]
    public async Task PresenceAndHands_AreInTheTopBar_ForThePresenterAndForFollowers_InTheOrderTheyWentUp()
    {
        var day = await TwoArticleBrief(app, 181, "Top bar");
        await using var pat = await Open(app, day);
        await Do(pat, p => p.StartMeeting());
        await using var owner = await Open(app, day, Owner);
        await Do(owner, p => p.JoinIfLive());
        await using var robin = await Open(app, day, Robin);
        await Do(robin, p => p.JoinIfLive());

        // Before any hand is up: everyone sees who is here, in the top bar, and a follower's Raise hand is there too.
        var html = await owner.Html();
        People(html).Should().Contain("tide-presence-dot").And.Contain("In the meeting: Tester (presenting), Olive Owner, Robin Analyst.")
            .And.Contain("Raise hand").And.Contain("aria-pressed=\"false\"").And.Contain("3 here");
        FooterHasNoneOfIt(html);

        // Olive, then Robin, raise a hand.
        await Do(owner, p => p.ToggleHand());
        await Do(robin, p => p.ToggleHand());
        Meetings.Get(day)!.Hands.Select(h => h.Name).Should().Equal("Olive Owner", "Robin Analyst");

        // The presenter: both hands in order, each one theirs to lower, and Clear all.
        await Do(pat, p => p.Follow());
        html = await pat.Html();
        var people = People(html);
        people.Should().Contain("Lower Olive Owner&#x27;s hand").And.Contain("Lower Robin Analyst&#x27;s hand").And.Contain("Clear all")
            .And.Contain("Olive Owner, hand raised").And.Contain("Robin Analyst, hand raised");
        people.IndexOf("Lower Olive Owner", StringComparison.Ordinal).Should().BeLessThan(people.IndexOf("Lower Robin Analyst", StringComparison.Ordinal), "first up, first shown");
        people.Should().NotContain("Raise hand", "the presenter has no hand of their own");
        FooterHasNoneOfIt(html);
        Footer(html).Should().Contain("Lock to me", "the presenter's other controls stay in the footer");

        // A follower: whose hands are up, in order, and their own as Lower hand.
        await Do(owner, p => p.Follow());
        html = await owner.Html();
        people = People(html);
        people.Should().Contain("Lower hand").And.Contain("aria-pressed=\"true\"").And.Contain("Hands up, in order").And.NotContain("&#x27;s hand\"", "only the presenter lowers another's hand");
        people.IndexOf("Olive Owner</span>", StringComparison.Ordinal).Should().BeLessThan(people.IndexOf("Robin Analyst</span>", StringComparison.Ordinal));
        FooterHasNoneOfIt(html);

        // Olive lowers hers; the presenter lowers Robin's from the top bar.
        await Do(owner, p => p.ToggleHand());
        Meetings.Get(day)!.Hands.Select(h => h.Name).Should().Equal("Robin Analyst");
        await Do(pat, p => p.ClearHand("dev:topbar-robin"));
        Meetings.Get(day)!.Hands.Should().BeEmpty();
        await Do(robin, p => p.Follow());
        People(await robin.Html()).Should().Contain("Raise hand").And.NotContain("tide-hands");

        await Do(pat, p => p.EndMeeting());
    }

    [Fact]
    public async Task WithNoMeeting_WhoElseHasTheBriefOpen_ShowsInTheTopBar_NotTheFooter()
    {
        var day = await TwoArticleBrief(app, 182, "Top bar no meeting");
        await using var pat = await Open(app, day);
        await using var robin = await Open(app, day, Robin);
        await Do(robin, _ => { });
        var html = await robin.Html();
        People(html).Should().Contain("tide-presence-dot").And.Contain("Tester is viewing");
        FooterHasNoneOfIt(html);
    }
}
