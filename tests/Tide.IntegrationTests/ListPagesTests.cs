using System.Net;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Actions;
using Tide.Application.Org;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Infrastructure.Persistence;
using Tide.Web.Components.Shared;

namespace Tide.IntegrationTests;

/// <summary>The app with the sample data and 55 more articles, so the article list and search have more than a page.</summary>
public sealed class ListsApp : TideApp
{
    public const int Probes = 55;
    private bool _seeded;

    public async Task<HttpClient> SeededClient()
    {
        var client = CreateClient();
        if (_seeded) return client;
        using (var scope = Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            if (!db.Articles.Any(a => a.Title.StartsWith("Paging probe")))
            {
                for (var i = 0; i < Probes; i++)
                    db.Articles.Add(new Article { Title = $"Paging probe {i:00}", AuthorId = "x", AuthorName = "X", CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-30).AddMinutes(i) });
                await db.SaveChangesAsync();
            }
        }
        _seeded = true;
        return client;
    }
}

/// <summary>The lists built on DataTable (articles, actions, stories, the brief archive) and search: each says how many it holds.</summary>
public partial class ListPagesTests(ListsApp app) : IClassFixture<ListsApp>
{
    private async Task<string> Page(string path)
    {
        var res = await (await app.SeededClient()).GetAsync(path);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        return WebUtility.HtmlDecode(await res.Content.ReadAsStringAsync());
    }

    // The opening <th> of the column whose sort button reads <paramref name="title"/>.
    private static string Heading(string html, string title)
    {
        var at = html.IndexOf($"<button type=\"button\" class=\"tide-sort\">{title}<", StringComparison.Ordinal);
        at.Should().BePositive($"the {title} heading sorts");
        return html[html.LastIndexOf("<th", at, StringComparison.Ordinal)..at];
    }

    private int Count<T>(Func<AppDbContext, IQueryable<T>> q)
    {
        using var scope = app.Services.CreateScope();
        return q(scope.ServiceProvider.GetRequiredService<AppDbContext>()).Count();
    }

    [Fact]
    public async Task Articles_PageOnTheServer_SayingHowManyThereAre()
    {
        var html = await Page("/articles");
        var total = Count(db => db.Articles);
        total.Should().BeGreaterThan(50);
        html.Should().Contain($"1–50 of {total:N0} articles");
        html.Should().Contain("aria-label=\"Pages of articles\"").And.Contain("aria-current=\"page\" aria-label=\"Page 1, this page\"").And.Contain("aria-label=\"Page 2\"");
        (await Page("/articles?page=2")).Should().Contain($"51–{Math.Min(100, total):N0} of {total:N0} articles", "the page in the address is read from the server");
        (await Page("/articles?page=999")).Should().Contain($"of {total:N0} articles").And.Contain("aria-label=\"Page 1\"", "a page past the end shows the last");
        html.Should().Contain("<label class=\"visually-hidden\" for=\"dt-").And.Contain("Filter articles");
        Heading(html, "Written").Should().Contain("aria-sort=\"descending\"");
        Heading(html, "Article").Should().NotContain("aria-sort", "only the sorted column says how");
        html.Should().NotContain("class=\"tide-sort\">Briefed", "a column that doesn't sort has no button");
    }

    [Fact]
    public async Task Articles_TheBriefedColumn_SeparatesItsDates()
    {
        await app.SeededClient();
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var article = new Article { Title = "Briefed twice probe", AuthorId = "x", AuthorName = "X" };
            db.Articles.Add(article);
            foreach (var day in new[] { new DateOnly(2027, 4, 6), new DateOnly(2027, 4, 9) })
            {
                var brief = new Brief { BriefDate = day, AnalystId = "x", AnalystName = "X" };
                brief.Items.Add(new BriefItem { Article = article, ArticleId = article.Id });
                db.Briefs.Add(brief);
            }
            await db.SaveChangesAsync();
        }

