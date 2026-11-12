using AwesomeAssertions;
using Tide.Application.Actions;
using Tide.Application.Briefs;
using Tide.Domain.Enums;

namespace Tide.UnitTests;

/// <summary>
/// The end of the meeting: who has what on the wrap-up (the brief's actions by team and holder), and on the open actions
/// slide how long each has waited for an update and the one thing the room can do about it.
/// </summary>
public class MeetingWrapUpTests
{
    private static EditionAction Act(int number, string team, string? holder, ActionStatus status = ActionStatus.Open) =>
        new(number, $"Action {number}", team, null, status, AssigneeId: holder is null ? null : "dev:" + holder, AssigneeName: holder);

    [Fact]
    public void WhoHasWhat_GroupsByTeamThenHolder_NobodyLastInATeam_EachInNumberOrder()
    {
        var groups = WrapUp.WhoHasWhat([Act(5, "Network", null), Act(2, "Cyber Defense", "Robin"), Act(3, "Network", "Alex"),
            Act(1, "Cyber Defense", "Robin"), Act(4, "Cyber Defense", null)]);

        groups.Select(g => g.Who).Should().Equal("Robin · Cyber Defense", "Cyber Defense · nobody yet", "Alex · Network", "Network · nobody yet");
        groups[0].Actions.Select(a => a.Number).Should().Equal(1, 2);
    }

    [Fact]
    public void WhoHasWhat_OfNoActions_IsEmpty() => WrapUp.WhoHasWhat([]).Should().BeEmpty();

    [Fact]
    public void WhoHasWhat_KeepsTwoPeopleOfOneName_Apart_NumberedOnlyThen() =>
        WrapUp.WhoHasWhat([Act(3, "Network", "Sam") with { AssigneeId = "dev:other-sam" }, Act(1, "Network", "Sam"), Act(2, "Cyber Defense", "Sam"),
                Act(4, "Network", "Alex")])
            .Select(g => g.Who).Should().Equal("Sam · Cyber Defense", "Alex · Network", "Sam (1) · Network", "Sam (2) · Network");

    private static readonly DateOnly Day = new(2026, 11, 12);

    private static OutstandingAction Open(string? holder = "dev:robin", ActionStatus status = ActionStatus.Open, int daysLate = 3,
        bool noOneToOwn = false, bool external = false, DateTimeOffset? lastActivity = null) =>
        new(new ActionRow(Guid.NewGuid(), 7, "Patch the gateways", ActionKind.PatchRemediate, Priority.P2, Guid.NewGuid(), "Network",
            holder, holder is null ? null : "Robin", Day.AddDays(-daysLate), status, ActionOutcome.None, null, null, null, null, 0,
            DateTimeOffset.UtcNow, TeamExternal: external, LastActivityUtc: lastActivity), daysLate, noOneToOwn);

    private static DateOnly DayOf(DateTimeOffset at) => DateOnly.FromDateTime(at.UtcDateTime);

    [Fact]
    public void QuietDays_CountsWholeDaysSinceItsLastUpdate_AsOfTheBriefsDay()
    {
        Open(lastActivity: new DateTimeOffset(2026, 11, 6, 15, 0, 0, TimeSpan.Zero)).Row.QuietDays(Day, DayOf).Should().Be(6);
        Open(lastActivity: new DateTimeOffset(2026, 11, 12, 1, 0, 0, TimeSpan.Zero)).Row.QuietDays(Day, DayOf).Should().Be(0);
        Open(lastActivity: new DateTimeOffset(2026, 11, 14, 1, 0, 0, TimeSpan.Zero)).Row.QuietDays(Day, DayOf).Should().Be(0, "after a past day's brief: not negative");
        Open(lastActivity: null).Row.QuietDays(Day, DayOf).Should().Be(0);
    }

    [Fact]
    public void QuietText_SaysHowLongItHasWaited_OrNothingWithinTheDay()
    {
        ActionRow.QuietText(6).Should().Be("no update for 6 days");
        ActionRow.QuietText(1).Should().Be("no update for 1 day");
        ActionRow.QuietText(0).Should().BeNull();
    }

