using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace Tide.UnitTests;

/// <summary>
/// The WebSocket checks in the deploy scripts. Blazor Server falls back to long polling (a request per keystroke) without
/// IIS's WebSocket Protocol feature, so the installer checks and offers it, Verify-Install fails without it, and the
/// upgrade warns.
/// </summary>
public partial class DeployScriptTests
{
    [Fact]
    public void The_installer_checks_the_WebSocket_feature_first_installs_it_only_when_asked_and_otherwise_warns()
    {
        var install = Read("deploy", "Install-Tide.ps1");
        install.Should().Contain(". (Join-Path $deployDir 'lib\\TideWebSockets.ps1')");
        install.Should().Contain("[switch] $InstallWebSockets");
        // Read from the answers file like the other switch, refused unless $true or $false.
        install.Should().Contain("foreach ($sw in 'ValidateServerCertificate', 'InstallWebSockets')");
        var check = install.IndexOf("$ws = Test-TideWebSockets", StringComparison.Ordinal);
        check.Should().BePositive();
        check.Should().BeLessThan(install.IndexOf("# --- 1. Publish ---", StringComparison.Ordinal), "the check runs before anything is published or changed");
        var block = install[check..install.IndexOf("# --- 1. Publish ---", StringComparison.Ordinal)];
        block.Should().Contain("$Host.UI.PromptForChoice", "an interactive run asks");
        block.Should().Contain("$unattended = [bool]$ConfigFile -or -not [Environment]::UserInteractive");
        block.Should().Contain("WARNING: carrying on without it").And.Contain("Get-TideWebSocketFixText").And.Contain("-InstallWebSockets",
            "an unattended run warns with the fix and carries on");
        block.Should().NotContain("throw (\"The IIS WebSocket", "a missing feature never stops an unattended install");
        block.Should().Contain("TIDE keeps each open page connected to the server over a WebSocket", "the warning says why it's needed");
        block.Should().Contain("Install-TideWebSocketFeature -Feature $wsFeature");
        block.Should().Contain("RestartNeeded", "it says when Windows wants a restart");
        // After the site exists: on for TIDE's site, the server-wide value left alone.
        install.Should().Contain("Enable-TideWebSocketConfig -IisPath $iisPath -SitePath $SitePath -Appcmd $appcmd");
        Read("deploy", "tide.config.template.psd1").Should().Contain("InstallWebSockets = $false");
    }

    [Fact]
    public void The_lib_uses_the_server_and_the_client_cmdlets_and_turns_WebSockets_on_with_appcmd()
    {
        var lib = Read("deploy", "lib", "TideWebSockets.ps1");
        lib.Should().Contain("Get-WindowsFeature -Name $name").And.Contain("Name = 'Web-WebSockets'");
        lib.Should().Contain("Get-WindowsOptionalFeature -Online -FeatureName $name").And.Contain("Name = 'IIS-WebSockets'");
        lib.Should().Contain("Install-WindowsFeature -Name Web-WebSockets");
        lib.Should().Contain("Enable-WindowsOptionalFeature -Online -FeatureName IIS-WebSockets -All -NoRestart");
        lib.Should().Contain("/section:system.webServer/webSocket /enabled:true /commit:apphost");
        lib.Should().Contain("-Filter 'system.webServer/webSocket'");
        lib.Should().Contain("Install-WindowsFeature Web-WebSockets").And.Contain("passes the Upgrade and Connection headers");
        lib.Should().Contain("/_blazor/negotiate?negotiateVersion=1").And.Contain("UseDefaultCredentials");
    }

    [Fact]
    public void Verify_Install_fails_without_WebSockets_and_tries_a_real_handshake()
    {
        var verify = Read("deploy", "Verify-Install.ps1");
        verify.Should().Contain("[switch] $SkipWebSocketHandshake");
        verify.Should().Contain("Report $ws.FeatureOk \"The IIS WebSocket Protocol feature");
        verify.Should().Contain("Test-TideWebSockets -IisPath $iisPath -SitePath $physical");
        verify.Should().Contain("Test-TideWebSocketHandshake -BaseUrl $base");
        verify.Should().Contain("$wsFix = Get-TideWebSocketFixText");
    }

