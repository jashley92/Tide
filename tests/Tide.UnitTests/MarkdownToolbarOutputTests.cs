using System.Text.RegularExpressions;
using FluentAssertions;
using Tide.Application.Content;

namespace Tide.UnitTests;

/// <summary>
/// The Markdown the formatting toolbar writes (wwwroot/js/markdown-toolbar.js) renders as the team meant through
/// MarkdownService: its indent puts a sub-item where its parent's text starts (two spaces under "- ", three under
/// "1. "), which CommonMark nests; its bold, italic and link marks render as such. The strings here are what the
/// toolbar's buttons and keys produce for each case (checked against the script's transforms).
/// </summary>
public partial class MarkdownToolbarOutputTests
{
    private readonly MarkdownService _md = new();

    private static string Squash(string html) => Spaces().Replace(html, "");

    [Fact]
    public void ASubBullet_ByTheIndentButton_IsAListInsideItsBullet()
    {
        // Bulleted list, "Invoice lures", Enter, Tab, "Payroll spoof", Enter, Shift+Tab, "Patch Thursday".
        var html = Squash(_md.ToHtml("- Invoice lures\n  - Payroll spoof\n  - Two reached inboxes\n- Patch Thursday"));
        html.Should().Be("<ul><li>Invoicelures<ul><li>Payrollspoof</li><li>Tworeachedinboxes</li></ul></li><li>PatchThursday</li></ul>");
    }

    [Fact]
    public void ANumberedSubList_IndentedUnderTheNumbersText_Nests_AndTheOuterNumbersCarryOn()
    {
        // Numbered list, "Hunt", Enter, Tab, "SIEM", Enter, "EDR", Enter, Shift+Tab, "Patch": the inner list starts at 1
        // and the outer one carries on at 2.
        var html = Squash(_md.ToHtml("1. Hunt\n   1. SIEM\n   2. EDR\n2. Patch"));
        html.Should().Be("<ol><li>Hunt<ol><li>SIEM</li><li>EDR</li></ol></li><li>Patch</li></ol>");
    }

    [Fact]
    public void BulletsUnderANumber_AndAThirdLevel_Nest()
    {
        var html = Squash(_md.ToHtml("1. Hunt\n   - SIEM\n     - Last 30 days\n2. Patch"));
        html.Should().Be("<ol><li>Hunt<ul><li>SIEM<ul><li>Last30days</li></ul></li></ul></li><li>Patch</li></ol>");
    }

    [Fact]
    public void TwoSpacesUnderANumber_WouldNotNest_WhichIsWhyTheToolbarIndentsThree()
    {
        // The case the toolbar avoids: a fixed two-space indent under "1. " is a new list, not a sub-list.
        Squash(_md.ToHtml("1. Hunt\n  - SIEM")).Should().NotContain("<li>Hunt<ul>");
    }

    [Fact]
    public void BoldItalicAndALink_FromTheButtons_Render()
    {
        var html = _md.ToHtml("- **Patch** the *edge* boxes: see [the advisory](https://vendor.example/advisory)");
        html.Should().Contain("<strong>Patch</strong>").And.Contain("<em>edge</em>")
            .And.Contain("<a href=\"https://vendor.example/advisory\" target=\"_blank\" rel=\"noopener noreferrer\">the advisory</a>");
        _md.ToHtml("***both***").Should().Contain("<em><strong>both</strong></em>");
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
