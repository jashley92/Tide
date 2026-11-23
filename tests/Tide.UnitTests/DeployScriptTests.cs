using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using AwesomeAssertions;
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

    internal static string ReadText(params string[] parts) => Read(parts);

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
            // Under /demo/: mapped only in Development with Demo:Seed (the screenshot tool's), never on an installed site.
            .Where(p => !p.StartsWith("/demo/", StringComparison.Ordinal))
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
        var check = script.IndexOf("$check = Invoke-TideBundleCheck -ZipPath $BundleZip", StringComparison.Ordinal);
        var handover = script.IndexOf("& $bundleScript @pass", StringComparison.Ordinal);
        var expand = script.IndexOf("ZipFile]::ExtractToDirectory($zipPath, $staging)", StringComparison.Ordinal);
        check.Should().BeGreaterThan(-1);
        handover.Should().BeGreaterThan(check);
        expand.Should().BeGreaterThan(handover, "the bundle's own script runs the upgrade, so the app isn't expanded twice");
        script.Should().Contain("ReleaseHashChecked", "the handover tells the bundle's script the check already ran");
        script.Should().Contain("-Uri \"$base/$zipName.sig\" -OutFile \"$BundleZip.sig\"", "-Version downloads the signature with the bundle");
        Read("deploy", "lib", "TideUpgrade.ps1").Should().Contain("/releases/download/$Tag/tide-$Tag.zip.sha256",
            "an unsigned bundle with no hash beside it is still checked against its release");
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
        var verify = At("RESTORE VERIFYONLY FROM DISK = @bak WITH CHECKSUM");
        new[] { dbBackup, verify, snapshot, settings, carveOut, stop, copy, stale, warm }.Should().BeInAscendingOrder();
        script.Should().Contain("/XF appsettings.Production.json VERSION.txt /XD logs App_Data", "the deploy keeps the settings, logs and App_Data");
        script.Should().Contain("COPY_ONLY", "the pre-upgrade backup doesn't reset the scheduled backups' chain");
        script.Should().NotContain("Join-Path $bakDir", "the backup folder is on the SQL Server, whose drive the web server may not have");
    }

    [Fact]
    public void Older_upgrade_backups_are_pruned_only_after_the_new_build_answered_and_never_this_runs()
    {
        var script = Read("deploy", "Upgrade-Tide.ps1");
        int At(string s)
        {
            var i = script.IndexOf(s, StringComparison.Ordinal);
            i.Should().BeGreaterThan(-1, "the script has '{0}'", s);
            return i;
        }
        var record = At("ConvertTo-Json | Set-Content -LiteralPath (Join-Path $BackupRoot 'snapshot.json')");
        var stop = At("Stop-TideAppPool -Name $AppPoolName\n");
        var lastRollback = script.LastIndexOf("Complete-Rollback \"", StringComparison.Ordinal);
        var versionWritten = At("Set-Content -Path (Join-Path $SitePath 'VERSION.txt')");
        var plan = At("$plan = Get-TidePrunePlan -StartProven $startProven -CustomBackupRoot (-not $upgradeBackupsDir) -Keep $KeepUpgradeBackups");
        var markGate = At("if ($plan.MarkSucceeded) {");
        var succeeded = At("Set-TideUpgradeSucceeded -Folder $BackupRoot");
        var baksGate = At("if ($plan.PruneBaks) {");
        var pruneBaks = At("Get-TidePrunableBaks -History $history -BakDir $bakDir -DbName $DbName -Files $inFolder");
        var foldersGate = At("if ($plan.PruneFolders) {");
        var pruneFolders = At("Get-TidePrunable -Items $snapshots -Keep $KeepUpgradeBackups -Exclude @($stamp)");
        var complete = At("Write-Step \"Upgrade complete.\"");
        new[] { record, stop, lastRollback, versionWritten, plan, markGate, succeeded, baksGate, pruneBaks, foldersGate, pruneFolders, complete }
            .Should().BeInAscendingOrder(
                "the record is written before anything changes, every failure rolls back and throws before the run is " +
                "marked succeeded and the prune, and both go by the plan, so a failed upgrade keeps every backup");
        script.IndexOf("Remove-Item -LiteralPath $path", StringComparison.Ordinal).Should().BeGreaterThan(baksGate);

        // Only TIDE's own answer proves the start: the warm-up's 'started' verdicts, not Windows authentication's.
        (script.Split("$startProven = $true").Length - 1).Should().Be(2);
        var authCase = script[At("        'auth' {\n            Write-Warn2 (\"Couldn't prove the new build started")..At("        'other' { Write-Warn2 \"$HealthUrl answered")];
        authCase.Should().Contain("$started = $true").And.NotContain("$startProven");
        script.Should().NotContain("xp_delete_file", "the .bak files are deleted only through a path this host reaches");
        script.Should().Contain("Remove-TideTree $old.FullName").And.NotContain("Remove-Item -LiteralPath $old.FullName -Recurse",
            "Windows PowerShell 5.1's Remove-Item -Recurse follows a junction into its target");
    }

    [Fact]
    public void Whatever_fails_between_stopping_the_pool_and_starting_it_rolls_back_and_every_rollback_asks_whether_the_old_build_fits()
    {
        // A file the stale-file removal can't delete, an IIS setting that won't take, a pool that won't start: each throws
        // under $ErrorActionPreference = 'Stop', and used to leave the site down and half deployed.
        var script = Read("deploy", "Upgrade-Tide.ps1");
        int At(string s, int from = 0)
        {
            var i = script.IndexOf(s, from, StringComparison.Ordinal);
            i.Should().BeGreaterThan(-1, "the script has '{0}'", s);
            return i;
        }
        var open = At("$deployStep = 'the copy'\ntry {");
        var close = At("    Start-WebAppPool -Name $AppPoolName\n} catch {", open);
        foreach (var step in new[] { "& robocopy $appSrc $SitePath", "Remove-TideStaleFiles -From $appSrc", "recycling.periodicRestart.time",
                     "Set-TideSingleWorkerProcess -Name $AppPoolName" })
            At(step, open).Should().BeLessThan(close, "{0} is inside the rollback's try", step);
        var handler = script[close..At("\n}\n", close)];
        handler.Should().Contain("Restore-PreviousBuild $failure").And.Contain("Complete-Rollback $failure");

        // Both rollbacks decide by what the previous build knows, not by what this run saw before deploying.
        script.Should().Contain("Complete-Rollback \"the new build didn't start\"");
        script.Should().Contain("Get-TideBuildMigrations $rollbackDir").And.Contain("Get-TideRollbackDecision -Applied $now -Known $rollbackKnows");
        script.Should().NotContain("$appliedBeforeDeploy -notcontains", "a re-run's snapshot already has the first run's migrations");
        script.Should().Contain("-PoolWasRunning $poolWasRunning");
        At("$poolWasRunning = (Get-WebAppPoolState").Should().BeLessThan(At("Stop-TideAppPool -Name $AppPoolName\n"),
            "the pool's state is read before this run stops it");
    }

    [Fact]
    public void The_scripts_that_manage_IIS_refuse_PowerShell_7_before_they_change_anything_and_the_rest_run_there()
    {
        foreach (var name in new[] { "Install-Tide.ps1", "Upgrade-Tide.ps1", "Verify-Install.ps1" })
        {
            var text = Read("deploy", name);
            var script = text[text.IndexOf("\n#>", StringComparison.Ordinal)..];   // the code, after the help
            var assert = script.IndexOf($"Assert-TideWindowsPowerShell '{name}'", StringComparison.Ordinal);
            assert.Should().BeGreaterThan(-1, "{0} refuses PowerShell 7", name);
            foreach (var later in new[] { "Import-Module WebAdministration", "Get-Credential", "Invoke-WebRequest", "IIS:\\", "Write-Step \"" })
            {
                var at = script.IndexOf(later, StringComparison.Ordinal);
                if (at >= 0) at.Should().BeGreaterThan(assert, "{0} refuses before {1}", name, later);
            }
        }
        // Documented to run in PowerShell 7 too (Test-TideBundle.ps1), or not using the IIS: drive.
        foreach (var name in new[] { "Test-TideBundle.ps1", "Backup-Tide.ps1", "Test-TideRestore.ps1", "Install-Database.ps1", "New-TideConfig.ps1" })
            Read("deploy", name).Should().NotContain("Assert-TideWindowsPowerShell", name);
    }

    [Fact]
    public void The_release_bundle_carries_the_deploy_folder_and_the_manifest_the_preflight_reads()
    {
        var release = Read(".github", "workflows", "release.yml");
        release.Should().Contain("cp -r deploy \"$root/deploy\"", "every deploy script, the upgrade's included, ships in the bundle");
        release.Should().Contain("\"$root/migrations-sqlserver.txt\"");
        release.Should().Contain("bash tools/ci/sqlserver-schema-script.sh publish/tide-schema-sqlserver.sql --no-build")
            .And.Contain("cp publish/tide-schema-sqlserver.sql \"$root/deploy/sql/tide-schema-sqlserver.sql\"",
                "the DBA schema script is where Install-Database.ps1 -ApplySchema looks, inside the signed zip");
        Read("deploy", "Install-Database.ps1").Should().Contain(@"Join-Path $scriptDir 'sql\tide-schema-sqlserver.sql'");
        release.IndexOf("deploy/sql/tide-schema-sqlserver.sql", StringComparison.Ordinal)
            .Should().BeLessThan(release.IndexOf("zip -qr", StringComparison.Ordinal));
        release.Should().Contain("tide-${version}.zip.sha256");
        release.Should().Contain("\"bundle/tide-${version}.zip.sig\" \"${args[@]}\"", "the signature is published with the bundle");
        release.Should().Contain("RELEASE_SIGNING_KEY: ${{ secrets.RELEASE_SIGNING_KEY }}").And.Contain("The RELEASE_SIGNING_KEY secret isn't set");
        release.IndexOf("- name: Sign the bundle", StringComparison.Ordinal).Should().BeLessThan(release.IndexOf("gh release create", StringComparison.Ordinal));
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
public partial class DeployScriptPowerShellTests
{
    private static readonly string Lib = Path.Combine(DeployScriptTests.Deploy, "lib");

    [GeneratedRegex(@"^\d{14}_", RegexOptions.None, 1000)]
    private static partial Regex MigrationFile();

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
            return JsonElement.Parse(stdout);
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
            "AppPoolName", "SqlInstance", "DbName", "HealthUrl", "BackupRoot", "DataRoot", "SkipDbBackup", "Force", "ReleaseHashChecked",
            "ExpectedSha256", "AllowUnsigned", "TrustedKeysPath", "KeepUpgradeBackups"]);
        string? Default(JsonElement s, string n) => s.GetProperty("Params").EnumerateArray().Single(p => p.GetProperty("Name").GetString() == n).GetProperty("Default").GetString();
        Default(upgrade, "KeepUpgradeBackups").Should().Be("3", "docs/UPGRADE.md says an upgrade keeps three");
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
    public void A_failed_upgrade_starts_the_previous_build_only_on_a_database_it_knows()
    {
        var r = Run("""
            $old = @('20260101000000_Initial', '20260201000000_Next')
            $new = '20260301000000_Later'
            $d = { param($a, $k, $b, $he, $ke, [hashtable] $more = @{}) Get-TideRollbackDecision -Applied $a -Known $k -AppliedBefore $b -HistoryError $he -KnownError $ke @more }
            $cases = [ordered]@{
                same       = & $d $old $old $old
                empty      = & $d @() $old @()
                older      = & $d @('20260101000000_Initial') $old @('20260101000000_Initial')
                migrated   = & $d ($old + $new) $old $old
                rerun      = & $d ($old + $new) $old ($old + $new)
                rerunCopy  = & $d ($old + $new) $old ($old + $new) $null $null @{ NewBuildRan = $false }
                noHistory  = & $d @() $old $old 'Login failed'
                noBuild    = & $d ($old + $new) @() $old $null 'Tide.Migrations.SqlServer.dll isn''t there.'
                noBefore   = & $d ($old + $new) $old @() $null $null @{ BeforeError = 'timeout' }
                dbaCopy    = & $d ($old + $new) $old ($old + $new) $null $null @{ DbaApplies = $true; NewBuildRan = $false }
                dbaWarmup  = & $d ($old + $new) $old ($old + $new) $null $null @{ DbaApplies = $true }
                dbaNew     = & $d ($old + $new) $old $old $null $null @{ DbaApplies = $true }
                copyNoHist = & $d @() $old $old 'Login failed' $null @{ NewBuildRan = $false }
                copyBlind  = & $d @() $old @() 'Login failed' $null @{ NewBuildRan = $false; BeforeError = 'Login failed'; PoolWasRunning = $true }
                copyNoBld  = & $d $old @() $old $null 'no dll' @{ NewBuildRan = $false; PoolWasRunning = $true }
                rerunNoBld = & $d ($old + $new) @() ($old + $new) $null 'no dll' @{ NewBuildRan = $false }
                rerunBlind = & $d @() $old @() 'Login failed' $null @{ NewBuildRan = $false; BeforeError = 'Login failed' }
                dbaNoBld   = & $d ($old + $new) @() ($old + $new) $null 'no dll' @{ NewBuildRan = $false; DbaApplies = $true }
            }
            ConvertTo-Json -Depth 4 -Compress -InputObject $cases
            """);
        (bool Safe, string Verdict, string[] Unknown, bool EarlierRun, string? Message) Case(string name)
        {
            var c = r.GetProperty(name);
            var unknown = c.GetProperty("Unknown");
            return (c.GetProperty("Safe").GetBoolean(), c.GetProperty("Verdict").GetString()!,
                unknown.ValueKind == JsonValueKind.Array ? unknown.EnumerateArray().Select(x => x.GetString()!).ToArray() : [unknown.GetString()!],
                c.GetProperty("EarlierRun").GetBoolean(), c.GetProperty("Message").GetString());
        }
        Case("same").Safe.Should().BeTrue();
        Case("empty").Safe.Should().BeTrue("a database with no history has nothing the previous build doesn't know");
        Case("older").Safe.Should().BeTrue();

        var migrated = Case("migrated");
        (migrated.Safe, migrated.Verdict, migrated.EarlierRun).Should().Be((false, "unknown", false));
        migrated.Unknown.Should().Equal("20260301000000_Later");
        migrated.Message.Should().Contain("STOPPED").And.Contain("20260301000000_Later").And.Contain("restore the pre-upgrade database backup");

        // Re-run without restoring: this run's snapshot of the history already had the new migration, which used to read
        // as "nothing new" and start the previous build on a schema it doesn't know.
        var rerun = Case("rerun");
        (rerun.Safe, rerun.EarlierRun).Should().Be((false, true));
        rerun.Message.Should().Contain("earlier upgrade").And.Contain("this run's pre-upgrade backup has them too").And.Contain("-preupgrade-");

        var noHistory = Case("noHistory");
        (noHistory.Safe, noHistory.Verdict).Should().Be((false, "history-unreadable"));
        noHistory.Message.Should().Contain("STOPPED").And.Contain("Login failed").And.Contain("20260201000000_Next", "the previous build's newest, to compare with");

        var noBuild = Case("noBuild");
        (noBuild.Safe, noBuild.Verdict).Should().Be((false, "build-unreadable"));
        noBuild.Message.Should().Contain("STOPPED").And.Contain("isn't there").And.Contain("20260301000000_Later", "the database's newest, to compare with");

        // AppMigrates re-run whose copy fails: the new build never ran, but the database still has the first run's migration.
        (Case("rerunCopy").Safe, Case("rerunCopy").EarlierRun).Should().Be((false, true));
        (Case("noBefore").Safe, Case("noBefore").Verdict).Should().Be((false, "before-unreadable"));

        // DbaApplies: the preflight requires the DBA to have applied the new release's migrations already, and the previous
        // build ran on them until the pool stopped. Restoring the backup would undo the DBA's schema.
        foreach (var dba in new[] { "dbaCopy", "dbaWarmup" })
        {
            var c = Case(dba);
            (c.Safe, c.Verdict).Should().Be((true, "dba-ahead"), dba);
            r.GetProperty(dba).GetProperty("Note").GetString().Should().Contain("by the DBA").And.Contain("20260301000000_Later");
        }
        (Case("dbaNew").Safe, Case("dbaNew").Verdict).Should().Be((false, "unknown"), "one that wasn't there before this run is the new build's");

        // A failure before the pool was started: the database is as it was, so the history only has to be read once.
        Case("copyNoHist").Safe.Should().BeTrue("the history from before the run stands in");
        // With nothing to compare, unchanged is enough only when the previous build was running on it when the run began
        // (or on DbaApplies, where only the DBA changes the schema).
        foreach (var blind in new[] { "copyBlind", "copyNoBld", "dbaNoBld" })
        {
            (Case(blind).Safe, Case(blind).Verdict).Should().Be((true, "unchanged"), blind);
            r.GetProperty(blind).GetProperty("Note").GetString().Should().StartWith("The new build never ran");
        }
        // AppMigrates re-run without restoring, whose copy fails: the pool was already stopped by the first, migrated run,
        // and the before-deploy history has its migrations too, so "unchanged" proves nothing.
        foreach (var rerunBlind in new[] { "rerunNoBld", "rerunBlind" })
        {
            (Case(rerunBlind).Safe, Case(rerunBlind).Verdict).Should().Be((false, "unchanged-unproven"), rerunBlind);
            Case(rerunBlind).Message.Should().Contain("STOPPED").And.Contain("already stopped").And.Contain("-preupgrade-");
        }
        Case("rerunNoBld").Message.Should().Contain("20260301000000_Later", "the database's newest, to compare with");
    }

    [Fact]
    public void A_backup_that_cant_be_read_back_for_want_of_permission_only_warns_and_one_that_reads_back_wrong_stops_the_upgrade()
    {
        // Stand-ins for the exceptions Invoke-DbScalar throws: a MethodInvocationException-like wrapper around a
        // SqlException-like one, with its Number and Errors.
        var r = Run("""
            $sql = { param($n, $m) [pscustomobject]@{ Message = $m; Number = $n; Errors = @([pscustomobject]@{ Number = $n }); InnerException = $null } }
            $wrap = { param($inner) [pscustomobject]@{ Message = 'Exception calling "ExecuteScalar"'; InnerException = $inner } }
            ConvertTo-Json -Compress -InputObject ([ordered]@{
                denied   = Get-TideVerifyFailureKind (& $wrap (& $sql 262 "CREATE DATABASE permission denied in database 'master'."))
                byText   = Get-TideVerifyFailureKind (& $wrap ([pscustomobject]@{ Message = 'CREATE DATABASE permission denied'; InnerException = $null }))
                terminated = Get-TideVerifyFailureKind (& $wrap (& $sql 3013 'VERIFY DATABASE is terminating abnormally.'))
                checksum = Get-TideVerifyFailureKind (& $wrap (& $sql 3189 'Damage to the backup set was detected.'))
                media    = Get-TideVerifyFailureKind (& $wrap (& $sql 3241 'The media family on device is incorrectly formed.'))
                osDenied = Get-TideVerifyFailureKind (& $wrap (& $sql 3201 'Cannot open backup device. Operating system error 5(Access is denied.).'))
                plain    = Get-TideVerifyFailureKind ([Exception]::new('timeout'))
            })
            """);
        r.GetProperty("denied").GetString().Should().Be("permission");
        r.GetProperty("byText").GetString().Should().Be("permission");
        foreach (var hard in new[] { "terminated", "checksum", "media", "osDenied", "plain" })
            r.GetProperty(hard).GetString().Should().Be("failed", hard);
        DeployScriptTests.ReadText("deploy", "Upgrade-Tide.ps1").Should().Contain("if ((Get-TideVerifyFailureKind $_.Exception) -ne 'permission') {");
    }

    [Fact]
    public void The_upgrade_reads_the_migrations_a_build_knows_from_its_assembly()
    {
        // The real assembly (built with these tests) against the migration files: every id, and nothing else.
        var expected = Directory.EnumerateFiles(Path.Combine(DeployScriptTests.RepoRoot(), "src", "Tide.Migrations.SqlServer", "Migrations"), "*.cs")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => MigrationFile().IsMatch(n!) && !n!.EndsWith(".Designer", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();
        expected.Should().NotBeEmpty();
        var r = Run("""
            $bin = '__BIN__'
            $dir = Join-Path ([IO.Path]::GetTempPath()) ('tide-build-' + [guid]::NewGuid().ToString('N'))
            New-Item -ItemType Directory -Path $dir | Out-Null
            try {
                # An id-like string that isn't an attribute's (no length byte before it) doesn't count.
                [IO.File]::WriteAllBytes((Join-Path $dir 'Tide.Migrations.SqlServer.dll'), [Text.Encoding]::ASCII.GetBytes("MZ x20260101000000_Initial`0"))
                $none = try { Get-TideBuildMigrations $dir; 'read' } catch { $_.Exception.Message }
                $missing = try { Get-TideBuildMigrations (Join-Path $dir 'nowhere'); 'read' } catch { $_.Exception.Message }
            } finally { Remove-Item -LiteralPath $dir -Recurse -Force }
            ConvertTo-Json -Compress -InputObject ([ordered]@{ ids = @(Get-TideBuildMigrations $bin); none = $none; missing = $missing })
            """.Replace("__BIN__", AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar).Replace("'", "''")));
        r.GetProperty("ids").EnumerateArray().Select(x => x.GetString()).Order(StringComparer.Ordinal).Should().Equal(expected);
        r.GetProperty("none").GetString().Should().Contain("names no migration");
        r.GetProperty("missing").GetString().Should().Contain("isn't there");
    }

    [Fact]
    public void A_script_that_manages_IIS_says_how_to_run_it_when_started_in_PowerShell_7()
    {
        var r = Run("""
            . (Join-Path (Join-Path $deploy 'lib') 'TideIis.ps1')
            ConvertTo-Json -Compress -InputObject ([ordered]@{
                core = Get-TideEditionRefusal 'Upgrade-Tide.ps1' -Edition 'Core'
                desktop = Get-TideEditionRefusal 'Upgrade-Tide.ps1' -Edition 'Desktop'
            })
            """);
        r.GetProperty("core").GetString().Should().StartWith("Upgrade-Tide.ps1 runs in Windows PowerShell 5.1, not PowerShell 7")
            .And.Contain("Nothing has been changed").And.Contain("powershell.exe");
        r.GetProperty("desktop").ValueKind.Should().Be(JsonValueKind.Null);

        // Started for real in each PowerShell here: PowerShell 7 refuses before any check runs; Windows PowerShell gets past
        // it (and isn't run further, since the checks would reach for IIS).
        foreach (var shell in Shells())
        {
            var run = Run("""
                $out = if ($PSVersionTable.PSEdition -ne 'Core') { 'not run' } else {
                    try { & (Join-Path $deploy 'Verify-Install.ps1') -Url 'http://127.0.0.1:9/' 6>$null | Out-Null; 'ran' } catch { $_.Exception.Message }
                }
                ConvertTo-Json -Compress -InputObject ([ordered]@{ edition = $PSVersionTable.PSEdition; out = $out })
                """, shell);
            if (run.GetProperty("edition").GetString() == "Core")
                run.GetProperty("out").GetString().Should().StartWith("Verify-Install.ps1 runs in Windows PowerShell 5.1", shell);
        }
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
    public void Pruning_keeps_the_newest_successful_upgrades_and_this_runs_and_ignores_what_the_upgrade_didnt_write()
    {
        var r = Run("""
            function S([string] $n, [bool] $ok = $true) { [pscustomobject]@{ Name = $n; Succeeded = $ok } }
            function P([string] $stamp) { "Tide-preupgrade-$stamp.bak" }
            $bak = Get-TidePreUpgradeBakPattern 'Tide'
            $dir = Get-TideUpgradeFolderPattern
            $baks = @(
                (S (P '20260105-090000')), (S (P '20260101-090000')), (S (P '20260103-090000')), (S (P '20260102-090000')), (S (P '20260104-090000')),
                (S 'Tide-test-preupgrade-20250101-090000.bak'),   # another database's
                (S 'TIDE-preupgrade-20250101-090000.bak'),        # another database's, on a case-sensitive server
                (S 'Tide-preupgrade-20251301-090000.bak'),        # not a real time
                (S 'Tide-full-20250101.bak'), (S 'Tide-preupgrade-20250101-090000.bak.old'), (S 'notes.txt'))
            $folders = @('20260101-090000', '20260102-090000', '20260103-090000', '20260104-090000', 'site-rollback', 'keep-me', '20260101-0900') |
                ForEach-Object { [pscustomobject]@{ Name = $_; FullName = "D:\inetpub\tide-upgrade-backups\$_"; Succeeded = $true } }
            $out = [ordered]@{
                newest   = @(Get-TidePrunable -Items $baks -Keep 3 -Pattern $bak | ForEach-Object { $_.Name })
                # This run's stamp sorts oldest (a clock set back): it's still never deleted.
                thisRun  = @(Get-TidePrunable -Items $baks -Keep 1 -Exclude @((P '20260101-090000')) -Pattern $bak | ForEach-Object { $_.Name })
                cased    = @(Get-TidePrunable -Items @((S 'TIDE-preupgrade-20260101-090000.bak'), (S 'Tide-preupgrade-20260102-090000.bak'), (S 'Tide-preupgrade-20260103-090000.bak')) -Keep 1 -Pattern $bak | ForEach-Object { $_.Name })
                folders  = @(Get-TidePrunable -Items $folders -Keep 2 -Exclude @('20260104-090000') -Pattern $dir | ForEach-Object { $_.FullName })
                fewer    = @(Get-TidePrunable -Items $baks -Keep 10 -Pattern $bak).Count
                none     = @(Get-TidePrunable -Items $baks -Keep 0 -Pattern $bak).Count
                empty    = @(Get-TidePrunable -Items @() -Keep 3 -Pattern $bak).Count
                escaped  = @(Get-TidePrunable -Items @((S 'TideXDb-preupgrade-20260101-090000.bak'), (S 'TideXDb-preupgrade-20260102-090000.bak')) -Keep 1 -Pattern (Get-TidePreUpgradeBakPattern 'Tide.Db')).Count
            }
            ConvertTo-Json -Compress -InputObject $out
            """);
        string[] Names(string p) => r.GetProperty(p).EnumerateArray().Select(x => x.GetString()!).ToArray();
        Names("newest").Should().Equal(["Tide-preupgrade-20260102-090000.bak", "Tide-preupgrade-20260101-090000.bak"],
            "the newest three stay, and nothing that isn't this database's pre-upgrade backup is picked");
        Names("thisRun").Should().Equal(["Tide-preupgrade-20260104-090000.bak", "Tide-preupgrade-20260103-090000.bak", "Tide-preupgrade-20260102-090000.bak"]);
        Names("cased").Should().Equal(["Tide-preupgrade-20260102-090000.bak"], "'TIDE' is another database on a case-sensitive server");
        Names("folders").Should().Equal(["D:\\inetpub\\tide-upgrade-backups\\20260102-090000", "D:\\inetpub\\tide-upgrade-backups\\20260101-090000"],
            "only the upgrade's own <time> folders count");
        r.GetProperty("fewer").GetInt32().Should().Be(0);
        r.GetProperty("none").GetInt32().Should().Be(0, "-KeepUpgradeBackups 0 keeps everything");
        r.GetProperty("empty").GetInt32().Should().Be(0);
        r.GetProperty("escaped").GetInt32().Should().Be(0, "a database name is matched literally, its dot included");
    }

    [Fact]
    public void Failed_upgrades_dont_use_up_the_keep_count_and_those_since_the_last_success_are_kept()
    {
        var r = Run("""
            function New-Run([string] $stamp, [bool] $ok) { [pscustomobject]@{ Name = $stamp; Succeeded = $ok } }
            $dir = Get-TideUpgradeFolderPattern
            # A success, three failed retries, then this run succeeds.
            $retries = @((New-Run '20260101-090000' $true), (New-Run '20260102-090000' $false), (New-Run '20260103-090000' $false), (New-Run '20260104-090000' $false), (New-Run '20260105-090000' $true))
            # Two more successes later.
            $later = $retries + @((New-Run '20260106-090000' $true), (New-Run '20260107-090000' $true))
            # Keep 1: the failed runs newer than the last success before this one still stay.
            $keepOne = @((New-Run '20260101-090000' $true), (New-Run '20260102-090000' $true), (New-Run '20260103-090000' $false), (New-Run '20260104-090000' $false), (New-Run '20260105-090000' $true))
            $out = [ordered]@{
                retries   = @(Get-TidePrunable -Items $retries -Keep 3 -Exclude @('20260105-090000') -Pattern $dir | ForEach-Object { $_.Name })
                later     = @(Get-TidePrunable -Items $later -Keep 3 -Exclude @('20260107-090000') -Pattern $dir | ForEach-Object { $_.Name })
                keepOne   = @(Get-TidePrunable -Items $keepOne -Keep 1 -Exclude @('20260105-090000') -Pattern $dir | ForEach-Object { $_.Name })
                # No success before this run: every failed run's stays. A bare name counts as a failed run's.
                noSuccess = @(Get-TidePrunable -Items @('20260101-090000', '20260102-090000', '20260103-090000') -Keep 1 -Exclude @('20260103-090000') -Pattern $dir).Count
            }
            ConvertTo-Json -Compress -InputObject $out
            """);
        string[] Names(string p) => r.GetProperty(p).EnumerateArray().Select(x => x.GetString()!).ToArray();
        Names("retries").Should().BeEmpty("the failed retries don't count toward three, and the first one's backup may be the only one from before its migrations");
        Names("later").Should().Equal(["20260104-090000", "20260103-090000", "20260102-090000", "20260101-090000"],
            "the newest three successes stay, and the failed runs are now older than the last success before this run");
        Names("keepOne").Should().Equal(["20260102-090000", "20260101-090000"]);
        r.GetProperty("noSuccess").GetInt32().Should().Be(0);
    }

    [Fact]
    public void Only_pre_upgrade_backups_this_servers_history_records_for_this_database_are_pruned()
    {
        var r = Run("""
            $history = @(
                'D:\SQLBackup\Tide-preupgrade-20260101-090000.bak', 'D:\SQLBackup\Tide-preupgrade-20260102-090000.bak',
                'd:\sqlbackup\Tide-preupgrade-20260103-090000.bak', 'D:\SQLBackup\Tide-preupgrade-20260104-090000.bak',
                'D:\SQLBackup\TIDE-preupgrade-20250101-090000.bak',   # a database whose name differs only in case
                'D:\SQLBackup\Tide-preupgrade-20250601-090000.bak',   # from before the records: no record names it
                'E:\Elsewhere\Tide-preupgrade-20251201-090000.bak')    # another folder
            $files = @(
                'Tide-preupgrade-20260101-090000.bak', 'Tide-preupgrade-20260102-090000.bak', 'Tide-preupgrade-20260103-090000.bak',
                'Tide-preupgrade-20260104-090000.bak', 'TIDE-preupgrade-20250101-090000.bak', 'Tide-preupgrade-20251201-090000.bak',
                'Tide-preupgrade-20250601-090000.bak',
                'Tide-preupgrade-20251101-090000.bak')               # another server's, sharing the folder
            $snaps = @('20260101-090000', '20260102-090000', '20260103-090000', '20260104-090000') |
                ForEach-Object { [pscustomobject]@{ Name = $_; Succeeded = $true; BakName = "Tide-preupgrade-$_.bak" } }
            $this = 'Tide-preupgrade-20260104-090000.bak'
            $out = [ordered]@{
                reachable   = @(Get-TidePrunableBaks -History $history -BakDir 'D:\SQLBackup\' -DbName 'Tide' -Files $files -Snapshots $snaps -Keep 2 -Exclude @($this))
                gone        = @(Get-TidePrunableBaks -History $history -BakDir 'D:\SQLBackup' -DbName 'Tide' -Files @($this) -Snapshots $snaps -Keep 1 -Exclude @($this)).Count
                unreachable = @(Get-TidePrunableBaks -History $history -BakDir 'D:\SQLBackup' -DbName 'Tide' -Snapshots $snaps -Keep 2 -Exclude @($this))
                noHistory   = @(Get-TidePrunableBaks -History @() -BakDir 'D:\SQLBackup' -DbName 'Tide' -Files $files -Snapshots $snaps -Keep 1 -Exclude @($this)).Count
                unrecorded  = @(Get-TidePrunableBaks -History $history -BakDir 'D:\SQLBackup' -DbName 'Tide' -Files $files -Keep 1 -Exclude @($this)).Count
                otherCase   = @(Get-TidePrunableBaks -History $history -BakDir 'D:\SQLBackup' -DbName 'TIDE' -Files $files -Snapshots $snaps -Keep 1).Count
                # A record's flag edited by hand to "true" or 1 isn't a success: those runs count as failed, newer than
                # the last real success (20260101), so they stay.
                handEdited  = @(Get-TidePrunableBaks -History $history -BakDir 'D:\SQLBackup' -DbName 'Tide' -Files $files -Keep 1 -Exclude @($this) -Snapshots @(
                    [pscustomobject]@{ Name = 'a'; Succeeded = $true; BakName = 'Tide-preupgrade-20260101-090000.bak' },
                    [pscustomobject]@{ Name = 'b'; Succeeded = 'true'; BakName = 'Tide-preupgrade-20260102-090000.bak' },
                    [pscustomobject]@{ Name = 'c'; Succeeded = 1; BakName = 'Tide-preupgrade-20260103-090000.bak' },
                    [pscustomobject]@{ Name = 'd'; Succeeded = $true; BakName = $this }))
            }
            ConvertTo-Json -Compress -InputObject $out
            """);
        string[] Names(string p) => r.GetProperty(p).EnumerateArray().Select(x => x.GetString()!).ToArray();
        Names("reachable").Should().Equal(["Tide-preupgrade-20260102-090000.bak", "Tide-preupgrade-20260101-090000.bak"],
            "a .bak this server's history doesn't record in the folder (another server's, another folder's), the database " +
            "named in another case, and one from before the records (older than every success, yet no record names it) stay");
        Names("handEdited").Should().Equal(["Tide-preupgrade-20260101-090000.bak"],
            "with only 20260101 a real success, it's the last success before this run: the hand-edited ones are newer and stay, " +
            "and 20260101 is past keep 1");
        r.GetProperty("gone").GetInt32().Should().Be(0, "a file the history names but that's no longer there isn't returned");
        Names("unreachable").Should().Equal(["Tide-preupgrade-20260102-090000.bak", "Tide-preupgrade-20260101-090000.bak"]);
        r.GetProperty("noHistory").GetInt32().Should().Be(0, "with no history, nothing is ours");
        r.GetProperty("unrecorded").GetInt32().Should().Be(0, "a backup no record names is never pruned");
        r.GetProperty("otherCase").GetInt32().Should().Be(0, "'TIDE' has one backup in the history, which is kept");
    }

    [Fact]
    public void A_records_succeeded_flag_counts_only_as_the_JSON_boolean_true()
    {
        var r = Run("""
            $root = Join-Path ([IO.Path]::GetTempPath()) ('tide-flags-' + [guid]::NewGuid().ToString('N'))
            try {
                $flags = [ordered]@{ '20260101-090000' = 'true'; '20260102-090000' = '"true"'; '20260103-090000' = '1'; '20260104-090000' = 'false' }
                foreach ($k in $flags.Keys) {
                    New-Item -ItemType Directory -Force -Path (Join-Path $root $k) | Out-Null
                    Set-Content -LiteralPath (Join-Path $root "$k\snapshot.json") -Encoding UTF8 -Value (
                        '{ "format": 1, "server": "WEB1", "site": "C:\\inetpub\\tide", "bak": "", "succeeded": ' + $flags[$k] + ' }')
                }
                $snaps = @(Get-TideUpgradeSnapshots -Root $root -SitePath 'C:\inetpub\tide' -Server 'WEB1')
            } finally { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue }
            $items = @(@{ Name = '20260101-090000'; Succeeded = $true }, @{ Name = '20260102-090000'; Succeeded = 'true' },
                @{ Name = '20260103-090000'; Succeeded = 1 }, @{ Name = '20260104-090000'; Succeeded = $true }) | ForEach-Object { [pscustomobject]$_ }
            $out = [ordered]@{
                snaps = @($snaps | ForEach-Object { "$($_.Name):$($_.Succeeded)" })
                # Keep 1, this run 20260104: with only 20260101 a success, the two between are a failed run's, newer than it.
                pruned = @(Get-TidePrunable -Items $items -Keep 1 -Exclude @('20260104-090000') -Pattern (Get-TideUpgradeFolderPattern) | ForEach-Object { $_.Name })
            }
            ConvertTo-Json -Compress -InputObject $out
            """);
        r.GetProperty("snaps").EnumerateArray().Select(x => x.GetString()).Should().Equal(
            "20260101-090000:True", "20260102-090000:False", "20260103-090000:False", "20260104-090000:False");
        r.GetProperty("pruned").EnumerateArray().Select(x => x.GetString()).Should().Equal(["20260101-090000"],
            "\"true\" and 1 don't count as successes, so those runs are kept as a failed run's newer than the last success");
    }

    [Fact]
    public void Only_a_proven_start_is_recorded_as_a_success_and_prunes_and_BackupRoot_prunes_nothing()
    {
        var r = Run("""
            function Show($p) { "$($p.MarkSucceeded):$($p.PruneBaks):$($p.PruneFolders):$([bool]$p.Note)" }
            $out = [ordered]@{
                proven     = Show (Get-TidePrunePlan -StartProven $true -CustomBackupRoot $false -Keep 3)
                unproven   = Show (Get-TidePrunePlan -StartProven $false -CustomBackupRoot $false -Keep 3)
                backupRoot = Show (Get-TidePrunePlan -StartProven $true -CustomBackupRoot $true -Keep 3)
                keepNone   = Show (Get-TidePrunePlan -StartProven $true -CustomBackupRoot $false -Keep 0)
            }
            ConvertTo-Json -Compress -InputObject $out
            """);
        r.GetProperty("proven").GetString().Should().Be("True:True:True:False");
        r.GetProperty("unproven").GetString().Should().Be("False:False:False:True",
            "a start taken on trust may yet need a rollback by hand: it isn't a success and deletes nothing");
        r.GetProperty("backupRoot").GetString().Should().Be("True:False:False:True",
            "a -BackupRoot run's records aren't with the others, so neither its .bak files nor its folders are pruned");
        r.GetProperty("keepNone").GetString().Should().Be("True:False:False:False");
    }

    [Fact]
    public void Only_this_sites_upgrade_folders_are_pruned_and_a_junction_inside_one_is_never_followed()
    {
        var r = Run("""
            $root = Join-Path ([IO.Path]::GetTempPath()) ('tide-upgrades-' + [guid]::NewGuid().ToString('N'))
            $outside = Join-Path ([IO.Path]::GetTempPath()) ('tide-target-' + [guid]::NewGuid().ToString('N'))
            # A junction on Windows (no admin needed); elsewhere pwsh makes nothing for -ItemType Junction, so a symbolic link.
            function New-Link([string] $path, [string] $target) {
                $kind = if ($PSVersionTable.PSEdition -eq 'Desktop' -or $IsWindows) { 'Junction' } else { 'SymbolicLink' }
                New-Item -ItemType $kind -Path $path -Value $target -ErrorAction Stop | Out-Null
            }
            function Add-Run([string] $stamp, [string] $site, [string] $server = 'WEB1', [switch] $Succeeded) {
                $d = Join-Path $root $stamp
                New-Item -ItemType Directory -Force -Path $d | Out-Null
                New-TideUpgradeRecord -Server $server -SitePath $site -IisPath 'TIDE' -DbName 'Tide' -BakName "Tide-preupgrade-$stamp.bak" -Started 'x' |
                    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $d 'snapshot.json') -Encoding UTF8
                if ($Succeeded) { Set-TideUpgradeSucceeded -Folder $d -Completed 'y' }
                return $d
            }
            try {
                $mine = Add-Run '20260101-090000' 'C:\inetpub\tide' -Succeeded
                New-Item -ItemType Directory -Force -Path (Join-Path $mine 'site-rollback') | Out-Null
                New-Item -ItemType Directory -Force -Path $outside | Out-Null
                Set-Content -LiteralPath (Join-Path $outside 'keep.txt') -Value 'not the snapshot''s'
                New-Link (Join-Path (Join-Path $mine 'site-rollback') 'link') $outside
                [void](Add-Run '20260102-090000' 'C:\inetpub\tide-test' -Succeeded)      # a sibling site's, sharing the folder
                [void](Add-Run '20260103-090000' 'C:\inetpub\tide' -Server 'WEB2' -Succeeded)   # another server's
                New-Item -ItemType Directory -Force -Path (Join-Path $root '20260104-090000') | Out-Null   # no record
                $bad = Join-Path $root '20260105-090000'
                New-Item -ItemType Directory -Force -Path $bad | Out-Null
                Set-Content -LiteralPath (Join-Path $bad 'snapshot.json') -Value '{ not json'
                [void](Add-Run '20260106-090000' 'C:\inetpub\tide\' -Succeeded)
                $failed = Add-Run '20260107-090000' 'c:\inetpub\TIDE'
                New-Link (Join-Path $root '20260108-090000') $mine   # a link to a run
                [void](Add-Run '20260109-090000' 'C:\inetpub\tide' -Succeeded)          # this run

                $snaps = @(Get-TideUpgradeSnapshots -Root $root -SitePath 'C:\inetpub\tide' -Server 'web1')
                $prune = @(Get-TidePrunable -Items $snaps -Keep 2 -Exclude @('20260109-090000') -Pattern (Get-TideUpgradeFolderPattern))
                foreach ($p in $prune) { Remove-TideTree $p.FullName }
                $out = [ordered]@{
                    snaps     = @($snaps | ForEach-Object { "$($_.Name):$($_.Succeeded):$($_.BakName)" })
                    pruned    = @($prune | ForEach-Object { $_.Name })
                    left      = @(Get-ChildItem -LiteralPath $root -Force | Sort-Object Name | ForEach-Object { $_.Name })
                    targetKept = (Test-Path -LiteralPath (Join-Path $outside 'keep.txt'))
                    linkTop   = $(try { Remove-TideTree (Join-Path $root '20260108-090000'); 'deleted' } catch { 'refused' })
                }
            } finally {
                Get-ChildItem -LiteralPath $root -Force -ErrorAction SilentlyContinue | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint } |
                    ForEach-Object { $l = $_.FullName; try { [IO.Directory]::Delete($l, $false) } catch { [IO.File]::Delete($l) } }
                Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
                Remove-Item -LiteralPath $outside -Recurse -Force -ErrorAction SilentlyContinue
            }
            ConvertTo-Json -Compress -InputObject $out
            """);
        string[] Names(string p) => r.GetProperty(p).EnumerateArray().Select(x => x.GetString()!).ToArray();
        Names("snaps").Should().Equal(
            ["20260101-090000:True:Tide-preupgrade-20260101-090000.bak", "20260106-090000:True:Tide-preupgrade-20260106-090000.bak",
             "20260107-090000:False:Tide-preupgrade-20260107-090000.bak", "20260109-090000:True:Tide-preupgrade-20260109-090000.bak"],
            "a sibling site's, another server's, one with no record or an unreadable one, and a link, aren't this site's");
        Names("pruned").Should().Equal(["20260101-090000"], "the failed run is newer than the last success before this one");
        Names("left").Should().Equal(["20260102-090000", "20260103-090000", "20260104-090000", "20260105-090000", "20260106-090000",
            "20260107-090000", "20260108-090000", "20260109-090000"]);
        r.GetProperty("targetKept").GetBoolean().Should().BeTrue("the junction inside the snapshot was removed as a link, not followed");
        r.GetProperty("linkTop").GetString().Should().Be("refused");
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
    public void The_restore_drill_never_takes_the_live_databases_name_whatever_the_instance_is_called()
    {
        // The drill drops its scratch database first. '.' (or localhost, or the FQDN) is the live instance by another name,
        // so the old check, the instance names compared as text, let -ScratchDatabase <live name> through to DROP DATABASE.
        var r = Run("""
            ConvertTo-Json -Compress -InputObject ([ordered]@{
                same      = Get-TideScratchRefusal 'Tide' 'Tide'
                otherCase = Get-TideScratchRefusal 'tide ' 'Tide'
                otherName = Get-TideScratchRefusal 'Tide_RestoreDrill' 'Tide'
                blank     = Get-TideScratchRefusal ' ' 'Tide'
            })
            """);
        r.GetProperty("same").GetString().Should().StartWith("The scratch database can't be named").And.Contain("Nothing has been changed");
        r.GetProperty("otherCase").GetString().Should().StartWith("The scratch database can't be named");
        r.GetProperty("otherName").ValueKind.Should().Be(JsonValueKind.Null);
        r.GetProperty("blank").GetString().Should().Contain("-ScratchDatabase is empty");
        DeployScriptTests.ReadText("deploy", "Test-TideRestore.ps1").Should().NotContain("$SqlInstance -eq $live.Instance",
            "the instance's name proves nothing");
    }

    [Fact]
    public void The_drill_drops_only_a_scratch_database_a_drill_restored_and_marks_each_one_it_restores()
    {
        // On a shared instance -ScratchDatabase Tide_Site2 could name another install's live database, which the drill
        // used to drop as "left by an earlier drill".
        var r = Run("""
            ConvertTo-Json -Compress -InputObject ([ordered]@{
                marked   = Get-TideScratchDropRefusal -Scratch 'Tide_Site2' -SqlInstance 'SQLHOST01' -Marked $true
                unmarked = Get-TideScratchDropRefusal -Scratch 'Tide_Site2' -SqlInstance 'SQLHOST01' -Marked $false
                mark     = Get-TideDrillMarkerSql 'Tide]Drill'
                query    = Get-TideDrillMarkerQuery 'Tide]Drill'
            })
            """);
        r.GetProperty("marked").ValueKind.Should().Be(JsonValueKind.Null);
        r.GetProperty("unmarked").GetString().Should().Contain("SQLHOST01 already has a database named Tide_Site2")
            .And.Contain("TideRestoreDrill").And.Contain("Nothing has been changed");
        r.GetProperty("mark").GetString().Should().Be("EXEC [Tide]]Drill].sys.sp_addextendedproperty @name = N'TideRestoreDrill', @value = N'1';");
        r.GetProperty("query").GetString().Should().Be("SELECT COUNT(*) FROM [Tide]]Drill].sys.extended_properties WHERE class = 0 AND name = N'TideRestoreDrill'");

        // The order in the script: the mark is checked before the DROP, and set straight after the restore.
        var drill = DeployScriptTests.ReadText("deploy", "Test-TideRestore.ps1");
        int At(string s) { var i = drill.IndexOf(s, StringComparison.Ordinal); i.Should().BeGreaterThan(-1, s); return i; }
        var check = At("Get-TideDrillMarkerQuery $ScratchDatabase");
        var refuse = At("if ($dropRefusal) { throw $dropRefusal }");
        var drop = At("Removing the scratch database left by an earlier drill");
        var restore = At("\"RESTORE DATABASE $(Get-SqlName $ScratchDatabase)");
        var mark = At("Get-TideDrillMarkerSql $ScratchDatabase");
        var dbcc = At("DBCC CHECKDB(");
        new[] { check, refuse, drop, restore, mark, dbcc }.Should().BeInAscendingOrder();
    }

    [WindowsFact]
    public void The_restore_drill_refuses_the_live_databases_name_before_it_connects_anywhere()
    {
        // The script itself, under each PowerShell here, with the instance spelled three ways. It throws before opening a
        // connection, so no SQL Server is needed. The drill reads the connection string with SqlClient, as on the server.
        foreach (var shell in Shells())
        {
            var r = Run("""
                $dir = Join-Path ([IO.Path]::GetTempPath()) ('tide-drill-' + [guid]::NewGuid().ToString('N'))
                New-Item -ItemType Directory -Path $dir | Out-Null
                try {
                    Set-Content -Path (Join-Path $dir 'appsettings.Production.json') -Value '{ "Database": { "Provider": "SqlServer" }, "ConnectionStrings": { "Default": "Server=SQLHOST01;Database=Tide;Integrated Security=true" } }'
                    $drill = { param($scratch, $instance) try { & (Join-Path $deploy 'Test-TideRestore.ps1') -SitePath $dir -BackupSet $dir -ScratchDatabase $scratch -SqlInstance $instance 6>$null | Out-Null; 'ran' } catch { $_.Exception.Message } }
                    $out = [ordered]@{ dot = & $drill 'Tide' '.'; fqdn = & $drill 'tide' 'sqlhost01.contoso.com'; same = & $drill 'Tide' 'SQLHOST01' }
                } finally { Remove-Item -LiteralPath $dir -Recurse -Force }
                ConvertTo-Json -Compress -InputObject $out
                """, shell);
            foreach (var refused in new[] { "dot", "fqdn", "same" })
                r.GetProperty(refused).GetString().Should().StartWith("The scratch database can't be named", "{0} under {1}", refused, shell);
        }
    }

    [Fact]
    public void The_drill_checks_the_key_ring_only_in_a_set_whose_backup_had_one_to_take()
    {
        // Without DataProtection:KeyPath, Backup-Tide.ps1 has no key ring to copy, and the drill used to fail every run.
        var r = Run("""
            $set = { param($folders) ('{ "format": 1, "folders": ' + $folders + ' }') | ConvertFrom-Json }
            $site = { param($keyPath) [pscustomobject]@{ Folder = 'D:\inetpub\tide'; Layers = @([pscustomobject]@{ DataProtection = [pscustomobject]@{ KeyPath = $keyPath } }) } }
            $noKeys = & $site ''
            $keys = & $site 'E:\TideData\dp-keys'
            ConvertTo-Json -Compress -InputObject ([ordered]@{
                withRing   = Test-TideSetHasKeyRing (& $set '[{ "label": "files" }, { "label": "dp-keys" }]') $noKeys
                notThere   = Test-TideSetHasKeyRing (& $set '[{ "label": "files" }, { "label": "dp-keys", "note": "not there" }]') $noKeys
                withoutKey = Test-TideSetHasKeyRing (& $set '[{ "label": "files" }]') $keys
                noFolders  = Test-TideSetHasKeyRing (& $set '[]') $keys
                oldSetSet  = Test-TideSetHasKeyRing ('{ "format": 1 }' | ConvertFrom-Json) $keys
                oldSetNone = Test-TideSetHasKeyRing ('{ "format": 1 }' | ConvertFrom-Json) $noKeys
            })
            """);
        r.GetProperty("withRing").GetBoolean().Should().BeTrue();
        r.GetProperty("notThere").GetBoolean().Should().BeTrue("a key path set but not found is a real failure");
        r.GetProperty("withoutKey").GetBoolean().Should().BeFalse("the set records what its backup had, not what the site has now");
        r.GetProperty("noFolders").GetBoolean().Should().BeFalse();
        r.GetProperty("oldSetSet").GetBoolean().Should().BeTrue("a set that lists no folders goes by the site's setting");
        r.GetProperty("oldSetNone").GetBoolean().Should().BeFalse();

        var drill = DeployScriptTests.ReadText("deploy", "Test-TideRestore.ps1");
        drill.Should().Contain("if (Test-TideSetHasKeyRing $manifest $site) {").And.Contain("Add-Check 'dp-keys' $true -Skipped");
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
    public void The_pool_is_set_to_one_worker_process_read_back_and_a_value_that_doesnt_take_is_reported()
    {
        // Stand-ins for the IIS provider: Set-ItemProperty records what it's asked; Get-Item answers the pool's
        // maxProcesses, first as a ConfigurationAttribute-like object (with .Value), as the provider can. Under each
        // PowerShell here, since the installer and the upgrade run under Windows PowerShell 5.1.
        foreach (var shell in Shells())
        {
            var r = Run("""
                . (Join-Path (Join-Path $deploy 'lib') 'TideIis.ps1')
                $script:max = [pscustomobject]@{ Value = [long]4 }
                $script:sticks = $true
                $script:calls = @()
                function Get-Item([string] $Path) { [pscustomobject]@{ processModel = [pscustomobject]@{ maxProcesses = $script:max } } }
                function Set-ItemProperty([string] $Path, [string] $Name, $Value) {
                    $script:calls += "$Path $Name=$Value"
                    if ($script:sticks) { $script:max = $Value }
                }
                $set = Set-TideSingleWorkerProcess -Name 'TIDE'
                $script:max = 2; $script:sticks = $false
                $locked = Set-TideSingleWorkerProcess -Name 'TIDE'
                ConvertTo-Json -Compress -InputObject ([ordered]@{
                    calls = @($script:calls); before = $set.Before; after = $set.After; ok = $set.Ok
                    lockedAfter = $locked.After; lockedOk = $locked.Ok })
                """, shell);
            r.GetProperty("calls")[0].GetString().Should().Be(@"IIS:\AppPools\TIDE processModel.maxProcesses=1");
            (r.GetProperty("before").GetInt32(), r.GetProperty("after").GetInt32(), r.GetProperty("ok").GetBoolean()).Should().Be((4, 1, true), shell);
            (r.GetProperty("lockedAfter").GetInt32(), r.GetProperty("lockedOk").GetBoolean()).Should().Be((2, false), shell);
        }

        // The installer stops on a pool that won't take it; the upgrade (its new build already in place) warns.
        var install = File.ReadAllText(Path.Combine(DeployScriptTests.Deploy, "Install-Tide.ps1"));
        var upgrade = File.ReadAllText(Path.Combine(DeployScriptTests.Deploy, "Upgrade-Tide.ps1"));
        install.Should().MatchRegex(@"Set-TideSingleWorkerProcess -Name \$AppPoolName\s+if \(-not \$single\.Ok\) \{\s+throw");
        upgrade.Should().MatchRegex(@"Set-TideSingleWorkerProcess -Name \$AppPoolName\s+if \(-not \$single\.Ok\) \{[^}]*Write-Warn2");
        upgrade.IndexOf("Set-TideSingleWorkerProcess", StringComparison.Ordinal).Should().BePositive()
            .And.BeLessThan(upgrade.IndexOf("Write-Step \"Starting app pool\"", StringComparison.Ordinal), "it's set while the pool is stopped");
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

    [WindowsFact]
    public void A_path_with_a_space_and_a_trailing_backslash_reaches_robocopy_and_a_scheduled_task_whole()
    {
        // Windows PowerShell 5.1 quoted "D:\TIDE drop\" with the backslash escaping the closing quote: robocopy exited 16,
        // and a task's -Destination took the rest of its command line. The task's line is read here by powershell.exe
        // -File, as Task Scheduler starts it. robocopy and Task Scheduler are Windows only.
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
    public void ApplySchema_runs_the_bundles_schema_script_in_the_database_and_refuses_before_anything_when_it_is_missing()
    {
        // A bundle's layout (tide-<version>\deploy\sql) in a temp folder, with stand-ins for Invoke-Sqlcmd (SqlServer 22) and
        // sqlcmd.exe that record the script each step runs. The schema script is EF's idempotent one: GO batches, and text
        // that isn't a sqlcmd template, so $(...) must not be read as a variable. Nothing connects anywhere.
        var r = Run("""
            $root = Join-Path ([IO.Path]::GetTempPath()) ('tide-bundle-' + [guid]::NewGuid().ToString('N'))
            $bundleDeploy = Join-Path $root 'tide-v9.9.9\deploy'
            New-Item -ItemType Directory -Path (Join-Path $bundleDeploy 'sql') | Out-Null
            Copy-Item (Join-Path $deploy 'Install-Database.ps1') $bundleDeploy
            Copy-Item (Join-Path $deploy 'sql\01-Create-Database.sql') (Join-Path $bundleDeploy 'sql')
            $schema = Join-Path $bundleDeploy 'sql\tide-schema-sqlserver.sql'
            $install = Join-Path $bundleDeploy 'Install-Database.ps1'
            $go = @{ SqlInstance = 'SQLHOST'; AppAccount = 'CONTOSO\svc-tide$'; SchemaMode = 'DbaApplies'; ApplySchema = $true }

            New-Item -Path function: -Name Invoke-Sqlcmd -Force -Value {
                [CmdletBinding()] param($ServerInstance, $Query, $InputFile, $Variable, $Database, $QueryTimeout, $OutputSqlErrors,
                    [switch] $AbortOnError, [string] $Encrypt, [switch] $TrustServerCertificate, [switch] $DisableVariables, [switch] $DisableCommands)
                if ($Query) { return [pscustomobject]@{ RunAs = 'CONTOSO\dba'; IsSysadmin = 1; CanCreateDb = 1; CanCreateLogin = 1; DbExists = 0; LoginExists = 0; ControlsDb = 0 } }
                $global:sqlLog += "$([IO.Path]::GetFileName($InputFile)) db=$Database novars=$([bool]$DisableVariables) nocmds=$([bool]$DisableCommands)"
            } | Out-Null
            $out = [ordered]@{}
            try {
                $global:sqlLog = @()
                $out.missing = try { & $install @go 6>$null | Out-Null; 'ok' } catch { $_.Exception.Message }
                $out.missingCalls = @($global:sqlLog)

                Set-Content -LiteralPath $schema -Value "SELECT N'`$(not a variable)';`nGO`n"
                $global:sqlLog = @()
                $out.module = try { & $install @go 6>$null | Out-Null; 'ok' } catch { $_.Exception.Message }
                $out.moduleCalls = @($global:sqlLog)

                # An Invoke-Sqlcmd that can't encrypt: every step goes to sqlcmd.exe.
                New-Item -Path function: -Name Invoke-Sqlcmd -Force -Value { param($ServerInstance) } | Out-Null
                New-Item -Path function: -Name sqlcmd.exe -Force -Value {
                    if ($args -contains '-Q') { 'CONTOSO\dba|1|1|1|0|0|0' }
                    elseif ($args -contains '-i') { $global:sqlLog += ($args | ForEach-Object { if ("$_" -like '*.sql') { [IO.Path]::GetFileName("$_") } else { "$_" } }) -join ' ' }
                    $global:LASTEXITCODE = 0
                } | Out-Null
                $global:sqlLog = @()
                $out.sqlcmd = try { & $install @go 6>$null | Out-Null; 'ok' } catch { $_.Exception.Message }
                $out.sqlcmdCalls = @($global:sqlLog)
            } finally { Remove-Item -LiteralPath $root -Recurse -Force }
            ConvertTo-Json -Depth 4 -Compress -InputObject $out
            """);
        string[] Calls(string name) => r.GetProperty(name).EnumerateArray().Select(x => x.GetString()!).ToArray();

        r.GetProperty("missing").GetString().Should().StartWith("Missing ").And.Contain("tide-schema-sqlserver.sql")
            .And.Contain("Nothing was changed");
        Calls("missingCalls").Should().BeEmpty("the database isn't created when its schema can't follow");

        r.GetProperty("module").GetString().Should().Be("ok");
        Calls("moduleCalls").Should().Equal("01-Create-Database.sql db= novars=False nocmds=False", "tide-schema-sqlserver.sql db=Tide novars=True nocmds=True");

        r.GetProperty("sqlcmd").GetString().Should().Be("ok");
        Calls("sqlcmdCalls").Should().HaveCount(2);
        Calls("sqlcmdCalls")[1].Should().Contain("-d Tide").And.Contain("-i tide-schema-sqlserver.sql").And.Contain(" -x ").And.Contain(" -X1 ").And.Contain(" -I ").And.Contain(" -b ")
            .And.Contain(" -f i:65001 ", "the script has non-ASCII text, read as UTF-8 whatever the console's code page");
    }

    [WindowsFact]
    public void An_upgrade_points_this_sites_scheduled_tasks_at_the_kept_scripts_and_leaves_another_sites_alone()
    {
        // Stand-ins for the ScheduledTasks cmdlets: the tasks as Backup-Tide.ps1 -Register writes them, with the site
        // path quoted with and without a trailing backslash, another site's task, and one already on the kept copy.
        // Scheduled tasks and their Windows paths are Windows only.
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
