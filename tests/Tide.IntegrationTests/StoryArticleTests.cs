using System.Reflection;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Abstractions;
using Tide.Application.Articles;
using Tide.Application.Intake;
using Tide.Application.Security;
using Tide.Application.Stack;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Web.Components.Pages;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>
/// A story's page adds an article: a new one on the story, prefilled with the CVEs, scorecard tags and products its
/// articles name (never above the new article's marking, never actors or techniques), or an existing one. Analysts only.
/// </summary>
public class StoryArticleTests : IDisposable
{
    private readonly TestDb _db = new();

    public StoryArticleTests() => _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst };

    public void Dispose() => _db.Dispose();

    private sealed record Seeded(Guid Story, Guid Ransomware, Guid Phishing, Guid EdgeVpn, Guid Mail);

    // A story with a CLEAR article (CVE-2026-0001, Ransomware, Edge VPN, a technique, an actor) and an AMBER one
    // (CVE-2026-0002, Phishing, Mail gateway); a retired tag and a retired product on the CLEAR one.
    private async Task<Seeded> StoryWithArticlesAsync()
    {
        var category = new ScorecardCategory { Key = "threat", Name = "Threat type" };
        var ransomware = new ScorecardTag { Key = "ransomware", Name = "Ransomware", Category = category };
        var phishing = new ScorecardTag { Key = "phishing", Name = "Phishing", Category = category };
        var retiredTag = new ScorecardTag { Key = "old", Name = "Old tag", Category = category, IsRetired = true };
        await using (var ctx = _db.Context())
        {
            ctx.ScorecardTags.AddRange(ransomware, phishing, retiredTag);
            await ctx.SaveChangesAsync();
        }
        var stack = _db.Stack();
        var edgeVpn = await stack.SaveAsync(new StackForm { Name = "Edge VPN" });
        var mail = await stack.SaveAsync(new StackForm { Name = "Mail gateway" });
        var retired = await stack.SaveAsync(new StackForm { Name = "Old firewall" });

        var story = await _db.Stories().CreateAsync("Edge VPN campaign", null);
        var clear = await ArticleAsync("Edge VPN exploited", TlpLevel.Clear, ["CVE-2026-0001"], [ransomware.Id], ["T1190"]);
        var amber = await ArticleAsync("ISAC: who is behind it", TlpLevel.Amber, ["CVE-2026-0002", "CVE-2026-0001"], [phishing.Id], []);
        // A retired tag can't be added through the form: it's put on directly, as one retired since it was added.
        await using (var ctx = _db.Context())
        {
            ctx.ArticleTags.Add(new ArticleTag { ArticleId = clear, TagId = retiredTag.Id });
            await ctx.SaveChangesAsync();
        }
        await _db.SetEarlierActorsAsync(clear, "FIN7");
        await stack.TagArticleAsync(clear, edgeVpn);
        await stack.TagArticleAsync(clear, retired);
        await stack.TagArticleAsync(amber, mail);
        await stack.SetRetiredAsync(retired, true);
        await _db.Stories().LinkAsync(clear, story);
        await _db.Stories().LinkAsync(amber, story);
        return new Seeded(story, ransomware.Id, phishing.Id, edgeVpn, mail);
    }

    private async Task<Guid> ArticleAsync(string title, TlpLevel tlp, string[] cves, Guid[] tags, string[] techniques)
    {
        var id = await _db.Articles().CreateAsync(title, "Vendor", null, tlp);
        var form = ArticleForm.From((await _db.Articles().GetAsync(id))!);
        form.Cves = cves.ToList();
        form.TagIds = tags.ToHashSet();
        form.TechniqueIds = techniques.ToList();
        await _db.Articles().SaveAsync(form);
        return id;
    }

    private async Task<Article> Saved(Guid id)
    {
        await using var ctx = _db.Context();
        return await ctx.Articles.AsNoTracking().Include(a => a.Cves).Include(a => a.Tags).Include(a => a.Products)
            .Include(a => a.Techniques).AsSplitQuery().FirstAsync(a => a.Id == id);
    }

    [Fact]
    public async Task NewArticleAsync_StartsADraftOnTheStory_WithItsCvesTagsAndProducts_NotItsActorsOrTechniques()
    {
        var s = await StoryWithArticlesAsync();

        var id = await _db.StoryArticles().NewArticleAsync(s.Story, "Edge VPN: patch released", TlpLevel.Amber);

        var a = await Saved(id);
        a.StoryId.Should().Be(s.Story);
        a.Status.Should().Be(ArticleStatus.Draft);
        a.Tlp.Should().Be(TlpLevel.Amber);
        a.Title.Should().Be("Edge VPN: patch released");
        a.Cves.Select(c => c.CveId).Should().BeEquivalentTo(["CVE-2026-0001", "CVE-2026-0002"]);
        a.Tags.Select(t => t.TagId).Should().BeEquivalentTo([s.Ransomware, s.Phishing], "live tags only: the retired one is left behind");
        a.Products.Select(p => p.ProductId).Should().BeEquivalentTo([s.EdgeVpn, s.Mail], "products in use only: the retired one is left behind");
        a.Techniques.Should().BeEmpty("techniques aren't copied");
        a.Actors.Should().BeNull("threat actors aren't copied");
        await using var ctx = _db.Context();
        (await ctx.ArticleAdversaries.AnyAsync(x => x.ArticleId == id)).Should().BeFalse("threat actors aren't copied");
        (await ctx.BriefItems.AnyAsync(i => i.ArticleId == id)).Should().BeFalse("adding it to a brief stays the analyst's choice");
    }

    [Fact]
    public async Task NewArticleAsync_NeverCopiesWhatOnlyAnArticleAboveItsMarkingNames()
    {
        var s = await StoryWithArticlesAsync();

        var id = await _db.StoryArticles().NewArticleAsync(s.Story, "Edge VPN: public advisory", TlpLevel.Clear);

        var a = await Saved(id);
        a.Tlp.Should().Be(TlpLevel.Clear);
        a.Cves.Select(c => c.CveId).Should().Equal(["CVE-2026-0001"], "CVE-2026-0002 is named only by the AMBER article");
        a.Tags.Select(t => t.TagId).Should().Equal([s.Ransomware]);
        a.Products.Select(p => p.ProductId).Should().Equal([s.EdgeVpn]);
    }

    [Fact]
    public async Task PrefillAsync_SaysWhatANewArticleWouldStartWith_AndHowManyArticlesSitAboveIt()
    {
        var s = await StoryWithArticlesAsync();

        var green = await _db.StoryArticles().PrefillAsync(s.Story, TlpLevel.Green);
        green.Cves.Should().Equal("CVE-2026-0001");
        green.TagIds.Should().Equal(s.Ransomware);
        green.ProductIds.Should().Equal(s.EdgeVpn);
        green.LeftOut.Should().Be(1);

        var red = await _db.StoryArticles().PrefillAsync(s.Story, TlpLevel.Red);
        red.Cves.Should().Equal("CVE-2026-0001", "CVE-2026-0002");
        red.LeftOut.Should().Be(0);
    }

    [Fact]
    public async Task NewArticleAsync_OnAStoryWithNoArticles_StartsAnEmptyDraftOnIt()
    {
        var story = await _db.Stories().CreateAsync("Quiet story", null);

        var id = await _db.StoryArticles().NewArticleAsync(story, "First report", TlpLevel.Green);

        var a = await Saved(id);
        a.StoryId.Should().Be(story);
        a.Cves.Should().BeEmpty();
        a.Tags.Should().BeEmpty();
        a.Products.Should().BeEmpty();
    }

    [Fact]
    public async Task NewArticleAsync_IsAudited_AsTheEditorsSaveIs()
    {
        var s = await StoryWithArticlesAsync();

        var id = await _db.StoryArticles().NewArticleAsync(s.Story, "Audited", TlpLevel.Clear);

        await using var ctx = _db.Context();
        var key = id.ToString();
        var rows = await ctx.AuditLog.AsNoTracking().Where(e => e.EntityKey.Contains(key)).Select(e => e.EntityType).ToListAsync();
        rows.Should().Contain([nameof(Article), nameof(ArticleCve), nameof(ArticleTag), nameof(ArticleProduct)]);
    }

    [Fact]
    public async Task NewArticleAsync_ForAStoryGone_StartsNothing()
    {
        await using (var ctx = _db.Context()) (await ctx.Articles.CountAsync()).Should().Be(0);

        await FluentActions.Invoking(() => _db.StoryArticles().NewArticleAsync(Guid.NewGuid(), "Orphan", TlpLevel.Clear))
            .Should().ThrowAsync<KeyNotFoundException>();

        await using (var ctx = _db.Context()) (await ctx.Articles.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData(AppRole.Viewer)]
    [InlineData(AppRole.Reader)]
    [InlineData(AppRole.ActionOwner)]
    public async Task EveryStep_ForSomeoneWhoCantWriteArticles_IsRefused(AppRole role)
    {
        var story = await _db.Stories().CreateAsync("Edge VPN campaign", null);
        _db.User.Roles = new HashSet<AppRole> { role };

        await FluentActions.Invoking(() => _db.StoryArticles().NewArticleAsync(story, "Not mine to write", TlpLevel.Clear)).Should().ThrowAsync<ForbiddenException>();
        await FluentActions.Invoking(() => _db.StoryArticles().PrefillAsync(story, TlpLevel.Clear)).Should().ThrowAsync<ForbiddenException>();
        await FluentActions.Invoking(() => _db.StoryArticles().CandidatesAsync(story, null)).Should().ThrowAsync<ForbiddenException>();
        await using var ctx = _db.Context();
        (await ctx.Articles.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task CandidatesAsync_ListsArticlesNotOnTheStory_NarrowedByWords()
    {
        var story = await _db.Stories().CreateAsync("Edge VPN campaign", null);
        var other = await _db.Stories().CreateAsync("Ransomware wave", null);
        var on = await _db.Articles().CreateAsync("Edge VPN exploited", "Vendor", null, TlpLevel.Clear);
        await _db.Stories().LinkAsync(on, story);
        var elsewhere = await _db.Articles().CreateAsync("Edge VPN ransomware link", "Vendor", null, TlpLevel.Amber);
        await _db.Stories().LinkAsync(elsewhere, other);
        var loose = await _db.Articles().CreateAsync("Phishing kit sold", "Vendor", null, TlpLevel.Green);

        var all = await _db.StoryArticles().CandidatesAsync(story, null);
        all.Select(c => c.Id).Should().BeEquivalentTo([elsewhere, loose], "never one already on the story");
        all.Single(c => c.Id == elsewhere).OnStory.Should().Be("Ransomware wave");
        all.Single(c => c.Id == loose).OnStory.Should().BeNull();

        var found = await _db.StoryArticles().CandidatesAsync(story, "edge vpn");
        found.Select(c => c.Id).Should().Equal(elsewhere);
    }

    [Fact]
    public async Task CandidatesAsync_ListsAtMostAPickersWorth()
    {
        var story = await _db.Stories().CreateAsync("Edge VPN campaign", null);
        for (var i = 0; i < StoryService.PickLimit + 3; i++) await _db.Articles().CreateAsync($"Report {i}", "Vendor", null, TlpLevel.Clear);

        (await _db.StoryArticles().CandidatesAsync(story, "report")).Should().HaveCount(StoryService.PickLimit);
    }
}

/// <summary>The story page's two actions: shown to analysts, hidden from Viewers; each adds through the story's services.</summary>
public class StoryArticlePageTests(TideApp app) : IClassFixture<TideApp>
{
    private static readonly SignIn Viewer = new("dev:story-viewer", "Vera Viewer", "Viewer");

    private async Task<Guid> StoryAsync(string name)
    {
        await using var scope = SignedIn(app, SignIn.Analyst);
        return await scope.ServiceProvider.GetRequiredService<StoryService>().CreateAsync(name + " " + Guid.NewGuid().ToString("N")[..6], null);
    }

    private Task<Rendered<Stories>> Open(Guid story, SignIn? who = null) =>
        RenderAsync<Stories>(app, $"/stories/{story}", new Dictionary<string, object?> { [nameof(Stories.Id)] = (Guid?)story }, who);

    // What a button does, then the page drawn again, as after an event.
    private static Task Do(Rendered<Stories> page, Func<Stories, Task> what) => page.Click(async p =>
    {
        await what(p);
        typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(p, null);
    });

    [Fact]
    public async Task AnAnalyst_SeesBothActions_AndAViewerNeither()
    {
        var story = await StoryAsync("Actions shown");

        await using (var page = await Open(story))
        {
            var html = await page.Html();
            html.Should().Contain("New article on this story").And.Contain("Add an existing article");
        }
        await using (var page = await Open(story, Viewer))
        {
            var html = await page.Html();
            html.Should().NotContain("New article on this story").And.NotContain("Add an existing article");
        }
    }

    [Fact]
    public async Task NewArticleOnThisStory_AsksForATitleAndMarking_SaysWhatItCopies_ThenStartsTheDraftOnTheStory()
    {
        var story = await StoryAsync("New from page");
        Guid amber;
        await using (var scope = SignedIn(app, SignIn.Analyst))
        {
            var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
            amber = await articles.CreateAsync("Above the line", "ISAC", null, TlpLevel.Amber);
            var form = ArticleForm.From((await articles.GetAsync(amber))!);
            form.Cves = ["CVE-2026-0002"];
            await articles.SaveAsync(form);
            await scope.ServiceProvider.GetRequiredService<StoryService>().LinkAsync(amber, story);
        }
        await using var page = await Open(story);

        await Do(page, p => p.StartNew());
        var html = await page.Html();
        html.Should().Contain("tide-story-new").And.Contain("1 article above TLP:CLEAR");

        await Do(page, async p => { p.NewTitle = "Started from the story"; await p.NewTlpChanged(TlpLevel.Amber); });
        (await page.Html()).Should().Contain("CVE-2026-0002").And.NotContain("above TLP:AMBER");

        await Do(page, p => p.CreateNew());

        await using var check = SignedIn(app, SignIn.Analyst);
        var detail = await check.ServiceProvider.GetRequiredService<StoryService>().GetAsync(story);
        var started = detail!.Articles.Should().ContainSingle(a => a.Title == "Started from the story").Subject;
        started.Tlp.Should().Be(TlpLevel.Amber);
        started.Cves.Select(c => c.CveId).Should().Equal("CVE-2026-0002");
    }

    [Fact]
    public async Task AddAnExistingArticle_ListsOnlyThoseNotOnTheStory_AndAddsThePickedOne()
    {
        var story = await StoryAsync("Attach from page");
        var tag = Guid.NewGuid().ToString("N")[..6];
        Guid on, loose;
        await using (var scope = SignedIn(app, SignIn.Analyst))
        {
            var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
            on = await articles.CreateAsync($"Attach {tag} already on", "Vendor", null, TlpLevel.Clear);
            loose = await articles.CreateAsync($"Attach {tag} loose", "Vendor", null, TlpLevel.Clear);
            await scope.ServiceProvider.GetRequiredService<StoryService>().LinkAsync(on, story);
        }
        await using var page = await Open(story);

        await Do(page, p => p.StartAttach());
        await Do(page, p => p.FindCandidates($"attach {tag}"));
        page.Component.Candidates.Select(c => c.Id).Should().Equal(loose);
        (await page.Html()).Should().Contain("tide-story-attach");

        await Do(page, p => p.Attach(page.Component.Candidates[0]));

        page.Component.Detail!.Articles.Select(a => a.Id).Should().BeEquivalentTo([on, loose]);
    }
}
