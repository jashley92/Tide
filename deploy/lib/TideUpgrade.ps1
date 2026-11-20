# Helpers for Upgrade-Tide.ps1 that decide things without touching IIS, SQL or the network, so they can be tested.
# Dot-source this file.
# ASCII only: Windows PowerShell 5.1 misreads non-ASCII in BOM-less files.

# A release tag, as the Release workflow accepts it: vX.Y.Z, or vX.Y.Z-alpha.N (-beta.N, -rc.N) for a pre-release.
function Test-TideReleaseTag([string] $Tag) {
    return [bool]($Tag -match '^v\d{1,4}\.\d{1,4}\.\d{1,4}(-(alpha|beta|rc)\.\d{1,4})?$')
}

# The tag a bundle's VERSION.txt names ("Version: v0.1.0-alpha.2"), or $null.
function Get-TideVersionFromText([string] $Text) {
    if ($Text -match '(?m)^Version:\s*(\S+)\s*$' -and (Test-TideReleaseTag $Matches[1])) { return $Matches[1] }
    return $null
}

# The SHA-256 a .sha256 file states (sha256sum's "<hash>  <name>", or the hash alone), or $null when it holds none.
function ConvertFrom-TideSha256File([string] $Text) {
    if (-not $Text) { return $null }
    $first = ($Text.Trim() -split '\s+')[0]
    if ($first -match '^[0-9a-fA-F]{64}$') { return $first.ToUpperInvariant() }
    return $null
}

# Whether a file's SHA-256 is the one expected. A missing or malformed expectation is never a match.
function Test-TideFileHash([string] $Path, [string] $Expected) {
    if ($Expected -notmatch '^[0-9a-fA-F]{64}$') { return $false }
    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
    return $actual -ieq $Expected
}

# The URL a binding answers at. IIS writes a binding as "IP:port:host". With no host name the site answers on that IP
# only (10.0.0.5:443:), so the probe asks that address, not localhost. An IPv6 address is bracketed ([::1]:443:), so the
# string can't simply be split on ':'. A port other than the scheme's default is kept. $Path is an application's path
# under the site ('tide'), when TIDE is installed under another site.
function Get-TideBindingUrl([string] $BindingInformation, [string] $Scheme, [int] $DefaultPort, [string] $Path) {
    $ip = ''; $port = ''; $hostHeader = ''
    if ($BindingInformation -match '^(?<ip>\[[^\]]*\]|[^:]*):(?<port>\d*):(?<host>.*)$') {
        $ip = $Matches['ip']; $port = $Matches['port']; $hostHeader = $Matches['host']
    }
    if (-not $hostHeader) { $hostHeader = if ($ip -and $ip -ne '*') { $ip } else { 'localhost' } }
    $portPart = if ($port -and $port -ne "$DefaultPort") { ":$port" } else { '' }
    $sub = if ($Path) { $Path.Trim('/') + '/' } else { '' }
    return "${Scheme}://$hostHeader$portPart/$sub"
}

# Whether a host name is one the app's host filtering (AllowedHosts, semicolon separated) lets in. No list, or '*',
# takes any host; '*.corp.example' takes any name under it.
function Test-TideHostAllowed([string] $Name, [string[]] $Allowed) {
    $Allowed = @($Allowed | Where-Object { $_ })
    if ($Allowed.Count -eq 0 -or $Allowed -contains '*') { return $true }
    foreach ($a in $Allowed) {
        if ($a -ieq $Name) { return $true }
        if ($a.StartsWith('*.') -and $Name.EndsWith($a.Substring(1), [StringComparison]::OrdinalIgnoreCase)) { return $true }
    }
    return $false
}

# What an answer from the site means:
#   'started'   TIDE answered (its /health said Healthy or Degraded; signed in, the site root gave a page or redirect);
#   'unhealthy' TIDE answered /health but its database doesn't (503 Unhealthy): still starting, or failing;
#   'auth'      Windows authentication answered instead of the app (the /health carve-out is missing);
#   'other'     something other than TIDE answered (IIS's own 404, another site);
#   'crashed'   the app failed (5xx: 500.30, 502.5);
#   'none'      nothing yet (no answer, a proxy's 407, IIS's 500.37 while a long migration runs, the rate limit's 429).
# $Probe is @{ Status = <int or $null>; Body = <text> }. -Health judges an answer from /health; without it, from the
# signed-in site root.
function Get-TideProbeVerdict($Probe, [switch] $Health) {
    $c = $Probe.Status
    $body = "$($Probe.Body)".Trim()
    if ($null -eq $c -or $c -eq 407 -or $c -eq 429) { return 'none' }
    # 500.37: the app hasn't finished starting within its startupTimeLimit (a long migration); the next request starts it
    # again where the migrations left off, so it's still starting, not a crash.
    if ($c -eq 500 -and $body -match '500\.37') { return 'none' }
    if ($Health -and $c -eq 503 -and $body -eq 'Unhealthy') { return 'unhealthy' }
    if ($c -ge 500) { return 'crashed' }
    if ($c -in 401, 403) { return 'auth' }
    if ($Health) {
        if ($c -eq 200 -and $body -in 'Healthy', 'Degraded') { return 'started' }
        return 'other'
    }
    if ($c -ge 200 -and $c -lt 400) { return 'started' }
    return 'other'
}

