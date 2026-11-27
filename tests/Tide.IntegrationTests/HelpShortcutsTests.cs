using AwesomeAssertions;
using Tide.Application.Security;
using Tide.Web.Components.Pages;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>Help's list of every page's keyboard shortcuts (Shortcuts.All), for the pages each role can open.</summary>
public class HelpShortcutsTests(TideApp app) : IClassFixture<TideApp>
{
    private static Func<Domain.Enums.Permission, bool> As(params string[] roles)
    {
        var has = RoleDefinitions.PermissionsForRoleNames(roles);
        return has.Contains;
    }

    private static IReadOnlyList<string> Titles(params string[] roles) =>
        [.. Web.Components.Layout.Shortcuts.All(As(roles)).Select(g => g.Title)];

    [Fact]
    public void AnAnalyst_GetsEveryPagesKeys_TheGlobalOnesFirst()
    {
        Titles("Analyst").Should().Equal("Everywhere", "In the Go to box", "Triage", "Article", "Editing the article", "Actions board",
            "Actions list", "Library", "A screenshot shown full size", "Our stack", "Adversaries", "Charts", "Present mode");
        var everywhere = Web.Components.Layout.Shortcuts.All(As("Analyst"))[0].Keys.Select(k => k.Keys[0]).ToList();
        everywhere.IndexOf("]").Should().Be(everywhere.FindLastIndex(k => k is "?" or "/") + 1, "the rail's key comes after the single keys, as on a page with a rail");
    }

    [Fact]
    public void AViewer_GetsNoTriageOrEditingKeys_AndNoTakeOnTheActions()
    {
        var titles = Titles("Viewer");
        titles.Should().NotContain(["Triage", "Editing the article"]).And.Contain("Article");
        foreach (var g in Web.Components.Layout.Shortcuts.All(As("Viewer")).Where(g => g.Title.StartsWith("Actions", StringComparison.Ordinal)))
            g.Keys.Should().NotContain(k => k.Keys.Contains("T") || k.Keys.Contains("Space"), $"{g.Title}: a Viewer can't take or choose actions");
    }

    [Fact]
    public async Task Help_ListsTheKeys_InAGroupToALine_WithEverywhereOpen()
    {
        await using var r = await RenderAsync<HelpPage>(app, "/help");
        var html = await r.Html();
        html.Should().Contain("id=\"help-keys\"").And.Contain("<h2 id=\"help-keys-title\">Keyboard shortcuts</h2>");
        html.Should().Contain("<details open><summary>Everywhere").And.Contain("<summary>Triage");
        html.Should().Contain("<caption class=\"visually-hidden\">Triage</caption>", "the summary says the title; the table keeps its name");
        html.Should().Contain("<kbd>Ctrl K</kbd>").And.Contain("Mark as a duplicate of the similar article");
    }
}