    [Fact]
    public void The_upgrade_only_warns_about_WebSockets()
    {
        var upgrade = Read("deploy", "Upgrade-Tide.ps1");
        upgrade.Should().Contain(". (Join-Path $deployDir 'lib\\TideWebSockets.ps1')");
        var from = upgrade.IndexOf("# 3a'. WEBSOCKETS", StringComparison.Ordinal);
        from.Should().BePositive();
        var block = upgrade[from..upgrade.IndexOf("# 3b. SQL reachable.", StringComparison.Ordinal)];
        block.Should().Contain("Test-TideWebSockets -IisPath $iisPath").And.Contain("Write-Warn2");
        block.Should().NotContain("throw", "WebSockets never block an upgrade");
    }

    [Fact]
    public void The_shipped_web_config_leaves_WebSockets_on_and_the_fix_reads_the_same_in_the_app()
    {
        Read("src", "Tide.Web", "web.config").Should().NotContainEquivalentOf("<webSocket enabled=\"false\"");
        Read("src", "Tide.Web", "Services", "ConnectionTransport.cs").Should().Contain("Install-WindowsFeature Web-WebSockets")
            .And.Contain("passes the Upgrade and Connection headers");
    }

    [Fact]
    public void The_docs_list_the_WebSocket_feature()
    {
        var install = Read("docs", "INSTALL.md");
        install.Should().Contain("Web-WebSockets").And.Contain("-InstallWebSockets").And.Contain("101");
        Read("docs", "UPGRADE.md").Should().Contain("Web-WebSockets");
    }
}

public partial class DeployScriptPowerShellTests
{
    private const string WebSocketsLib = ". (Join-Path $deploy 'lib\\TideWebSockets.ps1')\n";

    [Fact]
    public void The_WebSocket_switches_default_off()
    {
        var r = Run("""
            $out = @{}
            foreach ($name in 'Install-Tide.ps1', 'Verify-Install.ps1') {
                $ast = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $deploy $name), [ref]$null, [ref]$null)
                $out[$name] = @($ast.ParamBlock.Parameters | Where-Object { $_.Name.VariablePath.UserPath -in 'InstallWebSockets', 'SkipWebSocketHandshake' } | ForEach-Object {
                    [pscustomobject]@{ Name = $_.Name.VariablePath.UserPath; Type = $_.StaticType.Name; Default = $(if ($_.DefaultValue) { $_.DefaultValue.Extent.Text } else { $null }) } })
            }
            ConvertTo-Json -Depth 4 -Compress -InputObject $out
            """);
        foreach (var (script, name) in new[] { ("Install-Tide.ps1", "InstallWebSockets"), ("Verify-Install.ps1", "SkipWebSocketHandshake") })
        {
            var p = r.GetProperty(script).EnumerateArray().Single(x => x.GetProperty("Name").GetString() == name);
            p.GetProperty("Type").GetString().Should().Be("SwitchParameter");
            p.GetProperty("Default").ValueKind.Should().Be(JsonValueKind.Null, "{0} is off unless passed", name);
        }
    }