# The version a VERSION.txt records ("Version: ..."), or '(unknown)'.
function Get-TideRecordedVersion([string[]] $Lines) {
    $line = @($Lines | Where-Object { $_ -match '^\s*Version:' }) | Select-Object -First 1
    if (-not $line) { return '(unknown)' }
    $v = ($line -replace '^\s*Version:\s*', '').Trim()
    if ($v) { return $v }
    return '(unknown)'
}

# The VERSION.txt the upgrade writes into the site: the version, what it was upgraded from, when and by whom.
function New-TideVersionRecord([string] $Target, [string] $Previous, [string] $When, [string] $By) {
    $lines = @("Version: $Target")
    if ($Previous -and $Previous -ne '(unknown)' -and $Previous -ne $Target) { $lines += "Previous: $Previous" }
    $lines += "Upgraded: $When"
    $lines += "By: $By"
    return $lines
}

# --- Release integrity: the signature and the hash ---------------------------------------------------------------
# The Release workflow signs each bundle with TIDE's release key (RSA-PSS, SHA-256, a 32-byte salt) and publishes
# tide-<v>.zip.sig and tide-<v>.zip.sha256 beside it. The scripts trust the public keys pinned in lib\TideReleaseKeys.ps1
# (docs/decisions/0022-releases-are-signed-with-a-pinned-key.md).

# The keys a TideReleaseKeys.ps1 pins, each @{ Id = '<16 hex>'; Xml = '<RSAKeyValue>...</RSAKeyValue>' }. A file that
# pins none, or no file, gives none.
function Get-TideTrustedKeys([string] $Path) {
    if (-not $Path -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) { return @() }
    $TideReleaseKeys = @()
    . $Path
    return @(@($TideReleaseKeys) | Where-Object { $_ -and $_.Id -and $_.Xml })
}

# An RSA object that does RSA-PSS on this PowerShell. Windows PowerShell 5.1's RSA.Create() is the CryptoAPI provider,
# which has no PSS, so it gets RSACng; PowerShell 7's RSA.Create() does PSS on every platform.
function New-TideRsa {
    if ($PSVersionTable.PSEdition -eq 'Core') { return [System.Security.Cryptography.RSA]::Create() }
    return New-Object System.Security.Cryptography.RSACng
}

# A file's SHA-256 as bytes, read as a stream, since a bundle is large.
function Get-TideFileSha256Bytes([string] $Path) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $stream = [System.IO.File]::OpenRead($Path)
    try { return , $sha.ComputeHash($stream) }
    finally { $stream.Dispose(); $sha.Dispose() }
}

# The id of the pinned key a signature verifies against, or $null when none does. $Sha256 is the signed file's SHA-256
# (Get-TideFileSha256Bytes), $Signature the .sig file's bytes. A key that won't load is passed over.
function Test-TideReleaseSignature([byte[]] $Sha256, [byte[]] $Signature, $Keys) {
    if (-not $Sha256 -or -not $Signature) { return $null }
    foreach ($key in @($Keys)) {
        $rsa = New-TideRsa
        try {
            $rsa.FromXmlString([string]$key.Xml)
            if ($rsa.VerifyHash($Sha256, $Signature, [System.Security.Cryptography.HashAlgorithmName]::SHA256,
                    [System.Security.Cryptography.RSASignaturePadding]::Pss)) { return [string]$key.Id }
        } catch {
            Write-Verbose "Release key $($key.Id) couldn't be used: $($_.Exception.Message)"
        } finally { $rsa.Dispose() }
    }
    return $null
}

# The release tag a bundle's VERSION.txt names, or $null.
function Get-TideBundleTag([string] $ZipPath) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $ZipPath).Path)
    try {
        $entry = $zip.Entries | Where-Object { $_.FullName -match '^[^/\\]+[/\\]VERSION\.txt$' -and $_.Length -lt 4096 } |
            Select-Object -First 1
        if (-not $entry) { return $null }
        $reader = New-Object System.IO.StreamReader($entry.Open())
        try { return Get-TideVersionFromText $reader.ReadToEnd() }
        finally { $reader.Dispose() }
    } finally { $zip.Dispose() }
}

