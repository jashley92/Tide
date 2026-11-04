# Shared helpers for the backup and restore-drill scripts (Backup-Tide.ps1, Test-TideRestore.ps1). Dot-source this file
# after lib\TideSql.ps1, lib\TideJson.ps1 and lib\TidePath.ps1.
# ASCII only: Windows PowerShell 5.1 misreads non-ASCII in BOM-less files.

# The site's settings as the app reads them: appsettings.json, then appsettings.Production.json over it.
function Read-TideSite {
    param([Parameter(Mandatory)] [string] $SitePath)
    $site = (Resolve-Path $SitePath).Path
    $prod = Join-Path $site 'appsettings.Production.json'
    if (-not (Test-Path $prod)) { throw "appsettings.Production.json isn't in $site. Give -SitePath, the TIDE site folder." }
    $base = Join-Path $site 'appsettings.json'
    $layers = @()
    if (Test-Path $base) { $layers += (Read-TideJsonFile $base) }
    $layers += (Read-TideJsonFile $prod)
    return [pscustomobject]@{ Folder = $site; Layers = $layers }
}

# One setting by its path ('Files:RootPath'): the last layer that sets it wins, as in the app.
function Get-TideSetting {
    param([Parameter(Mandatory)] $Site, [Parameter(Mandatory)] [string] $Path)
    $found = $null
    foreach ($layer in $Site.Layers) {
        $node = $layer
        foreach ($part in $Path.Split(':')) {
            if ($null -eq $node -or $node -isnot [System.Management.Automation.PSCustomObject] -or -not $node.PSObject.Properties[$part]) { $node = $null; break }
            $node = $node.$part
        }
        if ($null -ne $node -and "$node" -ne '') { $found = $node }
    }
    return $found
}

# A path setting resolved as the app resolves it (against the site folder), or $null when it isn't set.
function Get-TidePath {
    param([Parameter(Mandatory)] $Site, [Parameter(Mandatory)] [string] $Path, [string] $Default)
    $p = Get-TideSetting $Site $Path
    if (-not $p) { $p = $Default }
    if (-not $p) { return $null }
    if ([System.IO.Path]::IsPathRooted($p)) { return [System.IO.Path]::GetFullPath($p) }
    return [System.IO.Path]::GetFullPath((Join-Path $Site.Folder $p))
}

# The SQL Server instance and database the site's connection string names, and whether it checks the certificate.
function Get-TideDatabase {
    param([Parameter(Mandatory)] $Site)
    $provider = Get-TideSetting $Site 'Database:Provider'
    if ("$provider" -ne 'SqlServer') { throw "The site's database isn't SQL Server (Database:Provider is '$provider'); these scripts work with SQL Server." }
    $cs = Get-TideSetting $Site 'ConnectionStrings:Default'
    if (-not $cs) { throw 'The site has no ConnectionStrings:Default.' }
    return ConvertFrom-TideConnectionString $cs
}

# The folders a backup set holds besides the database: the screenshots (Files:RootPath, which also holds the metrics
# drop folder) and the Data Protection key ring, which keeps sign-ins and antiforgery tokens valid after a restore.
# Label is the folder's name inside a backup set.
function Get-TideBackupFolders {
    param([Parameter(Mandatory)] $Site)
    $list = @(
        [pscustomobject]@{ Label = 'files';   Key = 'Files:RootPath';         Path = (Get-TidePath $Site 'Files:RootPath' 'App_Data/files') }
        [pscustomobject]@{ Label = 'dp-keys'; Key = 'DataProtection:KeyPath'; Path = (Get-TidePath $Site 'DataProtection:KeyPath') }
    )
    return @($list | Where-Object { $_.Path })
}

# Where the file store keeps a file (DiskFileStore): <root>\<first two of the id>\<id>.bin, the id without dashes.
function Get-TideStoredFilePath([string] $Root, [guid] $Id) {
    $n = $Id.ToString('N')
    return [System.IO.Path]::Combine($Root, $n.Substring(0, 2), "$n.bin")
}

# Copies a folder with robocopy; returns its file count and bytes. Robocopy's exit codes 0-7 are success.
function Copy-TideFolder {
    param([Parameter(Mandatory)] [string] $From, [Parameter(Mandatory)] [string] $To, [string[]] $ExcludeDirs = @())
    $From = Get-TideCleanPath $From; $To = Get-TideCleanPath $To   # a trailing backslash would break robocopy's quoting
    New-Item -ItemType Directory -Force -Path $To | Out-Null
    $rcArgs = @($From, $To, '/E', '/COPY:DAT', '/DCOPY:T', '/R:2', '/W:5', '/NP', '/NFL', '/NDL', '/NJH', '/NJS')
    if ($ExcludeDirs.Count) { $rcArgs += "/XD"; $rcArgs += $ExcludeDirs }
    & robocopy @rcArgs | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "Copying $From to $To failed (robocopy exit $LASTEXITCODE)." }
    $files = @(Get-ChildItem -Path $To -Recurse -File -Force -ErrorAction SilentlyContinue)
    return [pscustomobject]@{ Files = $files.Count; Bytes = [long](($files | Measure-Object -Property Length -Sum).Sum) }
}