    [Fact]
    public void The_feature_is_read_with_the_server_or_the_client_cmdlet()
    {
        var r = Run(WebSocketsLib + """
            $script:feature = $null; $script:optional = $null; $script:fails = $false; $script:moduleFile = $false
            function Get-WindowsFeature { [CmdletBinding()] param($Name) if ($script:fails) { throw 'no ServerManager' }; $script:feature }
            function Get-WindowsOptionalFeature { [CmdletBinding()] param([switch] $Online, $FeatureName) if ($script:fails) { throw 'The requested operation requires elevation.' }; $script:optional }
            function Test-TideWebSocketModuleFile { $script:moduleFile }
            function Get-CimInstance { [CmdletBinding()] param($ClassName) [pscustomobject]@{ ProductType = $script:productType } }
            $out = [ordered]@{}
            $script:feature = [pscustomobject]@{ Installed = $true };  $out.serverOn = Get-TideWebSocketFeature -Kind Server
            $script:feature = [pscustomobject]@{ Installed = $false }; $out.serverOff = Get-TideWebSocketFeature -Kind Server
            $script:optional = [pscustomobject]@{ State = 'Enabled' };  $out.clientOn = Get-TideWebSocketFeature -Kind Client
            $script:optional = [pscustomobject]@{ State = 'Disabled' }; $out.clientOff = Get-TideWebSocketFeature -Kind Client
            $script:fails = $true; $script:moduleFile = $true; $out.fallback = Get-TideWebSocketFeature -Kind Client
            $script:productType = 1; $out.kindClient = Get-TideWindowsKind
            $script:productType = 3; $out.kindServer = Get-TideWindowsKind
            ConvertTo-Json -Depth 4 -Compress -InputObject $out
            """);
        r.GetProperty("serverOn").GetProperty("Installed").GetBoolean().Should().BeTrue();
        r.GetProperty("serverOn").GetProperty("Name").GetString().Should().Be("Web-WebSockets");
        r.GetProperty("serverOn").GetProperty("InstallCommand").GetString().Should().Be("Install-WindowsFeature Web-WebSockets");
        r.GetProperty("serverOff").GetProperty("Installed").GetBoolean().Should().BeFalse();
        r.GetProperty("clientOn").GetProperty("Installed").GetBoolean().Should().BeTrue();
        r.GetProperty("clientOn").GetProperty("Name").GetString().Should().Be("IIS-WebSockets");
        r.GetProperty("clientOn").GetProperty("InstallCommand").GetString().Should().Be("Enable-WindowsOptionalFeature -Online -FeatureName IIS-WebSockets -All");
        r.GetProperty("clientOff").GetProperty("Installed").GetBoolean().Should().BeFalse();
        r.GetProperty("fallback").GetProperty("Installed").GetBoolean().Should().BeTrue("iiswsock.dll answers when the cmdlet can't run unelevated");
        r.GetProperty("fallback").GetProperty("Detail").GetString().Should().Contain("iiswsock.dll").And.Contain("requires elevation");
        r.GetProperty("kindClient").GetString().Should().Be("Client");
        r.GetProperty("kindServer").GetString().Should().Be("Server");
    }

    [Fact]
    public void The_feature_is_installed_with_the_matching_cmdlet_and_says_when_a_restart_is_needed()
    {
        var r = Run(WebSocketsLib + """
            $script:calls = @()
            function Install-WindowsFeature { [CmdletBinding()] param($Name) $script:calls += "Install-WindowsFeature $Name"; [pscustomobject]@{ Success = $true; RestartNeeded = 'Maybe' } }
            function Enable-WindowsOptionalFeature { [CmdletBinding()] param([switch] $Online, $FeatureName, [switch] $All, [switch] $NoRestart)
                $script:calls += "Enable-WindowsOptionalFeature $FeatureName All=$All NoRestart=$NoRestart"; [pscustomobject]@{ RestartNeeded = $true } }
            $out = [ordered]@{
                server = Install-TideWebSocketFeature -Feature ([pscustomobject]@{ Kind = 'Server' })
                client = Install-TideWebSocketFeature -Feature ([pscustomobject]@{ Kind = 'Client' })
            }
            $out.calls = $script:calls
            ConvertTo-Json -Depth 4 -Compress -InputObject $out
            """);
        r.GetProperty("server").GetProperty("Installed").GetBoolean().Should().BeTrue();
        r.GetProperty("server").GetProperty("RestartNeeded").GetString().Should().Be("Maybe");
        r.GetProperty("client").GetProperty("RestartNeeded").GetString().Should().Be("Yes");
        r.GetProperty("calls").EnumerateArray().Select(c => c.GetString()).Should().Equal(
            "Install-WindowsFeature Web-WebSockets", "Enable-WindowsOptionalFeature IIS-WebSockets All=True NoRestart=True");
    }

