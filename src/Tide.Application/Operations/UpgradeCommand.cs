namespace Tide.Application.Operations;

/// <summary>The copy of the deploy scripts Upgrade-Tide.ps1 (and Install-Tide.ps1) keeps beside the site, and the IIS
/// site (and application path under it) its site.txt says it was kept for.</summary>
public sealed record KeptDeployScripts(string Script, string? Site, string? AppPath);

/// <summary>
/// The commands Administration › System › Updates shows for upgrading this install to a release.
/// Once an install or upgrade has kept the deploy scripts beside the site (&lt;site folder&gt;-deploy),
/// it's one line: that copy of Upgrade-Tide.ps1 with -Version, which downloads the bundle and checks its SHA-256 itself.
/// Before that, it's the bundle's own script run on the downloaded zip.
/// </summary>
public static class UpgradeCommand
{
    public const string ScriptName = "Upgrade-Tide.ps1";

    /// <summary>The kept deploy scripts beside the site whose folder is <paramref name="contentRoot"/>, or null when
    /// there are none (or they can't be read).</summary>
    public static KeptDeployScripts? FindKept(string contentRoot)
    {
        try
        {
            var root = contentRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var parent = Path.GetDirectoryName(root);
            if (string.IsNullOrEmpty(parent)) return null;
            var folder = Path.Combine(parent, Path.GetFileName(root) + "-deploy");
            var script = Path.Combine(folder, ScriptName);
            if (!File.Exists(script)) return null;
            var siteFile = Path.Combine(folder, "site.txt");
            var fields = File.Exists(siteFile)
                ? File.ReadLines(siteFile).Take(10).Select(l => l.Split(':', 2)).Where(p => p.Length == 2)
                    .GroupBy(p => p[0].Trim(), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First()[1].Trim(), StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            return new KeptDeployScripts(script, Value(fields, "Site"), Value(fields, "AppPath"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>What to run on the web server, in an elevated PowerShell, to upgrade to <paramref name="tag"/>.</summary>
    public static string For(string tag, KeptDeployScripts? kept)
    {
        if (kept is not null)
        {
            var site = kept.Site is { } s ? $" -SiteName '{Quote(s)}'" : "";
            var app = kept.Site is not null && kept.AppPath is { } a ? $" -AppPath '{Quote(a)}'" : "";
            return $"""
                # In an elevated PowerShell on the web server (it downloads the release and checks its SHA-256 itself):
                & '{Quote(kept.Script)}'{site}{app} -Version {tag}
                """;
        }
        return $"""
            # In an elevated PowerShell, in the folder you downloaded tide-{tag}.zip to:
            Unblock-File .\tide-{tag}.zip
            Expand-Archive .\tide-{tag}.zip -DestinationPath .
            cd .\tide-{tag}\deploy
            .\{ScriptName} -BundleZip ..\..\tide-{tag}.zip
            """;
    }

    private static string? Value(Dictionary<string, string> fields, string key) =>
        fields.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

    // As the answers file quotes: PowerShell ends a '...' string at a typographic quote too (‘ ’ ‚ ‛), so a site folder
    // or name with one ("Robin’s site") broke the command, the rest of it run as code. Each is doubled, as ' is.
    private static string Quote(string s) => InstallAnswers.Escape(s);
}
