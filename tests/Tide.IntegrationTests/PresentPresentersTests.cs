using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Briefs;
using Tide.Application.Org;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Web.Services;
using static Tide.IntegrationTests.ComponentHarness;
using static Tide.IntegrationTests.PresentHarness;

namespace Tide.IntegrationTests;

/// <summary>
/// Who ran a meeting is kept on its brief as it starts and changes hands, not only as it ends: a live meeting is in memory,
/// so one dropped once everyone left (or lost to a restart) and then ended from the brief page still names who ran it,
/// rather than the analyst the brief was given.
/// </summary>
public class PresentPresentersTests(MeetingClockApp app) : IClassFixture<MeetingClockApp>
{
    private static readonly SignIn Robin = new("dev:presenters-robin", "Robin Analyst", "Analyst");

    private LiveMeetings Meetings => app.Services.GetRequiredService<LiveMeetings>();

    private async Task<Brief> Read(DateOnly day)
    {
        await using var scope = SignedIn(app, SignIn.Analyst);
        return (await scope.ServiceProvider.GetRequiredService<BriefService>().GetAsync(day))!;
    }

    // The brief page's End the meeting (DistributePanel), with the meeting no longer live.
    private async Task EndFromTheBriefPage(DateOnly day, SignIn who)
    {
        var brief = await Read(day);
        await using var scope = SignedIn(app, who);
        await scope.ServiceProvider.GetRequiredService<BriefService>().MarkPresentedAsync(brief.Id);
    }

    // Gives the brief to someone else to present, so "Presented by" falling back to them would show.
    private async Task GiveTo(DateOnly day, string id, string name)
    {
        await using var scope = SignedIn(app, SignIn.Analyst);
        var db = scope.ServiceProvider.GetRequiredService<Tide.Application.Abstractions.IAppDbContextFactory>();
        await using var ctx = await db.CreateAsync();
        var brief = ctx.Briefs.Single(b => b.BriefDate == day);
        (brief.AnalystId, brief.AnalystName) = (id, name);
        await ctx.SaveChangesAsync();
    }

    private void DropTheEmptyMeeting(DateOnly day)
    {
        app.Clock.Now += LiveMeetings.Abandoned + TimeSpan.FromSeconds(1);
        Meetings.Sweep();
        Meetings.Get(day).Should().BeNull("nobody had been in it for the abandoned period");
    }

    [Fact]
    public async Task AMeetingStarted_ThenDroppedOnceEmpty_AndEndedFromTheBriefPage_NamesWhoStartedIt()
    {
        var day = await TwoArticleBrief(app, 91, "Presenters dropped");
        await GiveTo(day, "dev:presenters-rostered", "Rostered Analyst");
        var pat = await Open(app, day);
        await Do(pat, p => p.StartMeeting());
        (await Read(day)).PresentedByName.Should().Be("Tester", "kept as the meeting starts");

        await pat.DisposeAsync();   // everyone left, and nobody came back
        DropTheEmptyMeeting(day);
        await EndFromTheBriefPage(day, SignIn.Analyst);

        var brief = await Read(day);
        brief.Status.Should().Be(BriefStatus.Presented);
        BriefService.PresentedBy(brief).Should().Be("Tester", "who ran it, not the analyst the brief was given");
    }

    [Fact]
    public async Task ATakeOver_IsKeptOnTheBriefAtOnce_AndOutlastsTheMeeting()
    {
        var day = await TwoArticleBrief(app, 92, "Presenters taken over");
        await GiveTo(day, "dev:presenters-rostered", "Rostered Analyst");
        var pat = await Open(app, day);
        await Do(pat, p => p.StartMeeting());
        var robin = await Open(app, day, Robin);
        await Do(robin, p => p.JoinIfLive());

        await pat.DisposeAsync();   // the presenter's tab closed
        app.Clock.Now += LiveMeetings.Grace + TimeSpan.FromSeconds(1);
        await Do(robin, p => p.TakeOver());
        Meetings.Get(day)!.PresenterId.Should().Be(Robin.UserId);
        (await Read(day)).PresentedByName.Should().Be("Tester and Robin Analyst", "kept as it changed hands");

        await robin.DisposeAsync();
        DropTheEmptyMeeting(day);
        await EndFromTheBriefPage(day, Robin);
        BriefService.PresentedBy(await Read(day)).Should().Be("Tester and Robin Analyst");
    }

    [Fact]
    public async Task ATakeOver_AfterTheBriefPageEndedTheMeeting_IsStillKept()
    {
        var day = await TwoArticleBrief(app, 93, "Presenters after the end");
        var pat = await Open(app, day);
        await Do(pat, p => p.StartMeeting());
        var robin = await Open(app, day, Robin);
        await Do(robin, p => p.JoinIfLive());
        await EndFromTheBriefPage(day, SignIn.Analyst);   // marked presented while the meeting still runs
        (await Read(day)).PresentedByName.Should().Be("Tester");

        await pat.DisposeAsync();
        app.Clock.Now += LiveMeetings.Grace + TimeSpan.FromSeconds(1);
        await Do(robin, p => p.TakeOver());
        (await Read(day)).PresentedByName.Should().Be("Tester and Robin Analyst", "present mode's end doesn't mark it presented again, so it's kept as it changes hands");

        await robin.DisposeAsync();
        EndMeeting(app, day);
    }
}
