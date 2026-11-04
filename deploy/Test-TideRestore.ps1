#Requires -Version 5.1

<#
.SYNOPSIS
    The restore drill: restores a TIDE backup set into a scratch database and proves the set whole.

.DESCRIPTION
    Takes a set Backup-Tide.ps1 made (-Destination, the newest set there; or -BackupSet, one set's folder), restores its
    database as a scratch database (-ScratchDatabase, default Tide_RestoreDrill_<server>_<site folder>) on -SqlInstance (default the site's own
    instance; give a recovery server's to test there), and checks:
      - DBCC CHECKDB finds no corruption;
      - the restored database has TIDE's migration history;
      - every screenshot the database lists is in the set's files folder at its recorded size, and -Sample of them (200;
        -HashAll for every one) hash to their recorded SHA-256;
      - the set holds the Data Protection key ring and the site's appsettings.Production.json.
    The findings are written as restore-check-<time>.json in the set. A failed check exits with code 2, which a scheduled
    task reports. The scratch database is dropped afterwards unless -KeepRestored.

    Run it as an account that can restore databases on -SqlInstance (dbcreator, or sysadmin) and read the backup.

.EXAMPLE
    .\Test-TideRestore.ps1 -Destination \\backup01\tide

.EXAMPLE
    .\Test-TideRestore.ps1 -BackupSet \\backup01\tide\TIDE-20261101-0130 -SqlInstance SQLREC01

.EXAMPLE
    .\Test-TideRestore.ps1 -Destination \\backup01\tide -Register -At 03:00
#>
[CmdletBinding()]
param(
    [string] $SitePath = 'D:\inetpub\tide',
    [string] $Destination,
    [string] $BackupSet,
    [string] $ScratchDatabase = 'Tide_RestoreDrill',
    [string] $SqlInstance,
    [int] $Sample = 200,
    [switch] $HashAll,
    [switch] $KeepRestored,
    [switch] $Register,
    [string] $TaskAccount = 'SYSTEM',
    # SQL Server sign-in, where Windows authentication isn't possible (asked for with Get-Credential; never stored).
    [System.Management.Automation.PSCredential] $SqlCredential,
    [string] $At = '03:00'
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib\TideSql.ps1')
. (Join-Path $PSScriptRoot 'lib\TideStamp.ps1')
. (Join-Path $PSScriptRoot 'lib\TideJson.ps1')
. (Join-Path $PSScriptRoot 'lib\TidePath.ps1')
. (Join-Path $PSScriptRoot 'lib\TideBackup.ps1')
# Without a trailing backslash, which breaks the quoting of the scheduled task's arguments (lib\TidePath.ps1).
$SitePath = Get-TideCleanPath $SitePath; $Destination = Get-TideCleanPath $Destination; $BackupSet = Get-TideCleanPath $BackupSet

if ($Register) {
    if (-not $Destination) { throw 'A scheduled drill restores the newest backup set: give -Destination with -Register.' }
    $arguments = "-SitePath $(ConvertTo-TideQuotedArg $SitePath) -Destination $(ConvertTo-TideQuotedArg $Destination) -Sample $Sample" +
        $(if ($SqlInstance) { " -SqlInstance $(ConvertTo-TideQuotedArg $SqlInstance)" } else { '' })
    # Every fourth Sunday (about monthly).
    $trigger = New-ScheduledTaskTrigger -Weekly -WeeksInterval 4 -DaysOfWeek Sunday -At $At
    Register-TideTask -Name 'monthly restore drill' -Script $MyInvocation.MyCommand.Path -SitePath $SitePath -Arguments $arguments -Trigger $trigger -Account $TaskAccount
    return
}

$site = Read-TideSite $SitePath
$live = Get-TideDatabase $site
if (-not $SqlInstance) { $SqlInstance = $live.Instance }
# Each install's drill its own scratch database, so two drilling on one instance can't drop each other's.
if (-not $PSBoundParameters.ContainsKey('ScratchDatabase')) { $ScratchDatabase = 'Tide_RestoreDrill_' + ((Get-TideInstallTag $site) -replace '-', '_') }
if ($ScratchDatabase -eq $live.Name -and $SqlInstance -eq $live.Instance) { throw "The scratch database can't be the live one ($($live.Name))." }
if (-not $BackupSet) {
    if (-not $Destination) { throw 'Give -Destination (the newest backup set there is used) or -BackupSet.' }
    $BackupSet = @(Get-TideBackupSets $Destination -Server $env:COMPUTERNAME -Site $site.Folder | Select-Object -First 1 -ExpandProperty FullName)[0]
    if (-not $BackupSet) { throw "No complete backup set of this install (a TIDE-* folder whose backup-set.json names this server and site) in $Destination." }
}
$manifest = Read-TideJsonFile (Join-Path $BackupSet 'backup-set.json')
$from = Split-Path -Leaf $BackupSet
$stamp = Get-TideStamp -Format 'yyyyMMdd-HHmm'
$checks = @()
$restoredHere = $false
function Open-Master { New-TideSqlConnection -SqlInstance $SqlInstance -DbName 'master' -SqlCredential $SqlCredential -ValidateServerCertificate:$live.ValidateServerCertificate -ApplicationName 'TIDE restore drill' }
function Add-Check([string] $Name, [bool] $Passed, [string] $Detail) {
    $script:checks += [ordered]@{ check = $Name; passed = $Passed; detail = $Detail }
    Write-Host "  [$(if ($Passed) { ' OK ' } else { 'FAIL' })] ${Name}: $Detail" -ForegroundColor $(if ($Passed) { 'Green' } else { 'Red' })
}

try {
    Write-Host "Restoring $from into $ScratchDatabase on $SqlInstance" -ForegroundColor Cyan
    $conn = Open-Master
    try {
        $exists = Invoke-TideSqlScalar -Connection $conn -Sql 'SELECT COUNT(*) FROM sys.databases WHERE name = @n' -Parameters @{ '@n' = $ScratchDatabase }
        if ([int]$exists -gt 0) {
            Write-Host "  Removing the scratch database left by an earlier drill." -ForegroundColor Gray
            $null = Invoke-TideSqlQuery -Connection $conn -Sql "ALTER DATABASE $(Get-SqlName $ScratchDatabase) SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE $(Get-SqlName $ScratchDatabase);"
        }
        $bak = $manifest.database.file
        $files = Invoke-TideSqlQuery -Connection $conn -Sql 'RESTORE FILELISTONLY FROM DISK = @bak' -Parameters @{ '@bak' = $bak }
        $dataDir = "$(Invoke-TideSqlScalar -Connection $conn -Sql "SELECT CAST(SERVERPROPERTY('InstanceDefaultDataPath') AS nvarchar(260))")"
        $logDir = "$(Invoke-TideSqlScalar -Connection $conn -Sql "SELECT CAST(SERVERPROPERTY('InstanceDefaultLogPath') AS nvarchar(260))")"
        $moves = @(); $params = @{ '@bak' = $bak }; $i = 0
        foreach ($f in $files) {
            $dir = if ($f.Type -eq 'L') { $logDir } else { $dataDir }
            $sep = if ($dir.Contains('/')) { '/' } else { '\' }
            $ext = [System.IO.Path]::GetExtension("$($f.PhysicalName)")
            $params["@l$i"] = $f.LogicalName
            $params["@p$i"] = $dir.TrimEnd('\', '/') + $sep + "$ScratchDatabase-$($f.LogicalName)$ext"
            $moves += "MOVE @l$i TO @p$i"; $i++
        }
        $null = Invoke-TideSqlQuery -Connection $conn -Parameters $params -Sql (
            "RESTORE DATABASE $(Get-SqlName $ScratchDatabase) FROM DISK = @bak WITH CHECKSUM, RECOVERY, " + ($moves -join ', '))
        $restoredHere = $true
        Add-Check 'restore' $true "restored $bak"

        Write-Host "  DBCC CHECKDB" -ForegroundColor Gray
        try {
            $null = Invoke-TideSqlQuery -Connection $conn -Sql "DBCC CHECKDB($(Get-SqlName $ScratchDatabase)) WITH NO_INFOMSGS, ALL_ERRORMSGS"
            Add-Check 'dbcc' $true 'DBCC CHECKDB found no errors'
        } catch { Add-Check 'dbcc' $false $_.Exception.Message }

        $scratch = Get-SqlName $ScratchDatabase
        $migrations = [int](Invoke-TideSqlScalar -Connection $conn -Sql "IF OBJECT_ID(N'$($ScratchDatabase.Replace("'", "''")).dbo.__EFMigrationsHistory') IS NULL SELECT 0 ELSE SELECT COUNT(*) FROM $scratch.dbo.__EFMigrationsHistory")
        Add-Check 'schema' ($migrations -gt 0) "$migrations migration(s) recorded"
        $stored = @(Invoke-TideSqlQuery -Connection $conn -Sql "SELECT Id, SizeBytes, Sha256 FROM $scratch.dbo.Files")
    } finally { $conn.Dispose() }

    # Every screenshot the database lists, in the set's copy of the file store, at its recorded size; a sample re-hashed.
    $root = Join-Path $BackupSet 'files\files'
    $missing = @(); $wrongSize = @(); $wrongHash = @()
    $toHash = if ($HashAll -or $stored.Count -le $Sample) { $stored } elseif ($Sample -le 0) { @() } else { @($stored | Get-Random -Count $Sample) }
    $hashIds = @{}; foreach ($r in $toHash) { if ($r) { $hashIds["$($r.Id)"] = $true } }
    foreach ($r in $stored) {
        if (-not $r) { continue }
        $p = Get-TideStoredFilePath $root ([guid]"$($r.Id)")
        if (-not (Test-Path -LiteralPath $p -PathType Leaf)) { $missing += "$($r.Id)"; continue }
        if ((Get-Item -LiteralPath $p).Length -ne [long]$r.SizeBytes) { $wrongSize += "$($r.Id)"; continue }
        if ($hashIds.ContainsKey("$($r.Id)") -and (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash -ine "$($r.Sha256)") { $wrongHash += "$($r.Id)" }
    }
    $fileDetail = "$($stored.Count) listed, $($hashIds.Count) re-hashed"
    if ($missing.Count) { $fileDetail += "; $($missing.Count) missing (first: $($missing[0]))" }
    if ($wrongSize.Count) { $fileDetail += "; $($wrongSize.Count) the wrong size (first: $($wrongSize[0]))" }
    if ($wrongHash.Count) { $fileDetail += "; $($wrongHash.Count) with the wrong SHA-256 (first: $($wrongHash[0]))" }
    Add-Check 'files' (($missing.Count + $wrongSize.Count + $wrongHash.Count) -eq 0) $fileDetail

    $keyRing = @(Get-ChildItem -Path (Join-Path $BackupSet 'files\dp-keys') -Filter '*.xml' -File -ErrorAction SilentlyContinue)
    Add-Check 'dp-keys' ($keyRing.Count -gt 0) "$($keyRing.Count) key(s) in the Data Protection key ring"
    $settingsCopy = Join-Path $BackupSet 'files\site\appsettings.Production.json'
    $settingsOk = $false
    try { $null = Read-TideJsonFile $settingsCopy; $settingsOk = $true } catch { }
    Add-Check 'settings' $settingsOk $(if ($settingsOk) { 'appsettings.Production.json is there and reads' } else { "no readable $settingsCopy" })
}
catch {
    Add-Check 'drill' $false $_.Exception.Message
}
finally {
    if ($restoredHere -and -not $KeepRestored) {
        try {
            $conn = Open-Master
            try { $null = Invoke-TideSqlQuery -Connection $conn -Sql "ALTER DATABASE $(Get-SqlName $ScratchDatabase) SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE $(Get-SqlName $ScratchDatabase);" }
            finally { $conn.Dispose() }
        } catch { Write-Warning "The scratch database $ScratchDatabase couldn't be removed: $($_.Exception.Message)" }
    } elseif ($restoredHere) {
        Write-Host "Kept the database $ScratchDatabase on $SqlInstance." -ForegroundColor Yellow
    }
}

$passed = @($checks | Where-Object { -not $_.passed }).Count -eq 0
$report = [ordered]@{ set = $from; checkedUtc = Get-TideUtcStamp; server = $env:COMPUTERNAME; passed = $passed; checks = $checks }
$reportPath = Join-Path $BackupSet "restore-check-$stamp.json"
try { $report | ConvertTo-Json -Depth 5 | Set-Content -Path $reportPath -Encoding UTF8; Write-Host "  Findings: $reportPath" -ForegroundColor Gray }
catch { Write-Warning "The findings couldn't be written to $reportPath ($($_.Exception.Message))." }
if (-not $passed) { Write-Host "Restore drill failed on $from." -ForegroundColor Red; exit 2 }
Write-Host "Restore verified: $from." -ForegroundColor Green
exit 0
