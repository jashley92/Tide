# IIS helpers shared by Install-Tide.ps1 and Upgrade-Tide.ps1 (and, through lib\TideWebSockets.ps1, Verify-Install.ps1).
#
# Stop-WebAppPool only asks the pool to stop. Its worker (w3wp) finishes open requests, Blazor circuits
# and background jobs first, and holds the app's DLLs until it exits, so copying files straight after (a fixed few
# seconds' wait) fails on locked files part way through.

function Stop-TideAppPool([string] $Name, [int] $TimeoutSeconds = 120) {
    # Stops the pool and waits until it reports Stopped and its worker process has exited. Throws if it doesn't in time.
    Import-Module WebAdministration -ErrorAction Stop
    if (-not (Test-Path "IIS:\AppPools\$Name")) { return }
    if ((Get-WebAppPoolState -Name $Name).Value -ne 'Stopped') { Stop-WebAppPool -Name $Name }
    $pattern = '-ap\s+"' + [regex]::Escape($Name) + '"'
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $state = (Get-WebAppPoolState -Name $Name).Value
        $workers = @(Get-CimInstance Win32_Process -Filter "Name = 'w3wp.exe'" -ErrorAction SilentlyContinue |
            Where-Object { $_.CommandLine -match $pattern })
        if ($state -eq 'Stopped' -and $workers.Count -eq 0) { return }
        Start-Sleep -Seconds 2
    }
    throw ("The app pool '$Name' didn't stop within $TimeoutSeconds seconds: its worker process still holds the app's " +
           "files. Stop the pool in IIS Manager (or end its w3wp.exe), then re-run.")
}

# The paths IIS lets through to the app without a Windows sign-in. The rest of the site stays Windows-authenticated.
# The app still enforces each one: /health (and /health/live below it) answers a status word only, with no detail;
# /api/v1 lets a bearer token (Authorization: Bearer tide_pat_...) reach the app, which still requires a token or a
# Windows sign-in on every call there.
function Get-TideAnonymousPaths { return @('health', 'api/v1') }

# Each carve-out is two <location> settings, applied in this order: anonymous on, then Windows, so the path is never
# left with neither. /health turns Windows off. /api/v1 leaves it on: with both on, IIS passes a request without
# Windows credentials to the app, and adds Negotiate to the app's 401, so a gMSA or a person still signs in there.
function Get-TideCarveOutSettings([string] $Path) {
    $windows = if ($Path -eq 'api/v1') { 'true' } else { 'false' }
    return @(
        [pscustomobject]@{ Section = 'anonymousAuthentication'; Enabled = 'true'   },
        [pscustomobject]@{ Section = 'windowsAuthentication';   Enabled = $windows }
    )
}

function Get-TideCarveOutText([string] $Path) {
    # The carve-out's settings in words, for the scripts' messages: 'anonymous on, Windows off'.
    $w = (Get-TideCarveOutSettings $Path | Where-Object { $_.Section -eq 'windowsAuthentication' }).Enabled
    return "anonymous on, Windows $(if ($w -eq 'true') { 'on' } else { 'off' })"
}

function Get-TideAnonymousPathPurpose([string] $Path) {
    if ($Path -eq 'health') { return 'the health probes to load balancers and monitors' }
    if ($Path -eq 'api/v1') { return 'the API to bearer tokens (the app still requires a token or a Windows sign-in)' }
    return "the app's anonymous endpoint /$Path"
}

function ConvertTo-TideEnabledText($Value) {
    # A config 'enabled' value as 'true', 'false', or $null when unset or unreadable.
    if ($null -eq $Value) { return $null }
    if ($Value.PSObject.Properties['Value']) { $Value = $Value.Value }   # a ConfigurationAttribute, or the bare value
    $s = ([string]$Value).Trim().ToLowerInvariant()
    if ($s -eq 'true' -or $s -eq 'false') { return $s }
    return $null
}

