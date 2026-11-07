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
