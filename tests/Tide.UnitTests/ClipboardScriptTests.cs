using AwesomeAssertions;

namespace Tide.UnitTests;

/// <summary>
/// The Copy buttons (<c>clipboard.js</c>). A query, a command or an indicator is copied as plain text with LF line breaks
/// and says "Copied."; only the SharePoint view asks for the rich copy and says where to paste it. The rich copy with the
/// SharePoint words used to be what every block got, a hunt query included.
/// </summary>
public class ClipboardScriptTests
{
    private static string Web(params string[] path) =>
        File.ReadAllText(Path.Combine([DeployScriptTests.RepoRoot(), "src", "Tide.Web", .. path]));

    [Fact]
    public void ABlockIsCopiedAsPlainTextWithLfLineBreaks_UnlessItsButtonAsksForTheRichCopy()
    {
        var js = Web("wwwroot", "js", "clipboard.js");
        js.Should().NotContain("SharePoint page.'", "what a copy says by default isn't one feature's words");
        js.Should().Contain("data-copy-rich").And.Contain("textContent").And.Contain(".replace(/\\r\\n?/g, '\\n')");
        js.Should().Contain("|| 'Copied.'");
    }

    [Fact]
    public void OnlyTheSharePointView_AsksForTheRichCopy_AndKeepsItsWords()
    {
        var sharePoint = Web("Components", "Pages", "SharePointPage.razor");
        sharePoint.Should().Contain("data-copy-rich").And.Contain("data-copy-done=\"Copied. Paste it into the SharePoint page.\"");
        var components = Path.Combine(DeployScriptTests.RepoRoot(), "src", "Tide.Web", "Components");
        Directory.GetFiles(components, "*.razor", SearchOption.AllDirectories)
            .Where(f => File.ReadAllText(f).Contains("data-copy-rich", StringComparison.Ordinal)).Select(Path.GetFileName)
            .Should().Equal("SharePointPage.razor");
    }
}
