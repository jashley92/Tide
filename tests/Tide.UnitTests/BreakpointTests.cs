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
        ContainerQueries(sources).Should().NotBeEmpty("the check must be reading the container queries too");
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
    [InlineData("x.css", "@container list (max-width: 800px) { a { } } @container list (min-width: 800px) { b { } }", "overlaps")]
    [InlineData("x.css", "@container list (max-width: 799.5px) { a { } } @container list (min-width: 800px) { b { } }", "leaves a gap")]
    [InlineData("x.css", "@container list (min-width: 800.5px) { a { } }", "min-width: 801px")]
    [InlineData("x.css", "@container list (max-width: 799.98px) { a { } } @container list (min-width: 820px) { b { } }", "aren't a pair")]
    [InlineData("x.css", "@container list (max-width: 799.98px) { a { } } @container list (min-width: 800px) { b { } } @container list (max-width: 899.98px) { c { } }", "aren't a pair")]
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
            // A container's width is its own, not one of the window's breakpoints; a named container's two sides meet.
            ["y.css"] = "@container list (max-width: 799.98px) { a { } } @container list (min-width: 800px) { b { } } @container steps (max-width: 46rem) { c { } }",
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
        problems.AddRange(ContainerProblems(sources));
        return problems;
    }

    private sealed record ContainerQuery(string File, string Name, string Side, decimal Px, string Text);

    /// <summary>The width conditions in px of each <c>@container</c> rule, with its container's name ("" for none).</summary>
    private static List<ContainerQuery> ContainerQueries(IReadOnlyDictionary<string, string> sources) =>
        sources.Where(s => !s.Key.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
            .SelectMany(s => ContainerPrelude().Matches(s.Key.EndsWith(".razor", StringComparison.OrdinalIgnoreCase)
                    ? string.Join("\n", StyleBlock().Matches(s.Value).Select(m => m.Groups[1].Value)) : s.Value)
                .SelectMany(p => Width().Matches(p.Groups["cond"].Value)
                    .Where(m => m.Groups[3].Value.Equals("px", StringComparison.OrdinalIgnoreCase))
                    .Select(m => new ContainerQuery(s.Key, p.Groups["name"].Value.Trim(), m.Groups[1].Value.ToLowerInvariant(),
                        decimal.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), m.Value))))
            .ToList();

    /// <summary>A container query is for the container's own width, so it needn't be one of the window's breakpoints. It
    /// is written the same way, though: <c>min-width: Npx</c> on the wide side and <c>max-width: (N - 0.02)px</c> on the
    /// narrow, a named container's two sides meeting at the same N, so a zoomed page never has both sides or neither.</summary>
    private static IEnumerable<string> ContainerProblems(IReadOnlyDictionary<string, string> sources)
    {
        var queries = ContainerQueries(sources);
        foreach (var q in queries)
        {
            var seam = q.Side == "min" ? q.Px : q.Px + 0.02m;
            if (seam == decimal.Truncate(seam)) continue;
            var n = Math.Round(seam, MidpointRounding.AwayFromZero);
            var wanted = q.Side == "min" ? n : n - 0.02m;
            var overlaps = q.Side == "min" ? q.Px < wanted : q.Px > wanted;
            yield return $"{q.File}: @container {q.Name} {q.Text} {(overlaps ? "overlaps" : "leaves a gap at")} its {n}px seam; write ({q.Side}-width: {wanted}px)";
        }
        // Only a named container's sides pair up: an unnamed query is for whichever container is nearest.
        foreach (var named in queries.Where(q => q.Name.Length > 0).GroupBy(q => q.Name))
        {
            var wide = named.Where(q => q.Side == "min").Select(q => q.Px).ToHashSet();
            var narrow = named.Where(q => q.Side == "max").Select(q => q.Px + 0.02m).ToHashSet();
            if (wide.Count > 0 && narrow.Count > 0 && !wide.SetEquals(narrow))
                yield return $"@container {named.Key}: its min-width sides ({string.Join(", ", wide.Order())}px) and max-width sides (for {string.Join(", ", narrow.Order())}px) aren't a pair";
        }
    }

    [GeneratedRegex(@"@media([^{]*)\{", RegexOptions.IgnoreCase)]
    private static partial Regex MediaPrelude();

    /// <summary>An @container rule's prelude: its container's name, when it names one, and its condition.</summary>
    [GeneratedRegex(@"@container\s+(?<name>(?!not\b)[a-z_-][\w-]*\s*)?(?<cond>[^{]*)\{", RegexOptions.IgnoreCase)]
    private static partial Regex ContainerPrelude();

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
