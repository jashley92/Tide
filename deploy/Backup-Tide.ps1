#Requires -Version 5.1

<#
.SYNOPSIS
    Backs up TIDE as one set: the database, the screenshots and action files (Files:RootPath), the Data Protection key ring and the site's
    appsettings.Production.json.

.DESCRIPTION
    For a server without a backup product. If your organization's backup product already backs up SQL Server and the data
    root, don't schedule this; keep the database and the data root in the product's backups together. If you run this
    beside a product anyway, add -CopyOnly so its backup chain isn't disturbed.

    Each run makes a folder <Destination>\TIDE-<yyyyMMdd-HHmm> holding:
      db\<database>.bak       a full backup WITH CHECKSUM, then checked with RESTORE VERIFYONLY
      files\files\            the screenshots (and the metrics drop folder under them)
      files\dp-keys\          the Data Protection key ring
      files\site\             the site's appsettings.Production.json
      backup-set.json         what's in the set
    A set holds a whole TIDE: Test-TideRestore.ps1 restores one and checks it.

    The database is in the FULL recovery model (Install-Database.ps1 sets it), so its transaction log is cleared only by
    a log backup: -Log takes one, into <Destination>\log, and -Register schedules one every -LogEveryMinutes (15) beside
    the nightly set. Without them the log grows until the disk is full. A database in the SIMPLE model has no log
    backups, and -Log does nothing.

    A run succeeds only when the backup, its verification and every copy succeed; it then keeps the newest -Keep sets.
    A failure removes the unfinished set and exits with code 1, which a scheduled task reports.

    Run it as an account that can back up the database and check the backup, read the data root, and write to
    -Destination: sysadmin, or db_backupoperator in TIDE's database plus dbcreator on the server (RESTORE VERIFYONLY,
    which checks each set, needs CREATE DATABASE) and SELECT on msdb.dbo.backupset (the set's size is read from it). SQL Server writes the .bak itself, as its service account, so that account needs write to the db folder
    too (or give -DatabaseBackupFolder, a folder as SQL Server sees it). The set holds the key ring: keep -Destination as
    tightly locked down as the data root.

.PARAMETER SitePath
    The TIDE site folder (where appsettings.Production.json is). Default D:\inetpub\tide.

.PARAMETER Destination
    Where the backup sets go: a local folder or a share.

.PARAMETER Keep
    How many sets to keep (default 14). Older ones are removed after a successful run.

.PARAMETER CopyOnly
    A COPY_ONLY backup, which doesn't reset another product's differential base.

.PARAMETER DatabaseBackupFolder
    Where SQL Server writes the .bak, as SQL Server sees the path, when it isn't this server (default: the set's db
    folder). Files written there aren't removed by -Keep: clear them as your SQL Server's maintenance does.

.PARAMETER Log
    A transaction-log backup only, into <Destination>\log (or -DatabaseBackupFolder). Logs older than the oldest set kept
    are removed. Not with -CopyOnly: a backup product that backs up the database takes its log backups too.

.PARAMETER Register
    Creates (or replaces) a nightly scheduled task that runs this script with the same -SitePath, -Destination, -Keep and
    -CopyOnly, at -At, and (unless -CopyOnly, or -LogEveryMinutes 0) a log backup task every -LogEveryMinutes, as
    -TaskAccount (default SYSTEM; a gMSA such as CONTOSO\svc-tide-bk$ needs no password). The tasks run the copy of this
    script kept beside the site (<site>-deploy), which upgrades keep current, when there is one.

.EXAMPLE
    .\Backup-Tide.ps1 -Destination \\backup01\tide

.EXAMPLE
    .\Backup-Tide.ps1 -Destination \\backup01\tide -Register -TaskAccount 'CONTOSO\svc-tide-bk$' -At 01:30
