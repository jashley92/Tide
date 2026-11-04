using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Articles;
using Tide.Application.Briefs;
using Tide.Application.Distribution;
using Tide.Application.Metrics;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Infrastructure.Persistence;
using Tide.Web.Components.Layout;
using Tide.Web.Components.Shared;
using Tide.Web.Services;

namespace Tide.IntegrationTests;

/// <summary>
/// "What readers get" beside the article editor (ReaderPreview): the article as each edition carries it, with what's left
/// out and what's missing shown to the writer only, never to readers.
/// </summary>
public class ReaderPreviewTests(TideApp app) : IClassFixture<TideApp>
{
    private static Article NewArticle(TlpLevel tlp = TlpLevel.Clear, Action<Article>? fill = null)
    {
        var a = new Article
        {
            Title = "Reader check", AuthorId = "x", AuthorName = "X", Tlp = tlp, Bluf = "Exploited in the wild.", SourceName = "Vendor",
            WhyItMatters = "We run the affected gateway.", WhyItMattersTlp = TlpLevel.Amber,
        };
        a.Bullets.Add(new ArticleBullet { Text = "Everyone may read this", Tlp = tlp, SortOrder = 0 });
        a.Bullets.Add(new ArticleBullet { Text = "Partner detail", Tlp = TlpLevel.Amber, SortOrder = 1 });
        a.Indicators.Add(new ArticleIndicator { Type = IndicatorType.Domain, Value = "evil.example" });
        fill?.Invoke(a);
        return a;
    }

