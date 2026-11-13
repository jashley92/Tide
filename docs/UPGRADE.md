# Releases and upgrading TIDE

How TIDE versions ship and how to move an install to a newer one. For a first install see **[INSTALL.md](INSTALL.md)**.

> Development needs none of this: `git pull` and `dotnet run` against SQLite ([development/getting-started.md](development/getting-started.md)).

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
Change the schema by adding a migration ([development/recipes.md](development/recipes.md#add-an-entity-or-a-column-and-its-migration)).

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
   its `.new`), `logs` and `App_Data` stay, and the data root is outside the site. While the pool is stopped it sets
   the pool as the installer does: no scheduled recycle, no overlapping recycle, and one worker process
   (`processModel.maxProcesses = 1`, Maximum Worker Processes in IIS Manager). It reads that back; a pool that still
   reads more than 1 (a lock in `applicationHost.config`) is a warning, not a failure, since the new build is already
   in place: set it by hand. Only one process runs the background jobs either way (the job lease), but each worker
   keeps its own sign-ins and live pages.
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

### v1.13.0

Upgrade as usual (`Upgrade-Tide.ps1 -Version latest`). It adds three migrations: `ActionStatusChangedAt` (which fills
when each action entered its status, from its close time, its latest status line or when it was raised),
`WhyItMattersKept` (five nullable columns on `Articles`, filled with nothing) and `ActionRecommendationLine` (which
links each existing action to the recommended line its title matches on its article, once). None of the earlier ones
change. On a DbaApplies install, the DBA applies them first, as for any release that carries one.

Most pages are laid out afresh around one idea: what you select shows what TIDE already knows about it, in a column on
the right, so you leave the page less. Every route, key and name that worked before still works.

**Changes to check before upgrading**
- **A new screenshot starts at TLP:AMBER**, or the article's marking when that's higher, so pasting one never puts it
  into the company-wide edition. Lowering it is a marking change, saved with Save. Screenshots already added keep
  their markings.
- **The article editor saves itself** after a two-second pause in typing. It never saves a change to a TLP marking, or
  anything that would put a part into the company-wide edition (those wait for Save), never saves over a colleague's
  newer version, and never touches an article on a sent brief. The audit log keeps every save, each autosave marked
  *Autosaved*; the article's history shows one editing session as one entry.
- **The meeting's open-actions slide is now *Actions that need review*.** As well as overdue and escalated actions,
  it lists open actions nobody has updated for 2 business days (5 when Blocked). Updates by TIDE itself (the
  escalation ladder's, a skipped reminder's) don't count. The team email's "Still open from earlier briefs" is
  unchanged.
- **Raising and working actions in the meeting is the presenter's, from the presenter's rail.** Someone following,
  an analyst included, raises or works an action on the action pages.
- **STIX exports give indicators, vulnerabilities and attack patterns new ids.** Each id now includes the object's TLP
  marking (a UUIDv5 of `type|value|tlp`), so the same indicator exported at CLEAR and at AMBER is two objects, never
  two conflicting versions of one. A platform that imported earlier exports sees these objects once more under new ids
  on the first import after upgrading; retire the old ones there if it doesn't. From then on, the same object at the
  same TLP keeps its id across exports and re-imports merge as before. Report ids don't change.
- **An action's assignee is a member of its team, in the editor too.** The action editor offers only the team's
  members and the server refuses anyone else, as the meeting already did. An action raised from the meeting goes to
  whoever raises it only when they're on the team; otherwise it's the team's. Actions whose holder isn't on the team
  keep them, and editing such an action without changing the holder still saves. Moving an action to another team
  keeps its holder only if they're on that team, as before.
- **Take it is for a member of the action's team.** It's shown, and allowed, only to the team's members, everywhere
  it appears (the action page, the Actions list and board, a lookup's sweeps, Today, the meeting). An analyst who isn't
  on the team still works the action but no longer takes it on: add them to the team if they should. Actions they
  already hold stay theirs.
- **Today says *1 of 2 sent*** in its header, its step strip and the top bar's brief chip while one of the day's two
  editions has gone and the other hasn't. It says *Sent* once both have (or the one that went is the only one with
  anything in it).

**New settings** (all unchanged until set)
- `Org:ActionBesideSlide`: `false`. *In a meeting, an action opens beside the slide* (Organization settings). Turn it
  on after a rehearsal in the meeting room.
- `Org:StaleOpenDays` (`2`) and `Org:StaleBlockedDays` (`5`): business days, 1 to 30, an open or a blocked action may
  go without an update before *Actions that need review* lists it (Organization settings).
- `Intake:PulledBy`: `08:00`. From then on each working day, while polling is on, Today tells analysts how many feeds
  haven't been pulled since (Sources).
- `Onboarding:Enabled`: `true`. Set to `false`, TIDE no longer shows *What's new* by itself; Help still lists every
  release's notes.

**For everyone**
- **What's new.** The first time each person opens TIDE after this upgrade, a dialog lists what changed for their
  role, once. **See all changes**, and **Help and what's new** in the account menu, open Help with each release's
  highlights for that role (and, for administrators, the full notes). Never in present mode, and never for a Reader or a Visitor.
- **Every page.** A **context rail** on the right of Today, Triage, the article editor, Actions, an action, Search,
  Our stack, the Library and Adversaries shows what TIDE knows about what's selected. It's open beside the page on a
  window 1500px or wider; below that, **Context** in the top bar opens it over the page. `]` shows or hides it. A
  **brief chip** in the top bar ("Brief 10:30 AM · step 3 of 5 · Order") goes back to Today from anywhere. The
  sidebar fits a short laptop screen. Popouts (Triage's Attach to…, the account menu, Export indicators) close on
  Escape, a click outside, or Tab.
- **Today.** The brief takes the page: the step strip, the running order, the opening note (read-only until Edit),
  My actions, Not on a brief yet and today's numbers. The edition check, your teams' queue, Escalated, reviews, the
  meeting and coming up are in the rail. A *Why it matters* warning has **Set to AMBER** and **Keep it** beside it: Keep
  it records that the paragraph is meant to go to everyone as it is, and the warning comes back if the text or marking
  changes. The edition check lists only the exceptions, and the Triage step says how many new items are on KEV and
  how many feeds weren't pulled since the morning, with **Pull all**.
- **Triage.** The decisions sit under the reading pane, with a line saying why the suggested step is suggested. A
  report an article already covers is attached in one click (**Attach to “…”**), and a covered group with **Attach
  all**, every report or none. The rail shows the item's CVE, what already covers it and its open actions, the other
  reports of the same event, the story, and what it names. The reading pane opens by itself on a wide window but
  never fetches an article until you press V or *Read it here*. **Pull all** on the Sources tab pulls every source,
  one at a time.
- **The article editor.** One column, in the order the brief reads, with the reader preview in the rail. *Why it
  matters* has the same Set to AMBER and Keep it as Today. Adding an indicator enables Save again, and bullets grow with
  their text again.
- **Actions.** Each tab shows its count, and **No owner** finds work nobody has. List rows offer **Take it**, a status
  menu and a Progress column; All open and My teams are grouped by due date. The board's four lanes fit the window,
  with Done, Cancelled and Not applicable as chips under them that still take a dropped card and open those actions.
  Select a row or card with a click or `J`/`K`; `T` takes it, `Enter` opens it, and the rail shows its next step,
  hunt query, article and last activity. A closed action's rail on Recently closed has **Reopen**. An external team's
  action that nobody here holds is named by its ticket ("INC-4411"), or "no ticket yet".
- **An action's page.** One column of work; the facts, the source article, the other actions raised from it, an
  external team's contact and Files are in the rail. Drop a file anywhere on the page. On a sweep or hunt the query is
  shown when the page opens and nothing is saved until you Copy or keep it; every built-in template now limits it to
  the last N days. Saved queries and playbooks can't be added to a closed action. A team's recorded reply reads as a
  quote.
- **Search.** A lookup answers in its first line ("On 1 article · briefed Mon Nov 9 · 2 actions open · no hunt result
  yet"), lists each article once, and shows what's *Seen with* it. The hunt query, who is sweeping for it and an
  export of its articles are in the rail.
- **Our stack.** Products that need a look come first, with a *Right now* column (CVEs, KEV, new triage items),
  filters, and old checks flagged. The selected product shows in the rail, with Edit, Start an outreach and Mark
  checked.
- **Library.** Saved queries, Outreach templates, Playbooks and Board rules are one page with four tabs and one search;
  the old addresses still work. The rail tries an entry on one of your open actions before you add it, and lists
  where it's been used.
- **Adversaries.** The list's selected profile, and a profile's facts and buttons, are in the rail.
- **Present mode.** Who is here and whose hand is up are in the top bar, for everyone. The article slide shows its
  indicators, and each recommended line the action it became. The footer names the slides, and the title slide says
  what comes first. The presenter's controls (take, raise, notes, Mark discussed, the opening note, missing numbers,
  hands, ending) are in the presenter's rail, never on a follower's screen. The wrap-up is three lists: not raised,
  who has what, and not discussed.

### v1.12.0

Upgrade as usual (`Upgrade-Tide.ps1 -Version latest`). It adds one migration, `MissedDismissKept`: two nullable columns
on `FeedItems`. None of the earlier ones change. On a DbaApplies install, the DBA applies it first, as for any release
that carries one.

**Changes to check before upgrading**
- **"Missed this week?" on Triage: Right to dismiss replaces Hide until next week.** The notice lists dismissed
  stories that name a KEV-listed CVE or a watched adversary and that no brief covered. Each now has *Right to dismiss*
  beside *Put back in New*, and the notice has *All N were right to dismiss*. One click, no dialog, Undo on the toast.
  It takes the story off the notice for the whole team (open Triage pages update), and the Dismissed tab shows who kept
  it. A story put back and dismissed again is asked about again. *Hide until next week*, which hid the whole notice in
  one browser, is gone.

### v1.11.0

Upgrade as usual (`Upgrade-Tide.ps1 -Version latest`). No migrations.

**New**
- **Chart several metrics at once.** On Metric history, *Chart: …* replaces the single-metric list. It opens every
  metric under its group: tick metrics one by one, or tick a group to add all its current metrics, and find one by name.
  Retired metrics are listed when you ask for them. What's charted is kept in the page's address, so a link shows the
  same chart.
  - **Layouts.** Up to 6 metrics in the same unit share one chart. *A chart each* draws a small chart per metric, each
    on its own scale over the same days. It's used for more than 6 metrics, or for mixed units.
  - **Readings.** Each metric shows its latest change, green when it moved the good way and red when the bad way. A
    chart of one metric shades where its warning and critical thresholds trip.
  - **From the table.** Filtering the table by group offers *Chart the groups shown*.
- **An Insight section in the sidebar** holds Trends, Metrics, Coverage and Reports.

**Changes**
- **Wide tables use the whole window.** Metric history and Trends no longer stop at 1400px, as the actions board
  already didn't, so on a wide screen their tables scroll sideways only when they're wider than the screen. Other pages
  keep the usual width.

### v1.10.1

Upgrade as usual (`Upgrade-Tide.ps1 -Version latest`). No migrations.

**Fixes**
- **The meeting's quick add is one row again.** In v1.10.0 its controls (what needs doing, kind, team, due date, Add)
  stacked one per line, full width, on every article slide: a stylesheet edit had dropped their layout. A test now
  checks each row of controls on a slide keeps its own.

### v1.10.0

Upgrade as usual (`Upgrade-Tide.ps1 -Version latest`). It adds two migrations: `OpeningNoteReviewed` and
`StoredSecrets`. None of the earlier ones change. On a DbaApplies install, the DBA applies them first, as for any
release that carries one.

**Changes to check before upgrading**
- **Secrets can be saved in the app.** The mail relay password, the NVD API key and the Azure OpenAI key can now be
  saved in Administration › System › Secrets, encrypted with the server's Data Protection keys, instead of only as
  environment variables on the app pool (which IIS keeps unencrypted in `applicationHost.config`). An environment
  variable still wins, so nothing changes until you save one and remove the variable. Back up the key ring
  (`DataProtection:KeyPath`) with the database: a database restored without it can't decrypt a saved secret, which
  Secrets then marks *Won't decrypt* until it's saved again.
- **Actions raised in the meeting are assigned to whoever raises them,** and go to internal teams only: the quick add
  and a recommended line's Raise no longer offer an external team. An external team's work is split off an action as
  a part, so it has a parent to follow it up.
- **The opening note is flagged until it's looked at.** A brief that starts with the previous brief's note shows
  analysts *Not updated* on Today until someone saves it or keeps it with *Still current*. A brief not yet presented
  when you upgrade shows the flag too, if it has a note.

**For administrators**
- **Secrets** (Administration › System). Each saved secret is never shown again: the page says whether one is saved,
  when and by whom, and whether the server's configuration sets it (which wins). The audit log records it only as set.
  Server configuration and Setting sources show a secret saved here as *Saved in Administration, encrypted*.

**For everyone**
- **Actions title first.** In lists and on cards an action leads with its title and ends with its number, small and
  muted: the Actions table (no # column; typing `A-1201` in its filter still finds it), the board, Today, search,
  stories, trends, our stack, adversaries and the slides. Where the number is what's quoted (an action's own page,
  "Part of", "Raised as", toasts and emails) it still leads.
- **An article's actions, in reach.** Beside the article editor, Preview, Actions and Details are now tabs, the count
  of its actions on the Actions tab, with how many recommended lines aren't raised yet. `?side=actions` or
  `?side=details` opens a tab. Above the tabs, from any of them: *Ready to brief*, or how many things are missing (in
  this edition and the other), with Show, which goes to the first.
- **The meeting slides.**
  - An article's actions are one line each: the whole title opens the action over the slide (the presenter's for
    everyone following), its number after it, then team and due date, then Take it, the assignee and the date. They
    share their control columns with the recommended lines above, so the boxes and buttons line up down the slide.
  - Tags and ATT&CK read better: tags as chips, a column per tactic with its techniques, and the CVEs summed up ("5
    CVEs, all on KEV") with the most pressing one and "+4 more", which opens them all over the slide.
  - The "won't be emailed" note is gone from the slides, and on actions it no longer appears when external team
    reminders are off for everyone (an administrator's choice); it still says when one team's reminders are off or it
    has no contact email.
- **Today in a narrow window.** The step strip compacts by its own width, so "Now" no longer runs into the next step
  beside an open sidebar; words no longer split mid-word; and no line starts with "·".
- **The opening note's helper text** under its box is gone: the toolbar shows the formatting.

### v1.9.0

Upgrade as usual (`Upgrade-Tide.ps1 -Version latest`). It adds four migrations: `SavedActionViews`,
`ActionOriginalDueDate` (which fills the new original due date from each action's current one), `ActionSoftDelete`
and `JobLeaseAndRuns`. None of the earlier ones change. On a DbaApplies install, the DBA applies them first, as for any
release that carries one. The upgrade also sets the app pool to one worker process (`processModel.maxProcesses = 1`)
and reads it back.

**Changes to check before upgrading**
- **Know your organization's email domains.** TIDE now asks before saving any address outside them, on every list
  that sends mail: the edition recipients, rotation managers, brief-not-ready recipients, a team's mailbox and
  escalation contact, external teams' contacts, and template copies. With `Email:OrganizationDomains` unset, the
  domain of `Email:From` is used. If you send from a relay or SaaS domain, set your real domains on Access &
  distribution first, or every address will be asked about. Addresses already saved aren't asked about again.
- **External teams' emails are held to the team edition's TLP ceiling.** An external team's reminder, and the
  escalation contact's email for an external team or an outside contact, no longer name an action whose article is
  marked above that ceiling. It's counted, with "1 more request is marked above what we share by email: ask {team}".
- **Reminders stop for people who no longer have access.** Each scheduled reminder and digest now goes only to someone
  whose roles grant what it shows and who has signed in within `Notifications:ActiveWithinDays` (30 by default, on
  Teams & SLA). An inactive assignee's reminder goes to the team mailbox or its active members instead, and the run's
  note counts who was skipped. Someone who rarely signs in may stop getting reminders: raise the setting if so.
- **One server runs the background jobs.** A database lease means only one process runs the reminders, intake,
  webhooks and the other jobs. A second server or process on the same database stands by, says so on Diagnostics,
  and refuses an administrator's Run now, naming the server that holds the lease. Servers sharing a database need
  roughly synchronized clocks.
- **A risky file downloads zipped from any link.** A link straight to an action's file (one pasted in a comment, say)
  now gets the password-protected zip when `Files:ZipDownloads` says so. Only the file list's "as is" link, which
  carries a short-lived pass for that person and file, gets the plain file. Under `Always`, the API zips every file
  too, whatever `?zip` says.
- **A configuration bundle import asks first.** A bundle that widens what leaves the organization (raises a TLP
  ceiling, adds recipients or outside addresses, lowers a source's TLP, turns on external reminders or the escalation
  ladder, adds an escalation step that emails outside, rewords an email that goes outside) shows a warning on each such
  change, and Import needs "I've read the warnings". An import whose plan changed since its preview is refused.

**New settings** (all unchanged until set)
- `Email:OrganizationDomains`: your organization's email domains (Access & distribution).
- `Notifications:ActiveWithinDays`: 30. Who still gets scheduled reminders (Teams & SLA).
- `Notifications:ExternalReminders:NewTeamsOn` (`true`), `NewTeamsBeforeDays` (`2`), `NewTeamsEveryDays` (`3`, `0`
  for once): how a new external team's reminders start (Teams & SLA).

**For administrators**
- **Standing copies on email templates.** Each template can carry an *Always copy (CC)* and *Always blind-copy (BCC)*
  list, and one archive address can be blind-copied on every email TIDE sends. Templates to people outside TIDE take
  blind copies only; an outside copy is confirmed; the archive copy must be inside the organization. Copies are
  audited and never travel in a configuration bundle. A copy the relay refuses never costs the email its recipients.
- **Deleted actions.** An analyst or administrator can delete an action raised by mistake or twice, with a reason
  (Cancelled and Not applicable stay for work that won't be done). It disappears everywhere: lists, counts, reminders,
  search, the API and exports. Administration › Deleted actions shows who deleted each, when and why, with Restore.
  Numbers are never reused.
- **Job health for monitoring.** `/health/jobs` answers Unhealthy when an enabled job is hung or overdue, and Degraded
  after three failures in a row; point SCOM or an uptime checker at it. Run history is kept for 30 days and survives a
  restart, and each job failure is logged as a Warning with a stable event id (7101–7107). Diagnostics shows Hung,
  Overdue and Failing badges and recent failures.
- **Downloads leave a record.** Exports above GREEN, every file download, the configuration bundle, the install answers
  file and `audit.csv` are in the audit log (Downloads), and every download is in the access log.
- **External teams.** New teams can start with their reminders off, and with their own schedule. Wherever work is
  handed to an external team that won't be emailed (reminders off for everyone or for the team, or no contact email),
  TIDE says so, with the fix for whoever may make it, and External teams marks such a team *Not emailed*.

**For everyone**
- **Actions page.** New filters: held by (anyone holding an open action), internal or external teams, and raised by
  you, with a *Raised by me* pill. *+ Filter* is grouped under Who, Where, What and When, with a find box for a long
  list. Choose the tab Actions opens on (*Open here by default*), and save any combination of tab, layout and filters
  as a named view beside the tabs.
- **Honest on-time rates.** Each action keeps the first due date it was given and how often it moved. Team
  commitments adds "on time vs. original date" and "dates moved", and an action's page says when its date has moved.
- **Today.** Whoever writes the brief sees *Not on a brief yet*: Ready articles, then their own drafts, each with Add.
  Everyone else sees *Recently presented*, within the team edition's TLP.
- **Unsaved work is kept.** Leaving a page with unsaved changes (email templates, a comment, Record an update, an
  outreach email, the brief's note, the meeting note) asks first.
- **Toasts.** A toast waits while it's pointed at or focused, its buttons are larger, and Alt+Z presses the newest
  Undo.
- **Fixes:** on Mine, filtering to a team or kind with no matches no longer says "Nothing assigned to you".

### v1.8.0

Upgrade as usual (`Upgrade-Tide.ps1 -Version latest`). It adds seven migrations: `PlaybooksTeamLibrary`,
`LongerHuntQueries`, `EditVersionsForRepliesProfilesProducts`, `SourceEditVersion`, `KeptRotationDays`,
`ChecklistItemAddedAt` and `TeamEditVersion`. None of the earlier ones change. On a DbaApplies install, the DBA
applies them first, as for any release that carries one.

**Changes to check before upgrading**
- **`Database:Provider` is checked at start-up.** It must be `SqlServer` or `Sqlite` (any case), or blank for SQLite.
  A mistyped value such as "SQL Server" used to be quietly read as SQLite. It now stops start-up with a message
  naming the setting. Check yours if it was ever hand-edited.
- **Playbooks moved.** They're now at Team › Playbooks (`/playbooks`), kept by anyone who works actions, like Saved
  queries and Outreach templates. The old `/admin/playbooks` address redirects. Existing playbooks keep their steps and
  order; one that was inactive is now *retired*.
- **An email whose delivery isn't known is Unknown, not Failed.** A send that times out, or whose connection drops
  after the relay may have taken it, is recorded as Unknown and never retried by itself. Diagnostics, the reminder
  runs, an escalation's history and the brief's next send confirmation say to check whether it arrived before sending
  again. A clear refusal (a bad recipient, sign-in, relay unreachable, pickup folder unusable) is still Failed.
- **One reminder a day per external contact.** When the escalation ladder's remind step and an external team's own
  reminder fall on the same day for the same action, the second is skipped, and its history says so.

**For administrators**
- **Brief rotation keeps who presented.** Changing a week on Administration › Brief rotation now leaves days whose
  brief was already presented (or sent, or held) with the person who presented them. **Correct past days** is a
  separate, deliberate step to change those too, recorded in the audit log as a correction.
- **Two administrators editing the same team, source or product** no longer overwrite each other. A form opened
  before someone else's save is refused with who changed it, and stays open with a Reload button. The same applies to
  adversary profiles and to recording an outreach reply.
- **Diagnostics shows a failing reminder scan** as a failed job, naming the scan, not "No failures".
- **A retired team stays on Workload** while it still has open actions, marked Retired.
- **Links written from the site root** (`[x](/actions/12)`) now work when TIDE runs under a sub-path such as /tide,
  and in email they point at `App:BaseUrl`.

**For everyone**
- **The action page is laid out around the next step.**
  - One main button for what the action needs now: take it, record the reply, write the email, record an update, or
    close it. Everything else is under **More**.
  - A **Next** line says what the action is waiting on.
  - **What's happened** is one feed of comments and history.
  - The checklist, timeline, hunt queries and outreach sit in a drawer beside it.
- **Board rules** (Team › Board rules): everyone can see the card rules; administrators still edit them in Settings.
- **Text boxes grow** with what you type, up to a limit, then scroll.
- **Ctrl+Enter** (Cmd+Enter on a Mac) posts or saves the text box you're in.
- **Hand-offs ask about reminders.** Handing off an action whose reminders are off asks whether to keep them off.
- **A story's page updates live** when a colleague links an article to it, takes one off, or changes one of its
  articles.
- **Checklists keep one order**, and a playbook applied by two people at once adds its steps once. A playbook that
  doesn't fit on the checklist is refused whole, saying how much room is left, rather than added in part.
- **Hunt queries** search an international domain in both forms (as written and as logs record it), and a query TIDE
  fills with hundreds of indicators is no longer refused for its length.
- **Pages keep up with colleagues.** Adversary profiles, external teams and a story's page update as others change
  them, without losing what you've typed. Two quick changes can no longer leave a page showing the older one.
- **Safer text.**
  - A link a web page planted in a drafting suggestion, in any Markdown form, reaches the brief as plain text.
  - Angle-bracket links open in a new tab without telling the site where you came from.
  - A web address inside a command-line, registry or other indicator is defanged.
  - Long URLs and hashes wrap instead of running off their card.
- **Fixes**, among about 45:
  - Feed items with a wrong weekday, a placeholder date or a future date keep a sensible date.
  - A meeting note stays on the article it was begun on, and a meeting that runs past midnight keeps its day.
  - Escalation emails aren't sent twice after a run is interrupted.
  - An external team an action is handed to gets its own reminder.
  - The meeting, the action page and Coverage all ask for a reason when a team is at its "Warn" limit.
  - A garbled link no longer breaks a list page.
  - Tab and Shift+Tab move through a filter's form.
  - Escape closes a dialog after its text was clicked.
  - Undo puts an article back on the brief it came off.
  - An outreach placeholder typed wrongly is refused on save, not sent as typed.

### v1.7.0

Upgrade as usual (`Upgrade-Tide.ps1 -Version latest`). It adds two migrations, `HandoffsTrackingAndExternalTeams` and
`EscalationLadder`; none of the earlier ones change. On a DbaApplies install, the DBA applies them first, as for any
release that carries one.

**Changes to check before upgrading**
- **A new action status, Tracking.** The API can now return `Tracking` as an action's status: handed to others and
  being followed. It counts as open. A script or SOAR playbook that lists the statuses it expects needs to allow it.
- **Starter content on upgrade.** If your Saved queries, Outreach templates or Playbooks library is still empty, it
  gets a starter set once: 13 hunt queries (KQL, XQL, Falcon), 5 email templates and 7 playbooks, saved by "TIDE".
  A library that already has anything in it is left alone, and starters you delete or retire never come back.
- **The menu.** Saved queries and Outreach templates moved from Daily to Team.

**For administrators**
- **Escalation ladder** (Administration › Organization › Teams & SLA › Escalation). Steps run by themselves on an open
  action as it slips, a set number of business days before or after its due date: remind the team, copy the team's
  escalation contact, raise the priority, or flag it escalated (a badge, an Escalated card on Today, and the top of the
  meeting's overdue list). Off until you turn it on; "Use the defaults" fills in a starting ladder. Each team can have
  an escalation contact.
- **Active-work limit per team** (Teams & SLA), optional: show the count, warn and ask for a reason, or refuse.
- **External teams** (Actions › External teams): teams tracked in TIDE that work elsewhere and have no members, with a
  contact, how to engage them, and their own reminder schedule. Anyone who works actions can add and edit them.
  Their reminder emails make sense without TIDE access (no links, nothing from an article unless the analyst wrote it
  in). They're off until you turn on external reminders on Teams & SLA.
- **Triage grouping settings** (Administration › Sources): how far apart two reports of one event can be (12 to 96
  hours, 48 by default), and extra words for the grouping to ignore, beside the built-in ones.
- **New settings** (all off or unchanged until set): `Notifications:ExternalReminders:Enabled`,
  `Notifications:Escalation:Enabled`, `:Scope` and `:Steps`. They're in Teams & SLA and in the config bundle.
- **Documentation.** The docs are rewritten as a full set (architecture, data model, API reference, business rules,
  workflows, operations), in `docs/`.

**For everyone**
- **Hand off, split and follow work across teams.**
  - **Hand off** any action to another team with a reason; the history shows where it came from. Changing the team in
    the editor counts as a hand-off too.
  - **Split to teams**: from one action, create a part for each responsible team, each with its own due date and
    ticket link. Parts fill in from the parent.
  - **Tracking** is a new status for work handed to others and being followed, with its own board column.
  - **Lineage** on a parent or a part: the article, the parent and every part, coloured by state, with a
    **remediation timeline** of each part against its due date. "Part of A-12" filters the board to its parts.
  - **Record an update**: paste a team's emailed reply and set the status, a new date and a note in one step.
- **Team commitments** (Reports › Team commitments): for each team, over 30 to 365 days, how many actions were due,
  the share done on time, the average days late, and what's open and overdue now. It exports to CSV.
- **One filter bar on every list.** Search, one-click pills for the common filters (Overdue, P1–P2, Tracking; in
  Triage, Names a CVE, KEV, We run it), chips for the rest, and a "+ Filter" menu. Links and bookmarks still work.
- **Pages.** Triage and the actions list come in pages of 50, with the real count (Triage no longer stops quietly at
  200); Saved queries and Outreach templates in pages of 20. Board columns show 20 cards with "Show more", and Done
  shows the last 7 days, with "Show older" for 30.
- **More filters**: actions by search, priority, due date and status; triage items by CVE, KEV and "Names what we run".
- **Triage groups** reports whichever way each outlet capitalises its headline, and "Not the same: split" now holds
  for the whole team, with Undo.
- **Outreach templates** can name a date in business days: `{{today:+2}}` (or `-2`).
- **Typing quickly** in the ATT&CK picker, a table's filter box or a type-ahead field no longer loses letters.

### v1.6.0

Upgrade as usual (`Upgrade-Tide.ps1 -Version latest`). It adds two migrations, `OutreachEmailsAndStackContacts` and
`TriageSplitsAndGroupingSettings`; none of the earlier ones change. On a DbaApplies install, the DBA applies them
first, as for any release that carries one.

**Changes to check before upgrading**
- **"Vendor inquiry" is now "Outreach".** Every existing vendor inquiry becomes an Outreach action, with its outcomes,
  SLA, playbooks and card rules unchanged. An SLA target saved under the old name moves with the migration. A server's
  `Actions:Sla:Kind:VendorInquiry` value, an older config bundle and an old trends link still read as Outreach.
- **The API says `Outreach`.** Where it used to return the kind `VendorInquiry`, it now returns `Outreach`. A script
  or SOAR playbook that matches on the old name needs updating.

**For administrators**
- **New settings:**
  - **Outreach follow-up reminder**, in Administration › Organization › Teams & SLA. It's off until you turn it on.
    The wait is 1 to 30 business days, default 5. Each person can turn it off in My notifications.
  - **Triage grouping**, in Administration › Sources:
    - How far apart two reports of one event can be, 12 to 96 hours, default 48 (unchanged).
    - Extra words for the grouping to ignore.
    - The built-in words it always ignores, shown read-only.
- **Open pages no longer break after a reconnect.** A page left open could start showing "Cannot access a disposed
  object … SafeAccessTokenHandle" on every action once its connection had dropped and come back (a network blip, a
  laptop waking). Until then, reloading cleared it. The same fault stopped the two-minute permission re-check on
  those pages, so role changes might not have reached them.
- **Pages open with less server work.** A page used to read everything twice as it opened: once to draw it, again
  when it went live. It now reads again only if something was saved in between. Today's reload costs about half the
  server time it did.

**For everyone**
- **Outreach emails from templates.** For asking a vendor, an ISAC, another team or a site's owner for information:
  - **Outreach templates**, in the menu, is the team's library of email templates. Anyone who works actions can add,
    edit and retire them. A template fills in the article (title, bottom line, source, CVEs with KEV and EPSS), its
    indicators (defanged, all of them or one kind), the Our stack product, and the action and sender. Its TLP ceiling
    decides what it may carry: anything above it is left out, and the email says so.
  - On an Outreach action, **Write the email** fills a template for you to copy into your own mail. TIDE never sends
    it. **Mark sent** records who it went to, and the action then waits for the reply. **Record the reply** attaches what
    came back and closes the conversation.
  - A template can name a date in business days, skipping weekends and holidays: `{{today:+2}}` is two business
    days from today, as in "action required by {{today:+2}}", and `{{today:-2}}` two back.
  - **Our stack products have a contact**, a name and an email address. When an Outreach action names that product,
    its contact is offered as the recipient.
- **Triage groups more of the same story.** Two reports of one event now group even when one outlet writes its
  headline in sentence case and the other capitalises every word (as with the two DigiCert breach reports). They still
  need a name in common.
- **"Not the same: split" is kept for everyone.** Splitting a suggested group used to last until you left the page. It
  now holds for the whole team, the row says who split it and when, and Undo puts it back.

### v1.5.4

Upgrade as usual (`Upgrade-Tide.ps1 -Version latest`). It adds one migration (`SavedQueriesAndActionIndicators`);
none of the earlier ones change. No settings are new or renamed. On a DbaApplies install, the DBA applies that
migration first, as for any release that carries one.

**For administrators**
- **"Something went wrong" can be traced.** When an action fails unexpectedly, its message now ends with a short
  reference, such as `(reference 9C41F07A)`. The full error is in the Application event log under `Tide.Errors`, with
  that reference, who it happened to and what they were doing. INSTALL.md's "Logs" section explains how to find each
  kind of error.
- **A page closing as the connection drops no longer ends the session.** Leaving a page with the running order, the
  board, triage or present mode on it while the connection was shutting down could end the whole session ("Something
  went wrong. Reload the page"). Those page cleanups now let a cancelled call go.
- **Saved hunt queries** are a new team library, at **Saved queries** after Actions in the menu. Anyone who can read
  actions sees them. Anyone who works actions can add, edit, retire and restore them. Deleting is for whoever saved one,
  or an administrator. The built-in generation templates (**Administration > Query templates**) stay admin-only.

**For everyone**
- **Pages no longer flicker on reload.** Today, Triage, the actions list and board, an action's page, a new action,
  present mode, the article editor and the review page used to draw, flash a loading skeleton, then draw again on
  every reload. They now draw once.
- **Hunt queries:**
  - **Add a saved query** from an action's Hunt queries card, and **Save for the team** on any hunt, including one
    you've edited.
  - A saved query can say `{{days}}`, `{{values}}` (every indicator) or `{{values:domain}}`, `{{values:sha256}}` and
    so on (one kind). Tide fills them in for the action, quoted for the query language. It lists the values the query
    doesn't use, so **No hits, close** still asks you to acknowledge them.
  - **Add indicators** from the hunt card: one at a time, or found in pasted text. On an action raised from an
    article, they're saved on the article, so the brief, exports and other actions see them; that needs the right to
    edit articles, and the card says so if you can't. An action with no article keeps its own.
- **Present mode:**
  - On an article's slide, **N indicators** is a button that opens them over the slide, by type and defanged. When
    the presenter opens them, everyone following sees them, as with a product's card. Team edition only.
  - The source's name at the top of an article's slide links to the original report, in a new tab.

### v1.5.3

Upgrade as usual (`Upgrade-Tide.ps1 -Version latest`). It adds no migration and no setting.

**For administrators**
- **Reminder runs keep what they sent.** The reminder check comes round every few minutes after its time; a later
  check that found nothing new no longer replaces the day's real result, so Teams & SLA no longer says "0 sent" (or
  "all recipients opted out") after emails went out.
- **On Teams & SLA, a count opens one list.** The rotation reminder and the brief-not-ready nudge share one choice;
  clicking either count now opens the list of people under that row only.
- **A broken email pickup folder is a failed send.** When `Email:PickupDirectory` can't be created or written, a send
  or correction is recorded as failed with a plain message, instead of an unknown outcome that blocked finishing the
  brief; a test copy shows the message instead of an error.
- **Too-large metrics are refused everywhere.** A value or a warning or critical threshold that would round past what
  the database holds is refused with "too large" on SQLite as on SQL Server, and thresholds keep four decimal places.
- **The repository's automation asks for less.** The release workflow passes CI only the one secret it reads, and
  Dependabot waits a week before proposing a newly published version (security updates aren't held back).

**For everyone**
- **Files on actions:**
  - a file whose name hides an invisible character in its extension (`update.ps1` written with one) is judged by the
    name it's stored and downloaded under, so a script is flagged and zipped as it should be;
  - present mode no longer lists a comment's removed files;
  - adding many files at once keeps every file's SHA-256 in the history;
  - someone moved to a role that only reads (Viewer, Reader) can no longer remove files they added before.
- **Rotation:** undoing an approved swap or cover partway through gives back only the days still to come; days already
  presented stay with whoever presented them, and a change whose days have all passed can't be undone.
- **Metrics:** the day a number is compared with is named the same way in the email and the edition preview as on the
  entry form and in present mode, so after a holiday it says "vs Fri".
- **Search:** typing a single digit finds that action (it used to find nothing).
- **Reports:** the period report's CSV also guards names that start with a tab or a line break, as the other exports do.

### v1.5.2

Upgrade as usual (`Upgrade-Tide.ps1 -Version latest`). It adds no migration and no setting.

**For administrators**
- **Diagnostics shows the real connection type again.** Administration › System › Diagnostics showed every live
  page's connection as "Unknown", so a page stuck on long polling never got its warning. It now shows WebSockets or
  Long polling, with the warning for the latter.
- **Verify-Install and the upgrade no longer report OK for what they couldn't read.** When the WebSocket feature or
  IIS's setting for it can't be read (usually because the script isn't elevated), Verify-Install now warns "run
  elevated to check it", or fails if it is already elevated; the upgrade warns and carries on. Verify-Install also
  reads the site's folder whichever way IIS gives it, so its web.config check is no longer skipped without a word.
- **The audit log names threat-actor and We run tags** ("Article threat actor", "Article product we run") and links
  the article they're on, instead of a raw key.

**For everyone**
- **Pickers act on what you typed.** Pressing Enter or clicking straight after typing in Threat actors, We run or the
  threat story picker could pick from the list for the earlier letters, such as tagging APT28 for "APT29", making a
  new profile for a MITRE id an existing one has, or linking the wrong story. A double click on "Add" adds once.
- **An article's History shows its threat actors and products we run** being added and removed, by name.
- **Present mode follows better:**
  - someone joining late is no longer paused as soon as they land, and catches up on a title slide that scrolls;
  - after the presenter changes hands, a late joiner lands where the new presenter is, and a follower's earlier pause
    doesn't come back;
  - Space and Page Down scroll the open card or tag panel, not the slide behind it, and closing the tag panel puts a
    follower back in step;
  - a scroll the presenter made just before moving on is no longer applied to the next slide.

### v1.5.1

Upgrade as usual (`Upgrade-Tide.ps1 -Version latest`). It adds no migration and no setting, and nothing you see changes.

**Present mode, under the hood**
- **Space and Page Down answer at once.** The page decides for itself whether to scroll the slide or move to the next
  one, instead of asking the server first.
- **Fitting a slide does less work.** A slide is measured fewer times when it's fitted, and only when the slide or
  its content changes size, not on every change elsewhere on the page. The sizes slides settle at are the same.
- **Following the presenter's scroll is lighter.** The presenter's page sends at most ten updates a second, and each
  follower is sent only the latest one at a time, so slow connections don't fall behind.

### v1.5.0

Upgrade as usual (`Upgrade-Tide.ps1 -Version latest`). It adds no migration and no setting.

**For everyone**
- **Long slides fit the screen.** In present mode a slide that's too tall for the window shrinks smoothly, down to 80%
  of its normal size (text, metric tiles, charts and screenshots together). Below that, only the slide's content
  scrolls: the header and the progress bar stay put, and the page itself never scrolls.
- **Scrolling a slide looks the part.** The content fades at the edge where there's more, the scrollbar is slim and in
  the slide's colours, and a **More ↓** button scrolls most of a screen and hides at the bottom. Long action and
  product cards get the same scrollbar and fades. With reduced motion, scrolling is instant and nothing fades in.
- **Space and Page Down scroll first,** and go to the next slide only once you're at the bottom; Shift+Space and
  Page Up do the reverse. The arrow keys still change slides straight away. The shortcuts list says so.
- **Followers follow the presenter's scroll.** When the presenter scrolls a slide, everyone following on that slide
  scrolls to the same place, and someone joining late starts there. Scrolling on your own pauses following for that
  slide only and shows **Back to {presenter}'s place**; that button or the next slide picks following back up. In a
  held meeting you can still scroll on your own, and the presenter's next scroll brings you back.

### v1.4.0

Upgrade as usual (`Upgrade-Tide.ps1 -Version latest`). It adds no migration and no setting.

**For administrators**
- **WebSockets are checked.** TIDE's pages need IIS's WebSocket Protocol feature; without it (or behind a proxy that
  strips the `Upgrade` header) they silently fall back to long polling, one request per keystroke. `Upgrade-Tide.ps1`
  now warns when the feature is missing or turned off for the site (it never stops the upgrade); install it with
  `Install-WindowsFeature Web-WebSockets`. `Install-Tide.ps1` checks for it and offers to install it (`-InstallWebSockets`
  to do so unasked), `Verify-Install.ps1` fails without it and tries a real WebSocket handshake, and Administration >
  System > Diagnostics shows the connection type your page is using and how many connections used each since the app
  started (a page that reconnects counts again). An unattended install (answers file or a non-interactive session)
  without `-InstallWebSockets` or `InstallWebSockets = $true` warns and carries on; the answers file downloaded from
  Administration now carries the `InstallWebSockets` key. See [INSTALL.md](INSTALL.md#1-what-you-need).
- **The article's free-text "Threat actors" box is gone.** Its column (`Articles.Actors`) is kept and nothing in it is
  deleted or rewritten by the upgrade; the editor no longer writes it. Each article's earlier text shows under the
  Threat actors picker as an **earlier note**: names a profile goes by (its name or an alias, ignoring case) are offered
  as one-click "Tag X" buttons, and the rest stay in sight as "Earlier note: ..." until an analyst clears them (×).
  Nothing is tagged automatically. Clearing is recorded in the article's history as "Threat actors (earlier note)".
- **Where the text was read, the tags are now read first.** A story's actors list and search use the tagged profiles'
  names, then whatever the earlier note names that no tag covers, so nothing that showed before disappears. No email,
  edition, slide, SharePoint copy, export or API field carried the text, so sent briefs are unchanged. The API never
  accepted it either, so no client needs changing.

**For everyone**
- **Threat actors** is the adversary picker, under Tags & ATT&CK › Context, where the text box was: the one place to tag.
- **We run** sits inside "Why it matters to us", always open, and uses the same search picker as Threat actors: what the
  text names first, then what was tagged lately, any other name ("(also Pulse Secure)"), retired products last, and
  "Add 'X' to Our stack". Mention still adds a product to why it matters.
- **The rail's Related tab is gone.** Similar articles is a folded section under the threat story, "Similar articles
  (N)". Someone who can't edit sees the article's products and threat actors as read-only cards in the rail.
- **Scorecard tags** show the tags chosen, an "Add a tag" search by tag or category name, and the full list folded
  under "Browse all tags", in the editor and in present mode's tag panel.
- **The ATT&CK picker** hides tactics with no match while you search (smoothly, or at once with reduced motion), says
  how many tactics and techniques match, and scrolls back to the start.
- **Search pickers** wait for a short pause in typing before they search (the story picker too, now), and keep the
  highlighted option in view as the arrow keys move.

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
