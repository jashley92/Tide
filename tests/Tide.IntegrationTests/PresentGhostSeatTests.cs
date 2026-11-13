using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Abstractions;
using Tide.Web.Components.Pages;
using Tide.Web.Services;
using static Tide.IntegrationTests.ComponentHarness;
using static Tide.IntegrationTests.PresentHarness;

namespace Tide.IntegrationTests;

/// <summary>
/// A present-mode page whose browser went before the meeting started (a closed tab, a killed browser) keeps its circuit
/// for minutes in case it comes back. When the meeting starts it isn't seated, recorded as attending or shown as here:
/// only pages connected at the time are. One whose connection comes back joins then, as anyone does.
/// </summary>
public class PresentGhostSeatTests(TideApp app) : IClassFixture<TideApp>
{
    private static readonly SignIn Xavier = new("dev:ghost-xavier", "Xavier Ghost", "Viewer");
    private static readonly SignIn Jordan = new("dev:ghost-jordan", "Jordan Follower", "Viewer");

    private LiveMeetings Meetings => app.Services.GetRequiredService<LiveMeetings>();

    private static PresenceCircuit CircuitOf(Rendered<Present> page, string id)
    {
        var circuit = page.Scope.ServiceProvider.GetRequiredService<PresenceCircuit>();
        circuit.Opened(id);
        return circuit;
    }

    // Who the meeting record and the audit log say attended, by user id.
    private async Task<(List<string> Recorded, List<string> Audited)> Record(Guid brief)
    {
        await using var scope = SignedIn(app, SignIn.Analyst);
        await using var db = await scope.ServiceProvider.GetRequiredService<IAppDbContextFactory>().CreateAsync();
        var recorded = await db.MeetingAttendees.Where(a => a.BriefId == brief).Select(a => a.UserId).ToListAsync();
        var audited = await db.AuditLog.Where(e => e.EntityType == "MeetingAttendee" && e.Action == "Added" && e.ChangesJson!.Contains(brief.ToString()))
            .Select(e => e.UserId).ToListAsync();
        return (recorded, audited);
    }

    private async Task<Guid> BriefOf(DateOnly day)
    {
        await using var scope = SignedIn(app, SignIn.Analyst);
        return (await scope.ServiceProvider.GetRequiredService<Tide.Application.Briefs.BriefService>().GetAsync(day))!.Id;
    }

    // Before the start: X's page opens, then its browser goes as `gone` says. Jordan's is open and connected. Then the
    // presenter starts, and every page hears it (each one's own Follow, as the meeting's change would run it).
    private async Task GhostThenStart(DateOnly day, Action<PresenceCircuit, string> gone)
    {
        await using var xavier = await Open(app, day, Xavier);
        var circuit = CircuitOf(xavier, "ghost-x-" + day.DayNumber);
        await using var jordan = await Open(app, day, Jordan);
        CircuitOf(jordan, "ghost-jordan-" + day.DayNumber);
        gone(circuit, "ghost-x-" + day.DayNumber);

        await using var pat = await Open(app, day);
        await Do(pat, p => p.StartMeeting());
        await Do(jordan, p => p.JoinIfLive());
        await Do(xavier, p => p.JoinIfLive());   // the page is still there, its browser isn't

        Meetings.Get(day)!.People.Select(p => p.UserId).Should().BeEquivalentTo([SignIn.Analyst.UserId, Jordan.UserId], "only pages connected at the start");
        var (recorded, audited) = await Record(await BriefOf(day));
        recorded.Should().BeEquivalentTo([SignIn.Analyst.UserId, Jordan.UserId], "a page still connected at the start is recorded, as before");
        audited.Should().NotContain(Xavier.UserId);
        await Do(pat, p => p.Follow());
        (await pat.Html()).Should().NotContain("Xavier Ghost").And.Contain("2 here");
        await Do(jordan, p => p.Follow());
        (await jordan.Html()).Should().NotContain("Xavier Ghost");
        EndMeeting(app, day);
    }

    [Fact]
    public async Task APageClosedBeforeTheStart_IsNeverSeatedRecordedOrShown()
    {
        // A tab closed: its circuit's connection goes down (PresenceCircuit.OnConnectionDownAsync), the circuit kept.
        var day = await TwoArticleBrief(app, 501, "Ghost");
        await GhostThenStart(day, (circuit, id) => circuit.Down(id));
    }

    [Fact]
    public async Task APageWhoseBrowserWasKilledBeforeTheStart_IsNeverSeatedRecordedOrShown()
    {
        var day = await TwoArticleBrief(app, 502, "Ghost");
        await GhostThenStart(day, (_, id) => Meetings.SetConnected(id, false));
    }

    [Fact]
    public async Task APageWhoseConnectionComesBack_JoinsTheMeetingThen()
    {
        var day = await TwoArticleBrief(app, 503, "Ghost");
        await using var xavier = await Open(app, day, Xavier);
        var circuit = CircuitOf(xavier, "ghost-back");
        circuit.Down("ghost-back");
        await using var pat = await Open(app, day);
        await Do(pat, p => p.StartMeeting());
        await Do(xavier, p => p.JoinIfLive());
        Meetings.Get(day)!.People.Should().NotContain(p => p.UserId == Xavier.UserId);

        circuit.Up("ghost-back");
        await Do(xavier, p => p.JoinIfLive());
        Meetings.Get(day)!.People.Should().Contain(p => p.UserId == Xavier.UserId, "back: here, and seated as anyone joining");
        (await Record(await BriefOf(day))).Recorded.Should().Contain(Xavier.UserId);
        EndMeeting(app, day);
    }
}
