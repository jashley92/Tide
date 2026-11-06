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
