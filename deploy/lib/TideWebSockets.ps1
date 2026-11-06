# WebSocket helpers shared by Install-Tide.ps1, Verify-Install.ps1 and Upgrade-Tide.ps1.
#
# TIDE is a Blazor Server app: each open page keeps one SignalR connection to the server. IIS can carry it over a
# WebSocket only when its WebSocket Protocol feature is installed and system.webServer/webSocket isn't turned off for the
# site. Without that (or behind a proxy or load balancer that drops the Upgrade and Connection headers) SignalR quietly
# falls back to long polling: the app still works, but every keystroke and click becomes its own HTTP request (many
# xhr "_blazor?id=" rows in the browser's Network tab), so typing lags and the server does far more work.
#
# The scripts ask Test-TideWebSockets and act on its answer. It reads IIS with lib\TideIis.ps1's helpers, loaded here
# when the script hasn't already.
if (-not (Get-Command Get-TideLocationEnabled -ErrorAction SilentlyContinue)) { . (Join-Path $PSScriptRoot 'TideIis.ps1') }

function Get-TideWebSocketFixText {
    # What to do when WebSockets don't work: the same words in every script, the docs and Administration > Diagnostics.
    return ("Install the IIS WebSocket Protocol feature (Install-WindowsFeature Web-WebSockets; on Windows 10/11 " +
            "Enable-WindowsOptionalFeature -Online -FeatureName IIS-WebSockets -All), and check any reverse proxy or load " +
            "balancer passes the Upgrade and Connection headers.")
}

function Get-TideWindowsKind {
    # 'Server' or 'Client': which cmdlets manage Windows features here. Windows Server has Get-WindowsFeature
    # (ServerManager); client Windows (10/11) has only the DISM module's Get-WindowsOptionalFeature. ProductType 1 is a
    # workstation, 2 a domain controller, 3 a server.
    try {
        $type = [int](Get-CimInstance -ClassName Win32_OperatingSystem -ErrorAction Stop).ProductType
        if ($type -eq 1) { return 'Client' }
        return 'Server'
    } catch {
        if (Get-Command Get-WindowsFeature -ErrorAction SilentlyContinue) { return 'Server' }
        return 'Client'
    }
}

function Get-TideWebSocketFeature {
    # Whether the IIS WebSocket Protocol feature is installed: Web-WebSockets on Windows Server (Get-WindowsFeature),
    # IIS-WebSockets on client Windows (Get-WindowsOptionalFeature -Online). Returns Kind, Name, Installed ($true or
    # $false), InstallCommand, CheckCommand and Detail (set when the cmdlet couldn't say, and the module file was asked).
    [CmdletBinding()]
    param([ValidateSet('Server', 'Client')] [string] $Kind = (Get-TideWindowsKind))
    # Each kind's feature, its commands, and how its cmdlet answers: $true or $false, or nothing when it doesn't know the name.
    $kinds = @{
        Server = @{
            Name = 'Web-WebSockets'; Cmdlet = 'Get-WindowsFeature'
            InstallCommand = 'Install-WindowsFeature Web-WebSockets'; CheckCommand = 'Get-WindowsFeature Web-WebSockets'
            Read = { param($name) $f = Get-WindowsFeature -Name $name -ErrorAction Stop; if ($f) { [bool]$f.Installed } }
        }
        Client = @{
            Name = 'IIS-WebSockets'; Cmdlet = 'Get-WindowsOptionalFeature'
            InstallCommand = 'Enable-WindowsOptionalFeature -Online -FeatureName IIS-WebSockets -All'
            CheckCommand = 'Get-WindowsOptionalFeature -Online -FeatureName IIS-WebSockets'
            Read = { param($name) $f = Get-WindowsOptionalFeature -Online -FeatureName $name -ErrorAction Stop; if ($f) { "$($f.State)" -eq 'Enabled' } }
        }
    }
    $k = $kinds[$Kind]
    $installed = $null; $detail = $null; $byFile = $false
    try {
        $installed = & $k.Read $k.Name
        if ($null -eq $installed) { $detail = "$($k.Cmdlet) doesn't know $($k.Name)." }
    } catch { $detail = $_.Exception.Message }
    # The cmdlets need elevation (Get-WindowsOptionalFeature) or ServerManager: without them, the feature's module file
    # tells, which IIS puts in inetsrv only when the feature is installed. JudgedByFile says the cmdlet couldn't check.
    if ($null -eq $installed) {
        $installed = Test-TideWebSocketModuleFile
        $byFile = $true
        $detail = "judged by iiswsock.dll, as $($k.CheckCommand) couldn't run: $detail"
    }
    return [pscustomobject]@{ Kind = $Kind; Name = $k.Name; Installed = $installed; InstallCommand = $k.InstallCommand; CheckCommand = $k.CheckCommand; Detail = $detail; JudgedByFile = $byFile }
}

