using System.Text.RegularExpressions;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Articles;
using Tide.Application.Briefs;
using Tide.Application.Distribution;
using Tide.Application.Mitre;
using Tide.Application.Org;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Infrastructure.Persistence;
using Tide.Web.Components.Pages;
using Tide.Web.Components.Shared;
using static Tide.IntegrationTests.ComponentHarness;
using static Tide.IntegrationTests.PresentHarness;

namespace Tide.IntegrationTests;

/// <summary>
/// The "Tags and ATT&amp;CK" section of an article's slide: its scorecard tags by category and its ATT&amp;CK techniques by tactic in kill-chain order, each
/// edition showing only what that edition's email and web copy show.
/// </summary>
public class PresentSlideTagsTests(TideApp app) : IClassFixture<TideApp>
{
    // Read once: a test that straddles midnight mustn't see two days.
    private DateOnly Today { get; } = app.Services.GetRequiredService<OrgClock>().CurrentBriefDate;

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

    private static async Task ToArticle(Rendered<Present> r, string title)
    {
        await r.Click(p => p.OnPresentKey("last"));
        for (var i = 0; i < 20 && !(await r.Html()).Contains($"<h2>{title}</h2>", StringComparison.Ordinal); i++)
            await r.Click(p => p.OnPresentKey("prev"));
        (await r.Html()).Should().Contain($"<h2>{title}</h2>");
    }

    /// <summary>The slide's "Tags and ATT&amp;CK" section, or null when it has none.</summary>
    internal static string? Section(string html)
    {
        var at = html.IndexOf("<section class=\"tide-slide-tagsec\"", StringComparison.Ordinal);
        return at < 0 ? null : html[at..(html.IndexOf("</section>", at, StringComparison.Ordinal) + 10)];
    }

    [Fact]
    public async Task TheTeamEdition_ShowsScorecardTagsByCategory_ThenATTCKByTactic_AtTheBottom_AndNothingUnderTheTitle()
    {
        var s = await Brief(91);
        await using var r = await Open(app, s.Day);
        await ToArticle(r, s.Tagged);
        var html = await r.Html();
        var section = Section(html)!;
        section.Should().NotBeNull();
        html.IndexOf("tide-slide-tagsec", StringComparison.Ordinal).Should().BeGreaterThan(html.IndexOf("tide-slide-bluf", StringComparison.Ordinal), "below the bottom line, not under the title");
        html.Should().NotContain("tide-slide-tags\"").And.NotContain("ATT&amp;CK and CVEs");
        section.Should().Contain("<span>Tags and ATT&amp;CK</span>");

        // The scorecard tags first: a chip each, by category in the scorecard's order, the category its tooltip and said
        // to a screen reader.
        var line = section[section.IndexOf("tide-slide-scorecard", StringComparison.Ordinal)..section.IndexOf("</ul>", StringComparison.Ordinal)];
        line.Should().Contain($"<li class=\"tide-chip tide-slide-tagchip\" title=\"Threat types 91\"><span class=\"visually-hidden\">Threat types 91: </span>{s.TagNames[0]}</li>")
            .And.Contain($"<li class=\"tide-chip tide-slide-tagchip\" title=\"Third-party risk 91\"><span class=\"visually-hidden\">Third-party risk 91: </span>{s.TagNames[2]}</li>");
        line.IndexOf(s.TagNames[1], StringComparison.Ordinal).Should().BeLessThan(line.IndexOf(s.TagNames[2], StringComparison.Ordinal));
        section.IndexOf("tide-slide-scorecard", StringComparison.Ordinal).Should().BeLessThan(section.IndexOf("tide-slide-attack", StringComparison.Ordinal));

        // Then a column per tactic in the matrix's order, its colour a bar, its techniques one to a line: each technique once.
        Regex.Matches(section, "class=\"tide-slide-tag-name\">([^<]+)<").Select(m => m.Groups[1].Value)
            .Should().Equal(["Initial Access", "Execution", "Impact"], "tactics in the matrix's order");
        section.Should().Contain($"style=\"--tac:{Ui.TacticColor(MitreTactic.InitialAccess)}\"").And.Contain(Ui.TacticGlyph(MitreTactic.Impact));
        section.Should().Contain("<span class=\"tide-slide-tech\"><span class=\"tide-slide-tech-id\">T1190</span> Exploit Public-Facing Application</span><span class=\"tide-slide-tech\"><span class=\"tide-slide-tech-id\">T1566.001</span> Spearphishing Attachment</span>");
        Regex.Count(html, "T1566\\.001").Should().Be(1, "a technique appears once on the slide");
        section.Should().NotContain("tide-chip-technique", "the old flat chip list is gone");
    }

    [Fact]
    public async Task TheCompanyWideEdition_ShowsTheATTCKItsCopyLists_AndNoScorecardTags_AsItsEmailAndWebCopyDo()
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

        await using var r = await Open(app, s.Day);
        await Do(r, p => p.SetEdition(new ChangeEventArgs { Value = nameof(Edition.Public) }));
        await ToArticle(r, s.Tagged);
        var html = await r.Html();
        var section = Section(html)!;
        section.Should().Contain("<span>ATT&amp;CK</span>", "no scorecard tags: the heading is ATT&CK alone")
            .And.Contain("aria-label=\"ATT&amp;CK by tactic\"").And.Contain("T1566.001</span> Spearphishing Attachment");
        section.Should().NotContain("tide-slide-scorecard").And.NotContain("tide-slide-tags-edit", "no editing on the company-wide edition");
        foreach (var name in s.TagNames) html.Should().NotContain(name, "the company-wide slide never widens its edition");
    }

    [Fact]
    public async Task AnArticleWithNoTags_HasNoEmptySection()
    {
        var s = await Brief(93);
        await using var r = await Open(app, s.Day);
        await ToArticle(r, s.Plain);
        // An analyst's team edition slide keeps only the heading with the button to add some (PresentTagEditTests).
        var section = Section(await r.Html())!;
        section.Should().NotContain("tide-slide-tactic").And.NotContain("tide-slide-scorecard").And.NotContain("<ul").And.Contain("> Add tags</button>");
        await Do(r, p => p.SetEdition(new ChangeEventArgs { Value = nameof(Edition.Public) }));
        await ToArticle(r, s.Plain);
        Section(await r.Html()).Should().BeNull("nothing to show, nothing to edit");
    }

    [Fact]
    public void ATacticsRow_ListsSixTechniques_AndNamesTheRest()
    {
        var many = AttackCatalog.All.Where(t => t.PrimaryTactic == MitreTactic.InitialAccess).Take(8).ToList();
        var row = new SlideTactic(MitreTactic.InitialAccess, many);
        row.Shown.Should().Equal(many.Take(SlideTags.MaxTechniques));
        row.Left.Should().Equal(many.Skip(SlideTags.MaxTechniques));
        new SlideTactic(MitreTactic.Impact, many.Take(1).ToList()).Left.Should().BeEmpty();
    }
}
