using AwesomeAssertions;

namespace Tide.UnitTests;

/// <summary>
/// Slow server actions (a fetch, a check, a pull) show they're busy one way: the button keeps its words and size, and
/// data-busy runs the loading scan bar along its foot once the action has taken longer than --tide-dur-patience. And a
/// Copy button confirms in place: data-copied turns its icon into a check for --tide-dur-signature.
/// </summary>
public class BusyFeedbackTests
{
    private static string Components => Path.Combine(DeployScriptTests.RepoRoot(), "src", "Tide.Web", "Components");

    [Fact]
    public void NoComponent_ShowsABootstrapSpinner_OrSwapsAButtonsWordsWhileItWaits()
    {
        var files = Directory.GetFiles(Components, "*.razor", SearchOption.AllDirectories).Select(f => (Name: Path.GetFileName(f), Text: File.ReadAllText(f))).ToList();
        files.Where(f => f.Text.Contains("spinner-border", StringComparison.Ordinal)).Select(f => f.Name).Should().BeEmpty();
        files.Where(f => f.Text.Contains("\"Checking…\" : \"Check", StringComparison.Ordinal)).Select(f => f.Name).Should().BeEmpty();
    }

    [Fact]
    public void TheBusyBar_WaitsItsPatience_AndBreathesUnderReducedMotion()
    {
        var css = Stylesheets.ReadAll();
        css.Should().Contain("--tide-dur-patience:");
        css.Should().MatchRegex(@"\[data-busy\]::after \{[^}]*animation: tide-busy-shown 0s var\(--tide-dur-patience\) forwards, tide-scan-bar var\(--tide-dur-loop\)");
        css.Should().MatchRegex(@"prefers-reduced-motion: reduce\) \{[^@]*\[data-busy\]::after \{[^}]*tide-breathe");
    }

    [Fact]
    public void ACopyThatWorked_PulsesItsButton_AndTheStatusStillSaysSo()
    {
        var js = File.ReadAllText(Path.Combine(Stylesheets.Web, "js", "clipboard.js"));
        js.Should().Contain("ts.pulse(btn, 'data-copied')").And.Contain("status.textContent = text;");
        var css = Stylesheets.ReadAll();
        css.Should().Contain("[data-copied] .bi::before { content: \"\\f272\"; }");
        css.Should().MatchRegex(@"prefers-reduced-motion: reduce\) \{\s*\[data-copied\]::after \{ display: none; \}");
    }
}
