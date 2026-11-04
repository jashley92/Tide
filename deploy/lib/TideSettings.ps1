# Settings the upgrade adds to an older appsettings.Production.json when they're missing. Dot-source this file.
#
# Two kinds, and a setting already present is never changed:
#   - Data folders: every setting the template sets under the data root (__DATA_ROOT__\...). One a later release added is
#     missing from an older install's file, and its store would then fall back to App_Data in the read-only web root.
#     It's added under the install's data root.
#   - Server settings: a section a later release added (a new transport, a new scan) isn't in an older install's file,
#     so an administrator can't see that it exists. Each missing one is added at the release's own appsettings.json
#     value, which the app already uses while the setting is absent, so adding it changes nothing until someone edits it.
# ASCII only: Windows PowerShell 5.1 misreads non-ASCII in BOM-less files.

# Sections (or single settings) to fill in. Each must exist in src\Tide.Web\appsettings.json and be server configuration,
# never one an administrator changes in the app (SettingsCatalog.InApp): a test checks both.
function Get-TideServerSettingPaths {
    return @(
        'Email:SmtpUsername', 'Email:SmtpUseDefaultCredentials', 'Email:SmtpTimeoutSeconds',
        'Intake:Proxy', 'Intake:AllowPrivateNetworks', 'Intake:TimeoutSeconds', 'Intake:MaxResponseKB',
        'Cve:NvdUrl', 'Cve:EpssUrl', 'Cve:KevUrl', 'Cve:MaxNvdPerRun',
        'Updates:Repository')
}

# Left out: secrets (set as environment variables on the app pool, never in the file) and development-only settings.
function Get-TideServerSettingExclusions { return @('Cve:NvdApiKey', 'Email:SmtpPassword', 'Email:PickupDirectory') }

function Test-TideJsonPath($Node, [string] $Path) {
    foreach ($part in $Path.Split(':')) {
        if ($null -eq $Node -or $Node -isnot [System.Management.Automation.PSCustomObject] -or -not $Node.PSObject.Properties[$part]) { return $false }
        $Node = $Node.$part
    }
    return $true
}

function Get-TideJsonNode($Node, [string] $Path) {
    foreach ($part in $Path.Split(':')) {
        if ($null -eq $Node -or $Node -isnot [System.Management.Automation.PSCustomObject] -or -not $Node.PSObject.Properties[$part]) { return $null }
        $Node = $Node.$part
    }
    return ,$Node   # the comma keeps a list a list (a returned array would be unrolled)
}

# Sets a value at a 'Section:Key' path, creating the sections on the way. Throws when a value stands where a section
# belongs, so a file that can't take the setting stops the upgrade before anything is written.
function Set-TideJsonValue($Node, [string] $Path, $Value) {
    $parts = $Path.Split(':')
    for ($i = 0; $i -lt $parts.Length - 1; $i++) {
        if (-not $Node.PSObject.Properties[$parts[$i]]) { $Node | Add-Member -NotePropertyName $parts[$i] -NotePropertyValue ([pscustomobject]@{}) }
        $Node = $Node.($parts[$i])
        if ($Node -isnot [System.Management.Automation.PSCustomObject]) { throw "appsettings.Production.json: $(($parts[0..$i]) -join ':') isn't a section, so $Path can't be set under it." }
    }
    $Node | Add-Member -NotePropertyName $parts[-1] -NotePropertyValue $Value -Force
}

# Every setting (a leaf: a value or a list) under the listed paths in $Defaults that $Live lacks.
function Find-TideMissingServerSettings($Live, $Defaults) {
    $excluded = Get-TideServerSettingExclusions
    $leaves = New-Object System.Collections.Generic.List[string]
    function Add-Leaves($node, [string] $path) {
        if ($node -is [System.Management.Automation.PSCustomObject]) {
            foreach ($p in $node.PSObject.Properties) {
                if ($p.Name.StartsWith('//')) { continue }
                Add-Leaves $p.Value "${path}:$($p.Name)"
            }
        } else { $leaves.Add($path) }
    }
    foreach ($path in Get-TideServerSettingPaths) {
        if (-not (Test-TideJsonPath $Defaults $path)) { continue }
        Add-Leaves (Get-TideJsonNode $Defaults $path) $path
    }
    return @($leaves | Where-Object { $excluded -notcontains $_ -and -not (Test-TideJsonPath $Live $_) })
}

# Adds each of $Paths (from Find-TideMissingServerSettings) to $Live with its $Defaults value. A section created on the
# way gets its explanation ('//'): the template's, else the release appsettings.json's.
function Add-TideServerSettings($Live, $Defaults, $Template, [string[]] $Paths) {
    foreach ($path in $Paths) {
        $parts = $path.Split(':')
        $node = $Live
        for ($i = 0; $i -lt $parts.Length - 1; $i++) {
            if (-not $node.PSObject.Properties[$parts[$i]]) {
                $section = [pscustomobject]@{}
                $at = ($parts[0..$i]) -join ':'
                $note = Get-TideJsonNode $Template "${at}://"
                if ($null -eq $note) { $note = Get-TideJsonNode $Defaults "${at}://" }
                if ($note -is [string]) { $section | Add-Member -NotePropertyName '//' -NotePropertyValue $note }
                $node | Add-Member -NotePropertyName $parts[$i] -NotePropertyValue $section
            }
            $node = $node.($parts[$i])
            if ($node -isnot [System.Management.Automation.PSCustomObject]) { throw "appsettings.Production.json: $(($parts[0..$i]) -join ':') isn't a section, so $path can't be added under it." }
        }
        if ($node.PSObject.Properties[$parts[-1]]) { continue }   # never change a setting that's there
        $value = Get-TideJsonNode $Defaults $path
        $node | Add-Member -NotePropertyName $parts[-1] -NotePropertyValue $value
    }
}

# Every string the template sets to "__DATA_ROOT__\..." as (Path in the JSON, Suffix under the data root).
function Get-TideDataRootSettings($Template, [string] $Prefix = '') {
    foreach ($prop in $Template.PSObject.Properties) {
        if ($prop.Name.StartsWith('//')) { continue }
        $path = if ($Prefix) { "${Prefix}:$($prop.Name)" } else { $prop.Name }
        if ($prop.Value -is [string] -and $prop.Value -like '__DATA_ROOT__*') {
            [pscustomobject]@{ Path = $path; Suffix = $prop.Value.Substring('__DATA_ROOT__'.Length).TrimStart('\', '/') }
        } elseif ($prop.Value -is [System.Management.Automation.PSCustomObject]) { Get-TideDataRootSettings $prop.Value $path }
    }
}

# The install's data root, from a data-folder setting it already has (value = <data root>\<suffix>), or $null.
function Resolve-TideDataRoot($Live, $Wanted) {
    foreach ($w in @($Wanted)) {
        $v = Get-TideJsonNode $Live $w.Path
        if ($v -is [string] -and $v.Length -gt $w.Suffix.Length + 1 -and
            $v.TrimEnd('\').EndsWith('\' + $w.Suffix, [StringComparison]::OrdinalIgnoreCase)) {
            $t = $v.TrimEnd('\')
            return $t.Substring(0, $t.Length - $w.Suffix.Length - 1)
        }
    }
    return $null
}

# The data-folder settings ($Wanted, from Get-TideDataRootSettings) that $Live lacks or leaves blank.
function Find-TideMissingDataSettings($Live, $Wanted) {
    return @(@($Wanted) | Where-Object { $_ -and -not "$(Get-TideJsonNode $Live $_.Path)" })
}