function Test-TideWebSocketModuleFile {
    # Whether IIS's WebSocket module (iiswsock.dll) is in inetsrv, the 64-bit folder even from a 32-bit shell.
    if (-not $env:WINDIR) { return $false }
    foreach ($p in @((Join-Path $env:WINDIR 'System32\inetsrv\iiswsock.dll'), (Join-Path $env:WINDIR 'Sysnative\inetsrv\iiswsock.dll'))) {
        if (Test-Path $p) { return $true }
    }
    return $false
}

function Install-TideWebSocketFeature {
    # Installs the feature Get-TideWebSocketFeature described, with the cmdlet for this kind of Windows. Returns
    # Installed ($true when it succeeded) and RestartNeeded ('Yes', 'No' or 'Maybe'). Throws when the cmdlet fails.
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)] $Feature)
    if ($Feature.Kind -eq 'Server') {
        $r = Install-WindowsFeature -Name Web-WebSockets -ErrorAction Stop
        $restart = "$($r.RestartNeeded)"
        if (-not $restart) { $restart = 'No' }
        return [pscustomobject]@{ Installed = [bool]$r.Success; RestartNeeded = $restart }
    }
    # -All turns on the parents it needs (IIS's Application Development features); -NoRestart lets this script say so.
    $r = Enable-WindowsOptionalFeature -Online -FeatureName IIS-WebSockets -All -NoRestart -ErrorAction Stop
    return [pscustomobject]@{ Installed = $true; RestartNeeded = $(if ($r.RestartNeeded) { 'Yes' } else { 'No' }) }
}

function Get-TideWebConfigWebSocket([string] $Path) {
    # The system.webServer/webSocket 'enabled' a web.config sets ('true' or 'false'), or $null when it doesn't (or there
    # is no file). A <location> with no path (or path ".") counts too, as IIS applies it to the folder itself.
    if (-not $Path -or -not (Test-Path -LiteralPath $Path)) { return $null }
    try { [xml]$x = Get-Content -LiteralPath $Path -Raw } catch { return $null }
    $found = $null
    $scopes = @($x.SelectNodes('/configuration/system.webServer')) +
              @($x.SelectNodes("/configuration/location[not(@path) or @path='' or @path='.']/system.webServer"))
    foreach ($scope in $scopes) {
        $node = $scope.SelectSingleNode('webSocket')
        if ($node -and $node.HasAttribute('enabled')) { $found = ConvertTo-TideEnabledText $node.GetAttribute('enabled') }
    }
    return $found
}

function Read-TideWebSocketSetting([string] $Location) {
    # system.webServer/webSocket 'enabled' in applicationHost.config: server-wide with no -Location, else as IIS applies
    # it at that site or application (its <location> entry, or what it inherits). $null when the section can't be read,
    # which is the case when the feature isn't installed (its schema comes with it).
    try { Import-Module WebAdministration -ErrorAction Stop } catch { return $null }
    return Get-TideLocationEnabled -Filter 'system.webServer/webSocket' -Location $Location
}

