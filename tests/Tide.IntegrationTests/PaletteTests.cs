using System.Net;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Org;
using Tide.Application.Search;
using Tide.Domain.Enums;
using Tide.Web.Components.Layout;

namespace Tide.IntegrationTests;

/// <summary>The command palette's entries (PaletteEntries) and the shortcuts list (Shortcuts), per person and page.</summary>
public class PaletteEntryTests
{
    private static readonly DateOnly Day = new(2026, 10, 29);

    private static List<string> Labels(params AppRole[] roles) => PaletteEntries.For(new FakeUser(roles), Day).Select(e => e.Label).ToList();

    [Fact]
    public void AnAnalyst_GetsTheNewCommands_AndEveryTeamPage()
    {
        var entries = PaletteEntries.For(new FakeUser(AppRole.Analyst), Day);
        entries.Take(2).Select(e => (e.Label, e.Href)).Should().Equal(("New article", "articles?new=1"), ("New action", "actions/new"));
        entries.Select(e => e.Label).Should().Contain(["Today", "Triage", "Articles", "Actions", "Search", "Stories", "Workload", "Published briefs", "Present today's brief"])
            .And.NotContain("Administration", "an analyst doesn't administer");
        entries.Single(e => e.Label == "Today").Href.Should().Be("", "Today is today's brief");
        entries.Should().NotContain(e => e.Label == "Today's brief", "it would only lead back to Today");
        entries.Single(e => e.Label == "Present today's brief").Href.Should().Be("present/2026-10-29");
    }

    [Fact]
    public void AReader_GetsOnlyThePublishedArchive()
    {
        Labels(AppRole.Reader).Should().Equal("Published briefs");
    }

    [Fact]
    public void AnActionOwner_GetsTheirPages_ButNoNewCommandsOrTriage()
    {
        var labels = Labels(AppRole.ActionOwner);
        labels.Should().Contain(["Actions", "Workload", "Articles"]);
        labels.Should().NotContain(["New article", "New action", "Triage", "Present today's brief", "Administration"]);
    }

    [Fact]
    public void ThePalette_ListsThePagesTheSidebarDoes()
    {
        foreach (var roles in new[] { new[] { AppRole.Reader }, [AppRole.ActionOwner], [AppRole.Analyst], [AppRole.Admin] })
        {
            var user = new FakeUser(roles);
            var pages = PaletteEntries.For(user, Day).Where(e => e.Kind == PaletteKind.Page).Select(e => e.Href);
            pages.Should().Equal(NavPages.For(user).Select(p => p.Href));
        }
    }

    [Theory]
    [InlineData("", 24)]
    [InlineData("triage", 1)]
    [InlineData("TRIAGE", 1)]
    [InlineData("ioc", 1)]
    [InlineData("new act", 1)]
    [InlineData("nothing like this", 0)]
    public void Match_NeedsEveryWord_InTheNameOrItsOtherWords(string query, int expected)
    {
        var all = PaletteEntries.For(new FakeUser(AppRole.Admin), Day);
        all.Should().HaveCount(24, "every page, the team's libraries and Board rules among them, and the shortcuts");
        PaletteEntries.Match(all, query).Should().HaveCount(expected);
    }

    [Fact]
    public void Match_FindsEveryBriefPage()
    {
        var all = PaletteEntries.For(new FakeUser(AppRole.Admin), Day);
        PaletteEntries.Match(all, " brief ").Select(e => e.Label).Should()
            .Equal("Today", "Brief archive", "Brief rotation", "Published briefs", "Present today's brief");
    }

    [Fact]
    public void Match_FindsThePlaybooksAndBoardRules_ByTheirOtherWords_ForAViewerToo()
    {
        var all = PaletteEntries.For(new FakeUser(AppRole.Viewer), Day);
        PaletteEntries.Match(all, "checklist").Select(e => (e.Label, e.Href)).Should().Equal(("Playbooks", "playbooks"));
        PaletteEntries.Match(all, "card colour").Select(e => (e.Label, e.Href)).Should().Equal(("Board rules", "board-rules"));
    }

