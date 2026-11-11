using System.Text.RegularExpressions;
using AwesomeAssertions;
using Tide.Application.Actions;
using Tide.Application.Content;

namespace Tide.IntegrationTests;

/// <summary>
/// A quote in rendered Markdown reads as a quote: a team's reply recorded on an action (and any "&gt; " line someone
/// writes) is a blockquote, and the app's stylesheet sets it apart, as the email templates already do.
/// </summary>
public class MarkdownQuoteTests(TideApp app) : IClassFixture<TideApp>
{
    [Fact]
    public void ATeamsRecordedReply_RendersAsAQuote()
    {
        var html = new MarkdownService().ToHtml(TeamUpdates.ReplyComment("Cyber Defense", "Patched both gateways.\nRebooted at 02:00."));

        html.Should().Contain("<blockquote>").And.Contain("Patched both gateways.");
    }

    [Fact]
    public async Task RenderedMarkdown_SetsAQuoteApart_WithARuleAndMutedText_FromTheTokens()
    {
        var css = await app.CreateClient().GetStringAsync("/app.css");

        var rule = Regex.Match(css, @"\.tide-md blockquote\s*\{(?<body>[^}]*)\}");
        rule.Success.Should().BeTrue("rendered Markdown's quotes need a style of their own: Bootstrap's reboot gives a bare blockquote only a margin");
        rule.Groups["body"].Value.Should().Contain("border-left").And.Contain("var(--tide-")
            .And.MatchRegex(@"padding(-left|-inline-start)?\s*:");
    }
}
