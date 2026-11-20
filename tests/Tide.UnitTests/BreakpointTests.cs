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
        // The site's stylesheets and scripts, and any component's scoped stylesheet or <style> block, so one added
        // later is held to the same breakpoints.
        var web = Path.Combine(DeployScriptTests.RepoRoot(), "src", "Tide.Web");
        var root = Path.Combine(web, "wwwroot");
        var css = Directory.GetFiles(root, "*.css", SearchOption.AllDirectories)
            .Where(f => !f.Contains("bootstrap", StringComparison.OrdinalIgnoreCase));
        var js = Directory.GetFiles(Path.Combine(root, "js"), "*.js", SearchOption.AllDirectories);
        var components = Directory.GetFiles(web, "*.razor*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".razor", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".razor.css", StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
        var sources = css.Concat(js).Concat(components).ToDictionary(f => Path.GetRelativePath(web, f), File.ReadAllText);

        Queries(sources).Should().HaveCountGreaterThan(20, "the check must be reading the stylesheets");
        Problems(sources).Should().BeEmpty();
    }

    [Theory]
    [InlineData("x.css", "@media (max-width: 640px) { a { } } @media (min-width: 641px) { b { } }", "leaves a gap")]
    [InlineData("x.css", "@media (max-width: 900px) { a { } } @media (min-width: 900px) { b { } }", "overlaps")]
    [InlineData("x.css", "@media (min-width: 1000.02px) { a { } }", "overlaps")]
    [InlineData("x.css", "@media (min-width: 899px) { a { } }", "overlaps")]
    [InlineData("x.css", "@media (max-width: 900px ) { a { } }", "overlaps")]
    [InlineData("x.css", "@media ( max-width:900px) { a { } }", "overlaps")]
    [InlineData("x.css", "@media (max-width : 900 px) { a { } }", "overlaps")]
    [InlineData("x.css", "@MEDIA (MAX-WIDTH:900PX) { a { } }", "overlaps")]
    [InlineData("x.css", "@media (min-width: 1050px) { a { } }", "not an agreed breakpoint")]
    [InlineData("x.css", "@media (max-width: 40em) { a { } }", "not in px")]
    [InlineData("x.css", "@media (width < 900px) { a { } }", "range syntax")]
    [InlineData("x.css", "@media (WIDTH >= 900px) { a { } }", "range syntax")]
    [InlineData("x.js", "matchMedia(' ( MAX-WIDTH: 900px ) ')", "overlaps")]
    [InlineData("x.js", "matchMedia(query)", "isn't a string literal")]
    [InlineData("x.js", "matchMedia('(min-width: ' + wide + 'px)')", "isn't a string literal")]
    [InlineData("x.js", "matchMedia(`(min-width: ${wide}px)`)", "isn't a string literal")]
    [InlineData("Components/X.razor.css", "@media (max-width: 900px) { a { } }", "overlaps")]
    [InlineData("Components/X.razor", "<div></div>\n<style>\n@media (max-width: 900px) { a { } }\n</style>", "overlaps")]
    public void ASeam_IsCaught(string file, string text, string problem)
    {
        Problems(new Dictionary<string, string> { [file] = text }).Should().ContainMatch($"*{problem}*");
    }

    [Fact]
    public void TheAgreedPairs_Pass()
    {
        Problems(new Dictionary<string, string>
        {
            ["x.css"] = "@media (max-width: 899.98px) { a { } } @media (min-width: 900px) and (max-width: 1099.98px) { b { } }",
            ["x.js"] = "var m = window.matchMedia && window.matchMedia('(min-width: 1500px)'); var r = matchMedia(\"(prefers-reduced-motion: reduce)\");",
            ["X.razor"] = "<p>@media is only a word here</p><style>@media (min-width: 641px) { a { } }</style>",
        }).Should().BeEmpty();
    }

    private sealed record Query(string File, string Side, decimal Px, string Unit, string Text);

    private static List<Query> Queries(IReadOnlyDictionary<string, string> sources) =>
        sources.SelectMany(s => Width().Matches(Conditions(s.Key, s.Value)).Select(m => new Query(s.Key, m.Groups[1].Value.ToLowerInvariant(),
            decimal.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), m.Groups[3].Value.ToLowerInvariant(), m.Value))).ToList();

    /// <summary>The media conditions in a file: each @media rule's prelude (in a .razor file, those in its &lt;style&gt;
    /// blocks), or each matchMedia's string argument in script.</summary>
    private static string Conditions(string file, string text)
    {
        if (file.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
            return string.Join("\n", MatchMediaCall().Matches(text).Where(m => m.Groups["arg"].Success).Select(m => m.Groups["arg"].Value));
        if (file.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
            text = string.Join("\n", StyleBlock().Matches(text).Select(m => m.Groups[1].Value));
        return string.Join("\n", MediaPrelude().Matches(text).Select(m => m.Groups[1].Value));
    }

    private static List<string> Problems(IReadOnlyDictionary<string, string> sources)
    {
        var problems = new List<string>();
        foreach (var (file, text) in sources)
        {
            // A query built at run time can't be checked here, so script passes matchMedia a plain string.
            if (file.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
                problems.AddRange(MatchMediaCall().Matches(text).Where(m => !m.Groups["arg"].Success || m.Groups["arg"].Value.Contains("${", StringComparison.Ordinal))
                    .Select(m => $"{file}: {m.Value}… isn't a string literal; write the query out so its breakpoint can be checked"));
            problems.AddRange(Range().Matches(Conditions(file, text)).Select(m => $"{file}: {m.Value} uses range syntax; write min-width or max-width"));
        }

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

    [GeneratedRegex(@"@media([^{]*)\{", RegexOptions.IgnoreCase)]
    private static partial Regex MediaPrelude();

    [GeneratedRegex(@"<style\b[^>]*>(.*?)</style\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex StyleBlock();

    /// <summary>A call of matchMedia; <c>arg</c> is its argument when that is one string literal and nothing more.</summary>
    [GeneratedRegex(@"\bmatchMedia\s*\(\s*(?:(?<q>['""`])(?<arg>(?:(?!\k<q>).)*)\k<q>\s*\))?")]
    private static partial Regex MatchMediaCall();

    [GeneratedRegex(@"\(\s*(min|max)-width\s*:\s*([0-9]+(?:\.[0-9]+)?)\s*([a-z]*)\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex Width();

    [GeneratedRegex(@"\(\s*width\s*[<>]|[<>]=?\s*width\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex Range();
}