    [Fact]
    public void Where_WebSockets_are_turned_off_is_found_and_judged()
    {
        var r = Run(WebSocketsLib + """
            $dir = Join-Path ([IO.Path]::GetTempPath()) ("tide-ws-" + [guid]::NewGuid().ToString('N'))
            New-Item -ItemType Directory -Path $dir | Out-Null
            try {
                $f = Join-Path $dir 'web.config'
                $out = [ordered]@{ missing = Get-TideWebConfigWebSocket $f }
                Set-Content -Path $f -Value '<configuration><system.webServer><aspNetCore startupTimeLimit="600" /></system.webServer></configuration>'
                $out.unset = Get-TideWebConfigWebSocket $f
                Set-Content -Path $f -Value '<configuration><system.webServer><webSocket enabled="false" /></system.webServer></configuration>'
                $out.off = Get-TideWebConfigWebSocket $f
                Set-Content -Path $f -Value '<configuration><location path="."><system.webServer><webSocket enabled="False" /></system.webServer></location></configuration>'
                $out.offInLocation = Get-TideWebConfigWebSocket $f
                Set-Content -Path $f -Value '<configuration><location path="health"><system.webServer><webSocket enabled="false" /></system.webServer></location></configuration>'
                $out.offElsewhere = Get-TideWebConfigWebSocket $f
                Set-Content -Path $f -Value '<configuration><system.webServer><webSocket enabled="true" /></system.webServer></configuration>'
                $out.on = Get-TideWebConfigWebSocket $f
            } finally { Remove-Item -Recurse -Force $dir }
            # What IIS uses: web.config over the site's entry, over the server's; 'true' when nothing sets it.
            function Read-TideWebSocketSetting([string] $Location) { if ($Location) { $script:site } else { $script:server } }
            function Get-TideWebConfigWebSocket([string] $Path) { $script:webConfig }
            function Effective($server, $site, $webConfig) {
                $script:server = $server; $script:site = $site; $script:webConfig = $webConfig
                (Get-TideWebSocketConfig -IisPath 'TIDE' -SitePath 'C:\site').Effective
            }
            $out.effectiveDefault = Effective $null $null $null
            $out.effectiveServerOff = Effective 'false' $null $null
            $out.effectiveSiteWins = Effective 'false' 'true' $null
            $out.effectiveWebConfigWins = Effective 'true' 'true' 'false'
            $on = [pscustomobject]@{ Name = 'Web-WebSockets'; Installed = $true; InstallCommand = 'Install-WindowsFeature Web-WebSockets' }
            $off = [pscustomobject]@{ Name = 'Web-WebSockets'; Installed = $false; InstallCommand = 'Install-WindowsFeature Web-WebSockets' }
            $unknown = [pscustomobject]@{ Name = 'Web-WebSockets'; Installed = $null; CheckCommand = 'Get-WindowsFeature Web-WebSockets'; Detail = 'no ServerManager' }
            $out.ok = Get-TideWebSocketVerdict -Feature $on -Config ([pscustomobject]@{ Server = $null; Site = $null; WebConfig = $null; Effective = 'true' })
            $out.notInstalled = Get-TideWebSocketVerdict -Feature $off
            $out.unknown = Get-TideWebSocketVerdict -Feature $unknown
            $out.siteOff = Get-TideWebSocketVerdict -Feature $on -Config ([pscustomobject]@{ Server = 'true'; Site = 'false'; WebConfig = $null; Effective = 'false' })
            $out.serverOff = Get-TideWebSocketVerdict -Config ([pscustomobject]@{ Server = 'false'; Site = 'false'; WebConfig = $null; Effective = 'false' })
            $out.webConfigOff = Get-TideWebSocketVerdict -Config ([pscustomobject]@{ Server = $null; Site = $null; WebConfig = 'false'; Effective = 'false' })
            $out.url = Get-TideBlazorSocketUrl 'https://intranet.contoso.com/tide/' 'a+b/c='
            $out.plainUrl = Get-TideBlazorSocketUrl 'http://tide.contoso.com' ''
            $out.fix = Get-TideWebSocketFixText
            ConvertTo-Json -Depth 4 -Compress -InputObject $out
            """);
        r.GetProperty("missing").ValueKind.Should().Be(JsonValueKind.Null);
        r.GetProperty("unset").ValueKind.Should().Be(JsonValueKind.Null);
        r.GetProperty("off").GetString().Should().Be("false");
        r.GetProperty("offInLocation").GetString().Should().Be("false");
        r.GetProperty("offElsewhere").ValueKind.Should().Be(JsonValueKind.Null, "a <location> for another path isn't the site's");
        r.GetProperty("on").GetString().Should().Be("true");
        r.GetProperty("effectiveDefault").GetString().Should().Be("true");
        r.GetProperty("effectiveServerOff").GetString().Should().Be("false");
        r.GetProperty("effectiveSiteWins").GetString().Should().Be("true");
        r.GetProperty("effectiveWebConfigWins").GetString().Should().Be("false");

        string[] Problems(string name) => r.GetProperty(name).GetProperty("Problems") is var p && p.ValueKind == JsonValueKind.Array
            ? [.. p.EnumerateArray().Select(x => x.GetString()!)] : p.ValueKind == JsonValueKind.String ? [p.GetString()!] : [];
        r.GetProperty("ok").GetProperty("Ok").GetBoolean().Should().BeTrue();
        Problems("ok").Should().BeEmpty();
        r.GetProperty("notInstalled").GetProperty("Ok").GetBoolean().Should().BeFalse();
        Problems("notInstalled").Should().ContainSingle().Which.Should().Contain("Install-WindowsFeature Web-WebSockets");
        Problems("unknown").Should().ContainSingle().Which.Should().Contain("Get-WindowsFeature Web-WebSockets").And.Contain("no ServerManager");
        Problems("siteOff").Should().ContainSingle().Which.Should().Contain("for the site").And.Contain("/enabled:true /commit:apphost");
        Problems("serverOff").Should().ContainSingle().Which.Should().Contain("server-wide");
        Problems("webConfigOff").Should().ContainSingle().Which.Should().Contain("web.config");
        r.GetProperty("url").GetString().Should().Be("wss://intranet.contoso.com/tide/_blazor?id=a%2Bb%2Fc%3D");
        r.GetProperty("plainUrl").GetString().Should().Be("ws://tide.contoso.com/_blazor");
        r.GetProperty("fix").GetString().Should().Contain("Install-WindowsFeature Web-WebSockets").And.Contain("Upgrade and Connection headers");
    }

