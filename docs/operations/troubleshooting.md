# Troubleshooting

How to diagnose a running TIDE that misbehaves, from a server that won't start to a reminder that didn't go. It's for
the operator and the administrator. Each entry reads **Symptom → Cause → Where to look → Fix**. How the pieces work is
in [configuration.md](configuration.md), [integrations.md](integrations.md) and the
[architecture](../architecture/overview.md) pages; the quick table for a new install is in
[INSTALL.md](../INSTALL.md#troubleshooting).

**Contents**

- [Where to look](#where-to-look)
- [Start-up and the database](#start-up-and-the-database)
- [Sign-in and access](#sign-in-and-access)
- [Connections and pages](#connections-and-pages)
- [Email](#email)
- [Reminders and digests](#reminders-and-digests)
- [Intake and CVE data](#intake-and-cve-data)
- [Articles, files and drafting](#articles-files-and-drafting)
- [Metrics drop folder](#metrics-drop-folder-files-arent-imported)
- [Settings, updates and webhooks](#settings-updates-and-webhooks)

## Where to look

| Source | What it tells you | How |
|---|---|---|
| Windows **Application** event log | Start-up failures, Critical and Error entries, warnings | Event Viewer › Windows Logs › Application, source `.NET Runtime`. Only warnings and above by default; see [INSTALL.md](../INSTALL.md#logs) to raise it. |
| `logs\stdout_*.log` in the site folder | Console output when the app can't start at all | Set `stdoutLogEnabled="true"` in `web.config`, recycle, reproduce, turn it off again. An upgrade replaces `web.config`. |
| An error reference | The exact failure behind a message | A page's error shows a trace id such as `00-4bf92f…-…`: search the log for the 32-character part. An action's message ending `(reference 9C41F07A)`: search for it; the `Tide.Errors` entry ("Unexpected error 9C41F07A for … in …") has who and the full exception. |
| **Administration › System › Diagnostics** | Database and schema, folders, key ring, email, failing sources, each background job's last run, role mappings, WebSockets | Administrators. Each check links to where it's fixed. |
| **Administration › System › Setting sources** | Each setting's value in force and where it came from | Administrators. |
| **Administration › Sources**, Triage's **Sources** tab | Each source's last poll, new items and error; the last CVE run | |
| **Administration › Teams & SLA** | Each reminder scan's last run, emails sent, opt-outs, error; **Run now** | |
| **Administration › Metric imports** | What each import did, row by row | |
| The brief's **Send** step | The send log: each send's status, error, and "outcome unknown" | |
| `/health`, `/health/live`, `/health/jobs` | Database readiness; the process up; the background jobs | Anonymous; status word only. |
| Browser developer tools (F12) | The `_blazor` WebSocket (101), console errors, 4xx and 5xx on files | Network and Console tabs. |
| IIS logs (`%SystemDrive%\inetpub\logs\LogFiles`) | Status code per request | |

TIDE never logs parameter values, passwords, email addresses, subjects or a relay's reply, so the log names kinds of
failure and counts, not content.

## Start-up and the database

### HTTP 500.30 or 500.37: the app didn't start

- **Cause:** start-up threw. TIDE stops on purpose rather than run wrongly when:
  - `Auth:Mode` isn't `Windows` in Production ("Auth:Mode is 'Dev' in Production…", `AuthModeGuard`);
  - `DataProtection:KeyPath` is relative ("must be an absolute path");
  - `App:PathBase` isn't a path like `/tide` (`PathBaseSetting`);
  - `Files:RootPath` is blank, or the pool can't create it;
  - `Database:ApplyMigrations=false` and migrations are pending (next entry);
  - `Database:Provider` is neither `SqlServer` nor `Sqlite` ("Database:Provider is 'SQL Server'…");
  - `ConnectionStrings:Default` is missing for SQL Server, wrong, or the pool account has no login;
  - the server's `Org:TimeZone` isn't a zone this server knows.
- **Where to look:** the Application event log, or `stdout` logging. The message names the setting.
- **Fix:** correct the setting in `appsettings.Production.json` (or the environment variable) and recycle. 500.37 is
  a start that took longer than `startupTimeLimit`, usually a long first migration: wait, then check the log.

### Migrations fail, or start-up says "The database schema is behind this version of TIDE"

- **Cause:** with `Database:ApplyMigrations=true` (AppMigrates) the pool needs `db_owner`; without it the migration
  fails. With `false` (DbaApplies) the app never migrates and refuses to start while any migration is pending
  (`DatabaseInitializer.InitializeAsync`).
- **Where to look:** the event log's exception. Diagnostics' **Schema** check lists pending migrations once the app
  runs. `Upgrade-Tide.ps1 -WhatIf` compares the database's `__EFMigrationsHistory` with the release's
  `migrations-sqlserver.txt` and names the missing ones.
- **Fix:**
  - **AppMigrates:** grant `db_owner` (`Install-Database.ps1` mode AppMigrates) and recycle.
  - **DbaApplies:** the DBA applies the release's schema script from the bundle's `deploy\sql`, then start or rerun the
    upgrade. Don't flip `ApplyMigrations` to `true` on a pool without `db_owner`.
  - "There is already an object named …": the database has TIDE's tables but no migration history. Don't force it;
    see [UPGRADE.md](../UPGRADE.md#upgrading-with-upgrade-tideps1) (preflight).
  - A failed upgrade that migrated: the pool is left stopped; restore the pre-upgrade backup
    ([backup-and-recovery.md](backup-and-recovery.md#after-a-failed-upgrade)).

### `/health` says `Unhealthy`

- **Cause:** the database didn't answer a query within 5 seconds (`DatabaseHealthCheck`).
- **Where to look:** the log's `Tide.Health` warning, once a minute: "Readiness is Unhealthy: database …".
- **Fix:** check SQL Server, the network, and the pool account's login. A 401 on `/health` means the anonymous
  carve-out is missing: rerun `Install-Tide.ps1` or `Upgrade-Tide.ps1`, which add it.

### `/health/jobs` says `Unhealthy` or `Degraded`

- **Cause:** a background job that's turned on is hung (a run past its limit), overdue (no good run in three of its
  intervals, at least 15 minutes), or failing (its last 3 or more runs failed: `Degraded`).
- **Where to look:** Diagnostics › Health checks › "Background jobs" names the job and why; the jobs table shows its
  last error and the recent failures. The log has a Warning per failed run, "Background job … failed (N in a row)",
  under the job's event id (7101 to 7107, listed in
  [backup-and-recovery.md](backup-and-recovery.md#hosted-services-inside-the-app)).
- **Fix:** fix what the error names (the relay for reminders, the proxy for feeds and the CVE data). A hung job
  usually waits on a host that never answers: recycle the pool to start it again. Overdue with no failures means the
  job stopped running: check "Job lease" on Diagnostics, then recycle.

### A job never runs on this server, and Diagnostics says "Standing by"

Run now, Poll now, Pull now, the CVE refresh and Check now say "This server is standing by: … runs the background
jobs. Run it there." on such a server.

- **Cause:** another process on the same database holds the job lease: another server pointed at this database (a
  standby started by mistake), or a second worker in the pool.
- **Where to look:** Diagnostics › "Job lease" names the holder (machine and process id), and "Second process" says
  both are running.
- **Fix:** stop the other server, or set the pool's Maximum Worker Processes to 1 (rerun `Install-Tide.ps1` or
  `Upgrade-Tide.ps1`, which set it). If the holder died, this process takes over within 3 minutes by itself, or at
  its next check when the holder was on the same server.

### A banner says "Second process running", or `/health/jobs` is `Degraded` with no failing job

- **Cause:** another TIDE process on the same database has written a heartbeat in the last 3 minutes: a second worker
  in the pool (a web garden), another server pointed at this database, or an overlapping recycle that hasn't finished.
  TIDE runs as one process, and some of its locks are the process's own, so two can make conflicting changes.
- **Where to look:** the banner (administrators only) and Diagnostics › "Second process" name both processes
  (machine and process id).
- **Fix:** stop the extra one, as above. The alarm clears within 3 minutes of it stopping, and at once when it shuts
  down cleanly. A brief alarm during a recycle needs nothing.

## Sign-in and access

### 401 for everyone

- **Cause:** Windows Authentication isn't on for the site, or the browser doesn't send credentials to the host.
- **Where to look:** IIS logs (401.2 means no credentials offered); IIS Manager › Authentication.
- **Fix:** turn Windows Authentication on (the installer does). Add the host to the browser's Local Intranet zone by
  policy.

### Sign-in prompts, or works only with NTLM: Kerberos and SPNs

- **Cause:** the `HTTP/<host>` SPN is missing, duplicated, or on an account that can't decrypt the ticket. Under
  another site with `useAppPoolCredentials` on, the parent's pool account decrypts tickets and TIDE's can't, so
  sign-in falls back to NTLM, or fails where NTLM is off.
- **Where to look:** `setspn -Q HTTP/<host>`; `klist` on a client; the Security log's logon events (Kerberos or NTLM);
  `Install-Tide.ps1`'s warning about `useAppPoolCredentials`.
- **Fix:** one SPN per host name, on the account that decrypts: the computer account with kernel-mode authentication
  and `useAppPoolCredentials` off (IIS's default), or the shared pool account. See
  [INSTALL.md](../INSTALL.md#under-another-site-optional).

### "You don't have access" for a signed-in person

- **Cause:** the account is in no mapped group, or only in groups mapped to roles that don't reach the page.
- **Where to look:** the log's "Access denied for {User} at {Method} {Path}" (pages) or "Access denied for {User} in
  {Operation}: {Reason}" (inside a page). **Access & distribution** lists the mappings. What each role can do is in
  [INSTALL.md](../INSTALL.md#what-each-role-can-do).
- **Fix:** map the group. A change reaches open pages within 2 minutes; no sign-out needed.

### Signed in, but a group grants nothing

- **Cause:**
  - A mapping by name (`CONTOSO\SOC-Analysts`) needs the group SID's name. `SidNames` waits 5 seconds for the
    server's LSA to translate the token's SIDs. A slow domain controller means that request gets only its
    **SID-mapped** roles, failing closed. Names that land later are kept, so it usually comes right on a later request.
  - A bare group name (`SOC-Analysts`) maps in the server's own domain. If the server couldn't find its domain, bare
    names map nothing until it does.
  - The group is in a different domain or forest and isn't in the token, or it's a distribution group (not security).
  - The role directory failed to reload after a mapping change, so sign-ins still use the old mappings.
- **Where to look:** the log: "A directory lookup in {Domain} has been blocked…", Critical "Couldn't look up this
  server's AD domain…", Critical "The role directory couldn't reload…". Diagnostics' **Role mappings** check.
  `whoami /groups` as the user shows the token's groups and SIDs.
- **Fix:** fix DC reachability from the web server. For a domain that's slow by nature, map the group by its SID,
  which needs no lookup. "Found this server's domain: bare AD group names map again" and "The role directory
  reloaded" mean it recovered by itself.

### A page stops working after a reconnect: "Cannot access a disposed object … SafeAccessTokenHandle" (fixed)

- **Cause:** a page's session was given the connection's Windows principal, whose claims read a logon token that IIS
  disposes when that connection ends. A page outlives its connection (a reconnect, a laptop waking), and then every
  permission check, and the 2-minute re-check, failed.
- **Fix:** fixed after v1.5.4. `PermissionRevalidatingAuthStateProvider` now copies the sign-in into plain claims as
  soon as it's set. Upgrade. If "Couldn't copy the session's sign-in; it keeps the one its connection gave it" appears
  in the log, report it: the copy failed and the old behaviour applies to that page.

## Connections and pages

### Typing lags, or pages keep reconnecting: WebSockets

- **Cause:** WebSockets don't get through, so Blazor falls back to long polling, which is slow and fragile. Either the
  IIS WebSocket Protocol feature isn't installed, `system.webServer/webSocket` is off for the site, or a proxy or load
  balancer drops the `Upgrade` and `Connection` headers.
- **Where to look:** Diagnostics' **Your connection** and **Everyone's connections**; the browser's Network tab (one
  `_blazor` WebSocket with 101 is good; many `xhr` rows for `_blazor?id=` is long polling); `Verify-Install.ps1`.
- **Fix:** `Install-WindowsFeature Web-WebSockets` (or `Install-Tide.ps1 -InstallWebSockets`), and pass the headers
  through any proxy. See [INSTALL.md](../INSTALL.md#5-check-it).

### Typing quickly in a search box drops letters, or the text jumps back (fixed)

- **Symptom:** in a table's filter box, a search picker (adversaries, stories, tags) or the ATT&CK matrix picker's
  filter, fast typing loses characters, or the box briefly shows an older word. Worse on a slow or distant connection.
- **Cause:** the box was drawn as `value="@text"` with `@oninput`. Only `@bind` makes Blazor copy the browser's value
  into its stored render as each keystroke arrives, so for these boxes Blazor's copy was the last value it drew. A
  later render compared the server's text with that stale copy and sent it, and by the time it landed the person had
  typed more, so it overwrote the newest keystrokes. Boxes that skip renders while typing pauses (the matrix picker)
  widened the gap.
- **Where to look:** search the `.razor` files for an input with `value="@` beside `@oninput`. The browser's Network
  tab, throttled to Slow 4G, makes it easy to reproduce.
- **Fix:** bind the box with `@bind:get`, `@bind:set` and `@bind:event="oninput"` (`DataTable`, `SearchCombobox`,
  `AttackMatrixPicker`). A render carrying the typed text then sends nothing, while a clear or a new value from outside
  still reaches the box. `SearchCombobox` keeps its own copy of the text and takes the parent's `Text` only when the
  parent changes it, so a parent redrawing older text doesn't overwrite what's typed.

### A list opens as "Something went wrong on this page" from a link (fixed)

- **Symptom:** a typed or edited address, or a link an email client cut short, opens Triage, Actions, an action's page,
  Articles, Trends or Team commitments as "Something went wrong on this page", for example `triage?named=1`,
  `actions?team=network` or `trends?subs=1`.
- **Cause:** the page read that query parameter as a `Guid?`, `int?` or `bool?` (`[SupplyParameterFromQuery]`), and
  Blazor fails the whole page when the value doesn't parse as that type.
- **Where to look:** search the `.razor` files for `[SupplyParameterFromQuery` on a property that isn't `string?`.
- **Fix:** read it as `string?` and parse it where it's used (`FilterValues.Id`, `Number`, `Flag`), so a value that
  isn't one is ignored, as an unknown `?status=` or `?page=` already was.

### The page goes grey with a reconnect message, or "Refused a circuit request … from origin …" in the log

- **Cause:**
  - A dropped connection (sleep, network blip). The session is kept for 20 minutes, so reconnecting picks up where it
    was.
  - A message from the browser over 1 MB drops the circuit. Every text box has a limit well under it; screenshots
    don't count.
  - Behind a reverse proxy that rewrites the host, the browser's `Origin` isn't the request's host, so
    `CircuitOriginMiddleware` refuses the circuit with 403.
  - The app pool recycled without a persisted key ring (`DataProtection:KeyPath` blank), so every session ended.
- **Where to look:** the log; Diagnostics' **Data Protection keys**; the browser console.
- **Fix:** set `App:BaseUrl` to the address users type: its host is also accepted as the circuit's origin. Set
  `DataProtection:KeyPath` to an absolute folder (the installer does).

### Links or styles break under a sub-path

- **Cause:** TIDE is served under a path (`/tide`) but doesn't know it, or knows it twice.
  - As an IIS application inside another site, IIS sets the path base; `App:PathBase` must stay blank.
  - Behind another reverse proxy that forwards `/tide` to the app's root, set `App:PathBase` to `/tide`. The page's
    `<base href>` and every app link follow `Request.PathBase`.
  - Links in emails and webhooks come from `App:BaseUrl`, not the path base.
- **Where to look:** the page source's `<base href>`; a CSS or `_blazor` request returning 404; an email's link.
- **Fix:** set exactly one of IIS's application path or `App:PathBase`, and set `App:BaseUrl` to the full address
  including the sub-path (`https://intranet.example.org/tide`). Recycle after either.

## Email

### Nothing is emailed

- **Cause and where to look**, in order:
  1. `Email:Enabled` is `false`: send buttons say email is off; Diagnostics' **Email** is Info "Off".
  2. No relay: "No mail relay is set (Email:SmtpHost)."
  3. Sign-in settings conflict (`EmailOptions.SignInProblem`): a user name without a password (or the reverse), or
     `SmtpUseDefaultCredentials` with either.
  4. The relay is unreachable: "The mail relay couldn't be reached. Check Email:SmtpHost and Email:SmtpPort…", logged
     as "Failed to send an email to N recipient(s): SmtpException …".
  5. The relay is backed off: "The mail relay hasn't been answering; try again in N minute(s)." After three failures
     in two minutes (`OutboundCircuit`), sends fail fast for 1 minute, doubling to 10. Diagnostics shows the wait.
  6. A refusal: "refused the sign-in (SMTP 535)", "said to try again later (SMTP 4xx)", "refused the message".
  7. `Email:PickupDirectory` set by mistake in production, or a folder the app pool can't write to: messages go to
     `.eml` files, or fail with "The email pickup folder (Email:PickupDirectory) can't be created or written to.", logged
     as "The email pickup directory can't be written to". It's never backed off as a relay that isn't answering.
- **Fix:** correct `Email:*` in `appsettings.Production.json`; it applies at the next send with no restart (the
  password, an environment variable, needs a recycle). Check the firewall to the relay, and that the relay lets TIDE
  send as `Email:From`. Send yourself a test copy from the brief's Send step.

### Some recipients didn't get it

- **Cause:** the relay accepted the message for some addresses and refused others; or an address wasn't valid and was
  skipped.
- **Where to look:** the send log says "N addresses were refused by the relay"; the event log "An email reached X of Y
  recipient(s)…" and "Skipped N recipient address(es) that aren't valid".
- **Fix:** correct the addresses on **Access & distribution**. Don't resend to everyone: the relay already delivered to
  the rest.

### A send says its outcome isn't known

- **Cause:** a frozen send was handed to the relay and then TIDE couldn't tell whether it went: the relay didn't answer
  within `Email:SmtpTimeoutSeconds` ("didn't answer within N seconds"), the connection dropped partway through, an
  unexpected exception during the send, or a send still "sending" 15 minutes later because the server stopped or
  saving its outcome failed (`SendOutcome.Seen`). A relay that refuses outright, can't be reached at all, or an
  unusable pickup folder is a plain failure instead.
- **Where to look:** the brief's Send step: the send's log entry says "whether it took the message isn't known" or
  "whether the relay took it isn't known". A slow relay that times out often: raise `Email:SmtpTimeoutSeconds`.
- **Fix:** ask a recipient. Then click **It went: mark sent** (the send is archived and the brief is Sent) or **It
  didn't: mark not sent** (it's Failed, and you can send again). Corrections have the same buttons. While a send is
  still with the relay, a second one of that edition is refused. See [distribution.md](../workflows/distribution.md).

## Reminders and digests

### A reminder or digest didn't go

- **Cause and where to look**, in order. **Teams & SLA** shows each scan's last run (sent count, note and error):
  1. The scan is off. Each kind is off until enabled (the personal digest's offer is on, but sends nothing until
     someone chooses one).
  2. Its time hasn't come: `ReminderTime` (or the digest's day and time, or the brief-not-ready time before the meeting)
     in `Org:TimeZone`, on a business day. A holiday is not a business day.
  3. It already went. A `NotificationMark` row records each thing sent, so it's never sent twice: once per action per
     due date for due soon and overdue (moving the due date earns a new one), once per email for outreach follow-up.
  4. People opted out on **My notifications**. `ReminderRun.Note` says "N opted out" or "all recipients opted out".
  5. The person has no email address. TIDE reads `mail` from AD at sign-in; someone who never signed in, or has no
     `mail`, has none. An unassigned action goes to its team's mailbox, else its members.
  6. Email is off or failing (previous section). A recipient whose email failed is tried again at the next scan.
     One whose email may have gone (the relay timed out or dropped the connection) isn't: it's marked as sent, and the
     run's note (and Diagnostics' *Reminder and digest emails* job) says "N emails may not have arrived … check with
     the recipients before sending again".
  7. The scan failed: the last run's error, and the log's "A reminder scan failed; it's tried again at the next poll."
- **Fix:** turn the scan on, check the time zone, holidays and the person's address. **Run now** runs a scan
  immediately (marks still stop repeats). A restart clears the "last run" display, not the marks.

## Intake and CVE data

### Feeds aren't polled, or a source keeps failing

- **Cause:**
  - `Intake:Enabled` is off (Sources). An administrator's **Poll now** still works.
  - The source failed. Each consecutive failure doubles its interval, up to 16 times (`IntakeService.Interval`).
  - The host is backed off: three failures in two minutes back it off 1 to 10 minutes (`OutboundCircuit`): "… hasn't
    been answering; it's tried again in N minute(s)".
  - Common errors: "couldn't be found (DNS)", "didn't answer within N seconds", "answered HTTP 403/404", "The response
    is larger than the N KB limit", "The server redirected from https to http, or more than five times".
  - An analyst's **Pull now** on Triage is refused inside the pull-now window (`Intake:PullNowMinutes`).
- **Where to look:** the source's last error on **Sources** or Triage's Sources tab; Diagnostics' **Sources** and the
  "Polling the sources" job; the log's "A request to {Host} failed" and "Looking up a host for {Host} failed".
- **Fix:** fix the URL, or the network path. A success resets the back-off. Raise `Intake:TimeoutSeconds` or
  `MaxResponseKB` for a slow or large feed.

### Nothing reaches the internet: the proxy

- **Cause:** the server reaches the internet only through a proxy, and `Intake:Proxy` isn't set (or is wrong). The
  proxy is read at start-up and signed in to as the app pool.
- **Where to look:** errors like "couldn't be found (DNS)" or "couldn't be reached (ConnectionError)" on every source;
  Setting sources for `Intake:Proxy`.
- **Fix:** set `Intake:Proxy` (`http://proxy.example.org:8080`) and recycle. Allow the app pool's account through the
  proxy. The same proxy serves the CVE services, MITRE, the update check and Azure OpenAI.

### "… is a private address, which TIDE doesn't fetch from"

- **Cause:** `AddressPolicy` refused the address: the feed, a CVE mirror, or a redirect resolves to a private range
  (10/8, 172.16/12, 192.168/16, 100.64/10, IPv6 unique-local) and `Intake:AllowPrivateNetworks` is off. Loopback,
  link-local and reserved addresses are always refused.
- **Where to look:** the source's error, and the log's "Refused to fetch from {Host}: …".
- **Fix:** for an internal feed or mirror, set `Intake:AllowPrivateNetworks=true` on the server. It doesn't allow
  loopback or link-local. It isn't counted against the host's back-off.

### CVE chips are missing or stale

- **Cause:** `Cve:Enabled` is off; the run hasn't come due (`Cve:RefreshHours`); NVD is slow without a key (one lookup
  per 6.5 seconds, `MaxNvdPerRun` a run, so a large backlog takes several runs); or a service failed. KEV, EPSS and NVD
  fail independently and the run keeps what it got.
- **Where to look:** **Sources** shows the last run: counts "KEV n, EPSS n, NVD n" and the first error ("KEV: …",
  "EPSS: …", "NVD (CVE-…): …"). Diagnostics' "Refreshing the CVE data" job; the log's "Refreshing the CVE data
  failed".
- **Fix:** turn it on; set `Cve__NvdApiKey`; check the proxy; for a mirror on a private address, set
  `Intake:AllowPrivateNetworks`. An NVD answer that isn't JSON (a proxy's block page, a rate-limit page) stops the NVD
  part: check what the proxy returns. **Refresh now** on Sources runs a short refresh.

## Articles, files and drafting

### Screenshots don't show

- **Cause:**
  - The viewer may not see it. A Reader only gets images from a sent company-wide edition, never a live screenshot;
    a Visitor only while in the meeting (`ScreenshotService.OpenAsync`). The endpoint answers 404, not 403.
  - The file isn't on disk: a database restored without its files, or `Files:RootPath` pointing elsewhere.
  - An image in Markdown from another site: the Content-Security-Policy allows images only from TIDE itself and
    `data:` (`img-src 'self' data:`), so an external `![](https://…)` is blocked.
  - Too many requests: 300 a minute per person on `/files/` (429).
  - The upload was refused: only real raster images are accepted as screenshots (the bytes are sniffed, not the
    name), within `Files:MaxImageMB`. Served with `X-Content-Type-Options: nosniff`, so a file never runs as another
    type.
- **Where to look:** the browser's Network tab for `/files/{id}/inline` (404, 429); Diagnostics' **Screenshot folder**;
  the console for CSP violations; `Test-TideRestore.ps1`, which checks every listed file is present.
- **Fix:** restore the files with the database ([backup-and-recovery.md](backup-and-recovery.md)); paste or drop the
  image into TIDE instead of linking it; check the role.

### "Someone else changed this … since you opened it. Reload…"

- **Cause:** optimistic concurrency. Articles, actions, comments and several admin pages carry an `EditVersion`; a save
  whose version is behind the stored one is refused with `StaleEditException`, so nobody silently overwrites a
  colleague.
- **Where to look:** the message names what changed. History shows who.
- **Fix:** reload, then reapply your change. Open pages usually update live; see
  [collaboration.md](../architecture/collaboration.md).

### AI drafting is refused or fails

- **Cause and fix:**
  - "This article is TLP:AMBER and the AI limit is TLP:GREEN, so nothing is sent." The input's TLP (the article's,
    or its feed's if higher) is above `Ai:MaxTlp`. By design (ADR
    [0004](../decisions/0004-opt-in-ai-with-tlp-ceiling.md)). Use the copied prompt in an approved tool only if
    policy allows, or draft by hand. Raise `Ai:MaxTlp` only with your data owner's approval.
  - "No AI provider is set up." `Ai:Provider`, an `https` `Ai:Endpoint`, `Ai:Deployment` and `Ai__ApiKey` are all
    needed (`AiOptions.Configured`).
  - "refused the key (Ai:ApiKey)" (401, 403), "no deployment by that name (Ai:Deployment)" (404), "busy (rate
    limited)" (429), "refused the request… filtered, or the model may not support this" (400: the deployment must
    support structured outputs), "didn't answer within N seconds", "couldn't be reached".
  - "Articles from {source} aren't fetched": the source's article fetch is Never. Paste the text.
  - Web grounding didn't help: it runs only with `Ai:WebGrounding`, a source marked Open, and a TLP:CLEAR article.
- **Where to look:** the editor's message; the log's "Azure OpenAI answered HTTP {Status}" or "couldn't be reached".

## Metrics drop folder: files aren't imported

- **Cause:**
  - The drop folder is off (**Metric imports**, `Metrics:Import:DropFolderEnabled`). While off it's checked every
    minute; on, every `PollMinutes`.
  - The file isn't eligible: only `*.csv` directly in `metrics-inbox`; hidden files, links and names starting `.` or `~`
    are skipped; a file changed in the last 30 seconds, or still held open by its writer, waits.
  - The exporting account can't write to `metrics-inbox` (the data root grants only the app pool).
  - The file was refused whole (moved to `rejected`), or importing threw (moved to `failed`). Over 1 MB is refused.
  - The database was unavailable: the file stays for the next pass.
  - Rows applied but values didn't change: an import never overwrites a value an analyst typed, or a day whose brief
    was sent.
- **Where to look:** **Metric imports** (each import's rows and problems); the `processed`, `rejected` and `failed`
  folders, where each file is renamed with the time it was read; the log: "Importing {File} failed; it's moved to the
  failed folder", "The database wasn't available for {File}", "Couldn't move {File} out of the metrics drop folder";
  Diagnostics' "Metrics drop folder" job.
- **Fix:** turn it on; grant the exporter modify on `metrics-inbox` alone; fix the CSV to the format shown on the page.
  Moved files are deleted after 90 days.

## Settings, updates and webhooks

### A settings change didn't take effect

- **Cause:**
  - An in-app value is saved, so editing the server's file changes nothing: the saved value wins.
  - A server setting read at start-up (sign-in, database, data folders, `Intake:Proxy`, `App:BaseUrl`, `App:PathBase`)
    or any environment variable needs a recycle.
  - The file has a value that won't bind (`"Enabled": "yes"`): the intake and drop-folder jobs log "An intake pass
    failed before it ran (check the Intake and Cve settings)" or "A Metrics:Import setting can't be read" and wait.
- **Where to look:** **Setting sources** shows the value in force and its source.
- **Fix:** change it where it's in force, or recycle. See [configuration.md](configuration.md#when-a-change-takes-effect).

### The upgrade refuses the bundle: its signature or SHA-256

- **Cause:** `Upgrade-Tide.ps1` checks a bundle before it expands it
  ([deploy/README.md](../../deploy/README.md#checking-a-bundle)), and stops with "Nothing was changed." when:
  - "… .sig isn't a valid signature of this bundle by TIDE's release key …": the zip was changed or damaged after it
    was signed (a partial download, a copy that altered it), or the `.sig` belongs to another release;
  - "The bundle isn't signed: there's no … .sig beside it": only the zip was copied, or it's a release from before
    signing (v1.8.0 or earlier), such as a downgrade;
  - "The bundle's SHA-256 (…) doesn't match …": the zip differs from the `.sha256` beside it, from `-ExpectedSha256`,
    or from the release's `.sha256` on github.com.
- **Where to look:** the upgrade's console output; `Get-FileHash <zip> -Algorithm SHA256` against the hash on the
  release page; `Test-TideBundle.ps1 <zip>` from the installed `<site folder>-deploy` for the same checks without
  upgrading.
- **Fix:** download the zip, its `.sig` and its `.sha256` from the release page again and put them in one folder. For
  a release from before signing, pass `-AllowUnsigned` (and the hash, with the `.sha256` beside it or
  `-ExpectedSha256`). `-AllowUnsigned` doesn't help with a `.sig` that doesn't verify: that is always refused.

### The upgrade warns "Not checked against the release"

- **Cause:** the bundle isn't signature-checked (no key is pinned yet, or `-AllowUnsigned`), no hash was at hand, and
  the server can't reach github.com to fetch the release's `.sha256`. The upgrade carries on.
- **Fix:** copy `tide-<v>.zip.sha256` from the release page beside the zip, or pass `-ExpectedSha256 <hash>`; either
  is used without asking github.com.

### The update check says it couldn't check

- **Cause:** no route to `api.github.com` (no proxy set, or blocked), or GitHub answered 404 (a private repository
  answers anonymous requests with 404).
- **Where to look:** **System › Updates** and Diagnostics; the log's "Update check failed: …" (once, until it works).
- **Fix:** set `Intake:Proxy` or allow the host, or leave the check off and watch the releases page.

### A webhook isn't delivered

- **Cause:** the receiver answered non-2xx, redirected (not followed), or didn't answer within 15 seconds; or "The
  signing secret can't be read (the server's keys changed). Add the webhook again.": the key ring changed (a lost
  `dp-keys`, or a restore to another server). A hook stops at its first
  failure each run and tries again next minute.
- **Where to look:** **API access** shows each hook's last status and error; **Ping** tests it. The log's "Sending
  webhooks failed".
- **Fix:** fix the receiver; for a lost key ring, remove and re-add the webhook and give the receiver the new secret.
  Webhooks don't use `Intake:Proxy`: allow the server out to the receiver directly.
