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
