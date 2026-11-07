using FluentAssertions;
using Tide.Application.Content;
using Tide.Application.Files;
using Tide.Application.Operations;

namespace Tide.UnitTests.Audit;

/// <summary>Regression tests from the security audit: rendering, exports and masking stay inert to hostile input.</summary>
public class SecurityAuditTests
{
    private readonly MarkdownService _md = new();

    [Theory]
    [InlineData("[x](//evil.example/stage2)")]
    [InlineData("[x](  //evil.example)")]
    public void AProtocolRelativeLink_IsNotALinkToAnotherHost(string markdown)
    {
        // "//host" names no scheme, so it slipped past the http/https/mailto check as a relative address.
        _md.ToHtml(markdown).Should().Contain("href=\"#\"").And.NotContain("evil.example\"");
        _md.ToInlineHtml(markdown).Should().Contain("href=\"#\"");
    }

    // A5-R-2 (Agent 5's acceptance test): a backslash is a slash in a scheme-relative address.
    private string Href(string url)
    {
        var html = _md.ToHtml($"[x]({url})");
        var at = html.IndexOf("href=\"", StringComparison.Ordinal) + 6;
        return html[at..html.IndexOf('"', at)];
    }

    [Theory]
    [InlineData("/\\evil.example")]
    [InlineData("\\\\evil.example")]
    [InlineData("/\\/evil.example")]
    [InlineData("\\/evil.example")]
    public void ABackslashSpellingOfAProtocolRelativeLink_IsNeutralisedToo(string url) => Href(url).Should().Be("#", url);

    [Theory]
    [InlineData("/local/path")]
    [InlineData("/")]
    [InlineData("#top")]
    [InlineData("./relative")]
    [InlineData("https://example.com/x")]
    public void ALocalOrAllowedLink_KeepsItsAddress(string url) => Href(url).Should().NotBe("#", url);

    [Theory]
    [InlineData("[x](javascript&colon;alert(1))")]
    [InlineData("[x](JaVaScRiPt:alert(1))")]
    [InlineData("[x](&#106;avascript:alert(1))")]
    [InlineData("[x](ja&#x76;ascript:alert(1))")]
    [InlineData("[x](<javascript:alert(1)>)")]
    [InlineData("[x](javascript\\:alert(1))")]
    [InlineData("[x]( javascript:alert(1))")]
    [InlineData("[x](data:text/html;base64,PHNjcmlwdD4=)")]
    [InlineData("[x](vbscript:msgbox(1))")]
    [InlineData("[x](https:evil)")]
    [InlineData("[x]: javascript:alert(1)\n\n[x]")]
    [InlineData("[x](javascript:alert(1)\u0000)")]
    public void AnUnsafeLinkScheme_HoweverItIsSpelled_IsNeutralised(string markdown)
    {
        var html = _md.ToHtml(markdown);
        html.Should().Contain("href=\"#\"").And.NotContainEquivalentOf("javascript:").And.NotContain("data:").And.NotContain("vbscript:");
    }

    [Fact]
    public void ALinkTitle_CannotBreakOutOfTheAttribute()
    {
        _md.ToHtml("[x](https://a \"x\\\" onmouseover=\\\"alert(1)\")").Should().NotContain("onmouseover");
    }

    [Fact]
    public void AnAngleBracketAutolink_WithAnUnsafeScheme_StaysText()
    {
        _md.ToHtml("<javascript:alert(1)>").Should().NotContain("<a ").And.Contain("javascript:alert(1)");
    }

    [Fact]
    public void AnAngleBracketAutolink_OpensInANewTab_WithoutAReferrerOrOpener_LikeAnyOtherLink()
    {
        // Clicked on a published brief or an action, it mustn't take the reader's tab away from TIDE, or tell the site
        // it links to the TIDE page it was clicked on.
        _md.ToHtml("<https://vendor.example/advisory>").Should()
            .Contain("<a href=\"https://vendor.example/advisory\" target=\"_blank\" rel=\"noopener noreferrer\">https://vendor.example/advisory</a>");
        _md.ToInlineHtml("see <https://vendor.example/a>").Should().Contain("target=\"_blank\" rel=\"noopener noreferrer\"");
        _md.ToHtml("<team@vendor.example>").Should().Contain("<a href=\"mailto:team@vendor.example\">team@vendor.example</a>", "an email address opens the mail app");
        // The email drops both, as it does for a [text](url) link.
        Tide.Application.Distribution.EmailComposer.InlineStyles(_md.ToHtml("<https://vendor.example/a>")).Should().NotContain("target=");
    }