# Checks a bundle before anything in it is expanded or run, and throws (saying "Nothing was changed.") when it's refused.
#   The signature, <zip>.sig beside it: valid against a pinned key, it's TIDE's release; present but not valid, it's
#   refused. Missing, it's refused when keys are pinned unless -AllowUnsigned (a bundle from before signing), and the
#   bundle is checked by its hash alone when none are.
#   The hash: -ExpectedSha256, else <zip>.sha256 beside it, must match. With neither, a bundle that isn't signed is
#   checked against its release's .sha256 on github.com when $Web (Invoke-WebRequest's parameters) is given; one that
#   can't be fetched is a warning.
# Returns Tag, KeyId (the key it's signed by, or $null), HashSource ('expected', 'local', 'release' or $null), Sha256,
# and the Notes and Warnings to show.
function Invoke-TideBundleCheck {
    param(
        [Parameter(Mandatory = $true)] [string] $ZipPath,
        $Keys = @(),
        [switch] $AllowUnsigned,
        [string] $ExpectedSha256,
        [string] $Tag,
        [string] $Repository = 'jashley92/Tide',
        [hashtable] $Web,
        [string] $WorkDir = [IO.Path]::GetTempPath()
    )
    $Keys = @($Keys)
    $notes = New-Object System.Collections.Generic.List[string]
    $warnings = New-Object System.Collections.Generic.List[string]
    if (-not (Test-Path -LiteralPath $ZipPath -PathType Leaf)) { throw "Bundle not found: $ZipPath" }
    $zipFull = (Resolve-Path -LiteralPath $ZipPath).Path
    $name = Split-Path -Leaf $zipFull
    if ($ExpectedSha256) {
        $ExpectedSha256 = $ExpectedSha256.Trim()
        if ($ExpectedSha256 -notmatch '^[0-9a-fA-F]{64}$') {
            throw "-ExpectedSha256 must be the 64 hex characters of a SHA-256, not '$ExpectedSha256'. Nothing was changed."
        }
    }
    if (-not $Tag) {
        try { $Tag = Get-TideBundleTag $zipFull }
        catch { $warnings.Add("Couldn't read VERSION.txt from the bundle ($($_.Exception.Message)).") }
    }
    $hashBytes = Get-TideFileSha256Bytes $zipFull
    $actual = [BitConverter]::ToString($hashBytes).Replace('-', '')

    # 1. The signature.
    $keyId = $null
    $sigPath = "$zipFull.sig"
    if (Test-Path -LiteralPath $sigPath -PathType Leaf) {
        if ($Keys.Count -eq 0) {
            $notes.Add("$name.sig is beside it, but this script pins no release key to check it with, so it's checked by its SHA-256.")
        } else {
            $keyId = Test-TideReleaseSignature $hashBytes ([IO.File]::ReadAllBytes($sigPath)) $Keys
            if (-not $keyId) {
                $ids = @($Keys | ForEach-Object { $_.Id }) -join ', '
                throw ("$name.sig isn't a valid signature of this bundle by TIDE's release key ($ids): the bundle was changed " +
                       "after it was signed, or isn't TIDE's. Nothing was changed. Download the bundle and its .sig from the release again.")
            }
            $notes.Add("Signed by TIDE's release key $keyId.")
        }
    } elseif ($Keys.Count -gt 0) {
        if (-not $AllowUnsigned) {
            throw ("The bundle isn't signed: there's no $name.sig beside it. Copy the .sig from the release next to the zip. " +
                   "A release from before signing (v1.8.0 and earlier) has none; pass -AllowUnsigned to check it by its " +
                   "SHA-256 instead. Nothing was changed.")
        }
        $warnings.Add("The bundle isn't signed (no $name.sig). -AllowUnsigned: it's checked by its SHA-256 only.")
    }

    # 2. The hash.
    $hashSource = $null; $expected = $null; $from = $null
    if ($ExpectedSha256) {
        $expected = $ExpectedSha256.ToUpperInvariant(); $hashSource = 'expected'; $from = 'the -ExpectedSha256 given'
    } elseif (Test-Path -LiteralPath "$zipFull.sha256" -PathType Leaf) {
        $expected = ConvertFrom-TideSha256File (Get-Content -LiteralPath "$zipFull.sha256" -Raw)
        if (-not $expected) { throw "$name.sha256 beside the bundle holds no SHA-256. Nothing was changed." }
        $hashSource = 'local'; $from = "$name.sha256 beside it"
    } elseif (-not $keyId) {
        if (-not $Tag) {
            $warnings.Add("The bundle doesn't name a release version, so its SHA-256 can't be checked against a release.")
        } elseif (-not $Web) {
            $warnings.Add("Not checked against a SHA-256. To check it, put tide-$Tag.zip.sha256 from the release beside the zip, or pass -ExpectedSha256.")
        } else {
            $shaFile = Join-Path $WorkDir ("tide-$Tag.zip.sha256." + [guid]::NewGuid().ToString('N'))
            $shaWeb = $Web.Clone(); $shaWeb.TimeoutSec = 30   # a host without github.com shouldn't wait long to find out
            $why = $null
            try {
                Invoke-WebRequest @shaWeb -Uri "https://github.com/$Repository/releases/download/$Tag/tide-$Tag.zip.sha256" -OutFile $shaFile
                $expected = ConvertFrom-TideSha256File (Get-Content -LiteralPath $shaFile -Raw)
                if (-not $expected) { $why = "the answer wasn't a .sha256" }
            } catch {
                $status = 0
                try { $status = [int]$_.Exception.Response.StatusCode } catch { $status = 0 }
                $why = if ($status -eq 404) { "github.com/$Repository has no $Tag release with a .sha256" } else { "couldn't fetch it: $($_.Exception.Message)" }
            } finally { Remove-Item -LiteralPath $shaFile -ErrorAction SilentlyContinue }
            if ($expected) { $hashSource = 'release'; $from = "the $Tag release's on github.com/$Repository" }
            else {
                $warnings.Add("Not checked against the release ($why). Carrying on. To check it, put tide-$Tag.zip.sha256 " +
                              "from the release beside the zip, or pass -ExpectedSha256.")
            }
        }
    }
    if ($expected) {
        if ($actual -ine $expected) {
            throw "The bundle's SHA-256 ($actual) doesn't match $from ($expected). Nothing was changed. Download the bundle from the release again."
        }
        $notes.Add("SHA-256 matches $($from): $actual")
    }
    return [pscustomobject]@{ Tag = $Tag; KeyId = $keyId; HashSource = $hashSource; Sha256 = $actual; Notes = @($notes); Warnings = @($warnings) }
}

