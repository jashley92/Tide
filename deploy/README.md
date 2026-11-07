# Deploy scripts

Windows PowerShell 5.1, ASCII only. See [docs/INSTALL.md](../docs/INSTALL.md) for the walkthrough.

| Script | Does |
|---|---|
| `New-TideConfig.ps1` | Asks for each install value, with defaults and checks, and writes `tide.config.psd1`. Runs anywhere; never asks for a password. |
| `tide.config.template.psd1` | The answers file both installers read. Generate it with `New-TideConfig.ps1`, or copy this to `tide.config.psd1` (git-ignored) and fill it in. |
| `Install-Database.ps1` + `sql/01-Create-Database.sql` | Creates the database and the app-pool account's Windows login. The two modes are AppMigrates (`db_owner`, the app applies its schema) and DbaApplies (read/write/EXECUTE; a DBA applies the schema). |
| `Install-Tide.ps1` | Publishes the app (or, from a release bundle, copies its app in) and creates the data root, `appsettings.Production.json`, the app pool and the site: its own host name, an SNI binding, Windows auth, anonymous `/health`, and anonymous `/api/v1` with Windows auth left on (for API tokens). Optionally (`ParentSite` and `AppPath`) it creates an application under an existing site instead, such as `/tide`, leaving that site's bindings and authentication alone. Re-runnable to repair an install. |
| `Verify-Install.ps1` | Checks the pool and site are running, the app and database answer, and the site asks for a Windows sign-in. Under another site, pass the URL with its path and the parent's site name. |
| `Upgrade-Tide.ps1` | The in-place upgrade ([docs/UPGRADE.md](../docs/UPGRADE.md#upgrading-with-upgrade-tideps1)), from `-Version` (downloaded with its `.sha256` and `.sig`), `-BundleZip`, `-AppSource` or `-Build`. A bundle is checked before it's expanded: its signature against the pinned release keys, and its SHA-256 against `-ExpectedSha256` or the `.sha256` beside it ([below](#checking-a-bundle)). Reads the install from IIS and `appsettings.Production.json`; checks the runtime, SQL Server and the database's migrations against the release's `migrations-sqlserver.txt` (on a DbaApplies install it stops unless the DBA has applied them all: it never migrates); backs up the database, the settings and the site; adds settings a newer release introduced without changing any; swaps the files, keeping `appsettings.Production.json`, `logs` and `App_Data`; warms up on `/health` and rolls back if the new build doesn't start. `-WhatIf` runs the checks only. |
| `Test-TideBundle.ps1` | Runs the upgrade's checks on a bundle, signature and SHA-256, and nothing else: no IIS, SQL Server or administrator rights. For a workstation before the zip goes to the server, and for a first install ([below](#checking-a-bundle)). |
| `Backup-Tide.ps1` | For a server without a backup product: the database, the screenshots, the key ring and the site's settings as one verified set; `-Log` for log backups; `-Register` schedules both ([docs/INSTALL.md](../docs/INSTALL.md#6-back-up)). |
| `Test-TideRestore.ps1` | The restore drill: restores a set into a scratch database, runs `DBCC CHECKDB`, and checks every screenshot the database lists is in the set. `-Register` schedules it. |
| `lib/TideReleaseKeys.ps1` | The public keys release bundles are signed with, which the upgrade and `Test-TideBundle.ps1` trust. Written by `tools/release/New-ReleaseSigningKey.ps1`, never by hand ([docs/development/releasing.md](../docs/development/releasing.md)). |
| `appsettings.Production.template.json` | What `Install-Tide.ps1` fills in, and where `Upgrade-Tide.ps1` finds the data-folder settings a newer release added. |
| `lib/TideIis.ps1`, `lib/TideAdGroups.ps1` | Shared IIS helpers (stopping the pool, the anonymous carve-outs, removing files a release no longer ships, keeping the deploy scripts beside the site) and writing group names as `DOMAIN\Group`. |
| `lib/TideSql.ps1`, `lib/TideSettings.ps1`, `lib/TideUpgrade.ps1`, `lib/TideBackup.ps1`, `lib/TideStamp.ps1`, `lib/TideJson.ps1`, `lib/TidePath.ps1` | The upgrade's and the backups' helpers: SQL over Windows authentication and the migration comparison; finding and adding missing settings; release tags, hashes and signatures (`Invoke-TideBundleCheck`), binding addresses and probe answers; a backup set's folders; dates in the invariant culture; settings files read as the app reads them (comments and trailing commas allowed, which Windows PowerShell 5.1 otherwise refuses); folder paths for robocopy and scheduled-task command lines (no trailing backslash to escape a closing quote). `tests/Tide.UnitTests/DeployScriptTests.cs` runs the pure ones in PowerShell. |

Install and upgrade both keep a copy of this folder beside the site (`<site folder>-deploy`, readable only by
Administrators, SYSTEM and the app pool), so the next upgrade is `& '<site folder>-deploy\Upgrade-Tide.ps1' -Version vX.Y.Z`,
which **Administration > System > Updates** shows. Scheduled backup tasks run that copy.

## Checking a bundle

Each release publishes three files: `tide-<v>.zip`, `tide-<v>.zip.sha256` and, from the first signed release (v1.9.0),
`tide-<v>.zip.sig`, a signature by TIDE's release key. `Upgrade-Tide.ps1`, the copy already installed beside the
site, checks a new bundle before it expands it or runs anything from it:

1. **The signature.** `<zip>.sig` beside the zip must verify against a key its `lib/TideReleaseKeys.ps1` pins; one
   that doesn't is refused. While a key is pinned, a bundle with no `.sig` is refused too, unless you pass
   `-AllowUnsigned` for a release from before signing (v1.8.0 or earlier). It's then checked by its SHA-256, with a
   warning.
2. **The SHA-256.** `-ExpectedSha256 <hex>` if you give it, else `<zip>.sha256` beside the zip (as published, in
   `sha256sum`'s format), must match; a mismatch is refused. With neither, a bundle that isn't signed is checked
   against its release's `.sha256` on github.com, and when the host can't reach github.com it warns and carries on.

Nothing is changed when a check fails.

**A server that can't reach github.com.** Download all three files from the release page on a machine that can, and
copy them into one folder on the server:

```powershell
& 'D:\inetpub\tide-deploy\Upgrade-Tide.ps1' -BundleZip D:\drop\tide-v1.9.0.zip
# With only the hash from the release page, not the .sha256 file:
& 'D:\inetpub\tide-deploy\Upgrade-Tide.ps1' -BundleZip D:\drop\tide-v1.9.0.zip -ExpectedSha256 <hash>
```

**Before copying it to the server.** `Test-TideBundle.ps1` runs the same checks and nothing else, on any machine with
Windows PowerShell 5.1 or PowerShell 7. Run it from a deploy folder you already trust (an earlier release's, or the
server's `<site folder>-deploy`), never from the bundle you're checking, whose own copy could have been changed with
it:

```powershell
.\Test-TideBundle.ps1 D:\drop\tide-v1.9.0.zip     # -Offline: never ask github.com
```

It exits 0 when the bundle is verified by its signature or its hash, and fails otherwise.

**A first install.** `Install-Tide.ps1` comes inside the bundle, so it can't vouch for the bundle. Before installing,
either run `Test-TideBundle.ps1` on it from the deploy folder of an earlier TIDE release, v1.9.0 or later (on a
workstation that has one, say), or compare `Get-FileHash tide-<v>.zip -Algorithm SHA256` with the hash on the release
page. The hash proves the zip is the one the page lists; only the signature proves TIDE's key made it.

**The first signed release.** v1.8.0's scripts pin no key, so v1.9.0, the first signed release, can be checked only
by its hash: v1.8.0 has no `Test-TideBundle.ps1`, and its upgrade checks the SHA-256, as before. Signatures are
checked from then on: by v1.9.0's installed `Upgrade-Tide.ps1` on the upgrades after it, and by v1.9.0's
`Test-TideBundle.ps1` (once v1.9.0 is a deploy folder you trust) on later bundles.

**Not yet here:** a DBA schema script for DbaApplies installs (`sql/tide-schema-sqlserver.sql`), planned before the
first release (1.0). `Upgrade-Tide.ps1` doesn't apply one: on a DbaApplies install it checks the DBA has, and stops
until they have.