    // A5-4 (Agent 5's acceptance test): text well within a field's limit that nests deeper than Markdig allows.
    private static string Shape(string shape) => shape switch
    {
        "[ x200" => new string('[', 200) + "x",
        _ => string.Concat(Enumerable.Repeat("> ", 200)) + "x",
    };

    [Theory]
    [InlineData("[ x200")]
    [InlineData("> x200")]
    public void StoredMarkdown_WithinTheColumnLimit_AlwaysRenders(string shape)
    {
        var text = Shape(shape);
        text.Length.Should().BeLessThan(2000, "it fits in a bullet");
        FluentActions.Invoking(() => _md.ToHtml(text)).Should().NotThrow();
        FluentActions.Invoking(() => _md.ToInlineHtml(text)).Should().NotThrow();
        FluentActions.Invoking(() => _md.ToPlainText(text)).Should().NotThrow();
        _md.ToHtml(text).Should().Contain(System.Net.WebUtility.HtmlEncode(text.Length > 50 ? text[..50] : text), "it's shown as typed, escaped").And.NotContain("<a ");
        MarkdownService.IsRenderable(text).Should().BeFalse();
        MarkdownService.IsRenderable("**bold** [x](https://a)").Should().BeTrue();
    }

    [Theory]
    [InlineData("=1+1", "'=1+1")]
    [InlineData("+1", "'+1")]
    [InlineData("-1", "'-1")]
    [InlineData("@SUM(1)", "'@SUM(1)")]
    [InlineData("\t=1", "'\t=1")]
    [InlineData("\r=1", "\"'\r=1\"")]
    [InlineData("=1,2", "\"'=1,2\"")]
    [InlineData("\"=1", "\"\"\"=1\"")]
    public void ACsvCell_NeverStartsAFormula(string text, string expected)
    {
        Csv.Cell(text).Should().Be(expected);
    }

    [Theory]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>")]
    [InlineData("<!DOCTYPE html><html><script>alert(1)</script></html>")]
    [InlineData("%PDF-1.4")]
    [InlineData("")]
    public void TheSniffer_RefusesAnythingButARasterImage(string content)
    {
        ImageSniffer.RasterContentType(System.Text.Encoding.ASCII.GetBytes(content)).Should().BeNull();
    }

    [Fact]
    public void TheSniffer_NamesARasterByItsBytes_NotItsLabel()
    {
        ImageSniffer.RasterContentType([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0]).Should().Be("image/png");
        ImageSniffer.RasterContentType([0xFF, 0xD8, 0xFF, 0xE0]).Should().Be("image/jpeg");
        ImageSniffer.RasterContentType("GIF89a"u8).Should().Be("image/gif");
        ImageSniffer.RasterContentType("RIFF\0\0\0\0WEBP"u8).Should().Be("image/webp");
        ImageSniffer.RasterContentType("RIFF\0\0\0\0WAVE"u8).Should().BeNull();
    }

    [Theory]
    [InlineData("https://feeds.example.com/x.xml?apikey=SECRET123", "SECRET123")]
    [InlineData("https://feeds.example.com/x.xml?token=SECRET123&f=rss", "SECRET123")]
    [InlineData("https://user:SECRET123@feeds.example.com/x.xml", "SECRET123")]
    public void AFeedAddress_LosesItsKeyWhenShown(string url, string secret)
    {
        SecretMask.UrlHasSecret(url).Should().BeTrue();
        SecretMask.Url(url).Should().NotContain(secret).And.Contain("feeds.example.com");
    }

    [Fact]
    public void ErrorText_LosesCredentials()
    {
        SecretMask.Scrub("Login failed; Password=Hunter2;Server=x and apikey: ABC and smtp://bob:pw@relay/")
            .Should().NotContain("Hunter2").And.NotContain("ABC").And.NotContain("bob:pw");
    }

    [Theory]
    [InlineData("Email:SmtpPassword")]
    [InlineData("Cve:NvdApiKey")]
    [InlineData("Some:ClientSecret")]
    [InlineData("Some:AccessToken")]
    public void ASettingNamedLikeASecret_IsOnlyEverShownAsSet(string key)
    {
        SecretMask.Display(key, SettingKind.Plain, "value").Should().Be(SecretMask.SetSecret);
    }
}
