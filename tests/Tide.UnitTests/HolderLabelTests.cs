using AwesomeAssertions;
using Tide.Application.Actions;
using Tide.Application.Briefs;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.UnitTests;

/// <summary>
/// Who has an action, in words: an external team's action nobody in TIDE holds is named by its ticket (linked only to an
/// http or https address), or says it has no ticket yet; a holder is named first; an internal team's reads as before.
/// </summary>
public class HolderLabelTests
{
    private static HolderLabel Of(string? holder = null, bool external = true, string? ticket = "INC-4411",
        string? url = "https://itsm.example.org/INC-4411") =>
        HolderLabel.Of(holder is null ? null : "dev:" + holder, holder, external, "Hosting Provider", ticket, url, "Unassigned");

    [Fact]
    public void AnExternalTeamsAction_WithATicket_IsNamedByIt_AndLinksToIt()
    {
        var label = Of();
        (label.Text, label.Short, label.Kind, label.Url).Should().Be(("INC-4411", "INC-4411", HolderKind.Ticket, (string?)"https://itsm.example.org/INC-4411"));
        label.ToString().Should().Be("INC-4411");
        Of(ticket: "  INC-4411  ").Text.Should().Be("INC-4411", "the reference is trimmed");
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://itsm.example.org/INC-4411")]
    [InlineData("itsm.example.org/INC-4411")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ATicketLink_ThatIsntHttpOrHttps_IsNotLinked(string? url) =>
        Of(url: url).Should().Be(new HolderLabel("INC-4411", HolderKind.Ticket));

    [Fact]
    public void AnExternalTeamsAction_WithOnlyATicketLink_IsNamedAsATicket()
    {
        var label = Of(ticket: " ");
        (label.Text, label.Kind, label.Url).Should().Be((HolderLabel.UnnamedTicket, HolderKind.Ticket, (string?)"https://itsm.example.org/INC-4411"));
    }

    [Fact]
    public void AnExternalTeamsAction_WithNoTicket_SaysSo_ShortBesideTheTeam()
    {
        var label = Of(ticket: null, url: null);
        (label.Text, label.Short, label.Kind, label.Url).Should().Be(("With Hosting Provider · no ticket yet", "no ticket yet", HolderKind.NoTicket, (string?)null));
        Of(ticket: null, url: "javascript:alert(1)").Kind.Should().Be(HolderKind.NoTicket, "an unsafe link is no ticket");
    }

    [Fact]
    public void AnExternalTeamsAction_SomeoneHolds_NamesThem_AsTheActionPageDoes() =>
        Of(holder: "Robin").Should().Be(new HolderLabel("Robin", HolderKind.Person));

    [Fact]
    public void AnInternalTeamsAction_IsUnchanged_ItsTicketNeverNamesIt()
    {
        Of(external: false).Should().Be(new HolderLabel("Unassigned", HolderKind.Nobody));
        Of(external: false, holder: "Robin").Text.Should().Be("Robin");
        HolderLabel.Of("dev:robin", null, false, "Network", null, null, "nobody").Text.Should().Be("dev:robin", "with no name, its id");
    }

    [Fact]
    public void AnAction_AsItsPageHasIt_ReadsItsTeam()
    {
        var a = new ActionItem { TicketRef = "INC-4411", TicketUrl = "https://itsm.example.org/INC-4411", Team = new Team { Name = "Hosting Provider", IsExternal = true } };
        HolderLabel.Of(a, "Unassigned").Text.Should().Be("INC-4411");
        a.Team = new Team { Name = "Network" };
        HolderLabel.Of(a, "Unassigned").Text.Should().Be("Unassigned");
        a.Team = null;
        HolderLabel.Of(a, "Unassigned").Text.Should().Be("Unassigned", "without its team it's taken as internal");
        HolderLabel.Of(new ActionItem { AssigneeName = "Robin" }, "").Text.Should().Be("Robin", "a template's sample has only a name");
    }

    [Fact]
    public void TheListsRows_SayTheSame()
    {
        var row = new ActionRow(Guid.NewGuid(), 7, "Patch the edge", ActionKind.PatchRemediate, Priority.P2, Guid.NewGuid(), "Hosting Provider",
            null, null, null, ActionStatus.Open, ActionOutcome.None, null, null, null, null, 0, DateTimeOffset.UtcNow, TeamExternal: true)
        { TicketRef = "INC-4411", TicketUrl = "https://itsm.example.org/INC-4411" };
        row.Holder("Unassigned").Text.Should().Be("INC-4411");
        (row with { TeamExternal = false }).Holder("Unassigned").Text.Should().Be("Unassigned");
        new EditionAction(7, "Patch the edge", "Hosting Provider", null, ActionStatus.Open, TeamExternal: true).Holder("nobody yet").Text
            .Should().Be("With Hosting Provider · no ticket yet");
        new Tide.Application.Search.ActionHit(Guid.NewGuid(), 7, "Patch", ActionStatus.Open, Priority.P2, "Hosting Provider", null, null, null, null,
            TeamExternal: true, TicketRef: "INC-4411").Holder("Unassigned").Text.Should().Be("INC-4411");
        new Tide.Application.Search.RelatedAction(7, "Patch", ActionStatus.Open, "Hosting Provider", null, null, 0, true, "INC-4411")
            .Holder("Unassigned").Text.Should().Be("INC-4411");
        new Tide.Application.Stack.StackAction(7, "Patch", Priority.P2, ActionStatus.Open, "Hosting Provider", null, null, true, null)
            .Holder("unassigned").Short.Should().Be("no ticket yet");
    }
}