    [Fact]
    public void The_fix_text_is_the_same_in_the_scripts_and_in_Diagnostics()
    {
        var r = Run(WebSocketsLib + """
            ConvertTo-Json -Compress -InputObject @{ fix = Get-TideWebSocketFixText }
            """);
        var source = File.ReadAllText(Path.Combine(DeployScriptTests.RepoRoot(), "src", "Tide.Web", "Services", "ConnectionTransport.cs"));
        var declaration = Regex.Match(source, @"public const string Fix =(?<parts>(?:\s*\+?\s*""[^""]*"")+)\s*;");
        declaration.Success.Should().BeTrue("ConnectionTransport.Fix is a constant of string literals");
        var fix = string.Concat(Regex.Matches(declaration.Groups["parts"].Value, @"""([^""]*)""").Select(m => m.Groups[1].Value));
        fix.Should().Be(r.GetProperty("fix").GetString(), "Administration > Diagnostics says what Get-TideWebSocketFixText says");
        fix.Should().Contain("Install-WindowsFeature Web-WebSockets").And.Contain("Enable-WindowsOptionalFeature -Online -FeatureName IIS-WebSockets -All");
    }

    [Fact]
    public void Test_TideWebSockets_judges_the_feature_and_the_config_each_and_together()
    {
        var r = Run(WebSocketsLib + """
            $script:feature = $null; $script:config = $null; $script:asked = @()
            function Get-TideWebSocketFeature { $script:asked += 'feature'; $script:feature }
            function Get-TideWebSocketConfig { param($IisPath, $SitePath) $script:asked += "config $IisPath $SitePath"; $script:config }
            $on = [pscustomobject]@{ Name = 'Web-WebSockets'; Installed = $true; InstallCommand = 'Install-WindowsFeature Web-WebSockets' }
            $off = [pscustomobject]@{ Name = 'Web-WebSockets'; Installed = $false; InstallCommand = 'Install-WindowsFeature Web-WebSockets' }
            $siteOff = [pscustomobject]@{ Server = 'true'; Site = 'false'; WebConfig = $null; Effective = 'false' }
            $fine = [pscustomobject]@{ Server = $null; Site = $null; WebConfig = $null; Effective = 'true' }
            $out = [ordered]@{}
            $script:feature = $on; $script:config = $fine; $out.ok = Test-TideWebSockets -IisPath 'TIDE' -SitePath 'C:\site'
            $script:feature = $off; $script:config = $siteOff; $out.both = Test-TideWebSockets -IisPath 'TIDE' -SitePath 'C:\site'
            $out.featureOnly = Test-TideWebSockets
            $out.configOnly = Test-TideWebSockets -IisPath 'TIDE' -SkipFeature
            $out.asked = $script:asked
            $out.site = Resolve-TideIisPath 'TIDE' ''
            $out.app = Resolve-TideIisPath 'Intranet' 'tide'
            $out.fromUrl = Resolve-TideIisPath 'Intranet' ([Uri]'https://intranet.contoso.com/tide/').AbsolutePath
            $out.fromRootUrl = Resolve-TideIisPath 'TIDE' ([Uri]'https://tide.contoso.com/').AbsolutePath
            ConvertTo-Json -Depth 5 -Compress -InputObject $out
            """);
        string[] List(JsonElement e) => e.ValueKind == JsonValueKind.Array ? [.. e.EnumerateArray().Select(x => x.GetString()!)]
            : e.ValueKind == JsonValueKind.String ? [e.GetString()!] : [];
        var ok = r.GetProperty("ok");
        ok.GetProperty("Ok").GetBoolean().Should().BeTrue();
        ok.GetProperty("FeatureOk").GetBoolean().Should().BeTrue();
        ok.GetProperty("ConfigOk").GetBoolean().Should().BeTrue();
        List(ok.GetProperty("Problems")).Should().BeEmpty();

        var both = r.GetProperty("both");
        both.GetProperty("Ok").GetBoolean().Should().BeFalse();
        both.GetProperty("FeatureOk").GetBoolean().Should().BeFalse();
        both.GetProperty("ConfigOk").GetBoolean().Should().BeFalse();
        var problems = List(both.GetProperty("Problems"));
        problems.Should().HaveCount(2);
        problems[0].Should().Contain("isn't installed", "the feature's problem comes first, as Get-TideWebSocketVerdict lists it");
        problems[1].Should().Contain("for the site");
        List(both.GetProperty("FeatureProblems")).Should().Equal(problems[0]);
        List(both.GetProperty("ConfigProblems")).Should().Equal(problems[1]);

        var featureOnly = r.GetProperty("featureOnly");
        featureOnly.GetProperty("Config").ValueKind.Should().Be(JsonValueKind.Null, "no -IisPath, no config read");
        featureOnly.GetProperty("ConfigOk").GetBoolean().Should().BeTrue();
        featureOnly.GetProperty("FeatureOk").GetBoolean().Should().BeFalse();
        var configOnly = r.GetProperty("configOnly");
        configOnly.GetProperty("Feature").ValueKind.Should().Be(JsonValueKind.Null, "-SkipFeature");
        configOnly.GetProperty("FeatureOk").GetBoolean().Should().BeTrue();
        configOnly.GetProperty("ConfigOk").GetBoolean().Should().BeFalse();
        List(r.GetProperty("asked")).Should().Equal(
            "feature", "config TIDE C:\\site", "feature", "config TIDE C:\\site", "feature", "config TIDE ");

        r.GetProperty("site").GetString().Should().Be("TIDE");
        r.GetProperty("app").GetString().Should().Be("Intranet/tide");
        r.GetProperty("fromUrl").GetString().Should().Be("Intranet/tide");
        r.GetProperty("fromRootUrl").GetString().Should().Be("TIDE");
    }