    [Fact]
    public void NextInTheRoom_IsOneThingToDo_TheMostPressingFirst()
    {
        Open(noOneToOwn: true, holder: null).NextInTheRoom(9).Should().Be("Reassign to a team with members");
        Open(status: ActionStatus.Blocked).NextInTheRoom(9).Should().Be("Ask what it's waiting on");
        Open(holder: null).NextInTheRoom(9).Should().Be("Find someone to take it");
        Open(holder: null, external: true).NextInTheRoom(9).Should().Be("Ask the team for an update", "an external team has nobody in TIDE to take it");
        Open().NextInTheRoom(2).Should().Be("Ask for an update");
        Open(daysLate: 0).NextInTheRoom(0).Should().Be("Ask for an update", "escalated, not yet due");
        Open().NextInTheRoom(1).Should().Be("Agree a new due date", "late, but someone updated it lately");
        Open(noOneToOwn: true).NextInTheRoom(9).Should().Be("Ask for an update", "someone holds it: it's theirs, whoever is on its team");
        Open(noOneToOwn: true, status: ActionStatus.Blocked).NextInTheRoom(9).Should().Be("Ask what it's waiting on");
    }

    [Fact]
    public void Holder_IsWhoeverHoldsIt_AsTheActionPageSays()
    {
        Open().Holder.Text.Should().Be("Robin");
        Open(noOneToOwn: true).Holder.Text.Should().Be("Robin", "a holder is the holder, even on a team with no members");
        Open(holder: null, noOneToOwn: true).Holder.Text.Should().Be("no one: the team has no members");
        Open(holder: null).Holder.Text.Should().Be("nobody yet");
        Open(holder: null).HolderAs("unassigned").Text.Should().Be("unassigned");
    }

    private static OutstandingAction External(string? ticket, string? holder = null, ActionStatus status = ActionStatus.Open, int daysLate = 3)
    {
        var o = Open(holder: holder, external: true, status: status, daysLate: daysLate);
        return o with { Row = o.Row with { TeamName = "Hosting Provider", TicketRef = ticket, TicketUrl = ticket is null ? null : "https://itsm.example.org/" + ticket } };
    }

    [Fact]
    public void AnExternalTeamsOverdueAction_IsNamedByItsTicket_AndTheRoomAsksForAnUpdateThere_NeverToReassign()
    {
        External("INC-4411").Holder.Should().Be(new HolderLabel("INC-4411", HolderKind.Ticket, "https://itsm.example.org/INC-4411"));
        External(null).Holder.Text.Should().Be("With Hosting Provider · no ticket yet");
        External("INC-4411", holder: "dev:robin").Holder.Text.Should().Be("Robin", "a holder in TIDE still wins");

        External("INC-4411").NextInTheRoom(9).Should().Be("Ask for an update on its ticket", "the Who column names it");
        External(null).NextInTheRoom(9).Should().Be("Ask the team for an update");
        External("INC-4411", daysLate: 0).NextInTheRoom(0).Should().Be("Ask for an update on its ticket", "escalated, not yet due");
        External("INC-4411").NextInTheRoom(1).Should().Be("Agree a new due date", "late, but updated lately");
        External("INC-4411", status: ActionStatus.Blocked).NextInTheRoom(9).Should().Be("Ask what it's waiting on");
        External("INC-4411", holder: "dev:robin").NextInTheRoom(9).Should().Be("Ask for an update", "Robin has it");
        foreach (var o in new[] { External("INC-4411"), External(null) })
            o.NextInTheRoom(9).Should().NotContain("Reassign").And.NotContain("Find someone");
    }

    private static EditionAction Ext(int number, string? ticket, string? holder = null) =>
        Act(number, "Hosting Provider", holder) with { TeamExternal = true, TicketRef = ticket, TicketUrl = ticket is null ? null : "https://itsm.example.org/" + ticket };

    [Fact]
    public void WhoHasWhat_GroupsAnExternalTeamsActions_ByTicket_ThoseWithNoTicketLast()
    {
        var groups = WrapUp.WhoHasWhat([Ext(4, null), Ext(3, "INC-9"), Ext(1, "INC-4411"), Ext(2, "INC-4411"), Ext(5, "INC-9", holder: "Robin"),
            Act(6, "Network", null)]);

        groups.Select(g => g.Who).Should().Equal("Robin · Hosting Provider", "INC-4411 · Hosting Provider", "INC-9 · Hosting Provider",
            "With Hosting Provider · no ticket yet", "Network · nobody yet");
        groups[1].Actions.Select(a => a.Number).Should().Equal(1, 2);
        groups[1].Outside!.Url.Should().Be("https://itsm.example.org/INC-4411");
        groups[0].Outside.Should().BeNull("Robin holds it");
        groups[4].Outside.Should().BeNull("an internal team's is unchanged");
    }
}
