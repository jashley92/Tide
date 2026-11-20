using System.Globalization;
using System.Text.RegularExpressions;
using AwesomeAssertions;

namespace Tide.UnitTests;

/// <summary>Every width breakpoint in the stylesheets and scripts is one of a few, written one way: <c>min-width: Npx</c>
/// on the wide side and <c>max-width: (N - 0.02)px</c> on the narrow side. Mixed spellings (640px and 640.98px, 900px on
/// both sides, 1000px against 1001px) left half-applied layouts under browser zoom, where the window is a fractional
/// number of pixels wide: at 640.5px some phone rules were on and others off, at exactly 900px the phone editor sat
/// under the desktop top bar, and between 1000px and 1001px neither of a slide's two layouts applied.</summary>
public partial class BreakpointTests
{
    /// <summary>The breakpoints there are: N is the first width of the wide side.</summary>
    private static readonly int[] Agreed = [421, 481, 641, 701, 768, 900, 1001, 1100, 1200, 1400, 1500, 1600, 1760, 1800];

    [Fact]
    public void EveryWidthQuery_IsAnAgreedBreakpoint_WithNeitherOverlapNorGap()
    {
        var root = Path.Combine(DeployScriptTests.RepoRoot(), "src", "Tide.Web", "wwwroot");
        var css = Directory.GetFiles(root, "*.css", SearchOption.AllDirectories)
            .Where(f => !f.Contains("bootstrap", StringComparison.OrdinalIgnoreCase));
        var js = Directory.GetFiles(Path.Combine(root, "js"), "*.js", SearchOption.AllDirectories);
        var sources = css.Concat(js).ToDictionary(f => Path.GetRelativePath(root, f), File.ReadAllText);

        Queries(sources).Should().HaveCountGreaterThan(20, "the check must be reading the stylesheets");
        Problems(sources).Should().BeEmpty();
    }

    [Theory]
    [InlineData("@media (max-width: 640px) { a { } } @media (min-width: 641px) { b { } }", "leaves a gap")]
    [InlineData("@media (max-width: 900px) { a { } } @media (min-width: 900px) { b { } }", "overlaps")]
    [InlineData("@media (min-width: 1000.02px) { a { } }", "overlaps")]
    [InlineData("@media (min-width: 899px) { a { } }", "overlaps")]
    [InlineData("@media (min-width: 1050px) { a { } }", "not an agreed breakpoint")]
    [InlineData("@media (max-width: 40em) { a { } }", "not in px")]
    [InlineData("@media (width < 900px) { a { } }", "range syntax")]
    public void ASeam_IsCaught(string text, string problem)
    {
        Problems(new Dictionary<string, string> { ["x.css"] = text }).Should().ContainMatch($"*{problem}*");
    }

    [Fact]
    public void TheAgreedPairs_Pass()
    {
        Problems(new Dictionary<string, string>
        {
            ["x.css"] = "@media (max-width: 899.98px) { a { } } @media (min-width: 900px) and (max-width: 1099.98px) { b { } }",
            ["x.js"] = "matchMedia('(min-width: 1500px)')",
        }).Should().BeEmpty();
    }

    private sealed record Query(string File, string Side, decimal Px, string Unit, string Text);

    private static List<Query> Queries(IReadOnlyDictionary<string, string> sources) =>
        sources.SelectMany(s => Width().Matches(Conditions(s.Key, s.Value)).Select(m => new Query(s.Key, m.Groups[1].Value,
            decimal.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), m.Groups[3].Value, m.Value))).ToList();

    /// <summary>The media conditions in a file: each @media rule's prelude, or each matchMedia's argument in script.</summary>
    private static string Conditions(string file, string text) => string.Join("\n", file.EndsWith(".js", StringComparison.Ordinal)
        ? MatchMedia().Matches(text).Select(m => m.Groups[1].Value)
        : MediaPrelude().Matches(text).Select(m => m.Groups[1].Value));

    private static List<string> Problems(IReadOnlyDictionary<string, string> sources)
    {
        var problems = new List<string>();
        foreach (var (file, text) in sources)
            problems.AddRange(Range().Matches(Conditions(file, text)).Select(m => $"{file}: {m.Value} uses range syntax; write min-width or max-width"));

        foreach (var q in Queries(sources))
        {
            if (q.Unit != "px")
            {
                problems.Add($"{q.File}: {q.Text} is not in px");
                continue;
            }
            // The breakpoint a query is for: the agreed one within a couple of pixels, whichever side it's on.
            var near = Agreed.Where(n => Math.Abs(n - q.Px) <= 2).ToList();
            if (near.Count == 0)
            {
                problems.Add($"{q.File}: {q.Text} is not an agreed breakpoint ({string.Join(", ", Agreed)}; min-width N, max-width N - 0.02)");
                continue;
            }
            var n = near.MinBy(n => Math.Abs(n - q.Px));
            var wanted = q.Side == "min" ? n : n - 0.02m;
            // A wide side starting lower, or a narrow side ending higher, than the agreed pair overlaps the other side;
            // the other way round it leaves widths that neither side covers.
            var overlaps = q.Side == "min" ? q.Px < wanted : q.Px > wanted;
            if (q.Px != wanted)
                problems.Add($"{q.File}: {q.Text} {(overlaps ? "overlaps" : "leaves a gap at")} the {n}px breakpoint; write ({q.Side}-width: {wanted}px)");
        }
        return problems;
    }

    [GeneratedRegex(@"@media([^{]*)\{")]
    private static partial Regex MediaPrelude();

    [GeneratedRegex(@"matchMedia\(\s*['""`]([^'""`]*)['""`]")]
    private static partial Regex MatchMedia();

    [GeneratedRegex(@"\((min|max)-width:\s*([0-9]+(?:\.[0-9]+)?)([a-z]*)\)")]
    private static partial Regex Width();

    [GeneratedRegex(@"\(\s*width\s*[<>]|[<>]=?\s*width\s*\)")]
    private static partial Regex Range();
}