#>
[CmdletBinding()]
param(
    [string] $SitePath = 'D:\inetpub\tide',
    [Parameter(Mandatory)] [string] $Destination,
    [int] $Keep = 14,
    [switch] $CopyOnly,
    [string] $DatabaseBackupFolder,
    [switch] $Log,
    [switch] $Register,
    [int] $LogEveryMinutes = 15,
    [string] $TaskAccount = 'SYSTEM',
    # SQL Server sign-in, where Windows authentication isn't possible (asked for with Get-Credential; never stored).
    [System.Management.Automation.PSCredential] $SqlCredential,
    [string] $At = '01:30'
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib\TideSql.ps1')
. (Join-Path $PSScriptRoot 'lib\TideStamp.ps1')
. (Join-Path $PSScriptRoot 'lib\TideJson.ps1')
. (Join-Path $PSScriptRoot 'lib\TidePath.ps1')
. (Join-Path $PSScriptRoot 'lib\TideBackup.ps1')
# Without a trailing backslash, which breaks the quoting of robocopy's and the scheduled task's arguments (lib\TidePath.ps1).
$SitePath = Get-TideCleanPath $SitePath; $Destination = Get-TideCleanPath $Destination
$DatabaseBackupFolder = Get-TideCleanPath $DatabaseBackupFolder

if ($Log -and $CopyOnly) { throw '-Log with -CopyOnly: a backup product that backs up the database takes its log backups too, and two log chains break each other.' }
if ($Keep -lt 1) { throw '-Keep must be 1 or more.' }

if ($Register) {
    $arguments = "-SitePath $(ConvertTo-TideQuotedArg $SitePath) -Destination $(ConvertTo-TideQuotedArg $Destination) -Keep $Keep" + $(if ($CopyOnly) { ' -CopyOnly' } else { '' }) +
                 $(if ($DatabaseBackupFolder) { " -DatabaseBackupFolder $(ConvertTo-TideQuotedArg $DatabaseBackupFolder)" } else { '' })
    Register-TideTask -Name 'nightly backup' -Script $MyInvocation.MyCommand.Path -SitePath $SitePath -Arguments $arguments `
        -Trigger (New-ScheduledTaskTrigger -Daily -At $At) -Account $TaskAccount
    # An account other than SYSTEM reads the screenshots and the key ring itself: given read on each (a repair of the
    # install keeps it). Its SQL rights come from Install-Database.ps1 -BackupAccount.
    if ($TaskAccount -ne 'SYSTEM') {
        foreach ($f in Get-TideBackupFolders (Read-TideSite $SitePath)) { if (Test-Path $f.Path) { Grant-TideFolderRead -Path $f.Path -Account $TaskAccount } }
    }
    if (-not $CopyOnly -and $LogEveryMinutes -gt 0) {
        $logTrigger = New-ScheduledTaskTrigger -Once -At (Get-Date).Date.AddMinutes(1) -RepetitionInterval (New-TimeSpan -Minutes $LogEveryMinutes)
        Register-TideTask -Name 'log backup' -Script $MyInvocation.MyCommand.Path -SitePath $SitePath -Arguments "$arguments -Log" `
            -Trigger $logTrigger -Account $TaskAccount
    }
    return
}

$site = Read-TideSite $SitePath
$db = Get-TideDatabase $site
$started = Get-Date
$stamp = Get-TideStamp -Format 'yyyyMMdd-HHmm' -When $started
$set = Join-Path $Destination ((Get-TideBackupSetPrefix $site) + $stamp)
function Open-Master { New-TideSqlConnection -SqlInstance $db.Instance -DbName 'master' -SqlCredential $SqlCredential -ValidateServerCertificate:$db.ValidateServerCertificate -ApplicationName 'TIDE backup' }

