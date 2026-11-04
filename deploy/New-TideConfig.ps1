#Requires -Version 5.1

<#
.SYNOPSIS
    Interactive generator for the TIDE install answers file (tide.config.psd1).

.DESCRIPTION
    Prompts for every value the two installers read - SQL instance, service account, schema mode, the IIS site (its own,
    or an application under an existing site), paths, host name, TLS certificate, the four AD role
    groups and the organization's time zone and meeting time - with defaults and light validation, then writes a
    ready-to-use .psd1 to pass to both installers with -ConfigFile. Run it anywhere (a workstation is fine): the AD,
    certificate and IIS checks are best-effort and only warn.

    No password is ever asked for or written: a gMSA has none, and a domain service account's password is asked for by
    Install-Tide.ps1 at install time.

.PARAMETER OutFile
    Where to write the answers file. Default: .\tide.config.psd1

.EXAMPLE
    .\New-TideConfig.ps1

.EXAMPLE
    .\New-TideConfig.ps1 -OutFile C:\deploy\tide.config.psd1
#>
[CmdletBinding()]
param(
    [string] $OutFile = (Join-Path (Get-Location) 'tide.config.psd1')
)

$ErrorActionPreference = 'Stop'

function Ask {
    param(
        [string] $Prompt,
        [string] $Default = '',
        [switch] $Required,
        [string] $Pattern,          # optional regex the answer must match
        [string] $PatternHint
    )
    while ($true) {
        $suffix = if ($Default -ne '') { " [$Default]" } elseif ($Required) { ' (required)' } else { ' (optional, blank to skip)' }
        $raw = Read-Host ("  " + $Prompt + $suffix)
        $val = if ([string]::IsNullOrWhiteSpace($raw)) { $Default } else { $raw.Trim() }
        if ($Required -and [string]::IsNullOrWhiteSpace($val)) { Write-Host "    A value is required." -ForegroundColor Yellow; continue }
        if ($val -ne '' -and $Pattern -and ($val -notmatch $Pattern)) {
            Write-Host "    $PatternHint" -ForegroundColor Yellow; continue
        }
        return $val
    }
}

function Ask-Choice {
    param([string] $Prompt, [string[]] $Options, [string] $Default)
    while ($true) {
        $val = Read-Host ("  " + $Prompt + " (" + ($Options -join '/') + ") [$Default]")
        if ([string]::IsNullOrWhiteSpace($val)) { return $Default }
        $match = $Options | Where-Object { $_ -ieq $val.Trim() }
        if ($match) { return $match }
        Write-Host "    Choose one of: $($Options -join ', ')" -ForegroundColor Yellow
    }
}

function Test-AccountResolves([string] $account) {
    try { [void] ([System.Security.Principal.NTAccount]::new($account)).Translate([System.Security.Principal.SecurityIdentifier]); return $true }
    catch { return $false }
}

