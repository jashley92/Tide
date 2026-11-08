using AwesomeAssertions;
using Tide.Application.Assist;
using Tide.Application.Intake;
using Tide.Application.Settings;
using Tide.Domain.Entities;

namespace Tide.UnitTests;

public class ArticleCleanupTests
{
    private static readonly string Para =
        "Attackers chained two flaws in the VPN gateway, gaining code execution without signing in, and the vendor has shipped fixes for every supported branch. ";

    private static string Body(int paragraphs, int from = 0) =>
        string.Concat(Enumerable.Range(from, paragraphs).Select(i => $"<p>{Para}Paragraph {i}.</p>"));

    private const string AdCopy = "Buy the best firewall today, half price this week only";

    [Theory]
    [InlineData("<div class=\"article-inline-ad\"><p>{0}</p></div>")]
    [InlineData("<div class=\"content-ad\"><p>{0}</p></div>")]
    [InlineData("<div class=\"ad-slot\"><p>{0}</p></div>")]
    [InlineData("<div class=\"story_ad_1\"><p>{0}</p></div>")]
    [InlineData("<div id=\"adunit-mid\"><p>{0}</p></div>")]
    [InlineData("<div class=\"wrap taboola-feed\"><p>{0}</p></div>")]
    [InlineData("<div class=\"OUTBRAIN\"><p>{0}</p></div>")]
    [InlineData("<ins class=\"adsbygoogle\">{0}</ins>")]
    [InlineData("<div data-ad-slot=\"123\"><p>{0}</p></div>")]
    [InlineData("<div id=\"div-gpt-ad-1234-0\"><p>{0}</p></div>")]
    [InlineData("<amp-ad><p>{0}</p></amp-ad>")]
    public void AnInlineAdvert_InsideTheArticle_IsTakenOut(string advert)
    {
        var html = $"<html><body><article>{Body(3)}{advert.Replace("{0}", AdCopy, StringComparison.Ordinal)}{Body(3, 3)}</article></body></html>";

        var found = ArticleText.Extract(html);

        found.Usable.Should().BeTrue();
        found.Text.Should().Contain("Paragraph 0.").And.Contain("Paragraph 5.").And.NotContain(AdCopy);
    }

    [Fact]
    public void AnAdvert_IsTakenOut_EvenInsideTheSourcesOwnSelector()
    {
        var html = $"<html><body><div class=\"post-body\">{Body(3)}<div class=\"post-ad\"><p>{AdCopy}</p></div>{Body(3, 3)}</div></body></html>";

        var found = ArticleText.Extract(html, "div.post-body");

        found.How.Should().Be("the source's selector");
        found.Text.Should().Contain("Paragraph 5.").And.NotContain(AdCopy);
    }

    [Theory]
    [InlineData("post has-ads")]
    [InlineData("article ad-free")]
    [InlineData("header")]
    [InlineData("download-links")]
    [InlineData("shadow-box")]
    public void AClassThatOnlyMentionsAdverts_KeepsTheArticle(string cls)
    {
        var html = $"<html><body><article class=\"{cls}\">{Body(4)}</article></body></html>";

        ArticleText.Extract(html).Text.Should().Contain("Paragraph 3.");
    }

    [Fact]
    public void AWrapperNamedLikeAnAdvert_ButHoldingTheArticle_IsKept()
    {
        var html = $"<html><body><div class=\"story-ad-wrapper\">{Body(10)}</div></body></html>";

        ArticleText.Extract(html).Text.Should().Contain("Paragraph 9.", "a block with more than {0} characters of its own text isn't removed by name", ArticleText.MaxAdText);
    }

    [Fact]
    public void LabelLines_AreDropped_ButASentenceAboutAdvertisingIsKept()
    {
        var html = $"""
            <html><body><article>
              {Body(2)}
              <p>ADVERTISEMENT</p>
              <div>— Advertisement —</div>
              <p>Story continues below advertisement</p>
              {Body(2, 2)}
              <p>Advertisement networks were abused to deliver the loader, researchers said.</p>
              <ul><li>Sponsored</li><li>{Para}</li></ul>
            </article></body></html>
            """;

        var text = ArticleText.Extract(html).Text;

        text.Should().NotContain("ADVERTISEMENT").And.NotContain("— Advertisement").And.NotContain("Story continues below");
        text.Should().Contain("Advertisement networks were abused").And.NotContain("- Sponsored").And.Contain("Paragraph 3.");
    }

