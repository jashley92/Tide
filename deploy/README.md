# Deploy scripts

Windows PowerShell 5.1, ASCII only. See [docs/INSTALL.md](../docs/INSTALL.md) for the walkthrough.

| Script | Does |
|---|---|
| `New-TideConfig.ps1` | Asks for each install value, with defaults and checks, and writes `tide.config.psd1`. Runs anywhere; never asks for a password. |
| `tide.config.template.psd1` | The answers file both installers read. Generate it with `New-TideConfig.ps1`, or copy this to `tide.config.psd1` (git-ignored) and fill it in. |
| `Install-Database.ps1` + `sql/01-Create-Database.sql` | Creates the database and the app-pool account's Windows login. The two modes are AppMigrates (`db_owner`, the app applies its schema) and DbaApplies (read/write/EXECUTE; a DBA applies the schema). |
| `Install-Tide.ps1` | Publishes the app (or, from a release bundle, copies its app in) and creates the data root, `appsettings.Production.json`, the app pool and the site: its own host name, an SNI binding, Windows auth, anonymous `/health`, and anonymous `/api/v1` with Windows auth left on (for API tokens). Optionally (`ParentSite` and `AppPath`) it creates an application under an existing site instead, such as `/tide`, leaving that site's bindings and authentication alone. Re-runnable to repair an install. |
| `Verify-Install.ps1` | Checks the pool and site are running, the app and database answer, and the site asks for a Windows sign-in. Under another site, pass the URL with its path and the parent's site name. |
| `Upgrade-Tide.ps1` | The in-place upgrade ([docs/UPGRADE.md](../docs/UPGRADE.md#upgrading-with-upgrade-tideps1)), from `-Version` (downloaded and checked against the release's `.sha256`), `-BundleZip`, `-AppSource` or `-Build`. Reads the install from IIS and `appsettings.Production.json`; checks the runtime, SQL Server and the database's migrations against the release's `migrations-sqlserver.txt` (on a DbaApplies install it stops unless the DBA has applied them all: it never migrates); backs up the database, the settings and the site; adds settings a newer release introduced without changing any; swaps the files, keeping `appsettings.Production.json`, `logs` and `App_Data`; warms up on `/health` and rolls back if the new build doesn't start. `-WhatIf` runs the checks only. |
| `Backup-Tide.ps1` | For a server without a backup product: the database, the screenshots, the key ring and the site's settings as one verified set; `-Log` for log backups; `-Register` schedules both ([docs/INSTALL.md](../docs/INSTALL.md#6-back-up)). |
| `Test-TideRestore.ps1` | The restore drill: restores a set into a scratch database, runs `DBCC CHECKDB`, and checks every screenshot the database lists is in the set. `-Register` schedules it. |
| `appsettings.Production.template.json` | What `Install-Tide.ps1` fills in, and where `Upgrade-Tide.ps1` finds the data-folder settings a newer release added. |
| `lib/TideIis.ps1`, `lib/TideAdGroups.ps1` | Shared IIS helpers (stopping the pool, the anonymous carve-outs, removing files a release no longer ships, keeping the deploy scripts beside the site) and writing group names as `DOMAIN\Group`. |
| `lib/TideSql.ps1`, `lib/TideSettings.ps1`, `lib/TideUpgrade.ps1`, `lib/TideBackup.ps1`, `lib/TideStamp.ps1`, `lib/TideJson.ps1`, `lib/TidePath.ps1` | The upgrade's and the backups' helpers: SQL over Windows authentication and the migration comparison; finding and adding missing settings; release tags, hashes, binding addresses and probe answers; a backup set's folders; dates in the invariant culture; settings files read as the app reads them (comments and trailing commas allowed, which Windows PowerShell 5.1 otherwise refuses); folder paths for robocopy and scheduled-task command lines (no trailing backslash to escape a closing quote). `tests/Tide.UnitTests/DeployScriptTests.cs` runs the pure ones in PowerShell. |

Install and upgrade both keep a copy of this folder beside the site (`<site folder>-deploy`, readable only by
Administrators, SYSTEM and the app pool), so the next upgrade is `& '<site folder>-deploy\Upgrade-Tide.ps1' -Version vX.Y.Z`,
which **Administration > System > Updates** shows. Scheduled backup tasks run that copy.

**Not yet here:** a DBA schema script for DbaApplies installs (`sql/tide-schema-sqlserver.sql`), planned before the
first release (1.0). `Upgrade-Tide.ps1` doesn't apply one: on a DbaApplies install it checks the DBA has, and stops
until they have.
