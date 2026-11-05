using System.Net;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Articles;
using Tide.Application.Briefs;
using Tide.Application.Org;
using Tide.Domain.Enums;
using Tide.Web.Components.Pages;
using Tide.Web.Services;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>Whom a page follows in a live meeting: a pill among the present header's tools, never a bar of its own.</summary>
public class PresentFollowPillTests(MeetingClockApp app) : IClassFixture<MeetingClockApp>
{
    private static readonly SignIn Owner = new("dev:pill-owner", "Olive Owner", "ActionOwner");
    private static readonly SignIn Robin = new("dev:pill-robin", "Robin Analyst", "Analyst");

    private DateOnly Today => app.Services.GetRequiredService<OrgClock>().CurrentBriefDate;
    private LiveMeetings Meetings => app.Services.GetRequiredService<LiveMeetings>();

    private static DateOnly BusinessDaysBefore(DateOnly day, int n)
    {
        for (var i = 0; i < n; i++) day = WorkingDays.Weekdays.Previous(day);
        return day;
    }

    private async Task<DateOnly> Brief(int daysBack)
    {
        var day = BusinessDaysBefore(Today, daysBack);
        await using var scope = SignedIn(app, SignIn.Analyst);
        var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
        var briefs = scope.ServiceProvider.GetRequiredService<BriefService>();
        var brief = await briefs.GetOrStartAsync(day);
        await briefs.AddArticleAsync(brief.Id, await articles.CreateAsync($"Pill first {daysBack}", "Vendor", null, TlpLevel.Clear));
        await briefs.AddArticleAsync(brief.Id, await articles.CreateAsync($"Pill second {daysBack}", "Vendor", null, TlpLevel.Clear));
        return day;
    }

    private static Task Do(Rendered<Present> r, Func<Present, Task> what) =>
        r.Click(async p =>
        {
            await what(p);
            await p.OnPresentKey("render");
        });

    private static Task Do(Rendered<Present> r, Action<Present> what) => Do(r, p =>
    {
        what(p);
        return Task.CompletedTask;
    });

    private void EndMeeting(DateOnly day) => Meetings.End(Meetings.Join(day, "dev:tester", "Tester", true, "", Guid.NewGuid(), 0)!);

    private Task<Rendered<Present>> Open(DateOnly day, SignIn? who = null) =>
        RenderAsync<Present>(app, $"/present/{day:yyyy-MM-dd}", new() { ["Day"] = $"{day:yyyy-MM-dd}" }, who);

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
        var day = await Brief(101);
        await using var pat = await Open(day);
        await Do(pat, p => p.StartMeeting());
        await pat.Click(p => p.OnPresentKey("next"));
        await pat.Click(p => p.OnPresentKey("next"));
        (await pat.Html()).Should().NotContain("class=\"tide-follow ", "the presenter has no pill");

        // Joining late lands on the presenter's slide, following.
        await using var owner = await Open(day, Owner);
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
        EndMeeting(day);
    }

    [Fact]
    public async Task WhenLocked_ThePillSaysSo()
    {
        var day = await Brief(102);
        await using var pat = await Open(day);
        await Do(pat, p => p.StartMeeting());
        await using var owner = await Open(day, Owner);
        await Do(owner, p => p.JoinIfLive());
        await Do(pat, p => p.ToggleLock());
        await Do(owner, p => p.Follow());

        var html = await owner.Html();
        Markup(html).Should().Contain("is-held").And.Contain("bi-lock-fill");
        Shown(html).Should().Be("Following Tester · locked");
        Heard(html).Should().Be("Following Tester · locked by the presenter");
        EndMeeting(day);
    }

    [Fact]
    public async Task WhenThePresenterGoes_ThePillSaysTheyreReconnecting_ThenOffersTheTakeOver_ToThoseWhoCanPresent()
    {
        var day = await Brief(103);
        var pat = await Open(day);
        await Do(pat, p => p.StartMeeting());
        await using var owner = await Open(day, Owner);
        await Do(owner, p => p.JoinIfLive());
        await using var robin = await Open(day, Robin);
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
        var day = await Brief(104);
        var pat = await Open(day);
        await Do(pat, p => p.StartMeeting());
        await using var viewer = await Open(day, new SignIn("dev:pill-viewer", "Val Viewer", "Viewer"));
        await Do(viewer, p => p.JoinIfLive());
        await using var visitor = await Open(day, new SignIn("dev:pill-visitor", "Vic Visitor", "Visitor"));
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
        EndMeeting(day);
    }
}
