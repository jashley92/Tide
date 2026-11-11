using System.Text.RegularExpressions;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Adversaries;
using Tide.Application.Articles;
using Tide.Application.Briefs;
using Tide.Application.Stack;
using Tide.Domain.Enums;
using Tide.Infrastructure.Persistence;
using Tide.Web.Components.Shared;

namespace Tide.IntegrationTests;

/// <summary>
/// Where the article editor puts things: a section index across the top, then the form as paired cards (Assessment
/// beside Tags &amp; ATT&amp;CK, Bottom line beside Why it matters, Bullets across both columns with the screenshots
/// in it, Recommended actions beside Indicators and CVEs), and what readers get in the context rail ("Context · what
/// readers get": Preview, Actions and Details, and the Team or Company-wide edition). Threat actors is the adversary
/// picker in the Tags card, We run is under the "Why it matters" text, Similar articles is folded under the threat
/// story in the rail, and someone who can't edit reads the preview in the page, the tags as read-only cards.
/// </summary>
public partial class EditorRailLayoutTests(TideApp app) : IClassFixture<TideApp>
{
    private async Task<Guid> NewArticle(string title, string? earlierActors = null, Action<Domain.Entities.Article>? fill = null)
    {
        await using var scope = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst);
        var id = await scope.ServiceProvider.GetRequiredService<ArticleService>().CreateAsync(title, "Vendor", null, TlpLevel.Clear);
        if (earlierActors is not null || fill is not null)
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var a = await db.Articles.Include(x => x.Bullets).SingleAsync(a => a.Id == id);
            if (earlierActors is not null) a.Actors = earlierActors;
            fill?.Invoke(a);
            await db.SaveChangesAsync();
        }
        return id;
    }

    // A threat whose "Why it matters" is CLEAR: the company-wide edition would carry it, so the editor warns.
    private Task<Guid> WhyAtClear(string title) => NewArticle(title, fill: a =>
    {
        a.Classification = Classification.Threat;
        a.Priority = Priority.P2;
        a.WhyItMatters = "Our remote-access gateways run the affected firmware.";
        a.WhyItMattersTlp = TlpLevel.Clear;
    });

    private async Task OnTodaysBrief(Guid id)
    {
        await using var scope = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst);
        var briefs = scope.ServiceProvider.GetRequiredService<BriefService>();
        await briefs.AddArticleAsync((await briefs.GetOrStartAsync(briefs.CurrentDate)).Id, id);
    }

    private async Task<Domain.Entities.Article> Saved(Guid id)
    {
        await using var scope = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst);
        return (await scope.ServiceProvider.GetRequiredService<ArticleService>().GetAsync(id))!;
    }

    private static int At(string html, string what)
    {
        var at = html.IndexOf(what, StringComparison.Ordinal);
        at.Should().BeGreaterThanOrEqualTo(0, $"the page has {what}");
        return at;
    }

    private static int[] All(string html, string what) =>
        Regex.Matches(html, Regex.Escape(what)).Select(m => m.Index).ToArray();

    [Fact]
    public async Task TheForm_IsPairedCards_UnderASectionIndex()
    {
        var id = await NewArticle("layout-pairs article");
        var html = await app.CreateClient().GetStringAsync($"/articles/{id}");

        var index = At(html, "aria-label=\"Article sections\"");
        var pairs = All(html, "<div class=\"tide-editor-pair\">");
        pairs.Should().HaveCount(3, "Assessment and Tags, Bottom line and Why it matters, Recommended and Indicators");
        int assessment = At(html, "id=\"sec-assessment\""), tags = At(html, "id=\"sec-tags\""), bluf = At(html, "id=\"sec-bluf\""),
            why = At(html, "id=\"sec-why\""), bullets = At(html, "id=\"sec-bullets\""), shots = At(html, "id=\"sec-shots\""),
            rec = At(html, "id=\"sec-rec\""), ind = At(html, "id=\"sec-ind\"");

        index.Should().BeLessThan(pairs[0], "the index is above the cards");
        new[] { pairs[0], assessment, tags, pairs[1], bluf, why, bullets, shots, pairs[2], rec, ind }.Should().BeInAscendingOrder(
            "each pair's two cards follow its start, and Bullets, with its screenshots, sits between the second pair and the third");
        html[why..rec].Should().Contain("aria-labelledby=\"sec-rec\"", "Recommended actions is a card of its own, not part of Why it matters");

        html.Should().NotContain("sec-tags-body", "Tags & ATT&CK is open, beside Assessment, not folded at the bottom")
            .And.NotContain("tags &#xB7;");
    }

    [Fact]
    public async Task ThreatActors_IsThePickerInTheTagsCard_AndWeRun_IsUnderWhyItMatters()
    {
        var id = await NewArticle("layout-1 article");
        var html = await app.CreateClient().GetStringAsync($"/articles/{id}");

        html.Should().NotContain("id=\"actors\"", "the free-text box is gone").And.NotContain("placeholder=\"Scattered Spider, UNC3944\"");
        var tags = At(html, "id=\"sec-tags\"");
        var context = At(html, "<h3 class=\"tide-subhead\">Context</h3>");
        var actors = At(html, $"for=\"adv-pick-{id}\">Threat actors</label>");
        context.Should().BeGreaterThan(tags).And.BeLessThan(At(html, "id=\"sec-bluf\""), "the context is in the Tags card, in the first pair");
        actors.Should().BeGreaterThan(context).And.BeLessThan(At(html, "for=\"sectors\">Sectors</label>"), "it reads as the same field, where the box was");

        var why = At(html, "id=\"f-why\"");
        var weRun = At(html, $"id=\"werun-h-{id}\" class=\"tide-subhead my-0\">We run</h3>");
        weRun.Should().BeGreaterThan(why).And.BeLessThan(At(html, "id=\"sec-bullets\""), "We run sits inside why it matters, under its text");
        At(html, $"id=\"stack-pick-{id}\"").Should().BeGreaterThan(weRun);
        html.Should().NotContain("<datalist", "We run uses the same search picker as Threat actors");
        html.Split("Why it matters to us</h2>").Length.Should().Be(2, "it's no section of its own");
    }

    [Fact]
    public async Task TheRail_IsWhatReadersGet_WithItsTabsAndEditions_AndSimilarArticlesFoldedUnderTheStory()
    {
        var id = await NewArticle("layout-2 article");
        var html = await app.CreateClient().GetStringAsync($"/articles/{id}");

        var rail = html[At(html, "<aside id=\"tide-rail\" class=\"tide-rail\" aria-label=\"Context &#xB7; what readers get\"")..];
        rail = rail[..rail.IndexOf("</aside>", StringComparison.Ordinal)];
        At(html, "id=\"tide-rail\"").Should().BeGreaterThan(At(html, "class=\"tide-sticky-save"), "the rail is the layout's, after the page");
        html.Should().NotContain("class=\"tide-editor-side\"", "nothing of the preview is left in the page for an analyst");

        rail.Should().Contain("aria-label=\"Beside the article\"").And.Contain("<span>Preview</span>").And.Contain("<span>Actions</span>")
            .And.Contain("<span>Details</span>").And.Contain("What readers get");
        var seg = rail[At(rail, "class=\"tide-seg\" role=\"group\" aria-label=\"Show\"")..];
        seg = seg[..seg.IndexOf("</div>", StringComparison.Ordinal)];
        seg.Split("class=\"tide-seg-opt").Length.Should().Be(3, "two choices: Team and Company-wide");
        seg.Should().Contain(">Team</button>").And.Contain(">Company-wide</button>");

        rail.Should().NotContain("Related articles and their open actions").And.NotContain("tide-seg-count");
        var story = At(rail, "for=\"story-pick\">Threat story");
        At(rail, "Similar articles (0)").Should().BeGreaterThan(story, "directly under the story");
        rail.Should().Contain("aria-expanded=\"false\" aria-controls=\"similar-body\"")
            .And.Contain("id=\"similar-body\" class=\"tide-disclosure-body\" hidden").And.NotContain("tide-related-list");
        rail[story..].Should().NotContain("Threat actors</h2>").And.NotContain("We run <a", "nothing tag-like is left in the rail for an analyst");
    }

    [Fact]
    public async Task SomeoneWhoCantEdit_ReadsThePreviewInThePage_WithTheTagsAsReadOnlyCards()
    {
        var id = await NewArticle("layout-3 article");
        await using (var scope = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst))
        {
            var sp = scope.ServiceProvider;
            await sp.GetRequiredService<AdversaryService>().TagArticleAsync(id, await sp.GetRequiredService<AdversaryService>().SaveAsync(new AdversaryForm { Name = "layout-3 Spider" }));
            await sp.GetRequiredService<StackService>().TagArticleAsync(id, await sp.GetRequiredService<StackService>().SaveAsync(new StackForm { Name = "layout-3 Gateway", Versions = "9.1" }));
        }
        var owner = new ComponentHarness.SignIn("dev:owner", "Owner", "ActionOwner");
        await using (var page = await ComponentHarness.RenderAsync<Web.Components.Pages.ArticleEdit>(app, $"/articles/{id}", new() { ["Id"] = id }, owner))
        {
            var html = await page.HtmlOnce("What readers get");
            html.Should().NotContain("id=\"tide-rail\"", "the preview is the page for someone who can't edit, not a rail beside it")
                .And.Contain("class=\"tide-editor-side\"").And.Contain("data-readonly=\"true\"");
        }

        var article = await Saved(id);
        await using var r = await ComponentHarness.RenderAsync<ReaderPreview>(app, $"/articles/{id}", new()
        {
            ["State"] = new ArticleEditorPanel.State(article, ArticleForm.From(article), [], [], false),
        }, owner);

        var preview = await r.HtmlOnce("layout-3 Gateway");
        preview.Should().Contain("<h2>We run <a class=\"ms-auto small fw-normal\" href=\"stack\">Our stack</a></h2>")
            .And.Contain("<h2>Threat actors <a class=\"ms-auto small fw-normal\" href=\"adversaries\">All profiles</a></h2>");
        (await r.HtmlOnce("layout-3 Spider")).Should().Contain("layout-3 Spider").And.Contain("9.1")
            .And.NotContain("role=\"combobox\" autocomplete=\"off\" spellcheck=\"false\" maxlength=\"100\"").And.NotContain("Untag");
    }

    [Fact]
    public async Task TheSectionIndex_SaysWhatsDone_AndHowMany()
    {
        Guid tag;
        using (var scope = app.Services.CreateScope())
            tag = scope.ServiceProvider.GetRequiredService<AppDbContext>().ScorecardTags.First().Id;
        var id = await NewArticle("layout-index article", fill: a =>
        {
            a.Classification = Classification.Threat;
            a.Priority = Priority.P1;
            a.Bluf = "Exploited in the wild.";
            a.Bullets.Add(new Domain.Entities.ArticleBullet { Text = "One" });
            a.Bullets.Add(new Domain.Entities.ArticleBullet { Text = "Two", SortOrder = 1 });
            a.RecommendedActions = "- Patch today\n- Hunt for the indicators";
            a.Tags.Add(new Domain.Entities.ArticleTag { TagId = tag });
            a.Techniques.Add(new Domain.Entities.ArticleTechnique { TechniqueId = "T1190" });
            a.Indicators.Add(new Domain.Entities.ArticleIndicator { Type = IndicatorType.IpAddress, Value = "203.0.113.45" });
            a.Cves.Add(new Domain.Entities.ArticleCve { CveId = "CVE-2026-0001" });
            a.WhyItMatters = "We run it.";
            a.WhyItMattersTlp = TlpLevel.Clear;
        });

        var html = await app.CreateClient().GetStringAsync($"/articles/{id}");
        var nav = html[At(html, "aria-label=\"Article sections\"")..];
        nav = nav[..nav.IndexOf("</nav>", StringComparison.Ordinal)];

        IndexItem().Matches(nav).Select(m => (m.Groups["label"].Value.Trim(), m.Groups["state"].Value.Trim())).Should().Equal(
            ("Assessment", "&#x2713;"),
            ("Bottom line", "&#x2713;"),
            ("Bullets", "2"),
            ("Why it matters", "check TLP"),
            ("Recommended", "2"),
            ("Indicators &amp; CVEs", "1 &#xB7; 1"),
            ("Tags &amp; ATT&amp;CK", "1 &#xB7; 1"));
    }

    [Fact]
    public async Task WhyItMatters_ReachingEveryone_IsFixedInItsCard_SetToAmberOrKeepIt_ThroughTheBriefsAnswers()
    {
        var id = await WhyAtClear("layout-why article");
        await OnTodaysBrief(id);
        await using var r = await ComponentHarness.RenderAsync<ArticleEditorPanel>(app, $"/articles/{id}", new() { ["Id"] = id });

        var html = await r.Html();
        var card = html[html.LastIndexOf("<section", At(html, "aria-labelledby=\"sec-why\""), StringComparison.Ordinal)..At(html, "id=\"sec-bullets\"")];
        card.Should().Contain("tide-section-warn").And.Contain("Will reach the company-wide edition.")
            .And.Contain(">Set to AMBER</button>").And.Contain(">Keep it</button>");

        // Set to AMBER, with nothing else unsaved and the article on today's open brief: the running order's own answer,
        // saved, checked and audited there; the form reads the article again, clean.
        await r.Click(p => p.SetWhyAmberAsync());
        (await Saved(id)).WhyItMattersTlp.Should().Be(TlpLevel.Amber);
        html = await r.Html();
        html.Should().NotContain("Will reach the company-wide edition.").And.Contain("All changes saved");
        (await HistoryRows(id)).Should().BeGreaterThan(0, "the change is audited");
    }

    [Fact]
    public async Task KeepIt_RecordsThatWhyItMattersIsMeantForEveryone_AndLeavesItsMarking()
    {
        var id = await WhyAtClear("layout-keep article");
        await OnTodaysBrief(id);
        await using var r = await ComponentHarness.RenderAsync<ArticleEditorPanel>(app, $"/articles/{id}", new() { ["Id"] = id });

        await r.Click(p => p.KeepWhyAsync());

        var saved = await Saved(id);
        (saved.WhyItMattersKept, saved.WhyItMattersTlp).Should().Be((true, TlpLevel.Clear));
        (await r.Html()).Should().NotContain("Will reach the company-wide edition.").And.Contain("All changes saved");
    }

    [Fact]
    public async Task OffABrief_SetToAmber_IsAnUnsavedEdit_AndKeepItIsntOffered()
    {
        var id = await WhyAtClear("layout-off-brief article");
        await using var r = await ComponentHarness.RenderAsync<ArticleEditorPanel>(app, $"/articles/{id}", new() { ["Id"] = id });

        var html = await r.Html();
        html[At(html, "aria-labelledby=\"sec-why\"")..At(html, "id=\"sec-bullets\"")].Should().Contain(">Set to AMBER</button>").And.NotContain(">Keep it</button>");

        await r.Click(p => p.SetWhyAmberAsync());

        (await Saved(id)).WhyItMattersTlp.Should().Be(TlpLevel.Clear, "nothing is saved until Save");
        (await r.Html()).Should().Contain("Unsaved changes").And.NotContain("Will reach the company-wide edition.");
        var ok = false;
        await r.Click(async p => ok = await p.Save());
        ok.Should().BeTrue();
        (await Saved(id)).WhyItMattersTlp.Should().Be(TlpLevel.Amber);
    }

    private async Task<int> HistoryRows(Guid id)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var key = id.ToString();
        return await db.AuditLog.CountAsync(e => e.EntityKey == key && e.ChangesJson != null && e.ChangesJson.Contains("WhyItMattersTlp"));
    }

    [Fact]
    public async Task ShowRelated_OpensSimilarArticles_WhereThePanelIs()
    {
        var id = await NewArticle("layout-4 article");
        var article = await Saved(id);
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

    // An item of the section index: its label, then what it says of the section (✓, a count, "check TLP" or "Needed").
    [GeneratedRegex("<button type=\"button\"[^>]*>(?<label>[^<]*)(?:<span class=\"visually-hidden\">,</span>)?(?:<span class=\"tide-pill-state[^\"]*\">(?<state>[^<]*)</span>)?\\s*</button>")]
    private static partial Regex IndexItem();

    private static string WebRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "Tide.sln"))) dir = Path.GetDirectoryName(dir)!;
        return Path.Combine(dir, "src", "Tide.Web");
    }
}
