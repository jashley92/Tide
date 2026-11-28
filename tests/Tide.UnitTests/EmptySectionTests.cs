using System.Text.RegularExpressions;
using AwesomeAssertions;

namespace Tide.UnitTests;

/// <summary>A section with nothing in it says so through SectionEmpty (an icon that says what's missing, then the line),
/// so every empty section looks alike. A bare muted "No … yet." line slips back in easily, one file at a time.</summary>
public partial class EmptySectionTests
{
    // Lines that read like an empty section but aren't one: a day cell in the rotation calendar, and a search
    // menu's "no match", which is a result rather than a section.
    private static readonly string[] NotSections = ["RotationPage.razor", "OutreachInsertMenu.razor"];

    [Fact]
    public void AnEmptySection_UsesSectionEmpty()
    {
        var components = Path.Combine(DeployScriptTests.RepoRoot(), "src", "Tide.Web", "Components");
        var bare = Directory.GetFiles(components, "*.razor", SearchOption.AllDirectories)
            .Where(f => !NotSections.Contains(Path.GetFileName(f)))
            .SelectMany(f => File.ReadAllLines(f).Select((line, i) => (f, i, line)))
            .Where(x => BareEmptyLine().IsMatch(x.line))
            .Select(x => $"{Path.GetFileName(x.f)}:{x.i + 1}: {x.line.Trim()}");

        bare.Should().BeEmpty("an empty section is a <SectionEmpty Icon=\"@Icons.…\">, not a bare muted line");
    }

    // <p class="small tide-muted …">No …, None …, Nothing …, Nobody … (or the same on a div)
    [GeneratedRegex("""<(?:p|div) class="(?:small tide-muted|tide-muted small)[^"]*">\s*(?:No|None|Nothing|Nobody)\b""")]
    private static partial Regex BareEmptyLine();
}
