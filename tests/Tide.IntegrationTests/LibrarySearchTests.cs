using System.Net;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Actions;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Web.Components.Layout;
using Tide.Web.Components.Pages;
using Tide.Web.Services;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>
/// The Library's one search, on its tab row: it looks through every tab the person may open, and lists what it found
/// tab by tab, its words in the address (?q=). A tab the person can't open gives nothing, whatever matches in it. The
/// page has one header, with the open tab's New button and one line about it; the saved queries are chosen by platform
/// with counts, sorted by platform or by use, grouped under their platform, with Retire and Delete under More.
/// </summary>
public partial class LibrarySearchTests(TideApp app) : IClassFixture<TideApp>
{
    private static readonly SignIn Viewer = new("dev:library-viewer", "Vera Viewer", "Viewer");
    private static readonly SignIn Reader = new("dev:library-reader", "Rae Reader", "Reader");

    // One entry in each library, each with the tag in its name; their ids.
    private async Task<(Guid Query, Guid Template, Guid Playbook, Guid Rule)> Seed(string tag)
    {
        await using var scope = SignedIn(app, SignIn.Analyst);
        var sp = scope.ServiceProvider;
        return (await sp.GetRequiredService<SavedQueryService>().SaveAsync(new SavedQueryForm { Name = "Found " + tag, Dialect = "kql", Text = "DeviceEvents | take 1" }),
            await sp.GetRequiredService<OutreachTemplateService>().SaveAsync(new OutreachTemplateForm { Name = "Found " + tag, Subject = "Are we affected?", Body = "Hello" }),
            await sp.GetRequiredService<PlaybookService>().SaveAsync(new PlaybookForm { Name = "Found " + tag, Steps = ["Look"] }),
            await sp.GetRequiredService<CardRuleService>().SaveAsync(new ActionCardRule { Name = "Found " + tag, Colour = CardColour.Red, Unassigned = true }));
    }

    private async Task<IReadOnlyList<LibraryHits>> Find(string text, SignIn who, IReadOnlyList<LibraryTab>? tabs = null)
    {
        await using var scope = SignedIn(app, who);
        var search = scope.ServiceProvider.GetRequiredService<LibrarySearch>();
        return tabs is null ? await search.FindAsync(text) : await search.FindAsync(text, tabs);
    }

    [Fact]
    public async Task OneSearch_LooksThroughEveryTab_AndGroupsWhatItFindsByTab()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        var (query, template, playbook, _) = await Seed(tag);

        var hits = await Find(tag, SignIn.Analyst);
        hits.Select(h => h.Tab.Key).Should().Equal(LibraryTabs.Queries, LibraryTabs.Templates, LibraryTabs.Playbooks, LibraryTabs.BoardRules);
        hits.Select(h => h.Ids.Single()).Take(3).Should().Equal(query, template, playbook);
        (await Find("", SignIn.Analyst)).Should().BeEmpty("no words, no search");

