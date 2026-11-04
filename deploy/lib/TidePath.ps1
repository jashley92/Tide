# Folder paths for native commands (robocopy) and scheduled-task command lines. Dot-source this file.
#
# Windows PowerShell 5.1 passes an argument that has a space as "...", and a trailing backslash then escapes the closing
# quote: robocopy reads "D:\TIDE drop\app\" as D:\TIDE drop\app" plus the arguments after it, and fails (exit 16). A
# scheduled task's command line is read by the same rule, so -Destination "\\backup01\tide share\" there took the rest
# of the line with it. The scripts take their path parameters without the trailing backslash, and quote a value into a
# command line with any trailing backslash doubled.
# ASCII only: Windows PowerShell 5.1 misreads non-ASCII in BOM-less files.

# The path without trailing separators, except a drive's root, which stays C:\ (C: alone is that drive's current folder).
function Get-TideCleanPath([string] $Path) {
    if (-not $Path) { return $Path }
    $p = $Path.TrimEnd('\', '/')
    if (-not $p) { return $Path.Substring(0, 1) }
    if ($p -match '^[A-Za-z]:$') { return "$p\" }
    return $p
}

# "<value>" for a command line, with trailing backslashes doubled so the closing quote stays a quote ("E:\\" is E:\).
function ConvertTo-TideQuotedArg([string] $Value) {
    return '"' + ($Value -replace '(\\+)$', '$1$1') + '"'
}