    [Fact]
    public void LabelLines_AreDropped_FromTheTextThePagePublishes()
    {
        var body = $"{Para}\nAdvertisement\n{Para}{Para}{Para}";
        var ld = $$"""<script type="application/ld+json">{"@type":"NewsArticle","articleBody":{{System.Text.Json.JsonSerializer.Serialize(body)}}}</script>""";

        var found = ArticleText.Extract($"<html><head>{ld}</head><body></body></html>");

        found.How.Should().Be("the article text the page publishes");
        found.Text.Should().NotContain("Advertisement");
    }

    [Fact]
    public void TheAdministratorsLists_ReplaceTheDefaults_AndTheSourcesSelectorIsAdded()
    {
        var html = $"""
            <html><body><article>
              {Body(3)}
              <div class="box-77"><p>{AdCopy}</p></div>
              <div class="x-widget"><p>Get our weekly roundup in your inbox, every Friday morning.</p></div>
              <p>Read more from our partners</p>
              <div data-ad-slot="1"><p>Slot text that stays when the default selectors are replaced</p></div>
              {Body(3, 3)}
            </article></body></html>
            """;
        var options = new IntakeOptions { AdSelectors = ".box-77", AdLabels = "Read more from*" };

        var text = ArticleText.Extract(html, cleanup: ArticleCleanup.For(options, new Source { ArticleRemoveSelector = ".x-widget" })).Text;

        text.Should().NotContain(AdCopy).And.NotContain("weekly roundup").And.NotContain("Read more from our partners");
        text.Should().Contain("Slot text that stays", "the saved list replaces the default selectors");
    }

    [Fact]
    public void EmptyLists_LeaveOnlyTheBuiltInNameRule()
    {
        var html = $"<html><body><article>{Body(3)}<p>Advertisement</p><div class=\"inline-ad\"><p>{AdCopy}</p></div>{Body(3, 3)}</article></body></html>";

        var text = ArticleText.Extract(html, cleanup: ArticleCleanup.For(new IntakeOptions { AdSelectors = "", AdLabels = "" }, (Source?)null)).Text;

        text.Should().Contain("Advertisement").And.NotContain(AdCopy);
    }

    [Theory]
    [InlineData("Advertisement", true)]
    [InlineData("  - advertisement:  ", true)]
    [InlineData("Story continues below advertisement", true)]
    [InlineData("Ad", true)]
    [InlineData("Advertisement networks were abused to deliver the loader.", false)]
    [InlineData("Sponsored by APT29, the campaign targeted embassies", false)]
    [InlineData("", false)]
    public void IsLabel_MatchesWholeLines_AndStarredLabelsMatchShortLinesStartingWithThem(string line, bool label) =>
        ArticleCleanup.Default.IsLabel(line).Should().Be(label);

    [Fact]
    public void AStarredLabel_DoesntMatchALongLine()
    {
        var line = "Story continues below " + new string('x', ArticleCleanup.MaxPrefixLine);

        ArticleCleanup.Default.IsLabel(line).Should().BeFalse();
    }

    [Fact]
    public void TheSettings_AreCheckedAsTheyreSaved()
    {
        OperationalSettings.Normalize(OperationalSettings.AdSelectors, ".a\n[data-x]\n.a").Should().Be(".a; [data-x]");
        OperationalSettings.Normalize(OperationalSettings.AdSelectors, "").Should().BeEmpty();
        OperationalSettings.Problem(OperationalSettings.AdSelectors, "div[").Should().Contain("isn't a CSS selector");
        OperationalSettings.Problem(OperationalSettings.AdSelectors, string.Join('\n', Enumerable.Range(0, 51).Select(i => ".a" + i))).Should().Contain("at most 50");
        OperationalSettings.Normalize(OperationalSettings.AdLabels, "Advertisement\nStory continues*").Should().Be("Advertisement; Story continues*");
        OperationalSettings.Problem(OperationalSettings.AdLabels, "—").Should().Contain("letter or digit");
        OperationalSettings.Problem(OperationalSettings.AdLabels, new string('a', 81)).Should().Contain("longer than 80");
    }

    [Fact]
    public void TheDefaults_AreValidSettings()
    {
        OperationalSettings.Problem(OperationalSettings.AdSelectors, ArticleCleanup.DefaultSelectors).Should().BeNull();
        OperationalSettings.Problem(OperationalSettings.AdLabels, ArticleCleanup.DefaultLabels).Should().BeNull();
    }
}
