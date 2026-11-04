#Requires -Version 5.1
#Requires -RunAsAdministrator

<#
.SYNOPSIS
    Installs (or repairs) the TIDE web app on a Windows Server IIS host: as a site of its own beside any others (the
    default), or, optionally, as an application under a path of an existing site (-ParentSite and -AppPath).

.DESCRIPTION
    Run AFTER Install-Database.ps1. In order:
      1. Puts the app in the site folder: from a release bundle, its already-published app folder (beside this deploy
         folder) is copied in; from the repository, the app is published (dotnet publish -c Release). With
         -SkipPublish it uses an app already copied into the site folder.
      2. Creates the data root (screenshots, the data-protection key ring) OUTSIDE the web root, with nothing inherited:
         SYSTEM and Administrators, and Modify for the app-pool account only.
      3. Writes appsettings.Production.json from the template.
      4. Creates the app pool (No Managed Code, your service account, always running, no overlapping recycle) and the
         site: its own host name, and with a certificate an SNI binding on 443, so other sites' bindings and
         certificates are left alone. Windows Authentication on, Anonymous off, except /health (anonymous on,
         Windows off) and /api/v1 (anonymous on as well as Windows, so a bearer token reaches the app).
      5. Starts the pool and the site.

    Optional: with -ParentSite (an existing IIS site, such as another app's) and -AppPath (such as 'tide'), step 4
    creates an application at https://<that site's host>/<AppPath> instead of a site, in TIDE's own pool and folder.
    The parent site's bindings, certificate and authentication are left alone; Windows Authentication and the /health
    and /api/v1 carve-outs are set on TIDE's path only. The folder must be outside the parent site's, which its own upgrades may
    mirror over. TIDE then shares the parent's origin: see docs/INSTALL.md, "Under another site".

    Integrated authentication throughout: no secrets are written. A gMSA (recommended) needs no password; a domain
    service account's password is passed with -AppPoolCredential, never read from the answers file.
    Re-runnable to repair an install: it stops the pool before publishing over the site, and keeps an existing
    appsettings.Production.json (writing the new one beside it as .new) unless -ReplaceSettings is passed.

.EXAMPLE
    .\Install-Tide.ps1 -ConfigFile .\tide.config.psd1

.EXAMPLE
    .\Install-Tide.ps1 -ConfigFile .\tide.config.psd1 -AppPoolCredential (Get-Credential CONTOSO\svc-tide)

.EXAMPLE
    .\Install-Tide.ps1 -ConfigFile .\tide.config.psd1 -ParentSite Intranet -AppPath tide

.PARAMETER ValidateServerCertificate
    Write a connection string that checks the SQL Server's TLS certificate (TrustServerCertificate=False): it must chain
    to a root the web server trusts and name the host in SqlInstance. Off by default, as most instances use a
    self-signed certificate. Also read from the answers file (ValidateServerCertificate = $true).
#>
[CmdletBinding()]
param(
    [string] $ConfigFile,
    [Alias('AppAccount')] [string] $AppPoolIdentity,
    [System.Management.Automation.PSCredential] $AppPoolCredential,
    [string] $SiteName,
    [string] $ParentSite,
    [string] $AppPath,
    [string] $AppPoolName,
    [string] $Hostname,
    [string] $SitePath,
    [string] $DataRoot,
    [string] $SqlInstance,
    [string] $DbName,
    [ValidateSet('AppMigrates', 'DbaApplies')] [string] $SchemaMode,
    [string] $CertificateThumbprint,
    [string] $AdGroupAnalysts,
    [string] $AdGroupActionOwners,
    [string] $AdGroupReaders,
    [string] $AdGroupAdmins,
    [string] $TimeZone,
    [string] $MeetingTime,
    [string] $TeamName,
    [switch] $SkipPublish,
    [switch] $ReplaceSettings,
    [switch] $ValidateServerCertificate
)

$ErrorActionPreference = 'Stop'
function Write-Step([string] $m) { Write-Host "==> $m" -ForegroundColor Cyan }

# --- Merge an optional answers file (explicit -Params win; passwords are never read from it) ---
$names = 'AppPoolIdentity', 'SiteName', 'ParentSite', 'AppPath', 'AppPoolName', 'Hostname', 'SitePath', 'DataRoot', 'SqlInstance', 'DbName', 'SchemaMode',
         'CertificateThumbprint', 'AdGroupAnalysts', 'AdGroupActionOwners', 'AdGroupReaders', 'AdGroupAdmins', 'TimeZone',
         'MeetingTime', 'TeamName'
if ($ConfigFile) {
    if (-not (Test-Path $ConfigFile)) { throw "Config file not found: $ConfigFile" }
    $cfg = Import-PowerShellDataFile -Path $ConfigFile
    if (-not $cfg.ContainsKey('AppPoolIdentity') -and $cfg.ContainsKey('AppAccount')) { $cfg['AppPoolIdentity'] = $cfg['AppAccount'] }
    foreach ($n in $names) {
        if (-not $PSBoundParameters.ContainsKey($n) -and $cfg.ContainsKey($n) -and "$($cfg[$n])" -ne '') { Set-Variable -Name $n -Value $cfg[$n] }
    }
}
# A switch in the answers file: $true or $false (or the words), anything else is refused rather than guessed at.
if ($ConfigFile -and -not $PSBoundParameters.ContainsKey('ValidateServerCertificate') -and $cfg.ContainsKey('ValidateServerCertificate')) {
    $v = "$($cfg['ValidateServerCertificate'])".Trim()
    if ($v -notin 'True', 'False', '') { throw "ValidateServerCertificate in $ConfigFile must be `$true or `$false; got '$v'." }
    $ValidateServerCertificate = [switch]($v -eq 'True')
}
if (-not $SiteName)    { $SiteName = 'TIDE' }
if (-not $AppPoolName) { $AppPoolName = 'TIDE' }
if (-not $DbName)      { $DbName = 'Tide' }
if (-not $SchemaMode)  { $SchemaMode = 'AppMigrates' }
if (-not $TimeZone)    { $TimeZone = 'America/New_York' }
if (-not $MeetingTime) { $MeetingTime = '10:30' }
if (-not $TeamName)    { $TeamName = 'Cyber Threat Intelligence' }

# The optional install under another site: both names, or neither.
$underParent = [bool]$ParentSite
if ($underParent -xor [bool]$AppPath) { throw "-ParentSite and -AppPath go together: name the existing site and the path TIDE answers at under it (such as 'tide')." }
if ($underParent) {
    $AppPath = $AppPath.Trim().Trim('/')
    if ($AppPath -notmatch '^[A-Za-z0-9_\-]+$') { throw "AppPath must be one path segment of letters, digits, '-' and '_' (such as 'tide'); got '$AppPath'." }
}

$missing = @('AppPoolIdentity', 'SitePath', 'DataRoot', 'SqlInstance' | Where-Object { -not (Get-Variable -Name $_ -ValueOnly) })
if (-not $underParent -and -not $Hostname) { $missing += 'Hostname' }
if ($missing.Count) { throw "Missing required value(s): $($missing -join ', '). Pass them or set them in -ConfigFile (see tide.config.template.psd1)." }

$SitePath = $SitePath.TrimEnd('\')
$DataRoot = $DataRoot.TrimEnd('\')
if ($DataRoot.StartsWith($SitePath + '\', [StringComparison]::OrdinalIgnoreCase) -or $DataRoot -ieq $SitePath) {
    throw "DataRoot ($DataRoot) must be outside the web root ($SitePath): it holds screenshots and the key ring."
}
if ($AppPoolIdentity.EndsWith('$') -and $AppPoolCredential) { throw "$AppPoolIdentity is a gMSA: don't pass -AppPoolCredential." }
if (-not $AppPoolIdentity.EndsWith('$') -and -not $AppPoolCredential) {
    $AppPoolCredential = Get-Credential -UserName $AppPoolIdentity -Message "Password for the TIDE app-pool account $AppPoolIdentity"
}

$deployDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot  = Split-Path -Parent $deployDir
$webProj   = Join-Path $repoRoot 'src\Tide.Web\Tide.Web.csproj'
# A release bundle (tide-<version>.zip) has the published app in app\ beside deploy\, and no source.
$bundleApp = Join-Path $repoRoot 'app'
. (Join-Path $deployDir 'lib\TideIis.ps1')
. (Join-Path $deployDir 'lib\TideAdGroups.ps1')
Import-Module WebAdministration -ErrorAction Stop

if ($underParent) {
    # Under another site: it must exist, TIDE's folder must sit outside its folder (an upgrade of that app may mirror
    # over its own folder, deleting what it doesn't know), and the path must be free or already TIDE's.
    if (-not (Test-Path "IIS:\Sites\$ParentSite")) { throw "There's no IIS site called '$ParentSite' to install TIDE under." }
    $parentPath = [Environment]::ExpandEnvironmentVariables((Get-ItemProperty "IIS:\Sites\$ParentSite" -Name physicalPath)).TrimEnd('\')
    foreach ($pair in @(@($SitePath, $parentPath), @($parentPath, $SitePath))) {
        if ($pair[0] -ieq $pair[1] -or $pair[0].StartsWith($pair[1] + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw "SitePath ($SitePath) and the site '$ParentSite' ($parentPath) must be separate folders, neither inside the other."
        }
    }
    # A parent site may also mirror its deploy scripts into "<site folder>-deploy" beside its folder.
    $parentDeploy = $parentPath + '-deploy'
    if ($SitePath -ieq $parentDeploy -or $SitePath.StartsWith($parentDeploy + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "SitePath ($SitePath) is inside $parentDeploy, which an upgrade of '$ParentSite' may mirror over. Pick a folder of its own."
    }
    $existingApp = Get-WebApplication -Site $ParentSite -Name $AppPath
    if ($existingApp -and [Environment]::ExpandEnvironmentVariables($existingApp.PhysicalPath).TrimEnd('\') -ine $SitePath) {
        throw "'$ParentSite/$AppPath' is already an application, at $($existingApp.PhysicalPath). Pick another AppPath, or set SitePath to that folder to repair it."
    }
    if (-not $existingApp -and (Test-Path (Join-Path $parentPath $AppPath))) {
        throw "'$ParentSite' already has a folder called '$AppPath' ($(Join-Path $parentPath $AppPath)), which TIDE would hide. Pick another AppPath."
    }
    # The site's own host name and scheme, for the settings and the next steps.
    $parentBindings = @((Get-Website -Name $ParentSite).bindings.Collection)
    $https = @($parentBindings | Where-Object { $_.protocol -eq 'https' })
    if (-not $Hostname) {
        $Hostname = @(($https + $parentBindings) | ForEach-Object { ($_.bindingInformation -split ':')[-1] } | Where-Object { $_ })[0]
        if (-not $Hostname) { throw "The site '$ParentSite' has no host name on its bindings. Pass -Hostname with the address readers use for it." }
    }
    if ($CertificateThumbprint) { Write-Host "    CertificateThumbprint is ignored under another site: TIDE uses the binding of '$ParentSite'." -ForegroundColor Yellow }
    # One host name has one HTTP/<host> SPN. With useAppPoolCredentials on, Kerberos tickets are decrypted with the
    # site's pool account, which /tide inherits; TIDE's own account can't, so sign-in falls back to NTLM (or fails).
    try {
        $appPoolCreds = Get-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' -Location $ParentSite `
            -Filter 'system.webServer/security/authentication/windowsAuthentication' -Name 'useAppPoolCredentials' -ErrorAction Stop
        if ($appPoolCreds.PSObject.Properties['Value']) { $appPoolCreds = $appPoolCreds.Value }
    } catch { $appPoolCreds = $null }
    if ("$appPoolCreds" -ieq 'true') {
        Write-Host ("    WARNING: '$ParentSite' decrypts Kerberos tickets with its own app-pool account (useAppPoolCredentials), " +
                    "so TIDE's account can't: sign-in to /$AppPath falls back to NTLM, or fails where NTLM is off. Run TIDE's " +
                    "pool as the same account, or see docs/INSTALL.md, 'Under another site'.") -ForegroundColor Yellow
    }
}

# Another site already answering for this host name would take TIDE's requests (or TIDE its): refuse, don't rebind.
foreach ($site in $(if ($underParent) { @() } else { Get-Website })) {
    if ($site.Name -eq $SiteName) { continue }
    foreach ($b in $site.bindings.Collection) {
        $hostPart = ($b.bindingInformation -split ':')[-1]
        if ($hostPart -and $hostPart -ieq $Hostname) {
            throw "The IIS site '$($site.Name)' already has a binding for $Hostname ($($b.bindingInformation)). Pick another host name for TIDE."
        }
    }
}

# --- 1. Publish ---
$appDll = Join-Path $SitePath 'Tide.Web.dll'
if (Test-Path $appDll) {
    Write-Step "Stopping app pool '$AppPoolName' (the site already has an install)"
    Stop-TideAppPool -Name $AppPoolName
}
if ($SkipPublish) {
    Write-Step "Using the pre-published app at $SitePath"
    if (-not (Test-Path $appDll)) { throw "-SkipPublish was set but $appDll is missing. Publish elsewhere and copy the output into $SitePath first." }
} elseif (Test-Path (Join-Path $bundleApp 'Tide.Web.dll')) {
    # Like a publish over the site, it replaces the app's files and leaves the rest (appsettings.Production.json) alone.
    Write-Step "Copying the release bundle's app ($bundleApp) to $SitePath"
    New-Item -ItemType Directory -Force -Path $SitePath | Out-Null
    Copy-Item -Path (Join-Path $bundleApp '*') -Destination $SitePath -Recurse -Force
    # A copy only adds and overwrites: what the earlier release had and this one doesn't would still be there.
    $removed = @(Remove-TideStaleFiles -From $bundleApp -To $SitePath)
    if ($removed.Count) { Write-Host "    Removed $($removed.Count) file(s) the earlier release had and this one doesn't: $($removed -join ', ')" -ForegroundColor Gray }
} else {
    Write-Step "Publishing TIDE to $SitePath"
    if (-not (Test-Path $webProj)) {
        throw "Found neither the source ($webProj) nor a release bundle's app ($bundleApp). Run this from the deploy folder of the repository or of an unzipped release bundle, or copy a published app into $SitePath and use -SkipPublish."
    }
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw "The .NET 10 SDK ('dotnet') is needed to publish here. Install it, use a release bundle, or publish elsewhere, copy the output into $SitePath and re-run with -SkipPublish."
    }
    New-Item -ItemType Directory -Force -Path $SitePath | Out-Null
    & dotnet publish $webProj -c Release -o $SitePath --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE). A NuGet restore error (NU1100) usually means no package source is registered for this account." }
}

# --- 2. Data root, outside the web root, nothing inherited ---
Write-Step "Creating the data root at $DataRoot"
$dataDirs = @('files', 'dp-keys') | ForEach-Object { Join-Path $DataRoot $_ }
foreach ($d in @($DataRoot) + $dataDirs) { New-Item -ItemType Directory -Force -Path $d | Out-Null }
$inherit = [System.Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit'
$none = [System.Security.AccessControl.PropagationFlags]::None
# On a re-run (a repair), the read rights given since to other accounts are kept: the backup tasks' gMSA reads the
# screenshots and key ring from here (Backup-Tide.ps1 -Register gives it that), and replacing the ACL would stop them.
$extra = @(Get-Acl -Path $DataRoot).Access | Where-Object {
    -not $_.IsInherited -and $_.AccessControlType -eq 'Allow' -and $_.FileSystemRights.ToString() -notmatch 'FullControl|Modify|Write' -and
    $_.IdentityReference.Value -notin @('NT AUTHORITY\SYSTEM', 'BUILTIN\Administrators', $AppPoolIdentity)
}
$acl = New-Object System.Security.AccessControl.DirectorySecurity
$acl.SetAccessRuleProtection($true, $false)
foreach ($sid in 'S-1-5-18', 'S-1-5-32-544') {   # SYSTEM, Administrators (by SID, so any OS language)
    $who = New-Object System.Security.Principal.SecurityIdentifier($sid)
    $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($who, 'FullControl', $inherit, $none, 'Allow')))
}
$acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($AppPoolIdentity, 'Modify', $inherit, $none, 'Allow')))
foreach ($rule in $extra) {
    $acl.AddAccessRule($rule)
    Write-Host "    Kept $($rule.IdentityReference)'s $($rule.FileSystemRights) on the data root." -ForegroundColor Gray
}
Set-Acl -Path $DataRoot -AclObject $acl
# The web root: read and execute only.
$siteAcl = Get-Acl $SitePath
$siteAcl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($AppPoolIdentity, 'ReadAndExecute', $inherit, $none, 'Allow')))
Set-Acl -Path $SitePath -AclObject $siteAcl

# --- 3. appsettings.Production.json ---
Write-Step "Writing appsettings.Production.json"
$scheme = if ($underParent) { $(if ($https.Count) { 'https' } else { 'http' }) } elseif ($CertificateThumbprint) { 'https' } else { 'http' }
$baseUrl = "${scheme}://$Hostname" + $(if ($underParent) { "/$AppPath" } else { '' })
function ConvertTo-JsonText([string] $s) { return ($s | ConvertTo-Json).Trim('"') }
function ConvertTo-GroupList([string] $role, [string] $name) {
    if (-not $name) { return '' }
    $resolved = ConvertTo-TideGroupName $role $name -OnWebServer
    return '"' + (ConvertTo-JsonText $resolved) + '"'
}
# Under another site, TIDE answers at every host name the site does (intranet and intranet.contoso.com, say): the app
# refuses a Host header AllowedHosts doesn't list, so each of the site's binding names goes in, the chosen one first.
$allowedHosts = @($Hostname)
if ($underParent) {
    foreach ($h in @($parentBindings | ForEach-Object { ($_.bindingInformation -split ':')[-1] } | Where-Object { $_ })) {
        if ($allowedHosts -notcontains $h) { $allowedHosts += $h }
    }
    if ($allowedHosts.Count -gt 1) { Write-Host "    AllowedHosts: $($allowedHosts -join ', ') (the host names of '$ParentSite')." -ForegroundColor Gray }
}
$json = Get-Content -Path (Join-Path $deployDir 'appsettings.Production.template.json') -Raw
$values = @{
    '__HOSTNAME__'            = ConvertTo-JsonText $Hostname
    '__ALLOWED_HOSTS__'       = ConvertTo-JsonText ($allowedHosts -join ';')
    '__BASE_URL__'            = ConvertTo-JsonText $baseUrl
    '__SQL_INSTANCE__'        = ConvertTo-JsonText $SqlInstance
    '__DB_NAME__'             = ConvertTo-JsonText $DbName
    '__DATA_ROOT__'           = ConvertTo-JsonText $DataRoot
    '__TIME_ZONE__'           = ConvertTo-JsonText $TimeZone
    '__MEETING_TIME__'        = ConvertTo-JsonText $MeetingTime
    '__TEAM_NAME__'           = ConvertTo-JsonText $TeamName
    '__APPLY_MIGRATIONS__'    = $(if ($SchemaMode -eq 'AppMigrates') { 'true' } else { 'false' })
    '__TRUST_SQL_CERT__'      = $(if ($ValidateServerCertificate) { 'False' } else { 'True' })
    '__GROUP_ANALYSTS__'      = ConvertTo-GroupList 'Analysts' $AdGroupAnalysts
    '__GROUP_ACTIONOWNERS__'  = ConvertTo-GroupList 'Action owners' $AdGroupActionOwners
    '__GROUP_READERS__'       = ConvertTo-GroupList 'Readers' $AdGroupReaders
    '__GROUP_ADMINS__'        = ConvertTo-GroupList 'Admins' $AdGroupAdmins
}
foreach ($k in $values.Keys) { $json = $json.Replace($k, $values[$k]) }
try { $null = $json | ConvertFrom-Json } catch { throw "The generated appsettings.Production.json isn't valid JSON: $($_.Exception.Message)" }
$prodSettings = Join-Path $SitePath 'appsettings.Production.json'
if ((Test-Path $prodSettings) -and -not $ReplaceSettings) {
    Set-Content -Path "$prodSettings.new" -Value $json -Encoding UTF8
    Write-Host "    Kept the existing appsettings.Production.json; the newly generated one is $prodSettings.new." -ForegroundColor Yellow
} else {
    Set-Content -Path $prodSettings -Value $json -Encoding UTF8
}
if (-not $AdGroupAdmins) {
    Write-Host "    NOTE: no Admin group given. Nobody can administer TIDE until a group is mapped to Admin (RoleMapping in $prodSettings)." -ForegroundColor Yellow
}

# --- 4. IIS: pool, site (or application), authentication ---
$iisPath = if ($underParent) { "$ParentSite/$AppPath" } else { $SiteName }
Write-Step "Configuring IIS (pool '$AppPoolName', $(if ($underParent) { "application '$iisPath'" } else { "site '$SiteName'" }))"
if (-not (Test-Path "IIS:\AppPools\$AppPoolName")) { New-WebAppPool -Name $AppPoolName | Out-Null }
Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name managedRuntimeVersion -Value ''      # No Managed Code
Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name startMode -Value AlwaysRunning
Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name processModel.idleTimeout -Value '00:00:00'
# No scheduled recycle (the default is every 29 hours, which drops every open session mid-meeting) and never two
# TIDE processes at once.
Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name recycling.periodicRestart.time -Value '00:00:00'
Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name recycling.disallowOverlappingRotation -Value $true
Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name processModel.identityType -Value SpecificUser
Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name processModel.userName -Value $AppPoolIdentity
$password = if ($AppPoolCredential) { $AppPoolCredential.GetNetworkCredential().Password } else { '' }   # a gMSA: none
Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name processModel.password -Value $password

if ($underParent) {
    # An application in its own pool under the existing site. The site itself (bindings, certificate, authentication,
    # its own pool) isn't touched.
    if (-not $existingApp) { New-WebApplication -Site $ParentSite -Name $AppPath -PhysicalPath $SitePath -ApplicationPool $AppPoolName | Out-Null }
    else { Set-ItemProperty "IIS:\Sites\$ParentSite\$AppPath" -Name applicationPool -Value $AppPoolName }
} elseif (-not (Test-Path "IIS:\Sites\$SiteName")) {
    if ($CertificateThumbprint) {
        # SslFlags 1 (SNI): the certificate is bound to $Hostname:443 only. Without it the binding takes the server-wide
        # 0.0.0.0:443 slot, failing when another site holds it, or taking that site's certificate over.
        New-Website -Name $SiteName -PhysicalPath $SitePath -ApplicationPool $AppPoolName -HostHeader $Hostname -Port 443 -Ssl -SslFlags 1 | Out-Null
        (Get-WebBinding -Name $SiteName -Protocol https).AddSslCertificate(($CertificateThumbprint -replace '\s', ''), 'My')
    } else {
        New-Website -Name $SiteName -PhysicalPath $SitePath -ApplicationPool $AppPoolName -HostHeader $Hostname -Port 80 | Out-Null
        Write-Host "    No certificate given: created an HTTP binding. Add TLS before real intel goes in." -ForegroundColor Yellow
    }
} else {
    # A re-run never changes the existing bindings.
    Set-ItemProperty "IIS:\Sites\$SiteName" -Name physicalPath -Value $SitePath
    Set-ItemProperty "IIS:\Sites\$SiteName" -Name applicationPool -Value $AppPoolName
}

# Windows Authentication on, Anonymous off. These sections are LOCKED at server scope, so the WebAdministration cmdlets
# throw 0x8007000D for a site; appcmd with /commit:apphost writes a <location> entry, which the lock permits.
$appcmd = Join-Path $env:WINDIR 'System32\inetsrv\appcmd.exe'
if (-not (Test-Path $appcmd)) { throw "appcmd.exe not found at $appcmd - is the IIS 'Web-Server' role installed?" }
foreach ($s in @(@{ Name = 'windowsAuthentication'; Enabled = 'true' }, @{ Name = 'anonymousAuthentication'; Enabled = 'false' })) {
    $out = & $appcmd set config "$iisPath" "/section:system.webServer/security/authentication/$($s.Name)" "/enabled:$($s.Enabled)" /commit:apphost 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to set $($s.Name) on '$iisPath' (appcmd exit $LASTEXITCODE): $out. Install the Windows Authentication role feature (Install-WindowsFeature Web-Windows-Auth -IncludeManagementTools) and run elevated."
    }
}
$carved = Sync-TideAnonymousCarveOuts -SiteName $iisPath -Appcmd $appcmd -SetAlways
Write-Host "    Anonymous under: $(($carved | ForEach-Object { $(if ($underParent) { "/$AppPath" } else { '' }) + $_.Path + " ($($_.Settings))" }) -join ', ')" -ForegroundColor Gray