function Get-TideLocationEnabled([string] $Filter, [string] $Location) {
    # A section's 'enabled' in applicationHost.config as IIS applies it: server-wide with no -Location, else at that site
    # or path (its <location> entry, or what it inherits). 'true', 'false', or $null when it can't be read. Reading is
    # allowed although the section is locked. Needs WebAdministration loaded.
    $read = @{ PSPath = 'MACHINE/WEBROOT/APPHOST'; Filter = $Filter; Name = 'enabled'; ErrorAction = 'Stop' }
    if ($Location) { $read.Location = $Location }
    try { $v = Get-WebConfigurationProperty @read } catch { return $null }
    return ConvertTo-TideEnabledText $v
}

function Set-TideApphostEnabled([string] $Appcmd, [string] $Location, [string] $Section, [string] $Enabled) {
    # Sets a section's 'enabled' at <location> with appcmd and /commit:apphost: allowed although the section is locked at
    # server scope, where the WebAdministration cmdlets throw 0x8007000D writing it for a site. Returns ExitCode (0 when
    # it worked) and Output (what appcmd said), for the caller's message. Throws when appcmd.exe isn't there.
    if (-not (Test-Path $Appcmd)) { throw "appcmd.exe not found at $Appcmd - is the IIS 'Web-Server' role installed?" }
    # appcmd reports a failure in its output and exit code; under a caller's 'Stop' a line on stderr would otherwise end
    # the run with the bare text before the caller could say what to do.
    $ErrorActionPreference = 'Continue'
    $out = & $Appcmd set config "$Location" "/section:$Section" "/enabled:$Enabled" /commit:apphost 2>&1
    return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = $out }
}

function Get-TideLocationAuth([string] $SiteName, [string] $Path, [string] $Section) {
    # The 'enabled' value IIS applies at <site>/<path>: its <location> entry's, or the one inherited from the site when
    # there is none. 'true', 'false', or $null when it can't be read. Reading is allowed although the section is locked.
    Import-Module WebAdministration -ErrorAction Stop
    return Get-TideLocationEnabled -Filter "system.webServer/security/authentication/$Section" -Location "$SiteName/$Path"
}

