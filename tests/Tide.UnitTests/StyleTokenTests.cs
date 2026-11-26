using System.Text.RegularExpressions;
using AwesomeAssertions;

namespace Tide.UnitTests;

/// <summary>The stylesheets use only the design tokens they define (css/00-tokens.css): a misspelt or missing one drops its rule silently
/// (U9-2: the adversary heat grid's red outline for a technique with no detection rule never showed).</summary>
public partial class StyleTokenTests
{
    [Fact]
    public void EveryTokenUsed_IsDefined()
    {
        var css = Path.Combine(DeployScriptTests.RepoRoot(), "src", "Tide.Web", "wwwroot");
        var files = Directory.GetFiles(css, "*.css", SearchOption.AllDirectories).Where(f => !f.Contains("bootstrap", StringComparison.OrdinalIgnoreCase)).ToList();
        var text = string.Join("\n", files.Select(File.ReadAllText));
        var defined = Defined().Matches(text).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

        Used().Matches(text).Select(m => m.Groups[1].Value).Distinct().Where(t => !defined.Contains(t)).Should().BeEmpty();
    }

    [Fact]
    public void RenderedMarkdown_BreaksAWordLongerThanItsCard()
    {
        // A comment or an action's details quoting a long URL or a SHA-256 ran out of its card and scrolled the page
        // sideways on a narrow window: .tide-md (the Md component's wrapper) set no wrapping.
        var css = Stylesheets.ReadAll();
        MarkdownRule().Match(css).Groups[1].Value.Should().MatchRegex(@"overflow-wrap:\s*anywhere");
    }

    [Fact]
    public void TransitionsAndAnimations_TakeTheirTimingFromTokens()
    {
        // Every duration and delay comes from a --tide-dur-* token, so the app keeps one rhythm and the reduced-motion
        // switch (--tide-motion) can reach the movement. A literal ".15s" slips past both. 0s is fine: it means "at once".
        var literals = MotionDeclaration().Matches(Stylesheets.ReadAll())
            .Where(d => LiteralDuration().Matches(d.Groups[1].Value).Any(t => t.Value is not ("0s" or "0ms")))
            .Select(d => d.Value.Trim());

        literals.Should().BeEmpty();
    }

    [Fact]
    public void Movement_StopsUnderReducedMotion()
    {
        // A transition that moves something (a transform, a width, a margin) takes a --tide-move-* duration, which the
        // reduced-motion switch zeroes. A fade or color change keeps its plain --tide-dur-*, so the change still shows.
        var moving = TransitionDeclaration().Matches(Stylesheets.ReadAll())
            .SelectMany(d => TopLevelComma().Split(d.Groups[1].Value))
            .Select(t => t.Trim())
            .Where(t => MovementProperty().IsMatch(t) && !t.Contains("--tide-move-", StringComparison.Ordinal));

        moving.Should().BeEmpty();
    }

    [Fact]
    public void LayersOverThePage_StackFromTokens()
    {
        // The layers over the page (the rail, popovers, toasts, the tour, dialogs) take their z-index from the --tide-z-*
        // scale, so a new overlay is placed by name rather than by guessing a number between two others.
        GlobalLayer().Matches(Stylesheets.ReadAll()).Select(m => m.Value).Should().BeEmpty();
    }

    [Fact]
    public void ReducedMotion_TurnsTheMotionSwitchOff()
    {
        Stylesheets.ReadAll().Should().MatchRegex(@"@media\s*\(prefers-reduced-motion:\s*reduce\)\s*\{\s*:root\s*\{\s*--tide-motion:\s*0;");
    }

    [GeneratedRegex(@"(?:transition|animation)(?:-duration|-delay)?\s*:([^;}]*)")]
    private static partial Regex MotionDeclaration();

    [GeneratedRegex(@"(?<![\w.-])\d*\.?\d+m?s\b")]
    private static partial Regex LiteralDuration();

    [GeneratedRegex(@"transition\s*:([^;}]*)")]
    private static partial Regex TransitionDeclaration();

    [GeneratedRegex(@",(?![^(]*\))")]
    private static partial Regex TopLevelComma();

    [GeneratedRegex(@"^(?:transform|translate|rotate|scale|width|height|flex-basis|inset|top|right|bottom|left|margin(?:-[a-z]+)?|border-width)\b")]
    private static partial Regex MovementProperty();

    [GeneratedRegex(@"z-index\s*:\s*\d{4,}")]
    private static partial Regex GlobalLayer();

    [GeneratedRegex(@"(--tide-[a-z0-9-]+)\s*:")]
    private static partial Regex Defined();

    [GeneratedRegex(@"var\(\s*(--tide-[a-z0-9-]+)")]
    private static partial Regex Used();

    [GeneratedRegex(@"(?m)^\.tide-md\s*\{([^}]*)\}")]
    private static partial Regex MarkdownRule();
}