# --- 5. Start ---
if ((Get-WebAppPoolState -Name $AppPoolName).Value -eq 'Stopped') { Start-WebAppPool -Name $AppPoolName } else { Restart-WebAppPool -Name $AppPoolName }
if (-not $underParent) { Start-Website -Name $SiteName -ErrorAction SilentlyContinue }

# Keep these deploy scripts beside the site (administrators only), so an upgrade is one command
# (Upgrade-Tide.ps1 -Version vX.Y.Z), which Administration > System > Updates shows.
try {
    $kept = Publish-TideDeployScripts -SourceDeployDir $deployDir -SitePath $SitePath -SiteName $(if ($underParent) { $ParentSite } else { $SiteName }) `
        -AppPath $(if ($underParent) { $AppPath } else { '' })
    if ($kept) { Write-Host "    Deploy scripts kept in $kept (to upgrade: & '$kept\Upgrade-Tide.ps1' -Version <vX.Y.Z>)" -ForegroundColor Gray }
} catch { Write-Host "    Couldn't keep a copy of the deploy scripts beside the site: $($_.Exception.Message)" -ForegroundColor Yellow }

$verifySite = if ($underParent) { $ParentSite } else { $SiteName }
Write-Step "Install complete."
Write-Host @"

Next steps:
  1. Browse to $baseUrl/ (the first start applies the database schema when SchemaMode is AppMigrates).
  2. Verify:  .\Verify-Install.ps1 -Url $baseUrl/ -AppPoolName $AppPoolName -SiteName $verifySite
  3. Back up the database and $DataRoot together (screenshots live in $DataRoot\files; $DataRoot\dp-keys keeps sign-ins
     alive across restarts): .\Backup-Tide.ps1 -SitePath $SitePath -Destination <backup share> -Register
"@ -ForegroundColor Green
