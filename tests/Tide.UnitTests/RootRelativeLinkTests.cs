using FluentAssertions;
using Tide.Application.Briefs;
using Tide.Application.Content;
using Tide.Application.Distribution;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.UnitTests;

/// <summary>A Markdown link written from the server's root ("[x](/actions/12)") reaches TIDE when it runs under a
/// sub-path: on a page the sub-path goes in front, in an email the app's address does.</summary>
public class RootRelativeLinkTests
{
    private readonly MarkdownService _md = new();
    private static readonly LinkRoot UnderTide = LinkRoot.FromBaseUri("https://intranet.example.org/tide/");

    [Fact]
    public void OnAPageUnderASubPath_ALinkFromTheRoot_IsGivenTheSubPath_AndStillOpensSafely()
    {
        var html = _md.ToHtml("See [the action](/actions/12).", UnderTide);

        html.Should().Contain("<a href=\"/tide/actions/12\" target=\"_blank\" rel=\"noopener noreferrer\">the action</a>");
    }

    [Fact]
    public void OnAPageUnderASubPath_ALinkThatAlreadyHasIt_IsLeftAlone()
    {
        _md.ToHtml("[a](/tide/actions/12) [b](/TIDE?x=1) [c](/tidebits/1)", UnderTide).Should()
            .Contain("href=\"/tide/actions/12\"").And.Contain("href=\"/TIDE?x=1\"")
            .And.Contain("href=\"/tide/tidebits/1\"")   // only the whole segment counts as the sub-path
            .And.NotContain("/tide/tide/");
    }

    [Fact]
    public void OnAPageUnderASubPath_InlineFieldsAndReferenceLinksAreGivenItToo()
    {
        _md.ToInlineHtml("A **bullet** with [a link](/briefs/2026-11-06)", UnderTide).Should().Contain("href=\"/tide/briefs/2026-11-06\"");
        _md.ToHtml("See [the action][1].\n\n[1]: /actions/12", UnderTide).Should().Contain("href=\"/tide/actions/12\"");
    }

    [Fact]
    public void AProtocolRelativeLink_IsNotTreatedAsFromTheRoot_AndIsStillRefused()
    {
        var html = _md.ToHtml("[x](//evil.example/a) [y](javascript:alert(1)) [z](/\\evil.example)", UnderTide);

        html.Should().NotContain("evil.example\"").And.NotContain("/tide//").And.NotContain("javascript:")
            .And.Contain("<a href=\"#\" target=\"_blank\" rel=\"noopener noreferrer\">x</a>");
    }

    [Theory]
    [InlineData("https://intranet.example.org/")]
    [InlineData("https://intranet.example.org")]
    public void AtTheServersRoot_NothingChanges(string baseUri)
    {
        _md.ToHtml("[a](/actions/12)", LinkRoot.FromBaseUri(baseUri)).Should()
            .Be(_md.ToHtml("[a](/actions/12)")).And.Contain("href=\"/actions/12\"");
    }

    [Fact]
    public void AnImageFromTheRoot_IsTreatedTheSameWay()
    {
        _md.ToHtml("![](/files/1/inline)", UnderTide).Should().Contain("/tide/files/1/inline").And.Contain("tide-image-ref");
        _md.ToHtml("![](/files/1/inline)", LinkRoot.Email("https://intranet.example.org/tide"))
            .Should().Contain("https://intranet.example.org/tide/files/1/inline");
    }

    [Fact]
    public void InAnEmail_ALinkFromTheRoot_IsMadeAbsoluteWithTheAppsAddress()
    {
        var html = _md.ToHtml("[a](/actions/12) and [b](/tide/actions/13) and [c](https://vendor.example/advisory)",
            LinkRoot.Email("https://intranet.example.org/tide/"));

        html.Should().Contain("href=\"https://intranet.example.org/tide/actions/12\"")
            .And.Contain("href=\"https://intranet.example.org/tide/actions/13\"")
            .And.Contain("href=\"https://vendor.example/advisory\"")
            .And.NotContain("/tide/tide/");
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("not an address")]
    public void InAnEmail_WithNoAppAddress_ALinkFromTheRoot_KeepsItsTextWithoutTheLink(string? baseUrl)
    {
        var html = _md.ToHtml("See [the action](/actions/12) or [the vendor](https://vendor.example/a).", LinkRoot.Email(baseUrl));

        html.Should().Contain("See the action or").And.NotContain("/actions/12")
            .And.Contain("<a href=\"https://vendor.example/a\"");
    }

    [Fact]
    public void ABriefsEmailAndSharePointCopy_PointRootLinksAtTheAppsAddress_OrLeaveThemAsText()
    {
        var article = new Article
        {
            Title = "Article", Tlp = TlpLevel.Clear, Bluf = "Bottom line.", Classification = Classification.Threat,
            WhyItMatters = "Tracked in [the action](/actions/12).",
        };
        article.Bullets.Add(new ArticleBullet { Text = "See [the earlier brief](/briefs/2026-11-05)", Tlp = TlpLevel.Clear });
        var brief = new Brief { BriefDate = new DateOnly(2026, 11, 6), AnalystName = "Analyst", Intro = "Read [the rotation](/rotation)." };
        brief.Items.Add(new BriefItem { Article = article, ArticleId = article.Id });
        var edition = BriefEditions.Build(brief, Edition.Public, TlpLevel.Clear);
        var renderer = new BriefRenderer(_md);

        var linked = renderer.Render(edition, new("CTI", null, "https://intranet.example.org/tide", img => "cid:" + img.ContentId));
        foreach (var html in new[] { linked.EmailHtml, linked.WebHtml })
            html.Should().Contain("href=\"https://intranet.example.org/tide/actions/12\"")
                .And.Contain("href=\"https://intranet.example.org/tide/briefs/2026-11-05\"")
                .And.Contain("href=\"https://intranet.example.org/tide/rotation\"");

        var unlinked = renderer.Render(edition, new("CTI", null, null, img => "cid:" + img.ContentId));
        foreach (var html in new[] { unlinked.EmailHtml, unlinked.WebHtml })
            html.Should().Contain("the action").And.Contain("the earlier brief").And.Contain("the rotation")
                .And.NotContain("href=\"/");
    }

    [Fact]
    public void ATemplatesBody_PointsARootLinkAtTheAppsAddress()
    {
        var t = EmailTemplateCatalog.Presenting;
        var body = "Check [the rotation](/rotation). " + string.Join(' ', t.Tokens.Where(k => k.Required).Select(k => "{{" + k.Name + "}}"));

        var html = EmailTemplateRenderer.Render(t, "s", "h", body, new Dictionary<string, string>(), "https://intranet.example.org/tide").BodyHtml;
        html.Should().Contain("href=\"https://intranet.example.org/tide/rotation\"");

        EmailTemplateRenderer.Render(t, "s", "h", body, new Dictionary<string, string>(), appUrl: null).BodyHtml
            .Should().Contain("the rotation").And.NotContain("href=\"/rotation\"");
    }
}
