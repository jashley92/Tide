using System.Text.RegularExpressions;
using AwesomeAssertions;

namespace Tide.UnitTests;

/// <summary>The scripts' motion keeps to the same rules as the stylesheets' (StyleTokenTests): timing from the tokens, so
/// the app keeps one rhythm, and only what the browser can composite moves, so a frame never waits on layout.</summary>
public partial class MotionScriptTests
{
    private static string Scripts() =>
        string.Join("\n", Directory.GetFiles(Path.Combine(DeployScriptTests.RepoRoot(), "src", "Tide.Web", "wwwroot", "js"), "*.js")
            .Select(File.ReadAllText));

    [Fact]
    public void Scripts_TakeTheirTimingFromTokens_ThatAreDefined()
    {
        var js = Scripts();
        var defined = Defined().Matches(Stylesheets.ReadAll()).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

        TokenRead().Matches(js).Select(m => m.Groups[1].Value).Distinct().Where(t => !defined.Contains(t))
            .Should().BeEmpty("a token a script reads that the stylesheet doesn't define times nothing");
        Scaled().Matches(js).Select(m => m.Value).Should().BeEmpty("a duration scaled in a script is one the tokens don't name");
        LiteralDuration().Matches(js).Select(m => m.Value).Should().BeEmpty("a script's durations come from the tokens");
    }

    [Fact]
    public void Scripts_AnimateTransformAndOpacity_NotLayout()
    {
        // Keyframes written in an animate() call: moving a thing by its left, top, width or height (or repainting it with a
        // filter) lays out or paints the page on every frame, where a transform is only composited.
        KeyframesWritten().Matches(Scripts()).Select(m => m.Groups[1].Value).Where(k => LayoutKey().IsMatch(k))
            .Should().BeEmpty();
    }

    [Fact]
    public void Stylesheets_TransitionNoLayout_ButTheTacticColumnsFolding()
    {
        // The one exception: an ATT&CK tactic column folding away while the picker filters, inside its own scrolled grid.
        TransitionDeclaration().Matches(Stylesheets.ReadAll()).Select(m => m.Groups[1].Value)
            .Where(v => LayoutTransition().IsMatch(v) && !v.Contains("flex-basis", StringComparison.Ordinal))
            .Should().BeEmpty();
    }

    [GeneratedRegex(@"(--tide-[a-z0-9-]+)\s*:")]
    private static partial Regex Defined();

    [GeneratedRegex(@"\b(?:ms|token)\('(--tide-[a-z0-9-]+)'\)")]
    private static partial Regex TokenRead();

    [GeneratedRegex(@"\bms\('--tide-[a-z0-9-]+'\)\s*[*/]\s*[\d.]+")]
    private static partial Regex Scaled();

    [GeneratedRegex(@"\b(?:duration|delay)\s*:\s*\d")]
    private static partial Regex LiteralDuration();

    [GeneratedRegex(@"\.animate\(\s*\[(.*?)\]\s*,", RegexOptions.Singleline)]
    private static partial Regex KeyframesWritten();

    [GeneratedRegex(@"\b(?:left|top|right|bottom|width|height|margin\w*|filter|borderRadius)\s*:")]
    private static partial Regex LayoutKey();

    [GeneratedRegex(@"transition\s*:([^;}]*)")]
    private static partial Regex TransitionDeclaration();

    [GeneratedRegex(@"(?:^|,)\s*(?:width|height|top|left|right|bottom|inset|margin(?:-[a-z]+)?|max-height|min-height)\s")]
    private static partial Regex LayoutTransition();
}
