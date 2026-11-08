using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Adversaries;
using Tide.Application.Articles;
using Tide.Application.Stack;
using Tide.Domain.Enums;
using Tide.Infrastructure.Persistence;
using Tide.Web.Components.Shared;

namespace Tide.IntegrationTests;

/// <summary>
/// The editor after the right rail's Related tab went: Threat actors is the adversary picker under Tags &amp; ATT&amp;CK ›
/// Context (the free-text box is gone), We run is under the "Why it matters" text, Similar articles is folded under
/// the threat story, and someone who can't edit sees the tags as read-only cards in the rail.
/// </summary>
public class EditorRailLayoutTests(TideApp app) : IClassFixture<TideApp>
{
    private async Task<Guid> NewArticle(string title, string? earlierActors = null)
    {
        await using var scope = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst);
        var id = await scope.ServiceProvider.GetRequiredService<ArticleService>().CreateAsync(title, "Vendor", null, TlpLevel.Clear);
        if (earlierActors is not null)
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.Articles.SingleAsync(a => a.Id == id)).Actors = earlierActors;
            await db.SaveChangesAsync();
        }
        return id;
    }

    private static int At(string html, string what)
    {
        var at = html.IndexOf(what, StringComparison.Ordinal);
        at.Should().BeGreaterThanOrEqualTo(0, $"the page has {what}");
        return at;
    }

    [Fact]
    public async Task ThreatActors_IsThePickerInContext_AndWeRun_IsUnderWhyItMatters()
    {
        var id = await NewArticle("layout-1 article");
        var html = await app.CreateClient().GetStringAsync($"/articles/{id}");

        html.Should().NotContain("id=\"actors\"", "the free-text box is gone").And.NotContain("placeholder=\"Scattered Spider, UNC3944\"");
        var context = At(html, "<h3 class=\"tide-subhead\">Context</h3>");
        var actors = At(html, $"for=\"adv-pick-{id}\">Threat actors</label>");
        actors.Should().BeGreaterThan(context).And.BeLessThan(At(html, "for=\"sectors\">Sectors</label>"), "it reads as the same field, where the box was");
        At(html, "id=\"sec-tags-body\"").Should().BeLessThan(context);

        var why = At(html, "id=\"f-why\"");
        var weRun = At(html, $"id=\"werun-h-{id}\" class=\"tide-subhead my-0\">We run</h3>");
        weRun.Should().BeGreaterThan(why).And.BeLessThan(At(html, "id=\"sec-rec\""), "We run sits inside why it matters, under its text");
        At(html, $"id=\"stack-pick-{id}\"").Should().BeGreaterThan(weRun);
        html.Should().NotContain("<datalist", "We run uses the same search picker as Threat actors");
        html.Split("Why it matters to us</h2>").Length.Should().Be(2, "it's no section of its own");
    }

    [Fact]
    public async Task TheRail_HasNoRelatedTab_AndSimilarArticles_IsFoldedUnderTheStory()
    {
        var id = await NewArticle("layout-2 article");
        var html = await app.CreateClient().GetStringAsync($"/articles/{id}");

        html.Should().NotContain("Related articles and their open actions").And.NotContain("tide-seg-count");
        var tabs = html[At(html, "class=\"tide-seg\" role=\"group\" aria-label=\"Show\"")..];
        tabs = tabs[..tabs.IndexOf("</div>", StringComparison.Ordinal)];
        tabs.Split("class=\"tide-seg-opt").Length.Should().Be(3, "two tabs: Team and Company-wide");

        var story = At(html, "for=\"story-pick\">Threat story");
        var similar = At(html, "Similar articles (0)");
        similar.Should().BeGreaterThan(story).And.BeLessThan(At(html, "<details class=\"tide-reader-details\""), "directly under the story");
        html.Should().Contain("aria-expanded=\"false\" aria-controls=\"similar-body\"")
            .And.Contain("id=\"similar-body\" class=\"tide-disclosure-body\" hidden").And.NotContain("tide-related-list");
        html[story..].Should().NotContain("Threat actors</h2>").And.NotContain("We run <a", "nothing tag-like is left in the rail for an analyst");
    }

    [Fact]
    public async Task SomeoneWhoCantEdit_SeesTheTagsAsReadOnlyCardsInTheRail()
    {
        var id = await NewArticle("layout-3 article");
        await using (var scope = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst))
        {
            var sp = scope.ServiceProvider;
            await sp.GetRequiredService<AdversaryService>().TagArticleAsync(id, await sp.GetRequiredService<AdversaryService>().SaveAsync(new AdversaryForm { Name = "layout-3 Spider" }));
            await sp.GetRequiredService<StackService>().TagArticleAsync(id, await sp.GetRequiredService<StackService>().SaveAsync(new StackForm { Name = "layout-3 Gateway", Versions = "9.1" }));
        }
        await using var scopeArticle = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst);
        var article = (await scopeArticle.ServiceProvider.GetRequiredService<ArticleService>().GetAsync(id))!;
        await using var r = await ComponentHarness.RenderAsync<ReaderPreview>(app, $"/articles/{id}", new()
        {
            ["State"] = new ArticleEditorPanel.State(article, ArticleForm.From(article), [], [], false),
        }, new ComponentHarness.SignIn("dev:owner", "Owner", "ActionOwner"));

        var html = await r.HtmlOnce("layout-3 Gateway");
        html.Should().Contain("<h2>We run <a class=\"ms-auto small fw-normal\" href=\"stack\">Our stack</a></h2>")
            .And.Contain("<h2>Threat actors <a class=\"ms-auto small fw-normal\" href=\"adversaries\">All profiles</a></h2>");
        (await r.HtmlOnce("layout-3 Spider")).Should().Contain("layout-3 Spider").And.Contain("9.1")
            .And.NotContain("role=\"combobox\" autocomplete=\"off\" spellcheck=\"false\" maxlength=\"100\"").And.NotContain("Untag");
    }

    [Fact]
    public async Task ShowRelated_OpensSimilarArticles_WhereThePanelIs()
    {
        var id = await NewArticle("layout-4 article");
        await using var scope = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst);
        var article = (await scope.ServiceProvider.GetRequiredService<ArticleService>().GetAsync(id))!;
        await using var r = await ComponentHarness.RenderAsync<ReaderPreview>(app, $"/articles/{id}", new()
        {
            ["State"] = new ArticleEditorPanel.State(article, ArticleForm.From(article), [], [], false, CanWrite: true),
        });
        r.Component.SimilarOpen.Should().BeFalse("folded at first");
        r.Component.SimilarTitle.Should().Be("Similar articles (0)");

        await r.Click(p => { p.ShowRelated(); return Task.CompletedTask; });

        r.Component.SimilarOpen.Should().BeTrue();
        var html = await r.Html();
        html.Should().Contain("aria-expanded=\"true\" aria-controls=\"similar-body\"").And.Contain("Nothing else shares a CVE, a link or the story with this article.")
            .And.Contain("class=\"tide-reader-doc tide-edition\"", "the edition preview stays: opening Similar articles doesn't replace it");
    }

    [Fact]
    public async Task TheEarlierNote_OffersItsMatchAsATag_AndItsLeftoverShowsUntilCleared()
    {
        Guid spider;
        await using (var scope = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst))
            spider = await scope.ServiceProvider.GetRequiredService<AdversaryService>().SaveAsync(new AdversaryForm { Name = "note-5 Scattered Spider" });
        var id = await NewArticle("layout-5 article", "note-5 Scattered Spider, UNC9999");
        await using var r = await ComponentHarness.RenderAsync<ArticleAdversaryCard>(app, $"/articles/{id}", new() { ["ArticleId"] = id, ["CanWrite"] = true });

        var html = await r.Html();
        html.Should().Contain("Earlier note names note-5 Scattered Spider:").And.Contain("Tag note-5 Scattered Spider</button>")
            .And.Contain("<span>Earlier note: “UNC9999”</span>").And.Contain("aria-label=\"Clear the earlier note\"");

        await r.Click(c => c.Tag(spider));
        html = await r.Html();
        html.Should().Contain("Untag note-5 Scattered Spider").And.NotContain("Earlier note names").And.Contain("Earlier note: “UNC9999”", "the leftover stays until cleared");

        await r.Click(c => c.ClearNote());
        (await r.Html()).Should().NotContain("Earlier note");
        r.Component.Mine!.Leftover.Should().BeEmpty();
    }

    [Fact]
    public async Task MentionFromWeRunOrSimilarArticles_AddsToWhyItMatters()
    {
        var id = await NewArticle("layout-6 article");
        await using var r = await ComponentHarness.RenderAsync<ArticleEditorPanel>(app, $"/articles/{id}", new() { ["Id"] = id });

        // Both wire their "Mention" to the editor's MentionAsync: We run inside the panel, Similar articles through the page.
        await r.Click(p => p.MentionAsync("We run layout-6 Gateway (9.1)."));
        await r.Click(p => p.MentionAsync("A-12 (open)"));

        (await r.Html()).Should().Contain("We run layout-6 Gateway (9.1).&#xA;A-12 (open)</textarea>");
        var page = await File.ReadAllTextAsync(Path.Combine(WebRoot(), "Components", "Pages", "ArticleEdit.razor"));
        page.Should().Contain("OnMention=\"Mention\"");
        var panel = await File.ReadAllTextAsync(Path.Combine(WebRoot(), "Components", "Shared", "ArticleEditorPanel.razor"));
        panel.Should().Contain("<ArticleStackCard ArticleId=\"_article.Id\" CanWrite=\"_canWrite\" OnMention=\"MentionAsync\" />");
    }

    private static string WebRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "Tide.sln"))) dir = Path.GetDirectoryName(dir)!;
        return Path.Combine(dir, "src", "Tide.Web");
    }
}
