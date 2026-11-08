using AwesomeAssertions;
using Tide.Application.Articles;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.UnitTests;

/// <summary>
/// "Why it matters" on a threat starts at AMBER (or the article's TLP when higher): on a new threat, and when an article
/// first turns from informational to threat with it still empty. Informational articles keep CLEAR, and a saved value is
/// never moved behind the writer's back.
/// </summary>
public class WhyItMattersDefaultTests
{
    [Theory]
    [InlineData(TlpLevel.Clear, TlpLevel.Amber)]
    [InlineData(TlpLevel.Green, TlpLevel.Amber)]
    [InlineData(TlpLevel.Amber, TlpLevel.Amber)]
    [InlineData(TlpLevel.AmberStrict, TlpLevel.AmberStrict)]
    [InlineData(TlpLevel.Red, TlpLevel.Red)]
    public void AThreat_StartsAtAmber_OrTheArticlesTlpWhenHigher(TlpLevel article, TlpLevel expected) =>
        Article.DefaultWhyItMattersTlp(Classification.Threat, article).Should().Be(expected);

    [Fact]
    public void AnInformationalArticle_KeepsClear() =>
        Article.DefaultWhyItMattersTlp(Classification.Informational, TlpLevel.Green).Should().Be(TlpLevel.Clear);

    [Fact]
    public void ANewArticle_ClassifiedAsAThreat_StartsWhyAtAmber()
    {
        var a = new Article { Tlp = TlpLevel.Green };

        a.Classify(Classification.Threat).Should().BeTrue();

        a.Classification.Should().Be(Classification.Threat);
        a.WhyItMattersTlp.Should().Be(TlpLevel.Amber);
    }

    [Fact]
    public void ANewArticle_KeptInformational_KeepsClear()
    {
        var a = new Article { Tlp = TlpLevel.Green };

        a.Classify(Classification.Informational).Should().BeFalse();

        a.WhyItMattersTlp.Should().Be(TlpLevel.Clear);
    }

    [Fact]
    public void AWrittenWhy_IsNeverMoved_WhenTheArticleTurnsToAThreat()
    {
        var a = new Article { WhyItMatters = "Phishing like this targets payroll.", WhyItMattersTlp = TlpLevel.Clear };

        a.Classify(Classification.Threat).Should().BeFalse();

        a.WhyItMattersTlp.Should().Be(TlpLevel.Clear, "the writer chose that marking for text they wrote");
    }

    [Fact]
    public void AThreatStayingAThreat_OrTurningInformational_LeavesTheWhyTlpAlone()
    {
        Article.WhyItMattersTlpOnClassify(Classification.Threat, Classification.Threat, null, TlpLevel.Clear, TlpLevel.Clear)
            .Should().BeNull("a threat whose writer lowered it to CLEAR keeps CLEAR on every save");
        Article.WhyItMattersTlpOnClassify(Classification.Threat, Classification.Informational, null, TlpLevel.Amber, TlpLevel.Clear)
            .Should().BeNull("turning informational never lowers it");
    }

    [Fact]
    public void ARaisedWhy_IsNeverLowered_ByTheDefault() =>
        Article.WhyItMattersTlpOnClassify(Classification.Informational, Classification.Threat, " ", TlpLevel.Red, TlpLevel.Clear)
            .Should().BeNull();

    [Fact]
    public void TheEditor_TurningToAThreat_WithWhyEmpty_StartsItAtAmber_AndSaysSo()
    {
        var form = new ArticleForm { Tlp = TlpLevel.Green, WhyItMattersTlp = TlpLevel.Green };

        form.SetClassification(Classification.Threat).Should().BeTrue("the editor shows the hint when this is true");

        form.WhyItMattersTlp.Should().Be(TlpLevel.Amber);
        form.WhyItMattersAtThreatDefault.Should().BeTrue();
    }

    [Fact]
    public void TheEditor_AWrittenWhy_StaysWhereItIs_AndTurningInformationalDropsThePriority()
    {
        var form = new ArticleForm { WhyItMatters = "We run it.", WhyItMattersTlp = TlpLevel.Clear };

        form.SetClassification(Classification.Threat).Should().BeFalse();
        form.WhyItMattersTlp.Should().Be(TlpLevel.Clear);
        form.WhyItMattersAtThreatDefault.Should().BeFalse();

        form.Priority = Priority.P2;
        form.SetClassification(Classification.Informational).Should().BeFalse();
        form.Priority.Should().BeNull();
        form.WhyItMattersTlp.Should().Be(TlpLevel.Clear);
    }

    [Fact]
    public void TheHint_IsntShown_WhenTheDefaultRaisesNothing()
    {
        var form = ArticleForm.From(new Article { Tlp = TlpLevel.Amber, Classification = Classification.Threat, WhyItMattersTlp = TlpLevel.Amber });

        form.WhyItMattersAtThreatDefault.Should().BeFalse("at the article's own TLP there's nothing to point out");
    }
}