    [Fact]
    public void FromSearch_ListsActionsThenArticlesThenStories_AndEndsWithTheSearchPage()
    {
        var article = Guid.NewGuid();
        var story = Guid.NewGuid();
        var results = new SearchResults("A-12 & co", ["A-12"], null, null,
            new HitList<ArticleHit>([new ArticleHit(article, "Phishing wave", null, TlpLevel.Green, Classification.Informational, null,
                ArticleStatus.Ready, "Robin", DateTimeOffset.UtcNow, [], null)], 1),
            new HitList<ActionHit>([new ActionHit(Guid.NewGuid(), 12, "Block the domain", ActionStatus.InProgress, Priority.P1, "SOC", null, null, null, null)], 1),
            new HitList<StoryHit>([new StoryHit(story, "Qakbot", StoryStatus.Active, 3, null)], 1));

        var entries = PaletteEntries.FromSearch(results, new FakeUser(AppRole.Analyst));

        entries.Select(e => (e.Label, e.Href, e.Kind)).Should().Equal(
            ("A-12 Block the domain", "actions/12", PaletteKind.Action),
            ("Phishing wave", $"articles/{article}", PaletteKind.Article),
            ("Qakbot", $"stories/{story}", PaletteKind.Story),
            ("Search everything for “A-12 & co”", "search?q=A-12%20%26%20co", PaletteKind.Search));
        entries[0].Detail.Should().StartWith("Action · ");
        entries[1].Detail.Should().Be("Article · Ready");
    }

    [Fact]
    public void FromSearch_LeavesOutActions_ForSomeoneWhoMayNotReadThem()
    {
        // Defence in depth, as the search page does (Search.razor's _canOpenActions): no role that may search lacks
        // ReadActions today, but a hit the person couldn't open isn't offered.
        var hits = new HitList<ActionHit>([new ActionHit(Guid.NewGuid(), 12, "Block the domain", ActionStatus.Open, Priority.P1, "SOC", null, null, null, null)], 1);
        var results = new SearchResults("block", ["block"], null, null, HitList<ArticleHit>.None, hits, HitList<StoryHit>.None);
        PaletteEntries.FromSearch(results, new FakeUser(AppRole.Reader)).Should().NotContain(e => e.Kind == PaletteKind.Action);
        PaletteEntries.FromSearch(results, new FakeUser(AppRole.Viewer)).Should().Contain(e => e.Kind == PaletteKind.Action);
    }

    [Fact]
    public void FromSearch_ShowsAFewOfEachKind()
    {
        var hits = Enumerable.Range(1, 9).Select(n => new ActionHit(Guid.NewGuid(), n, "Act " + n, ActionStatus.Open, Priority.P4, "SOC", null, null, null, null)).ToList();
        var results = new SearchResults("act", ["act"], null, null, HitList<ArticleHit>.None, new HitList<ActionHit>(hits, hits.Count), HitList<StoryHit>.None);
        PaletteEntries.FromSearch(results, new FakeUser(AppRole.Analyst)).Count(e => e.Kind == PaletteKind.Action).Should().Be(PaletteEntries.HitsPerKind);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("a", false)]
    [InlineData("7", true)]
    [InlineData("A-7", true)]
    [InlineData("ph", true)]
    public void Searchable_IsTwoCharactersOrAnActionNumber(string query, bool expected) =>
        CommandPalette.Searchable(query).Should().Be(expected);

