# Known issues and limits

The limits, debts and fragile areas in TIDE today, each with its impact and what to do about it. It's for an
operator planning an install, a security reviewer, and a developer about to work near one of them. Some are
deliberate trade-offs (the ADR is linked); none is a defect awaiting a fix unless it says so. Numbers for each bound
are in [performance.md](performance.md).

A search of `src` and `deploy` finds no `TODO`, `HACK` or `FIXME` comments. The accepted analyzer findings are marked
`// NOSONAR` with the reason beside them; they're listed under Security below.

**Contents**

- [Security](#security)
- [Scale](#scale)
- [Data](#data)
- [UX](#ux)

## Security

| Issue | Impact | Workaround |
|---|---|---|
| **Action owners are trusted with RED inside TIDE.** A ceiling governs what leaves the app, not what signed-in people see. Anyone with `ReadTeamEdition` (Action owner, Viewer) reads articles and the actions raised from them at every marking. Decided on 2026-11-03. No test reads a RED article as an Action owner yet; `ArticleService.RequireRead` checks only `ReadTeamEdition`. | A team mapped to Action owner sees RED and AMBER+STRICT content, including indicators. | Map only teams that should see everything to Action owner or Viewer. Use Reader for everyone else. See [architecture/editions-and-tlp.md](../architecture/editions-and-tlp.md). |
| **The zip download is deliberately weak.** `/files/{id}/zip` wraps an action's file in a zip with the published password `infected`, in classic ZipCrypto so File Explorer opens it (`FileDownloads.Zipped`, `FileRisk.ZipPassword`, `// NOSONAR S2068`). | It stops an accidental double-click and an antivirus deleting a sample on download. It gives no confidentiality. | Treat the file as unprotected. Who may download it is decided per file by `ActionFileService.OpenAsync`. |
| **`style-src 'unsafe-inline'`** in the CSP (`SecurityHeadersMiddleware`, and the email preview's CSP in `Program.cs`, `// NOSONAR S7039`). Scripts are `'self'` only. | Inline styles are allowed. An email's styles must be inline to survive mail clients. | None needed; script injection is still blocked. |
| **Health probes are anonymous.** `/health` and `/health/live` need no sign-in; the installer opens them in IIS (`Get-TideAnonymousPaths`). | Anyone who reaches the server learns whether TIDE and its database answer. The answer is one word, rate-limited to 120 a minute per address. | Restrict them at the network if that matters. |
| **The development sign-in exists in the build.** `DevAuthenticationHandler` signs everyone in without a password. | Enabled by mistake, anyone is an administrator. | `AuthModeGuard` refuses to start in Production with anything but `Auth:Mode=Windows`, and in other non-Development environments unless `Auth:AllowDevSignInOutsideDevelopment=true`. Never set that on a real server. |
| **A slow domain controller grants nothing.** Group SIDs are named under a time limit (`SidNames`, `BoundedDirectoryCalls`); a group not named in time grants nothing that request. | Fails closed: a person may briefly see "access denied" while AD is slow. | Their next request tries again; unresolved names are retried after a minute or five. Check AD health. |
| **Email uses `System.Net.Mail.SmtpClient`** (`SmtpClientFactory`). It speaks STARTTLS (`Email:EnableSsl`) and signs in with a user name and password or the app pool's account. | No implicit TLS (port 465) and no OAuth sign-in to the relay. **Needs verification:** whether any target relay needs either; a test send from Diagnostics would show it. | Use a relay that accepts STARTTLS on 25 or 587, or a local relay. |
| **AI web grounding leaves the Azure data boundary.** With `Ai:WebGrounding=true`, Azure's web search (billed separately) looks up an article TIDE can't fetch. | What the lookup asks goes to Grounding with Bing, outside the Azure data boundary. Only for sources marked Open and TLP:CLEAR articles. **Needs verification:** exactly which fields are sent (`AzureOpenAiAssistant`). | Leave it off (the default) where that's not acceptable ([ADR 0004](../decisions/0004-opt-in-ai-with-tlp-ceiling.md)). |
| **Webhook secrets depend on the key ring.** A hook's signing secret is encrypted with Data Protection (`DataProtectionSecrets`). | If the key ring is lost or changed (a new `DataProtection:KeyPath`, a restore without it), every hook fails with "The signing secret can't be read". | Back up the key folder with the database ([operations/backup-and-recovery.md](../operations/backup-and-recovery.md)); otherwise remove and add each webhook again. |

## Scale

TIDE runs as **one process** ([ADR 0001](../decisions/0001-blazor-server-on-prem.md)). The installer sets the app pool
to never overlap a recycle (`recycling.disallowOverlappingRotation`) and never recycle on a schedule, so there are
never two TIDE processes at once. A lot of state lives only in that process:

| In-memory state | Where | What breaks with two processes (a web farm, a web garden, an overlapping recycle) |
|---|---|---|
| `ChangeFeed` | `src/Tide.Application/Abstractions/ChangeFeed.cs` | Colleagues on the other process don't see live updates. |
| `Presence` and `BoardDrags` | `src/Tide.Web/Services/Together.cs`, `BoardDrags.cs` | "Viewing" and "editing" avatars and drags on the board show only same-process colleagues. |
| `LiveMeetings` | `src/Tide.Web/Services/LiveMeetings.cs` | Followers on the other process don't follow the presenter; a Visitor's seat (`MeetingAccess`) isn't seen, so their reads are refused. |
| `SourcePollGuard`, `CveRunLog` | `src/Tide.Application/Intake/SourcePulls.cs`, `CveService.cs` | A source can be polled twice at once; the second save fails on duplicate addresses. Two CVE refreshes can run together. |
| Static gates: `ReminderService.Running`, `MetricImportService.Gate`, `HolidayService.Loading`, `RotationService.Deciding` | Those classes | Two reminder scans can send before either writes its marks; two imports or two rotation decisions can overlap. |
| Caches refreshed on save: `SavedSettings`, `EmailTemplateStore`, `RoleDirectory`, `OrgClock.Days` (holidays), `PublishedContentCache` | `src/Tide.Application/Settings/SavedSettings.cs` and others | A setting, template, role mapping or holiday saved on one process isn't seen by the other until it restarts. |
| `PrerenderStore` | `src/Tide.Web/Services/PrerenderHandoff.cs` | A prerender's state isn't found; the page loads twice (harmless). |
| `JobMonitor`, `ReminderLog`, `SponsoredLog` | `src/Tide.Application/Operations/JobMonitor.cs` and others | Diagnostics shows only its own process's runs. Every hosted service also runs in each process. |

**Workaround:** run one server and one process. Scale up, not out. For availability, rely on a fast restart (the app
pool is `AlwaysRunning`) and the database's own high availability. Making TIDE multi-process would need a shared
backplane for the feed, presence and meetings, distributed locks for the guards, and cache invalidation across
processes.

Other scale limits:

| Issue | Impact | Workaround |
|---|---|---|
| **Search uses `LIKE '%term%'`** ([ADR 0011](../decisions/0011-search-with-portable-like-queries.md)). | Each search scans the tables it searches; no stemming or ranking. Fine at a team's scale (tens of thousands of articles). | Hits are capped at 50 a kind and 1,000 candidates. If the corpus outgrows it, a full-text index can replace the matching inside `SearchService`. |
| **The audit log's action and words filters have no index.** | Without a person chosen, those filters are held to 92 days (`AuditLogService.UnindexedDays`). | Choose a person, or narrow the dates. |
| **Uploads are read whole into server memory.** `BrowserFiles.ReadAsync` buffers each file in the circuit. | Up to `Files:MaxEvidenceMB` (25 MB by default) a file, ten files an upload, per person at once. | Keep the limits modest on a small server. |
| **Feed polling is sequential.** Each minute's pass polls due sources one after another, then the CVE step. | A slow source (up to `Intake:TimeoutSeconds`, 20 by default) delays the rest. Failing sources back off (interval doubled per failure, up to 16 times). | Pause dead feeds (Source health suggests them). |
| **Circuits hold memory for 20 minutes after a disconnect** (`DisconnectedCircuitRetentionPeriod`). | Memory per open tab, for longer than the default. | **Needs verification:** memory per circuit; see [performance.md](performance.md#memory-and-connections). |

## Data

| Issue | Impact | Workaround |
|---|---|---|
| **No DBA schema script ships.** `Install-Database.ps1 -SchemaMode DbaApplies` expects `deploy/sql/tide-schema-sqlserver.sql`, and `DatabaseInitializer` tells an operator to apply "the schema script for this release (deploy/sql)", but no such file is in the repository or the bundle. `deploy/README.md` still calls it "planned before the first release (1.0)". | A DBA-applied install has no script to apply. | Use `AppMigrates` (the app pool is db_owner during upgrades), or generate a script with `dotnet ef migrations script --idempotent` for the SQL Server set. **Needs verification:** whether the release bundle should carry one. |
| **Live meetings don't survive a restart.** The meeting (who presents, the slide, raised hands) is in memory. | A recycle mid-meeting drops it; everyone rejoins. What's kept of it (attendance, notes) is in the database. | Upgrade outside meeting time. The installer turns off scheduled recycling. |
| **Diagnostics history is cleared by a restart.** `JobMonitor`, `ReminderLog`, `CveRunLog` and `SponsoredLog` are in memory. | After a restart Diagnostics shows no last run until each job runs again. | Read the logs for earlier runs ([operations/troubleshooting.md](../operations/troubleshooting.md)). |
| **Some tables grow without pruning.** `AuditLog` and `NotificationMarks` are never pruned; feed items are (after `Intake:RetentionDays`, unless kept), kept article text after `Intake:ReadingDays`, metric drop-folder files after 90 days. | Slow growth over years. | **Needs verification:** sizes after a year in production. Archive old audit rows by hand if needed; never delete marks newer than a reminder's look-back, or reminders repeat. |
| **SQLite is for development only.** It stores `DateTimeOffset` as ticks, doesn't enforce lengths, and orders ties differently. | A query that works on SQLite can fail or order differently on SQL Server. | Provider theories and `[SqlServerFact]` tests; CI's `sql-server` job ([development/testing.md](../development/testing.md#sql-server-tests)). |
| **Released migrations are immutable.** | A mistake in a shipped migration can only be fixed by a new one. | CI enforces it; see [development/recipes.md](../development/recipes.md#add-an-entity-or-a-column-and-its-migration). |
| **Enums and names are stored.** Enum values are stored as numbers; preference names, email template ids, hunt value kinds (by name in bundles) and webhook event names as text. | Renumbering or renaming one corrupts stored data or older bundles. | Append only. A rename needs a migration that moves rows and a read of the old name (as `SettingsCatalog.Renamed` does for an SLA kind). |
| **Data Protection keys in development** use ASP.NET Core's default location for your user. | Webhook secrets saved in one development environment can't be read in another. | Add webhooks again after moving a development database. |

## UX

| Issue | Impact | Workaround |
|---|---|---|
| **A recycle or deployment drops open circuits.** | Pages reconnect; unsaved changes in a tab survive a dropped connection for 20 minutes, but not a process restart. | Upgrade outside working hours; the article editor warns before leaving with unsaved changes. |
| **Two prompts are the browser's own**: leaving an article with unsaved changes, and ending the meeting in present mode. | They look unlike TIDE's own dialogs. | None; navigation must be answered synchronously. |
| **Long polling where WebSockets aren't available.** | Slower live updates and higher server load when IIS's WebSocket Protocol feature is missing or a proxy blocks it. | Install the feature (`InstallWebSockets` in the answers file). Diagnostics shows each connection's transport. |
| **No stemming in search**: "patching" doesn't find "patch". | Analysts must try word forms. | Search for the stem. |
| **Results are capped**: search 50 per kind per page, the article list 50 a page. | Older items need a narrower filter. | Use filters and dates. |
| **Analyst Pull now is rate-limited** per source (`Intake:PullNowMinutes`, 15 by default). | A source pulled recently can't be pulled again from Triage. | An administrator's Poll now on Sources isn't held to it. |
| **A Visitor sees a meeting only while seated in it.** | Reloading after the meeting ends, or opening a link out, is refused. | By design ([architecture/security.md](../architecture/security.md)). |
| **The development `?as=` identity sticks** in a cookie. | A developer stays "Robin" until the cookie is cleared. | Use a private window ([development/getting-started.md](../development/getting-started.md#the-development-sign-in)). |
| **No automated browser tests.** JavaScript behaviour (keyboard shortcuts, drag and drop, paste, present mode keys) is checked by hand. | A script regression can ship unnoticed. | Exercise the affected screen by hand before a release ([development/testing.md](../development/testing.md#what-isnt-covered)). |