# Inside means the same folder or under it: D:\inetpub\tidedata isn't inside D:\inetpub\tide.
function Test-Inside([string] $path, [string] $root) {
    $p = $path.TrimEnd('\'); $r = $root.TrimEnd('\')
    return ($p -ieq $r) -or $p.StartsWith($r + '\', [StringComparison]::OrdinalIgnoreCase)
}

# The app stops at startup on a time zone the server doesn't know. Windows PowerShell can't check IANA ids (the app
# can), so an Area/Location id is taken on its shape, and anything else must be a zone this machine knows.
function Test-TimeZone([string] $id) {
    if ($id -match '^(UTC|[A-Za-z]+(/[A-Za-z0-9_+\-]+)+)$') { return $true }
    try { [void] [TimeZoneInfo]::FindSystemTimeZoneById($id); return $true } catch { return $false }
}

Write-Host ""
Write-Host "TIDE - install answers generator" -ForegroundColor Cyan
Write-Host "Answer the prompts; blank accepts the [default]. Nothing is applied - this only writes $OutFile." -ForegroundColor Gray
Write-Host ""

# --- SQL Server ---------------------------------------------------------------
Write-Host "SQL Server" -ForegroundColor Cyan
$SqlInstance = Ask -Prompt 'SQL instance TIDE shares with your other apps (HOST or HOST\INSTANCE)' -Default 'SQLHOST\INSTANCE' -Required
$DbName      = Ask -Prompt 'Database name (TIDE gets one of its own)' -Default 'Tide' -Required

# --- Service account ----------------------------------------------------------
Write-Host ""
Write-Host "Service account (the IIS app-pool identity AND its SQL login)" -ForegroundColor Cyan
$AppAccount = Ask -Prompt 'Service account (DOMAIN\name; a gMSA ends with $)' -Required `
    -Pattern '^[^\\]+\\[^\\]+$' -PatternHint 'Use DOMAIN\name form, e.g. CONTOSO\svc-tide$ (gMSA) or CONTOSO\svc-tide.'
if ($AppAccount.EndsWith('$')) {
    Write-Host "    A gMSA (trailing '$') - passwordless. Install it on the web host (Install-ADServiceAccount)." -ForegroundColor Gray
} else {
    Write-Host "    A domain account - Install-Tide.ps1 asks for its password at install time (not stored here)." -ForegroundColor Gray
}
if (-not (Test-AccountResolves $AppAccount)) {
    Write-Host "    NOTE: couldn't resolve '$AppAccount' from this machine (fine off the domain). It MUST resolve on the target hosts." -ForegroundColor Yellow
}

Write-Host "    AppMigrates: the app applies its schema at startup (db_owner). DbaApplies: a DBA applies it (read/write/EXECUTE)." -ForegroundColor Gray
$SchemaMode = Ask-Choice -Prompt 'Schema mode' -Options @('AppMigrates', 'DbaApplies') -Default 'AppMigrates'
$DataPath = Ask -Prompt 'SQL data-file folder (.mdf; blank = the instance default)' -Default ''
$LogPath  = Ask -Prompt 'SQL log-file folder (.ldf; blank = the instance default)'  -Default ''
Write-Host "    Check the SQL Server's TLS certificate? Only if it chains to a root the servers trust (not self-signed)." -ForegroundColor Gray
$ValidateCert = (Ask-Choice -Prompt 'Check the SQL certificate' -Options @('No', 'Yes') -Default 'No') -eq 'Yes'

# --- Web host / IIS -----------------------------------------------------------
Write-Host ""
Write-Host "Web host / IIS" -ForegroundColor Cyan
Write-Host "    TIDE usually gets a site of its own (its own host name and SNI binding). It can instead be an application" -ForegroundColor Gray
Write-Host "    under an existing site, such as https://intranet.contoso.com/tide (docs/INSTALL.md, 'Under another site')." -ForegroundColor Gray
$placement = Ask-Choice -Prompt 'Site of its own, or under an existing site' -Options @('Own', 'Under') -Default 'Own'

$SitePath = Ask -Prompt "TIDE's own folder (web root)" -Default 'D:\inetpub\tide' -Required
$DataRoot = Ask -Prompt 'Data root (screenshots and the sign-in key ring - OUTSIDE the web root)' -Default 'E:\TideData' -Required
while (Test-Inside $DataRoot $SitePath) {
    Write-Host "    The data root must be outside the web root ($SitePath); the installer refuses it otherwise." -ForegroundColor Yellow
    $DataRoot = Ask -Prompt 'Data root' -Required
}
$AppPoolName = Ask -Prompt 'IIS app-pool name (TIDE runs in a pool of its own either way)' -Default 'TIDE' -Required

$SiteName = 'TIDE'; $Hostname = ''; $CertificateThumbprint = ''; $ParentSite = ''; $AppPath = ''
if ($placement -eq 'Under') {
    $sites = @()
    try { Import-Module WebAdministration -ErrorAction Stop; $sites = @(Get-Website | ForEach-Object { $_.Name }) } catch { }
    if ($sites.Count -gt 0) { Write-Host ("    Sites on this server: " + ($sites -join ', ')) -ForegroundColor Gray }
    $ParentSite = Ask -Prompt 'Existing IIS site to install under' -Default 'Intranet' -Required
    if ($sites.Count -gt 0 -and -not ($sites | Where-Object { $_ -ieq $ParentSite })) {
        Write-Host "    NOTE: there's no site called '$ParentSite' here (fine if this isn't the web host). It must exist there." -ForegroundColor Yellow
    }
    $AppPath = (Ask -Prompt 'Path TIDE answers at under it' -Default 'tide' -Required `
        -Pattern '^/?[A-Za-z0-9_\-]+/?$' -PatternHint "One path segment of letters, digits, '-' and '_', such as tide.").Trim('/')
    $Hostname = Ask -Prompt "Host name readers use (blank = the site's own)" -Default '' `
        -Pattern '^[A-Za-z0-9.-]+$' -PatternHint 'Enter a DNS name, e.g. intranet.contoso.com.'
    Write-Host "    TIDE uses the site's binding and certificate, and leaves its other settings alone." -ForegroundColor Gray
} else {
    $Hostname = Ask -Prompt 'Host name (host header)' -Default 'tide.contoso.com' -Required `
        -Pattern '^[A-Za-z0-9.-]+$' -PatternHint 'Enter a DNS name, e.g. tide.contoso.com.'
    $SiteName = Ask -Prompt 'IIS site name' -Default 'TIDE' -Required

    # TLS certificate: pick from LocalMachine\My, else a thumbprint, else none.
    Write-Host ""
    Write-Host "TLS certificate (HTTPS on ${Hostname}:443, bound with SNI so other sites keep theirs)" -ForegroundColor Cyan
    $certs = @()
    try { $certs = @(Get-ChildItem Cert:\LocalMachine\My -ErrorAction Stop | Where-Object { $_.HasPrivateKey }) } catch { }
    if ($certs.Count -gt 0) {
        Write-Host "  Installed certificates in LocalMachine\My:" -ForegroundColor Gray
        for ($i = 0; $i -lt $certs.Count; $i++) {
            Write-Host ("    [{0}] {1}  (exp {2})  {3}" -f $i, $certs[$i].Subject, $certs[$i].NotAfter.ToString('yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture), $certs[$i].Thumbprint)
        }
        while ($true) {
            $pick = Ask -Prompt 'Certificate number, a full thumbprint, or blank for HTTP-only' -Default ''
            if ($pick -match '^\d+$' -and [int]$pick -lt $certs.Count) { $CertificateThumbprint = $certs[[int]$pick].Thumbprint; break }
            $thumb = ($pick -replace '\s', '').ToUpperInvariant()
            if ($thumb -eq '' -or $thumb -match '^[0-9A-F]{40}$') { $CertificateThumbprint = $thumb; break }
            Write-Host "    Enter a number from the list, a 40-hex-character thumbprint, or leave it blank." -ForegroundColor Yellow
        }
    } else {
        Write-Host "  (No certificates with a private key here - fine if this isn't the web host.)" -ForegroundColor Gray
        $CertificateThumbprint = (Ask -Prompt 'TLS certificate thumbprint, or blank for HTTP-only' -Default '' `
            -Pattern '^[0-9A-Fa-f\s]{40,}$' -PatternHint 'A thumbprint is 40 hex characters (or leave it blank).') -replace '\s', ''
        $CertificateThumbprint = $CertificateThumbprint.ToUpperInvariant()
    }
    if ($CertificateThumbprint -eq '') {
        Write-Host "    No certificate - an HTTP binding is created for a first smoke test. Add TLS before real use." -ForegroundColor Yellow
    }
}

# --- AD role groups -----------------------------------------------------------
Write-Host ""
Write-Host "AD groups for TIDE's four roles (your real group names; '-' leaves a role to map later in the app)" -ForegroundColor Cyan
. (Join-Path $PSScriptRoot 'lib\TideAdGroups.ps1')
function Ask-Group([string] $role, [string] $default) {
    # Stored as DOMAIN\Group (the group's own domain): a bare name maps only the web server's own domain.
    $g = Ask -Prompt "$role group (Group, DOMAIN\Group, or -)" -Default $default -Required
    if ($g -eq '-') { return '' }
    return ConvertTo-TideGroupName $role $g
}
$AdGroupAnalysts     = Ask-Group 'Analyst (the CTI team: write articles, build and present briefs)'      'CTI-Analysts'
$AdGroupActionOwners = Ask-Group 'Action owner (other security teams: the team edition, their actions)' 'Security-Teams'
$AdGroupReaders      = Ask-Group 'Reader (everyone else: the published archive)'                         'Domain Users'
$AdGroupAdmins       = Ask-Group "Admin (TIDE's administrators: everything)"                              'CTI-Admins'

# --- The organization ---------------------------------------------------------
Write-Host ""
Write-Host "The organization" -ForegroundColor Cyan
while ($true) {
    $TimeZone = Ask -Prompt 'Time zone the team works in (IANA id)' -Default 'America/New_York' -Required
    if (Test-TimeZone $TimeZone) { break }
    Write-Host "    Use an IANA id such as America/New_York or Europe/London (the app won't start on an unknown one)." -ForegroundColor Yellow
}
$MeetingTime = Ask -Prompt 'When the daily brief is presented (24-hour HH:mm)' -Default '10:30' -Required `
    -Pattern '^([01]\d|2[0-3]):[0-5]\d$' -PatternHint 'Use 24-hour HH:mm, e.g. 10:30 or 14:00.'
$TeamName = Ask -Prompt 'Team name (shown on the brief)' -Default 'Cyber Threat Intelligence' -Required

# --- Write the answers file ---------------------------------------------------
# Each quote is doubled, the typographic single quotes too: PowerShell ends a '...' string at any of them.
$quoteClass = '([' + "'" + [char]0x2018 + [char]0x2019 + [char]0x201A + [char]0x201B + '])'
function Q([string] $v) { "'" + ($v -replace $quoteClass, '$1$1') + "'" }

$body = @"
# TIDE install answers - generated $((Get-Date).ToString('yyyy-MM-dd HH:mm', [Globalization.CultureInfo]::InvariantCulture)) by New-TideConfig.ps1.
# Pass it to both installers with -ConfigFile. It names your AD groups, hosts and account: don't commit it.
# Holds NO passwords: a gMSA has none, and a domain account's password is asked for by Install-Tide.ps1.
@{
    # --- SQL Server (Install-Database.ps1 and Install-Tide.ps1) ---
    SqlInstance = $(Q $SqlInstance)
    DbName      = $(Q $DbName)
    AppAccount  = $(Q $AppAccount)
    SchemaMode  = $(Q $SchemaMode)
    DataPath    = $(Q $DataPath)
    LogPath     = $(Q $LogPath)
    ValidateServerCertificate = $(if ($ValidateCert) { '$true' } else { '$false' })

    # --- IIS (Install-Tide.ps1) ---
    SiteName    = $(Q $SiteName)
    AppPoolName = $(Q $AppPoolName)
    Hostname    = $(Q $Hostname)
    SitePath    = $(Q $SitePath)
    DataRoot    = $(Q $DataRoot)
    CertificateThumbprint = $(Q $CertificateThumbprint)
    ParentSite  = $(Q $ParentSite)
    AppPath     = $(Q $AppPath)

    # --- Who can do what (blank: map it later in the app) ---
    AdGroupAnalysts     = $(Q $AdGroupAnalysts)
    AdGroupActionOwners = $(Q $AdGroupActionOwners)
    AdGroupReaders      = $(Q $AdGroupReaders)
    AdGroupAdmins       = $(Q $AdGroupAdmins)

    # --- The organization ---
    TimeZone    = $(Q $TimeZone)
    MeetingTime = $(Q $MeetingTime)
    TeamName    = $(Q $TeamName)
}
"@

if (Test-Path $OutFile) {
    $ow = Ask-Choice -Prompt "`n$OutFile exists. Overwrite?" -Options @('y', 'n') -Default 'n'
    if ($ow -eq 'n') { Write-Host "Stopped - the existing file is untouched." -ForegroundColor Yellow; exit 1 }
}
$body | Set-Content -Path $OutFile -Encoding utf8

# Check it loads, as the installers will read it.
[void] (Import-PowerShellDataFile -Path $OutFile)

$name = Split-Path -Leaf $OutFile
Write-Host ""
Write-Host "Wrote $OutFile" -ForegroundColor Green
Write-Host "Next:" -ForegroundColor Green
Write-Host "  1. On the SQL host, elevated:  .\Install-Database.ps1 -ConfigFile .\$name" -ForegroundColor Green
Write-Host "  2. On the web host, elevated:  .\Install-Tide.ps1 -ConfigFile .\$name" -ForegroundColor Green
if (-not $AppAccount.EndsWith('$')) {
    Write-Host "     (It asks for $AppAccount's password, or pass -AppPoolCredential.)" -ForegroundColor Green
}
if ($SchemaMode -eq 'DbaApplies') {
    Write-Host "     (DbaApplies: your DBA applies the schema before the app starts; see docs/INSTALL.md.)" -ForegroundColor Green
}
Write-Host "  3. Check it:                   .\Verify-Install.ps1 (see docs/INSTALL.md, section 5)" -ForegroundColor Green
