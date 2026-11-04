using System.Text.RegularExpressions;
using FluentAssertions;

namespace Tide.UnitTests;

/// <summary>A component's attribute values are encoded once more when rendered, so an HTML entity written in one shows
/// as typed: the search box read <c>Words, &amp;quot;a phrase&amp;quot;</c>, and a screen reader said "ATT amp CK".
/// The character itself is written instead (a single-quoted attribute for a straight quote).</summary>
public partial class RazorAttributeTests
{
    [Fact]
    public void NoAttributeValue_HoldsAnHtmlEntity()
    {
        var src = Path.Combine(DeployScriptTests.RepoRoot(), "src");
        var found = Directory.GetFiles(src, "*.razor", SearchOption.AllDirectories)
            .SelectMany(f => File.ReadAllLines(f).Select((line, i) => (f, i, line)))
            .Where(x => EntityInAttribute().IsMatch(x.line))
            .Select(x => $"{Path.GetFileName(x.f)}:{x.i + 1}: {x.line.Trim()}");

        found.Should().BeEmpty();
    }

    // name="…&entity;…" or name='…&entity;…'
    [GeneratedRegex("""[\w-]+\s*=\s*(?:"[^"]*&(?:[a-zA-Z]+|#\d+|#x[0-9a-fA-F]+);[^"]*"|'[^']*&(?:[a-zA-Z]+|#\d+|#x[0-9a-fA-F]+);[^']*')""")]
    private static partial Regex EntityInAttribute();
}
