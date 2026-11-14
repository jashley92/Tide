using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Articles;
using Tide.Application.Briefs;
using Tide.Application.Org;
using Tide.Domain.Enums;
using Tide.Web.Services;
using static Tide.IntegrationTests.ComponentHarness;
using static Tide.IntegrationTests.PresentHarness;

namespace Tide.IntegrationTests;

/// <summary>The app on a Saturday: Today shows Monday's brief.</summary>
public sealed class SaturdayApp : TideApp
{
    // Saturday 14 November 2026, 11:00 in New York.
    protected override TimeProvider? Time { get; } = new TestClock(new DateTimeOffset(2026, 11, 14, 11, 0, 0, TimeSpan.FromHours(-5)));
}

/// <summary>
/// A meeting isn't held before its brief's day. On a weekend Today shows Monday's brief, and present mode says when its
/// meeting can start rather than offering Start and refusing it: Start is disabled with the day beside it, and there's no
/// End to press. It can still be rehearsed, as today's brief.
/// </summary>
public class PresentBeforeItsDayTests(SaturdayApp app) : IClassFixture<SaturdayApp>
{
    private static readonly DateOnly Monday = new(2026, 11, 16);

    [Fact]
    public async Task OnAWeekend_MondaysMeeting_SaysWhenItCanStart_AndNothingStartsOrEnds()
    {
        app.Services.GetRequiredService<OrgClock>().CurrentBriefDate.Should().Be(Monday, "Today shows the next working day's brief");
        await using (var scope = SignedIn(app, SignIn.Analyst))
        {
            var briefs = scope.ServiceProvider.GetRequiredService<BriefService>();
            var brief = await briefs.GetOrStartAsync(Monday);
            await briefs.AddArticleAsync(brief.Id, await scope.ServiceProvider.GetRequiredService<ArticleService>()
                .CreateAsync("Weekend prepared article", "Vendor", null, TlpLevel.Clear));
        }

        await using var r = await Open(app, Monday);
        var html = await r.Html();
        html.Should().MatchRegex("<button id=\"present-start\"[^>]* disabled[^>]*aria-describedby=\"present-start-when\"",
                "Start is there, disabled, and says why")
            .And.Contain("<span id=\"present-start-when\" class=\"tide-present-help\">The meeting can start on Mon Nov 16, the brief&#x27;s day.</span>")
            .And.NotContain("> End the meeting</button>", "there's no meeting to end, and the brief can't be marked presented yet");

        await Do(r, p => p.StartMeeting());
        app.Services.GetRequiredService<LiveMeetings>().Get(Monday).Should().BeNull("nothing started");
        (await r.Html()).Should().NotContain("can't be held before");

        await using var rehearsal = await Rehearse(app, Monday, SignIn.Analyst);
        (await rehearsal.Html()).Should().NotContain("Only today's brief can be rehearsed.", "Monday's is today's brief on a weekend")
            .And.Contain("Rehearsal: nothing here is shared or saved");
    }

    [Theory]
    [InlineData(2026, 11, 14, 2)]   // Saturday: Monday
    [InlineData(2026, 11, 15, 1)]   // Sunday: Monday
    [InlineData(2026, 11, 16, 0)]   // Monday: as it is
    [InlineData(2026, 11, 13, 0)]   // Friday: as it is
    public void TheLayoutChecksDemoClock_IsAWorkingDays_TheTimeOfDayKept(int year, int month, int day, int days)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        // 11 PM in New York: already the next day in UTC, which mustn't decide it.
        var now = new DateTimeOffset(year, month, day, 23, 0, 0, TimeSpan.FromHours(-5));
        var clock = new DemoWorkingDayClock(zone, new TestClock(now));
        clock.Days.Should().Be(days);
        clock.GetUtcNow().Should().Be(now.AddDays(days));
    }
}
