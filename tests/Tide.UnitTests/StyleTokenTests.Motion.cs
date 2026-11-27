using System.Text.RegularExpressions;
using AwesomeAssertions;

namespace Tide.UnitTests;

/// <summary>The scripts' motion keeps to the stylesheets' rules: timing from the tokens, so the app keeps one rhythm, and
/// only what the browser can composite moves, so a frame never waits on layout.</summary>
public partial class StyleTokenTests
{
    [Fact]
    public void Scripts_TakeTheirTimingFromTokens_ThatAreDefined()
    {
        var js = Stylesheets.Scripts();
        var defined = Defined().Matches(Stylesheets.ReadAll()).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

        TokenRead().Matches(js).Select(m => m.Groups[1].Value).Distinct().Where(t => !defined.Contains(t))
            .Should().BeEmpty("a token a script reads that the stylesheet doesn't define times nothing");
        ScaledToken().Matches(js).Select(m => m.Value).Should().BeEmpty("a duration scaled in a script is one the tokens don't name");
        ScriptDuration().Matches(js).Select(m => m.Value).Should().BeEmpty("a script's durations come from the tokens");
    }

    [Fact]
    public void Scripts_AnimateTransformAndOpacity_NotLayout()
    {
        // Keyframes written in an animate() call: moving a thing by its left, top, width or height (or repainting it with a
        // filter) lays out or paints the page on every frame, where a transform is only composited.
        KeyframesWritten().Matches(Stylesheets.Scripts()).Select(m => m.Groups[1].Value).Where(k => LayoutKey().IsMatch(k))
            .Should().BeEmpty();
    }

    [Fact]
    public void Transitions_MoveNoLayout_ButTheTacticColumnsFolding()
    {
        // A transition of a box's size or place lays the page out on every frame. The one exception: an ATT&CK tactic
        // column folding away while the picker filters, inside its own scrolled grid.
        TransitionRule().Matches(Stylesheets.ReadAll())
            .Where(r => !r.Groups["sel"].Value.Contains(".tide-atkm-col", StringComparison.Ordinal))
            .SelectMany(r => TopLevelComma().Split(r.Groups["value"].Value).Select(t => r.Groups["sel"].Value.Trim() + ": " + t.Trim()))
            .Where(t => LayoutProperty().IsMatch(t[(t.LastIndexOf(": ", StringComparison.Ordinal) + 2)..]))
            .Should().BeEmpty();
    }

    [GeneratedRegex(@"\b(?:ms|token)\('(--tide-[a-z0-9-]+)'\)")]
    private static partial Regex TokenRead();

    [GeneratedRegex(@"\bms\('--tide-[a-z0-9-]+'\)\s*[*/]\s*[\d.]+")]
    private static partial Regex ScaledToken();

    [GeneratedRegex(@"\b(?:duration|delay)\s*:\s*\d")]
    private static partial Regex ScriptDuration();

    [GeneratedRegex(@"\.animate\(\s*\[(.*?)\]\s*,", RegexOptions.Singleline)]
    private static partial Regex KeyframesWritten();

    [GeneratedRegex(@"\b(?:left|top|right|bottom|width|height|margin\w*|filter|borderRadius)\s*:")]
    private static partial Regex LayoutKey();

    [GeneratedRegex(@"(?<sel>[^{}]+)\{[^{}]*?transition\s*:(?<value>[^;}]*)")]
    private static partial Regex TransitionRule();

    [GeneratedRegex(@"^(?:width|height|top|left|right|bottom|inset|flex-basis|margin(?:-[a-z]+)?|border-width|max-height|min-height)\b")]
    private static partial Regex LayoutProperty();
}
