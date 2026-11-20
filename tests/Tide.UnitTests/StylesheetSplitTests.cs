using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AwesomeAssertions;

namespace Tide.UnitTests;

/// <summary>The stylesheet split into parts by area (tools/css/split-app-css.mjs) is a pure move: the parts, joined in the
/// order App.razor loads them, are the single app.css they were cut from, so the cascade is unchanged. The identity check
/// goes with the first change that moves rules between parts on purpose; the order check stays.</summary>
public partial class StylesheetSplitTests
{
    private static string RepoFile(params string[] parts) => Path.Combine([DeployScriptTests.RepoRoot(), .. parts]);

    [Fact]
    public void TheParts_JoinedInOrder_AreTheSingleFileTheyWereCutFrom()
    {
        var manifest = RepoFile("tools", "css", "split-manifest.json");
        if (!File.Exists(manifest))
        {
            // Before the split has run: the one stylesheet, and no parts beside it.
            File.Exists(Path.Combine(Stylesheets.Web, "app.css")).Should().BeTrue();
            Stylesheets.Parts().Should().BeEmpty();
            return;
        }

        using var json = JsonDocument.Parse(File.ReadAllText(manifest));
        var parts = Stylesheets.Parts();
        parts.Select(Path.GetFileName).Should().Equal(json.RootElement.GetProperty("parts").EnumerateArray().Select(p => p.GetString()));
        File.Exists(Path.Combine(Stylesheets.Web, "app.css")).Should().BeFalse("its content is in the parts now: a stale copy would be edited, and loaded by nothing");

        var joined = string.Concat(parts.Select(f => File.ReadAllText(f).ReplaceLineEndings("\n")));
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(joined)))
            .Should().Be(json.RootElement.GetProperty("sourceSha256Lf").GetString(), "the parts are app.css as it was, cut and nothing else");
    }

    [Fact]
    public void AppRazor_LoadsTheParts_InFileNameOrder_BetweenBootstrapAndPalette()
    {
        var razor = File.ReadAllText(RepoFile("src", "Tide.Web", "Components", "App.razor"));
        var hrefs = Link().Matches(razor).Select(m => m.Groups[1].Value).ToList();
        var parts = Stylesheets.Parts().Select(f => "css/" + Path.GetFileName(f)).ToList();

        if (parts.Count == 0)
        {
            hrefs.Should().Contain("app.css");
            return;
        }
        hrefs.Should().NotContain("app.css");
        hrefs.Where(h => h.StartsWith("css/", StringComparison.Ordinal)).Should().Equal(parts, "the parts load in the order of their names, which is the cascade's");
        var first = hrefs.IndexOf(parts[0]);
        hrefs.GetRange(first, parts.Count).Should().Equal(parts, "nothing loads between two parts");
        hrefs[first - 1].Should().Be("lib/bootstrap-icons/bootstrap-icons.min.css", "the parts override Bootstrap, as app.css did");
        hrefs[first + parts.Count].Should().Be("palette.css", "palette.css comes after the last part, as it came after app.css");
    }

    [GeneratedRegex("<link rel=\"stylesheet\" href=\"([^\"?]+)\\?v=[^\"]+\"")]
    private static partial Regex Link();
}
