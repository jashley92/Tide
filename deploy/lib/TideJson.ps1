# Reading JSON settings files as the app reads them. Dot-source this file.
#
# .NET's configuration reader skips // and /* */ comments and allows a trailing comma, so an administrator can add a
# note to appsettings.Production.json and the app runs on. Windows PowerShell 5.1's ConvertFrom-Json refuses both, and
# the scheduled backups and restore drills run under it: one comment would fail every run. These remove both, outside
# strings, before parsing.
# ASCII only: Windows PowerShell 5.1 misreads non-ASCII in BOM-less files.

# The text with its comments and trailing commas removed; strings (a URL's '//', a "//" note key) are kept as they are.
function ConvertTo-TideStrictJson([string] $Text) {
    if (-not $Text) { return $Text }
    $n = $Text.Length
    # First the comments. A comment becomes a space, so the tokens either side stay apart.
    $sb = New-Object System.Text.StringBuilder $n
    $i = 0
    while ($i -lt $n) {
        $c = $Text[$i]
        if ($c -eq [char]'"') {
            $j = $i + 1
            while ($j -lt $n -and $Text[$j] -ne [char]'"') { if ($Text[$j] -eq [char]92) { $j++ }; $j++ }
            $end = [Math]::Min($j + 1, $n)
            [void]$sb.Append($Text, $i, $end - $i)
            $i = $end
        } elseif ($c -eq [char]'/' -and $i + 1 -lt $n -and $Text[$i + 1] -eq [char]'/') {
            while ($i -lt $n -and $Text[$i] -ne [char]"`n") { $i++ }
        } elseif ($c -eq [char]'/' -and $i + 1 -lt $n -and $Text[$i + 1] -eq [char]'*') {
            $close = $Text.IndexOf('*/', $i + 2)
            $i = if ($close -lt 0) { $n } else { $close + 2 }
            [void]$sb.Append(' ')
        } else {
            [void]$sb.Append($c)
            $i++
        }
    }
    # Then a comma with only white space before the } or ] that closes its object or array.
    $t = $sb.ToString()
    $n = $t.Length
    $sb = New-Object System.Text.StringBuilder $n
    $i = 0
    while ($i -lt $n) {
        $c = $t[$i]
        if ($c -eq [char]'"') {
            $j = $i + 1
            while ($j -lt $n -and $t[$j] -ne [char]'"') { if ($t[$j] -eq [char]92) { $j++ }; $j++ }
            $end = [Math]::Min($j + 1, $n)
            [void]$sb.Append($t, $i, $end - $i)
            $i = $end
            continue
        }
        if ($c -eq [char]',') {
            $j = $i + 1
            while ($j -lt $n -and [char]::IsWhiteSpace($t[$j])) { $j++ }
            if ($j -lt $n -and ($t[$j] -eq [char]'}' -or $t[$j] -eq [char]']')) { $i++; continue }
        }
        [void]$sb.Append($c)
        $i++
    }
    return $sb.ToString()
}

# JSON text to objects, as ConvertFrom-Json, taking the comments and trailing commas the app takes.
function ConvertFrom-TideJson([string] $Text) {
    return (ConvertTo-TideStrictJson $Text | ConvertFrom-Json)
}

# A JSON file read as the app reads it. A file that still isn't JSON is named in the error.
function Read-TideJsonFile([string] $Path) {
    $text = Get-Content -LiteralPath $Path -Raw -Encoding UTF8 -ErrorAction Stop
    try { return (ConvertFrom-TideJson $text) }
    catch { throw "$Path isn't valid JSON ($($_.Exception.Message))." }
}

# Whether a JSON file has comments or trailing commas, which rewriting it (ConvertTo-Json) would drop.
function Test-TideJsonHasExtras([string] $Text) {
    return [bool]($Text -and (ConvertTo-TideStrictJson $Text) -ne $Text)
}