if ($Log) {
    try {
        $conn = Open-Master
        try {
            $model = Invoke-TideSqlScalar -Connection $conn -Sql 'SELECT recovery_model_desc FROM sys.databases WHERE name = @n' -Parameters @{ '@n' = $db.Name }
            if ("$model" -eq 'SIMPLE') { Write-Host "$($db.Name) is in the SIMPLE recovery model: it has no log to back up." -ForegroundColor Gray; exit 0 }
            # A log backup needs a full one before it (since the database was created, or last switched to FULL).
            $base = Invoke-TideSqlScalar -Connection $conn -Sql 'SELECT COUNT(*) FROM sys.database_recovery_status WHERE database_id = DB_ID(@n) AND last_log_backup_lsn IS NOT NULL' -Parameters @{ '@n' = $db.Name }
            if ([int]$base -eq 0) { Write-Host 'No full backup to follow yet, so no log backup: the nightly set comes first.' -ForegroundColor Yellow; exit 0 }
            # Each install's logs in a folder of their own: the prune below then can't touch another install's chain.
            $tag = Get-TideInstallTag $site
            $logFolder = if ($DatabaseBackupFolder) { $DatabaseBackupFolder } else { Join-Path (Join-Path $Destination 'log') $tag }
            if (-not $DatabaseBackupFolder) { New-Item -ItemType Directory -Force -Path $logFolder | Out-Null }
            $sep = if ($logFolder.Contains('/')) { '/' } else { '\' }
            $trn = $logFolder.TrimEnd('\', '/') + $sep + "$tag-$($db.Name)-$(Get-TideStamp -Format 'yyyyMMdd-HHmmss' -When $started).trn"
            $express = [int](Invoke-TideSqlScalar -Connection $conn -Sql "SELECT CAST(SERVERPROPERTY('EngineEdition') AS int)") -eq 4
            $with = @('CHECKSUM', 'INIT'); if (-not $express) { $with += 'COMPRESSION' }
            $null = Invoke-TideSqlQuery -Connection $conn -Sql ("BACKUP LOG $(Get-SqlName $db.Name) TO DISK = @trn WITH " + ($with -join ', ')) -Parameters @{ '@trn' = $trn }
        } finally { $conn.Dispose() }
        # Logs older than the oldest set kept can't be used by any set kept.
        if (-not $DatabaseBackupFolder) {
            $oldest = @(Get-TideBackupSets $Destination -Server $env:COMPUTERNAME -Site $site.Folder | Select-Object -Last 1)
            if ($oldest.Count -eq 1) {
                Get-ChildItem -Path $logFolder -Filter '*.trn' | Where-Object { $_.LastWriteTime -lt $oldest[0].CreationTime } | Remove-Item -Force
            }
        }
        Write-Host "Log backup: $trn" -ForegroundColor Green
        exit 0
    }
    catch {
        Write-Host "Log backup failed: $($_.Exception.Message)" -ForegroundColor Red
        exit 1
    }
}

try {
    New-Item -ItemType Directory -Force -Path (Join-Path $set 'db') | Out-Null
    $bak = Join-Path (Join-Path $set 'db') "$($db.Name).bak"
    if ($DatabaseBackupFolder) { $bak = $DatabaseBackupFolder.TrimEnd('\', '/') + $(if ($DatabaseBackupFolder.Contains('/')) { '/' } else { '\' }) + "$(Get-TideInstallTag $site)-$($db.Name)-$stamp.bak" }

    Write-Host "Backing up $($db.Name) on $($db.Instance) to $bak" -ForegroundColor Cyan
    $conn = Open-Master
    try {
        # Express has no backup compression.
        $express = [int](Invoke-TideSqlScalar -Connection $conn -Sql "SELECT CAST(SERVERPROPERTY('EngineEdition') AS int)") -eq 4
        $with = @('CHECKSUM', 'INIT', "NAME = N'TIDE $stamp'")
        if (-not $express) { $with += 'COMPRESSION' }
        if ($CopyOnly) { $with += 'COPY_ONLY' }
        $null = Invoke-TideSqlQuery -Connection $conn -Sql ("BACKUP DATABASE $(Get-SqlName $db.Name) TO DISK = @bak WITH " + ($with -join ', ')) -Parameters @{ '@bak' = $bak }
        $null = Invoke-TideSqlQuery -Connection $conn -Sql 'RESTORE VERIFYONLY FROM DISK = @bak WITH CHECKSUM' -Parameters @{ '@bak' = $bak }
        $row = (Invoke-TideSqlQuery -Connection $conn -Parameters @{ '@db' = $db.Name } -Sql @'
SELECT TOP 1 b.backup_size, b.compressed_backup_size
FROM msdb.dbo.backupset b WHERE b.database_name = @db AND b.type = 'D' ORDER BY b.backup_finish_date DESC
'@)[0]
        $dbBytes = if ($row.compressed_backup_size) { [long]$row.compressed_backup_size } else { [long]$row.backup_size }
    } finally { $conn.Dispose() }
    Write-Host "  Database backed up and verified ($([math]::Round($dbBytes / 1MB, 1)) MB)." -ForegroundColor Gray

    $folders = @()
    foreach ($f in Get-TideBackupFolders $site) {
        if (-not (Test-Path $f.Path)) {
            Write-Host "  $($f.Label): $($f.Path) isn't there; nothing to copy." -ForegroundColor Yellow
            $folders += [ordered]@{ label = $f.Label; setting = $f.Key; source = $f.Path; files = 0; bytes = 0; note = 'not there' }
            continue
        }
        $copied = Copy-TideFolder -From $f.Path -To (Join-Path $set "files\$($f.Label)")
        Write-Host "  $($f.Label): $($copied.Files) files" -ForegroundColor Gray
        $folders += [ordered]@{ label = $f.Label; setting = $f.Key; source = $f.Path; files = $copied.Files; bytes = $copied.Bytes }
    }

    # The site's own settings, as they are now, for a rebuild.
    $settingsCopy = Join-Path $set 'files\site\appsettings.Production.json'
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $settingsCopy) | Out-Null
    Copy-Item -LiteralPath (Join-Path $site.Folder 'appsettings.Production.json') -Destination $settingsCopy -Force
    Write-Host "  site: appsettings.Production.json" -ForegroundColor Gray

    $record = [ordered]@{
        format = 1
        createdUtc = Get-TideUtcStamp -When $started
        finishedUtc = Get-TideUtcStamp
        server = $env:COMPUTERNAME
        site = $site.Folder
        database = [ordered]@{ instance = $db.Instance; name = $db.Name; file = $bak; bytes = $dbBytes; copyOnly = [bool]$CopyOnly; verified = $true }
        folders = $folders
        siteSettings = $settingsCopy
    }
    $record | ConvertTo-Json -Depth 5 | Set-Content -Path (Join-Path $set 'backup-set.json') -Encoding UTF8

    # Keep the newest -Keep sets (a set counts once its backup-set.json is written).
    foreach ($old in (Get-TideBackupSets $Destination -Server $env:COMPUTERNAME -Site $site.Folder | Select-Object -Skip $Keep)) {
        Remove-Item -LiteralPath $old.FullName -Recurse -Force
        Write-Host "  Removed the old set $($old.Name)." -ForegroundColor Gray
    }
    Write-Host "Backup set complete: $set" -ForegroundColor Green
}
catch {
    Write-Host "Backup failed: $($_.Exception.Message)" -ForegroundColor Red
    # A set that didn't finish (no backup-set.json) is removed, so it can't be mistaken for one or fill the destination.
    if ((Test-Path $set) -and -not (Test-Path (Join-Path $set 'backup-set.json'))) { Remove-Item -LiteralPath $set -Recurse -Force -ErrorAction SilentlyContinue }
    exit 1
}
# Robocopy leaves 1 ("files copied") in $LASTEXITCODE, which a scheduled task would read as a failure.
exit 0