    [Fact]
    public void The_IIS_reads_and_writes_are_shared_and_a_failed_or_missing_appcmd_is_reported()
    {
        var r = Run(WebSocketsLib + """
            $script:reads = @(); $script:answer = $null; $script:fails = $false
            function Get-WebConfigurationProperty { [CmdletBinding()] param($PSPath, $Location, $Filter, $Name)
                $script:reads += "$PSPath|$Location|$Filter|$Name"; if ($script:fails) { throw 'locked' }; $script:answer }
            function Import-Module { param($Name, $ErrorAction) }
            $out = [ordered]@{}
            $script:answer = [pscustomobject]@{ Value = ' True ' }; $out.attribute = Get-TideLocationAuth -SiteName 'TIDE' -Path 'health' -Section 'anonymousAuthentication'
            $script:answer = 'False'; $out.bare = Read-TideWebSocketSetting -Location 'TIDE'
            $script:answer = 'maybe'; $out.odd = Read-TideWebSocketSetting
            $script:fails = $true; $out.unreadable = Get-TideLocationAuth -SiteName 'TIDE' -Path 'api/v1' -Section 'windowsAuthentication'
            $out.reads = $script:reads

            $dir = Join-Path ([IO.Path]::GetTempPath()) ('tide-ws-appcmd-' + [guid]::NewGuid().ToString('N'))
            New-Item -ItemType Directory -Path $dir | Out-Null
            try {
                $log = Join-Path $dir 'calls.txt'
                $ok = Join-Path $dir 'appcmd.ps1'
                Set-Content -Path $ok -Value ("Add-Content -LiteralPath '" + $log.Replace("'", "''") + "' -Value (`$args -join ' ')`nexit 0")
                $bad = Join-Path $dir 'appcmd-fails.ps1'
                Set-Content -Path $bad -Value "Write-Output 'ERROR ( message:Cannot find SITE object )'`nexit 3"
                function Get-TideWebSocketConfig { param($IisPath, $SitePath) [pscustomobject]@{ Server = 'true'; Site = 'false'; WebConfig = $null; Effective = 'false' } }
                $out.changed = @(Enable-TideWebSocketConfig -IisPath 'Intranet/tide' -Appcmd $ok)
                $out.calls = @(Get-Content -LiteralPath $log | ForEach-Object { [string]$_ })
                $out.failed = try { Enable-TideWebSocketConfig -IisPath 'TIDE' -Appcmd $bad; 'no error' } catch { $_.Exception.Message }
                $out.missing = try { Set-TideApphostEnabled -Appcmd (Join-Path $dir 'none.exe') -Location 'TIDE' -Section 'system.webServer/webSocket' -Enabled 'true'; 'no error' } catch { $_.Exception.Message }
            } finally { Remove-Item -LiteralPath $dir -Recurse -Force }
            ConvertTo-Json -Depth 4 -Compress -InputObject $out
            """);
        r.GetProperty("attribute").GetString().Should().Be("true");
        r.GetProperty("bare").GetString().Should().Be("false");
        r.GetProperty("odd").ValueKind.Should().Be(JsonValueKind.Null);
        r.GetProperty("unreadable").ValueKind.Should().Be(JsonValueKind.Null);
        r.GetProperty("reads").EnumerateArray().Select(x => x.GetString()).Should().Equal(
            "MACHINE/WEBROOT/APPHOST|TIDE/health|system.webServer/security/authentication/anonymousAuthentication|enabled",
            "MACHINE/WEBROOT/APPHOST|TIDE|system.webServer/webSocket|enabled",
            "MACHINE/WEBROOT/APPHOST||system.webServer/webSocket|enabled",
            "MACHINE/WEBROOT/APPHOST|TIDE/api/v1|system.webServer/security/authentication/windowsAuthentication|enabled");
        r.GetProperty("changed").EnumerateArray().Select(x => x.GetString()).Should().Equal(
            "applicationHost.config: webSocket enabled for 'Intranet/tide' false -> true");
        r.GetProperty("calls").EnumerateArray().Select(x => x.GetString()).Should().Equal(
            "set config Intranet/tide /section:system.webServer/webSocket /enabled:true /commit:apphost");
        r.GetProperty("failed").GetString().Should().Be(
            "Failed to turn WebSockets on for 'TIDE' (appcmd exit 3): ERROR ( message:Cannot find SITE object ). By hand: appcmd set config "
            + "\"TIDE\" /section:system.webServer/webSocket /enabled:true /commit:apphost");
        r.GetProperty("missing").GetString().Should().StartWith("appcmd.exe not found at ").And.EndWith("is the IIS 'Web-Server' role installed?");
    }

    [Fact]
    public void An_unreachable_site_fails_the_handshake_with_a_reason()
    {
        // Port 9 (discard) on loopback: nothing answers, so the negotiate request fails fast.
        var r = Run(WebSocketsLib + """
            ConvertTo-Json -Compress -InputObject (Test-TideWebSocketHandshake -BaseUrl 'http://127.0.0.1:9/' -TimeoutSec 5)
            """);
        r.GetProperty("Ok").GetBoolean().Should().BeFalse();
        r.GetProperty("Status").GetString().Should().Be("unreachable");
        r.GetProperty("Detail").GetString().Should().StartWith("the negotiate request failed");
    }
}
