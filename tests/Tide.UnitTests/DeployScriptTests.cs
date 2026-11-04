using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Tide.Application.Operations;

namespace Tide.UnitTests;

/// <summary>
/// Checks on the deploy scripts that no build or test would otherwise catch: their text (ASCII, dates,
/// the carve-outs, the order the upgrade does things in) and, by running PowerShell, that every script parses, the
/// parameters the docs and the Updates page rely on, and the pure helpers the upgrade decides with.
/// </summary>
public partial class DeployScriptTests
{
    internal static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Tide.sln"))) return dir.FullName;
        throw new InvalidOperationException("Couldn't find the repo root (Tide.sln).");
    }

    internal static string Deploy => Path.Combine(RepoRoot(), "deploy");

    // With line endings as LF, so a check that spans lines holds on a Windows checkout too (core.autocrlf writes CRLF).
    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([RepoRoot(), .. parts])).ReplaceLineEndings("\n");

    [Fact]
    public void The_deploy_scripts_are_plain_ascii()
    {
        // Windows PowerShell 5.1 reads a BOM-less script in the ANSI code page, so a non-ASCII character (an em dash) turns
        // into mojibake, and in a string changes what the script does. A leading UTF-8 BOM is allowed.
        var offenders = Directory.EnumerateFiles(Deploy, "*.ps*1", SearchOption.AllDirectories)
            .Select(f => (File: Path.GetRelativePath(Deploy, f), Bytes: File.ReadAllBytes(f)))
            .Select(x => (x.File, Bytes: x.Bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]) ? x.Bytes[3..] : x.Bytes))
            .Where(x => x.Bytes.Any(b => b > 127))
            .Select(x => x.File);
        offenders.Should().BeEmpty();
    }

    [Fact]
    public void No_deploy_script_holds_a_stray_control_character()
    {
        // A lost backslash escape ("files\retired" written by a generator) puts a carriage return mid-line. Tabs and line
        // endings only.
        Directory.EnumerateFiles(Deploy, "*.ps*1", SearchOption.AllDirectories)
            .SelectMany(f => File.ReadAllText(f).Split('\n').Select(l => l.TrimEnd('\r'))
                .Select((line, i) => (File: Path.GetRelativePath(Deploy, f), Line: i + 1, Text: line)))
            .Where(x => x.Text.Any(c => c < ' ' && c != '\t'))
            .Select(x => $"{x.File}:{x.Line}")
            .Should().BeEmpty();
    }

    [Fact]
    public void AllowedHosts_gets_its_own_value_so_TIDE_under_another_site_answers_at_each_of_its_host_names()
    {
        // The email From uses the one host name; AllowedHosts, under another site, every name on the site's bindings.
        Read("deploy", "appsettings.Production.template.json").Should().Contain("\"AllowedHosts\": \"__ALLOWED_HOSTS__\"");
        var install = Read("deploy", "Install-Tide.ps1");
        install.Should().Contain("'__ALLOWED_HOSTS__'       = ConvertTo-JsonText ($allowedHosts -join ';')");
        install.Should().Contain("foreach ($h in @($parentBindings |");
    }

    [Fact]
    public void Every_sqlcmd_that_runs_a_script_file_turns_QUOTED_IDENTIFIER_on()
    {
        // sqlcmd defaults QUOTED_IDENTIFIER off, and the schema's filtered indexes fail without it.
        var calls = Directory.EnumerateFiles(Deploy, "*.ps1", SearchOption.AllDirectories)
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (File: Path.GetFileName(f), Line: i + 1, Text: line)))
            .Where(l => SqlcmdWithInputFile().IsMatch(l.Text))
            .ToList();
        calls.Should().NotBeEmpty();
        calls.Where(c => !UpperIFlag().IsMatch(c.Text)).Select(c => $"{c.File}:{c.Line}").Should().BeEmpty();
    }

    [Fact]
    public void Dates_are_formatted_with_the_invariant_culture()
    {
        // Get-Date -Format and ToString('yyyy...') use the server's culture, whose calendar can be Thai Buddhist or Hijri,
        // so backup folders and .bak names would sort out of order.
        var offenders = Directory.EnumerateFiles(Deploy, "*.ps*1", SearchOption.AllDirectories)
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (File: Path.GetRelativePath(Deploy, f), Line: i + 1, Text: line)))
            .Where(l => CultureDate().IsMatch(l.Text) || ToStringWithDatePattern().IsMatch(l.Text))
            .Select(l => $"{l.File}:{l.Line}: {l.Text.Trim()}");
        offenders.Should().BeEmpty(@"use Get-TideStamp (lib\TideStamp.ps1)");
        Read("deploy", "lib", "TideStamp.ps1").Should().Contain("InvariantCulture").And.Contain("ParseExact");
    }

    [Fact]
    public void The_shipped_web_config_lets_health_reach_the_app()
    {
        // The SDK's default web.config wraps the handler in <location inheritInChildApplications="false">, which IIS
        // doesn't carry into the <location path="TIDE/health"> carve-out, so /health would go to IIS's static handler.
        var withoutComments = XmlComment().Replace(Read("src", "Tide.Web", "web.config"), "");
        withoutComments.Should().Contain("<system.webServer>").And.NotContain("<location").And.NotContain("inheritInChildApplications");
    }

    [Fact]
    public void Every_anonymous_endpoint_the_app_maps_is_carved_out_by_the_install_and_the_upgrade()
    {
        // IIS answers 401 before the app runs on a path that isn't carved out of Windows authentication, so an endpoint the
        // app maps anonymous only works if the installer opens its path, and on a site upgraded from before it was added
        // only if the upgrade opens it too.
        var program = Read("src", "Tide.Web", "Program.cs");
        var maps = MappedRoute().Matches(program).ToList();
        var anonymous = maps
            .Select((m, i) => (Path: m.Groups["path"].Value, Body: program[m.Index..(i + 1 < maps.Count ? maps[i + 1].Index : program.Length)]))
            .Where(x => x.Body.Contains(".AllowAnonymous()", StringComparison.Ordinal))
            .Select(x => x.Path)
            .ToList();
        anonymous.Should().Contain(["/health", "/health/live"]);

        var lib = Read("deploy", "lib", "TideIis.ps1");
        var carved = AnonymousPaths().Match(lib);
        carved.Success.Should().BeTrue(@"lib\TideIis.ps1 lists the carved-out paths in Get-TideAnonymousPaths");
        var paths = QuotedItem().Matches(carved.Groups[1].Value).Select(m => "/" + m.Groups[1].Value).ToList();
        anonymous.Where(a => !paths.Any(p => a == p || a.StartsWith(p + "/", StringComparison.Ordinal)))
            .Should().BeEmpty("every anonymous endpoint needs its path in Get-TideAnonymousPaths");

        foreach (var script in new[] { "Install-Tide.ps1", "Upgrade-Tide.ps1" })
        {
            var text = Read("deploy", script);
            text.Should().Contain(@"lib\TideIis.ps1", script + " loads the shared module");
            text.Should().MatchRegex(@"Sync-TideAnonymousCarveOuts\s+-SiteName", script + " applies the shared carve-outs");
            text.Should().NotMatchRegex(@"\$anonymousPaths\s*=", script + " has no list of its own");
        }
    }

    [Fact]
    public void The_upgrade_checks_a_bundle_against_its_release_and_hands_over_before_expanding_it()
    {
        var script = Read("deploy", "Upgrade-Tide.ps1");
        var check = script.IndexOf("/releases/download/$tag/tide-$tag.zip.sha256", StringComparison.Ordinal);
        var handover = script.IndexOf("& $bundleScript @pass", StringComparison.Ordinal);
        var expand = script.IndexOf("ZipFile]::ExtractToDirectory($zipPath, $staging)", StringComparison.Ordinal);
        check.Should().BeGreaterThan(-1);
        handover.Should().BeGreaterThan(check);
        expand.Should().BeGreaterThan(handover, "the bundle's own script runs the upgrade, so the app isn't expanded twice");
        script.Should().Contain("ReleaseHashChecked", "the handover tells the bundle's script the check already ran");
        script.Should().NotContain("Expand-Archive -Path $BundleZip", "Expand-Archive is many times slower in Windows PowerShell 5.1");
    }

    [Fact]
    public void The_upgrade_backs_up_before_it_changes_anything_and_stops_at_the_confirmation_under_WhatIf()
    {
        var script = Read("deploy", "Upgrade-Tide.ps1");
        int At(string s)
        {
            var i = script.IndexOf(s, StringComparison.Ordinal);
            i.Should().BeGreaterThan(-1, "the script has '{0}'", s);
            return i;
        }
        var migrationCheck = At("Compare-TideMigrations -Expected");
        var whatIf = At("What if: Performing the operation");
        var dbBackup = At("BACKUP DATABASE");
        var snapshot = At("& robocopy $SitePath $rollbackDir");
        var settings = At("Set-TideJsonValue $liveCfg");
        var carveOut = At("Sync-TideAnonymousCarveOuts -SiteName $iisPath -Appcmd $appcmd)");
        var stop = At("Stop-TideAppPool -Name $AppPoolName\n");
        var copy = At("& robocopy $appSrc $SitePath");
        var stale = At("Remove-TideStaleFiles -From $appSrc -To $SitePath");
        var warm = At("Get-TideProbeVerdict $probe -Health");

        migrationCheck.Should().BeLessThan(whatIf);
        whatIf.Should().BeLessThan(dbBackup);
        new[] { dbBackup, snapshot, settings, carveOut, stop, copy, stale, warm }.Should().BeInAscendingOrder();
        script.Should().Contain("/XF appsettings.Production.json VERSION.txt /XD logs App_Data", "the deploy keeps the settings, logs and App_Data");
        script.Should().Contain("COPY_ONLY", "the pre-upgrade backup doesn't reset the scheduled backups' chain");
    }

    [Fact]
    public void The_release_bundle_carries_the_deploy_folder_and_the_manifest_the_preflight_reads()
    {
        var release = Read(".github", "workflows", "release.yml");
        release.Should().Contain("cp -r deploy \"$root/deploy\"", "every deploy script, the upgrade's included, ships in the bundle");
        release.Should().Contain("\"$root/migrations-sqlserver.txt\"");
        release.Should().Contain("tide-${version}.zip.sha256");
        var script = Read("deploy", "Upgrade-Tide.ps1");
        script.Should().Contain("-Filter 'migrations-sqlserver.txt'").And.Contain("tide-$Version.zip").And.Contain(@"src\Tide.Migrations.SqlServer\Migrations");
    }

    [GeneratedRegex(@"function\s+Get-TideServerSettingPaths\s*\{\s*return\s+@\(([^)]*)\)", RegexOptions.None, 1000)]
    private static partial Regex ServerSettingPaths();

    [Fact]
    public void Every_server_setting_the_upgrade_adds_is_in_appsettings_json_and_set_on_the_server_only()
    {
        // The upgrade adds each at the release's appsettings.json value, so it must be there; and one an administrator
        // changes in the app (saved in the database, over the server's value) mustn't be pinned in the file.
        var listed = ServerSettingPaths().Match(Read("deploy", "lib", "TideSettings.ps1"));
        listed.Success.Should().BeTrue();
        var paths = QuotedItem().Matches(listed.Groups[1].Value).Select(m => m.Groups[1].Value).ToList();
        paths.Should().NotBeEmpty();
        using var defaults = JsonDocument.Parse(Read("src", "Tide.Web", "appsettings.json"));
        foreach (var path in paths)
        {
            var node = defaults.RootElement;
            foreach (var part in path.Split(':'))
                node.TryGetProperty(part, out node).Should().BeTrue("{0} is in src/Tide.Web/appsettings.json", path);
            SettingsCatalog.InApp.Where(s => s.Key == path || s.Key.StartsWith(path + ":", StringComparison.OrdinalIgnoreCase))
                .Should().BeEmpty("{0} is set on the server only", path);
        }
    }

    [GeneratedRegex(@"app\.Map(Get|Post|HealthChecks)\(\s*""(?<path>/[^""]*)""", RegexOptions.None, 1000)]
    private static partial Regex MappedRoute();

    [GeneratedRegex(@"function\s+Get-TideAnonymousPaths\s*\{\s*return\s+@\(([^)]*)\)", RegexOptions.None, 1000)]
    private static partial Regex AnonymousPaths();

    [GeneratedRegex("<!--.*?-->", RegexOptions.Singleline, 1000)]
    private static partial Regex XmlComment();

    [GeneratedRegex("'([^']+)'", RegexOptions.None, 1000)]
    private static partial Regex QuotedItem();

    [GeneratedRegex(@"sqlcmd(\.exe)?\b.*\s-i\s", RegexOptions.None, 1000)]
    private static partial Regex SqlcmdWithInputFile();

    [GeneratedRegex(@"\s-I(\s|$)", RegexOptions.None, 1000)]
    private static partial Regex UpperIFlag();

    [GeneratedRegex(@"Get-Date\b[^|;]*-Format\b|\[datetime\]::Parse\(|-as\s+\[datetime\]|\.ToShort(Date|Time)String\(|\.ToLong(Date|Time)String\(", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex CultureDate();

    [GeneratedRegex(@"\.ToString\(\s*'[^']*(yyyy|HH|dd|MM)[^']*'\s*\)", RegexOptions.None, 1000)]
    private static partial Regex ToStringWithDatePattern();
}

/// <summary>
/// Runs PowerShell (pwsh, else Windows PowerShell) over the deploy scripts: every one parses, the parameters, and the
/// helpers in deploy\lib the upgrade decides with. Nothing here touches IIS, SQL Server or the network.
/// </summary>
public class DeployScriptPowerShellTests
{
    private static readonly string Lib = Path.Combine(DeployScriptTests.Deploy, "lib");

    /// <summary>Runs <paramref name="script"/> with the deploy\lib helpers loaded and returns what it wrote, as JSON.</summary>
    private static JsonElement Run(string script, string? shell = null)
    {
        var file = Path.Combine(Path.GetTempPath(), "tide-deploy-test-" + Guid.NewGuid().ToString("N") + ".ps1");
        var prelude = string.Join("\n", new[] { "TideStamp", "TideJson", "TidePath", "TideSql", "TideSettings", "TideUpgrade", "TideBackup" }
            .Select(n => $". '{Path.Combine(Lib, n + ".ps1").Replace("'", "''")}'"));
        File.WriteAllText(file, "$ErrorActionPreference = 'Stop'\n$deploy = '" + DeployScriptTests.Deploy.Replace("'", "''") + "'\n"
            + prelude + "\n" + script);
        try
        {
            var psi = new ProcessStartInfo(shell ?? Shell(), ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", file])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var p = Process.Start(psi)!;
            var stderr = p.StandardError.ReadToEndAsync();
            var stdout = p.StandardOutput.ReadToEnd();
            p.WaitForExit(120_000).Should().BeTrue("PowerShell finishes");
            p.ExitCode.Should().Be(0, "the script ran: {0}", stderr.Result);
            return JsonDocument.Parse(stdout).RootElement.Clone();
        }
        finally { File.Delete(file); }
    }

    private static string? _shell;

    private static string Shell() => _shell ??= Shells().FirstOrDefault()
        ?? throw new InvalidOperationException("These tests need PowerShell: pwsh (PowerShell 7) or, on Windows, powershell.exe.");

    /// <summary>
    /// Every PowerShell here: pwsh, and on Windows also powershell.exe (5.1), which the scheduled tasks run and whose
    /// ConvertFrom-Json and argument quoting differ. A check that depends on those runs on each.
    /// </summary>
    private static IEnumerable<string> Shells()
    {
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (var name in OperatingSystem.IsWindows() ? new[] { "pwsh.exe", "powershell.exe" } : ["pwsh"])
        {
            var hit = dirs.Select(d => Path.Combine(d, name)).FirstOrDefault(File.Exists);
            if (hit is not null) yield return hit;
        }
    }

    [Fact]
    public void Every_deploy_script_parses()
    {
        var result = Run("""
            $out = foreach ($f in Get-ChildItem -Path $deploy -Recurse -Filter *.ps1) {
                $errors = $null
                [void][System.Management.Automation.Language.Parser]::ParseFile($f.FullName, [ref]$null, [ref]$errors)
                # A script's help is read only when a blank line parts it from the #Requires lines above it.
                $help = if ($f.DirectoryName -eq $deploy) { -not "$((Get-Help $f.FullName).Synopsis)".Trim().StartsWith($f.Name) } else { $true }
                [pscustomobject]@{ File = $f.Name; Help = $help; Errors = @($errors | ForEach-Object { "$($_.Extent.StartLineNumber): $($_.Message)" }) }
            }
            ConvertTo-Json -Depth 4 -Compress -InputObject @($out)
            """);
        var files = result.EnumerateArray().ToList();
        files.Select(f => f.GetProperty("File").GetString()).Should().Contain(
            ["Upgrade-Tide.ps1", "Backup-Tide.ps1", "Test-TideRestore.ps1", "Install-Tide.ps1", "TideSettings.ps1", "TideUpgrade.ps1"]);
        files.Where(f => f.GetProperty("Errors").GetArrayLength() > 0)
            .Select(f => f.GetProperty("File").GetString() + ": " + string.Join("; ", f.GetProperty("Errors").EnumerateArray().Select(e => e.GetString())))
            .Should().BeEmpty();
        files.Where(f => !f.GetProperty("Help").GetBoolean()).Select(f => f.GetProperty("File").GetString())
            .Should().BeEmpty("Get-Help shows every deploy script's comment-based help");
    }

    [Fact]
    public void The_scripts_take_the_parameters_the_docs_and_the_Updates_page_use()
    {
        var result = Run("""
            $out = @{}
            foreach ($name in 'Upgrade-Tide.ps1', 'Backup-Tide.ps1', 'Test-TideRestore.ps1') {
                $ast = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $deploy $name), [ref]$null, [ref]$null)
                $params = @($ast.ParamBlock.Parameters | ForEach-Object {
                    [pscustomobject]@{
                        Name = $_.Name.VariablePath.UserPath
                        Default = $(if ($_.DefaultValue) { $_.DefaultValue.Extent.Text } else { $null })
                        Mandatory = [bool]($_.Attributes | Where-Object { $_.TypeName.Name -eq 'Parameter' -and $_.NamedArguments.ArgumentName -contains 'Mandatory' })
                    } })
                $binding = $ast.ParamBlock.Attributes | Where-Object { $_.TypeName.Name -eq 'CmdletBinding' }
                $out[$name] = [pscustomobject]@{
                    Params = $params
                    ShouldProcess = [bool]($binding.NamedArguments | Where-Object { $_.ArgumentName -eq 'SupportsShouldProcess' })
                    Admin = [bool]$ast.ScriptRequirements.IsElevationRequired
                    Help = [bool]((Get-Help (Join-Path $deploy $name) -Full).description) -and [bool]((Get-Help (Join-Path $deploy $name)).examples)
                }
            }
            ConvertTo-Json -Depth 5 -Compress -InputObject $out
            """);
        var upgrade = result.GetProperty("Upgrade-Tide.ps1");
        var names = upgrade.GetProperty("Params").EnumerateArray().Select(p => p.GetProperty("Name").GetString()).ToList();
        names.Should().Contain(["BundleZip", "Version", "Repository", "Proxy", "AppSource", "Build", "SiteName", "AppPath", "SitePath",
            "AppPoolName", "SqlInstance", "DbName", "HealthUrl", "BackupRoot", "DataRoot", "SkipDbBackup", "Force", "ReleaseHashChecked"]);
        string? Default(JsonElement s, string n) => s.GetProperty("Params").EnumerateArray().Single(p => p.GetProperty("Name").GetString() == n).GetProperty("Default").GetString();
        Default(upgrade, "Repository").Should().Be("'jashley92/Tide'");
        Default(upgrade, "SiteName").Should().Be("'TIDE'", "Install-Tide.ps1's default site");
        upgrade.GetProperty("ShouldProcess").GetBoolean().Should().BeTrue("-WhatIf runs the checks and stops");
        upgrade.GetProperty("Admin").GetBoolean().Should().BeTrue();
        upgrade.GetProperty("Help").GetBoolean().Should().BeTrue();

        var backup = result.GetProperty("Backup-Tide.ps1");
        backup.GetProperty("Params").EnumerateArray().Single(p => p.GetProperty("Name").GetString() == "Destination").GetProperty("Mandatory").GetBoolean().Should().BeTrue();
        backup.GetProperty("Params").EnumerateArray().Select(p => p.GetProperty("Name").GetString()).Should().Contain(["SitePath", "Log", "Register", "CopyOnly", "Keep"]);
        result.GetProperty("Test-TideRestore.ps1").GetProperty("Params").EnumerateArray().Select(p => p.GetProperty("Name").GetString())
            .Should().Contain(["Destination", "BackupSet", "ScratchDatabase", "Register"]);
    }

    [Fact]
    public void The_migration_check_stops_on_a_mismatch_and_on_a_DbaApplies_database_that_lacks_a_migration()
    {
        var r = Run("""
            $e = @('20260101000000_Initial', '20260201000000_Next')
            $cases = [ordered]@{
                current    = Compare-TideMigrations -Expected $e -Applied $e -HasAppSchema $true
                pending    = Compare-TideMigrations -Expected $e -Applied @('20260101000000_Initial') -HasAppSchema $true
                fresh      = Compare-TideMigrations -Expected $e -Applied @() -HasAppSchema $false
                dbaPending = Compare-TideMigrations -Expected $e -Applied @('20260101000000_Initial') -HasAppSchema $true -DbaApplies $true
                dbaCurrent = Compare-TideMigrations -Expected $e -Applied $e -HasAppSchema $true -DbaApplies $true
                newer      = Compare-TideMigrations -Expected $e -Applied ($e + '20260301000000_Later') -HasAppSchema $true
                noHistory  = Compare-TideMigrations -Expected $e -Applied @() -HasAppSchema $true
                diverged   = Compare-TideMigrations -Expected $e -Applied @('20260201000000_Next') -HasAppSchema $true
            }
            ConvertTo-Json -Depth 4 -Compress -InputObject $cases
            """);
        (string Verdict, bool Ok, string[] Pending, string Message) Case(string name)
        {
            var c = r.GetProperty(name);
            return (c.GetProperty("Verdict").GetString()!, c.GetProperty("Ok").GetBoolean(),
                c.GetProperty("Pending").ValueKind == JsonValueKind.Array ? c.GetProperty("Pending").EnumerateArray().Select(x => x.GetString()!).ToArray() : [c.GetProperty("Pending").GetString()!],
                c.GetProperty("Message").GetString()!);
        }
        Case("current").Should().Match<(string Verdict, bool Ok, string[] Pending, string Message)>(c => c.Verdict == "ok" && c.Ok);
        Case("pending").Verdict.Should().Be("pending");
        Case("pending").Ok.Should().BeTrue("the app applies it when it starts");
        Case("pending").Pending.Should().Equal("20260201000000_Next");
        Case("fresh").Ok.Should().BeTrue();
        Case("dbaPending").Should().Match<(string Verdict, bool Ok, string[] Pending, string Message)>(c => c.Verdict == "dba-pending" && !c.Ok);
        Case("dbaPending").Message.Should().Contain(@"deploy\sql").And.Contain("20260201000000_Next").And.Contain("Nothing has been changed");
        Case("dbaCurrent").Ok.Should().BeTrue();
        Case("newer").Should().Match<(string Verdict, bool Ok, string[] Pending, string Message)>(c => c.Verdict == "unknown" && !c.Ok);
        Case("noHistory").Should().Match<(string Verdict, bool Ok, string[] Pending, string Message)>(c => c.Verdict == "no-history" && !c.Ok);
        Case("diverged").Should().Match<(string Verdict, bool Ok, string[] Pending, string Message)>(c => c.Verdict == "baseline" && !c.Ok);
    }

    [Fact]
    public void A_bundle_is_checked_against_the_sha256_its_release_states()
    {
        var r = Run("""
            $f = [IO.Path]::GetTempFileName()
            try {
                Set-Content -Path $f -Value 'a bundle' -Encoding ascii -NoNewline
                $hash = (Get-FileHash -LiteralPath $f -Algorithm SHA256).Hash
                $out = [ordered]@{
                    parsed    = ConvertFrom-TideSha256File ("$($hash.ToLowerInvariant())  tide-v0.2.0.zip`n")
                    bare      = ConvertFrom-TideSha256File $hash
                    junk      = ConvertFrom-TideSha256File '<html>Not Found</html>'
                    empty     = ConvertFrom-TideSha256File ''
                    matches   = Test-TideFileHash $f $hash.ToLowerInvariant()
                    differs   = Test-TideFileHash $f ('0' * 64)
                    malformed = Test-TideFileHash $f 'abc'
                    hash      = $hash
                }
            } finally { Remove-Item $f }
            ConvertTo-Json -Compress -InputObject $out
            """);
        var hash = r.GetProperty("hash").GetString();
        r.GetProperty("parsed").GetString().Should().Be(hash);
        r.GetProperty("bare").GetString().Should().Be(hash);
        r.GetProperty("junk").ValueKind.Should().Be(JsonValueKind.Null);
        r.GetProperty("empty").ValueKind.Should().Be(JsonValueKind.Null);
        r.GetProperty("matches").GetBoolean().Should().BeTrue();
        r.GetProperty("differs").GetBoolean().Should().BeFalse();
        r.GetProperty("malformed").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public void Release_tags_and_VERSION_txt_are_read_as_the_Release_workflow_writes_them()
    {
        var r = Run("""
            $out = [ordered]@{
                tags = @('v0.1.0', 'v0.1.0-alpha.2', 'v1.2.3-rc.1', 'v1.2', '1.2.3', 'v1.2.3-preview.1', 'v1.2.3; rm') | ForEach-Object { Test-TideReleaseTag $_ }
                version = Get-TideVersionFromText "Version: v0.1.0-alpha.2`nCommit:  abc`nBuilt:   2026-10-31T00:00:00Z"
                notATag = Get-TideVersionFromText "Version: development build"
                recorded = Get-TideRecordedVersion @('Version: v0.1.0', 'Previous: v0.0.9')
                unknown = Get-TideRecordedVersion @('nothing')
                record = New-TideVersionRecord -Target 'v0.2.0' -Previous 'v0.1.0' -When '2026-10-31 10:00 +00:00' -By 'CONTOSO\admin'
                firstRecord = New-TideVersionRecord -Target 'v0.2.0' -Previous '(unknown)' -When 'w' -By 'b'
            }
            ConvertTo-Json -Compress -InputObject $out
            """);
        r.GetProperty("tags").EnumerateArray().Select(x => x.GetBoolean()).Should().Equal(true, true, true, false, false, false, false);
        r.GetProperty("version").GetString().Should().Be("v0.1.0-alpha.2");
        r.GetProperty("notATag").ValueKind.Should().Be(JsonValueKind.Null);
        r.GetProperty("recorded").GetString().Should().Be("v0.1.0");
        r.GetProperty("unknown").GetString().Should().Be("(unknown)");
        r.GetProperty("record").EnumerateArray().Select(x => x.GetString()).Should().Equal(
            "Version: v0.2.0", "Previous: v0.1.0", "Upgraded: 2026-10-31 10:00 +00:00", @"By: CONTOSO\admin");
        r.GetProperty("firstRecord").EnumerateArray().Select(x => x.GetString()).Should().NotContain(l => l!.StartsWith("Previous", StringComparison.Ordinal));
    }

    [Fact]
    public void The_warm_up_reads_TIDEs_own_answer_and_probes_the_address_the_binding_answers_at()
    {
        var r = Run("""
            $v = { param($s, $b, [switch] $h) if ($h) { Get-TideProbeVerdict @{ Status = $s; Body = $b } -Health } else { Get-TideProbeVerdict @{ Status = $s; Body = $b } } }
            $out = [ordered]@{
                healthy   = & $v 200 'Healthy' -h
                degraded  = & $v 200 'Degraded' -h
                unhealthy = & $v 503 'Unhealthy' -h
                iis404    = & $v 404 '<html><title>404</title></html>' -h
                otherPage = & $v 200 '<html>another site</html>' -h
                auth      = & $v 401 '' -h
                crashed   = & $v 500 'HTTP Error 500.30 - ASP.NET Core app failed to start' -h
                starting  = & $v 500 'HTTP Error 500.37 - ASP.NET Core app failed to start within startup time limit' -h
                limited   = & $v 429 '' -h
                none      = & $v $null '' -h
                rootPage  = & $v 200 '<html>TIDE</html>'
                rootRedir = & $v 302 ''
                rootGone  = & $v 404 ''
                urls = @(
                    (Get-TideBindingUrl '*:443:tide.contoso.com' 'https' 443 $null),
                    (Get-TideBindingUrl '*:8443:tide.contoso.com' 'https' 443 $null),
                    (Get-TideBindingUrl '10.0.0.5:80:' 'http' 80 $null),
                    (Get-TideBindingUrl '[::1]:443:' 'https' 443 $null),
                    (Get-TideBindingUrl '*:80:' 'http' 80 $null),
                    (Get-TideBindingUrl '*:443:intranet.contoso.com' 'https' 443 '/tide/'))
                hosts = @(
                    (Test-TideHostAllowed 'localhost' @()),
                    (Test-TideHostAllowed 'localhost' @('*')),
                    (Test-TideHostAllowed 'TIDE.contoso.com' @('tide.contoso.com')),
                    (Test-TideHostAllowed 'a.corp.example' @('*.corp.example')),
                    (Test-TideHostAllowed 'localhost' @('tide.contoso.com')))
            }
            ConvertTo-Json -Compress -InputObject $out
            """);
        string V(string n) => r.GetProperty(n).GetString()!;
        V("healthy").Should().Be("started");
        V("degraded").Should().Be("started");
        V("unhealthy").Should().Be("unhealthy");
        V("iis404").Should().Be("other");
        V("otherPage").Should().Be("other", "only TIDE's status word counts");
        V("auth").Should().Be("auth");
        V("crashed").Should().Be("crashed");
        V("starting").Should().Be("none", "500.37 is a long migration still running");
        V("limited").Should().Be("none");
        V("none").Should().Be("none");
        V("rootPage").Should().Be("started");
        V("rootRedir").Should().Be("started");
        V("rootGone").Should().Be("other");
        r.GetProperty("urls").EnumerateArray().Select(x => x.GetString()).Should().Equal(
            "https://tide.contoso.com/", "https://tide.contoso.com:8443/", "http://10.0.0.5/", "https://[::1]/", "http://localhost/",
            "https://intranet.contoso.com/tide/");
        r.GetProperty("hosts").EnumerateArray().Select(x => x.GetBoolean()).Should().Equal(true, true, true, true, false);
    }

    [Fact]
    public void Missing_server_settings_are_added_at_the_releases_values_and_existing_ones_are_never_changed()
    {
        var r = Run("""
            $defaults = Get-Content (Join-Path $deploy '..\src\Tide.Web\appsettings.json') -Raw | ConvertFrom-Json
            $live = [pscustomobject]@{
                Email = [pscustomobject]@{ Enabled = $true; SmtpHost = 'relay.contoso.com'; SmtpTimeoutSeconds = 90 }
                Intake = [pscustomobject]@{ Enabled = $true }
            }
            $missing = @(Find-TideMissingServerSettings $live $defaults)
            Add-TideServerSettings $live $defaults $null $missing
            $again = @(Find-TideMissingServerSettings $live $defaults)
            $bad = [pscustomobject]@{ Cve = 'oops' }
            $thrown = $null
            try { Add-TideServerSettings $bad $defaults $null @(Find-TideMissingServerSettings $bad $defaults) } catch { $thrown = $_.Exception.Message }
            $out = [ordered]@{
                missing = $missing
                again = $again.Count
                smtpHost = $live.Email.SmtpHost
                timeout = $live.Email.SmtpTimeoutSeconds
                intakeEnabled = $live.Intake.Enabled
                intakeProxy = $live.Intake.Proxy
                cveKev = $live.Cve.KevUrl
                cveEnabled = Test-TideJsonPath $live 'Cve:Enabled'
                cveNote = [bool]$live.Cve.'//'
                nvdKey = Test-TideJsonPath $live 'Cve:NvdApiKey'
                password = Test-TideJsonPath $live 'Email:SmtpPassword'
                pickup = Test-TideJsonPath $live 'Email:PickupDirectory'
                updateCheck = Test-TideJsonPath $live 'Updates:CheckEnabled'
                repository = $live.Updates.Repository
                recipients = Test-TideJsonPath $live 'Notifications:BriefNotReady:Recipients'
                thrown = $thrown
            }
            ConvertTo-Json -Depth 4 -Compress -InputObject $out
            """);
        var missing = r.GetProperty("missing").EnumerateArray().Select(x => x.GetString()).ToList();
        missing.Should().Contain(["Intake:Proxy", "Cve:KevUrl", "Email:SmtpUsername", "Updates:Repository"]);
        missing.Should().NotContain(["Cve:Enabled", "Notifications:Digest:Day", "Updates:CheckIntervalHours"], "they're changed in the app");
        missing.Should().NotContain(["Email:SmtpHost", "Email:SmtpTimeoutSeconds", "Intake:Enabled"], "they're set already");
        r.GetProperty("again").GetInt32().Should().Be(0, "a second run adds nothing");
        r.GetProperty("smtpHost").GetString().Should().Be("relay.contoso.com");
        r.GetProperty("timeout").GetInt32().Should().Be(90, "an existing setting is never changed");
        r.GetProperty("intakeEnabled").GetBoolean().Should().BeTrue();
        r.GetProperty("intakeProxy").GetString().Should().Be("");
        r.GetProperty("cveKev").GetString().Should().StartWith("https://www.cisa.gov/", "the value the app already uses without it");
        r.GetProperty("cveEnabled").GetBoolean().Should().BeFalse("enrichment is turned on in the app");
        r.GetProperty("cveNote").GetBoolean().Should().BeTrue("a new section carries its explanation");
        r.GetProperty("nvdKey").GetBoolean().Should().BeFalse("secrets go in environment variables, never the file");
        r.GetProperty("password").GetBoolean().Should().BeFalse();
        r.GetProperty("pickup").GetBoolean().Should().BeFalse();
        r.GetProperty("updateCheck").GetBoolean().Should().BeFalse("the update check is set in the app");
        r.GetProperty("repository").GetString().Should().Be("jashley92/Tide");
        r.GetProperty("recipients").GetBoolean().Should().BeFalse("the reminders are set in the app");
        r.GetProperty("thrown").GetString().Should().Contain("Cve isn't a section");
    }

    [Fact]
    public void Missing_data_folder_settings_are_found_from_the_template_and_placed_under_the_installs_data_root()
    {
        var r = Run("""
            $text = (Get-Content (Join-Path $deploy 'appsettings.Production.template.json') -Raw) -replace '\[\s*__GROUP_[A-Z]+__\s*\]', '[]' -replace '__APPLY_MIGRATIONS__', 'true'
            $wanted = @(Get-TideDataRootSettings ($text | ConvertFrom-Json))
            $bs = [char]92
            $live = [pscustomobject]@{ Files = [pscustomobject]@{ RootPath = "E:${bs}TideData${bs}files" } }
            $before = @(Find-TideMissingDataSettings $live $wanted | ForEach-Object { $_.Path })
            $root = Resolve-TideDataRoot $live $wanted
            Set-TideJsonValue $live 'DataProtection:KeyPath' "$root${bs}dp-keys"
            $out = [ordered]@{
                wanted = @($wanted | ForEach-Object { "$($_.Path)=$($_.Suffix)" })
                before = $before
                root = $root
                after = @(Find-TideMissingDataSettings $live $wanted).Count
                none = Resolve-TideDataRoot ([pscustomobject]@{ Files = [pscustomobject]@{ RootPath = 'App_Data/files' } }) $wanted
            }
            ConvertTo-Json -Compress -InputObject $out
            """);
        r.GetProperty("wanted").EnumerateArray().Select(x => x.GetString()).Should().BeEquivalentTo("DataProtection:KeyPath=dp-keys", "Files:RootPath=files");
        r.GetProperty("before").EnumerateArray().Select(x => x.GetString()).Should().Equal("DataProtection:KeyPath");
        r.GetProperty("root").GetString().Should().Be(@"E:\TideData");
        r.GetProperty("after").GetInt32().Should().Be(0);
        r.GetProperty("none").ValueKind.Should().Be(JsonValueKind.Null, "a relative path names no data root");
    }

    [Fact]
    public void The_backup_account_is_given_exactly_what_backups_need_and_must_be_an_account_name()
    {
        var r = Run("""
            $sql = Get-Content -Raw (Join-Path $deploy 'sql\02-Grant-Backup.sql')
            $bad = try { & (Join-Path $deploy 'Install-Database.ps1') -SqlInstance 'SQLHOST' -AppAccount 'CONTOSO\svc-tide$' -BackupAccount "CONTOSO\bk'; DROP" 6>$null | Out-Null; 'ok' } catch { $_.Exception.Message }
            ConvertTo-Json -Compress -InputObject ([ordered]@{
                backupOperator = $sql.Contains('ALTER ROLE db_backupoperator ADD MEMBER')
                dbcreator = $sql.Contains('ALTER SERVER ROLE dbcreator ADD MEMBER')
                msdb = $sql.Contains('GRANT SELECT ON dbo.backupset')
                stopsOnError = $sql.Contains(':on error exit')
                bad = $bad
            })
            """);
        foreach (var grant in new[] { "backupOperator", "dbcreator", "msdb", "stopsOnError" }) r.GetProperty(grant).GetBoolean().Should().BeTrue(grant);
        r.GetProperty("bad").GetString().Should().StartWith("BackupAccount must be an account name", "it goes into the SQL inside brackets and quotes");
    }

    [Fact]
    public void Backup_sets_are_this_installs_only_newest_first()
    {
        // S7-1: production and test back up to one share; each sees, prunes and drills only its own sets.
        var r = Run("""
            $dest = Join-Path ([IO.Path]::GetTempPath()) ('tide-sets-' + [guid]::NewGuid().ToString('N'))
            function Add-TestSet([string] $name, [string] $server, [string] $site) {
                $d = Join-Path $dest $name
                New-Item -ItemType Directory -Force -Path $d | Out-Null
                ConvertTo-Json @{ format = 1; server = $server; site = $site } | Set-Content -Path (Join-Path $d 'backup-set.json')
            }
            try {
                Add-TestSet 'TIDE-20260101-0130' 'WEB1' 'C:\inetpub\tide'
                Add-TestSet 'TIDE-WEB1-tide-20260103-0130' 'WEB1' 'C:\inetpub\tide'
                Add-TestSet 'TIDE-WEB1-tide-test-20260104-0130' 'WEB1' 'C:\inetpub\tide-test'
                Add-TestSet 'TIDE-WEB2-tide-20260105-0130' 'WEB2' 'C:\inetpub\tide'
                New-Item -ItemType Directory -Force -Path (Join-Path $dest 'TIDE-WEB1-tide-20260106-0130') | Out-Null
                $bad = Join-Path $dest 'TIDE-WEB1-tide-20260107-0130'
                New-Item -ItemType Directory -Force -Path $bad | Out-Null
                Set-Content -Path (Join-Path $bad 'backup-set.json') -Value '{ not json'
                $mine = @(Get-TideBackupSets $dest -Server 'WEB1' -Site 'C:\inetpub\tide' | ForEach-Object { $_.Name })
                $all = @(Get-TideBackupSets $dest | ForEach-Object { $_.Name })
                $env:COMPUTERNAME = 'WEB 1'
                $prefix = Get-TideBackupSetPrefix ([pscustomobject]@{ Folder = 'C:\inetpub\tide test' })
            } finally { Remove-Item -LiteralPath $dest -Recurse -Force }
            ConvertTo-Json -Compress -InputObject ([ordered]@{ mine = $mine; all = $all; prefix = $prefix })
            """);
        r.GetProperty("mine").EnumerateArray().Select(x => x.GetString()).Should().Equal("TIDE-WEB1-tide-20260103-0130", "TIDE-20260101-0130");
        r.GetProperty("all").GetArrayLength().Should().Be(5, "a folder without backup-set.json isn't a set; an unreadable one is, unfiltered");
        r.GetProperty("prefix").GetString().Should().Be("TIDE-WEB-1-tide-test-");
    }

    [Fact]
    public void Backup_helpers_find_the_folders_and_files_a_set_holds()
    {
        var r = Run("""
            $bs = [char]92
            $dir = Join-Path ([IO.Path]::GetTempPath()) ('tide-site-' + [guid]::NewGuid().ToString('N'))
            New-Item -ItemType Directory -Path $dir | Out-Null
            try {
                Set-Content -Path (Join-Path $dir 'appsettings.json') -Value '{ "Files": { "RootPath": "App_Data/files" }, "DataProtection": { "KeyPath": "" } }'
                Set-Content -Path (Join-Path $dir 'appsettings.Production.json') -Value ('{ "DataProtection": { "KeyPath": ' + (ConvertTo-Json (Join-Path $dir 'keys')) + ' } }')
                $site = Read-TideSite $dir
                $folders = @(Get-TideBackupFolders $site)
                $out = [ordered]@{
                    labels = @($folders | ForEach-Object { $_.Label })
                    files = $folders[0].Path -eq [IO.Path]::GetFullPath((Join-Path $dir 'App_Data/files'))
                    keys = $folders[1].Path -eq (Join-Path $dir 'keys')
                    stored = (Get-TideStoredFilePath 'R:/files' ([guid]'0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d')).Replace($bs, '/')
                    stamp = Get-TideStamp -When ([datetime]'2026-01-02T03:04:05')
                    parsed = (ConvertFrom-TideStamp '20260102-030405').ToString('o', [Globalization.CultureInfo]::InvariantCulture)
                    notAStamp = ConvertFrom-TideStamp 'by hand'
                    sqlName = Get-SqlName 'Ti]de'
                }
            } finally { Remove-Item -LiteralPath $dir -Recurse -Force }
            ConvertTo-Json -Compress -InputObject $out
            """);
        r.GetProperty("labels").EnumerateArray().Select(x => x.GetString()).Should().Equal("files", "dp-keys");
        r.GetProperty("files").GetBoolean().Should().BeTrue("a relative path is from the site folder, as the app reads it");
        r.GetProperty("keys").GetBoolean().Should().BeTrue("appsettings.Production.json wins over appsettings.json");
        r.GetProperty("stored").GetString().Should().Be("R:/files/0a/0a1b2c3d4e5f4a6b8c7d9e0f1a2b3c4d.bin", "as DiskFileStore keeps a file");
        r.GetProperty("stamp").GetString().Should().Be("20260102-030405");
        r.GetProperty("parsed").GetString().Should().StartWith("2026-01-02T03:04:05");
        r.GetProperty("notAStamp").ValueKind.Should().Be(JsonValueKind.Null);
        r.GetProperty("sqlName").GetString().Should().Be("[Ti]]de]");
    }

    [Fact]
    public void The_carve_outs_open_health_without_Windows_and_the_API_to_tokens_with_Windows_left_on()
    {
        // A bearer token has no Windows sign-in, so IIS must let /api/v1 through anonymous; Windows stays on there for the
        // gMSA route. Each path turns anonymous on before it touches Windows, so it's never left with neither. A stand-in
        // for appcmd records the calls the installer's -SetAlways makes.
        var r = Run("""
            . (Join-Path (Join-Path $deploy 'lib') 'TideIis.ps1')
            $dir = Join-Path ([IO.Path]::GetTempPath()) ('tide-appcmd-' + [guid]::NewGuid().ToString('N'))
            New-Item -ItemType Directory -Path $dir | Out-Null
            try {
                $log = Join-Path $dir 'calls.txt'
                $fake = Join-Path $dir 'appcmd.ps1'
                Set-Content -Path $fake -Value ("Add-Content -LiteralPath '" + $log.Replace("'", "''") + "' -Value (`$args -join ' ')`nexit 0")
                $results = @(Sync-TideAnonymousCarveOuts -SiteName 'TIDE' -Appcmd $fake -SetAlways)
                $out = [ordered]@{
                    paths = @(Get-TideAnonymousPaths)
                    calls = @(Get-Content -LiteralPath $log | ForEach-Object { [string]$_ })
                    settings = @($results | ForEach-Object { "$($_.Path)=$($_.Settings)" })
                    states = @($results | ForEach-Object { $_.State })
                }
            } finally { Remove-Item -LiteralPath $dir -Recurse -Force }
            ConvertTo-Json -Compress -InputObject $out
            """);
        r.GetProperty("paths").EnumerateArray().Select(x => x.GetString()).Should().Equal("health", "api/v1");
        const string Auth = "/section:system.webServer/security/authentication/";
        r.GetProperty("calls").EnumerateArray().Select(x => x.GetString()).Should().Equal(
            $"set config TIDE/health {Auth}anonymousAuthentication /enabled:true /commit:apphost",
            $"set config TIDE/health {Auth}windowsAuthentication /enabled:false /commit:apphost",
            $"set config TIDE/api/v1 {Auth}anonymousAuthentication /enabled:true /commit:apphost",
            $"set config TIDE/api/v1 {Auth}windowsAuthentication /enabled:true /commit:apphost");
        r.GetProperty("settings").EnumerateArray().Select(x => x.GetString()).Should().Equal(
            "/health=anonymous on, Windows off", "/api/v1=anonymous on, Windows on");
        r.GetProperty("states").EnumerateArray().Select(x => x.GetString()).Should().AllBe("configured");
    }

    [Fact]
    public void Settings_files_with_the_comments_and_trailing_commas_the_app_accepts_are_read_under_Windows_PowerShell_too()
    {
        // .NET's configuration reader takes these; Windows PowerShell 5.1's ConvertFrom-Json doesn't, and the scheduled
        // backups run under it. Strings that look like comments (a URL, a "//" note key, "/*") are left as they are.
        foreach (var shell in Shells())
        {
            var r = Run("""
                $text = @'
                {
                  // relay changed 2026-11
                  "Email": { "SmtpHost": "relay.contoso.com", /* was smtp01 */ "SmtpPort": 25, },
                  "//": "a note, // not a comment",
                  "Url": "https://intel.example/feed?a=1/*b*/",
                  "Quote": "say \"hi\" // still text",
                  "Groups": [ "CONTOSO\\TIDE-Analysts", ],
                  "Files": { "RootPath": "E:\\TideData\\files" }, /* the data root
                     is outside the web root */
                }
                '@
                $j = ConvertFrom-TideJson $text
                $dir = Join-Path ([IO.Path]::GetTempPath()) ('tide-json-' + [guid]::NewGuid().ToString('N'))
                New-Item -ItemType Directory -Path $dir | Out-Null
                try {
                    Set-Content -Path (Join-Path $dir 'appsettings.Production.json') -Value $text -Encoding UTF8
                    $site = Read-TideSite $dir
                    Set-Content -Path (Join-Path $dir 'broken.json') -Value '{ "a": }'
                    $bad = try { Read-TideJsonFile (Join-Path $dir 'broken.json'); 'read' } catch { $_.Exception.Message }
                } finally { Remove-Item -LiteralPath $dir -Recurse -Force }
                $out = [ordered]@{
                    smtpHost = $j.Email.SmtpHost
                    smtpPort = $j.Email.SmtpPort
                    note = $j.'//'
                    url = $j.Url
                    quote = $j.Quote
                    groups = @($j.Groups)
                    root = Get-TideSetting $site 'Files:RootPath'
                    bad = $bad
                    extras = Test-TideJsonHasExtras $text
                    plain = Test-TideJsonHasExtras '{ "a": "x//y", "b": [1, 2] }'
                    plainText = ConvertTo-TideStrictJson '{ "a": "x//y", "b": [1, 2] }'
                }
                ConvertTo-Json -Compress -InputObject $out
                """, shell);
            r.GetProperty("smtpHost").GetString().Should().Be("relay.contoso.com");
            r.GetProperty("smtpPort").GetInt32().Should().Be(25);
            r.GetProperty("note").GetString().Should().Be("a note, // not a comment");
            r.GetProperty("url").GetString().Should().Be("https://intel.example/feed?a=1/*b*/");
            r.GetProperty("quote").GetString().Should().Be("say \"hi\" // still text");
            r.GetProperty("groups").EnumerateArray().Select(x => x.GetString()).Should().Equal(@"CONTOSO\TIDE-Analysts");
            r.GetProperty("root").GetString().Should().Be(@"E:\TideData\files", "Read-TideSite, which the backups and the drill use");
            r.GetProperty("bad").GetString().Should().Contain("broken.json").And.Contain("isn't valid JSON");
            r.GetProperty("extras").GetBoolean().Should().BeTrue();
            r.GetProperty("plain").GetBoolean().Should().BeFalse();
            r.GetProperty("plainText").GetString().Should().Be("""{ "a": "x//y", "b": [1, 2] }""");
        }
    }

    [Fact]
    public void A_path_with_a_space_and_a_trailing_backslash_reaches_robocopy_and_a_scheduled_task_whole()
    {
        // Windows PowerShell 5.1 quoted "D:\TIDE drop\" with the backslash escaping the closing quote: robocopy exited 16,
        // and a task's -Destination took the rest of its command line. The task's line is read here by powershell.exe
        // -File, as Task Scheduler starts it. robocopy and Task Scheduler are Windows only.
        if (!OperatingSystem.IsWindows()) return;
        foreach (var shell in Shells())
        {
            var r = Run("""
                $bs = [char]92
                $dir = Join-Path ([IO.Path]::GetTempPath()) ('tide path ' + [guid]::NewGuid().ToString('N'))
                New-Item -ItemType Directory -Path (Join-Path $dir 'src folder') | Out-Null
                try {
                    Set-Content -Path (Join-Path $dir 'src folder\a.txt') -Value 'a'
                    $copy = Copy-TideFolder -From ((Join-Path $dir 'src folder') + $bs) -To ((Join-Path $dir 'dst folder') + $bs)
                    $echo = Join-Path $dir 'echo.ps1'
                    Set-Content -Path $echo -Value 'param($Destination, $Keep) "$Destination|$Keep"'
                    # As Register-TideTask builds a task's command line, and as Task Scheduler hands it to powershell.exe.
                    $task = {
                        param($dest)
                        $psi = New-Object System.Diagnostics.ProcessStartInfo 'powershell.exe', ('-NoProfile -NonInteractive -ExecutionPolicy Bypass -File ' +
                            (ConvertTo-TideQuotedArg $echo) + ' -Destination ' + (ConvertTo-TideQuotedArg (Get-TideCleanPath $dest)) + ' -Keep 3')
                        $psi.UseShellExecute = $false; $psi.RedirectStandardOutput = $true
                        $p = [System.Diagnostics.Process]::Start($psi); $o = $p.StandardOutput.ReadToEnd().Trim(); $p.WaitForExit(); $o
                    }
                    $out = [ordered]@{
                        copied = $copy.Files
                        root = & $task "E:$bs"
                        share = & $task "$bs${bs}backup01${bs}tide share$bs"
                        clean = @((Get-TideCleanPath "D:${bs}TIDE drop${bs}app$bs$bs"), (Get-TideCleanPath "E:$bs"), (Get-TideCleanPath 'E:/'),
                                  (Get-TideCleanPath "$bs"), (Get-TideCleanPath '/var/opt/mssql/backup/'), (Get-TideCleanPath ''), (Get-TideCleanPath 'D:\tide'))
                        quoted = @((ConvertTo-TideQuotedArg 'D:\tide'), (ConvertTo-TideQuotedArg 'E:\'), (ConvertTo-TideQuotedArg 'SQLHOST\PROD'))
                    }
                } finally { Remove-Item -LiteralPath $dir -Recurse -Force }
                ConvertTo-Json -Compress -InputObject $out
                """, shell);
            r.GetProperty("copied").GetInt32().Should().Be(1, "robocopy got both folders whole");
            r.GetProperty("root").GetString().Should().Be(@"E:\|3");
            r.GetProperty("share").GetString().Should().Be(@"\\backup01\tide share|3");
            r.GetProperty("clean").EnumerateArray().Select(x => x.GetString()).Should().Equal(
                @"D:\TIDE drop\app", @"E:\", @"E:\", @"\", "/var/opt/mssql/backup", "", @"D:\tide");
            r.GetProperty("quoted").EnumerateArray().Select(x => x.GetString()).Should().Equal(
                "\"D:\\tide\"", "\"E:\\\\\"", "\"SQLHOST\\PROD\"");
        }
    }

    [Fact]
    public void Install_Database_uses_an_Invoke_Sqlcmd_only_with_the_parameters_it_has_and_otherwise_sqlcmd()
    {
        // Stand-ins for each Invoke-Sqlcmd (SqlServer 22, SqlServer 21, the SQLPS module SQL Server setup installs) and for
        // sqlcmd.exe record which engine ran each step and with which TLS parameters. Nothing connects anywhere.
        foreach (var shell in Shells())
        {
            var r = Run("""
                $common = '[CmdletBinding()] param($ServerInstance, $Query, $InputFile, $Variable, $Database, $QueryTimeout, $OutputSqlErrors, [switch] $AbortOnError'
                $body = @'
                $global:sqlLog += ('module' + (@($PSBoundParameters.Keys | Where-Object { $_ -in 'Encrypt', 'EncryptConnection', 'TrustServerCertificate' } | Sort-Object | ForEach-Object { " $_=$($PSBoundParameters[$_])" }) -join ''))
                if ($Query) { [pscustomobject]@{ RunAs = 'CONTOSO\dba'; IsSysadmin = 1; CanCreateDb = 1; CanCreateLogin = 1; DbExists = 0; LoginExists = 0; ControlsDb = 0 } }
                '@
                $modules = [ordered]@{
                    v22   = ', [string] $Encrypt, [switch] $EncryptConnection, [switch] $TrustServerCertificate)'
                    v21   = ', [switch] $EncryptConnection, [switch] $TrustServerCertificate)'
                    sqlps = ', [switch] $EncryptConnection)'
                    bare  = ')'
                }
                $fakeSqlcmd = { $global:sqlLog += ('sqlcmd' + $(if ($args -contains '-C') { ' -C' })); if ($args -contains '-Q') { 'CONTOSO\dba|1|1|1|0|0|0' }; $global:LASTEXITCODE = 0 }
                # With no sqlcmd.exe: the stand-in removed, and PATH an empty folder, so a real one isn't found either.
                $empty = Join-Path ([IO.Path]::GetTempPath()) ('tide-nopath-' + [guid]::NewGuid().ToString('N'))
                New-Item -ItemType Directory -Path $empty | Out-Null
                $out = [ordered]@{}
                $path = $env:PATH
                try {
                    foreach ($m in $modules.Keys) {
                        New-Item -Path function: -Name Invoke-Sqlcmd -Value ([scriptblock]::Create($common + $modules[$m] + "`n" + $body)) -Force | Out-Null
                        foreach ($case in 'trust', 'validate', 'trust-nosqlcmd') {
                            $global:sqlLog = @()
                            $extra = @{}; if ($case -eq 'validate') { $extra.ValidateServerCertificate = $true }
                            if ($case -eq 'trust-nosqlcmd') { Remove-Item function:sqlcmd.exe -ErrorAction SilentlyContinue; $env:PATH = $empty }
                            else { New-Item -Path function: -Name sqlcmd.exe -Value $fakeSqlcmd -Force | Out-Null }
                            $result = try { & (Join-Path $deploy 'Install-Database.ps1') -SqlInstance 'SQLHOST' -AppAccount 'CONTOSO\svc-tide$' @extra 6>$null | Out-Null; 'ok' } catch { $_.Exception.Message }
                            $env:PATH = $path
                            $out["$m/$case"] = [pscustomobject]@{ Result = $result; Calls = @($global:sqlLog | Select-Object -Unique) }
                        }
                    }
                } finally { $env:PATH = $path; Remove-Item -LiteralPath $empty -Recurse -Force }
                ConvertTo-Json -Depth 4 -Compress -InputObject $out
                """, shell);
            (string Result, string[] Calls) Case(string name) => (r.GetProperty(name).GetProperty("Result").GetString()!,
                r.GetProperty(name).GetProperty("Calls").EnumerateArray().Select(x => x.GetString()!).ToArray());
            Case("v22/trust").Result.Should().Be("ok");
            Case("v22/trust").Calls.Should().Equal(["module TrustServerCertificate=True"], "SqlServer 22 encrypts by default");
            Case("v22/validate").Calls.Should().Equal("module TrustServerCertificate=False");
            Case("v21/trust").Calls.Should().Equal("module EncryptConnection=True TrustServerCertificate=True");
            Case("v21/validate").Calls.Should().Equal("module EncryptConnection=True TrustServerCertificate=False");
            Case("sqlps/trust").Result.Should().Be("ok");
            Case("sqlps/trust").Calls.Should().Equal(["sqlcmd -C"], "SQLPS can't skip the certificate check, so sqlcmd -C runs every step");
            Case("sqlps/validate").Calls.Should().Equal("module EncryptConnection=True");
            Case("sqlps/trust-nosqlcmd").Result.Should().Contain("can't encrypt the connection without checking").And.Contain("Install-Module SqlServer")
                .And.Contain("-ValidateServerCertificate").And.Contain("Nothing was changed");
            Case("sqlps/trust-nosqlcmd").Calls.Should().BeEmpty();
            Case("bare/validate").Calls.Should().Equal("sqlcmd");
            Case("bare/trust-nosqlcmd").Result.Should().Contain("can't encrypt the connection");
            Case("v22/trust-nosqlcmd").Result.Should().Be("ok");
        }
    }

    [Fact]
    public void An_upgrade_points_this_sites_scheduled_tasks_at_the_kept_scripts_and_leaves_another_sites_alone()
    {
        // Stand-ins for the ScheduledTasks cmdlets: the tasks as Backup-Tide.ps1 -Register writes them, with the site
        // path quoted with and without a trailing backslash, another site's task, and one already on the kept copy.
        // Scheduled tasks and their Windows paths are Windows only.
        if (!OperatingSystem.IsWindows()) return;
        foreach (var shell in Shells())
        {
            var r = Run("""
                . (Join-Path (Join-Path $deploy 'lib') 'TideIis.ps1')
                $kept = Join-Path ([IO.Path]::GetTempPath()) ('tide-kept-' + [guid]::NewGuid().ToString('N'))
                New-Item -ItemType Directory -Path $kept | Out-Null
                try {
                    foreach ($n in 'Backup-Tide.ps1', 'Test-TideRestore.ps1') { Set-Content -Path (Join-Path $kept $n) -Value '#' }
                    $pre = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File '
                    function Task($name, $file, $siteArg) {
                        [pscustomobject]@{ TaskName = $name; TaskPath = '\'; Actions = @([pscustomobject]@{ Execute = 'powershell.exe'; Arguments = "$pre`"$file`" -SitePath `"$siteArg`" -Keep 14" }) }
                    }
                    $script:tasks = @(
                        (Task 'TIDE nightly backup' 'C:\rel\v1\deploy\Backup-Tide.ps1' 'D:\inetpub\tide'),
                        (Task 'TIDE monthly restore drill' 'C:\rel\v1\deploy\Test-TideRestore.ps1' 'D:\inetpub\tide\\'),
                        (Task 'TIDE log backup' 'C:\rel\v1\deploy\Backup-Tide.ps1' 'D:\inetpub\tide\'),
                        (Task 'TIDE other site' 'C:\rel\v1\deploy\Backup-Tide.ps1' 'D:\inetpub\tide2'),
                        (Task 'TIDE kept' (Join-Path $kept 'Backup-Tide.ps1') 'D:\inetpub\tide'),
                        [pscustomobject]@{ TaskName = 'TIDE bare'; TaskPath = '\'; Actions = @([pscustomobject]@{ Execute = 'x.exe'; Arguments = '' }) })
                    $script:set = @()
                    function Get-ScheduledTask { param($TaskName, $ErrorAction) $script:tasks }
                    function New-ScheduledTaskAction { param($Execute, $Argument) [pscustomobject]@{ Execute = $Execute; Arguments = $Argument } }
                    function Set-ScheduledTask { param($TaskName, $TaskPath, $Action, $ErrorAction) $script:set += "$TaskName|$($Action.Arguments)" }
                    Update-TideTaskPaths -KeptDir $kept -SitePath 'D:\inetpub\tide\' 6>$null
                    $out = [ordered]@{ kept = $kept; set = @($script:set) }
                } finally { Remove-Item -LiteralPath $kept -Recurse -Force }
                ConvertTo-Json -Compress -InputObject $out
                """, shell);
            var kept = r.GetProperty("kept").GetString();
            r.GetProperty("set").EnumerateArray().Select(x => x.GetString()).Should().Equal(
                $"TIDE nightly backup|-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{kept}\\Backup-Tide.ps1\" -SitePath \"D:\\inetpub\\tide\" -Keep 14",
                $"TIDE monthly restore drill|-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{kept}\\Test-TideRestore.ps1\" -SitePath \"D:\\inetpub\\tide\\\\\" -Keep 14",
                $"TIDE log backup|-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{kept}\\Backup-Tide.ps1\" -SitePath \"D:\\inetpub\\tide\\\" -Keep 14");
        }
    }
}
