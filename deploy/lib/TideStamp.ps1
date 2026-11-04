# Date stamps for file names and records the deploy scripts write. Dot-source this file.
#
# Get-Date's Format parameter and a ToString date pattern use the server's culture, whose calendar can be Thai Buddhist
# (year 2569) or Hijri (1448), so a stamp written on such a server sorts out of order with the rest. Every stamp the
# deploy scripts write goes through here, in the invariant culture.
# ASCII only: Windows PowerShell 5.1 misreads non-ASCII in BOM-less files.

function Get-TideStamp {
    param(
        [string] $Format = 'yyyyMMdd-HHmmss',
        [switch] $Utc,
        [datetime] $When = [datetime]::Now
    )
    $t = if ($Utc) { $When.ToUniversalTime() } else { $When }
    return $t.ToString($Format, [System.Globalization.CultureInfo]::InvariantCulture)
}

function ConvertFrom-TideStamp {
    # The instant a 'yyyyMMdd-HHmmss' stamp names, or $null when $Text holds none (a folder named by hand).
    param([string] $Text, [string] $Format = 'yyyyMMdd-HHmmss')
    $parsed = [datetime]::MinValue
    $ok = [datetime]::TryParseExact($Text, $Format, [System.Globalization.CultureInfo]::InvariantCulture,
        [System.Globalization.DateTimeStyles]::None, [ref] $parsed)
    if ($ok) { return $parsed }
    return $null
}

# An ISO 8601 UTC time for a record (backup-set.json), whatever the server's calendar.
function Get-TideUtcStamp([datetime] $When = [datetime]::Now) { return Get-TideStamp -Format 'yyyy-MM-ddTHH:mm:ssZ' -Utc -When $When }