# --- Rolling back: may the previous build start against the database as it is now? --------------------------------

# The SQL Server migration ids a build knows: the string of each [Migration("<id>")] attribute in the build's
# Tide.Migrations.SqlServer.dll. The assembly is read as bytes, not loaded: Windows PowerShell 5.1 runs on .NET Framework,
# which can't load a .NET 10 assembly. An attribute keeps its string in the assembly as UTF-8 after a byte giving its
# length, so an id counts only where that byte is its length. Throws when the folder has no such assembly, or it names
# no migration.
function Get-TideBuildMigrations([string] $Folder) {
    $dll = Join-Path $Folder 'Tide.Migrations.SqlServer.dll'
    if (-not (Test-Path -LiteralPath $dll -PathType Leaf)) { throw "$dll isn't there." }
    # Latin-1 maps each byte to the character of the same code, so a match's index is its offset in the file.
    $text = [Text.Encoding]::GetEncoding(28591).GetString([IO.File]::ReadAllBytes($dll))
    $ids = @(foreach ($m in [regex]::Matches($text, '\d{14}_[A-Za-z0-9_]+')) {
        if ($m.Index -gt 0 -and [int]$text[$m.Index - 1] -eq $m.Length) { $m.Value }
    })
    if ($ids.Count -eq 0) { throw "$dll names no migration." }
    return @($ids | Sort-Object -Unique)
}

