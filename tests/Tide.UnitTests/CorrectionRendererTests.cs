using FluentAssertions;
using Tide.Application.Briefs;
using Tide.Application.Content;
using Tide.Application.Distribution;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.UnitTests;

/// <summary>A correction reads a frozen edition back by article, diffs it line by line, and renders as the brief's email does.</summary>
public class CorrectionRendererTests
{
    private static readonly Guid A = Guid.NewGuid(), B = Guid.NewGuid();

    private static string Frozen() =>
        "<h1>CTI daily brief: Monday, October 26, 2026</h1>\n<p><strong>TLP:CLEAR</strong> Anyone.</p>\n" +
        $"<h2 data-article=\"{A}\">1. First &amp; foremost</h2>\n<p><strong>Bottom line:</strong> one</p>\n<ul>\n<li>a bullet</li>\n</ul>\n" +
        "<figure><img src=\"cid:shot1@tide\" alt=\"x\"><figcaption>Figure 1: a caption</figcaption></figure>\n" +
        $"<h2 data-article=\"{B}\">2. Second</h2>\n<p><strong>Bottom line:</strong> two</p>\n" +
        "<p><em>1 restricted item was briefed to the security team and left out of this edition.</em></p>\n<p><strong>TLP:CLEAR</strong></p>\n";

    [Fact]
    public void SentSections_ReadsEachArticle_ByItsId_AndLeavesTheEditionsClosingLinesOut()
    {
        var sections = SentSections.Parse(Frozen());

        sections.Select(s => (s.Number, s.Headline, s.ArticleId)).Should().Equal((1, "First & foremost", A), (2, "Second", B));
        SentSections.TextLines(sections[0].Html).Should().Equal("First & foremost", "Bottom line: one", "• a bullet", "Figure: a caption");
        SentSections.TextLines(sections[1].Html).Should().Equal("Second", "Bottom line: two");
        SentSections.Parse(null).Should().BeEmpty();
    }

    [Fact]
    public void SentSections_AnOlderSendsHeadings_HaveNoArticle_AndAnUnnumberedOneIsNumberedInOrder()
    {
        var sections = SentSections.Parse("<h2>1. Old</h2>\n<p>x</p>\n<h2>Unnumbered</h2>\n<h2 data-article=\"not-a-guid\">3. Odd</h2>\n<h2x>no</h2x>");

        sections.Select(s => (s.Number, s.Headline, s.ArticleId)).Should().Equal((1, "Old", (Guid?)null), (2, "Unnumbered", null), (3, "Odd", null));
    }

    [Fact]
    public void Diff_MarksWhatWasRemovedAndAdded_InOrder()
    {
        var diff = SentSections.Diff(["title", "a", "b", "c"], ["title", "a", "B", "c", "d"]);

        diff.Select(d => (d.Kind, d.Text)).Should().Equal(
            (DiffKind.Same, "title"), (DiffKind.Same, "a"), (DiffKind.Removed, "b"), (DiffKind.Added, "B"), (DiffKind.Same, "c"), (DiffKind.Added, "d"));
        SentSections.Diff([], ["x"]).Should().ContainSingle(d => d.Kind == DiffKind.Added);
        SentSections.Diff(["x"], []).Should().ContainSingle(d => d.Kind == DiffKind.Removed);
    }

    [Fact]
    public void TheSemanticRendering_NamesEachArticle_SoItsSectionIsFoundAgain()
    {
        var article = new Article { Title = "Open article", Tlp = TlpLevel.Clear, Bluf = "Everyone may read this." };
        var brief = new Brief { BriefDate = new DateOnly(2026, 10, 26), AnalystName = "Analyst" };
        brief.Items.Add(new BriefItem { Article = article, ArticleId = article.Id });
        var html = new BriefRenderer(new MarkdownService()).Semantic(BriefEditions.Build(brief, Edition.Public, TlpLevel.Green),
            new BriefRenderContext("CTI", null, null, _ => ""));

        SentSections.Parse(html).Should().ContainSingle().Which.ArticleId.Should().Be(article.Id);
        PublishedContent.Parse(html).Headlines.Should().Equal("Open article");
    }

