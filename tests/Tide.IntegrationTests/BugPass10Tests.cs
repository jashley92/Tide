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
