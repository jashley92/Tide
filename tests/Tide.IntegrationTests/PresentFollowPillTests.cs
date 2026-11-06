using System.Net;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Tide.Web.Components.Pages;
using Tide.Web.Services;
using static Tide.IntegrationTests.ComponentHarness;
using static Tide.IntegrationTests.PresentHarness;

namespace Tide.IntegrationTests;

/// <summary>Whom a page follows in a live meeting: a pill among the present header's tools, never a bar of its own.</summary>
public class PresentFollowPillTests(MeetingClockApp app) : IClassFixture<MeetingClockApp>
{
    private static readonly SignIn Owner = new("dev:pill-owner", "Olive Owner", "ActionOwner");
    private static readonly SignIn Robin = new("dev:pill-robin", "Robin Analyst", "Analyst");

    private LiveMeetings Meetings => app.Services.GetRequiredService<LiveMeetings>();

    /// <summary>The pill's markup: inside the header's tools, with no full-width bar anywhere.</summary>
    internal static string Markup(string html)
    {
        html.Should().NotContain("tide-meeting-bar", "the old bar is gone");
        var tools = html.IndexOf("<div class=\"tide-present-tools\">", StringComparison.Ordinal);
        var at = html.IndexOf("<div class=\"tide-follow ", StringComparison.Ordinal);
        at.Should().BeGreaterThan(tools, "the pill sits among the header's tools").And.BeLessThan(html.IndexOf("</header>", StringComparison.Ordinal));
        return html[at..(html.IndexOf("</div>", at, StringComparison.Ordinal) + 6)];
    }

    /// <summary>What the pill says on screen (the screen-reader-only words left out), on one line.</summary>
    internal static string Shown(string html) => Words(Regex.Replace(Markup(html), "<span class=\"visually-hidden\">[^<]*</span>", ""));

    /// <summary>What a screen reader hears of it.</summary>
    internal static string Heard(string html) => Words(Markup(html));

    private static string Words(string markup) =>
        Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(markup, "<[^>]+>", " ")), @"\s+", " ").Trim();

    [Fact]
    public async Task ALateJoiner_Follows_ThenBrowsesAway_AndIsBroughtBack()
    {
        var day = await TwoArticleBrief(app, 101, "Pill");
        await using var pat = await Open(app, day);
        await Do(pat, p => p.StartMeeting());
        await pat.Click(p => p.OnPresentKey("next"));
        await pat.Click(p => p.OnPresentKey("next"));
        (await pat.Html()).Should().NotContain("class=\"tide-follow ", "the presenter has no pill");

        // Joining late lands on the presenter's slide, following.
        await using var owner = await Open(app, day, Owner);
        await Do(owner, p => p.JoinIfLive());
        var html = await owner.Html();
        Markup(html).Should().Contain("class=\"tide-follow is-following\" role=\"status\"");
        Shown(html).Should().Be("Following Tester");
        html.Should().Contain("aria-label=\"Slide 3 of");

        await owner.Click(p => p.OnPresentKey("next"));
        html = await owner.Html();
        Markup(html).Should().Contain("is-away").And.Contain("<button class=\"btn btn-sm btn-primary tide-follow-btn\"");
        Shown(html).Should().Be("Tester is on slide 3 · Back to Tester");

        await Do(owner, p => p.BackToPresenter());
        html = await owner.Html();
        Shown(html).Should().Be("Following Tester");
        html.Should().Contain("aria-label=\"Slide 3 of", "back on the presenter's slide");
        EndMeeting(app, day);
    }

    [Fact]
    public async Task WhenLocked_ThePillSaysSo()
    {
        var day = await TwoArticleBrief(app, 102, "Pill");
        await using var pat = await Open(app, day);
        await Do(pat, p => p.StartMeeting());
        await using var owner = await Open(app, day, Owner);
        await Do(owner, p => p.JoinIfLive());
        await Do(pat, p => p.ToggleLock());
        await Do(owner, p => p.Follow());

        var html = await owner.Html();
        Markup(html).Should().Contain("is-held").And.Contain("bi-lock-fill");
        Shown(html).Should().Be("Following Tester · locked");
        Heard(html).Should().Be("Following Tester · locked by the presenter");
        EndMeeting(app, day);
    }

    [Fact]
    public async Task WhenThePresenterGoes_ThePillSaysTheyreReconnecting_ThenOffersTheTakeOver_ToThoseWhoCanPresent()
    {
        var day = await TwoArticleBrief(app, 103, "Pill");
        var pat = await Open(app, day);
        await Do(pat, p => p.StartMeeting());
        await using var owner = await Open(app, day, Owner);
        await Do(owner, p => p.JoinIfLive());
        await using var robin = await Open(app, day, Robin);
        await Do(robin, p => p.JoinIfLive());

        await pat.DisposeAsync();   // the presenter's tab closed
        await Do(robin, p => p.Follow());
        var html = await robin.Html();
        Markup(html).Should().Contain("is-reconnecting");
        Shown(html).Should().Be("Tester is reconnecting…");

        app.Clock.Now += LiveMeetings.Grace + TimeSpan.FromSeconds(1);
        await Do(robin, p => p.Follow());
        html = await robin.Html();
        Markup(html).Should().Contain("is-dropped");
        Shown(html).Should().Be("Tester dropped out · Take over");

        await Do(owner, p => p.Follow());
        Shown(await owner.Html()).Should().Be("Tester dropped out", "someone who can't present has nothing to take over");

        await Do(robin, p => p.TakeOver());
        (await robin.Html()).Should().NotContain("class=\"tide-follow ", "presenting now");
        await Do(owner, p => p.Follow());
        Shown(await owner.Html()).Should().Be("Following Robin Analyst");
        Meetings.End(Meetings.Join(day, Robin.UserId, Robin.Name, true, "", Guid.NewGuid(), 0)!);
    }

    [Fact]
    public async Task AViewerAndAVisitor_FollowWithThePill_AndAreNeverOfferedTheTakeOver()
    {
        var day = await TwoArticleBrief(app, 104, "Pill");
        var pat = await Open(app, day);
        await Do(pat, p => p.StartMeeting());
        await using var viewer = await Open(app, day, new SignIn("dev:pill-viewer", "Val Viewer", "Viewer"));
        await Do(viewer, p => p.JoinIfLive());
        await using var visitor = await Open(app, day, new SignIn("dev:pill-visitor", "Vic Visitor", "Visitor"));
        await Do(visitor, p => p.GoLive());
        Meetings.Get(day)!.People.Select(p => p.Name).Should().Contain(["Val Viewer", "Vic Visitor"]);
        foreach (var r in new[] { viewer, visitor })
        {
            Shown(await r.Html()).Should().Be("Following Tester");
            await r.Click(p => p.OnPresentKey("next"));
            Shown(await r.Html()).Should().Be("Tester is on slide 1 · Back to Tester");
            await Do(r, p => p.BackToPresenter());
        }

        await pat.DisposeAsync();
        app.Clock.Now += LiveMeetings.Grace + TimeSpan.FromSeconds(1);
        foreach (var r in new[] { viewer, visitor })
        {
            await Do(r, p => p.Follow());
            var html = await r.Html();
            Shown(html).Should().Be("Tester dropped out");
            html.Should().NotContain("Take over");
        }
        EndMeeting(app, day);
    }
}
