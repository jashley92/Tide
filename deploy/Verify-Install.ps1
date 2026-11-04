#Requires -Version 5.1

<#
.SYNOPSIS
    Checks a TIDE install: the pool and site are running, the app answers, its database is reachable, and the rest of
    the site still asks for a Windows sign-in.

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
    [string] $SiteName = 'TIDE'
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

$base = $Url.TrimEnd('/')
Write-Host "Checking TIDE at $base" -ForegroundColor Cyan

if (Get-Module -ListAvailable WebAdministration) {
    Import-Module WebAdministration
    # A pool or site that doesn't exist is a failed check, not the end of the run.
    $poolState = try { (Get-WebAppPoolState -Name $AppPoolName).Value } catch { 'missing' }
    $siteState = try { (Get-WebsiteState -Name $SiteName).Value } catch { 'missing' }
    Report ($poolState -eq 'Started') "App pool '$AppPoolName' is started (it's $poolState)" "Start it in IIS Manager and check the Application event log"
    Report ($siteState -eq 'Started') "Site '$SiteName' is started (it's $siteState)" "Another site may hold the same binding"
}

Report ((Get-Status "$base/health/live") -eq 200) "The app answers (/health/live)" "500.30/500.37: read the Application event log and stdout logs"
Report ((Get-Status "$base/health") -eq 200) "The database answers (/health)" "Check ConnectionStrings:Default and the app-pool account's SQL login"
$anon = Get-Status "$base/"
Report ($anon -eq 401) "The site asks for a Windows sign-in (anonymous gets 401, got $anon)" "Windows Authentication must be on and Anonymous off for the site"
$signedIn = Get-Status "$base/" -UseDefaultCredentials
Report ($signedIn -in 200, 302) "A signed-in request is served (got $signedIn)" "403 means your account has no TIDE role yet; map a group to Admin"

if ($failed -gt 0) { Write-Host "$failed check(s) failed." -ForegroundColor Red; exit 1 }
Write-Host "All checks passed." -ForegroundColor Green
