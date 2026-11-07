#Requires -Version 5.1
#Requires -RunAsAdministrator

<#
.SYNOPSIS
    In-place upgrade of an existing TIDE IIS deployment to a newer release.

.DESCRIPTION
    Safe, repeatable upgrade of an install Install-Tide.ps1 stood up, as a site of its own or as an application under
    another site. Unlike re-running the installer, it never reconfigures IIS (beyond the /health and /api/v1 carve-outs below) or
    changes an existing setting: it backs up, swaps the app's files, and lets the app apply its pending migrations, or,
    on a DbaApplies install, checks the DBA already has. It rolls the files back if the new build doesn't start.

    Stages:
      1. Resolve the live deployment from IIS and the deployed appsettings.Production.json (site folder, app pool, SQL
         instance and database, schema mode, health URL).
      2. Preflight: the ASP.NET Core Module and the .NET 10 runtime are present (and, as a warning only, the IIS
         WebSocket Protocol feature is installed and on for the site), SQL is reachable, and the migration
         check: the database's __EFMigrationsHistory against the release's migrations-sqlserver.txt. It stops on a
         lineage mismatch (the state that makes the app fail at startup with "There is already an object named ..."),
         and on a DbaApplies install it stops unless the database already has every migration in the manifest: the
         upgrade never migrates such a database. It also lists any data-folder or server setting a newer release added
         that the file lacks, and checks the anonymous /health and /api/v1 carve-outs. -WhatIf stops here.
      3. Back up: a server-side, COPY_ONLY full backup of the database, a copy of appsettings.Production.json, and a
         snapshot of the current site files (the rollback point).
      4. Add any missing settings (at the value the app already uses without them; data folders under the data root),
         set the /health and /api/v1 carve-outs if they differ, stop the pool, copy the new files into the site (keeping
         appsettings.Production.json, logs and App_Data), and remove what the release no longer ships, as
         Install-Tide.ps1 does.
      5. Start the pool and warm up: the first start applies pending migrations (AppMigrates); poll /health until TIDE
         answers. If it doesn't, roll the files back automatically, and leave the pool stopped when the new build had
         already migrated, until the database backup is restored.

    Integrated authentication throughout: no secrets are read or written.