# Whether the previous build, its files put back after a failed upgrade, may be started against the database: when the
# database records no migration that build doesn't know, or only ones it already ran on before this upgrade (a DbaApplies
# database the DBA brought to the new release first). Started on a schema it doesn't know, it may fail, or write data the
# schema no longer means; not knowing either side is no answer, so the pool stays stopped then too, unless the new build
# never ran, when the database is as it was before the upgrade.
#   -Applied       : the database's migration ids now.
#   -Known         : the ids the previous build knows (Get-TideBuildMigrations on its rollback snapshot).
#   -AppliedBefore : the ids the database had before this run deployed anything.
#   -HistoryError, -KnownError, -BeforeError : why the history now, the previous build's ids or the history before this
#                    run couldn't be read, when they couldn't.
#   -DbaApplies    : the app doesn't migrate (Database:ApplyMigrations false): the DBA applies the schema ahead of the upgrade.
#   -NewBuildRan   : the new build may have started (false when the copy, the stale-file removal or the pool's settings
#                    failed, before the pool was started).
#   -PoolWasRunning: the app pool was started when this run began, so the previous build was running on the database.
# Returns Safe, Verdict ('ok', 'unchanged', 'unchanged-unproven', 'dba-ahead', 'history-unreadable', 'build-unreadable', 'before-unreadable',
# 'unknown'), Unknown (the ids the previous build doesn't know), EarlierRun (every one of them was there before this run:
# an earlier upgrade that failed applied them), Message (when it isn't safe: what's wrong and what to do, to follow
# "Upgrade failed (...).") and Note (when it is safe but something should be said first).
function Get-TideRollbackDecision {
    param(
        [string[]] $Applied = @(),
        [string[]] $Known = @(),
        [string[]] $AppliedBefore = @(),
        [string] $HistoryError,
        [string] $KnownError,
        [string] $BeforeError,
        [bool] $DbaApplies,
        [bool] $NewBuildRan = $true,
        [bool] $PoolWasRunning
    )
    $app = @($Applied | ForEach-Object { "$_".Trim() } | Where-Object { $_ })
    $knows = @($Known | ForEach-Object { "$_".Trim() } | Where-Object { $_ })
    $before = @($AppliedBefore | ForEach-Object { "$_".Trim() } | Where-Object { $_ })
    $result = { param($v, $safe, $unknown, $earlier, $msg, $note)
        [pscustomobject]@{ Safe = $safe; Verdict = $v; Unknown = @($unknown); EarlierRun = $earlier; Message = $msg; Note = $note } }
    $stopped = "The previous build's files are back, but the app pool is left STOPPED"
    $restore = "restore the pre-upgrade database backup (in SQL Server's default backup folder), then start the app pool"
    $list = { param($ids) "$($ids.Count) migration(s) the previous build doesn't know ($([string]::Join(', ', @($ids | Select-Object -First 3))))" }

    if (-not $NewBuildRan) {
        # Nothing of this run touched the database: what it holds now is what it held before, and the previous build
        # was running on it then, unless an earlier, failed upgrade had left it stopped.
        $state = if (-not $HistoryError) { $app } elseif (-not $BeforeError) { $before } else { $null }
        if ($null -eq $state -or $KnownError) {
            $why = (@($HistoryError, $KnownError, $BeforeError) | Where-Object { $_ } | Select-Object -Unique) -join '; '
            # Unchanged by this run proves the previous build fits only if it was running on this database when the run
            # began. A pool already stopped may be an earlier upgrade's that migrated and failed (AppMigrates), whose
            # migrations this run's before-deploy history includes too. On DbaApplies the database only ever has what the
            # DBA applied, which the previous build ran on.
            if ($PoolWasRunning -or $DbaApplies) {
                return & $result 'unchanged' $true @() $false $null ("The new build never ran, so this run didn't change " +
                    "the database; the previous build is started again without comparing its migrations ($why).")
            }
            $hint = if ($state -and $state.Count) { " The database's newest migration is $($state | Sort-Object | Select-Object -Last 1)." } else { '' }
            return & $result 'unchanged-unproven' $false @() $false ("$stopped. The new build never ran, so this run didn't " +
                "change the database, but the app pool was already stopped when the upgrade began (an earlier upgrade that " +
                "failed may have migrated it) and the upgrade can't compare the database's migrations with the previous " +
                "build's ($why).$hint If an earlier upgrade applied migrations the previous release doesn't have, restore " +
                "the pre-upgrade backup that earlier upgrade took (<database>-preupgrade-<time>.bak, in SQL Server's default " +
                "backup folder), then start the app pool; if not, start the app pool.") $null
        }
        $unknown = @($state | Where-Object { $knows -notcontains $_ })
        if ($unknown.Count -eq 0) { return & $result 'ok' $true @() $false $null $null }
    } else {
        if ($HistoryError) {
            $hint = if ($knows.Count) {
                " The previous build's newest migration is $($knows | Sort-Object | Select-Object -Last 1): if the history has a " +
                "later one, $restore; if not, start the app pool."
            } else { " If it has migrations the previous release doesn't, $restore; if not, start the app pool." }
            return & $result 'history-unreadable' $false @() $false ("$stopped. The database's migration history couldn't be read " +
                "($HistoryError), so the upgrade can't tell whether the previous build can run against it. Once SQL Server " +
                "answers, look at dbo.__EFMigrationsHistory in the database.$hint") $null
        }
        if ($KnownError) {
            $hint = if ($app.Count) {
                " The database's newest migration is $($app | Sort-Object | Select-Object -Last 1): if the previous release " +
                "doesn't have it, $restore; if it does, start the app pool."
            } else { ' The database records no migration, so start the app pool.' }
            return & $result 'build-unreadable' $false @() $false ("$stopped. The migrations the previous build knows couldn't be " +
                "listed ($KnownError), so the upgrade can't tell whether it can run against the database.$hint") $null
        }
        $unknown = @($app | Where-Object { $knows -notcontains $_ })
        if ($unknown.Count -eq 0) { return & $result 'ok' $true @() $false $null $null }
        if ($BeforeError) {
            return & $result 'before-unreadable' $false $unknown $false ("$stopped`: the database has $(& $list $unknown), and " +
                "the history from before this run couldn't be read ($BeforeError), so the upgrade can't tell who applied them. " +
                "If they were there before the upgrade (a DBA applied them), start the app pool; if the new build applied " +
                "them, $restore.") $null
        }
        $new = @($unknown | Where-Object { $before -notcontains $_ })
        if ($new.Count -gt 0) {
            return & $result 'unknown' $false $new $false "$stopped`: the database has $(& $list $new). The new build applied them: $restore." $null
        }
    }
    # Every one was there before this run started.
    if ($DbaApplies) {
        # The DBA applied the new release's schema before the upgrade, as a DbaApplies upgrade requires, and the previous
        # build was running on it until the pool stopped. Restoring the backup would undo the DBA's work.
        return & $result 'dba-ahead' $true $unknown $true $null ("The database has $(& $list $unknown), applied before this " +
            "upgrade (by the DBA, for the new release). The previous build was running on them before the upgrade, so it's " +
            "started again; keep the database as it is for the next attempt.")
    }
    return & $result 'unknown' $false $unknown $true ("$stopped`: the database has $(& $list $unknown). An earlier upgrade " +
        "that failed applied them, so this run's pre-upgrade backup has them too: restore the pre-upgrade backup that earlier " +
        "upgrade took (<database>-preupgrade-<time>.bak, in SQL Server's default backup folder), then start the app pool.") $null
}