        var html = await Page("/articles?q=Briefed%20twice%20probe");
        html.Should().Contain("</a>, <a href=\"briefs/2027-04-0").And.NotContain("Apr 6</a><a").And.NotContain("Apr 9</a><a",
            "the dates read apart, not \"Tue Apr 6Fri Apr 9\"");
    }

    [Fact]
    public async Task Articles_AFilter_CountsItsMatches_OrSaysNothingMatches()
    {
        (await Page("/articles?q=Paging%20probe")).Should().Contain($"1–50 of {ListsApp.Probes} matching articles");
        var none = await Page("/articles?q=zzqx");
        none.Should().Contain("No articles match ‘zzqx’").And.Contain("Clear the filter");
        none.Should().NotContain("<table class=\"tide-table\">");
    }

    [Fact]
    public async Task Articles_OtherFilters_ShowAsChips_EachLinkingToTheListWithoutIt()
    {
        var html = await Page("/articles?q=lure&tactic=TA0001&from=2026-10-01&to=2026-10-31&tag=zzqx");
        html.Should().Contain(">Oct 2026</button>").And.Contain("<a href=\"articles?q=lure&tag=zzqx&tactic=TA0001\" class=\"tide-chip-remove\" aria-label=\"Remove filter Oct 2026\"");
        html.Should().Contain("<a href=\"articles?q=lure&from=2026-10-01&to=2026-10-31&tag=zzqx\" class=\"tide-chip-remove\" aria-label=\"Remove filter Tactic: Initial Access\"");
        html.Should().Contain("<a href=\"articles?q=lure&from=2026-10-01&to=2026-10-31&tactic=TA0001\" class=\"tide-chip-remove\" aria-label=\"Remove filter Tag: zzqx\"",
            "a key no tag has is shown as it is");
        html.Should().Contain("<a href=\"articles?q=lure\" class=\"tide-filterbar-clear small\">Clear</a>", "the words stay");
        html.Should().Contain("value=\"lure\"", "the words are in the box");
        html.Should().Contain("No articles match ‘lure’");
        var none = await Page("/articles?tag=zzqx");
        none.Should().Contain("No articles match these filters").And.Contain("<a class=\"btn btn-outline-secondary btn-sm\" href=\"articles\">Clear filters</a>");
        var draft = await Page("/articles?q=Paging%20probe&status=draft");
        draft.Should().Contain($"1–50 of {ListsApp.Probes} matching articles").And.NotContain("Remove filter Status", "the Draft pill says it");
        FilterBarHtml.Pressed(draft, "Draft").Should().BeTrue();
        FilterBarHtml.Pressed(draft, "Ready").Should().BeFalse();
        (await Page("/articles?q=Paging%20probe&status=presented")).Should().Contain("aria-label=\"Remove filter Status: Presented\"", "no pill says Presented");
        var plain = await Page("/articles");
        plain.Should().NotContain("tide-chip-remove").And.NotContain("tide-filterbar-clear").And.Contain("class=\"tide-filter-add\"");
    }

    [Fact]
    public async Task AnAttackFilteredList_ShowsTheTacticFilter_AndEachRowsMatchingTechniques()
    {
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            if (!await db.Articles.AnyAsync(a => a.Title == "Attack probe"))
            {
                var probe = new Article { Title = "Attack probe", Status = ArticleStatus.Ready, AuthorId = "x", AuthorName = "X" };
                probe.Techniques = new[] { "T1190", "T1059", "T1566.001" }.Select(id => new ArticleTechnique { ArticleId = probe.Id, TechniqueId = id }).ToList();
                db.Add(probe);
                await db.SaveChangesAsync();
            }
        }

        // The Trends drill: a period and a tactic, each a chip in the filter bar.
        var drilled = await Page("/articles?q=Attack%20probe&from=2020-01-01&to=2100-12-31&tactic=TA0001");
        drilled.Should().Contain("Attack probe").And.Contain(">Tactic: Initial Access</button>");
        drilled.Should().Contain("aria-label=\"Remove filter Tactic: Initial Access\"").And.Contain("aria-label=\"Remove filter Jan 2020 – Dec 2100\"");
        drilled.Should().Contain("role=\"group\" aria-label=\"Matching techniques\"");
        drilled.Should().Contain("<span class=\"tide-chip tide-chip-technique\">T1190 Exploit Public-Facing Application</span>")
            .And.Contain("<span class=\"tide-chip tide-chip-technique\">T1566.001 Spearphishing Attachment</span>");
        drilled.Should().NotContain("T1059 Command and Scripting Interpreter", "Execution isn't the tactic asked for");

        // A tactic alone: its chip.
        var tactic = await Page("/articles?q=Attack%20probe&tactic=TA0002");
        tactic.Should().Contain(">Tactic: Execution</button>");
        tactic.Should().Contain("T1059 Command and Scripting Interpreter").And.NotContain("T1190 Exploit");

        var technique = await Page("/articles?q=Attack%20probe&technique=T1566&withsubs=true");
        technique.Should().Contain(">Technique: T1566 Phishing and sub-techniques</button>").And.Contain("T1566.001 Spearphishing Attachment").And.NotContain("T1190 Exploit");
    }

    [Fact]
    public async Task WithoutAnAttackFilter_RowsCarryNoTechniqueChips()
    {
        var html = await Page("/articles");
        html.Should().NotContain("Matching techniques").And.NotContain("tide-chip-technique");
        html.Should().NotContain("tide-chip-remove", "no filter is in force");
        (await Page("/articles?technique=none")).Should().NotContain("Matching techniques", "no technique is no technique to show");
    }

    [Fact]
    public async Task ATrendsCount_OpensAsTheArticleList_FilteredToTheSameArticles()
    {
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            if (!await db.Articles.AnyAsync(a => a.Title == "Pivot probe"))
            {
                var probe = new Article { Title = "Pivot probe", Status = ArticleStatus.Ready, AuthorId = "x", AuthorName = "X" };
                var brief = new Brief { BriefDate = new DateOnly(2025, 1, 15) };
                brief.Items.Add(new BriefItem { ArticleId = probe.Id });
                db.AddRange(probe, brief);
                await db.SaveChangesAsync();
            }
        }

        var trends = await Page("/trends?p=2025-01&line=articles:all&at=2025-01");
        trends.Should().Contain("<a class=\"btn btn-sm btn-outline-primary\" href=\"articles?from=2025-01-01&to=2025-01-31\">")
            .And.Contain("Open this article in Articles");
        (await Page("/trends")).Should().Contain("Select a number to list what it counts.");
        (await Page("/trends?p=2025-01&view=actions&line=actions:raised&at=2025-01")).Should().NotContain("in Articles</a>", "actions aren't articles");

        var list = await Page("/articles?from=2025-01-01&to=2025-01-31");
        list.Should().Contain("Pivot probe").And.Contain("1 matching article").And.Contain(">Jan 2025</button>");
    }

    [Fact]
    public async Task Stories_CountSortAndFilter()
    {
        var html = await Page("/stories");
        var total = Count(db => db.Stories);
        html.Should().Contain(total == 1 ? "1 story" : $"{total} stories");
        Heading(html, "Status").Should().Contain("aria-sort=\"ascending\"");
        (await Page("/stories?q=zzqx")).Should().Contain("No stories match ‘zzqx’");
        (await Page("/stories?q=edge%20VPN")).Should().Contain($"Showing 1 of {total} {(total == 1 ? "story" : "stories")}");
    }

    [Fact]
    public async Task TheBriefArchive_ListsEveryBrief_WithACount()
    {
        var html = await Page("/briefs");
        var total = Count(db => db.Briefs);
        html.Should().Contain(total == 1 ? "1 brief" : $"{total} briefs");
        Heading(html, "Day").Should().Contain("aria-sort=\"descending\"");
        (await Page("/briefs?q=zzqx")).Should().Contain("No briefs match ‘zzqx’");
    }

    [Fact]
    public async Task TheActionsList_CountsSortsAndGroups_AndTheBoardKeepsItsCount()
    {
        var open = Count(db => db.Actions.Where(a => a.Status == ActionStatus.Open || a.Status == ActionStatus.InProgress || a.Status == ActionStatus.Blocked));
        var html = await Page("/actions?view=open&layout=list");
        html.Should().Contain(open == 1 ? "1 action" : $"{open} actions");
        Heading(html, "Due").Should().Contain("aria-sort=\"ascending\"");
        html.Should().Contain("All open actions, sorted by due, ascending");

        var grouped = await Page("/actions?view=open&group=kind");
        grouped.Should().Contain("scope=\"rowgroup\"").And.Contain("IOC sweep");

        (await Page("/actions?view=open&layout=list&q=zzqx")).Should().Contain("No actions match ‘zzqx’");
        (await Page("/actions?view=closed")).Should().NotContain("aria-sort=", "the closed view comes most recently closed first");
        (await Page("/actions?view=open&layout=board")).Should().Contain($"{open} action").And.NotContain("Filter actions");
    }

    [Fact]
    public async Task Mine_IsGroupedByWhenItsDue_WithCounts_SortedWithinAndNarrowedByTheFilter()
    {
        var today = app.Services.GetRequiredService<OrgClock>().Today;
        var weekEnd = ActionSla.EndOfBusinessWeek(today);
        (string Title, ActionStatus Status, DateOnly? Due)[] mine =
        [
            ("Urgency probe someday", ActionStatus.Open, null),
            ("Urgency probe next week", ActionStatus.Open, weekEnd.AddDays(3)),
            ("Urgency probe late", ActionStatus.Open, today.AddDays(-3)),
            ("Urgency probe today", ActionStatus.InProgress, today),
            ("Urgency probe this week", ActionStatus.Open, weekEnd),
            ("Urgency probe stuck", ActionStatus.Blocked, today.AddDays(-2)),
        ];
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            if (!await db.Actions.AnyAsync(a => a.Title.StartsWith("Urgency probe")))
            {
                var team = await db.Teams.FirstAsync();
                var n = await db.Actions.MaxAsync(a => (int?)a.Number) ?? 0;
                foreach (var (title, status, due) in mine)
                    db.Actions.Add(new ActionItem { Number = ++n, Title = title, Status = status, DueDate = due, TeamId = team.Id, AssigneeId = DevUser, AssigneeName = "Dev Analyst" });
                await db.SaveChangesAsync();
            }
        }

        // The headings, in their order, each with its count; empty groups (and only they) are left out.
        var expected = Enum.GetValues<ActionUrgency>()
            .Select(u => (Name: ActionSla.UrgencyName(u), Count: mine.Count(m => ActionSla.UrgencyOf(m.Status, m.Due, today) == u)))
            .Where(g => g.Count > 0).ToList();
        var html = await Page("/actions?view=mine&layout=list&q=Urgency%20probe");
        Groups(html).Should().Equal(expected);
        Groups(html).Select(g => g.Name).Should().Contain(["Overdue", "Due today", "Later", "Blocked"]);
        html.Should().Contain("Mine actions, grouped by when they're due, sorted by due, ascending");
        html.IndexOf("Urgency probe next week", StringComparison.Ordinal).Should()
            .BeLessThan(html.IndexOf("Urgency probe someday", StringComparison.Ordinal), "rows sort within their group, no date last");
        html.IndexOf("Urgency probe late", StringComparison.Ordinal).Should()
            .BeLessThan(html.IndexOf("Urgency probe stuck", StringComparison.Ordinal), "blocked comes last, though it's overdue");

        Groups(await Page("/actions?view=mine&layout=list&q=Urgency%20probe%20late")).Should().Equal([("Overdue", 1)]);

        // Grouped by kind when asked, and the other views aren't grouped.
        var byKind = await Page("/actions?view=mine&group=kind&q=Urgency%20probe");
        Groups(byKind).Select(g => g.Name).Should().NotContain("Overdue").And.Contain(ActionService.KindName(ActionKind.Other));
        (await Page("/actions?view=open&layout=list&q=Urgency%20probe")).Should().NotContain("scope=\"rowgroup\"");
        (await Page("/actions?view=mine&layout=board")).Should().NotContain("scope=\"rowgroup\"");
    }

    [Fact]
    public async Task Mine_WithMoreThanAPage_CountsEveryRowInAGroup_NotOnlyThoseShown()
    {
        var today = app.Services.GetRequiredService<OrgClock>().Today;
        var n = 0;
        ActionRow Row(string title, ActionStatus status, DateOnly? due) => new(Guid.NewGuid(), ++n, title, ActionKind.Other, Priority.P3,
            Guid.Empty, "SOC", DevUser, "Dev Analyst", due, status, ActionOutcome.None, null, null, null, null, 0, DateTimeOffset.UtcNow);
        // Soonest due first: the 105 overdue, then the blocked (due next year), then the 20 with no date. A page is 100.
        var rows = Enumerable.Range(0, 105).Select(i => Row($"Late {i}", ActionStatus.Open, today.AddDays(-200 + i)))
            .Concat(Enumerable.Range(0, 3).Select(i => Row($"Stuck {i}", ActionStatus.Blocked, today.AddYears(1))))
            .Concat(Enumerable.Range(0, 20).Select(i => Row($"Someday {i}", ActionStatus.Open, null)))
            .ToList();

        await using var r = await ComponentHarness.RenderAsync<ActionTable>(app, "/actions?view=mine", new()
        {
            ["Rows"] = (IReadOnlyList<ActionRow>)rows, ["Today"] = today, ["GroupByUrgency"] = true, ["DefaultSort"] = (TableSort?)ActionTable.SoonestDue,
        });
        var html = WebUtility.HtmlDecode(await r.Html());

        Groups(html).Should().Equal(("Overdue", 105), ("Later", 20), ("Blocked", 3));
        html.Should().Contain("<span class=\"tide-count\">105</span><span class=\"tide-dt-group-more\"><span aria-hidden=\"true\">· </span>5 on other pages</span>")
            .And.Contain("<span class=\"tide-count\">20</span><span class=\"tide-dt-group-more\"><span aria-hidden=\"true\">· </span>on other pages</span>")
            .And.Contain("1–100 of 128 actions").And.Contain("aria-label=\"Page 2\"")
            .And.Contain("Late 99<").And.NotContain("Late 100<").And.NotContain("Someday 0<");

        // The second page, from the address: only its rows are drawn, under the same group counts.
        await using var second = await ComponentHarness.RenderAsync<ActionTable>(app, "/actions?view=mine&page=2", new()
        {
            ["Rows"] = (IReadOnlyList<ActionRow>)rows, ["Today"] = today, ["GroupByUrgency"] = true, ["DefaultSort"] = (TableSort?)ActionTable.SoonestDue,
            ["PageInAddress"] = true,
        });
        var two = WebUtility.HtmlDecode(await second.Html());
        Groups(two).Should().Equal(("Overdue", 105), ("Later", 20), ("Blocked", 3));
        two.Should().Contain("101–128 of 128 actions").And.Contain("aria-current=\"page\" aria-label=\"Page 2, this page\"")
            .And.Contain("Late 100<").And.Contain("Someday 0<").And.Contain("Stuck 2<").And.NotContain("Late 99<");
    }

    private const string DevUser = "S-1-5-21-DEV-1001";

    private static List<(string Name, int Count)> Groups(string html) =>
        GroupHeading().Matches(html).Select(m => (m.Groups["name"].Value.Trim(), int.Parse(m.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture))).ToList();

    // A group heading: its name, then its count.
    [GeneratedRegex("scope=\"rowgroup\">(?<name>[^<]+)<span class=\"tide-count\">(?<n>[0-9]+)</span>")]
    private static partial Regex GroupHeading();

    [Fact]
    public async Task Search_SaysHowManyMatch_AndPagesThem()
    {
        var html = await Page("/search?q=Paging%20probe");
        html.Should().Contain($"(1–50 of {ListsApp.Probes})");
        html.Should().Contain("aria-label=\"Pages of articles\"").And.Contain("aria-label=\"Page 2\"");
    }
}
