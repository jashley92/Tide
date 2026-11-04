# Shared SQL helpers for Upgrade-Tide.ps1, Backup-Tide.ps1 and Test-TideRestore.ps1. Dot-source this file.
#
# Uses System.Data.SqlClient, which ships with Windows PowerShell 5.1 / .NET Framework, so the scripts need neither the
# SqlServer module nor sqlcmd.exe. Windows integrated authentication by default; pass a PSCredential (Get-Credential)
# only where integrated auth isn't possible. Nothing is stored.
# ASCII only: Windows PowerShell 5.1 misreads non-ASCII in BOM-less files.

function New-TideSqlConnection {
    # An open connection. The connection is always encrypted; -ValidateServerCertificate also checks the instance's
    # certificate, as the site's connection string does when it says TrustServerCertificate=False.
    param(
        [Parameter(Mandatory)] [string] $SqlInstance,
        [Parameter(Mandatory)] [string] $DbName,
        [System.Management.Automation.PSCredential] $SqlCredential,
        [switch] $ValidateServerCertificate,
        [string] $ApplicationName = 'TIDE deploy'
    )
    $b = New-Object System.Data.SqlClient.SqlConnectionStringBuilder
    $b['Data Source'] = $SqlInstance
    $b['Initial Catalog'] = $DbName
    $b['Encrypt'] = $true
    $b['TrustServerCertificate'] = -not $ValidateServerCertificate
    $b['Application Name'] = $ApplicationName
    if ($SqlCredential) {
        $b['User ID'] = $SqlCredential.UserName
        $b['Password'] = $SqlCredential.GetNetworkCredential().Password
    } else {
        $b['Integrated Security'] = $true
    }
    $conn = New-Object System.Data.SqlClient.SqlConnection $b.ConnectionString
    $conn.Open()
    return $conn
}

function Invoke-TideSqlScalar {
    param(
        [Parameter(Mandatory)] [System.Data.SqlClient.SqlConnection] $Connection,
        [Parameter(Mandatory)] [string] $Sql,
        [hashtable] $Parameters = @{},
        [int] $TimeoutSeconds = 0
    )
    $cmd = $Connection.CreateCommand()
    $cmd.CommandText = $Sql
    $cmd.CommandTimeout = $TimeoutSeconds
    foreach ($k in $Parameters.Keys) { [void]$cmd.Parameters.AddWithValue($k, $Parameters[$k]) }
    $v = $cmd.ExecuteScalar()
    if ($v -is [System.DBNull]) { return $null }
    return $v
}

# A query's rows, with parameters, no time limit by default (backups and restores take as long as they take).
function Invoke-TideSqlQuery {
    param(
        [Parameter(Mandatory)] [System.Data.SqlClient.SqlConnection] $Connection,
        [Parameter(Mandatory)] [string] $Sql,
        [hashtable] $Parameters = @{},
        [int] $TimeoutSeconds = 0
    )
    $cmd = $Connection.CreateCommand()
    $cmd.CommandText = $Sql
    $cmd.CommandTimeout = $TimeoutSeconds
    foreach ($k in $Parameters.Keys) { [void]$cmd.Parameters.AddWithValue($k, $Parameters[$k]) }
    $reader = $cmd.ExecuteReader()
    $rows = @()
    try {
        do {
            while ($reader.Read()) {
                $row = [ordered]@{}
                for ($i = 0; $i -lt $reader.FieldCount; $i++) {
                    $v = $reader.GetValue($i)
                    $row[$reader.GetName($i)] = if ($v -is [System.DBNull]) { $null } else { $v }
                }
                $rows += [pscustomobject]$row
            }
        } while ($reader.NextResult())
    } finally { $reader.Close() }
    return ,$rows   # one array, even of one row or none: callers don't wrap it in @() again
}

# The first column of each row, as strings.
function Invoke-TideSqlColumn {
    param(
        [Parameter(Mandatory)] [System.Data.SqlClient.SqlConnection] $Connection,
        [Parameter(Mandatory)] [string] $Sql
    )
    $cmd = $Connection.CreateCommand()
    $cmd.CommandText = $Sql
    $reader = $cmd.ExecuteReader()
    $out = New-Object System.Collections.Generic.List[string]
    try { while ($reader.Read()) { $out.Add([string]$reader.GetValue(0)) } }
    finally { $reader.Close() }
    # The items themselves (no leading comma): the caller collects them with @(). A ',' here would wrap the list in a
    # second array, so every applied migration would read as unknown.
    return $out.ToArray()
}