# What a failed RESTORE VERIFYONLY of the pre-upgrade backup means: 'permission' when the account may back up but not
# read a backup back (it needs CREATE DATABASE, error 262), so the backup was written WITH CHECKSUM but not checked;
# 'failed' for anything else (3013, 3189, 3241, a media or checksum error), when the backup can't be trusted. Takes the
# exception, and looks through its inner exceptions for SQL Server's error number.
function Get-TideVerifyFailureKind($Exception) {
    $permissionNumbers = 262, 229, 230, 297, 300
    for ($e = $Exception; $e; $e = $e.InnerException) {
        $numbers = @()
        if ($e.PSObject.Properties['Errors'] -and $e.Errors) { $numbers += @($e.Errors | ForEach-Object { $_.Number }) }
        if ($e.PSObject.Properties['Number']) { $numbers += $e.Number }
        if (@($numbers | Where-Object { $permissionNumbers -contains [int]$_ }).Count) { return 'permission' }
        if ("$($e.Message)" -match 'permission denied|CREATE DATABASE permission|does not have permission') { return 'permission' }
        if (-not $e.PSObject.Properties['InnerException']) { break }
    }
    return 'failed'
}

# --- Pruning old upgrade backups ------------------------------------------------------------------------------------
# Every upgrade leaves a <database>-preupgrade-<stamp>.bak in SQL Server's backup folder and a
# tide-upgrade-backups\<stamp> folder beside the site. After a successful upgrade the script keeps the newest few of
# each. The patterns are exactly the names the upgrade writes, so nothing else in those folders is ever picked.
# ConvertFrom-TideStamp is lib\TideStamp.ps1's.

# The name of a pre-upgrade backup of $DbName, its stamp in the group 'stamp'. Matched whole, so the pattern for 'Tide'
# never matches another database's 'Tide-test-preupgrade-<stamp>.bak'.
function Get-TidePreUpgradeBakPattern([string] $DbName) {
    return '^' + [regex]::Escape($DbName) + '-preupgrade-(?<stamp>\d{8}-\d{6})\.bak$'
}

# The name of an upgrade's folder under tide-upgrade-backups.
function Get-TideUpgradeFolderPattern { return '^(?<stamp>\d{8}-\d{6})$' }

