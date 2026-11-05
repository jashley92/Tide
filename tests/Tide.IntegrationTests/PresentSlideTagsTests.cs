using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Articles;
using Tide.Application.Briefs;
using Tide.Application.Distribution;
using Tide.Application.Org;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Infrastructure.Persistence;
using Tide.Web.Components.Pages;
using Tide.Web.Components.Shared;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>
/// The tag row on an article's slide: its ATT&amp;CK tactics in kill-chain order and its scorecard tags by category, each
/// edition showing only what that edition's email and web copy show.
/// </summary>
public class PresentSlideTagsTests(TideApp app) : IClassFixture<TideApp>
{
    private DateOnly Today => app.Services.GetRequiredService<OrgClock>().CurrentBriefDate;

    private static DateOnly BusinessDaysBefore(DateOnly day, int n)
    {
        for (var i = 0; i < n; i++) day = WorkingDays.Weekdays.Previous(day);
        return day;
    }

    private sealed record Setup(DateOnly Day, string Tagged, string Plain, string[] TagNames);

    // A past day's brief: an article with four techniques under three tactics and tags in two scorecard categories, then one with neither.
    private async Task<Setup> Brief(int daysBack)
    {
        var day = BusinessDaysBefore(Today, daysBack);
        var tagged = $"Tagged slide {daysBack}";
        var plain = $"Plain slide {daysBack}";
        Guid first, second;
        await using (var scope = SignedIn(app, SignIn.Analyst))
        {
            var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
            var briefs = scope.ServiceProvider.GetRequiredService<BriefService>();
            var brief = await briefs.GetOrStartAsync(day);
            first = await articles.CreateAsync(tagged, "Vendor", null, TlpLevel.Clear);
            second = await articles.CreateAsync(plain, "Vendor", null, TlpLevel.Clear);
            await briefs.AddArticleAsync(brief.Id, first);
            await briefs.AddArticleAsync(brief.Id, second);
        }

        var names = new[] { $"Ransomware {daysBack}", $"Data extortion {daysBack}", $"Supplier breach {daysBack}" };
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var threats = new ScorecardCategory { Key = $"threats-{daysBack}", Name = $"Threat types {daysBack}", SortOrder = 0 };
            var third = new ScorecardCategory { Key = $"third-{daysBack}", Name = $"Third-party risk {daysBack}", SortOrder = 1 };
            var tags = new[]
            {
                new ScorecardTag { Key = $"ransomware-{daysBack}", Name = names[0], Category = threats, SortOrder = 0 },
                new ScorecardTag { Key = $"extortion-{daysBack}", Name = names[1], Category = threats, SortOrder = 1 },
                new ScorecardTag { Key = $"supplier-{daysBack}", Name = names[2], Category = third, SortOrder = 0 },
            };
            db.ScorecardCategories.AddRange(threats, third);
            db.ScorecardTags.AddRange(tags);
            db.ArticleTags.AddRange(tags.Select(t => new ArticleTag { ArticleId = first, TagId = t.Id }));
            db.ArticleTechniques.AddRange(new[] { "T1486", "T1566.001", "T1059.001", "T1190" }
                .Select(id => new ArticleTechnique { ArticleId = first, TechniqueId = id }));
            await db.SaveChangesAsync();
        }
        return new Setup(day, tagged, plain, names);
    }

    private Task<Rendered<Present>> Open(DateOnly day) =>
        RenderAsync<Present>(app, $"/present/{day:yyyy-MM-dd}", new() { ["Day"] = $"{day:yyyy-MM-dd}" });

    private static Task Do(Rendered<Present> r, Action<Present> what) =>
        r.Click(async p =>
        {
            what(p);
            await p.OnPresentKey("render");
        });

    private static async Task ToArticle(Rendered<Present> r, string title)
    {
        await r.Click(p => p.OnPresentKey("last"));
        for (var i = 0; i < 20 && !(await r.Html()).Contains($"<h2>{title}</h2>", StringComparison.Ordinal); i++)
            await r.Click(p => p.OnPresentKey("prev"));
        (await r.Html()).Should().Contain($"<h2>{title}</h2>");
    }

    // The slide's tag row, from its start to the bottom line under it.
    private static string Row(string html)
    {
        var at = html.IndexOf("<div class=\"tide-slide-tags\">", StringComparison.Ordinal);
        at.Should().BePositive("the slide has a tag row");
        return html[at..html.IndexOf("tide-slide-bluf", at, StringComparison.Ordinal)];
    }

    [Fact]
    public async Task TheTeamEdition_ShowsTacticsInKillChainOrder_AndScorecardTagsByCategory()
    {
        var s = await Brief(91);
        await using var r = await Open(s.Day);
        await ToArticle(r, s.Tagged);
        var row = Row(await r.Html());

        Regex.Matches(row, "class=\"tide-slide-tag-name\">([^<]+)<").Select(m => m.Groups[1].Value)
            .Should().Equal(["Initial Access", "Execution", "Impact"], "tactics in the matrix's order");
        row.Should().Contain($"style=\"--tac:{Ui.TacticColor(MitreTactic.InitialAccess)}\"").And.Contain(Ui.TacticGlyph(MitreTactic.Impact));
        row.Should().Contain("title=\"T1190 Exploit Public-Facing Application; T1566.001 Spearphishing Attachment\"", "each tactic's techniques are in its title")
            .And.Contain("<span class=\"visually-hidden\">: T1190 Exploit Public-Facing Application; T1566.001 Spearphishing Attachment</span>", "and read out")
            .And.Contain("<span class=\"tide-slide-tag-n\" aria-hidden=\"true\">2</span>", "a tactic with two techniques counts them");

        row.Should().Contain("class=\"tide-slide-tag-cat\" aria-hidden=\"true\">Threat types 91<")
            .And.Contain("aria-label=\"Scorecard: Third-party risk 91\"", "two categories: grouped by name");
        foreach (var name in s.TagNames) row.Should().Contain($">{name}</li>");
        row.IndexOf(s.TagNames[0], StringComparison.Ordinal).Should().BeLessThan(row.IndexOf(s.TagNames[2], StringComparison.Ordinal), "the scorecard's order");
    }

    [Fact]
    public async Task TheCompanyWideEdition_ShowsTheTacticsItsCopyLists_AndNoScorecardTags_AsItsEmailAndWebCopyDo()
    {
        var s = await Brief(92);

        // What each edition's copies carry: the email, the web (SharePoint) copy and the brief page's edition preview.
        await using (var scope = SignedIn(app, SignIn.Analyst))
        {
            var brief = (await scope.ServiceProvider.GetRequiredService<BriefService>().GetAsync(s.Day))!;
            var renderer = app.Services.GetRequiredService<BriefRenderer>();
            var context = new BriefRenderContext("CTI", null, "https://tide.example.org", img => "cid:" + img.ContentId);
            foreach (var edition in new[] { BriefEditions.Build(brief, Edition.Public, TlpLevel.Green), BriefEditions.Build(brief, Edition.Team, TlpLevel.AmberStrict) })
            {
                var sent = renderer.Render(edition, context);
                await using var preview = await RenderAsync<EditionView>(app, "/", new() { ["Edition"] = edition });
                foreach (var copy in new[] { sent.EmailHtml, sent.WebHtml, await preview.Html() })
                {
                    copy.Should().Contain("T1566.001", $"the {edition.Edition} copy lists the ATT&CK techniques: so does its slide");
                    foreach (var name in s.TagNames) copy.Should().NotContain(name, $"no {edition.Edition} copy carries scorecard tags: only the team's slide shows them");
                }
            }
        }
        SlideTags.ShowsScorecard(Edition.Public).Should().BeFalse();
        SlideTags.ShowsScorecard(Edition.Team).Should().BeTrue();

        await using var r = await Open(s.Day);
        await Do(r, p => p.SetEdition(new ChangeEventArgs { Value = nameof(Edition.Public) }));
        await ToArticle(r, s.Tagged);
        var html = await r.Html();
        var row = Row(html);
        row.Should().Contain("aria-label=\"ATT&amp;CK tactics\"").And.Contain("Initial Access");
        row.Should().NotContain("Scorecard").And.NotContain("tide-slide-tag-cat");
        foreach (var name in s.TagNames) html.Should().NotContain(name, "the company-wide slide never widens its edition");
    }

    [Fact]
    public async Task AnArticleWithNoTags_HasNoEmptyRow()
    {
        var s = await Brief(93);
        await using var r = await Open(s.Day);
        await ToArticle(r, s.Plain);
        // An analyst's team edition slide keeps only the button to add some (PresentTagEditTests): no chips, no empty lists.
        var row = Row(await r.Html());
        row.Should().NotContain("tide-slide-tactic").And.NotContain("tide-slide-tag-group").And.NotContain("<ul").And.Contain("> Add tags</button>");
        await Do(r, p => p.SetEdition(new ChangeEventArgs { Value = nameof(Edition.Public) }));
        await ToArticle(r, s.Plain);
        (await r.Html()).Should().NotContain("tide-slide-tags");
    }

    [Fact]
    public void ManyTags_AreCappedToTheRow_TheRestCountedInAMoreChip()
    {
        var groups = new List<SlideTagGroup> { new("A", ["a1", "a2", "a3", "a4"]), new("B", ["b1", "b2", "b3", "b4"]) };
        var (shown, left) = SlideTags.Cap(groups);
        shown.SelectMany(g => g.Tags).Should().Equal("a1", "a2", "a3", "a4", "b1");
        left.Should().Equal("b2", "b3", "b4");
        SlideTags.Cap([new("A", ["a1"])]).Left.Should().BeEmpty();
    }
}
