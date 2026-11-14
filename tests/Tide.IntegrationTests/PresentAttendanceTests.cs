using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Briefs;
using Tide.Application.Org;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Web.Components.Pages;
using Tide.Web.Services;
using static Tide.IntegrationTests.ComponentHarness;
using static Tide.IntegrationTests.PresentHarness;

namespace Tide.IntegrationTests;

/// <summary>
/// Who attended, as everyone in the meeting adds to it: the presenter from their rail, anyone else on the team edition
/// following from the wrap-up slide (never a Visitor), each taking off only the names they added, the presenter anyone,
/// joined or added.
/// Every page in the meeting sees the list change at once.
/// </summary>
public class PresentAttendanceTests(TideApp app) : IClassFixture<TideApp>
{
    private static readonly SignIn Viewer = new("dev:attend-val", "Val Viewer", "Viewer");
    private static readonly SignIn Owner = new("dev:attend-olive", "Olive Owner", "ActionOwner");
    private static readonly SignIn Visitor = new("dev:attend-vic", "Vic Visitor", "Visitor");

    private const string Box = "id=\"wrap-add-name\"";

    private LiveMeetings Meetings => app.Services.GetRequiredService<LiveMeetings>();

    private async Task<IReadOnlyList<MeetingAttendee>> Attendees(Guid brief)
    {
        await using var scope = SignedIn(app, SignIn.Analyst);
        return (await scope.ServiceProvider.GetRequiredService<MeetingService>().GetAsync(brief)).Attendees;
    }

    private async Task<Guid> BriefOf(DateOnly day)
    {
        await using var scope = SignedIn(app, SignIn.Analyst);
        return (await scope.ServiceProvider.GetRequiredService<BriefService>().GetAsync(day))!.Id;
    }

    // The presenter starts the meeting and goes to the wrap-up; each of the others joins and follows them there.
    private static async Task ToWrapUp(Rendered<Present> presenter, params Rendered<Present>[] others)
    {
        await Do(presenter, p => p.StartMeeting());
        foreach (var r in others) await Do(r, p => p.GoLive());
        await presenter.Click(p => p.OnPresentKey("last"));
        foreach (var r in others) await Do(r, p => p.Follow());
    }

    [Fact]
    public async Task AFollower_AddsSomeoneOnTheWrapUp_AndEveryPageInTheMeetingSeesIt()
    {
        var day = await TwoArticleBrief(app, 401, "Attendance");
        var brief = await BriefOf(day);
        await using var pat = await Open(app, day);
        await using var val = await Open(app, day, Viewer);
        await using var olive = await Open(app, day, Owner);
        await using var vic = await Open(app, day, Visitor);
        await ToWrapUp(pat, val, olive, vic);

        var html = await val.Html();
        html.Should().Contain("Attended (").And.Contain(Box).And.Contain("aria-label=\"Add someone who was here\"");
        (await olive.Html()).Should().Contain(Box, "an action owner following adds too");
        (await vic.Html()).Should().Contain("Attended (").And.NotContain(Box, "never a Visitor");
        (await pat.Html()).Should().NotContain(Box, "the presenter adds from their rail").And.Contain("Someone in the room");

        await Do(val, p => p.AddAttendeeName = "Sam from the SOC");
        await Do(val, p => p.AddAttendee());
        (await val.Html()).Should().Contain("Sam from the SOC").And.Contain("aria-label=\"Remove Sam from the SOC\"");
        val.Component.AddAttendeeName.Should().BeNull("the box empties once it's added");
        (await pat.HtmlOnce("Sam from the SOC")).Should().Contain("aria-label=\"Remove Sam from the SOC\"", "the presenter takes off anyone added");
        (await olive.HtmlOnce("Sam from the SOC")).Should().NotContain("Remove Sam from the SOC", "only whoever added it, or the presenter");
        (await vic.HtmlOnce("Sam from the SOC")).Should().NotContain("Remove Sam from the SOC");
        // Someone who joined: the presenter can take them off (a mistaken join); a follower can't.
        (await pat.Html()).Should().Contain("aria-label=\"Remove Olive Owner\"", "the presenter takes off anyone, joined too");
        (await val.Html()).Should().NotContain("Remove Olive Owner", "a follower takes off only whom they added");

        // Olive's, forged, takes off nothing (the service refuses it too: MeetingServiceTests).
        var sam = (await Attendees(brief)).Single(a => a.Name == "Sam from the SOC");
        await Do(olive, p => p.RemoveAttendee(sam));
        (await Attendees(brief)).Should().Contain(a => a.Name == "Sam from the SOC");

        await Do(val, p => p.RemoveAttendee(sam));
        (await Attendees(brief)).Should().NotContain(a => a.Name == "Sam from the SOC");
        (await pat.Until(p => !p.AttendanceNames.Contains("Sam from the SOC"))).Should().BeTrue("the others see it go at once");
        EndMeeting(app, day);
    }

    [Fact]
    public async Task AVisitor_GetsNoBox_AndAForgedAddChangesNothing()
    {
        var day = await TwoArticleBrief(app, 402, "Attendance");
        var brief = await BriefOf(day);
        await using var pat = await Open(app, day);
        await using var vic = await Open(app, day, Visitor);
        await ToWrapUp(pat, vic);

        (await vic.Html()).Should().NotContain(Box);
        await Do(vic, p => p.AddAttendeeName = "Forged Person");
        await Do(vic, p => p.AddAttendee());
        (await Attendees(brief)).Should().NotContain(a => a.Name == "Forged Person");
        EndMeeting(app, day);
    }