    [Theory]
    [InlineData("triage", "Triage")]
    [InlineData("present/2026-10-29", "Present mode")]
    [InlineData("actions", "Actions board")]
    [InlineData("actions?view=open&layout=board", "Actions board")]
    [InlineData("actions/12", "A screenshot shown full size")]
    [InlineData("articles/5b0f6c1e-0000-0000-0000-000000000001", "Article")]
    [InlineData("trends", "Charts")]
    [InlineData("metrics", "Charts")]
    public void Shortcuts_StartWithThePagesOwn(string path, string first)
    {
        var groups = Shortcuts.For(path);
        groups[0].Title.Should().Be(first);
        groups.Should().EndWith([Shortcuts.Everywhere, Shortcuts.Palette]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("articles")]
    [InlineData("actions/new")]
    [InlineData("search?q=x")]
    public void Shortcuts_OnAPageWithoutItsOwn_AreTheGlobalOnes(string path) =>
        Shortcuts.For(path).Should().Equal(Shortcuts.Everywhere, Shortcuts.Palette);

    [Fact]
    public void TheTriageShortcuts_AreTheKeysTriageJsHandles_AndTheLettersAreMarked()
    {
        var triage = Shortcuts.For("triage")[0].Keys;
        triage.SelectMany(k => k.Keys).Should().BeEquivalentTo(["↓", "↑", "J", "K", "S", "X", "D", "P", "Esc", "O", "V", "R", "Enter", "→", "←"], "Esc closes the promote form");
        triage.Where(k => k.Keys.All(key => key.Length == 1 && char.IsLetter(key[0]))).Should().OnlyContain(k => k.Letter);
        triage.Single(k => k.Keys.Contains("↓")).Letter.Should().BeFalse("arrows aren't affected by the switch");
        Shortcuts.Everywhere.Keys.Single(k => k.Keys.Contains("?")).Letter.Should().BeTrue();
    }
}

/// <summary>The palette's and the shortcuts list's buttons and script, as rendered.</summary>
public class PaletteRenderTests(TideApp app) : IClassFixture<TideApp>
{
    [Fact]
    public async Task AnAnalystsTopBar_HasTheGoToAndShortcutsButtons_AndThePageLoadsTheirScript()
    {
        var html = await app.CreateClient().GetStringAsync("/articles");
        html.Should().Contain("class=\"tide-palette-btn\"").And.Contain("aria-keyshortcuts=\"Control&#x2B;K Meta&#x2B;K\"")
            .And.Contain("class=\"tide-shortcuts-btn\"").And.Contain("aria-keyshortcuts=\"Shift&#x2B;?\"")
            .And.Contain("Ctrl K")
            .And.Contain("<script src=\"js/palette.js?v=").And.Contain("href=\"palette.css?v=");
        html.Should().NotContain("data-palette-input", "the dialog isn't drawn until it opens");
    }

    [Fact]
    public async Task ThePalettesNewArticle_OpensTheForm()
    {
        var html = await app.CreateClient().GetStringAsync("/articles?new=1");
        html.Should().Contain("<h2>New article</h2>");
        (await app.CreateClient().GetStringAsync("/articles")).Should().NotContain("<h2>New article</h2>");
    }

    [Fact]
    public async Task PresentMode_HasTheKeysButNoButtons()
    {
        var day = app.Services.GetRequiredService<OrgClock>().CurrentBriefDate;
        var res = await app.CreateClient().GetAsync($"/present/{day:yyyy-MM-dd}");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await res.Content.ReadAsStringAsync();
        html.Should().NotContain("tide-palette-btn").And.NotContain("tide-shortcuts-btn");
        html.Should().Contain("js/palette.js?v=");
    }
}

public class ReaderPaletteTests(ReaderApp app) : IClassFixture<ReaderApp>
{
    [Fact]
    public async Task AReader_HasTheGoToButton_ForTheOnePageTheyMayOpen()
    {
        var html = await app.CreateClient().GetStringAsync("/published");
        html.Should().Contain("class=\"tide-palette-btn\"").And.Contain("class=\"tide-shortcuts-btn\"");
        html.Should().NotContain("tide-top-search", "a Reader has no search");
    }
}
