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

        // Olive, then Robin, raise a hand. The presenter's page hears each change on its own; it has heard Olive's before
        // Robin's hand goes up (on a loaded machine it could hear both at once, and say both names).
        await Do(owner, p => p.ToggleHand());
        People(await pat.HtmlOnce("role=\"status\">Olive Owner raised a hand.</span>"))
            .Should().Contain("role=\"status\">Olive Owner raised a hand.</span>");
        await Do(robin, p => p.ToggleHand());
        Meetings.Get(day)!.Hands.Select(h => h.Name).Should().Equal("Olive Owner", "Robin Analyst");

        // The presenter: the hands' count, their names in order, and (opened from the count) the list where each one is
        // theirs to lower, and Clear all.
        await Do(pat, p => p.Follow());
        html = await pat.Html();
        var people = People(html);
        // Said briefly, in a status region of its own; the roster is read when reached, not again at every change.
        people.Should().Contain("role=\"status\">Robin Analyst raised a hand.</span>")
            .And.NotContain("tide-presence-sm\" role=\"status\"");
        people.Should().Contain("aria-label=\"2 hands up: Olive Owner, Robin Analyst\"").And.Contain("<span class=\"tide-hands-n\">2</span>")
            .And.Contain("Olive Owner, hand raised").And.Contain("Robin Analyst, hand raised").And.Contain("aria-expanded=\"false\"")
            .And.NotContain("present-hands\" class", "the list is shut until asked for").And.NotContain("Clear all");
        people.IndexOf("<span>Olive Owner</span>", StringComparison.Ordinal).Should().BeLessThan(people.IndexOf("<span>Robin Analyst</span>", StringComparison.Ordinal), "first up, first shown");
        people.Should().NotContain("Raise hand", "the presenter has no hand of their own");
        await Do(pat, p => p.ToggleHandsList());
        people = People(await pat.Html());
        people.Should().Contain("id=\"present-hands\"").And.NotContain("Lower", "the presenter lowers hands from the rail");
        people.IndexOf(">Olive Owner</span>", StringComparison.Ordinal).Should().BeLessThan(people.IndexOf(">Robin Analyst</span>", StringComparison.Ordinal), "first up, first shown");
        html = await pat.Html();
        html.Should().Contain("Lower Olive Owner&#x27;s hand").And.Contain("Lower Robin Analyst&#x27;s hand").And.Contain("Clear all");
        html.IndexOf("Lower Olive Owner", StringComparison.Ordinal).Should().BeLessThan(html.IndexOf("Lower Robin Analyst", StringComparison.Ordinal), "first up, first shown");
        await Do(pat, p => p.OnPresentKey("close"));
        People(await pat.Html()).Should().NotContain("id=\"present-hands\"", "Esc shuts it");
        FooterHasNoneOfIt(html);
        Footer(html).Should().NotContain("Lock to me", "the presenter's other controls are in the rail");

        // A follower: whose hands are up, in order, and their own as Lower hand.
        await Do(owner, p => p.Follow());
        html = await owner.Html();
        people = People(html);
        people.Should().Contain("Lower hand").And.Contain("aria-pressed=\"true\"").And.Contain("2 hands up: Olive Owner, Robin Analyst");
        await Do(owner, p => p.ToggleHandsList());
        people = People(await owner.Html());
        people.Should().Contain("Hands up, in order").And.NotContain("&#x27;s hand\"", "only the presenter lowers another's hand").And.NotContain("Clear all");
        people.IndexOf("Olive Owner</span>", StringComparison.Ordinal).Should().BeLessThan(people.IndexOf("Robin Analyst</span>", StringComparison.Ordinal));
        FooterHasNoneOfIt(html);

        // Olive lowers hers; the presenter lowers Robin's from the top bar.
        await Do(owner, p => p.ToggleHand());
        Meetings.Get(day)!.Hands.Select(h => h.Name).Should().Equal("Robin Analyst");
        await Do(pat, p => p.ClearHand("dev:topbar-robin"));
        Meetings.Get(day)!.Hands.Should().BeEmpty();
        await Do(robin, p => p.Follow());
        People(await robin.Html()).Should().Contain("Raise hand").And.NotContain("tide-hands");
        People(await owner.Html()).Should().NotContain("present-hands", "no hands, no list");

        await Do(pat, p => p.EndMeeting());
    }

    [Fact]
    public async Task TheListOfHands_ShutsWhenTheLastHandGoesDown_AndDoesntOpenByItselfForTheNext()
    {
        var day = await TwoArticleBrief(app, 184, "Top bar list");
        await using var pat = await Open(app, day);
        await Do(pat, p => p.StartMeeting());
        await using var owner = await Open(app, day, Owner);
        await Do(owner, p => p.JoinIfLive());
        await using var robin = await Open(app, day, Robin);
        await Do(robin, p => p.JoinIfLive());

        await Do(owner, p => p.ToggleHand());
        await Do(pat, p => p.Follow());
        await Do(pat, p => p.ToggleHandsList());
        People(await pat.Html()).Should().Contain("id=\"present-hands\"");

        await Do(owner, p => p.ToggleHand());
        await Do(pat, p => p.Follow());
        People(await pat.Html()).Should().NotContain("present-hands");
        await Do(robin, p => p.ToggleHand());
        await Do(pat, p => p.Follow());
        var people = People(await pat.Html());
        people.Should().Contain("1 hand up: Robin Analyst").And.Contain("aria-expanded=\"false\"").And.NotContain("id=\"present-hands\"", "it opens only when asked");

        await Do(pat, p => p.EndMeeting());
    }

    [Fact]
    public async Task EightInTheMeeting_FiveDotsAndAPlusThree_AndTheCountSaysEight_WhileTheStatusNamesEveryone()
    {
        var day = await TwoArticleBrief(app, 183, "Top bar eight");
        await using var pat = await Open(app, day);
        await Do(pat, p => p.StartMeeting());
        var live = Meetings.Get(day)!;
        var seats = Enumerable.Range(1, 7).Select(n => Meetings.Join(day, $"dev:eight-{n}", $"Person {n}", false, "", Guid.NewGuid(), live.Slide)!).ToList();
        try
        {
            await Do(pat, p => p.Follow());
            var people = People(await pat.Html());
            people.Should().Contain("class=\"tide-presence-dot ", Exactly.Times(6), "five people and \"+3\"");
            people.Should().Contain(">&#x2B;3</span>").And.Contain("8 here").And.Contain("Person 7.");
        }
        finally
        {
            foreach (var s in seats) s.Dispose();
            await Do(pat, p => p.EndMeeting());
        }
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
