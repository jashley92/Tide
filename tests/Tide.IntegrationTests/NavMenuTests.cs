using System.Text.RegularExpressions;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Org;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Infrastructure.Persistence;
using Tide.Web.Components.Layout;

namespace Tide.IntegrationTests;

/// <summary>The sidebar as rendered: its groups (each label with its links' hrefs, in order) and the header.</summary>
internal sealed partial record RenderedNav(IReadOnlyList<(string Label, IReadOnlyList<string> Links)> Groups, string Html, string Head, string Page)
{
    public static async Task<RenderedNav> GetAsync(HttpClient client, string path)
    {
        var html = await client.GetStringAsync(path);
        var nav = MainNav().Match(html).Value;
        nav.Should().NotBeEmpty();
        var head = SideHead().Match(html).Value;
        head.Should().NotBeEmpty();
        var groups = Group().Matches(nav)
            .Select(m => (m.Groups[1].Value, (IReadOnlyList<string>)Href().Matches(m.Groups[2].Value).Select(h => h.Groups[1].Value).ToList()))
            .ToList();
        return new RenderedNav(groups, nav, head, html);
    }

    public IReadOnlyList<string> Labels => Groups.Select(g => g.Label).ToList();

    public IReadOnlyList<string> Links => Groups.SelectMany(g => g.Links).ToList();

    [GeneratedRegex("<nav aria-label=\"Main\".*?</nav>", RegexOptions.Singleline)]
    private static partial Regex MainNav();

    [GeneratedRegex("<div class=\"tide-side-head\">.*?</div>", RegexOptions.Singleline)]
    private static partial Regex SideHead();

    // A group: its label, then its own list of links.
    [GeneratedRegex("<span class=\"tide-nav-group-label\"[^>]*>([^<]*)</span>\\s*<ul[^>]*>(.*?)</ul>", RegexOptions.Singleline)]
    private static partial Regex Group();

    [GeneratedRegex("<a [^>]*href=\"([^\"]*)\"")]
    private static partial Regex Href();
}

public class NavMenuTests(TideApp app) : IClassFixture<TideApp>
{
    [Fact]
    public async Task AnAnalystAdmin_SeesEveryLink_UnderGroupLabels_TheDailyOnesFirst_AndNoMore()
    {
        var nav = await RenderedNav.GetAsync(app.CreateClient(), "/articles");
        nav.Groups.Select(g => (g.Label, string.Join(",", g.Links))).Should().Equal(
            ("Daily", ",triage,articles,actions,search"),
            ("Intelligence", "stories,briefs,stack,adversaries"),
            ("Insight", "trends,metrics,coverage,reports"),
            ("Team", "rotation,workload,library"),
            ("Company-wide", "published"),
            ("Administration", "admin"));
        nav.Html.Should().NotContain("nav-more").And.NotContain(">More<").And.NotContain("<button", "the labels are text, not controls");
        nav.Page.Should().NotContain("tide-nav-foot", "the collapse button moved to the top");
    }

    [Fact]
    public async Task TheCollapseButton_IsAnIconAtTheTopOfTheSidebar_ThatSaysWhetherItIsExpanded()
    {
        var nav = await RenderedNav.GetAsync(app.CreateClient(), "/articles");
        var button = nav.Head[nav.Head.IndexOf("<button type=\"button\" id=\"nav-collapse-toggle\"", StringComparison.Ordinal)..];
        button = button[..(button.IndexOf("</button>", StringComparison.Ordinal) + 9)];
        button.Should().Contain("class=\"tide-side-collapse\"").And.Contain("aria-expanded=\"true\"")
            .And.Contain("aria-controls=\"tide-nav-links\"").And.Contain("aria-label=\"Collapse or expand the sidebar\"")
            .And.Contain("title=\"Collapse the sidebar\"").And.Contain("bi-chevron-double-left");
        nav.Head.IndexOf("nav-collapse-toggle", StringComparison.Ordinal).Should()
            .BeGreaterThan(nav.Head.IndexOf("tide-brand", StringComparison.Ordinal), "it sits beside the brand, in the header");
    }

    [Fact]
    public void TheGroups_AreInSidebarOrder_AndEachPageInOne()
    {
        NavPages.All().Select(p => p.Group).Distinct().Should().Equal(
            NavPages.Daily, NavPages.Intelligence, NavPages.Insight, NavPages.Team, NavPages.CompanyWide, NavPages.Administration);
        NavPages.All().Take(5).Should().OnlyContain(p => p.Group == NavPages.Daily);
    }

    [Fact]
    public async Task TheDailyCounts_AreShown_AndSaidInWords()
    {
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var team = await db.Teams.FirstAsync();
            var today = app.Services.GetRequiredService<OrgClock>().Today;
            db.Actions.Add(new ActionItem
            {
                Number = 901, Title = "Late and mine", Kind = ActionKind.IocSweep, Priority = Priority.P2, TeamId = team.Id,
                DueDate = today.AddDays(-2), AssigneeId = "S-1-5-21-DEV-1001", AssigneeName = "Dev Analyst",
            });
            await db.SaveChangesAsync();
        }
        int newItems;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            newItems = await db.FeedItems.CountAsync(i => i.Status == FeedItemStatus.New && !i.Source!.IsRetired);
        }
        newItems.Should().BePositive("the sample data has items to triage");

        var nav = await RenderedNav.GetAsync(app.CreateClient(), "/articles");
        nav.Html.Should().MatchRegex($"href=\"triage\"[^>]*aria-label=\"Triage, {newItems} new\"");
        nav.Html.Should().MatchRegex("href=\"actions\"[^>]*aria-label=\"Actions, 1 overdue for you\"");
        nav.Html.Should().Contain("<span class=\"tide-nav-count is-alert\" aria-hidden=\"true\">1</span>");
    }

    [Fact]
    public void CountTitle_NamesTheLink_WithItsCountWhenThereIsOne()
    {
        NavMenu.CountTitle("Triage", 4, "new").Should().Be("Triage, 4 new");
        NavMenu.CountTitle("Triage", 0, "new").Should().Be("Triage");
    }
}

public class ActionOwnerNavTests(ActionOwnerApp app) : IClassFixture<ActionOwnerApp>
{
    [Fact]
    public async Task AnActionOwner_SeesTheirLinksInTheirGroups_WithNoTriageAndNoAdministrationLabel()
    {
        var nav = await RenderedNav.GetAsync(app.CreateClient(), "/actions");
        nav.Groups.Select(g => (g.Label, string.Join(",", g.Links))).Should().Equal(
            ("Daily", ",articles,actions,search"),
            ("Intelligence", "stories,briefs,stack,adversaries"),
            ("Insight", "trends"),
            ("Team", "workload,library"),
            ("Company-wide", "published"));
        nav.Html.Should().NotContain("aria-label=\"Triage").And.NotContain("Administration");
    }
}

public class ReaderNavTests(ReaderApp app) : IClassFixture<ReaderApp>
{
    [Fact]
    public async Task AReader_SeesOnlyThePublishedBriefs_UnderTheirOneLabel()
    {
        var nav = await RenderedNav.GetAsync(app.CreateClient(), "/published");
        nav.Labels.Should().Equal("Company-wide");
        nav.Links.Should().Equal("published");
        nav.Html.Should().NotContain("Daily").And.NotContain("Intelligence");
    }
}
