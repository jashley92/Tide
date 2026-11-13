using AwesomeAssertions;
using Tide.Application.Operations;
using Tide.Application.Security;
using Tide.Domain.Enums;

namespace Tide.UnitTests;

/// <summary>Help's guides: short walkthroughs of a page, each for those who can use that page, and "Learn to present"
/// for whoever can present.</summary>
public class GuidesTests
{
    private static Func<Permission, bool> Can(params AppRole[] roles) => RoleDefinitions.PermissionsFor(roles).Contains;

    private static IReadOnlyList<Guide> For(params AppRole[] roles) => Guides.For(Can(roles));

    [Theory]
    [InlineData(AppRole.Analyst)]
    [InlineData(AppRole.Admin)]
    public void WhoeverCanPresent_GetsLearnToPresent(AppRole role)
    {
        var present = For(role).Single(g => g.Key == Guides.Present);
        present.Title.Should().Be("Learn to present");
        present.Rehearsal.Should().BeTrue();
        present.Steps.Count.Should().Be(7);
        present.Steps.Select(s => s.Route).Should().Equal("title", "title", "article", "article", "open", "wrap", "wrap");
    }

    [Theory]
    [InlineData(AppRole.Viewer)]
    [InlineData(AppRole.ActionOwner)]
    [InlineData(AppRole.Reader)]
    [InlineData(AppRole.Visitor)]
    public void NobodyWhoCantPresent_GetsLearnToPresent(AppRole role) =>
        For(role).Should().NotContain(g => g.Key == Guides.Present);

    [Theory]
    [InlineData(AppRole.Reader)]
    [InlineData(AppRole.Visitor)]
    public void ReadersAndVisitors_HaveNoGuides(AppRole role) => For(role).Should().BeEmpty();

    [Theory]
    [InlineData(AppRole.Analyst)]
    [InlineData(AppRole.Admin)]
    [InlineData(AppRole.Viewer)]
    [InlineData(AppRole.ActionOwner)]
    public void EveryGuide_AndEachOfItsSteps_IsOneThePersonCanUse(AppRole role)
    {
        var can = Can(role);
        foreach (var g in For(role))
        {
            can(g.Needs).Should().BeTrue(g.Key);
            g.Steps.Should().NotBeEmpty().And.OnlyContain(s => can(s.Needs), g.Key);
            g.Steps.Count.Should().BeInRange(1, 7, g.Key);
            g.Steps.Should().OnlyContain(s => s.Title.Length > 0 && s.Text.Length > 0 && s.Text.Length <= 260, g.Key);
        }
    }

    [Fact]
    public void LearnToPresent_PointsAtRaising_WithoutDoingIt_AndExplainsEnding()
    {
        var steps = Guides.For(Can(AppRole.Analyst)).Single(g => g.Key == Guides.Present).Steps;
        steps.Single(s => s.Key == "raise").Text.Should().Contain("Raise");
        steps.Single(s => s.Key == "rail").Text.Should().Contain("]");
        steps.Single(s => s.Key == "end").Text.Should().Contain("End the meeting");
        steps.Single(s => s.Key == "wrap").Text.Should().Contain("three lists");
    }

    [Fact]
    public void ThePageGuide_IsFoundByTheOpenPage_AndNeverTheRehearsal()
    {
        var can = Can(AppRole.Admin);
        Guides.ForPage("present/2026-11-12", can).Should().BeNull("Learn to present starts from Help, never from a page");
        foreach (var g in Guides.For(can).Where(g => !g.Rehearsal))
            Guides.ForPage(g.Page.Replace("{n}", "12", StringComparison.Ordinal).Replace("{id}", Guid.Empty.ToString(), StringComparison.Ordinal), can)
                .Should().Be(g);
    }

    [Theory]
    [InlineData("actions/{n}", "actions/12", true)]
    [InlineData("actions/{n}", "actions/new", false)]
    [InlineData("actions/{n}", "actions", false)]
    [InlineData("articles/{id}", "articles/3f2504e0-4f89-11d3-9a0c-0305e82c3301", true)]
    [InlineData("articles/{id}", "articles/new", false)]
    [InlineData("stories", "stories", true)]
    [InlineData("stories", "Stories", true)]
    [InlineData("stories", "stories/3f2504e0-4f89-11d3-9a0c-0305e82c3301", false)]
    [InlineData("", "", true)]
    [InlineData("", "triage", false)]
    public void APage_MatchesItsAddress(string page, string open, bool matches) => Guides.Matches(page, open).Should().Be(matches);

    [Fact]
    public void TheTour_EndsBySayingThereAreGuidesInHelp()
    {
        var steps = Tours.For([RoleGroup.Analysts], Can(AppRole.Analyst));
        steps[^1].Text.Should().EndWith("There's a guide for each page in Help.");
    }
}
