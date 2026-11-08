using AwesomeAssertions;
using Tide.Application.Actions;
using Tide.Application.Articles;
using Tide.Application.Briefs;
using Tide.Domain.Enums;

namespace Tide.UnitTests;

/// <summary>The editor's reader preview: an unsaved form cut for an edition, with what it leaves out and what's missing.</summary>
public class ArticlePreviewTests
{
    private static ArticleForm Form(TlpLevel tlp = TlpLevel.Clear) => new()
    {
        Id = Guid.NewGuid(), Title = "Draft", Tlp = tlp, Bluf = "Bottom line.", SourceName = "Vendor",
        Bullets = [new() { Text = "open", Tlp = tlp }, new() { Text = "partner only", Tlp = TlpLevel.Amber }],
        TagIds = [Guid.NewGuid()],
    };

    [Fact]
    public void AnUnsavedForm_IsCutAsTheEditionWouldCutIt()
    {
        var form = Form();
        form.WhyItMatters = "We run it.";
        form.WhyItMattersTlp = TlpLevel.Amber;

        var team = ArticlePreview.Build(form, Edition.Team, TlpLevel.AmberStrict);
        var company = ArticlePreview.Build(form, Edition.Public, TlpLevel.Green);

        team.Verdict.Should().Be(EditionVerdict.Included);
        team.Cut!.Bullets.Select(b => b.Text).Should().Equal("open", "partner only");
        company.Verdict.Should().Be(EditionVerdict.Partly);
        company.Cut!.Bullets.Select(b => b.Text).Should().Equal("open");
        company.Cut.ShowWhy.Should().BeFalse();
        company.Cut.WithheldParts.Should().Be(2, "the AMBER bullet and why it matters");
        company.Checks.Should().BeEmpty();
    }

    [Fact]
    public void AnArticleAboveTheCeiling_IsLeftOutWhole()
    {
        var p = ArticlePreview.Build(Form(TlpLevel.Amber), Edition.Public, TlpLevel.Green);

        p.LeftOut.Should().BeTrue();
        p.Verdict.Should().Be(EditionVerdict.LeftOut);
        p.DropsTeamOnlyParts(3).Should().BeFalse("there's nothing of it to say that about");
    }

    [Fact]
    public void TheChecks_RunOnTheUnsavedForm()
    {
        var form = Form();
        form.Bluf = "  ";
        form.Classification = Classification.Threat;

        var p = ArticlePreview.Build(form, Edition.Team, TlpLevel.AmberStrict);

        p.Checks.Select(c => c.Part).Should().Equal(ArticlePart.BottomLine, ArticlePart.Assessment, ArticlePart.Recommended);
        p.Checks.Where(c => c.Blocking).Select(c => c.Part).Should().Equal(ArticlePart.BottomLine, ArticlePart.Assessment);
    }

    [Fact]
    public void OnlyTheCompanyWideEdition_DropsIndicatorsAndActions()
    {
        var form = Form();
        var actions = new[] { new EditionAction(7, "Hunt", "SOC", null, ActionStatus.Open) };

        ArticlePreview.Build(form, Edition.Team, TlpLevel.AmberStrict, actions).DropsTeamOnlyParts(1).Should().BeFalse();
        var company = ArticlePreview.Build(form, Edition.Public, TlpLevel.Green, actions);
        company.Cut!.Actions.Should().BeEmpty();
        company.DropsTeamOnlyParts(0).Should().BeFalse("nothing to drop");
        company.DropsTeamOnlyParts(1).Should().BeTrue();

        form.Indicators.Add(new IndicatorForm { Type = IndicatorType.Domain, Value = "evil.example" });
        ArticlePreview.Build(form, Edition.Public, TlpLevel.Green).DropsTeamOnlyParts(0).Should().BeTrue();
    }

    [Fact]
    public void TeamActions_LeaveOutCancelledOnes_InNumberOrder()
    {
        static ActionRow Row(int n, ActionStatus s) => new(Guid.NewGuid(), n, $"A{n}", ActionKind.Other, Priority.P3, Guid.NewGuid(), "SOC",
            null, null, new DateOnly(2026, 11, 1), s, ActionOutcome.None, null, null, null, null, 0, DateTimeOffset.UnixEpoch);

        var got = ArticlePreview.TeamActions([Row(9, ActionStatus.Open), Row(3, ActionStatus.Cancelled), Row(4, ActionStatus.Done)]);

        got.Select(a => a.Number).Should().Equal(4, 9);
        got[1].Should().Be(new EditionAction(9, "A9", "SOC", new DateOnly(2026, 11, 1), ActionStatus.Open));
    }

    [Theory]
    [InlineData("company", Edition.Public)]
    [InlineData("COMPANY", Edition.Public)]
    [InlineData("team", Edition.Team)]
    [InlineData(null, Edition.Team)]
    [InlineData("nonsense", Edition.Team)]
    public void TheEdition_InALink(string? key, Edition expected)
    {
        ArticlePreview.ParseEdition(key).Should().Be(expected);
        ArticlePreview.ParseEdition(ArticlePreview.EditionKey(expected)).Should().Be(expected);
    }
}