function Get-TideWebSocketConfig {
    # Where WebSockets are turned on or off for TIDE: server-wide, at the site (or application) in applicationHost.config,
    # and in the site's own web.config. Server and Site are 'true' or 'false' (IIS answers with the schema's default when
    # nothing sets it), or $null when applicationHost.config couldn't be read: unelevated, or without the feature, whose
    # schema the section comes with. WebConfig is $null when the file doesn't set it. Readable says applicationHost.config
    # answered. Effective is what IIS uses: web.config over the site's entry, over the server's; $null when unknown.
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)] [string] $IisPath, [string] $SitePath)
    $server = Read-TideWebSocketSetting
    $site = Read-TideWebSocketSetting -Location $IisPath
    $webConfig = if ($SitePath) { Get-TideWebConfigWebSocket ([IO.Path]::Combine($SitePath, 'web.config')) } else { $null }
    $readable = ($null -ne $server -or $null -ne $site)
    $effective = if ($webConfig) { $webConfig } elseif ($site) { $site } elseif ($server) { $server } else { $null }
    return [pscustomobject]@{ Server = $server; Site = $site; WebConfig = $webConfig; Readable = $readable; Effective = $effective }
}

function Get-TideWebSocketVerdict {
    # Whether WebSockets can work for the site, from Get-TideWebSocketFeature and Get-TideWebSocketConfig: Ok, and
    # Problems (each a sentence saying what's wrong and how to fix it). Either may be left out, to judge only the other.
    [CmdletBinding()]
    param($Feature, $Config)
    $problems = @()
    if ($Feature -and $Feature.Installed -eq $false) {
        $problems += "The IIS WebSocket Protocol feature ($($Feature.Name)) isn't installed: $($Feature.InstallCommand)."
    } elseif ($Feature -and $null -eq $Feature.Installed) {
        $problems += "Couldn't tell whether the IIS WebSocket Protocol feature ($($Feature.Name)) is installed ($($Feature.Detail)). Check with: $($Feature.CheckCommand)."
    }
    if ($Config) {
        if ($Config.WebConfig -eq 'false') {
            $problems += "The site's web.config turns WebSockets off (<webSocket enabled=""false"" />): remove that element."
        } elseif ($Config.Effective -eq 'false') {
            $where = if ($Config.Site -eq 'false' -and $Config.Server -ne 'false') { 'for the site' } else { 'server-wide' }
            $problems += "WebSockets are turned off $where in applicationHost.config (system.webServer/webSocket enabled=false): Install-Tide.ps1 turns them on for TIDE's site, or appcmd set config ""<site>"" /section:system.webServer/webSocket /enabled:true /commit:apphost."
        }
    }
    return [pscustomobject]@{ Ok = ($problems.Count -eq 0); Problems = $problems }
}

function Test-TideWebSockets {
    # The WebSocket check the scripts share: the IIS feature (unless -SkipFeature) and, given -IisPath, whether IIS has
    # them on where TIDE answers. Returns Feature and Config (Get-TideWebSocketFeature's and Get-TideWebSocketConfig's,
    # $null when not checked), and Get-TideWebSocketVerdict's judgement of each and of both: FeatureOk and
    # FeatureProblems, ConfigOk and ConfigProblems, Ok and Problems (the feature's first). FeatureChecked and
    # ConfigChecked are $false when that part couldn't be read (its Ok then means only that nothing wrong was seen), with
    # FeatureWarning and ConfigWarning saying so; Warnings lists both.
    [CmdletBinding()]
    param([string] $IisPath, [string] $SitePath, [switch] $SkipFeature)
    $feature = if ($SkipFeature) { $null } else { Get-TideWebSocketFeature }
    $config = if ($IisPath) { Get-TideWebSocketConfig -IisPath $IisPath -SitePath $SitePath } else { $null }
    $f = Get-TideWebSocketVerdict -Feature $feature
    $c = Get-TideWebSocketVerdict -Config $config
    $featureChecked = -not ($feature -and $feature.JudgedByFile)
    $featureWarning = if ($featureChecked) { $null } else {
        "Couldn't check the IIS WebSocket Protocol feature ($($feature.Name)) with $($feature.CheckCommand), so it was judged by iiswsock.dll: run elevated to check it."
    }
    $configChecked = -not $config -or $config.Readable -or [bool]$config.WebConfig
    $configWarning = if ($configChecked) { $null } else {
        "Couldn't check whether WebSockets are on for '$IisPath' (system.webServer/webSocket in applicationHost.config couldn't be read): run elevated to check it."
    }
    return [pscustomobject]@{
        Feature = $feature; FeatureOk = $f.Ok; FeatureProblems = @($f.Problems); FeatureChecked = $featureChecked; FeatureWarning = $featureWarning
        Config = $config; ConfigOk = $c.Ok; ConfigProblems = @($c.Problems); ConfigChecked = $configChecked; ConfigWarning = $configWarning
        Ok = ($f.Ok -and $c.Ok); Problems = @($f.Problems) + @($c.Problems)
        Warnings = @(@($featureWarning, $configWarning) | Where-Object { $_ })
    }
}