    private static CorrectionContent Content(bool slip = false, string? note = "Line one\nline <two>") => new("CTI", "https://tide.example.org", Edition.Public,
        new DateOnly(2026, 10, 26), TlpLevel.Green, slip, note,
        [new ArticleDiff(1, A, "First", CorrectedState.Changed, [new(DiffKind.Same, "First"), new(DiffKind.Removed, "• old"), new(DiffKind.Added, "• new")], 1),
         new ArticleDiff(2, B, null, CorrectedState.LeftTheEdition, [])]);

    [Fact]
    public void TheCorrectionEmail_IsTablesAndInlineStyles_WithTheNoteAndOnlyTheChangedLines()
    {
        var r = CorrectionRenderer.Render(Content());

        r.EmailHtml.Should().Contain("<table role=\"presentation\"").And.NotContain("<style").And.NotContain("var(--")
            .And.Contain("Line one<br>line &lt;two&gt;").And.Contain(">Was</td>").And.Contain("• old").And.Contain(">Now</td>").And.Contain("• new")
            .And.Contain("Article 1: First").And.Contain("Article 2</h2>").And.Contain("no longer in this edition")
            .And.Contain("1 line that went is restricted now").And.Contain("https://tide.example.org/published/2026-10-26")
            .And.Contain(EmailComposer.Palette.OkSoft).And.Contain(EmailComposer.Palette.ThreatSoft);
        r.EmailHtml.Should().NotContain(">First</td>", "an unchanged line isn't repeated");
        r.WebHtml.Should().Contain("<del>Was: • old</del>").And.Contain("<ins>Now: • new</ins>").And.NotContain("style=").And.NotContain("<h2");
        r.Text.Should().Contain("Was • old").And.Contain("Now • new");
    }

    [Fact]
    public void ASlip_HasTheFixedWords_NamesArticlesByNumberOnlyUnlessAHeadlineIsGiven_AndNoLink()
    {
        var r = CorrectionRenderer.Render(Content(slip: true, note: null));

        r.EmailHtml.Should().Contain("was sent above its audience&#39;s marking").And.Contain("article 1 (“First”) and article 2.")
            .And.Contain(CorrectionRenderer.NotRecalled).And.NotContain("• old").And.NotContain("• new").And.NotContain("Open in TIDE")
            .And.NotContain("From the sender");
        r.WebHtml.Should().Contain(CorrectionRenderer.NotRecalled).And.NotContain("• old");
        CorrectionRenderer.SlipText(Content(slip: true) with { Articles = [] }).Should().NotContain("It was in");
    }

    [Fact]
    public void TheSubject_IsTheOriginalsWithCorrectionInFront_ItsMarkingTheCorrections()
    {
        CorrectionRenderer.Subject("CTI daily brief, Mon Oct 26 [TLP:CLEAR]", TlpLevel.Clear, TlpLevel.Clear)
            .Should().Be("Correction: CTI daily brief, Mon Oct 26 [TLP:CLEAR]");
        CorrectionRenderer.Subject("CTI daily brief, Mon Oct 26 [TLP:CLEAR]", TlpLevel.Clear, TlpLevel.Green)
            .Should().Be("Correction: CTI daily brief, Mon Oct 26 [TLP:GREEN]");
        new ArticleDiff(3, A, null, CorrectedState.Unchanged, []).Label.Should().Be("Article 3");
    }

    [Fact]
    public void PublishedContent_WithCorrections_MatchesTheirWordsToo()
    {
        var day = PublishedContent.Parse("<h2>1. Day</h2><p>alpha</p>").With([PublishedContent.Parse("<p>beta</p>")]);

        day.Headlines.Should().Equal("Day");
        day.Matches("alpha beta").Should().BeTrue();
        new PublishedContentCache().Invoking(c => c.Remove(Guid.NewGuid())).Should().NotThrow();
    }
}
