using AwesomeAssertions;
using Tide.Web.Services;

namespace Tide.IntegrationTests;

/// <summary>Bug pass 10: presence that outlived a closed browser.</summary>
public class BugPass10PresenceTests
{
    private const string Brief = "brief:2026-10-22";

    [Fact]
    public void ATabWhoseConnectionDropped_IsntShownAsHere_UntilItComesBack()
    {
        // A closed browser's circuit is kept for minutes in case it reconnects; its avatar shouldn't stay that long.
        var presence = new Presence();
        var circuit = new PresenceCircuit(presence);
        circuit.Opened("circuit-1");
        using var robin = presence.Join(Brief, "S-ROBIN", "Robin", "circuit-1");
        using var sam = presence.Join(Brief, "S-SAM", "Sam", "circuit-2");
        var changed = new List<string>();
        presence.Changed += changed.Add;

        circuit.Down("circuit-1");

        presence.Others(Brief, "S-ME").Select(p => p.Name).Should().Equal("Sam");
        changed.Should().Equal(Brief);

        circuit.Up("circuit-1");

        presence.Others(Brief, "S-ME").Select(p => p.Name).Should().Equal("Robin", "Sam");
        changed.Should().Equal(Brief, Brief);
    }

    [Fact]
    public void AClosedCircuit_IsForgotten_SoItsIdIsntKeptAsAway()
    {
        var presence = new Presence();
        using var robin = presence.Join(Brief, "S-ROBIN", "Robin", "circuit-1");

        presence.SetConnected("circuit-1", false);
        presence.RemoveCircuit("circuit-1");
        presence.SetConnected("", false);   // a prerender has no circuit; nothing to hide

        presence.AwayCount.Should().Be(0);
        presence.Others(Brief, "S-ME").Should().BeEmpty();
    }
}

/// <summary>Bug pass 10: a presenter's page that comes back after someone took over.</summary>
public sealed class BugPass10MeetingTests : IDisposable
{
    private static readonly DateOnly Day = new(2026, 9, 1);
    private readonly TestClock _time = new(new(2026, 9, 1, 9, 0, 0, TimeSpan.Zero));
    private readonly LiveMeetings _meetings;

    public BugPass10MeetingTests() => _meetings = new LiveMeetings(_time);

    public void Dispose() => _meetings.Dispose();

    [Fact]
    public void APageThatReconnects_HearsTheMeetingAsItIsNow_EvenThoughItsOwnSeatChanged()
    {
        // Its own changes a page doesn't hear (it has them already); but coming back, it has missed the take-over made
        // while it was away, and kept the presenter's rail. The change is told to every page, its own included.
        var patsPage = Guid.NewGuid();
        _meetings.Start(Day, Guid.NewGuid(), "pat", "Pat", "c-pat", patsPage, 0);
        var robin = _meetings.Join(Day, "robin", "Robin", true, "c-robin", Guid.NewGuid(), 0)!;
        _meetings.SetConnected("c-pat", false);
        _time.Now += LiveMeetings.Grace;
        _meetings.TakeOver(robin).Should().BeTrue();
        var heard = new List<MeetingChange>();
        _meetings.Changed += heard.Add;

        _meetings.SetConnected("c-pat", true);

        heard.Should().ContainSingle().Which.Origin.Should().NotBe(patsPage);
        _meetings.Get(Day)!.PresenterId.Should().Be("robin");
    }
}
