#Requires -Version 5.1

<#
.SYNOPSIS
    Checks a TIDE release bundle's signature and SHA-256, as Upgrade-Tide.ps1 does, without upgrading anything.

.DESCRIPTION
    Runs the checks Upgrade-Tide.ps1 runs on a bundle before it expands it (lib\TideUpgrade.ps1,
    Invoke-TideBundleCheck), and nothing else: no IIS, no SQL Server, no administrator rights. Use it on a workstation
    before copying a bundle to the server, or to check a bundle for a first install, which Install-Tide.ps1 (inside
    the bundle) can't vouch for.

      - The signature: tide-<version>.zip.sig beside the zip must verify against a release key this script's
        lib\TideReleaseKeys.ps1 pins. One that doesn't is refused. While keys are pinned, a bundle with no .sig is
        refused unless -AllowUnsigned.
      - The SHA-256: -ExpectedSha256, else tide-<version>.zip.sha256 beside the zip, must match. With neither, and no
        valid signature, it's checked against the release's .sha256 on github.com (unless -Offline).

    Run it from a deploy folder you already trust (an earlier release's, or the kept copy beside an installed site),
    not from the bundle being checked: a bundle's own copy of this script could have been changed with it. It exits 0
    when the bundle is verified (by its signature or its hash), and fails otherwise.

.PARAMETER BundleZip
    The bundle to check (tide-<version>.zip), with its .sig and .sha256 beside it as the release publishes them.

.PARAMETER ExpectedSha256
    The bundle's SHA-256 from the release page, in place of a .sha256 beside it.

.PARAMETER AllowUnsigned
    Accept a bundle with no .sig (a release from before signing: v1.8.0 and earlier), checked by its SHA-256 only.

.PARAMETER Offline
    Never ask github.com: only the .sig, -ExpectedSha256 and the .sha256 beside the zip count.

.PARAMETER Repository
    The GitHub repository releases come from (default 'jashley92/Tide'), for the github.com hash check.

.PARAMETER Proxy
    An outbound proxy for the github.com hash check, used with your Windows credentials.

.PARAMETER PassThru
    Also return the result (Tag, KeyId, HashSource, Sha256, Notes, Warnings).

.EXAMPLE
    .\Test-TideBundle.ps1 D:\drop\tide-v1.9.0.zip

.EXAMPLE
    .\Test-TideBundle.ps1 .\tide-v1.8.0.zip -AllowUnsigned -ExpectedSha256 <the SHA-256 from the release page>
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)] [string] $BundleZip,
    [string] $ExpectedSha256,
    [switch] $AllowUnsigned,
    [switch] $Offline,
    [string] $Repository = 'jashley92/Tide',
    [string] $Proxy,
    [switch] $PassThru,
    # For tests only: a TideReleaseKeys.ps1 to trust instead of the lib\TideReleaseKeys.ps1 beside this script.
    [Parameter(DontShow)] [string] $TrustedKeysPath
)

$ErrorActionPreference = 'Stop'
$lib = Join-Path $PSScriptRoot 'lib'
. (Join-Path $lib 'TideUpgrade.ps1')
if ([Net.ServicePointManager]::SecurityProtocol -ne [Net.SecurityProtocolType]::SystemDefault) {
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
}
if ($Repository -notmatch '^[A-Za-z0-9-]{1,39}/[A-Za-z0-9._-]{1,100}$') { throw "-Repository must be owner/name, not '$Repository'." }
if ($TrustedKeysPath -and -not (Test-Path -LiteralPath $TrustedKeysPath -PathType Leaf)) { throw "-TrustedKeysPath not found: $TrustedKeysPath" }
$keysFile = if ($TrustedKeysPath) { $TrustedKeysPath } else { Join-Path $lib 'TideReleaseKeys.ps1' }
$keys = @(Get-TideTrustedKeys $keysFile)

$web = $null
if (-not $Offline) {
    $web = @{ UseBasicParsing = $true; TimeoutSec = 30 }
    if ($Proxy) { $web.Proxy = $Proxy; $web.ProxyUseDefaultCredentials = $true }
}
$ProgressPreference = 'SilentlyContinue'

Write-Host "==> Checking $BundleZip" -ForegroundColor Cyan
$pinned = if ($keys.Count) { @($keys | ForEach-Object { $_.Id }) -join ', ' } else { 'none' }
Write-Host "    Release keys pinned here: $pinned" -ForegroundColor Gray
$check = Invoke-TideBundleCheck -ZipPath $BundleZip -Keys $keys -AllowUnsigned:$AllowUnsigned -ExpectedSha256 $ExpectedSha256 `
    -Repository $Repository -Web $web
foreach ($n in $check.Notes) { Write-Host "    $n" -ForegroundColor Gray }
foreach ($w in $check.Warnings) { Write-Host "    $w" -ForegroundColor Yellow }
if (-not $check.KeyId -and -not $check.HashSource) {
    throw "Not verified: the bundle has no signature this script can check and no SHA-256 to compare with. Put the release's .sig or .sha256 beside it, or pass -ExpectedSha256."
}
$release = if ($check.Tag) { "the $($check.Tag) bundle" } else { 'the bundle' }
if ($check.KeyId) { Write-Host "OK: $release is signed by TIDE's release key $($check.KeyId)." -ForegroundColor Green }
else { Write-Host "OK: $release matches its SHA-256, but isn't signature-checked." -ForegroundColor Green }
if ($PassThru) { $check }
