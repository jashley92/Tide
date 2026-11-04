using FluentAssertions;
using Tide.Domain.Enums;
using Tide.Web.Services;

namespace Tide.IntegrationTests;

public class BoardDragsTests
{
    private static readonly Guid MyTab = Guid.NewGuid(), RobinsTab = Guid.NewGuid(), SamsTab = Guid.NewGuid();
    private static readonly Guid Card = Guid.NewGuid(), OtherCard = Guid.NewGuid();

    [Fact]
    public void Others_AreTheCardsOtherPeopleHold_AndTheColumnTheyreOver()
    {
        var drags = new BoardDrags(TimeProvider.System);
        drags.Report(MyTab, "c-me", OtherCard, "S-ME", "Me", null);
        drags.Report(RobinsTab, "c-robin", Card, "S-ROBIN", "Robin", ActionStatus.InProgress);

        drags.Others("S-ME").Should().ContainSingle()
            .Which.Should().Match<BoardDrag>(d => d.ActionId == Card && d.Name == "Robin" && d.Over == ActionStatus.InProgress);
        drags.Others("S-ROBIN").Single().ActionId.Should().Be(OtherCard);
    }

    [Fact]
    public void Report_SaysSoOnlyWhenTheCardOrColumnChanges()
    {
        var drags = new BoardDrags(TimeProvider.System);
        var heard = 0;
        drags.Changed += _ => heard++;

        drags.Report(RobinsTab, "c-robin", Card, "S-ROBIN", "Robin", null);
        drags.Report(RobinsTab, "c-robin", Card, "S-ROBIN", "Robin", null);
        drags.Report(RobinsTab, "c-robin", Card, "S-ROBIN", "Robin", ActionStatus.Blocked);
        drags.Report(RobinsTab, "c-robin", Card, "S-ROBIN", "Robin", ActionStatus.Blocked);

        heard.Should().Be(2, "picking it up and crossing into Blocked; the repeats are no news");
        drags.Others("S-ME").Single().Over.Should().Be(ActionStatus.Blocked);
    }

    [Fact]
    public void End_LetsGo_OfThatTabsCardOnly()
    {
        var drags = new BoardDrags(TimeProvider.System);
        var heard = 0;
        drags.Report(RobinsTab, "c-robin", Card, "S-ROBIN", "Robin", null);
        drags.Report(SamsTab, "c-sam", OtherCard, "S-SAM", "Sam", null);
        drags.Changed += _ => heard++;

        drags.End(RobinsTab);
        drags.End(RobinsTab);   // let go twice: once

        drags.Others("S-ME").Select(d => d.Name).Should().Equal("Sam");
        heard.Should().Be(1);
    }

    [Fact]
    public void ATabThatClosesMidDrag_LetsGo_WithItsCircuit()
    {
        var drags = new BoardDrags(TimeProvider.System);
        var circuit = new PresenceCircuit(new Presence(), drags);
        circuit.Opened("c-robin");
        drags.Report(RobinsTab, circuit.CircuitId, Card, "S-ROBIN", "Robin", ActionStatus.Done);
        drags.Report(SamsTab, "c-sam", OtherCard, "S-SAM", "Sam", null);

        circuit.Closed("c-robin");
        drags.RemoveCircuit("");   // a prerender's empty id clears nothing

        drags.Others("S-ME").Select(d => d.Name).Should().Equal("Sam");
    }

    [Fact]
    public void ADragNotHeardFromInAWhile_StopsShowing()
    {
        var clock = new TestClock(new(2026, 10, 29, 9, 0, 0, TimeSpan.Zero));
        var drags = new BoardDrags(clock);
        drags.Report(RobinsTab, "c-robin", Card, "S-ROBIN", "Robin", null);

        clock.Now += BoardDrags.Stale - TimeSpan.FromSeconds(1);
        drags.Others("S-ME").Should().ContainSingle();
        drags.Report(RobinsTab, "c-robin", Card, "S-ROBIN", "Robin", ActionStatus.Blocked);   // word from the board
        clock.Now += TimeSpan.FromSeconds(2);
        drags.Others("S-ME").Should().ContainSingle("it was heard from a moment ago");
        clock.Now += BoardDrags.Stale;
        drags.Others("S-ME").Should().BeEmpty("its end must have been lost");
    }
}
