using System.Text.RegularExpressions;
using FluentAssertions;

namespace Tide.UnitTests;

/// <summary>The stylesheets use only the design tokens app.css defines: a misspelt or missing one drops its rule silently
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
        var css = File.ReadAllText(Path.Combine(DeployScriptTests.RepoRoot(), "src", "Tide.Web", "wwwroot", "app.css"));
        Regex.Match(css, @"(?m)^\.tide-md\s*\{([^}]*)\}").Groups[1].Value.Should().MatchRegex(@"overflow-wrap:\s*anywhere");
    }

    [GeneratedRegex(@"(--tide-[a-z0-9-]+)\s*:")]
    private static partial Regex Defined();

    [GeneratedRegex(@"var\(\s*(--tide-[a-z0-9-]+)")]
    private static partial Regex Used();
}
