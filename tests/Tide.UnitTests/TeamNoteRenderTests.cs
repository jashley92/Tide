using FluentAssertions;
using Tide.Application.Briefs;
using Tide.Application.Content;
using Tide.Application.Distribution;
using Tide.Application.Metrics;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.UnitTests;

/// <summary>The team's opening note: a section near the top of the team edition's email, before the numbers, with its
/// bullets nested; never anywhere in the company-wide edition.</summary>
public class TeamNoteRenderTests
{
    private readonly BriefRenderer _renderer = new(new MarkdownService());

    private const string Note = "Phishing trends this week\n\n- Invoice lures are up\n  - Most spoof a payroll vendor\n  - Two reached inboxes\n- Patch window on Thursday";

    private static Brief Brief(string? note = Note)
    {
        var article = new Article { Title = "Open article", Tlp = TlpLevel.Clear, Bluf = "Everyone may read this." };
        var brief = new Brief { BriefDate = new DateOnly(2026, 10, 26), AnalystName = "Analyst", TeamNote = note };
        brief.Items.Add(new BriefItem { Article = article, ArticleId = article.Id, SortOrder = 0 });
        return brief;
    }

    private static MetricDay Metrics()
    {
        var tile = new MetricTile(new MetricDefinition { Name = "Older than 24 hours" }, 3m, null, null, MetricLevel.Normal, [3m]);
        return new MetricDay(new DateOnly(2026, 10, 26), [new MetricGroupDay(new MetricGroup { Name = "Alert queue" }, [tile])]);
    }

    private static BriefRenderContext Context() => new("CTI", Metrics(), null, img => "cid:" + img.ContentId);

    [Fact]
    public void TeamEmail_HasTheNote_BeforeTheNumbers_WithItsBulletsNested()
    {
        var r = _renderer.Render(BriefEditions.Build(Brief(), Edition.Team, TlpLevel.AmberStrict), Context());

        var html = r.EmailHtml;
        html.Should().Contain(BriefRenderer.TeamNoteHeading).And.Contain("Phishing trends this week");
        html.IndexOf(BriefRenderer.TeamNoteHeading, StringComparison.Ordinal).Should()
            .BeLessThan(html.IndexOf("The day's numbers", StringComparison.Ordinal), "the note comes before the numbers");
        html.IndexOf("The day's numbers", StringComparison.Ordinal).Should().BeLessThan(html.IndexOf("Open article", StringComparison.Ordinal));

        // The sub-bullets are a list inside their bullet's item, which closes after them; each list styled inline for Outlook.
        var outer = html.IndexOf("Invoice lures are up", StringComparison.Ordinal);
        html[outer..html.IndexOf("Patch window", StringComparison.Ordinal)].Should().MatchRegex(
            @"^Invoice lures are up\s*<ul style=""[^""]+"">\s*<li style=""[^""]+"">Most spoof a payroll vendor</li>\s*<li style=""[^""]+"">Two reached inboxes</li>\s*</ul>\s*</li>\s*<li style=");
        html.Should().NotContain("<ul>").And.NotContain("<li>", "every list and item is styled");

        r.Text.Should().Contain("- Invoice lures are up").And.Contain("- Most spoof a payroll vendor");
        r.WebHtml.Should().Contain($"<h3>{BriefRenderer.TeamNoteHeading}</h3>").And.Contain("<li>Invoice lures are up\n<ul>\n<li>Most spoof a payroll vendor</li>");
        SentSections.Parse(r.WebHtml).Should().ContainSingle("the note's heading doesn't start an article's section").Which.Headline.Should().Be("Open article");
    }

    [Fact]
    public void ThePublicEdition_NeverCarriesTheNote()
    {
        var edition = BriefEditions.Build(Brief(), Edition.Public, TlpLevel.Red);
        edition.ShowTeamNote.Should().BeFalse("whatever the ceiling");
        edition.TeamNote.Should().BeNull();

        var r = _renderer.Render(edition, Context());
        foreach (var text in new[] { BriefRenderer.TeamNoteHeading, "Phishing trends this week", "Invoice lures", "payroll vendor" })
        {
            r.EmailHtml.Should().NotContain(text);
            r.Text.Should().NotContain(text);
            r.WebHtml.Should().NotContain(text);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \n ")]
    public void ABlankNote_HasNoSection(string? note)
    {
        var edition = BriefEditions.Build(Brief(note), Edition.Team, TlpLevel.AmberStrict);
        edition.ShowTeamNote.Should().BeFalse();

        var r = _renderer.Render(edition, Context());
        r.EmailHtml.Should().NotContain(BriefRenderer.TeamNoteHeading);
        r.WebHtml.Should().NotContain(BriefRenderer.TeamNoteHeading);
    }
}