# The complete backup sets under a destination (a TIDE-* folder with backup-set.json), newest first by the stamp that
# ends each name. With -Server and -Site, only the sets that install made (as its backup-set.json records): two installs
# may back up to one share, and neither may prune the other's sets or drill a restore of them.
function Get-TideBackupSets([string] $Destination, [string] $Server, [string] $Site) {
    if (-not (Test-Path $Destination)) { return @() }
    $sets = @(Get-ChildItem -Path $Destination -Directory -Filter 'TIDE-*' | Where-Object { Test-Path (Join-Path $_.FullName 'backup-set.json') })
    if ($Server -or $Site) {
        # A manifest that can't be read is skipped (it's not this install's to prune or drill), not a failed run.
        $sets = @($sets | Where-Object {
            try { $m = Read-TideJsonFile (Join-Path $_.FullName 'backup-set.json') } catch { return $false }
            (-not $Server -or "$($m.server)" -eq $Server) -and (-not $Site -or "$($m.site)" -eq $Site)
        })
    }
    return @($sets | Sort-Object { $_.Name.Substring([Math]::Max(0, $_.Name.Length - 13)) } -Descending)
}

# This install's tag, <server>-<site folder>, in the names of its sets, log folder and backup files, so two installs
# backing up to one share (or one SQL Server backup folder) at the same minute never write over each other.
function Get-TideInstallTag($Site) {
    return ($env:COMPUTERNAME + '-' + (Split-Path -Leaf $Site.Folder)) -replace '[^A-Za-z0-9-]', '-'
}

# The name a set's folder starts with for this install: TIDE-<server>-<site folder>-.
function Get-TideBackupSetPrefix($Site) {
    return 'TIDE-' + (Get-TideInstallTag $Site) + '-'
}

# Registers (or replaces) a Windows scheduled task that runs a deploy script as the account given. The task is named
# for its site ("TIDE nightly backup (tide)"), so a second site on the server registers tasks of its own instead of
# replacing the first site's; this site's task of the old, unnamed form ("TIDE nightly backup") is removed. An account
# other than SYSTEM (a gMSA) is given read on the kept deploy folder the task runs from, and named in its site.txt, so
# each upgrade (which rewrites that folder's permissions) grants it again.
function Register-TideTask {
    param(
        [Parameter(Mandatory)] [string] $Name,
        [Parameter(Mandatory)] [string] $Script,
        [Parameter(Mandatory)] [string] $Arguments,
        [Parameter(Mandatory)] $Trigger,
        [string] $Account = 'SYSTEM',
        # The site, to find the copy of the deploy scripts kept beside it (<site>-deploy), which upgrades keep current.
        [string] $SitePath
    )
    $taskName = "TIDE $Name"
    if ($SitePath) {
        $site = $SitePath.TrimEnd([char]92)
        $taskName = "TIDE $Name ($(Split-Path -Leaf $site))"
        $older = Get-ScheduledTask -TaskName "TIDE $Name" -ErrorAction SilentlyContinue
        if ($older -and "$(@($older.Actions)[0].Arguments)" -match ('-SitePath "' + [regex]::Escape($site) + '\\*"')) {
            Unregister-ScheduledTask -TaskName "TIDE $Name" -Confirm:$false
            Write-Host "Removed this site's task 'TIDE $Name' (it's '$taskName' now)." -ForegroundColor Gray
        }
        $keptDir = Join-Path (Split-Path -Parent $site) "$(Split-Path -Leaf $site)-deploy"
        if ($Account -ne 'SYSTEM' -and (Test-Path $keptDir)) { Grant-TideTaskAccountRead -KeptDir $keptDir -Account $Account }
        $kept = Join-Path $keptDir (Split-Path -Leaf $Script)
        if (Test-Path $kept) { $Script = $kept }
        else { Write-Warning "There's no $kept, so the task runs $Script. Move or delete this folder and the task fails: install or upgrade (which keeps the deploy scripts beside the site), then run -Register again." }
    }
    $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$Script`" $Arguments"
    $principal = if ($Account -eq 'SYSTEM') {
        New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
    } else {
        # A group managed service account (DOMAIN\name$) needs no password; another account is asked for one.
        New-ScheduledTaskPrincipal -UserId $Account -LogonType Password -RunLevel Highest
    }
    $settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Hours 12) -MultipleInstances IgnoreNew
    $task = New-ScheduledTask -Action $action -Principal $principal -Trigger $Trigger -Settings $settings `
        -Description "TIDE: $Name (see docs/INSTALL.md, Back up)"
    if ($Account -ne 'SYSTEM' -and -not $Account.EndsWith('$')) {
        $cred = Get-Credential -UserName $Account -Message "Password for $Account, to run the scheduled task"
        Register-ScheduledTask -TaskName $taskName -InputObject $task -User $Account -Password $cred.GetNetworkCredential().Password -Force | Out-Null
    } else {
        Register-ScheduledTask -TaskName $taskName -InputObject $task -Force | Out-Null
    }
    Write-Host "Registered the scheduled task '$taskName', running $Script as $Account." -ForegroundColor Green
}

# Lets an account read a folder and everything in it.
function Grant-TideFolderRead([string] $Path, [string] $Account) {
    $inherit = [System.Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
    $acl = Get-Acl -Path $Path
    $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($Account, 'ReadAndExecute', $inherit, 'None', 'Allow')))
    Set-Acl -Path $Path -AclObject $acl
    Write-Host "  $Account may read $Path." -ForegroundColor Gray
}

# Lets a task account read the kept deploy folder, and names it in site.txt for the next upgrade to grant again.
function Grant-TideTaskAccountRead([string] $KeptDir, [string] $Account) {
    Grant-TideFolderRead -Path $KeptDir -Account $Account
    $siteTxt = Join-Path $KeptDir 'site.txt'
    if ((Test-Path $siteTxt) -and -not (@(Get-Content $siteTxt) -contains "TaskAccount: $Account")) {
        Add-Content -Path $siteTxt -Value "TaskAccount: $Account" -Encoding ascii
    }
}
