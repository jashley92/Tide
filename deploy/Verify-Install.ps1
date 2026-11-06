#Requires -Version 5.1

<#
.SYNOPSIS
    Checks a TIDE install: the pool and site are running, the app answers, its database is reachable, the rest of
    the site still asks for a Windows sign-in, and WebSockets work: the IIS WebSocket Protocol feature is installed and on
    for the site, and a real WebSocket to the app (signed in as you) gets its Upgrade. Without WebSockets TIDE falls back
    to long polling, one request per keystroke.

.PARAMETER SkipWebSocketHandshake
    Leave out the real WebSocket handshake (the feature and configuration checks still run).

.EXAMPLE
    .\Verify-Install.ps1 -Url https://tide.contoso.com/ -AppPoolName TIDE -SiteName TIDE

.EXAMPLE
    # Installed under another site (-ParentSite Intranet -AppPath tide): the URL has the path, the site is the parent.
    .\Verify-Install.ps1 -Url https://intranet.contoso.com/tide/ -AppPoolName TIDE -SiteName Intranet
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $Url,
    [string] $AppPoolName = 'TIDE',
    [string] $SiteName = 'TIDE',
    [switch] $SkipWebSocketHandshake
)

$ErrorActionPreference = 'Stop'
$failed = 0
function Report([bool] $ok, [string] $what, [string] $fix = '') {
    if ($ok) { Write-Host "  [ OK ] $what" -ForegroundColor Green }
    else { Write-Host "  [FAIL] $what$(if ($fix) { " - $fix" })" -ForegroundColor Red; $script:failed++ }
}
function Get-Status([string] $u, [switch] $UseDefaultCredentials) {
    try {
        # Windows PowerShell 5.1 raises an error for a redirect it won't follow (-MaximumRedirection 0); under 'Stop' that
        # error lost the 302 and reported 0. SilentlyContinue keeps the response; an HTTP error or no answer still throws.
        $r = Invoke-WebRequest -Uri $u -UseBasicParsing -UseDefaultCredentials:$UseDefaultCredentials -MaximumRedirection 0 -TimeoutSec 60 `
            -ErrorAction SilentlyContinue
        if ($r) { return [int]$r.StatusCode }
        return 0
    } catch {
        if ($_.Exception.Response) { return [int]$_.Exception.Response.StatusCode }
        return 0
    }
}

. (Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'lib\TideWebSockets.ps1')
$wsFix = Get-TideWebSocketFixText
$warned = 0
$elevated = Test-TideElevated
function Warn([string] $what) { Write-Host "  [WARN] $what" -ForegroundColor Yellow; $script:warned++ }
function Report-Checked([bool] $ok, [bool] $checked, [string] $warning, [string] $what, [string] $fix) {
    # A check that couldn't read what it checks is a warning, not an OK; elevated, where it should have read it, a failure.
    if (-not $ok -or $checked) { Report $ok $what $fix }
    elseif ($elevated) { Report $false $what ($warning -replace ': run elevated to check it\.$', ', although this runs elevated.') }
    else { Warn $warning }
}
$base = $Url.TrimEnd('/')
Write-Host "Checking TIDE at $base" -ForegroundColor Cyan

if (Get-Module -ListAvailable WebAdministration) {
    Import-Module WebAdministration
    # A pool or site that doesn't exist is a failed check, not the end of the run.
    $poolState = try { (Get-WebAppPoolState -Name $AppPoolName).Value } catch { 'missing' }
    $siteState = try { (Get-WebsiteState -Name $SiteName).Value } catch { 'missing' }
    Report ($poolState -eq 'Started') "App pool '$AppPoolName' is started (it's $poolState)" "Start it in IIS Manager and check the Application event log"
    Report ($siteState -eq 'Started') "Site '$SiteName' is started (it's $siteState)" "Another site may hold the same binding"

    # WebSockets: the feature, then whether IIS has them on where TIDE answers (the site, or the application under it
    # that the URL's path names).
    $iisPath = Resolve-TideIisPath $SiteName ([Uri]$Url).AbsolutePath
    $physical = Get-TideIisPhysicalPath $iisPath
    if (-not $physical) { Warn "Couldn't read the folder IIS serves '$iisPath' from, so its web.config wasn't checked for <webSocket enabled=""false"" />." }
    $ws = Test-TideWebSockets -IisPath $iisPath -SitePath $physical
    $feature = $ws.Feature
    $featureState = if ($feature.Installed) { 'installed' } elseif ($null -eq $feature.Installed) { "unreadable: $($feature.Detail)" } else { 'not installed' }
    Report-Checked $ws.FeatureOk $ws.FeatureChecked $ws.FeatureWarning "The IIS WebSocket Protocol feature ($($feature.Name)) is installed (it's $featureState)" "$($feature.InstallCommand); $wsFix"
    Report-Checked $ws.ConfigOk $ws.ConfigChecked $ws.ConfigWarning "WebSockets are on for '$iisPath' (system.webServer/webSocket: $($ws.Config.Effective))" "$($ws.ConfigProblems -join ' ') $wsFix"
}

Report ((Get-Status "$base/health/live") -eq 200) "The app answers (/health/live)" "500.30/500.37: read the Application event log and stdout logs"
Report ((Get-Status "$base/health") -eq 200) "The database answers (/health)" "Check ConnectionStrings:Default and the app-pool account's SQL login"
$anon = Get-Status "$base/"
Report ($anon -eq 401) "The site asks for a Windows sign-in (anonymous gets 401, got $anon)" "Windows Authentication must be on and Anonymous off for the site"
$signedIn = Get-Status "$base/" -UseDefaultCredentials
Report ($signedIn -in 200, 302) "A signed-in request is served (got $signedIn)" "403 means your account has no TIDE role yet; map a group to Admin"

# A real WebSocket, as a browser opens one: signed in as you, through whatever proxy or load balancer is in the way.
if (-not $SkipWebSocketHandshake) {
    $hs = Test-TideWebSocketHandshake -BaseUrl $base
    $hsFix = if ($hs.Status -eq 'unreachable') { "Sign in as a TIDE user (a 403 means your account has no TIDE role); $wsFix" } else { $wsFix }
    Report $hs.Ok "A WebSocket to the app gets its Upgrade ($($hs.Detail))" $hsFix
}

if ($failed -gt 0) { Write-Host "$failed check(s) failed." -ForegroundColor Red; exit 1 }
if ($warned -gt 0) { Write-Host "All checks passed, with $warned warning(s): see [WARN] above." -ForegroundColor Yellow; exit 0 }
Write-Host "All checks passed." -ForegroundColor Green