    [Fact]
    public async Task OnTheCompanyWideEdition_NoOneSeesAttendance()
    {
        var day = await TwoArticleBrief(app, 403, "Attendance");
        await using var pat = await Open(app, day);
        await using var val = await Open(app, day, Viewer);
        await ToWrapUp(pat, val);
        (await val.Html()).Should().Contain(Box);

        await Do(val, p => p.SetEdition(new ChangeEventArgs { Value = nameof(Edition.Public) }));
        await Do(val, p => p.OnPresentKey("last"));
        (await val.Html()).Should().NotContain("Attended (").And.NotContain(Box);
        EndMeeting(app, day);
    }

    [Fact]
    public async Task ARehearsal_HasNoBox_AndAddsNoOne()
    {
        var day = app.Services.GetRequiredService<OrgClock>().CurrentBriefDate;
        await TwoArticleBrief(app, 0, "Attendance rehearsal");
        var brief = await BriefOf(day);
        Meetings.Get(day).Should().BeNull();
        await using (var scope = SignedIn(app, SignIn.Analyst))
            await scope.ServiceProvider.GetRequiredService<MeetingService>().AddAttendeeAsync(brief, "Sam from the SOC");
        await using var pat = await Rehearse(app, day, SignIn.Analyst);
        await pat.Click(p => p.OnPresentKey("last"));
        var html = await pat.Html();
        html.Should().NotContain(Box).And.NotContain("Remove Sam from the SOC").And.NotContain("placeholder=\"Someone in the room\"");
        await Do(pat, p => p.AddAttendeeName = "Rehearsed Person");
        await Do(pat, p => p.AddAttendee());
        var sam = (await Attendees(brief)).Single(a => a.Name == "Sam from the SOC");
        await Do(pat, p => p.RemoveAttendee(sam));
        (await Attendees(brief)).Select(a => a.Name).Should().Equal("Sam from the SOC");
    }

    [Fact]
    public async Task WithNoMeetingLive_ThePresentersRail_StillAddsAndTakesOffNames()
    {
        var day = await TwoArticleBrief(app, 404, "Attendance");
        var brief = await BriefOf(day);
        await using (var scope = SignedIn(app, SignIn.Analyst))
            await scope.ServiceProvider.GetRequiredService<MeetingService>().AddAttendeeAsync(brief, "Sam from the SOC");
        await using var pat = await Open(app, day);
        await pat.Click(p => p.OnPresentKey("last"));
        (await pat.Html()).Should().Contain("placeholder=\"Someone in the room\"").And.Contain("aria-label=\"Remove Sam from the SOC\"").And.NotContain(Box);

        await Do(pat, p => p.AddAttendeeName = "Kim from IT");
        await Do(pat, p => p.AddAttendee());
        var sam = (await Attendees(brief)).Single(a => a.Name == "Sam from the SOC");
        await Do(pat, p => p.RemoveAttendee(sam));
        (await Attendees(brief)).Select(a => a.Name).Should().Equal("Kim from IT");
    }

    private static readonly SignIn Robin = new("dev:attend-robin", "Robin Analyst", "Analyst");

    [Fact]
    public async Task AfterTheMeeting_WhoeverPresentsCorrectsWhoAttended_AndAFollowersPageStaysAFollowers()
    {
        var day = await TwoArticleBrief(app, 405, "Attendance");
        var brief = await BriefOf(day);
        await using var pat = await Open(app, day);
        await using var val = await Open(app, day, Viewer);
        await using var robin = await Open(app, day, Robin);
        await ToWrapUp(pat, val, robin);
        await Do(val, p => p.AddAttendeeName = "Late Person");
        await Do(val, p => p.AddAttendee());
        await Do(pat, p => p.EndMeeting());
        Meetings.Get(day).Should().BeNull();

        // Robin, an analyst who followed it to the end: still a follower's page, with nothing to correct from here.
        await Do(robin, p => p.Follow());
        var followed = await robin.HtmlOnce("Late Person");
        followed.Should().NotContain("id=\"present-rail\"").And.NotContain("id=\"present-rail-toggle\"").And.NotContain("Before you end")
            .And.NotContain(Box).And.NotContain("Remove Late Person");
        followed.Should().Contain("Attended (").And.Contain("Late Person", "who attended is still shown");

        // The presenter, opening it again: the rail says the meeting is over, and only what's left to do is offered.
        await using var again = await Open(app, day);
        var title = await again.Html();
        title.Should().Contain("after the meeting").And.Contain("The meeting is over").And.NotContain("Ready to present?");
        await again.Click(p => p.OnPresentKey("last"));
        var wrap = await again.Html();
        wrap.Should().Contain("After the meeting").And.NotContain("Before you end").And.NotContain("Raise as").And.NotContain("End the meeting");
        wrap.Should().Contain("placeholder=\"Someone in the room\"").And.Contain("aria-label=\"Remove Late Person\"")
            .And.Contain("aria-label=\"Remove Robin Analyst\"", "the record is theirs to correct: anyone, joined or added");
        var late = (await Attendees(brief)).Single(a => a.Name == "Late Person");
        await Do(again, p => p.RemoveAttendee(late));
        await Do(again, p => p.AddAttendeeName = "Kim from IT");
        await Do(again, p => p.AddAttendee());
        (await Attendees(brief)).Select(a => a.Name).Should().Contain("Kim from IT").And.NotContain("Late Person");

        // A follower who reopens it after the meeting adds and removes nothing (a Viewer doesn't correct the record).
        await using var valAgain = await Open(app, day, Viewer);
        await valAgain.Click(p => p.OnPresentKey("last"));
        (await valAgain.Html()).Should().NotContain(Box).And.NotContain("Remove Kim from IT");
    }
}