# Which of $Items to delete. An item is anything with a Name (a name alone counts as a failed run's), and Succeeded:
# $true when that upgrade succeeded; false or missing when it failed or isn't known. It counts only when its whole name
# matches $Pattern, case included (on a case-sensitive server 'Tide' and 'TIDE' are two databases), and the pattern's
# 'stamp' group is a real Get-TideStamp time, which orders it. A name in $Exclude (this run's, which succeeded) is never
# returned.
#   Successful runs: the newest $Keep stay, this run's among them; older ones go.
#   Failed runs don't use up $Keep: one newer than the last success before this run stays (after failed retries, the
#   first one's backup may be the only one from before its migrations); older ones go. With no earlier success, all stay.
# $Keep 0 or less prunes nothing. Returns the items themselves, newest first.
function Get-TidePrunable {
    param(
        $Items = @(),
        [int] $Keep = 3,
        [string[]] $Exclude = @(),
        [Parameter(Mandatory = $true)] [string] $Pattern
    )
    if ($Keep -lt 1) { return @() }
    $excluded = @($Exclude | Where-Object { $_ })
    $matched = @(foreach ($item in @($Items)) {
        if ($null -eq $item) { continue }
        $name = if ($item -is [string]) { $item } else { [string]$item.Name }
        $m = [regex]::Match($name, $Pattern)
        if (-not $m.Success -or -not $m.Groups['stamp'].Success) { continue }
        $when = ConvertFrom-TideStamp $m.Groups['stamp'].Value
        if ($null -eq $when) { continue }
        # The boolean true only: -eq $true would take 1 or "true" (a record edited by hand) as a success.
        $ok = ($item -isnot [string]) -and $item.PSObject.Properties['Succeeded'] -and ($item.Succeeded -is [bool]) -and $item.Succeeded
        [pscustomobject]@{ Item = $item; Name = $name; When = $when; Succeeded = [bool]$ok; Excluded = ($excluded -ccontains $name) }
    })
    # Newest first; two of one stamp (a copy made by hand) order by name, so the choice is the same every run.
    $ordered = @($matched | Sort-Object -Property @{ Expression = 'When'; Descending = $true }, @{ Expression = 'Name'; Descending = $true })
    $out = New-Object System.Collections.Generic.List[object]
    $successes = 0; $lastSuccess = $null
    foreach ($o in $ordered) {
        if (-not ($o.Succeeded -or $o.Excluded)) { continue }
        $successes++
        if (-not $o.Excluded -and $null -eq $lastSuccess) { $lastSuccess = $o.When }
        if ($successes -gt $Keep -and -not $o.Excluded) { $out.Add($o) }
    }
    if ($null -ne $lastSuccess) {
        foreach ($o in $ordered) {
            if (-not $o.Succeeded -and -not $o.Excluded -and $o.When -lt $lastSuccess) { $out.Add($o) }
        }
    }
    return @($out | Sort-Object -Property @{ Expression = 'When'; Descending = $true }, @{ Expression = 'Name'; Descending = $true } |
        ForEach-Object { $_.Item })
}

# The record an upgrade writes into its backup folder (snapshot.json): whose snapshot it is (this server, the site
# folder), its database backup, and, once the new build answered, that it succeeded. Pruning goes by it: a folder
# without one, or another site's, is never pruned, since sibling sites share the parent's tide-upgrade-backups.
function New-TideUpgradeRecord([string] $Server, [string] $SitePath, [string] $IisPath, [string] $DbName, [string] $BakName, [string] $Started) {
    return [ordered]@{ format = 1; server = $Server; site = $SitePath; iisPath = $IisPath; database = $DbName; bak = $BakName
                       started = $Started; succeeded = $false; completed = $null }
}

# Marks the record in $Folder succeeded. Throws when there's none.
function Set-TideUpgradeSucceeded([string] $Folder, [string] $Completed) {
    $file = Join-Path $Folder 'snapshot.json'
    $r = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json
    $r | Add-Member -NotePropertyName succeeded -NotePropertyValue $true -Force
    $r | Add-Member -NotePropertyName completed -NotePropertyValue $Completed -Force
    ConvertTo-Json -InputObject $r | Set-Content -LiteralPath $file -Encoding UTF8
}