function Sync-TideAnonymousCarveOuts {
    # Applies each path's Get-TideCarveOutSettings (anonymous on; Windows off, or on for /api/v1) at <site>/<path>,
    # with appcmd and /commit:apphost: the authentication sections are LOCKED at server scope, so the WebAdministration cmdlets throw
    # 0x8007000D writing them for a site, while a <location> entry in applicationHost.config is allowed.
    #   -SetAlways : write every setting without reading it first (the installer).
    #   -DryRun    : read only and report what would change.
    # Returns one object per path: Path, Settings (Get-TideCarveOutText), State ('ok', 'configured' or 'would-configure')
    # and Changes (what differed, as "section: was -> now"). Throws on a failed write, naming the fix and what had already been changed.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] [string] $SiteName,
        [string] $Appcmd = (Join-Path $env:WINDIR 'System32\inetsrv\appcmd.exe'),
        [switch] $SetAlways,
        [switch] $DryRun
    )
    if (-not $DryRun -and -not (Test-Path $Appcmd)) {
        throw "appcmd.exe not found at $Appcmd - is the IIS 'Web-Server' role installed?"
    }
    $results = @()
    $done = @()
    foreach ($p in (Get-TideAnonymousPaths)) {
        $changes = @()
        foreach ($s in (Get-TideCarveOutSettings $p)) {
            $was = $null
            if (-not $SetAlways) {
                $was = Get-TideLocationAuth -SiteName $SiteName -Path $p -Section $s.Section
                if ($was -eq $s.Enabled) { continue }
            }
            $wasText = if ($SetAlways) { 'set' } elseif ($null -eq $was) { 'unreadable' } else { $was }
            $changes += "$($s.Section): $wasText -> $($s.Enabled)"
            if ($DryRun) { continue }
            $set = Set-TideApphostEnabled -Appcmd $Appcmd -Location "$SiteName/$p" `
                -Section "system.webServer/security/authentication/$($s.Section)" -Enabled $s.Enabled
            if ($set.ExitCode -ne 0) {
                $already = if ($done.Count -gt 0) { " Already changed: $($done -join '; ')." } else { '' }
                throw ("Failed to set $($s.Section) on '$SiteName/$p' (appcmd exit $($set.ExitCode)): $($set.Output). This opens " +
                       "$(Get-TideAnonymousPathPurpose $p) without a Windows sign-in.$already Fix: run elevated, and " +
                       "make sure the IIS authentication role services are installed (anonymous authentication comes with " +
                       "the Web-Server role; Install-WindowsFeature Web-Server, Web-Windows-Auth -IncludeManagementTools), " +
                       "then re-run. By hand: appcmd set config ""$SiteName/$p"" /section:system.webServer/security/" +
                       "authentication/$($s.Section) /enabled:$($s.Enabled) /commit:apphost")
            }
            $done += "/$p $($s.Section) -> $($s.Enabled)"
        }
        $state = if ($changes.Count -eq 0) { 'ok' } elseif ($DryRun) { 'would-configure' } else { 'configured' }
        $results += [pscustomobject]@{ Path = "/$p"; Settings = (Get-TideCarveOutText $p); State = $state; Changes = $changes }
    }
    return $results
}

function Remove-TideStaleFiles {
    # After a release's app is copied over an older one: removes what the older release shipped and this one doesn't,
    # so a static file a release dropped (an old script, say) isn't still served. Only the app's own files are judged:
    # everything under wwwroot, and the assemblies (*.dll, *.pdb) at the top of the folder. Nothing else is touched
    # (appsettings.Production.json, logs, App_Data, anything an administrator put there). Returns the paths removed.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] [string] $From,
        [Parameter(Mandatory = $true)] [string] $To
    )
    $from = (Resolve-Path -LiteralPath $From).Path.TrimEnd('\')
    $to = (Resolve-Path -LiteralPath $To).Path.TrimEnd('\')
    $stale = @()
    $web = Join-Path $to 'wwwroot'
    if (Test-Path -LiteralPath $web) {
        $stale += @(Get-ChildItem -LiteralPath $web -Recurse -File -Force | Where-Object {
            -not (Test-Path -LiteralPath (Join-Path $from $_.FullName.Substring($to.Length + 1)))
        })
    }
    $stale += @(Get-ChildItem -LiteralPath $to -File -Force | Where-Object {
        $_.Extension -in '.dll', '.pdb' -and -not (Test-Path -LiteralPath (Join-Path $from $_.Name))
    })
    foreach ($f in $stale) { Remove-Item -LiteralPath $f.FullName -Force }
    # Folders under wwwroot the release no longer has, once empty.
    if (Test-Path -LiteralPath $web) {
        Get-ChildItem -LiteralPath $web -Recurse -Directory -Force | Sort-Object { $_.FullName.Length } -Descending |
            Where-Object { -not (Get-ChildItem -LiteralPath $_.FullName -Force) } | ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }
    }
    return @($stale | ForEach-Object { $_.FullName.Substring($to.Length + 1) })
}

function Get-TideAppPoolSid([string] $SiteName) {
    # The SID the site's app pool runs as, or $null when the site, pool or account can't be resolved.
    try {
        if (-not $SiteName -or -not (Test-Path "IIS:\Sites\$SiteName")) { return $null }
        $pool = (Get-ItemProperty "IIS:\Sites\$SiteName" -Name applicationPool)
        if ($pool -isnot [string]) { $pool = $pool.Value }
        if (-not $pool) { return $null }
        $type = [string](Get-ItemProperty "IIS:\AppPools\$pool" -Name processModel.identityType)
        switch ($type) {
            'LocalSystem'    { return New-Object System.Security.Principal.SecurityIdentifier('S-1-5-18') }
            'LocalService'   { return New-Object System.Security.Principal.SecurityIdentifier('S-1-5-19') }
            'NetworkService' { return New-Object System.Security.Principal.SecurityIdentifier('S-1-5-20') }
            'SpecificUser'   { $account = [string](Get-ItemProperty "IIS:\AppPools\$pool" -Name processModel.userName) }
            default          { $account = "IIS AppPool\$pool" }   # ApplicationPoolIdentity
        }
        if (-not $account) { return $null }
        return (New-Object System.Security.Principal.NTAccount($account)).Translate([System.Security.Principal.SecurityIdentifier])
    } catch { return $null }
}

function Test-TideAspNetCoreModule {
    # Whether the ASP.NET Core Module (IIS's host for the app; without it every request is 500.19) is installed, wherever
    # this PowerShell can see it. A 32-bit PowerShell has System32 redirected to SysWOW64 and $env:ProgramFiles pointed
    # at "Program Files (x86)", so the real 64-bit locations (Sysnative, ProgramW6432) and both registry views are checked.
    foreach ($p in @((Join-Path $env:WINDIR 'System32\inetsrv\aspnetcorev2.dll'),
                     (Join-Path $env:WINDIR 'Sysnative\inetsrv\aspnetcorev2.dll'))) {
        if (Test-Path $p) { return $true }
    }
    foreach ($view in 'Registry64', 'Registry32') {
        try {
            $k = [Microsoft.Win32.RegistryKey]::OpenBaseKey('LocalMachine', $view).OpenSubKey('SOFTWARE\Microsoft\IIS Extensions\IIS AspNetCore Module V2')
            if ($k) { $k.Dispose(); return $true }
        } catch { }
    }
    $pf64 = if ($env:ProgramW6432) { $env:ProgramW6432 } else { $env:ProgramFiles }
    return (Test-Path (Join-Path $pf64 'IIS\Asp.Net Core Module\V2'))
}

function Get-TideAspNetRuntime([int] $Major = 10) {
    # The newest ASP.NET Core shared framework of that major installed (the one IIS runs the app on), or $null. The
    # 64-bit folder, even from a 32-bit shell. Preview folders (10.0.0-rc.1...) aren't versions and are left out.
    $pf64 = if ($env:ProgramW6432) { $env:ProgramW6432 } else { $env:ProgramFiles }
    $root = Join-Path $pf64 'dotnet\shared\Microsoft.AspNetCore.App'
    if (-not (Test-Path $root)) { return $null }
    $found = foreach ($d in Get-ChildItem $root -Directory -ErrorAction SilentlyContinue) {
        $v = $null
        if ([version]::TryParse($d.Name, [ref]$v) -and $v.Major -eq $Major) { $v }
    }
    return ($found | Sort-Object -Descending | Select-Object -First 1)
}

function Get-TideKeptDeployFolder([string] $SitePath) {
    # Where the install and the upgrade keep a copy of the deploy scripts: beside the site folder, as <site folder>-deploy
    # (D:\inetpub\tide -> D:\inetpub\tide-deploy), so two sites under one parent don't share it.
    $site = $SitePath.TrimEnd([char]92)
    return Join-Path (Split-Path -Parent $site) "$(Split-Path -Leaf $site)-deploy"
}

function Publish-TideDeployScripts([string] $SourceDeployDir, [string] $SitePath, [string] $SiteName, [string] $AppPath) {
    # Keeps a copy of the deploy scripts beside the site, so the next upgrade is one command (Upgrade-Tide.ps1 -Version
    # vX.Y.Z) and Administration > Updates can show it. Administrators and SYSTEM may change the folder; the app-pool
    # identity may only read it (the Updates page looks for the script), and no one else may open it: it can hold the
    # answers file, which names the AD role groups, SQL host and service account. Never the data root: the app pool can
    # write there, and these scripts are run elevated. site.txt names the IIS site (and the application path under it),
    # so the command the Updates page shows upgrades this install and no other. Returns the folder, or $null.
    $target = Get-TideKeptDeployFolder $SitePath
    if (-not (Test-Path (Join-Path $SourceDeployDir 'Upgrade-Tide.ps1'))) { return $null }
    if ((Resolve-Path $SourceDeployDir).Path.TrimEnd('\') -ieq $target.TrimEnd('\')) { return $target }   # running from it
    # The task accounts this site's scheduled tasks run as (Register-TideTask names them): read before the copy, which
    # replaces site.txt, and granted read again below, since this rewrites the folder's permissions.
    $siteTxt = Join-Path $target 'site.txt'
    $taskAccounts = @(if (Test-Path $siteTxt) { Get-Content $siteTxt | Where-Object { $_ -like 'TaskAccount: *' } })
    New-Item -ItemType Directory -Force -Path $target | Out-Null
    & robocopy $SourceDeployDir $target /MIR /XF tide.config.psd1 /R:2 /W:2 /NFL /NDL /NJH /NJS /NP | Out-Null
    # robocopy reports success when it can't read the target at all, so check the copy landed.
    if ($LASTEXITCODE -ge 8 -or -not (Test-Path (Join-Path $target 'Upgrade-Tide.ps1'))) {
        throw "Copying the deploy scripts to $target failed (robocopy exit $LASTEXITCODE)."
    }
    $lines = @("Site: $SiteName")
    if ($AppPath) { $lines += "AppPath: $AppPath" }
    $lines += $taskAccounts
    Set-Content -Path $siteTxt -Value $lines -Encoding ascii

    # Then lock it down; Set-Acl passes the inheritable rules on to what was just copied.
    $inherit = [System.Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
    $none    = [System.Security.AccessControl.PropagationFlags]::None
    $acl = New-Object System.Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true, $false)   # no inherited rights
    foreach ($sidText in 'S-1-5-32-544', 'S-1-5-18') {   # Administrators, SYSTEM
        $sid = New-Object System.Security.Principal.SecurityIdentifier($sidText)
        $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($sid, 'FullControl', $inherit, $none, 'Allow')))
    }
    $iisPath = if ($AppPath) { "$SiteName\$AppPath" } else { $SiteName }
    $poolSid = Get-TideAppPoolSid -SiteName $iisPath
    if ($poolSid) {
        $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($poolSid, 'ReadAndExecute', $inherit, $none, 'Allow')))
    } else {
        Write-Warning "Couldn't resolve the app-pool identity for '$iisPath'; $target is readable by Administrators and SYSTEM only."
    }
    foreach ($line in $taskAccounts) {
        $account = $line.Substring('TaskAccount: '.Length).Trim()
        try { $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($account, 'ReadAndExecute', $inherit, $none, 'Allow'))) }
        catch { Write-Warning "The scheduled tasks' account $account couldn't be given read on ${target}: $($_.Exception.Message). Its tasks can't run until it can." }
    }
    Set-Acl -Path $target -AclObject $acl
    return $target
}

function Update-TideTaskPaths([string] $KeptDir, [string] $SitePath) {
    # A task registered from a release folder (Backup-Tide.ps1 -Register and the like) runs that folder's copy of the
    # script, and fails once the folder is gone. Point this site's TIDE tasks at the kept copy, which every upgrade
    # refreshes. Only tasks whose arguments name this site (-SitePath) are touched, so another site's are left alone.
    if (-not $KeptDir -or -not (Get-Command Get-ScheduledTask -ErrorAction SilentlyContinue)) { return }
    $site = $SitePath.TrimEnd([char]92)
    foreach ($task in @(Get-ScheduledTask -TaskName 'TIDE *' -ErrorAction SilentlyContinue)) {
        $action = @($task.Actions)[0]
        if (-not $action -or -not $action.Arguments) { continue }
        # The path as registered: with or without a trailing backslash (doubled when the scripts quote it).
        if ($action.Arguments -notmatch ('-SitePath "' + [regex]::Escape($site) + '\\*"')) { continue }
        if ($action.Arguments -notmatch '-File "([^"]+[.]ps1)"') { continue }
        $file = $Matches[1]
        $want = Join-Path $KeptDir (Split-Path -Leaf $file)
        if ($file -ieq $want -or -not (Test-Path $want)) { continue }
        try {
            $new = New-ScheduledTaskAction -Execute $action.Execute -Argument $action.Arguments.Replace("-File `"$file`"", "-File `"$want`"")
            Set-ScheduledTask -TaskName $task.TaskName -TaskPath $task.TaskPath -Action $new -ErrorAction Stop | Out-Null
            Write-Host "    Scheduled task '$($task.TaskName)' now runs $want (it ran $file)." -ForegroundColor Gray
        } catch {
            Write-Host "    Scheduled task '$($task.TaskName)' runs $file, which upgrades don't refresh, and couldn't be changed ($($_.Exception.Message)). Run its script's -Register again from $KeptDir." -ForegroundColor Yellow
        }
    }
}