function Test-TideElevated {
    # Whether this PowerShell runs as an administrator (elevated); $false where that can't be told (not Windows).
    try {
        $id = [Security.Principal.WindowsIdentity]::GetCurrent()
        return (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    } catch { return $false }
}

function ConvertTo-TideIisText($Value, [string] $Name) {
    # An IIS provider answer as plain text: the bare string, a ConfigurationAttribute's .Value, or a property bag's
    # <Name> (Get-ItemProperty -Name answers each way, by provider and version). $null when there is none.
    if ($null -eq $Value) { return $null }
    if ($Name -and $Value.PSObject.Properties[$Name]) { $Value = $Value.$Name }
    if ($null -ne $Value -and $Value -isnot [string] -and $Value.PSObject.Properties['Value']) { $Value = $Value.Value }
    $text = [string]$Value
    if ($text) { return $text }
    return $null
}

function Get-TideIisPhysicalPath([string] $IisPath) {
    # The folder IIS serves the site or application ('Intranet/tide') from, environment variables expanded, or $null
    # when IIS doesn't say.
    try { $v = Get-ItemProperty "IIS:\Sites\$($IisPath.Replace('/', '\'))" -Name physicalPath -ErrorAction Stop } catch { return $null }
    $text = ConvertTo-TideIisText $v 'physicalPath'
    if (-not $text) { return $null }
    return [Environment]::ExpandEnvironmentVariables($text)
}

function Resolve-TideIisPath([string] $SiteName, [string] $AppPath) {
    # The IIS path TIDE answers at: the site, or the application under it ('Intranet/tide').
    $app = "$AppPath".Trim('/')
    if ($app) { return "$SiteName/$app" }
    return $SiteName
}

function Enable-TideWebSocketConfig {
    # Turns WebSockets on for TIDE's site (or application) when IIS has them off there: a <location> entry in
    # applicationHost.config, with appcmd and /commit:apphost (as the installer sets authentication, which works whether
    # or not the section is locked for web.config files), and the attribute in the site's web.config when it says false. The server-wide value,
    # which other sites use, is left alone. Returns what it changed.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] [string] $IisPath,
        [string] $SitePath,
        [string] $Appcmd = (Join-Path $env:WINDIR 'System32\inetsrv\appcmd.exe')
    )
    $changed = @()
    $config = Get-TideWebSocketConfig -IisPath $IisPath -SitePath $SitePath
    if ($config.WebConfig -eq 'false') {
        $file = [IO.Path]::Combine($SitePath, 'web.config')
        [xml]$x = Get-Content -LiteralPath $file -Raw
        foreach ($node in @($x.SelectNodes('//system.webServer/webSocket[@enabled]'))) { $node.SetAttribute('enabled', 'true') }
        $x.Save($file)
        $changed += "web.config: webSocket enabled false -> true"
    }
    if ($config.Site -eq 'false') {
        $set = Set-TideApphostEnabled -Appcmd $Appcmd -Location $IisPath -Section 'system.webServer/webSocket' -Enabled 'true'
        if ($set.ExitCode -ne 0) {
            throw ("Failed to turn WebSockets on for '$IisPath' (appcmd exit $($set.ExitCode)): $($set.Output). By hand: appcmd set config " +
                   """$IisPath"" /section:system.webServer/webSocket /enabled:true /commit:apphost")
        }
        $changed += "applicationHost.config: webSocket enabled for '$IisPath' false -> true"
    }
    return $changed
}

function Get-TideBlazorSocketUrl([string] $BaseUrl, [string] $ConnectionToken) {
    # The address a browser opens its WebSocket at: the app's _blazor hub with ws:// or wss://, and the negotiated id.
    $u = [Uri]($BaseUrl.TrimEnd('/') + '/_blazor')
    $scheme = if ($u.Scheme -eq 'https') { 'wss' } else { 'ws' }
    $query = if ($ConnectionToken) { '?id=' + [Uri]::EscapeDataString($ConnectionToken) } else { '' }
    return "${scheme}://$($u.Authority)$($u.AbsolutePath)$query"
}

function Test-TideWebSocketHandshake {
    # Opens a WebSocket to the app the way a browser does, signed in as you (Windows authentication, like the rest of
    # Verify-Install.ps1): negotiate at <base>/_blazor/negotiate, then the Upgrade to wss://<base>/_blazor?id=<token>.
    # Returns Ok, Status ('upgraded', 'no-websockets', 'refused' or 'unreachable') and Detail.
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)] [string] $BaseUrl, [int] $TimeoutSec = 30)
    $base = $BaseUrl.TrimEnd('/')
    try {
        $r = Invoke-WebRequest -Uri "$base/_blazor/negotiate?negotiateVersion=1" -Method Post -UseBasicParsing -UseDefaultCredentials `
            -TimeoutSec $TimeoutSec -ErrorAction Stop
        $negotiated = $r.Content | ConvertFrom-Json
    } catch {
        $code = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { 0 }
        return [pscustomobject]@{ Ok = $false; Status = 'unreachable'; Detail = "the negotiate request failed ($(if ($code) { "HTTP $code" } else { $_.Exception.Message }))" }
    }
    $transports = @($negotiated.availableTransports | ForEach-Object { "$($_.transport)" })
    if ($transports -notcontains 'WebSockets') {
        return [pscustomobject]@{ Ok = $false; Status = 'no-websockets'; Detail = "the app doesn't offer WebSockets here (it offers $($transports -join ', '))" }
    }
    $token = if ($negotiated.connectionToken) { $negotiated.connectionToken } else { $negotiated.connectionId }
    $ws = New-Object System.Net.WebSockets.ClientWebSocket
    $ws.Options.UseDefaultCredentials = $true
    $cts = New-Object System.Threading.CancellationTokenSource ([TimeSpan]::FromSeconds($TimeoutSec))
    try {
        $null = $ws.ConnectAsync([Uri](Get-TideBlazorSocketUrl $base $token), $cts.Token).GetAwaiter().GetResult()
        if ($ws.State -ne [System.Net.WebSockets.WebSocketState]::Open) {
            return [pscustomobject]@{ Ok = $false; Status = 'refused'; Detail = "the WebSocket ended up $($ws.State)" }
        }
        try { $null = $ws.CloseOutputAsync([System.Net.WebSockets.WebSocketCloseStatus]::NormalClosure, 'Verify-Install', $cts.Token).GetAwaiter().GetResult() } catch { }
        return [pscustomobject]@{ Ok = $true; Status = 'upgraded'; Detail = 'the Upgrade succeeded (101 Switching Protocols)' }
    } catch {
        $e = $_.Exception
        while ($e.InnerException) { $e = $e.InnerException }
        return [pscustomobject]@{ Ok = $false; Status = 'refused'; Detail = "the Upgrade failed: $($e.Message)" }
    } finally {
        $ws.Dispose()
        $cts.Dispose()
    }
}