# This site's upgrade folders under $Root: each with a snapshot.json naming this server and this site folder. Another
# site's, one with no record or one that won't read, and a link (junction or symbolic link), are left out. Each is
# Name, FullName, Succeeded and BakName (the pre-upgrade backup that run took, or '').
function Get-TideUpgradeSnapshots([string] $Root, [string] $SitePath, [string] $Server) {
    if (-not $Root -or -not (Test-Path -LiteralPath $Root -PathType Container)) { return @() }
    $site = "$SitePath".TrimEnd('\', '/')
    return @(foreach ($d in @(Get-ChildItem -LiteralPath $Root -Directory -Force)) {
        if ($d.Attributes -band [IO.FileAttributes]::ReparsePoint) { continue }
        $file = Join-Path $d.FullName 'snapshot.json'
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { continue }
        try { $r = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json } catch { continue }
        if (-not $r -or "$($r.site)".TrimEnd('\', '/') -ine $site -or "$($r.server)" -ine $Server) { continue }
        # JSON's true only (ConvertFrom-Json gives a [bool]); "true" or 1, edited by hand, isn't a success.
        $ok = ($r.succeeded -is [bool]) -and $r.succeeded
        [pscustomobject]@{ Name = $d.Name; FullName = $d.FullName; Succeeded = $ok; BakName = [string]$r.bak }
    })
}

# The names of the pre-upgrade backups of $DbName in $BakDir to delete. Only a file this SQL Server's backup history
# ($History, msdb's physical_device_name for this database) records in $BakDir is ours: another server writing to a
# shared folder, or a database whose name differs only in case, is never picked. -Files: the names in the folder, when
# this host reaches it (a file the history names that's gone isn't returned); $null to go by the history alone.
# $Snapshots (Get-TideUpgradeSnapshots, this site's records) say which runs took which backup and which succeeded. A
# backup no record names (one from before the records, or a -BackupRoot run's, whose record is elsewhere) is never
# pruned and doesn't count, as a folder without a record isn't.
function Get-TidePrunableBaks {
    param(
        [string[]] $History = @(),
        [string] $BakDir,
        [Parameter(Mandatory = $true)] [string] $DbName,
        $Files = $null,
        $Snapshots = @(),
        [int] $Keep = 3,
        [string[]] $Exclude = @()
    )
    $dir = "$BakDir".TrimEnd('\', '/')
    if (-not $dir) { return @() }
    $ours = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
    foreach ($h in @($History)) {
        if ("$h" -match '^(?<d>.*)[\\/](?<n>[^\\/]+)$' -and $Matches['d'].TrimEnd('\', '/') -ieq $dir) { [void]$ours.Add($Matches['n']) }
    }
    $names = if ($null -ne $Files) { @(@($Files) | ForEach-Object { "$_" } | Where-Object { $ours.Contains($_) }) } else { @($ours) }
    $succeeded = New-Object 'System.Collections.Generic.Dictionary[string,bool]' ([StringComparer]::Ordinal)
    foreach ($s in @($Snapshots)) {
        if ($s -and $s.BakName) { $succeeded[[string]$s.BakName] = (($s.Succeeded -is [bool]) -and $s.Succeeded) }
    }
    # This run's own backup counts toward $Keep (and is never returned) even if its record couldn't be written.
    $items = @($names | Where-Object { $succeeded.ContainsKey($_) -or $Exclude -ccontains $_ } |
        ForEach-Object { [pscustomobject]@{ Name = $_; Succeeded = ($succeeded.ContainsKey($_) -and $succeeded[$_]) } })
    return @(Get-TidePrunable -Items $items -Keep $Keep -Exclude $Exclude -Pattern (Get-TidePreUpgradeBakPattern $DbName) |
        ForEach-Object { $_.Name })
}

# What a finished upgrade does with its backups: whether it records itself as a success (MarkSucceeded), prunes the
# older .bak files (PruneBaks) and backup folders (PruneFolders), and a Note to show when it doesn't.
#   -StartProven      : TIDE itself answered the warm-up. A start taken on trust (Windows authentication answered) may yet
#                       need a rollback by hand, so it neither counts as a success nor deletes a backup.
#   -CustomBackupRoot : -BackupRoot was given. Neither is pruned: its records are there, not with the others, so its
#                       runs' backups couldn't be told apart.
#   -Keep             : -KeepUpgradeBackups; 0 prunes nothing.
function Get-TidePrunePlan([bool] $StartProven, [bool] $CustomBackupRoot, [int] $Keep) {
    $plan = { param($mark, $baks, $folders, $note) [pscustomobject]@{ MarkSucceeded = $mark; PruneBaks = $baks; PruneFolders = $folders; Note = $note } }
    if (-not $StartProven) {
        return & $plan $false $false $false ("The new build's start wasn't proven, so this upgrade isn't recorded as a " +
            "success and no older backup was deleted.")
    }
    if ($Keep -lt 1) { return & $plan $true $false $false $null }
    if ($CustomBackupRoot) {
        return & $plan $true $false $false ("With -BackupRoot, older upgrade backups aren't pruned (neither the " +
            "pre-upgrade .bak files nor the backup folders): clear them by hand.")
    }
    return & $plan $true $true $true $null
}

# Deletes a folder and what it holds without following a link inside it: Windows PowerShell 5.1's Remove-Item -Recurse
# goes through a junction and deletes its target's files. A link is removed as a link. Refuses a folder that is a link.
function Remove-TideTree([string] $Path) {
    $top = Get-Item -LiteralPath $Path -Force
    if ($top.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "$Path is a link, not a folder: left as it is." }
    foreach ($c in @(Get-ChildItem -LiteralPath $Path -Force)) {
        if ($c.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            if ($c.PSIsContainer) { [IO.Directory]::Delete($c.FullName, $false) } else { [IO.File]::Delete($c.FullName) }
        } elseif ($c.PSIsContainer) {
            Remove-TideTree $c.FullName
        } else {
            $c.Attributes = [IO.FileAttributes]::Normal
            [IO.File]::Delete($c.FullName)
        }
    }
    [IO.Directory]::Delete($Path, $false)
}
