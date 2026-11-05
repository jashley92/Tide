# Releases and upgrading TIDE

How TIDE versions ship and how to move an install to a newer one. For a first install see **[INSTALL.md](INSTALL.md)**.

> Development needs none of this: `git pull` and `dotnet run` against SQLite ([development.md](development.md)).

---

## How releases ship

Each version tag builds a **release bundle** attached to the matching
[GitHub Release](https://github.com/jashley92/Tide/releases): `tide-<version>.zip` and a `.sha256` beside it. The
bundle holds the published app (`app/`), the deploy scripts (`deploy/`), the SQL Server migration ids it carries
(`migrations-sqlserver.txt`) and a `VERSION.txt`. Deploying from it means **the server needs no .NET SDK and no NuGet
access**. The version is stamped into the assemblies.

The release workflow ([release.yml](../.github/workflows/release.yml)) refuses a tag unless:

- it's a version: `vX.Y.Z`, or `vX.Y.Z-alpha.N` (`-beta.N`, `-rc.N`) for a pre-release;
- its commit is on `main`;
- every CI gate passes on that commit (build, tests, SQL Server migrations, migrations in sync with the model, the
  vulnerable-package scan) and SonarQube Cloud's quality gate passed for it on `main`;
- no migration that an earlier release shipped (alphas included) was edited or removed;
- a release (not a pre-release) has its notes: a `### vX.Y.Z` section under [Notes](#notes) below.

### Versions

TIDE ships **releases from `main`**: `v1.0.0`, `v1.1.0` and so on, with `vX.Y.Z-rc.N` (or `-alpha.N`, `-beta.N`)
for a pre-release. Pre-releases never show as the latest release. A release's description is its section under
[Notes](#notes); a pre-release without one gets GitHub's list of the pull requests merged since the previous tag.

Once a release ships (a pre-release too), its migrations are fixed: databases installed from it have to keep upgrading.
Change the schema by adding a migration ([development.md](development.md#changing-the-schema)).

### Cutting a release

1. Merge what's going in to `main` and wait for CI on `main` to pass (SonarQube Cloud included).
2. For a release, add its `### vX.Y.Z` section under [Notes](#notes) and merge that. For a pre-release it's optional.
3. Tag the commit on `main` and push the tag:

   ```bash
   git tag v1.0.0 origin/main
   git push origin v1.0.0
   ```

The Release workflow reruns the gates on that commit and publishes the release about 15 minutes later. If it fails,
fix the cause on `main`, delete the tag (`git push origin :refs/tags/v1.0.0`, then `git tag -d`) and tag again.

---

## Installing or upgrading from a bundle

A **first install** runs `Install-Tide.ps1` from the bundle ([below](#a-first-install-from-a-bundle)). Every
**upgrade** after that runs `Upgrade-Tide.ps1`, which backs up, swaps the app's files, and rolls back if the new build
doesn't start.

### Upgrading with Upgrade-Tide.ps1

In an elevated PowerShell on the web server. Once an install or upgrade has kept the deploy scripts beside the site
(`<site folder>-deploy`, such as `D:\inetpub\tide-deploy`), an upgrade is one line, which **Administration › System ›
Updates** shows with the newest version filled in:

```powershell
& 'D:\inetpub\tide-deploy\Upgrade-Tide.ps1' -Version v0.2.0
```

It downloads `tide-v0.2.0.zip` and its `.sha256` from this repository's releases (through `-Proxy` if the server needs
one), refuses a bundle whose hash doesn't match, and hands over to the bundle's own copy of the script, so a fix to the
upgrade itself takes effect in the upgrade that ships it. `-Version latest` takes the newest release (or, before 1.0,
the newest pre-release). A server without outbound HTTPS: download the bundle elsewhere, copy it over, and pass it
instead:

```powershell
Unblock-File .\tide-v0.2.0.zip
Expand-Archive .\tide-v0.2.0.zip -DestinationPath .
cd .\tide-v0.2.0\deploy
.\Upgrade-Tide.ps1 -BundleZip ..\..\tide-v0.2.0.zip
```

Unblock the one file you copied: a downloaded zip is marked as coming from the internet, Windows carries the mark onto
every file unzipped from it, and the scripts then won't run under the usual `RemoteSigned` policy. Don't loosen the
machine's policy. With `-BundleZip`, the script checks the zip against the `.sha256` of the release its `VERSION.txt`
names when the server can reach GitHub, and warns that it couldn't when it can't. The hash shows the bundle arrived
whole; it doesn't show who published it, so take bundles only from this repository's
[Releases](https://github.com/jashley92/Tide/releases) page.

Installed under another site (`Install-Tide.ps1 -ParentSite Intranet -AppPath tide`)? Name that site and the path:
`-SiteName Intranet -AppPath tide`. The kept copy remembers both, and the Updates page's command includes them.

What it does, in order:

1. **Resolves the install** from IIS and the site's `appsettings.Production.json`: the site folder, the app pool, the
   SQL Server instance and database, the schema mode (`Database:ApplyMigrations`), and the address it probes
   (`<site>/health`, from the site's binding; `-HealthUrl` overrides it).
2. **Preflight**, which changes nothing:
   - the ASP.NET Core Module and the .NET 10 runtime are installed, and SQL Server answers;
   - the database's `__EFMigrationsHistory` against the release's `migrations-sqlserver.txt`. It stops if the database
     has a migration the release doesn't (a newer or different lineage), or TIDE's tables but no history, the state
     that makes the app fail at startup with "There is already an object named ...";
   - **DbaApplies:** it stops unless the database already has every migration the release lists, and names the ones
     missing. The upgrade never migrates a DbaApplies database: have the DBA apply the release's schema script from
     the bundle's `deploy\sql` folder first, then run the upgrade again;
   - settings a newer release added that the file lacks: a data folder (added under the data root; pass `-DataRoot`
     when it can't be worked out), or a server setting (added at the value the app already uses without it, so nothing
     changes until you edit it). A setting already there is never changed, and secrets (`Email__SmtpPassword`,
     `Cve__NvdApiKey`) stay in environment variables. Comments in the file are read past, as the app reads them, but
     adding a setting writes the file again without them (it says so first; the copy in step 3 keeps them);
   - the anonymous `/health` and `/api/v1` carve-outs, and that the server can reach the site at the probe address.

   **`-WhatIf` stops here**, having reported all of it.
3. **Backs up**: a `COPY_ONLY` full backup of the database (to SQL Server's default backup folder; `-SkipDbBackup` when
   your backup product's is current), `appsettings.Production.json`, and a snapshot of the site folder, under
   `<site folder's parent>\tide-upgrade-backups\<time>` (or `-BackupRoot`).
4. **Deploys**: adds the missing settings and the carve-outs, stops the pool (waiting until its worker has let go of the
   files), copies the release's `app` folder over the site, and removes what the earlier release shipped under
   `wwwroot`, and the assemblies at the top of the folder, that this one doesn't. `appsettings.Production.json` (and
   its `.new`), `logs` and `App_Data` stay, and the data root is outside the site.
5. **Starts and warms up**: on AppMigrates the first start applies the new migrations; it polls `/health` for up to 11
   minutes (past the 10-minute `startupTimeLimit`). If TIDE doesn't answer `Healthy`, it puts the previous files back
   and starts the pool again; if the new build had already applied migrations, it leaves the pool **stopped** and says
   so: restore the pre-upgrade database backup, then start the pool. It writes `VERSION.txt` into the site (the
   version, the one before, when and who), and refreshes the kept deploy scripts.

Then run `Verify-Install.ps1` ([INSTALL.md](INSTALL.md#5-check-it)); the script prints the command.

Upgrading from the repository instead: `-Build` publishes the app (it needs the .NET 10 SDK on the server) and reads the
migration list from the source; `-AppSource <folder>` takes an app already published. Both run the same checks.

**Rolling back by hand**, if the automatic rollback couldn't: stop the pool, copy `site-rollback` from the backup folder
over the site folder (leave `appsettings.Production.json`, `logs` and `App_Data`), restore the pre-upgrade database
backup if the new build migrated, and start the pool.

### A first install from a bundle

1. Download `tide-<version>.zip` and its `.sha256` from [Releases](https://github.com/jashley92/Tide/releases), check
   it (`(Get-FileHash .\tide-<version>.zip).Hash` matches the hash in the `.sha256`), then unblock and unzip it as
   above.
2. From the bundle's `deploy` folder, with your `tide.config.psd1`, run `Install-Database.ps1`, then the installer
   ([INSTALL.md](INSTALL.md)):

   ```powershell
   .\Install-Tide.ps1 -ConfigFile .\tide.config.psd1
   ```

   It sees it's in a bundle and copies the bundle's `app` folder into the site folder (`SitePath`); no SDK is needed. It
   keeps the deploy scripts beside the site, so the next upgrade is `Upgrade-Tide.ps1 -Version`.
3. Run `Verify-Install.ps1` ([INSTALL.md](INSTALL.md#5-check-it)).

Re-running `Install-Tide.ps1` over an install still works as a repair: it stops the pool, copies the bundle's app over
the site, removes what the release no longer ships, and keeps `appsettings.Production.json`. It takes no backup and
doesn't roll back, so upgrade with `Upgrade-Tide.ps1`.

Lost the server's `tide.config.psd1`? **Administration › System › Configuration bundle** writes one from what the
running server knows (its SQL Server instance and database, app pool, folders, AD groups and organization; never a
credential), with anything it can't know left blank and marked.

### Knowing when there's a new release

**Administration › System › Updates** shows the running version and, once an administrator turns the check on there
(or sets `Updates:CheckEnabled` on the server), asks GitHub's public releases list for newer ones about once a day
(the interval is set on the same page; `Updates:CheckIntervalHours` is the server's default), through `Intake:Proxy` when one is set. A newer release is marked on the
Administration tabs, with its notes and the upgrade command above. The check is one anonymous request with a 10-second limit:
it's off by default, and a server without internet access just shows that it couldn't check. A server on a
pre-release follows pre-releases; one on a release doesn't. While the repository is private, GitHub answers an
anonymous request with 404, and the page says so.

---

## Notes

Each release's notes, newest first, under a `### vX.Y.Z` heading (a pre-release's under its own tag, when it has
any). What changed for administrators: new settings, steps the upgrade needs, anything that behaves differently.

### v1.3.0

Upgrade as usual (`Upgrade-Tide.ps1 -Version latest`). It adds three migrations (`ActionFiles`, `CommentFileRemoval`,
`UserPreferences`); none of the earlier ones change.

**For administrators**
- **Two new roles: Viewer and Visitor.** Map an AD group to each on Role mappings if you want them. A **Viewer** reads
  everything (actions included) and can join a live meeting, raise a hand and add meeting notes, but changes nothing. A
  **Visitor** sees the company-wide archive, and inside a live meeting sees what the room sees, for as long as they're
  seated; they can raise a hand. Role mappings now shows **What each role can do**, the same table as in INSTALL.md.
- **Commenting on actions** now needs to be able to work actions or write articles; reading the team edition alone no
  longer allows it. No existing role loses anything.
- **New settings:** `Files:ZipDownloads` (Teams & SLA, "Action files": which files download in a zip with the password
  `infected`; default the ones that can run) and `Notifications:PersonalDigest:Enabled` (Teams & SLA; offer the
  personal digest, default on).
- **Downloads** of action and comment files are sent with a stricter header (`sandbox; default-src 'none'`), so nothing
  in a file can run as a page in TIDE.
- **People choose their own notifications** under My notifications in the account menu. Teams & SLA shows how many have
  turned each email off, and who.
- **Clock boxes** (reminder, digest and meeting times, the Send dialog's "held at") can be typed into again.

**For everyone**
- **Files on actions:** a Files section in the side column lists an action's files and its comments' files, with size,
  SHA-256 and a check of each file's real type. Files are removed with a reason and kept; an administrator can restore
  them. Lists show a paperclip count.
- **Present mode:** article slides show ATT&CK by tactic and the scorecard tags, which analysts can edit from the slide;
  "Following" is a small pill in the header instead of a bar.
- **Actions** remembers List or Board for each person (Board by default).
- **Adversaries** on an article are picked from a search by name, alias or MITRE id, with "Add" for a new one.
- **The article's draft pane** folds away once every suggestion is decided.
- **My notifications:** turn off reminders, digests and rotation emails you don't want, or take a personal digest.

### v1.2.0

Upgrade as usual (`Upgrade-Tide.ps1 -Version latest`). It adds one migration (`ActionCommentEditVersion`); none of the
earlier ones change. No settings are new or renamed.

- **Upgrade script fix.** `Upgrade-Tide.ps1` no longer stops with `DriveNotFoundException` right after the database
  backup when the SQL Server's backup drive doesn't exist on the web server. If you hit that upgrading to v1.1.0, the
  backup was taken and the site was unchanged.
- **"Title slide line".** The brief's older one-line, TLP-marked note on the title slide is now called the title slide
  line, so Today has one "Opening note" (the team's). Nothing about either changes.
- **API:** a brief's response has a new `finishedAs` field (`emailed`, `distributed` or `notSent`; null until it's
  finished). Its `status` is still `Sent` for all three.
- **AD:** names and email addresses are looked up again every 24 hours, so a change in AD shows within a day instead of
  at the next app-pool recycle. If AD can't be reached, a test copy goes to the address saved from AD on an earlier visit.
- **Briefs:** Finish as Not sent waits while an edition is still being sent; Back to ready is refused while the brief's
  meeting is running in present mode; a rota change no longer moves the presenter of a meeting that was held. History
  no longer shows user-id rows.
- **Our stack:** a product's page names only articles at or below the team edition's ceiling and counts the rest, and it
  updates live.
- **Comments on actions:** a save that would overwrite someone else's change to the same comment is refused, with their
  text shown; editing a second comment asks before discarding unsaved changes to the first.
- **Opening metrics:** trend charts include the brief's own day and any weekday holiday with values; the Metrics history
  filter hides days with nothing in the groups shown.
- **Formatting toolbar:** Enter on an empty sub-bullet moves it out a level; a new list after a paragraph gets a blank
  line first.
- About 40 fixes from four bug-fix rounds over the v1.1.0 features: present mode's live meetings (hand-off, reloads,
  edition switches, followers), the numbers form under slow saves, the toolbar, the opening note, Back to ready and Not
  sent, Pull now, and running on SQL Server (lists now sort the same, ignoring case, on both databases).

### v1.1.0

Upgrade as usual (`Upgrade-Tide.ps1 -Version latest`). The upgrade adds new migrations; none of the earlier ones change.

- **Email addresses come from AD.** With Windows authentication, TIDE now reads each user's `mail` attribute along with
  their display name, as the app-pool account. Test copies, reminders, digests and rotation emails reach people who had
  no address before. Each user's address is filled in on their next visit. A UPN is never used in place of `mail`.
- **New setting: Analyst pull-now window** (`Intake:PullNowMinutes`, default 15, 1 to 1440), on the Sources admin
  page's Polling card. Analysts can pull a polled source from Triage's new Sources tab at most once per window per
  source. Admin Poll now on the Sources page isn't limited.
- **Opening metrics set-up** has a chart per group for present mode: Trend (the default for existing groups), One trend
  per metric, Share of the whole, Mix over time, Side by side, Against threshold, or None. Present mode now shows one
  slide per group, and a group whose values are all 0 is listed on the first metrics slide instead of getting its own.
- **Team opening note** on each brief: team edition only (brief page, its own slide, the team email), carried forward
  from the previous brief. It never appears in the company-wide edition, archive, SharePoint copy or API.
- **Back to ready** reopens a presented brief's meeting, bringing back articles carried to the next brief. Briefs
  presented before this upgrade can't tell carried articles from hand-added ones, so both stay on the next brief.
- **Finish as Not sent**: with email on, a brief can be finished without sending, with a required reason shown in the
  archive. It locks the day's numbers as a send does and can still be emailed later.
- **Present mode** shows "We run" product chips on team-edition article slides, with a card the presenter can open for
  everyone, and each metric's note on its tile.
- **A formatting toolbar** (bullets, numbering, indent, bold, italic, link) on the Markdown text boxes.
- **Today's numbers and Metrics history** handle many groups: a progress bar with a chip per group, finished groups
  folded, a grouped history table with a fixed header and Day column, and a group filter.

### v1.0.0

The first public release. A server on any `v0.1.0-alpha` release upgrades to it as usual (`Upgrade-Tide.ps1 -Version
latest`): every migration the alphas shipped is unchanged, and new ones are added on top.

- **Webhooks** post an event once it's 30 seconds old, so a save still being written is never skipped.
- **Reviewers** can be any user who can write articles, Admins included.
- **New-TideConfig.ps1** suggests `Intranet` as the parent IIS site when installing under one; type your own site's name.