    private async Task<Guid> Save(Article a)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (db.ScorecardTags.FirstOrDefault() is { } tag) a.Tags.Add(new ArticleTag { TagId = tag.Id });
        db.Articles.Add(a);
        await db.SaveChangesAsync();
        return a.Id;
    }

    // The preview's body: from its heading to the folded Details.
    private async Task<string> Preview(Guid id, string query = "")
    {
        var html = await app.CreateClient().GetStringAsync($"/articles/{id}{query}");
        var at = html.IndexOf("class=\"tide-card tide-reader\"", StringComparison.Ordinal);
        at.Should().BePositive("the reader preview replaces the right rail");
        return html[at..html.IndexOf("<details class=\"tide-reader-details\"", at, StringComparison.Ordinal)];
    }

    [Fact]
    public async Task TheTeamEdition_IsShownFirst_WithEverythingUpToItsCeiling()
    {
        var body = await Preview(await Save(NewArticle()));

        body.Should().Contain("What readers get").And.Contain("Security team edition")
            .And.Contain("aria-pressed=\"true\" aria-keyshortcuts=\"1\"").And.Contain("aria-pressed=\"false\" aria-keyshortcuts=\"2\"")
            .And.Contain("Everyone may read this").And.Contain("Partner detail").And.Contain("We run the affected gateway.")
            .And.Contain("evil[.]example").And.Contain("Ready to brief")
            .And.NotContain(ReaderPreview.LeftOutMarker).And.NotContain("Includes unsaved changes");
    }

    [Fact]
    public async Task TheCompanyWideEdition_MarksWhatItLeavesOut_InPlace()
    {
        var body = await Preview(await Save(NewArticle()), "?edition=company");

        body.Should().Contain("Company-wide edition").And.Contain("aria-pressed=\"true\" aria-keyshortcuts=\"2\"")
            .And.Contain("Everyone may read this")
            .And.NotContain("Partner detail").And.NotContain("We run the affected gateway.").And.NotContain("evil[.]example");
        body.Split(ReaderPreview.LeftOutMarker).Should().HaveCount(3, "the AMBER bullet and why it matters are each marked");
        body.Should().Contain("<li class=\"tide-left-out-item\"><p class=\"tide-left-out\">Left out of this edition &#xB7; <span class=\"tide-tlp tide-tlp-amber\"")
            .And.Contain("Indicators and follow-up actions aren't part of the company-wide edition.")
            .And.NotContain("restricted part", "the markers say it where it happens");
    }

    [Fact]
    public async Task AnArticleAboveTheCeiling_IsMarkedLeftOutWhole()
    {
        var body = await Preview(await Save(NewArticle(TlpLevel.Amber)), "?edition=company");

        body.Should().Contain("class=\"tide-reader-out\"").And.Contain("the whole article is <span class=\"tide-tlp tide-tlp-amber\"")
            .And.Contain(ReaderPreview.OnlyYou).And.NotContain("Everyone may read this").And.NotContain("Exploited in the wild.");
    }

    [Fact]
    public async Task AMissingPart_IsAPlaceholder_ThatGoesToItsField()
    {
        var body = await Preview(await Save(NewArticle(fill: a =>
        {
            a.Bluf = "";
            a.Classification = Classification.Threat;
            a.Priority = Priority.P2;
        })));

        body.Should().Contain("2 things missing")
            .And.Contain("<button type=\"button\" class=\"tide-placeholder\"")
            .And.Contain("Add the bottom line (BLUF).").And.Contain("A threat should say what to do (recommended actions).")
            .And.Contain("<span class=\"visually-hidden\">Blocking:</span>").And.Contain("Write it");
        body.IndexOf("Add the bottom line", StringComparison.Ordinal).Should()
            .BeLessThan(body.IndexOf("Everyone may read this", StringComparison.Ordinal), "the placeholder stands where the bottom line goes");
    }

    [Fact]
    public async Task Details_HoldWhatTheRailDid_AndBothEditionsVerdicts()
    {
        var id = await Save(NewArticle(fill: a => a.Cves.Add(new ArticleCve { CveId = "CVE-2026-0001" })));
        var html = await app.CreateClient().GetStringAsync($"/articles/{id}");
        var details = html[html.IndexOf("<details class=\"tide-reader-details\"", StringComparison.Ordinal)..];

        details.Should().Contain("Both editions").And.Contain("Included in full")
            .And.Contain("Partly included: 2 parts above TLP:GREEN left out")
            .And.Contain("Actions").And.Contain($"actions/new?article={id}")
            .And.Contain("CVE context").And.Contain("CVE-2026-0001")
            .And.NotContain("Threat story", "the story picker is out in the open, above Details");
        html.Should().Contain("Threat story");
        html.Should().NotContain("Ready to brief?", "the checklist card is gone: its gaps are placeholders now");
    }

    [Fact]
    public async Task TheLeftOutMarker_NeverReachesReaders()
    {
        var article = NewArticle();
        var amber = NewArticle(TlpLevel.Amber);
        await Save(article);
        await Save(amber);
        (await Preview(article.Id, "?edition=company")).Should().Contain(ReaderPreview.LeftOutMarker);

        var brief = new Brief { BriefDate = new DateOnly(2026, 10, 29), AnalystName = "Robin" };
        brief.Items.Add(new BriefItem { Article = article, ArticleId = article.Id, SortOrder = 0 });
        brief.Items.Add(new BriefItem { Article = amber, ArticleId = amber.Id, SortOrder = 1 });
        var renderer = app.Services.GetRequiredService<BriefRenderer>();
        var context = new BriefRenderContext("CTI", null, "https://tide.example.org", img => "cid:" + img.ContentId);

        foreach (var edition in new[] { BriefEditions.Build(brief, Edition.Public, TlpLevel.Green), BriefEditions.Build(brief, Edition.Team, TlpLevel.AmberStrict) })
        {
            var sent = renderer.Render(edition, context);
            foreach (var copy in new[] { sent.EmailHtml, sent.Text, sent.WebHtml })
                copy.Should().NotContain(ReaderPreview.LeftOutMarker).And.NotContain(ReaderPreview.OnlyYou);

            // The brief's own edition preview draws the same articles, without the editor's markers.
            await using var r = await ComponentHarness.RenderAsync<EditionView>(app, "/", new() { ["Edition"] = edition });
            var shown = await r.Html();
            shown.Should().Contain("Reader check").And.NotContain(ReaderPreview.LeftOutMarker).And.NotContain("tide-placeholder");
        }
    }

    // A threat with nothing missing whose "Why it matters" is TLP:CLEAR, so the company-wide edition carries it.
    private static Article OpenWhy() => NewArticle(fill: a =>
    {
        a.Classification = Classification.Threat;
        a.Priority = Priority.P2;
        a.RecommendedActions = "Patch the gateway.";
        a.WhyItMattersTlp = TlpLevel.Clear;
    });

    [Fact]
    public async Task AThreatsWhyItMatters_ThatReachesEveryone_IsWarnedAbout_WithAOneClickFix()
    {
        foreach (var query in new[] { "", "?edition=company" })
        {
            var body = await Preview(await Save(OpenWhy()), query);
            body.Should().Contain("1 thing missing")
                .And.Contain("<span class=\"visually-hidden\">Warning:</span>")
                .And.Contain("Why it matters is TLP:CLEAR and will reach the company-wide edition.")
                .And.Contain("Check it")
                .And.Contain("class=\"tide-placeholder-row\"").And.Contain(">Mark AMBER<span class=\"visually-hidden\"> (why it matters)</span></button>");
        }
    }

    [Fact]
    public async Task MarkAmber_RaisesWhyItMatters_AsAnUnsavedEdit()
    {
        var id = await Save(OpenWhy());
        ArticleEditorPanel.State? seen = null;
        await using var r = await ComponentHarness.RenderAsync<ArticleEditorPanel>(app, $"/articles/{id}", new()
        {
            ["Id"] = id,
            ["Aside"] = (RenderFragment<ArticleEditorPanel.State>)(state => b => { seen = state; b.AddContent(0, "The preview"); }),
        });
        seen!.CanWrite.Should().BeTrue();

        await r.Click(p => p.FixAsync(ArticleFix.WhyItMattersAmber));
        await r.Html();

        seen!.Form.WhyItMattersTlp.Should().Be(TlpLevel.Amber);
        seen.Dirty.Should().BeTrue("it's saved with the rest of the article");
        ArticlePreview.Build(seen.Form, Edition.Public, TlpLevel.Green, companyCeiling: TlpLevel.Green).Checks
            .Should().BeEmpty("the company-wide edition now leaves it out");
        r.Scope.ServiceProvider.GetRequiredService<ToastService>().Toasts
            .Should().Contain(t => t.Message == "Why it matters is marked TLP:AMBER. Save to keep it.");
    }

    [Fact]
    public async Task TheTeamEditionsPreview_OpensWithTheDaysNumbers_AsItsEmailDoes_AndTheCompanyWideOneNever()
    {
        var group = new MetricGroup { Name = "Alert queue" };
        var metric = new MetricDefinition { Name = "Older than 24 hours", Group = group };
        var friday = new DateOnly(2026, 10, 23);
        var tile = new MetricTile(metric, 7, null, new MetricComparison(friday, 4, 3, MetricTrend.Worse), MetricLevel.Warn, [2m, 4m, 7m]);
        var day = new MetricDay(new DateOnly(2026, 10, 26), [new MetricGroupDay(group, [tile])]);
        var brief = new Brief { BriefDate = day.Date, AnalystName = "Robin" };
        var article = NewArticle();
        brief.Items.Add(new BriefItem { Article = article, ArticleId = article.Id, SortOrder = 0 });

        await using (var r = await ComponentHarness.RenderAsync<EditionView>(app, "/", new()
                     {
                         ["Edition"] = BriefEditions.Build(brief, Edition.Team, TlpLevel.AmberStrict), ["Metrics"] = day,
                     }))
        {
            var team = await r.Html();
            team.Should().Contain("The day&#x27;s numbers").And.Contain("<caption>Alert queue</caption>")
                .And.Contain("<th scope=\"row\">Older than 24 hours</th>").And.Contain("tide-level-warn")
                .And.Contain("class=\"tide-trend-worse\"").And.Contain(" vs Fri").And.Contain("tide-spark");
            team.IndexOf("The day&#x27;s numbers", StringComparison.Ordinal).Should()
                .BeLessThan(team.IndexOf("Reader check", StringComparison.Ordinal), "the numbers open the edition");
        }

        await using var company = await ComponentHarness.RenderAsync<EditionView>(app, "/", new()
        {
            ["Edition"] = BriefEditions.Build(brief, Edition.Public, TlpLevel.Green), ["Metrics"] = day,
        });
        (await company.Html()).Should().NotContain("numbers").And.NotContain("Older than 24 hours");
    }

    [Fact]
    public void KeysOneAndTwo_AreListed_AsCharacterShortcuts()
    {
        var article = Shortcuts.For("articles/" + Guid.NewGuid()).First(g => g.Title == "Article");

        article.Keys.Should().Contain(k => k.Keys.SequenceEqual(new[] { "1" }) && k.Letter && k.What.Contains("Team"))
            .And.Contain(k => k.Keys.SequenceEqual(new[] { "2" }) && k.Letter && k.What.Contains("Company-wide"));
    }
}
