using AwesomeAssertions;
using Tide.Application.Operations;
using Tide.Application.Security;
using Tide.Domain.Enums;

namespace Tide.UnitTests;

/// <summary>Each page's guide: for whoever can use its page, three to five steps, none on a control they can't use.</summary>
public class PageGuidesTests
{
    private static Func<Permission, bool> Can(params AppRole[] roles) => RoleDefinitions.PermissionsFor(roles).Contains;

    private static string[] Keys(params AppRole[] roles) => Guides.For(Can(roles)).Where(g => !g.Rehearsal).Select(g => g.Key).ToArray();

    private static readonly string[] Everyones =
        ["today", "actions", "action", "metrics", "trends", "stories", "adversaries", "library", "stack", "search", "workload", "rotation"];

    [Fact]
    public void AnAnalyst_GetsAGuideForEveryPage()
    {
        Keys(AppRole.Analyst).Should().BeEquivalentTo([.. Everyones, "triage", "article", "coverage"]);
        Keys(AppRole.Admin).Should().BeEquivalentTo(Keys(AppRole.Analyst));
    }

    [Theory]
    [InlineData(AppRole.Viewer)]
    [InlineData(AppRole.ActionOwner)]
    public void AViewerOrActionOwner_GetsOnlyThePagesTheyCanUse(AppRole role)
    {
        Keys(role).Should().BeEquivalentTo(Everyones, "Triage, the article editor and Coverage need WriteArticles");
    }

    [Fact]
    public void EachGuide_IsThreeToFiveSteps_ForAnAnalyst()
    {
        foreach (var g in Guides.For(Can(AppRole.Analyst)).Where(g => !g.Rehearsal))
        {
            g.Steps.Count.Should().BeInRange(3, 5, g.Key);
            g.Steps.Should().OnlyContain(s => s.Target != null, g.Key);
            g.Steps.Select(s => s.Key).Should().OnlyHaveUniqueItems(g.Key);
        }
        Guides.For(Can(AppRole.Analyst)).Select(g => g.Key).Should().OnlyHaveUniqueItems();
    }

    [Theory]
    [InlineData(AppRole.Viewer)]
    [InlineData(AppRole.ActionOwner)]
    public void EveryGuide_HasAtLeastTwoSteps_ForSomeoneWhoCantWrite(AppRole role)
    {
        foreach (var g in Guides.For(Can(role))) g.Steps.Count.Should().BeGreaterThanOrEqualTo(2, g.Key);
    }

    [Fact]
    public void AViewersSteps_NeverNameWhatOnlyWritersDo()
    {
        string[] writers = ["Take it", "Add a ", "Swap or get cover", "Promote", "Raise", "Start an outreach"];
        foreach (var g in Guides.For(Can(AppRole.Viewer)))
            g.Steps.Should().OnlyContain(s => !writers.Any(w => s.Text.Contains(w, StringComparison.Ordinal)), g.Key);
    }

    [Theory]
    [InlineData("", "today")]
    [InlineData("triage", "triage")]
    [InlineData("articles/3f2504e0-4f89-11d3-9a0c-0305e82c3301", "article")]
    [InlineData("actions", "actions")]
    [InlineData("actions/12", "action")]
    [InlineData("metrics", "metrics")]
    [InlineData("search", "search")]
    [InlineData("rotation", "rotation")]
    public void APage_HasItsGuide(string page, string key) => Guides.ForPage(page, Can(AppRole.Analyst))!.Key.Should().Be(key);

    [Fact]
    public void SearchsGuide_ShowsTheExports() =>
        Guides.Find("search", Can(AppRole.Viewer))!.Steps.Should().Contain(s => s.Text.Contains("STIX 2.1", StringComparison.Ordinal));
}
