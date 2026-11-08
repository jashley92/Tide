using AwesomeAssertions;
using Tide.Application.Operations;

namespace Tide.UnitTests;

public sealed class UpgradeCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tide-upgrade-cmd-" + Guid.NewGuid().ToString("N"));
    private string Site => Path.Combine(_root, "tide");
    private string Kept => Path.Combine(_root, "tide-deploy");

    public UpgradeCommandTests() => Directory.CreateDirectory(Site);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private void KeepScripts(params string[] siteTxt)
    {
        Directory.CreateDirectory(Kept);
        File.WriteAllText(Path.Combine(Kept, UpgradeCommand.ScriptName), "# kept");
        if (siteTxt.Length > 0) File.WriteAllLines(Path.Combine(Kept, "site.txt"), siteTxt);
    }

    [Fact]
    public void WithoutKeptScripts_ItsTheBundlesOwnUpgradeScript_OnTheDownloadedZip()
    {
        UpgradeCommand.FindKept(Site).Should().BeNull();
        var cmd = UpgradeCommand.For("v0.2.0-alpha.1", null);
        cmd.Should().Contain(@"Expand-Archive .\tide-v0.2.0-alpha.1.zip")
            .And.Contain(@"cd .\tide-v0.2.0-alpha.1\deploy")
            .And.Contain(@".\Upgrade-Tide.ps1 -BundleZip ..\..\tide-v0.2.0-alpha.1.zip")
            .And.NotContain("Install-Tide.ps1");
    }

    [Fact]
    public void WithKeptScripts_ItsOneLine_WithTheVersion_AndTheSiteTheyWereKeptFor()
    {
        KeepScripts("Site: TIDE");
        var kept = UpgradeCommand.FindKept(Site + Path.DirectorySeparatorChar);
        kept.Should().Be(new KeptDeployScripts(Path.Combine(Kept, "Upgrade-Tide.ps1"), "TIDE", null));
        var cmd = UpgradeCommand.For("v0.2.0", kept);
        cmd.Should().EndWith($"& '{Path.Combine(Kept, "Upgrade-Tide.ps1")}' -SiteName 'TIDE' -Version v0.2.0");
        cmd.Should().NotContain("-BundleZip").And.NotContain("-AppPath");
    }

    [Fact]
    public void UnderAnotherSite_TheCommandNamesThatSiteAndTidesPath()
    {
        KeepScripts("Site: Case'Book", "AppPath: tide", "Other: ignored");
        var kept = UpgradeCommand.FindKept(Site);
        kept!.AppPath.Should().Be("tide");
        UpgradeCommand.For("v0.2.0", kept).Should().Contain("-SiteName 'Case''Book' -AppPath 'tide' -Version v0.2.0", "a quote is doubled inside '...'");
    }

    // PowerShell ends a '...' string at a typographic quote as well: "Robin’s site" ended the site name there, and the rest
    // of the line ran as code. Doubled, as the answers file doubles them.
    [Fact]
    public void ATypographicQuote_IsDoubledInsideTheQuotes()
    {
        KeepScripts("Site: Robin’s site");
        UpgradeCommand.For("v0.2.0", UpgradeCommand.FindKept(Site)).Should().Contain("-SiteName 'Robin’’s site' -Version v0.2.0");
    }

    [Fact]
    public void WithoutSiteTxt_TheScriptsDefaultSiteIsUsed()
    {
        KeepScripts();
        var kept = UpgradeCommand.FindKept(Site);
        kept.Should().Be(new KeptDeployScripts(Path.Combine(Kept, "Upgrade-Tide.ps1"), null, null));
        UpgradeCommand.For("v1.0.0", kept with { AppPath = "tide" }).Should().NotContain("-SiteName").And.NotContain("-AppPath")
            .And.Contain("' -Version v1.0.0");
    }

    [Fact]
    public void ABlankSiteLine_OrARootFolder_FindsNothingToName()
    {
        KeepScripts("Site:   ");
        UpgradeCommand.FindKept(Site)!.Site.Should().BeNull();
        UpgradeCommand.FindKept(Path.GetPathRoot(_root)!).Should().BeNull("a drive root has no folder beside it");
    }
}