# The SQL Server instance and database a connection string names, and whether it checks the instance's certificate.
function ConvertFrom-TideConnectionString([string] $ConnectionString) {
    $b = New-Object System.Data.SqlClient.SqlConnectionStringBuilder $ConnectionString
    return [pscustomobject]@{
        Instance = $b.DataSource
        Name = $b.InitialCatalog
        ValidateServerCertificate = (-not $b.TrustServerCertificate)
    }
}

# A SQL Server identifier, bracketed and with any closing bracket doubled.
function Get-SqlName([string] $Name) { return '[' + $Name.Replace(']', ']]') + ']' }

# The table every TIDE database has from its first migration on: its presence with no migration history means a schema
# the manifest can't account for.
function Get-TideSchemaMarkerTable { return 'dbo.Articles' }

function Compare-TideMigrations {
    # Compares the migrations a database has applied (its __EFMigrationsHistory) with a release's manifest
    # (migrations-sqlserver.txt), and says whether the release may be deployed over it. Pure: no SQL here.
    #   -Expected     : the manifest's migration ids.
    #   -Applied      : the database's applied ids.
    #   -HasAppSchema : whether TIDE's tables exist (Get-TideSchemaMarkerTable).
    #   -DbaApplies   : the app doesn't migrate (Database:ApplyMigrations false), so nothing may be pending.
    # Returns Verdict ('ok', 'pending', 'dba-pending', 'no-history', 'unknown', 'baseline'), Ok (the release may be
    # deployed), Pending and Unknown (ids), and Message (a sentence for the operator).
    param(
        [string[]] $Expected = @(),
        [string[]] $Applied = @(),
        [bool] $HasAppSchema,
        [bool] $DbaApplies
    )
    $exp = @($Expected | ForEach-Object { "$_".Trim() } | Where-Object { $_ })
    $app = @($Applied | ForEach-Object { "$_".Trim() } | Where-Object { $_ })
    $unknown = @($app | Where-Object { $exp -notcontains $_ })
    $pending = @($exp | Where-Object { $app -notcontains $_ })
    $baseline = $exp | Sort-Object | Select-Object -First 1   # the Initial this release expects

    $result = { param($v, $ok, $msg) [pscustomobject]@{ Verdict = $v; Ok = $ok; Pending = $pending; Unknown = $unknown; Message = $msg } }

    if ($app.Count -eq 0 -and $HasAppSchema) {
        return & $result 'no-history' $false ("MIGRATION MISMATCH: the database has TIDE's tables but no migration history. " +
            "Applying this release would try to create tables that exist and the app would fail to start ('There is already " +
            "an object named ...'). Reconcile the migration history first (docs/UPGRADE.md).")
    }
    if ($unknown.Count -gt 0) {
        return & $result 'unknown' $false ("MIGRATION MISMATCH: the database records migration(s) this release doesn't have " +
            "($([string]::Join(', ', @($unknown | Select-Object -First 5)))). It's on a newer or different lineage than this " +
            "build: don't deploy it over this database (docs/UPGRADE.md).")
    }
    if ($HasAppSchema -and $baseline -and ($app -notcontains $baseline)) {
        return & $result 'baseline' $false ("MIGRATION MISMATCH: this release's first migration ('$baseline') isn't in the " +
            "database's history, but TIDE's tables exist: the lineage diverged. Deploying would fail at startup (docs/UPGRADE.md).")
    }
    if ($pending.Count -eq 0) {
        return & $result 'ok' $true 'The database is already at this release''s schema (no pending migrations).'
    }
    if ($DbaApplies) {
        return & $result 'dba-pending' $false ("This install's schema is applied by a DBA (Database:ApplyMigrations is false), " +
            "and the database lacks $($pending.Count) of this release's migration(s): $([string]::Join(', ', $pending)). " +
            "Nothing has been changed. Have the DBA apply this release's schema script from its deploy\sql folder to the " +
            "database first (docs/UPGRADE.md, DbaApplies), then run the upgrade again. The upgrade never migrates a " +
            "DbaApplies database itself.")
    }
    return & $result 'pending' $true ("$($pending.Count) migration(s) will be applied when the new build starts: " +
        [string]::Join(', ', $pending))
}
