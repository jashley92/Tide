using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Articles;
using Tide.Application.Briefs;
using Tide.Application.Org;
using Tide.Domain.Enums;
using Tide.Web.Components.Pages;
using Tide.Web.Services;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>The present page's tests' shared steps: open it, do what a button would, and end the meeting after.</summary>
public static class PresentHarness
{
    /// <summary>Opens present mode for <paramref name="day"/>, signed in as <paramref name="who"/> (the tester if none), in a
    /// window wide enough to dock the presenter's rail (present.js says so in a browser): a presenter's page has its rail
    /// open beside the slide, as it would be on a meeting-room screen.</summary>
    public static async Task<Rendered<Present>> Open(TideApp app, DateOnly day, SignIn? who = null)
    {
        var r = await RenderAsync<Present>(app, $"/present/{day:yyyy-MM-dd}", new() { ["Day"] = $"{day:yyyy-MM-dd}" }, who);
        await r.Click(p => p.OnRailRoom(true));
        return r;
    }

    /// <summary>Opens present mode for <paramref name="day"/> in rehearsal (Help's Learn to present), signed in as
    /// <paramref name="who"/>.</summary>
    public static Task<Rendered<Present>> Rehearse(TideApp app, DateOnly day, SignIn who) =>
        RenderAsync<Present>(app, $"/present/{day:yyyy-MM-dd}?rehearse=1", new() { ["Day"] = $"{day:yyyy-MM-dd}" }, who);

    /// <summary>Does what a button would and then renders, as a click does (the page doesn't hear its own meeting
    /// changes, which is what used to render it after them).</summary>
    public static Task Do(Rendered<Present> r, Func<Present, Task> what) =>
        r.Click(async p =>
        {
            await what(p);
            await p.OnPresentKey("render");   // no key of its own: only renders
        });

    public static Task Do(Rendered<Present> r, Action<Present> what) => Do(r, p =>
    {
        what(p);
        return Task.CompletedTask;
    });

    /// <summary>Ends a test's meeting, if it's still going on, as its presenter rejoining would.</summary>
    public static void EndMeeting(TideApp app, DateOnly day)
    {
        var meetings = app.Services.GetRequiredService<LiveMeetings>();
        if (meetings.Get(day) is { } live) meetings.End(meetings.Join(day, live.PresenterId, "Someone", true, "", Guid.NewGuid(), 0)!);
    }

    /// <summary>A brief <paramref name="daysBack"/> working days before the brief day, with two articles titled
    /// "<paramref name="titled"/> first" and "… second".</summary>
    public static async Task<DateOnly> TwoArticleBrief(TideApp app, int daysBack, string titled)
    {
        var day = app.Services.GetRequiredService<OrgClock>().CurrentBriefDate;
        for (var i = 0; i < daysBack; i++) day = WorkingDays.Weekdays.Previous(day);
        await using var scope = SignedIn(app, SignIn.Analyst);
        var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
        var briefs = scope.ServiceProvider.GetRequiredService<BriefService>();
        var brief = await briefs.GetOrStartAsync(day);
        await briefs.AddArticleAsync(brief.Id, await articles.CreateAsync($"{titled} first {daysBack}", "Vendor", null, TlpLevel.Clear));
        await briefs.AddArticleAsync(brief.Id, await articles.CreateAsync($"{titled} second {daysBack}", "Vendor", null, TlpLevel.Clear));
        return day;
    }
}