.PARAMETER BundleZip
    A release bundle the Release workflow built (tide-<version>.zip: app\, deploy\, migrations-sqlserver.txt,
    VERSION.txt). Before anything in it is expanded or run, this script checks it:
      - its signature, tide-<version>.zip.sig beside it, against the release keys this script pins
        (lib\TideReleaseKeys.ps1). A signature that doesn't verify is refused. While keys are pinned, a bundle without
        one is refused unless -AllowUnsigned;
      - its SHA-256: -ExpectedSha256, else tide-<version>.zip.sha256 beside it (sha256sum's format, or the hash alone).
        A mismatch is refused. With neither, and no valid signature, it's checked against the .sha256 of the release its
        VERSION.txt names on github.com (or through -Proxy); when that can't be fetched, it warns and carries on.
    On a host that can't reach github.com, copy the zip's .sig and .sha256 from the release page next to it.

.PARAMETER Version
    A release to download from GitHub and upgrade to, such as v0.2.0 or v0.2.0-alpha.3, or 'latest' (the newest
    release, else the newest pre-release). Downloads tide-<version>.zip, its .sha256 and its .sig, checks them as for
    -BundleZip, and carries on as with -BundleZip. Needs outbound HTTPS to github.com (or -Proxy); a host without it
    downloads the bundle elsewhere and uses -BundleZip. Administration > System > Updates shows this command with the
    newest version filled in.

.PARAMETER ExpectedSha256
    The bundle's SHA-256 (64 hex characters, from the release page), when you have the hash but not the .sha256 file.
    The bundle is refused if it doesn't match. Used in place of a .sha256 beside the zip.

.PARAMETER AllowUnsigned
    Accept a bundle with no .sig (a release from before signing: v1.8.0 and earlier) although this script pins a
    release key. It's then checked by its SHA-256 only, with a warning. A .sig that doesn't verify is still refused.

.PARAMETER Repository
    The GitHub repository releases come from (default 'jashley92/Tide').

.PARAMETER Proxy
    An outbound proxy for -Version and the -BundleZip hash check (http://proxy.contoso.local:8080), used with your
    Windows credentials.

.PARAMETER AppSource
    A folder that already holds the published app (an extracted bundle's app\, or a publish output), instead of a bundle.

.PARAMETER Build
    Publish from this repository instead of a bundle (needs the .NET 10 SDK on this host).

.PARAMETER SiteName
    The IIS site TIDE is (default 'TIDE'). Installed under another site (Install-Tide.ps1 -ParentSite), the name of
    that site, with -AppPath.

.PARAMETER AppPath
    The application path TIDE answers at under -SiteName ('tide'), when it's installed under another site.

.PARAMETER SitePath
    Overrides the site folder otherwise read from IIS. Likewise -AppPoolName, -SqlInstance, -DbName (otherwise read from
    the connection string) and -HealthUrl (the address the warm-up probes, otherwise built from the site's binding).

.PARAMETER BackupRoot
    Where the settings copy and the rollback snapshot go (default <site folder's parent>\tide-upgrade-backups\<time>).
    The database backup is written by SQL Server to its default backup folder.

.PARAMETER DataRoot
    The install's data root. Only needed when appsettings.Production.json lacks a data-folder setting a newer release
    added and the root can't be worked out from the others.

.PARAMETER SkipDbBackup
    Skip the database backup (only when your backup product's is current).

.PARAMETER Force
    Don't ask before backing up and deploying. -WhatIf runs the checks and stops before any change.

.EXAMPLE
    .\Upgrade-Tide.ps1 -Version v0.2.0

.EXAMPLE
    .\Upgrade-Tide.ps1 -BundleZip .\tide-v0.2.0.zip -WhatIf

.EXAMPLE
    .\Upgrade-Tide.ps1 -Version latest -SiteName Intranet -AppPath tide

.EXAMPLE
    .\Upgrade-Tide.ps1 -BundleZip D:\drop\tide-v0.2.0.zip -ExpectedSha256 <the SHA-256 from the release page>
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string] $BundleZip,
    [string] $Version,
    [string] $Repository = 'jashley92/Tide',
    [string] $Proxy,
    [string] $ExpectedSha256,
    [switch] $AllowUnsigned,
    [string] $AppSource,
    [switch] $Build,

    [string] $SiteName = 'TIDE',
    [string] $AppPath,
    [string] $SitePath,
    [string] $AppPoolName,
    [string] $SqlInstance,
    [string] $DbName,
    [string] $HealthUrl,

    [string] $BackupRoot,
    [string] $DataRoot,
    [switch] $SkipDbBackup,
    [int]    $WarmupTimeoutSec = 660,   # past the app's 600 s startupTimeLimit (web.config)
    [switch] $Force,

    # Set by the handover below: the script that handed over has already checked the bundle against its release.
    [Parameter(DontShow)] [switch] $ReleaseHashChecked,
    # For tests only: a TideReleaseKeys.ps1 to trust instead of the lib\TideReleaseKeys.ps1 beside this script.
    [Parameter(DontShow)] [string] $TrustedKeysPath
)

$ErrorActionPreference = 'Stop'
# -WhatIf stops at the confirmation, before anything changes. Until then the run stages the build in TEMP and runs the
# preflight for real (an extraction or New-Item skipped by -WhatIf would only break it); the flag is still passed on by
# the handover to the bundle's script below.
$dryRun = [bool]$WhatIfPreference
$WhatIfPreference = $false
# -Confirm also sets $ConfirmPreference to Low for this script, so every cmdlet below would ask, and an N at one of them
# would fail the run part way. -Confirm asks one question, the upgrade's ($PSCmdlet.ShouldProcess reads the switch).
$confirmAsked = $PSBoundParameters.ContainsKey('Confirm') -and [bool]$PSBoundParameters['Confirm']
if ($confirmAsked) { $ConfirmPreference = 'High' }
# Windows PowerShell 5.1 offers only SSL3/TLS 1.0 unless the host sets SchUseStrongCrypto; a host with those off would
# refuse every HTTPS request below. Left to the OS when it already decides (SystemDefault includes TLS 1.2 and 1.3).
if ([Net.ServicePointManager]::SecurityProtocol -ne [Net.SecurityProtocolType]::SystemDefault) {
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
}
function Write-Step([string] $m) { Write-Host "==> $m" -ForegroundColor Cyan }
function Write-Info([string] $m) { Write-Host "    $m" -ForegroundColor Gray }
function Write-Warn2([string] $m) { Write-Host "    $m" -ForegroundColor Yellow }

$sourceCount = @($BundleZip, $Version, $AppSource).Where({ $_ }).Count + [int][bool]$Build
if ($sourceCount -ne 1) { throw "Specify exactly one source: -Version <vX.Y.Z>, -BundleZip <zip>, -AppSource <folder>, or -Build." }

$deployDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot  = Split-Path -Parent $deployDir
. (Join-Path $deployDir 'lib\TideStamp.ps1')
. (Join-Path $deployDir 'lib\TideJson.ps1')
. (Join-Path $deployDir 'lib\TidePath.ps1')
. (Join-Path $deployDir 'lib\TideIis.ps1')
. (Join-Path $deployDir 'lib\TideSql.ps1')
. (Join-Path $deployDir 'lib\TideSettings.ps1')
. (Join-Path $deployDir 'lib\TideUpgrade.ps1')
. (Join-Path $deployDir 'lib\TideWebSockets.ps1')
# Without a trailing backslash, which breaks the quoting of robocopy's arguments (lib\TidePath.ps1).
$AppSource = Get-TideCleanPath $AppSource; $SitePath = Get-TideCleanPath $SitePath
$BackupRoot = Get-TideCleanPath $BackupRoot; $DataRoot = Get-TideCleanPath $DataRoot
$stamp = Get-TideStamp   # local time, invariant culture: backup folders and .bak names sort on any server

# --- 1. Resolve the live deployment from IIS + appsettings.Production.json -----
Import-Module WebAdministration -ErrorAction Stop
if ($AppPath) {
    $AppPath = $AppPath.Trim().Trim('/')
    $iisPath = "$SiteName/$AppPath"
    Write-Step "Resolving the TIDE application '$iisPath'"
    if (-not (Test-Path "IIS:\Sites\$SiteName")) { throw "IIS site '$SiteName' not found. Pass -SiteName, the site TIDE is installed under." }
    $iisApp = Get-WebApplication -Site $SiteName -Name $AppPath
    if (-not $iisApp) { throw "'$SiteName' has no application at /$AppPath. Pass the -AppPath Install-Tide.ps1 was given, or use Install-Tide.ps1 for a first install." }
    if (-not $SitePath)    { $SitePath    = [Environment]::ExpandEnvironmentVariables($iisApp.PhysicalPath) }
    if (-not $AppPoolName) { $AppPoolName = $iisApp.applicationPool }
} else {
    $iisPath = $SiteName
    Write-Step "Resolving the '$SiteName' deployment"
    $site = Get-Website -Name $SiteName -ErrorAction SilentlyContinue
    if (-not $site) {
        throw ("IIS site '$SiteName' not found. Pass -SiteName; when TIDE is installed under another site, pass that site as " +
               "-SiteName and TIDE's path as -AppPath. For a first install use Install-Tide.ps1.")
    }
    if (-not $SitePath)    { $SitePath    = [Environment]::ExpandEnvironmentVariables($site.physicalPath) }
    if (-not $AppPoolName) { $AppPoolName = $site.applicationPool }
}
$SitePath = Get-TideCleanPath $SitePath
if (-not (Test-Path $SitePath)) { throw "Site folder not found: $SitePath" }

$prodSettings = Join-Path $SitePath 'appsettings.Production.json'
if (-not (Test-Path $prodSettings)) { throw "appsettings.Production.json not found in $SitePath. Is this a TIDE site?" }
try { $liveCfg = Read-TideJsonFile $prodSettings }
catch { throw "$($_.Exception.Message) Fix it, then run the upgrade again." }

$provider = "$(Get-TideJsonNode $liveCfg 'Database:Provider')"
if ($provider -and $provider -ne 'SqlServer') { throw "Database:Provider is '$provider'. The upgrade works with SQL Server installs only." }
# The connection string the app uses, for the instance, database and whether the instance's certificate is checked.
try { $dbInfo = ConvertFrom-TideConnectionString "$(Get-TideJsonNode $liveCfg 'ConnectionStrings:Default')" }
catch { throw "Couldn't read ConnectionStrings:Default in $prodSettings ($($_.Exception.Message))." }
if (-not $SqlInstance) { $SqlInstance = $dbInfo.Instance }
if (-not $DbName)      { $DbName      = $dbInfo.Name }
if (-not $SqlInstance -or -not $DbName) { throw "ConnectionStrings:Default in $prodSettings names no server or database. Pass -SqlInstance and -DbName." }
# DbaApplies: the app doesn't migrate (Database:ApplyMigrations false, the app pool has no db_owner).
$dbaApplies = "$(Get-TideJsonNode $liveCfg 'Database:ApplyMigrations')" -ieq 'false'
$schemaMode = if ($dbaApplies) { 'DbaApplies' } else { 'AppMigrates' }

if (-not $HealthUrl) {
    $https = Get-WebBinding -Name $SiteName -Protocol https -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($https) {
        $HealthUrl = Get-TideBindingUrl ([string]$https.bindingInformation) 'https' 443 $AppPath
    } else {
        $http = Get-WebBinding -Name $SiteName -Protocol http -ErrorAction SilentlyContinue | Select-Object -First 1
        $HealthUrl = if ($http) { Get-TideBindingUrl ([string]$http.bindingInformation) 'http' 80 $AppPath } else { Get-TideBindingUrl '*:80:' 'http' 80 $AppPath }
    }
}
if (-not $HealthUrl.EndsWith('/')) { $HealthUrl += '/' }
# The warm-up asks the app itself. IIS answers an anonymous request to the rest of the site with 401 before the app runs,
# so that proves nothing; /health is carved out of Windows authentication and answered by TIDE with its status word.
$ProbeUrl = $HealthUrl + 'health'
Write-Info "Site folder : $SitePath"
Write-Info "IIS path    : $iisPath"
Write-Info "App pool    : $AppPoolName"
Write-Info "Database    : $DbName on $SqlInstance ($schemaMode)"
Write-Info "Probe URL   : $ProbeUrl"

# The app's host filtering answers 400 to a Host header AllowedHosts doesn't list, and the probe URL is often a binding's
# IP or localhost. The probe still connects there but names the site as the app knows it: a binding's host name, else
# the first plain AllowedHosts entry. No list, or '*', takes any host, so the URL's own is kept.
function Get-AllowedHosts {
    foreach ($f in @($prodSettings, (Join-Path $SitePath 'appsettings.json'))) {
        try {
            $cfgHosts = (Read-TideJsonFile $f).AllowedHosts
            if ($null -ne $cfgHosts) { return @(([string]$cfgHosts).Split(';') | ForEach-Object { $_.Trim() } | Where-Object { $_ }) }
        } catch { }
    }
    return @()
}
$allowedHosts = @(Get-AllowedHosts)
$probeUri = [Uri]$ProbeUrl
$ProbeHost = $null
if (-not (Test-TideHostAllowed $probeUri.Host $allowedHosts)) {
    $bindingHosts = @(Get-WebBinding -Name $SiteName -ErrorAction SilentlyContinue | Where-Object { $_.protocol -in 'https', 'http' } |
        Sort-Object { $_.protocol -ne 'https' } | ForEach-Object { if ([string]$_.bindingInformation -match ':\d*:(?<h>[^:]+)$') { $Matches['h'] } })
    # A wildcard (an IIS '*.corp.example' binding or AllowedHosts entry) can't be sent as a Host, so only plain names.
    $ProbeHost = @($bindingHosts) + @($allowedHosts) | Where-Object { $_ -notmatch '\*' } |
        Where-Object { Test-TideHostAllowed $_ $allowedHosts } | Select-Object -First 1
    if ($ProbeHost -and -not $probeUri.IsDefaultPort) { $ProbeHost = "${ProbeHost}:$($probeUri.Port)" }
    if ($ProbeHost) { Write-Info "Probe Host  : $ProbeHost ('$($probeUri.Host)' isn't in AllowedHosts)" }
}

# One request to the site, straight there (never through the administrator's proxy), without following redirects,
# tolerant of a self-signed certificate (set by the caller). Says what it got.
function Invoke-SiteProbe([string] $Url, [switch] $WithCredentials) {
    $req = [System.Net.HttpWebRequest]::Create($Url)
    $req.Proxy = $null
    $req.AllowAutoRedirect = $false
    $req.Timeout = 30000
    $req.ReadWriteTimeout = 30000
    $req.UserAgent = 'TIDE-Upgrade'
    if ($ProbeHost) { $req.Host = $ProbeHost }
    if ($WithCredentials) { $req.UseDefaultCredentials = $true }
    $resp = $null
    try { $resp = $req.GetResponse() }
    catch {
        $ex = $_.Exception
        while ($ex -and $ex -isnot [System.Net.WebException]) { $ex = $ex.InnerException }
        if ($ex -and $ex.Response) { $resp = $ex.Response }
        else {
            $msg = if ($ex) { "$($ex.Status): $($ex.Message)" } else { $_.Exception.Message }
            return @{ Status = $null; Detail = $msg; Body = ''; Error = $(if ($ex) { [string]$ex.Status } else { '' }) }
        }
    }
    try {
        $body = ''
        try {
            $reader = New-Object System.IO.StreamReader($resp.GetResponseStream())
            $buf = New-Object char[] 4096
            $n = $reader.Read($buf, 0, $buf.Length)
            if ($n -gt 0) { $body = New-Object string($buf, 0, $n) }
        } catch { }
        $detail = "$([int]$resp.StatusCode) $($resp.StatusDescription)"
        if ($body -match '<title>([^<]+)</title>') { $detail += " ($($Matches[1].Trim()))" }
        elseif ($body -and $body.Length -le 20) { $detail += " ($($body.Trim()))" }
        return @{ Status = [int]$resp.StatusCode; Detail = $detail; Body = $body }
    } finally { $resp.Close() }
}

# Short-lived connections for the preflight, the backup and the rollback check (Windows authentication).
function Invoke-DbScalar([string] $Sql, [hashtable] $Parameters = @{}, [int] $TimeoutSeconds = 30) {
    $c = New-TideSqlConnection -SqlInstance $SqlInstance -DbName $DbName -ValidateServerCertificate:$dbInfo.ValidateServerCertificate -ApplicationName 'TIDE upgrade'
    try { return Invoke-TideSqlScalar -Connection $c -Sql $Sql -Parameters $Parameters -TimeoutSeconds $TimeoutSeconds }
    finally { $c.Dispose() }
}
function Get-AppliedMigrations {
    $c = New-TideSqlConnection -SqlInstance $SqlInstance -DbName $DbName -ValidateServerCertificate:$dbInfo.ValidateServerCertificate -ApplicationName 'TIDE upgrade'
    try { return @(Invoke-TideSqlColumn -Connection $c -Sql "IF OBJECT_ID('dbo.__EFMigrationsHistory') IS NOT NULL SELECT MigrationId FROM dbo.__EFMigrationsHistory ORDER BY MigrationId") }
    finally { $c.Dispose() }
}

# --- 2. Stage the new build (so the preflight can read its migration manifest) -
$staging = Join-Path $env:TEMP "tide-upgrade-$stamp"
New-Item -ItemType Directory -Force -Path $staging | Out-Null
$appSrc = $null; $manifest = $null; $targetVersion = $null; $tag = $null
$hashChecked = [bool]$ReleaseHashChecked

if ($Version -or $BundleZip) {
    if ($Repository -notmatch '^[A-Za-z0-9-]{1,39}/[A-Za-z0-9._-]{1,100}$') { throw "-Repository must be owner/name, not '$Repository'." }
    $web = @{ UseBasicParsing = $true; TimeoutSec = 300 }
    if ($Proxy) { $web.Proxy = $Proxy; $web.ProxyUseDefaultCredentials = $true }
    $ProgressPreference = 'SilentlyContinue'   # Windows PowerShell's progress bar slows a download many times over
}

# -Version downloads the bundle and its .sha256 itself, as you, on this host, and refuses a bundle whose hash doesn't
# match. Nothing the web app wrote is used.
if ($Version) {
    if ($Version -ieq 'latest') {
        Write-Step "Finding the latest release of $Repository"
        $ua = @{ 'User-Agent' = 'TIDE-Upgrade' }
        $latest = $null
        try { $latest = Invoke-RestMethod @web -Uri "https://api.github.com/repos/$Repository/releases/latest" -Headers $ua }
        catch {
            $status = 0
            try { $status = [int]$_.Exception.Response.StatusCode } catch { }
            if ($status -ne 404) { throw "Couldn't ask github.com/$Repository for its latest release ($($_.Exception.Message)). Pass -Version vX.Y.Z, or -Proxy." }
        }
        # No release yet (only pre-releases, as before 1.0): the newest of those.
        if (-not $latest) {
            $latest = @(Invoke-RestMethod @web -Uri "https://api.github.com/repos/$Repository/releases?per_page=30" -Headers $ua) |
                Where-Object { -not $_.draft } | Select-Object -First 1
        }
        if (-not $latest) { throw "github.com/$Repository has no published release." }
        $Version = [string]$latest.tag_name
        Write-Info "Latest: $Version"
    }
    if (-not (Test-TideReleaseTag $Version)) { throw "-Version must be a release tag like v0.2.0 or v0.2.0-alpha.3 (or 'latest'), not '$Version'." }

    $download = Join-Path $env:TEMP "tide-download-$stamp"
    New-Item -ItemType Directory -Force -Path $download | Out-Null
    $zipName = "tide-$Version.zip"
    $base = "https://github.com/$Repository/releases/download/$Version"
    Write-Step "Downloading $zipName from $Repository"
    $BundleZip = Join-Path $download $zipName
    try {
        Invoke-WebRequest @web -Uri "$base/$zipName.sha256" -OutFile "$BundleZip.sha256"
        Invoke-WebRequest @web -Uri "$base/$zipName" -OutFile $BundleZip
    } catch {
        throw ("Couldn't download $Version from github.com/$Repository ($($_.Exception.Message)). Check the version exists " +
               "and this host can reach github.com (or pass -Proxy); otherwise download the bundle elsewhere and use -BundleZip.")
    }
    # And its signature. A release from before signing has none (404); the check below decides what that means.
    try { Invoke-WebRequest @web -Uri "$base/$zipName.sig" -OutFile "$BundleZip.sig" }
    catch {
        Remove-Item -LiteralPath "$BundleZip.sig" -ErrorAction SilentlyContinue
        Write-Info "No $zipName.sig downloaded ($($_.Exception.Message))."
    }
    $tag = $Version
}
if ($BundleZip -and -not $hashChecked) {
    # This script, the installed one, checks the bundle before it's expanded or its own script is handed over to, since a
    # bundle's script can't vouch for itself: its signature against the release keys this script pins, and its SHA-256
    # against -ExpectedSha256, a .sha256 beside it, or (unsigned, with neither) its release's on github.com
    # (lib\TideUpgrade.ps1, Invoke-TideBundleCheck). A bundle that fails is refused here, with nothing changed.
    if (-not (Test-Path $BundleZip -PathType Leaf)) { throw "Bundle not found: $BundleZip" }
    $keysFile = if ($TrustedKeysPath) { $TrustedKeysPath } else { Join-Path $deployDir 'lib\TideReleaseKeys.ps1' }
    if ($TrustedKeysPath -and -not (Test-Path -LiteralPath $TrustedKeysPath -PathType Leaf)) { throw "-TrustedKeysPath not found: $TrustedKeysPath" }
    $trustedKeys = @(Get-TideTrustedKeys $keysFile)
    Write-Step "Checking the bundle's signature and SHA-256"
    $check = Invoke-TideBundleCheck -ZipPath $BundleZip -Keys $trustedKeys -AllowUnsigned:$AllowUnsigned -ExpectedSha256 $ExpectedSha256 `
        -Tag $tag -Repository $Repository -Web $web -WorkDir $staging
    foreach ($n in $check.Notes) { Write-Info $n }
    foreach ($w in $check.Warnings) { Write-Warn2 $w }
    $tag = $check.Tag
    $hashChecked = $true   # the bundle's own script, handed over to below, doesn't check it again
}

if ($BundleZip) {
    if (-not (Test-Path $BundleZip)) { throw "Bundle not found: $BundleZip" }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zipPath = (Resolve-Path $BundleZip).Path

    # The upgrade is run by the release's own script, not by this one: -Version runs from the kept copy, which is the
    # previous release's, and a fix to the upgrade itself would otherwise only reach the upgrade after the one that
    # ships it. The bundle's script, run on the same zip, finds itself and carries on. Only its deploy folder is
    # extracted to run it, so the app isn't expanded twice.
    $handoverDir = $null
    $zip = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        $scriptEntry = $zip.Entries | Where-Object { $_.FullName -match '^([^/\\]+[/\\])?deploy[/\\]Upgrade-Tide\.ps1$' } |
            Select-Object -First 1
        if ($scriptEntry) {
            $sha = [System.Security.Cryptography.SHA256]::Create()
            $entryStream = $scriptEntry.Open()
            try { $bundleScriptHash = [BitConverter]::ToString($sha.ComputeHash($entryStream)).Replace('-', '') }
            finally { $entryStream.Dispose(); $sha.Dispose() }
            if ($bundleScriptHash -ne (Get-FileHash -Path $MyInvocation.MyCommand.Path -Algorithm SHA256).Hash) {
                # A folder of its own: the bundle's script, started within the same second, stages in tide-upgrade-<stamp>.
                $handoverDir = [IO.Path]::GetFullPath((Join-Path $env:TEMP "tide-handover-$stamp\deploy"))
                $prefix = $scriptEntry.FullName.Replace('\', '/')
                $prefix = $prefix.Substring(0, $prefix.Length - 'Upgrade-Tide.ps1'.Length)   # "<root>/deploy/"
                foreach ($e in $zip.Entries) {
                    $name = $e.FullName.Replace('\', '/')
                    if (-not $e.Name -or -not $name.StartsWith($prefix, [StringComparison]::Ordinal)) { continue }   # folders, the rest
                    $to = [IO.Path]::GetFullPath((Join-Path $handoverDir $name.Substring($prefix.Length)))
                    if (-not $to.StartsWith($handoverDir + '\', [StringComparison]::OrdinalIgnoreCase)) {
                        throw "The bundle has an entry that points outside its deploy folder ($($e.FullName)). Nothing was changed."
                    }
                    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $to) | Out-Null
                    [System.IO.Compression.ZipFileExtensions]::ExtractToFile($e, $to, $true)
                }
            }
        }
    } finally { $zip.Dispose() }

    if ($handoverDir) {
        $bundleScript = Join-Path $handoverDir 'Upgrade-Tide.ps1'
        $accepted = (Get-Command $bundleScript).Parameters.Keys
        $pass = @{}
        foreach ($k in $PSBoundParameters.Keys) {
            if ($k -notin 'BundleZip', 'Version', 'ReleaseHashChecked' -and $k -in $accepted) { $pass[$k] = $PSBoundParameters[$k] }
        }
        $pass.BundleZip = $BundleZip
        if ($dryRun) { $pass.WhatIf = $true }
        if ($hashChecked -and $accepted -contains 'ReleaseHashChecked') { $pass.ReleaseHashChecked = $true }
        $handoverName = if ($tag) { $tag } else { 'new' }
        Write-Step "Handing over to the upgrade script in the $handoverName bundle"
        & $bundleScript @pass
        return
    }

    # ZipFile rather than Expand-Archive, which in Windows PowerShell 5.1 takes many times as long over the app's files.
    # The staging folder is new, so nothing is overwritten; .NET refuses an entry that would land outside it.
    Write-Step "Expanding the release bundle"
    [System.IO.Compression.ZipFile]::ExtractToDirectory($zipPath, $staging)
    $appSrc   = Get-ChildItem -Path $staging -Recurse -Directory -Filter 'app' | Select-Object -First 1 -ExpandProperty FullName
    $manifest = Get-ChildItem -Path $staging -Recurse -Filter 'migrations-sqlserver.txt' | Select-Object -First 1 -ExpandProperty FullName
    $verFile  = Get-ChildItem -Path $staging -Recurse -Filter 'VERSION.txt' | Select-Object -First 1 -ExpandProperty FullName
    if ($verFile) { $targetVersion = Get-TideRecordedVersion (Get-Content $verFile) }
    if ($targetVersion -eq '(unknown)') { $targetVersion = $null }
    if (-not $appSrc) { throw "The bundle has no 'app' folder: $BundleZip" }
}
elseif ($AppSource) {
    if (-not (Test-Path (Join-Path $AppSource 'Tide.Web.dll'))) { throw "-AppSource '$AppSource' doesn't contain Tide.Web.dll." }
    $appSrc = Get-TideCleanPath (Resolve-Path $AppSource).Path
}
else {
    Write-Step "Building from source (dotnet publish)"
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw "-Build needs the .NET 10 SDK ('dotnet') on this host." }
    $appSrc = Join-Path $staging 'app'
    & dotnet publish (Join-Path $repoRoot 'src\Tide.Web\Tide.Web.csproj') -c Release -o $appSrc --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)." }
}
# An app folder or a build has no manifest: derive it from the repository, as the Release workflow does.
if (-not $manifest) {
    $migDir = Join-Path $repoRoot 'src\Tide.Migrations.SqlServer\Migrations'
    if (Test-Path $migDir) {
        $manifest = Join-Path $staging 'migrations-sqlserver.txt'
        Get-ChildItem $migDir -Filter '*.cs' |
            Where-Object { $_.Name -match '^\d+_' -and $_.Name -notmatch '\.Designer\.cs$' } |
            ForEach-Object { $_.BaseName } | Sort-Object | Set-Content -Path $manifest -Encoding ascii
    }
}
if (-not (Test-Path (Join-Path $appSrc 'Tide.Web.dll'))) { throw "The staged build has no Tide.Web.dll ($appSrc)." }
# An app folder or a build has no VERSION.txt: read the version stamped into the app (a release build's).
if (-not $targetVersion) {
    $stamped = ([string](Get-Item (Join-Path $appSrc 'Tide.Web.dll')).VersionInfo.ProductVersion) -replace '\+.*$', ''
    $targetVersion = if ($stamped -and $stamped -ne '1.0.0' -and (Test-TideReleaseTag "v$stamped")) { "v$stamped" } else { 'development build' }
}

# --- 3. Preflight -------------------------------------------------------------
Write-Step "Preflight checks"

# 3a. The ASP.NET Core Module (IIS can't host the app without it: 500.19) and the .NET 10 runtime (else 500.30/500.31).
if (-not (Test-TideAspNetCoreModule)) {
    throw "The ASP.NET Core Module isn't installed. Install the .NET 10 Hosting Bundle, run 'iisreset', then run the upgrade again."
}
$runtime = Get-TideAspNetRuntime 10
if (-not $runtime) {
    throw ("The .NET 10 ASP.NET Core runtime wasn't found. Install the .NET 10 Hosting Bundle (not just the SDK), run " +
           "'iisreset', then run the upgrade again; otherwise the app fails to start (500.30).")
}
Write-Info "ASP.NET Core Module and the .NET $runtime runtime are present."

# 3a'. WEBSOCKETS, a warning only: without them TIDE still works, over long polling (a request per keystroke), so they
# never stop an upgrade. Install-Tide.ps1 installs the feature and turns them on for the site.
try {
    $ws = Test-TideWebSockets -IisPath $iisPath -SitePath $SitePath
    if ($ws.Ok -and $ws.Warnings.Count -gt 0) { Write-Warn2 ("WARNING: couldn't check WebSockets fully. $($ws.Warnings -join ' ') Without them TIDE falls back to long polling. " + (Get-TideWebSocketFixText)) }
    elseif ($ws.Ok) { Write-Info "WebSockets: the IIS WebSocket Protocol feature is installed and on for '$iisPath'." }
    else { Write-Warn2 ("WARNING: WebSockets won't work, so TIDE falls back to long polling. $($ws.Problems -join ' ') " + (Get-TideWebSocketFixText)) }
} catch {
    Write-Warn2 "WARNING: couldn't check WebSockets ($($_.Exception.Message)). $(Get-TideWebSocketFixText)"
}

# 3b. SQL reachable.
try { [void](Invoke-DbScalar 'SELECT 1') } catch { throw "Can't reach the database $DbName on $SqlInstance ($($_.Exception.Message))." }
Write-Info "Database reachable."

# 3c. MIGRATIONS: the database's history against the release's manifest. Stops on a lineage mismatch, and on a
# DbaApplies install unless every migration in the manifest is already applied.
if ($manifest -and (Test-Path $manifest)) {
    $expectedIds = @(Get-Content $manifest | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    $hasAppSchema = [int](Invoke-DbScalar "SELECT CASE WHEN OBJECT_ID(@t) IS NULL THEN 0 ELSE 1 END" @{ '@t' = (Get-TideSchemaMarkerTable) }) -eq 1
    $appliedIds = @(Get-AppliedMigrations)
    $migrations = Compare-TideMigrations -Expected $expectedIds -Applied $appliedIds -HasAppSchema $hasAppSchema -DbaApplies $dbaApplies
    if (-not $migrations.Ok) { throw $migrations.Message }
    Write-Info "Migrations: $($migrations.Message)"
} elseif ($dbaApplies) {
    throw ("There's no migration manifest (migrations-sqlserver.txt) to check this DbaApplies database against, so the " +
           "upgrade can't tell whether the DBA has applied this release's schema. Nothing has been changed. Upgrade from " +
           "the release bundle (-Version or -BundleZip), or from the repository (-Build).")
} else {
    Write-Warn2 "No migration manifest: skipping the migration check (a lineage mismatch can't be caught before deploying)."
}

# 3d. DATA-FOLDER SETTINGS. Every store the release keeps under the data root must be configured: one added in a later
# release is missing from an older install's file, and its store would fall back to App_Data in the read-only web root.
$template = $null
if ($appSrc) {
    $template = Get-ChildItem -Path (Split-Path -Parent $appSrc) -Recurse -Filter 'appsettings.Production.template.json' -ErrorAction SilentlyContinue |
        Select-Object -First 1 -ExpandProperty FullName
}
if (-not $template) { $t2 = Join-Path $deployDir 'appsettings.Production.template.json'; if (Test-Path $t2) { $template = $t2 } }
$missingData = @()
$templateCfg = $null
if ($template) {
    # The template has placeholders where JSON values go (the AD groups, a bool): only its strings are read here, so
    # those are made valid first.
    $templateText = (Get-Content $template -Raw -Encoding UTF8) -replace '\[\s*__GROUP_[A-Z]+__\s*\]', '[]' -replace '__APPLY_MIGRATIONS__', 'true'
    $templateCfg = ConvertFrom-TideJson $templateText
    $wanted = @(Get-TideDataRootSettings $templateCfg)
    if (-not $DataRoot) { $DataRoot = Resolve-TideDataRoot $liveCfg $wanted }
    $missingData = @(Find-TideMissingDataSettings $liveCfg $wanted)
    if ($missingData.Count -eq 0) { Write-Info "Data-folder settings: all present." }
    elseif (-not $DataRoot) {
        throw ("appsettings.Production.json is missing data-folder setting(s) this release uses (" +
               (($missingData | ForEach-Object { $_.Path }) -join ', ') + ") and the data root can't be worked out from " +
               "the others. Nothing has been changed. Run it again with -DataRoot <your data root, such as E:\TideData>.")
    } else {
        Write-Warn2 ("Data-folder settings missing; they'll be added under $DataRoot after the backup: " +
                     (($missingData | ForEach-Object { "$($_.Path) = $(Join-Path $DataRoot $_.Suffix)" }) -join '; '))
    }
} else {
    Write-Warn2 "No appsettings.Production.template.json found: skipping the data-folder settings check."
}

# 3e. SERVER SETTINGS ADDED SINCE THIS INSTALL WAS MADE, added after the backup at the value the app already uses.
$releaseDefaults = $null; $serverSettingsToAdd = @()
$releaseBase = Join-Path $appSrc 'appsettings.json'
if (Test-Path $releaseBase) {
    $releaseDefaults = Read-TideJsonFile $releaseBase
    $probeCfg = Read-TideJsonFile $prodSettings
    $serverSettingsToAdd = @(Find-TideMissingServerSettings $probeCfg $releaseDefaults)
    # Tried here on a copy, so a file that can't take them (a value where a section belongs) stops before anything changes.
    try { Add-TideServerSettings $probeCfg $releaseDefaults $templateCfg $serverSettingsToAdd }
    catch { throw "$($_.Exception.Message) Fix appsettings.Production.json and run the upgrade again." }
    if ($serverSettingsToAdd.Count -eq 0) { Write-Info "Server settings: all present." }
    else {
        $sections = @(Get-TideServerSettingPaths | Where-Object { $p = $_; @($serverSettingsToAdd | Where-Object { $_ -eq $p -or $_.StartsWith("${p}:") }).Count })
        Write-Info ("Server settings to add to appsettings.Production.json at the values in use (nothing changes until you " +
                    "edit them): " + ($sections -join ', '))
    }
} else {
    Write-Warn2 "The release's appsettings.json wasn't found: skipping the server settings check."
}
# Adding settings rewrites the file through ConvertTo-Json, which can't keep comments: said now, while nothing has changed.
if (($missingData.Count -or $serverSettingsToAdd.Count) -and (Test-TideJsonHasExtras (Get-Content $prodSettings -Raw -Encoding UTF8))) {
    Write-Warn2 ("appsettings.Production.json has comments (or trailing commas). Adding the settings above writes it again " +
                 "without them; the backup below keeps the file as it is now, to copy your notes back from.")
}

# 3f. THE /health AND /api/v1 CARVE-OUTS. IIS answers 401, before the app runs, on a path that isn't carved out of
# Windows authentication: the warm-up asks /health, and an API token has no Windows sign-in. Read here; any that differs is set after the backup, before the new
# build starts.
$appcmd = Join-Path $env:WINDIR 'System32\inetsrv\appcmd.exe'
$carveOuts = @(Sync-TideAnonymousCarveOuts -SiteName $iisPath -Appcmd $appcmd -DryRun)
$carveOutsToSet = @($carveOuts | Where-Object { $_.State -ne 'ok' })
if ($carveOutsToSet.Count -eq 0) {
    Write-Info "Anonymous carve-outs: all set ($(($carveOuts | ForEach-Object { "$($_.Path): $($_.Settings)" }) -join '; '))."
} else {
    if (-not (Test-Path $appcmd)) {
        throw ("The anonymous carve-out(s) need configuring ($(($carveOutsToSet | ForEach-Object { $_.Path }) -join ', ')), but " +
               "appcmd.exe isn't at $appcmd. Nothing has been changed. Install the IIS management tools " +
               "(Install-WindowsFeature Web-Server -IncludeManagementTools), then run it again.")
    }
    foreach ($c in $carveOutsToSet) { Write-Warn2 "Anonymous carve-out for $($c.Path) ($($c.Settings)) to configure after the backup: $($c.Changes -join '; ')" }
}

# 3g. The warm-up decides whether the new build started by asking $ProbeUrl. Asked now, before anything is stopped: any
# HTTP answer shows this host can reach it. No answer at all would make the warm-up roll back a build that started.
# An answer the warm-up would read as "not started" (a 400 for the Host, or a page that isn't TIDE's even signed in)
# stops the run here too, as the current build answers just as the new one will.
$hostSent = if ($ProbeHost) { $ProbeHost } else { $probeUri.Authority }
$unreachable = "Nothing has been changed. Pass -HealthUrl with an address this host can reach (the address you open " +
               "TIDE at, or one of its binding's: http://localhost:<port>/, https://10.0.0.5/), then run it again."
[System.Net.ServicePointManager]::ServerCertificateValidationCallback = { $true }
try {
    $reach = Invoke-SiteProbe $ProbeUrl
    $reachVerdict = Get-TideProbeVerdict $reach -Health
    $rootReach = $null
    if ($reach.Status -ne 400 -and $reachVerdict -in 'auth', 'other') { $rootReach = Invoke-SiteProbe $HealthUrl -WithCredentials }
} finally { [System.Net.ServicePointManager]::ServerCertificateValidationCallback = $null }
# A timeout is an answer too slow, not none: the current build may be starting cold.
if ($null -eq $reach.Status -and $reach.Error -ne 'Timeout') {
    throw "This host can't reach the site at $ProbeUrl ($($reach.Detail)), so the upgrade couldn't tell whether the new build started. $unreachable"
}
if ($reach.Status -eq 400) {
    throw ("The site at $ProbeUrl answered '$($reach.Detail)' to Host '$hostSent'. TIDE answers 400 to a host name " +
           "AllowedHosts (in $prodSettings) doesn't list, and IIS to one no binding has, so the upgrade would read the new " +
           "build as not started and roll it back. Nothing has been changed. Add the name to AllowedHosts (semicolon " +
           "separated) or pass -HealthUrl with the address you open TIDE at, then run it again.")
}
Write-Info "Probe reaches the site ($($reach.Detail))."
if ($rootReach) {
    Write-Info "Signed-in probe of ${HealthUrl}: $($rootReach.Detail)"
    if ($null -eq $rootReach.Status -and $rootReach.Error -ne 'Timeout') {
        throw ("$ProbeUrl answered '$($reach.Detail)', not TIDE's status, and $HealthUrl couldn't be reached " +
               "($($rootReach.Detail)), so the upgrade couldn't tell whether the new build started. $unreachable")
    }
    if ((Get-TideProbeVerdict $rootReach) -eq 'other') {
        throw ("$ProbeUrl answered '$($reach.Detail)' and $HealthUrl, signed in, '$($rootReach.Detail)' (Host '$hostSent'): " +
               "neither is TIDE, so the upgrade would read the new build as not started and roll it back. Nothing has been " +
               "changed. Check that the address reaches this site and that AllowedHosts lists its name, then run it again.")
    }
}

# --- 4. Confirm ---------------------------------------------------------------
$currentVerFile = Join-Path $SitePath 'VERSION.txt'
$currentVersion = if (Test-Path $currentVerFile) { Get-TideRecordedVersion (Get-Content $currentVerFile) } else {
    $live = ([string](Get-Item (Join-Path $SitePath 'Tide.Web.dll') -ErrorAction SilentlyContinue).VersionInfo.ProductVersion) -replace '\+.*$', ''
    if ($live -and (Test-TideReleaseTag "v$live")) { "v$live" } else { '(unknown)' }
}
Write-Host ""
Write-Host "Ready to upgrade '$iisPath':" -ForegroundColor White
Write-Host "    Current version : $currentVersion"
Write-Host "    Target version  : $targetVersion"
Write-Host "    Site folder     : $SitePath"
Write-Host "    Database        : $DbName on $SqlInstance ($schemaMode)"
Write-Host ""
# At ConfirmImpact Medium ShouldProcess says yes without asking, so it can't stand in for the prompt; under -WhatIf
# the run stops here.
if ($dryRun) {
    Write-Host "What if: Performing the operation ""Upgrade TIDE"" on target ""$iisPath"". Nothing has been changed."
    return
}
if (-not $PSCmdlet.ShouldProcess($iisPath, "Upgrade TIDE")) { Write-Warn2 "Aborted."; return }
if (-not $Force -and -not $PSBoundParameters.ContainsKey('Confirm')) {
    $ans = Read-Host "Back up and deploy? (y/N)"
    if ($ans -notmatch '^(y|yes)$') { Write-Warn2 "Aborted."; return }
}

# --- 5. Back up ---------------------------------------------------------------
if (-not $BackupRoot) { $BackupRoot = Join-Path (Split-Path -Parent $SitePath) "tide-upgrade-backups\$stamp" }
New-Item -ItemType Directory -Force -Path $BackupRoot | Out-Null

if (-not $SkipDbBackup) {
    Write-Step "Backing up the database (server-side, to SQL Server's default backup folder)"
    $bakName = "$DbName-preupgrade-$stamp.bak"
    # COPY_ONLY, so this extra full backup doesn't become the base of the scheduled differentials or log chain.
    $backupSql = "BACKUP DATABASE $(Get-SqlName $DbName) TO DISK = @bak WITH COPY_ONLY, INIT, CHECKSUM, NAME = N'TIDE pre-upgrade $stamp'"
    try {
        [void](Invoke-DbScalar "$backupSql, COMPRESSION" @{ '@bak' = $bakName } 0)
    } catch {
        # Express has no backup compression: retry without it before giving up.
        try { [void](Invoke-DbScalar $backupSql @{ '@bak' = $bakName } 0) }
        catch { throw "The database backup failed ($($_.Exception.Message)). Fix it, or run again with -SkipDbBackup if your backup product's is current." }
    }
    # The folder is on the SQL Server, so it's joined as text: Join-Path fails when its drive doesn't exist here.
    $bakDir = "$(Invoke-DbScalar "SELECT CAST(SERVERPROPERTY('InstanceDefaultBackupPath') AS nvarchar(4000))")"
    Write-Info "Database backup: $(if ($bakDir) { $bakDir.TrimEnd('\') + '\' + $bakName } else { $bakName }) (on the SQL Server)"
} else { Write-Warn2 "Skipping the database backup (-SkipDbBackup)." }

Write-Step "Backing up the settings and the current files (the rollback point)"
Copy-Item $prodSettings (Join-Path $BackupRoot 'appsettings.Production.json') -Force
$rollbackDir = Join-Path $BackupRoot 'site-rollback'
& robocopy $SitePath $rollbackDir /E /XD logs App_Data /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "Couldn't snapshot the current site for rollback (robocopy exit $LASTEXITCODE). Nothing has been deployed." }
Write-Info "Rollback snapshot: $rollbackDir"

# --- 5b. Add the missing data-folder settings (the settings are backed up above) ---------
if ($missingData.Count -gt 0) {
    Write-Step "Adding missing data-folder settings to appsettings.Production.json"
    $liveCfg = Read-TideJsonFile $prodSettings
    # A new store folder gets the app identity's Modify, as the installer grants the data root.
    $pool = Get-Item "IIS:\AppPools\$AppPoolName"
    $appIdentity = if ($pool.processModel.identityType -eq 'SpecificUser' -and $pool.processModel.userName) { $pool.processModel.userName } else { "IIS APPPOOL\$AppPoolName" }
    foreach ($m in $missingData) {
        $value = Join-Path $DataRoot $m.Suffix
        Set-TideJsonValue $liveCfg $m.Path $value
        if (-not (Test-Path $value)) {
            New-Item -ItemType Directory -Force -Path $value | Out-Null
            $acl = Get-Acl $value
            $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($appIdentity, 'Modify', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
            Set-Acl -Path $value -AclObject $acl
        }
        Write-Info "$($m.Path) = $value"
    }
    $liveCfg | ConvertTo-Json -Depth 32 | Set-Content -Path $prodSettings -Encoding UTF8
}

# --- 5c. Add the server settings the preflight found missing ---------
if ($serverSettingsToAdd.Count -gt 0) {
    Write-Step "Adding server settings to appsettings.Production.json (at the values in use; docs/INSTALL.md says what each does)"
    $liveCfg = Read-TideJsonFile $prodSettings
    $toAdd = @(Find-TideMissingServerSettings $liveCfg $releaseDefaults)
    Add-TideServerSettings $liveCfg $releaseDefaults $templateCfg $toAdd
    $liveCfg | ConvertTo-Json -Depth 32 | Set-Content -Path $prodSettings -Encoding UTF8
    foreach ($a in $toAdd) { Write-Info "$a = $(ConvertTo-Json -Compress -InputObject (Get-TideJsonNode $releaseDefaults $a))" }
}

# --- 5d. Set the carve-outs the preflight found missing or wrong --------
# Before the pool stops, so a failure leaves the current build running and nothing deployed.
if ($carveOutsToSet.Count -gt 0) {
    Write-Step "Configuring the anonymous carve-outs ($(($carveOutsToSet | ForEach-Object { "$($_.Path): $($_.Settings)" }) -join '; '))"
    try { $carveOuts = @(Sync-TideAnonymousCarveOuts -SiteName $iisPath -Appcmd $appcmd) }
    catch { throw "$($_.Exception.Message) The upgrade stopped before deploying: the current build is still running. Backups are in $BackupRoot." }
    foreach ($c in $carveOuts) { Write-Info "$($c.Path): $(if ($c.State -eq 'ok') { 'already set' } else { "configured ($($c.Changes -join '; '))" })." }
}

# --- 6. Deploy ---------------------------------------------------------------
Write-Step "Stopping app pool '$AppPoolName'"
# Until the worker has exited and let go of the DLLs, not a fixed few seconds.
Stop-TideAppPool -Name $AppPoolName

# What the database has applied before the new build runs, so a rollback can tell whether it migrated.
$appliedBeforeDeploy = @()
try { $appliedBeforeDeploy = @(Get-AppliedMigrations) }
catch { Write-Warn2 "Couldn't read the migration history before deploying: $($_.Exception.Message)" }

# A rollback copy that fails (often the same locked DLL that failed the deploy) leaves a mix of old and new files. Say so
# and leave the pool stopped, rather than start it and report the previous build restored.
function Restore-PreviousBuild([string] $why) {
    & robocopy $rollbackDir $SitePath /MIR /XF appsettings.Production.json /XD logs App_Data /R:5 /W:3 /NFL /NDL /NJH /NJS /NP | Out-Null
    $restoreRc = $LASTEXITCODE
    if ($restoreRc -ge 8) {
        Stop-WebAppPool -Name $AppPoolName -ErrorAction SilentlyContinue
        throw ("Upgrade failed ($why), and restoring the previous build's files failed too (robocopy exit $restoreRc), so " +
               "the site folder holds a mix of old and new files. The app pool is left STOPPED. Copy the snapshot " +
               "$rollbackDir over $SitePath (keep appsettings.Production.json, logs and App_Data), then start the app pool. " +
               "If the new build applied migrations, restore the pre-upgrade database backup first (docs/UPGRADE.md).")
    }
}

Write-Step "Deploying the new files (keeping appsettings.Production.json, logs and App_Data)"
& robocopy $appSrc $SitePath /E /XF appsettings.Production.json VERSION.txt /XD logs App_Data /R:2 /W:2 /NFL /NDL /NJH /NJS /NP | Out-Null
$rc = $LASTEXITCODE
if ($rc -ge 8) {
    Write-Warn2 "The copy failed (robocopy exit $rc): rolling the files back."
    Restore-PreviousBuild "the copy failed (robocopy exit $rc)"
    Start-WebAppPool -Name $AppPoolName -ErrorAction SilentlyContinue
    throw "Upgrade aborted during the copy; the previous build was restored."
}
# A copy only adds and overwrites: what the earlier release had and this one doesn't would still be there (as the
# installer does: under wwwroot, and the assemblies at the top of the folder).
$removed = @(Remove-TideStaleFiles -From $appSrc -To $SitePath)
if ($removed.Count) { Write-Info "Removed $($removed.Count) file(s) the earlier release had and this one doesn't: $($removed -join ', ')" }

# As the installer sets them: no scheduled recycle, no overlap.
Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name recycling.periodicRestart.time -Value '00:00:00'
Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name recycling.disallowOverlappingRotation -Value $true

Write-Step "Starting app pool"
Start-WebAppPool -Name $AppPoolName

# --- 7. Warm up (applies pending migrations on AppMigrates) + verify ----------
Write-Step $(if ($dbaApplies) { "Warming up" } else { "Warming up (the first start applies pending migrations)" })
[System.Net.ServicePointManager]::ServerCertificateValidationCallback = { $true }
$deadline = (Get-Date).AddSeconds($WarmupTimeoutSec)
$started = $false; $lastStatus = $null; $verdict = 'none'; $lastDetail = $null
while ((Get-Date) -lt $deadline) {
    $probe = Invoke-SiteProbe $ProbeUrl
    if ($probe.Detail -ne $lastDetail) { Write-Info "Probe: $($probe.Detail)"; $lastDetail = $probe.Detail }
    $lastStatus = $probe.Status
    $verdict = Get-TideProbeVerdict $probe -Health
    if ($verdict -eq 'started') { $started = $true; break }
    if ($verdict -in 'crashed', 'auth', 'other') { break }
    Start-Sleep -Seconds 3   # still starting, or its database not answering yet
}
if ($verdict -in 'auth', 'other') {
    if ($verdict -eq 'auth') {
        # /health isn't anonymous on this site although the carve-out above is set (a web.config or a delegated setting
        # overrides it), so IIS answered.
        Write-Warn2 "$ProbeUrl answered $lastStatus (Windows authentication, not the app)."
    } else {
        Write-Warn2 "$ProbeUrl answered '$lastDetail', which isn't TIDE's status."
    }
    # Ask the site root as the signed-in user instead: a page there is the app's own answer.
    Write-Warn2 "Probing $HealthUrl as the signed-in user instead."
    $probe = Invoke-SiteProbe $HealthUrl -WithCredentials
    Write-Info "Probe: $($probe.Detail)"
    $lastStatus = $probe.Status
    switch (Get-TideProbeVerdict $probe) {
        'started' { $started = $true }
        'auth' {
            Write-Warn2 ("Couldn't prove the new build started: the site root was answered by Windows authentication too. " +
                         "Open $HealthUrl in a browser now; if it fails, roll back by hand (docs/UPGRADE.md).")
            $started = $true
        }
        'other' { Write-Warn2 "$HealthUrl answered '$($probe.Detail)', which isn't a TIDE page." }
    }
}
[System.Net.ServicePointManager]::ServerCertificateValidationCallback = $null
if (-not $started -and $null -eq $lastStatus) {
    Write-Warn2 ("No answer from $ProbeUrl within $WarmupTimeoutSec seconds (last: $lastDetail). If the site works in a " +
                 "browser, this host can't reach it by that name: pass -HealthUrl with an address it can reach.")
}
if (-not $started -and $verdict -eq 'unhealthy') {
    Write-Warn2 "TIDE started but its database didn't answer within $WarmupTimeoutSec seconds (/health: Unhealthy)."
}

if (-not $started -or ($lastStatus -ge 500)) {
    Write-Warn2 "The app didn't come up (last status: $(if ($lastStatus) { $lastStatus } else { 'no response' })). Checking the event log..."
    try {
        Get-WinEvent -FilterHashtable @{ LogName = 'Application'; Level = 2; StartTime = (Get-Date).AddMinutes(-15) } -MaxEvents 5 -ErrorAction SilentlyContinue |
            Where-Object { $_.ProviderName -match 'AspNetCore|\.NET Runtime' } |
            ForEach-Object { Write-Host "    [$($_.TimeCreated)] $($_.Message.Split([Environment]::NewLine)[0])" -ForegroundColor Red }
    } catch { }
    Write-Warn2 "Rolling back to the previous build."
    try { Stop-TideAppPool -Name $AppPoolName }
    catch { Write-Warn2 $_.Exception.Message }
    Restore-PreviousBuild "the new build didn't start"
    # If the new build recorded migrations, the previous build would run against a schema it doesn't know. Leave the pool
    # stopped until the database backup is restored.
    $migrated = @()
    try {
        $now = @(Get-AppliedMigrations)
        $migrated = @($now | Where-Object { $appliedBeforeDeploy -notcontains $_ })
    } catch { Write-Warn2 "Couldn't read the migration history to check what the new build applied: $($_.Exception.Message)" }
    if ($migrated.Count -gt 0) {
        throw ("Upgrade failed. The previous build's files are back, but the new build had already applied " +
               "$($migrated.Count) migration(s) ($([string]::Join(', ', @($migrated | Select-Object -First 3)))), so the app pool " +
               "is left STOPPED. Restore the pre-upgrade database backup (in SQL Server's default backup folder), then start " +
               "the app pool. Review the event-log error above and docs/UPGRADE.md.")
    }
    Start-WebAppPool -Name $AppPoolName -ErrorAction SilentlyContinue
    $poolState = (Get-WebAppPoolState -Name $AppPoolName -ErrorAction SilentlyContinue).Value
    $poolNote = if ($poolState -eq 'Started') { "the app pool restarted" } else { "the app pool did NOT start (start '$AppPoolName' in IIS Manager)" }
    throw ("Upgrade failed: the previous build has been restored and $poolNote. The database is unchanged by the new " +
           "build. Review the event-log error above and docs/UPGRADE.md.")
}

# Record the deployed version, so the next upgrade can show current -> target, and what it was upgraded from, when and
# by whom.
$record = New-TideVersionRecord -Target $targetVersion -Previous $currentVersion -When (Get-TideStamp -Format 'yyyy-MM-dd HH:mm zzz') `
    -By ([Security.Principal.WindowsIdentity]::GetCurrent().Name)
Set-Content -Path (Join-Path $SitePath 'VERSION.txt') -Value $record -Encoding ascii

# Keep this release's deploy scripts beside the site (administrators only), so the next upgrade is -Version vX.Y.Z.
$kept = $null
try {
    $newDeploy = if ($BundleZip) { Get-ChildItem -Path $staging -Recurse -Directory -Filter 'deploy' | Select-Object -First 1 -ExpandProperty FullName } else { $deployDir }
    if (-not $newDeploy) { $newDeploy = $deployDir }
    $kept = Publish-TideDeployScripts -SourceDeployDir $newDeploy -SitePath $SitePath -SiteName $SiteName -AppPath $AppPath
    if ($kept) { Write-Info "Deploy scripts kept in $kept (next time: & '$kept\Upgrade-Tide.ps1' -Version <vX.Y.Z>)" }
} catch { Write-Warn2 "Couldn't keep a copy of the deploy scripts beside the site: $($_.Exception.Message)" }
# Apart, so a task that can't be re-pointed isn't reported as a failed copy.
try { Update-TideTaskPaths -KeptDir $kept -SitePath $SitePath }
catch { Write-Warn2 "Couldn't point the TIDE scheduled tasks at $kept ($($_.Exception.Message)). Run each script's -Register again from there." }

$verifySite = $SiteName
Write-Step "Upgrade complete."
Write-Host @"

TIDE answered ($lastStatus): the new build started$(if ($dbaApplies) { '.' } else { ' and applied any pending migrations.' })

Verify:
  .\Verify-Install.ps1 -Url $HealthUrl -AppPoolName $AppPoolName -SiteName $verifySite
  Then sign in, and check Administration > System > Diagnostics.

Backups for rollback are under:
  $BackupRoot
  (the settings and site-rollback\; the database backup is in SQL Server's default backup folder)
"@ -ForegroundColor Green
