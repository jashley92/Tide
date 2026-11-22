using System.Text.RegularExpressions;

namespace Tide.IntegrationTests;

/// <summary>The app's own stylesheet as a browser gets it: every stylesheet App.razor links (Bootstrap and the icon font
/// aside), fetched from the running app in the order they load and joined. A test that reads the styles through this
/// also proves each link resolves, whichever parts the stylesheet is in (wwwroot/css).</summary>
internal static partial class Stylesheet
{
    /// <summary>The paths App.razor loads the app's own stylesheets from, in order, without their ?v=.</summary>
    internal static IReadOnlyList<string> Paths()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Tide.sln"))) dir = dir.Parent;
        var razor = File.ReadAllText(Path.Combine(dir!.FullName, "src", "Tide.Web", "Components", "App.razor"));
        return [.. Link().Matches(razor).Select(m => "/" + m.Groups[1].Value).Where(p => !p.StartsWith("/bootstrap/", StringComparison.Ordinal) && !p.StartsWith("/lib/", StringComparison.Ordinal))];
    }

    /// <summary>The first of them, the design tokens: for a check on how a static file is served.</summary>
    internal static string First => Paths()[0];

    internal static async Task<string> StylesheetAsync(this TideApp app)
    {
        var client = app.CreateClient();
        var parts = new List<string>();
        foreach (var path in Paths()) parts.Add(await client.GetStringAsync(path)); // throws on a link that doesn't resolve
        return string.Concat(parts);
    }

    [GeneratedRegex("<link rel=\"stylesheet\" href=\"([^\"?]+)\\?v=[^\"]+\"")]
    private static partial Regex Link();
}