        // Each tab is the group of what it holds that matches: its count is how many, its link keeps the words, and the
        // open tab lists them.
        var html = WebUtility.HtmlDecode(await app.CreateClient().GetStringAsync($"/library/templates?q={tag}"));
        html.Should().MatchRegex($"<input id=\"library-find\" type=\"search\"[^>]*value=\"{tag}\"", "the words come back from the address");
        var bar = LibraryTabBar().Match(html).Groups[1].Value;
        var links = bar.Split("<a ").Skip(1).ToList();
        links.Should().HaveCount(4);
        foreach (var key in new[] { "queries", "templates", "playbooks", "board-rules" })
            links.Should().ContainSingle(l => l.Contains($"href=\"library/{key}?q={tag}\"") && l.Contains("<span class=\"tide-tabbar-count\">1</span>"), $"the {key} tab counts its one match");
        html.Should().Contain("Found " + tag).And.Contain("1 template").And.Contain("id=\"ot-retired\"");
    }

    [Fact]
    public async Task ATabThePersonCannotOpen_GivesNothing_EvenWhenItMatches()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        await Seed(tag);
        // The Playbooks tab as if it asked for administration: a viewer can't open it, so the search doesn't look in it.
        var tabs = LibraryTabs.All.Select(t => t.Key == LibraryTabs.Playbooks ? t with { Requires = Permission.Administer } : t).ToList();

        var hits = await Find(tag, Viewer, tabs);
        hits.Select(h => h.Tab.Key).Should().NotContain(LibraryTabs.Playbooks).And.Contain(LibraryTabs.Queries);
        (await Find(tag, SignIn.Analyst, tabs)).Select(h => h.Tab.Key).Should().Contain(LibraryTabs.Playbooks, "an administrator can open it");
        (await Find(tag, Reader)).Should().BeEmpty("someone who can't read actions opens no tab of the Library");
    }

    [Fact]
    public async Task ThePage_HasOneHeader_WithTheTabsNewButton_AndOneSearchOnTheTabRow()
    {
        var html = WebUtility.HtmlDecode(await app.CreateClient().GetStringAsync("/library/queries"));
        var head = PageHeadSub().Match(html).Groups[1].Value;
        head.Should().Contain("New saved query</button>", "the open tab's New button is in the page's header");
        var sub = TabSub().Match(head).Groups[1].Value;
        AnyTag().Replace(sub, "").Trim().Length.Should().BeInRange(1, 120, "one line about the tab");
        html.Should().MatchRegex("<div class=\"tide-library-tabrow\">\\s*<nav class=\"tide-tabbar tide-tabbar-page\" aria-label=\"Library\">.*?</nav>\\s*<form [^>]*role=\"search\"",
            "the one search is on the tab row");
        html.Should().Contain("placeholder=\"Search the whole library\"");
        foreach (var own in new[] { "sq-find", "ot-find", "pb-find" }) html.Should().NotContain($"id=\"{own}\"", "no tab has a search of its own");
    }

    [Fact]
    public async Task SavedQueries_AreChosenByPlatformWithCounts_SortedByPlatformOrUse_AndGroupedUnderTheirPlatform()
    {
        IReadOnlyList<SavedQueryRow> rows;
        await using (var scope = SignedIn(app, SignIn.Analyst)) rows = await scope.ServiceProvider.GetRequiredService<SavedQueryService>().ListAsync();
        await using var r = await RenderAsync<SavedQueriesPage>(app, "/library/queries");
        var html = WebUtility.HtmlDecode(await r.Html());

        html.Should().MatchRegex("role=\"group\" aria-label=\"Platform\"");
        html.Should().MatchRegex($"aria-pressed=\"true\"[^>]*>\\s*<span>All</span>\\s*<span class=\"tide-tabbar-count\">{rows.Count}</span>");
        foreach (var (key, name) in new[] { ("xql", "XQL"), ("kql", "KQL"), ("fql", "Falcon") })
            html.Should().MatchRegex($"aria-pressed=\"false\"[^>]*>\\s*<span>{name}</span>\\s*<span class=\"tide-tabbar-count\">{rows.Count(q => q.Query.Dialect == key)}</span>");
        html.Should().MatchRegex("role=\"group\" aria-label=\"Sort\"").And.Contain("<span>By platform</span>").And.Contain("<span>Most used</span>");
        var first = rows[0].Query.Dialect;
        html.Should().Contain($"<li class=\"tide-library-group\">{HuntQueryBuilder.DialectName(first)} · {rows.Count(q => q.Query.Dialect == first)}</li>",
            "each platform's queries under a heading with their count");

        await r.Click(c => { c.SortBy("used"); return Task.CompletedTask; });
        r.Component.Shown.Select(q => q.Actions).Should().BeInDescendingOrder("most used first");
        (await r.Html()).Should().NotContain("<li class=\"tide-library-group\">", "by use, the platforms are mixed, so there are no headings");
        await r.Click(c => { c.ChoosePlatform("kql"); return Task.CompletedTask; });
        r.Component.Shown.Should().OnlyContain(q => q.Query.Dialect == "kql");
    }

    [Fact]
    public async Task RetireAndDelete_AreUnderMore_AndCopyIsTheEntrysPrimaryButton()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        var (query, _, _, _) = await Seed(tag);
        await using var r = await RenderAsync<SavedQueriesPage>(app, $"/library/queries?row={query}");
        var html = WebUtility.HtmlDecode(await r.Html());
        var entry = html[html.IndexOf("<article class=\"tide-hunt tide-library-entry", StringComparison.Ordinal)..];
        entry = entry[..entry.IndexOf("</article>", StringComparison.Ordinal)];
        entry.Should().MatchRegex("<button type=\"button\" class=\"btn btn-primary btn-sm\" data-copy-target=\"sq-[0-9a-f]+-text\"");
        entry.Should().MatchRegex("aria-haspopup=\"menu\"[^>]*>\\s*More").And.NotContain(">Retire</button>").And.NotContain(">Delete</button>");
        // The menu's items, read as the page's own events read them (signed in as the person it was drawn for).
        static async Task<IReadOnlyList<string>> More(Rendered<SavedQueriesPage> page)
        {
            IReadOnlyList<string> labels = [];
            await page.Click(c => { labels = [.. c.MoreItems(c.Selected!.Query).Select(i => i.Label)]; return Task.CompletedTask; });
            return labels;
        }
        (await More(r)).Should().Equal("Retire", "Delete");

        await using var viewer = await RenderAsync<SavedQueriesPage>(app, $"/library/queries?row={query}", @as: Viewer);
        (await More(viewer)).Should().BeEmpty("someone who only reads actions can neither retire nor delete");
        (await viewer.Html()).Should().NotContain("aria-haspopup=\"menu\"");
    }

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex AnyTag();

    [GeneratedRegex("<nav class=\"tide-tabbar tide-tabbar-page\" aria-label=\"Library\">(.*?)</nav>", RegexOptions.Singleline)]
    private static partial Regex LibraryTabBar();

    [GeneratedRegex("<div class=\"tide-page-head\">.*?</h1>(.*?)<nav class=\"tide-tabbar", RegexOptions.Singleline)]
    private static partial Regex PageHeadSub();

    [GeneratedRegex("<div class=\"tide-sub\">(.*?)</div>", RegexOptions.Singleline)]
    private static partial Regex TabSub();
}
