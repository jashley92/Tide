namespace Tide.UnitTests;

/// <summary>The app's own stylesheets as the browser gets them: the parts under wwwroot/css in file-name order (the order
/// App.razor loads them in), then palette.css; app.css as well on a checkout from before the split. Line endings as LF,
/// so a check that spans lines holds on a Windows checkout.</summary>
internal static class Stylesheets
{
    internal static string Web => Path.Combine(DeployScriptTests.RepoRoot(), "src", "Tide.Web", "wwwroot");

    /// <summary>The parts under wwwroot/css, in the order they load in.</summary>
    internal static IReadOnlyList<string> Parts()
    {
        var dir = Path.Combine(Web, "css");
        return Directory.Exists(dir) ? [.. Directory.GetFiles(dir, "*.css").Order(StringComparer.Ordinal)] : [];
    }

    internal static string ReadAll()
    {
        var single = Path.Combine(Web, "app.css");
        string[] files = [.. File.Exists(single) ? [single] : Array.Empty<string>(), .. Parts(), Path.Combine(Web, "palette.css")];
        return string.Concat(files.Select(f => File.ReadAllText(f).ReplaceLineEndings("\n")));
    }
}
