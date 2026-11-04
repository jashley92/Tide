# Architecture

## Projects

| Project | Holds |
|---|---|
| `Tide.Domain` | Entities and enums. No dependencies. Also the indicator normalizer (`Observables/IocObservable`: refang, defang, detect a type). |
| `Tide.Application` | The services pages call (`ArticleService`, `ScreenshotService`, `BriefService`, `DistributionService`, `MetricsService`, `ActionService`, `TeamService`, `ScorecardService`, `RoleMappingService`), the SLA rules (`Actions/ActionSla`), the hunt query dialects (`Actions/HuntQueries`), the reminder scans (`Actions/ReminderService`), intake (`Intake/IntakeService`, `FeedParser`, `UrlCanonicalizer`, `CveService`, `StoryService`), the brief rotation (`Rotation/RotationService`), the permission model (`Security/`), the TLP edition cut (`Briefs/BriefEditions`), the email and SharePoint rendering (`Distribution/BriefRenderer`, `EmailComposer`), the metric rules and CSV import (`Metrics/MetricMath`, `MetricCsv`, `MetricImportService`), the indicator finder (`Indicators/IocExtractor`), search and indicator lookup (`Search/SearchService`, `SearchText`), the indicator exports (`Exports/ExportService`, `Stix`, `IndicatorCsv`), threat trends (`Trends/TrendsService`, `Period`), the ATT&CK catalog (`Mitre/`), and the organization clock (`Org/OrgClock`). |
| `Tide.Infrastructure` | `AppDbContext`, the audit interceptor, the SQLite migrations, the role and user directories, the on-disk file store, email (`Notifications/EmailSender`, `SmtpClientFactory`, `OutboundCircuit`), and the one outbound HTTP client (`Notifications/OutboundHttp`). |
| `Tide.Migrations.SqlServer` | The SQL Server migrations, in their own assembly (one model, one migrations set per assembly). |
| `Tide.Web` | The Blazor Server host: authentication, pages, the screenshot endpoint, health checks, and the background pollers (`Services/ReminderHostedService`, `Services/IntakeHostedService`). |

## Requests and sign-in

- **Sign-in.** In production it's Windows (Negotiate) under IIS. `RoleClaimsTransformer` turns the user's AD group
  SIDs into names, matches them against the mappings administered in the app (`RoleDirectory`, a cached snapshot),
  and adds role and permission claims. Group names are looked up under a time limit (`SidNames`,
  `BoundedDirectoryCalls`), so a slow domain controller can't hold requests. A group not named in time grants nothing
  that request: it fails closed.
- **Open sessions.** A Blazor circuit keeps its principal, so `PermissionRevalidatingAuthStateProvider` re-derives
  roles every two minutes. Removing a mapping reaches open sessions without a reconnect.
- **Where it's served.** At the root of its own host name by default. Optionally under a path of another site
  (`/tide`): IIS sets the request's path base, or `App:PathBase` does behind another proxy (`PathBaseSetting`), and
  `App.razor` writes it into `<base href>`. So links, scripts and the circuit stay relative, and code that builds an
  address in the app prefixes `Request.PathBase` (the status-code redirects, email image URLs, the dev sign-in cookie).
- **Development.** `DevAuthenticationHandler` signs everyone in with `DevAuth:Roles`. `AuthModeGuard` refuses to
  start in Production with anything but Windows.
- **Authorization.** It runs on permission claims. `Policies.Required` holds one policy per permission, and roles are
  bundles of permissions (`RoleDefinitions`). Services check the permission again themselves, so a page that forgets
  can't write.

## Data

- **Short-lived contexts.** Services take `IAppDbContextFactory` and open a context per operation. A Blazor circuit
  shares one DI scope, so a held context would be used by overlapping event handlers.
- **Audit.** `AuditInterceptor` stamps created and modified fields, and writes an `AuditLogEntry` (who, what, before
  and after) in the same save. `HistoryService` reads it back for the History disclosure on an article (its own row,
  and its tag, technique and CVE link rows by key prefix) and a brief, 50 rows at a time by the `(EntityType,
  EntityKey)` index; `HistoryFormatter` turns the JSON into field lines and drops noise (edit version, stamps). It's
  `WriteArticles` only, since the log holds every field at every marking. Bullets, screenshots, indicators and brief
  items have their own keys and aren't in it.
- **Audit log.** Administration › System › Audit log (`/admin/system/audit`) reads the whole log through
  `AuditLogService`: newest first, in numbered pages of 50 (`?page=`), filtered by person, kind of record, one
  record ("Only this record"), dates (org time zone), action and words over the changed values, all kept in the
  address. It's `Administer` only, by the page's policy and in the service. `AuditRecords` names each row's record
  (its title, or what it's part of: a bullet by its article, a comment by its action) in one query per kind on the
  page, falling back to the row's own Title or Name for a record that's gone, and links articles, briefs, actions,
  stories, sources, teams and the other admin records. `HistoryFormatter.AuditChanges` writes the field lines,
  leaving out the stamps (`UpdatedAtUtc`, `UpdatedBy`) as well as History's noise. The CSV export
  (`/admin/system/audit.csv`, same query names) holds at most 10,000 rows, one line per field, through `Csv.Cell`.
- **Indexes and bounds.** `AuditLog` has indexes on `AtUtc`, `(EntityType, EntityKey)` and `(UserId, AtUtc)`. The
  kind and record filters use the second, the person filter the third, over any range. Nothing indexes the action or
  the values, so with either of those and no person the query is held to at most 92 days (the chosen dates, cut to
  that, or the 92 days to today), and the page says so. With a person, they're read over that person's rows only.
- **Secrets in the log.** The interceptor writes a secret setting's value (`SettingsCatalog.IsSecret`: listed as
  `SettingKind.Secret`, or named like one) only as set, and a source address without its key or sign-in. The audit
  log masks again when it shows a row, whatever the row holds: a secret as "(set)" or "(changed)", a URL-kind
  setting and a source address through `SecretMask.Url`, and a source's last error through `SecretMask.Scrub`.
  The words filter matches a secret setting's row by its key only, so a search can't confirm a value.
- **Edits.** Articles and briefs carry an `EditVersion` concurrency token. A save from a stale copy is refused with
  `StaleEditException`.
- **Screenshots.** Bytes live on disk (`DiskFileStore`, under `Files:RootPath`, outside the web root) and rows in
  `StoredFile`. An upload is accepted only if its bytes are PNG, JPEG, GIF or WebP. The inline endpoint sniffs them
  again before serving. The team gets any screenshot; a Reader gets only an image frozen in a sent company-wide
  edition (ADR 0007).

## TLP and editions

Every article, bullet, screenshot and "why it matters to us" has a `TlpLevel`, and so does the brief's opening note
(`Brief.IntroTlp`, left out of an edition whose ceiling is below it). A part counts at its own marking or its
article's, whichever is higher (`Article.Effective`; `Article.HighestTlp` is the highest of them all).
A threat's "why it matters" starts at AMBER, or the article's TLP when higher (`Article.DefaultWhyItMattersTlp`): when
it's promoted as a threat, and when an article turns from informational to threat while it's still empty
(`Article.Classify`, `ArticleForm.SetClassification`). It only raises, never touches one already written, and isn't a
column default, so saved articles keep what they have.
`BriefEditions.Build(brief, edition, ceiling, actions)` returns only what the ceiling allows; the team edition also
carries each article's indicators and the actions raised from it (`BriefEditions.ActionsAsync`). The previews, present mode, the email and the SharePoint copy all go through it, so there's one place that
decides what an audience sees. The ceilings (and each edition's recipient list) are defaults in `Distribution` config
that an administrator can override in the app (`DistributionSettings`).

A ceiling governs what leaves the app, not what people see in it. Signed in, an Action owner (`ReadTeamEdition`) sees
articles, and the actions raised from them, at every marking, as an analyst does; the team edition's ceiling applies to
the edition as it's sent and previewed, to API tokens (each has its own) and to webhooks (`WebhookService`), which post
what the team edition would carry. That's deliberate, decided on 2026-11-03, and tests pin it: anyone mapped to the
Action owner role is trusted with RED inside TIDE, so map only the teams that should be.

## Settings changed in the app

The rule: operational settings are changed in the app and audited; security-sensitive and infrastructure
settings (sign-in, hosting, the database, files, all of email, intake's proxy, network and size limits, the CVE
addresses and NVD key, the update repository) stay on the server. Each in-app setting is a row in the `Settings` table
keyed by the configuration key it overrides, so the server's value is the default until an administrator saves one, and
the save is audited by the interceptor like any change. `SettingsCatalog.InApp` lists them with the page each is edited
on; Setting sources shows "Saved in Administration" or the server's source, and the configuration bundle carries them.

The edition ceilings and recipients (`DistributionSettings`), the SLA targets (`SlaSettings`), the rotation's manager
emails and the update check's switch (`UpdateSettings`) are read by their own services. The rest are the operational
settings (`Settings/OperationalSettings`): the organization's time zone, meeting time and team name; the reminders'
times, switches and the brief-not-ready recipients; intake polling, its retention and shortest poll; CVE enrichment and
its interval; the metrics drop folder and its interval; the update check's interval. `OperationalSettings` checks every
value (HH:mm times, numbers within the bounds the services already applied, an IANA time zone, email addresses) on save
and on import. `OperationalSettingsService` saves them, administrators only. `SavedSettings` (a singleton) holds the
saved values in memory, read at start-up and again on every save or bundle import. `OperationalSettingsOverlay<T>` lays
them over the server's values in `OrgOptions`, `ReminderOptions`, `IntakeOptions`, `CveOptions`, `MetricImportOptions`
and `UpdateOptions` (an `IPostConfigureOptions<T>`) and reloads those options when they change (an
`IOptionsChangeTokenSource<T>`). So everything that reads them through `IOptionsMonitor<T>` (each background service on
each pass, `OrgClock`, the pages) has a new value at once, without a restart; `OrgClock` keeps a snapshot it replaces on
each change.

**Holidays** (Organization settings, ADR 0018) are rows of `Holidays` (date and name). `WorkingDays` is Monday to
Friday less them, and `OrgClock.Days` holds the current one: `HolidayService` loads it at start-up (from
`DatabaseInitializer`) and again after each add, remove, import (.ics) or copy, and a bundle import does too. Every rule
that counts days takes it: `ActionSla` (due dates, due soon, end of the working week, days until), the reminders, brief
days and carry-over, the rota's days and runs, and the metrics' sparklines and missed days. The pure helpers take it
as a parameter (left out, it's Monday to Friday, as their unit tests use). Metric values on a holiday are kept if
entered; only a weekend's are refused.

**Email templates** (Administration › Email templates) are the wording of the notification
emails: the four reminder scans (due soon, overdue, the weekly digest, brief not ready), presenting next, and the
rotation's request, approved, recorded and declined emails. `Distribution/EmailTemplateCatalog` lists each with its
default subject, heading and Markdown body and the `{{Name}}` values it fills in. A changed part is a `Settings` row
`EmailTemplate:{id}:{Subject|Heading|Body}`; one saved as its default has no row, so a reset removes them.
`EmailTemplateCatalog.Problem` checks each part on save and on import: a name the email doesn't have, an HTML value
outside the body, a body missing the value the email is for (the reminders' `{{ActionList}}`), or too long.
`EmailTemplateRenderer` renders the body with raw HTML escaped and then fills in the values: text ones are encoded, so
an action's title can't add markup, and TIDE's own markup (the actions table, links) goes in as it is. TIDE keeps the
rest: the shell, the line under the heading, the TLP marking in a reminder's subject and at its top, the managers' copy
heading, and the plain-text part. `EmailTemplateStore` (a singleton) holds the saved parts, read at start-up and on
every save, reset or import; `ReminderService` and `RotationService` render from it. `EmailTemplateService` saves with a
stamp of what was saved when the editor opened, so a stale page is refused, previews the draft with sample values in a
sandboxed frame, and sends a test only to the administrator's own address. The configuration bundle carries the saved
parts. The brief's email isn't a template: it's written from the brief.

The readiness checks (`ArticleChecks.For`) are pure. Given the company-wide ceiling, they also warn when a threat's
"why it matters" is at or below it, so goes to everyone (`ArticleChecks.WhyReachesCompany`). The warning carries a
one-click fix (`ArticleFix`) that the editor applies to its form. It shows in the editor's preview, the running order
and, on the company-wide column, the edition check.

## Distribution

`DistributionService` sends an edition: it cuts it with `BriefEditions.Build`, renders it with `BriefRenderer`, and
hands it to `IEmailSender`. The email is tables and inline styles (colours from `EmailComposer.Palette`, which mirrors
the light tokens), with screenshots as `cid:` linked resources at their real width. Sending is a plan the analyst
confirms (`SendPlan`: marking, subject, recipients, and a fingerprint of the rendered email); the send is refused if any
of that changed since. A real send freezes a `BriefSend` (email HTML, a semantic web copy, and copies of its images as
`BriefSendImage`s) before the relay is asked, then records the outcome; only a send the relay took marks the brief Sent. One real send of an edition
runs at a time, and the confirmation says if the edition already went. Test sends go to the
sender only and are logged without a copy. The Reader archive (`/published`) shows frozen company-wide sends, and
lists each with its headlines and filters on its words; both are parsed from the send's frozen semantic HTML
(`PublishedContent`, cached per send in `PublishedContentCache`), never read from the articles or the search index, so
nothing the company wasn't sent can show or match. The SharePoint view renders the same semantic HTML with a zip of its images. The email preview and frozen emails are
served by endpoints with their own CSP (no script, `sandbox`, framable only by the app).

A mistake after a send is put right by a correction (ADR 0015), a `BriefCorrection` row of its own (`BriefSend` gains no
column, so every query over sends is unchanged). `CorrectionService` plans one as a send is planned (`CorrectionPlan`:
marking, subject, the original send's recipients, and a fingerprint of the rendered correction, refused if any of it
changed), freezes it before the relay is asked and records its outcome through the same helper (`SendOutcome`); one
correction of a send goes at a time, and only a real send the relay took can be corrected. Its email
(`CorrectionRenderer`, the brief's tables, inline styles and palette, no images) carries the writer's required note and
a line diff of each article: the "as sent" side is the article's section of the send's frozen web copy, found by the
`data-article` attribute the semantic headings carry (by headline, or by the writer's match, for an older send), less
any line that's above the ceiling now; the "now" side is cut by `BriefEditions.Build` at the lower of the send's
ceiling and the edition's current one. An article that has left the edition is named by number only. A TLP slip is a
correction with fixed wording and no diff, a second tick that it can't be recalled, and an optional note held to the
ceiling. A slip of a company-wide send withdraws as soon as it's frozen, whatever becomes of its email.
`DistributionService.Published` leaves out every company-wide send of the slip's day made before the slip, so
`/published`, its filter and the inline image endpoint give Readers nothing of them. That covers the send the slip
names, earlier sends, and later ones made before the slip, a re-send that put it right included: a slip doesn't say
which line slipped, so "put right" can't be told for certain. A send made after the slip is never withdrawn by it,
and that is how the corrected edition is published; the send log marks each withdrawn send and says to send the
edition again. The day shows the slip's notice, and the team keeps the frozen rows in the send log. Corrections show in the send log under the send they correct. On
`/published` a correction shows above the edition it corrects, its words joining the day's for the filter. A
withdrawn send's own corrections don't show there, because they quote its words.

## Opening metrics

Administrators define `MetricGroup`s and `MetricDefinition`s (direction, format, optional thresholds); analysts
enter one `MetricValue` per metric per brief date. `MetricMath` holds the rules: a value is compared with the most
recent earlier business day that has one; a zero change is grey whatever the direction; thresholds apply in the
metric's direction (at or above for lower-is-better and neutral, at or below for higher-is-better). Metrics show on
a slide after the title slide and at the top of the team email, never in the company-wide edition. Retiring or
renaming a metric keeps its values; the history page and its CSV export cover retired metrics too. Once a day's brief is
Sent its values are fixed for everyone but administrators (`MetricsService.IsLockedAsync`), so the page agrees with
what went out.

Values can also come from CSV (`MetricImportService`, ADR 0014): an administrator's upload, or files left in the drop
folder (`Infrastructure/Storage/MetricDropFolder`, read by `MetricImportHostedService` when
`Metrics:Import:DropFolderEnabled` is on, set on Metric imports). `MetricCsv` reads the file. Each row sets one metric on one day by key and
date, so a file read twice changes nothing. An import never replaces a typed value or touches a sent day. Each read is
a `MetricImport` (counts, problems), and each imported value records `Source = Imported` and its `ImportId`.

Ending the meeting (`BriefService.MarkPresentedAsync`) keeps two times: `MeetingHeldAtUtc`, when it was held (now, or
for a past day's brief that day's meeting time unless the presenter picks another), and `PresentedAtUtc`, when it was
recorded. A meeting can't be held before its day or in the future.

A meeting is live while the presenter runs it in present mode (`LiveMeetings`, in memory like `Presence`, one per brief
day): who presents, their slide (the others follow it), who's on which slide, and raised hands. It's dropped when it
ends, or after 15 minutes with nobody in it; a presenter gone 30 seconds can be taken over by another analyst. What's
kept is the database's (`MeetingService`): `MeetingAttendee` rows as people join (the presenter adds anyone who was in
the room by name, and their leaving times are written as the meeting ends) and `MeetingNote`s, each its author's,
against the article on screen. Notes and attendance publish `ChangeFeed.Meeting`, not the brief's own topic, so a
note doesn't show as an edit to the brief. Anyone on the team edition can open present mode to join; starting,
marking discussed and ending stay `PresentBriefs`. The meeting is per process, so it isn't shared across a pair of
load-balanced servers.

## Actions

An `ActionItem` belongs to a `Team` (members are `TeamMember`s, by user id) and optionally an assignee, and links to
the article it came from and, for a remediation split across teams, a parent (one level). `ActionService` checks who
may do what (ADR 0008): analysts create and edit any action; someone with `WorkActions` works only their teams'.
Comments are wider (ADR 0008, amended): anyone with `ReadTeamEdition` may comment with text on any action, and edit or
delete their own; files on a comment stay with whoever works the action (`ActionDetail.CanComment` beside `CanWork`).
Each change also writes an `ActionEvent`, the readable history on the action. One raised for a past day's brief
records that day (`RaisedOn`), which reports count it on; its SLA runs from when it was recorded. A new action's due date comes from
`SlaSettings` (business days per priority, per-kind overrides), and `ActionSla` reads where an action stands. An action
raised in the meeting (`QuickAddAsync`) takes its article's priority, except that Awareness, Vendor inquiry and Other
are never above P2 (`QuickPriority`); an article with no priority leaves it to the kind. Present mode splits the
article's recommended actions into lines (`RecommendedActions.Lines`), guesses each line's kind from its wording
(`GuessKind`), and counts a line raised once an action on the article has its title. Changing a saved action's priority
offers the new SLA date; it never moves the date by itself. The outcomes an action may close with depend on its kind
(`ActionService.KindOutcomes`, always with Other), and the one it already has stays a choice.
Today's team queue (`ActionService.TeamQueueAsync`, ordered by `TeamQueue.From`) is drawn for anyone with
`WorkActions`; without `WriteArticles` it leads Today in place of the producers' step strip, running order and edition
check. The page branches on those permissions, never on a role name (ADR 0002).

Hunt queries (ADR 0006) are built by `HuntQueryBuilder` from the article's indicators, or from every article's in its
story, looking back `{{days}}` (`HuntQueryBuilder.WindowDays`); an analyst can also add a hand-written query. `HuntValues` first splits them
into kinds (`HuntValueKind`): an indicator type, except that IPv4 and IPv6 addresses, and MD5, SHA-1, SHA-256 and
SHA-512 hashes (by length), are kinds of their own, because SIEMs keep them in different fields. Each kind gets one
block, from the dialect's template (`IQueryDialect`: XQL, KQL, CrowdStrike) or an administrator's `QueryTemplate`
(which can also add a template for a kind with none, or turn a kind off), with the values quoted and escaped. The analyst runs it in their SIEM and records the result, optionally with an evidence screenshot,
on the `HuntQuery`. Golden files in the unit tests pin each dialect's output. A generated query's first line says how
many days it looks back, and each kind it couldn't query says how many values it left out (`HuntQueryBuilder.WindowOf`,
`LeftOut`). `ActionService.CloseWithNoHitsAsync` records a query's result as no hits and closes its sweep or hunt Done
(No hits) in one save and one history line, naming the days searched; it needs left-out values acknowledged, and is
refused while another query on the action has hits or hasn't been run.

`ReminderService` holds the scans (due soon, overdue, weekly digest, brief not ready). `ReminderHostedService` polls
every five minutes; each scan is off until enabled, waits for its time of day, batches one email per recipient, and
marks each email with the TLP of the articles its actions were raised from, and writes a `NotificationMark` as each
email goes, so it's sent once even when a scan stops partway. Scans run one at a
time, and one that fails is logged without stopping the others.

## Intake

A `Source` is an RSS/Atom feed or the CISA KEV catalog. `IntakeHostedService` polls the due ones every minute when
`Intake:Enabled` (ADR 0009; turned on in Administration › Sources, read on each pass), through `IOutboundHttp` (one client: proxy, time and size limits, conditional GETs, a
per-host back-off, and `AddressPolicy`: never loopback or link-local, private ranges only when allowed, checked on each
redirect hop and each connection). A source whose poll fails in any way (a fetch, a feed the parser can't read, an unexpected fault) records the error
and backs off; the other sources still run. `FeedParser` reads the XML skipping any DOCTYPE unread, so an entity it declares stays undeclared (a control character XML forbids, pasted into one post, becomes a space rather than refusing the feed) and reduces summaries to plain text, without the "The post … appeared first on …" footer WordPress feeds end with (stripped before the excerpt is cut), keeping each item's own tags. A feed's filter (`FeedFilter`: only these tags or title words, never those) decides which items are stored at all; then, while `Intake:SkipSponsored` is on (the default), `SponsoredFilter` leaves out an item that reads as sponsored. Its markers (`Intake:SponsoredMarkers`) and exceptions (`Intake:SponsoredExceptions`) are data, in-app operational settings edited on Sources with the server's values as defaults: a phrase matches as whole words, ignoring case, with hyphens, dashes and spaces alike, in the item's author (RSS `<author>` or `dc:creator`, Atom `author/name`), its RSS `<source>`, any tag, the title or the first 120 characters of the summary, and a marker inside an exception ("state-sponsored") doesn't count. Nor, whatever the exceptions say, does a marker starting "sponsored" where the news names a state as the sponsor: joined on with a hyphen or dash ("Iran-sponsored"), a state word just before ("Chinese sponsored"), or one in the next few words ("sponsored by the North Korean regime", "sponsored by Beijing's MSS"). A new feed's convention is a new phrase, not code. Sponsored items count toward `LastFilteredOut`; which markers matched on each source's last poll is kept in memory (`SponsoredLog`, a few titles per source) for the Sources list, so a restart clears it. Each
`FeedItem` is keyed by the SHA-256 of its canonical address (`UrlCanonicalizer`), so it's stored once; a new item
whose title words mostly match a recent item's (`TitleMatch`) gets `SimilarToId`. The inbox (`IntakeService.InboxAsync`)
adds past coverage: articles with the same CVE or link, and the brief they were on. Promotion creates a draft
`Article` through `ArticleService`. New items that share a CVE are shown as one story (`InboxRow.SameCve`); once one is
promoted, a follow-up can be attached to that article as another report (`IntakeService.AttachAsync`, an
`ArticleLink`, which also counts as the article's link for coverage) and the rest dismissed as already covered. Any
item still to decide can be attached to a recent article ("Attach to…", from `IntakeService.AttachTargetsAsync`:
drafts from the last two weeks, ready articles, and today's brief). Attaching copies the item's missing CVEs onto the
article (bumping its `EditVersion`, so an editor open on it can't save over them) and refuses an article already
presented or on a presented or sent brief unless the caller says so outright; coverage says which it is
(`Coverage.Attachable`), with the covering articles' open actions for the row's "Follow-up to" line. `SimilarToId` is
set once, at intake, by the first match, so a reworded headline often isn't flagged; the inbox also suggests groups
("Looks like one event", `TriageGroups.Find`) each time it loads, in New and Shortlisted, over the items shown, and
stores nothing (no column, no migration). Two items published within 48 hours join when they share a CVE, or at least
two distinctive headline tokens, one of them a name: capitalised or product-like words and amounts (money counts by its
scale, as outlets round it differently), leaving out common words, the words every security headline uses (hack,
breach, flaw, critical, patch…) and the publisher's own name. Groups are connected runs of such pairs, joined tightest
pair first and held to 8 items, and are offered only with two or more items from two or more feeds
(`InboxRow.Group`). Triage shows a group as one line that opens to its reports' own rows; "Not the same" splits it in
the page only. "Promote as one article" (`IntakeService.PromoteAsOneAsync`) claims every report in one statement
inside a transaction, so a group is claimed whole or not at all, makes the article from the newest report (at the most
restrictive of the sources' TLPs, unless chosen) and adds each of the others as a report, as attaching does, CVEs
included; a failure after the claim removes the article and gives every report back. An analyst can
also add a report link by hand in the editor (`ArticleService.AddLinkAsync`, http or https only). Items nobody kept are pruned daily, and an item published before the retention period isn't taken in, so a feed that
still lists a pruned item (the KEV catalog lists a year or more) doesn't bring it back as new.

`CveService` keeps a `CveRecord` per CVE (KEV flag and dates, EPSS, CVSS), refreshed by the same poller when
`Cve:Enabled`; pages only read it, and `CveService.ThreatHints` turns KEV or a high EPSS into a suggestion. `CveService.SuggestedPriority` gives triage's promote form a priority and its reason ("on KEV, CVSS 10"); the form preselects it and the analyst can change it. The page keeps its place in the address (`triage?tab=…&at=…`): "Promote and write" rewrites the current history entry (`triage.js`) before opening the draft, so Back returns to the same tab, filters and row, and the brief's Add article picker links a shortlisted item there with its promote form open. A `Story`
is a thread across days: articles point at it (`Article.StoryId`), and its page is their timeline.

## Brief rotation

`RotationService` keeps an ordered roster (`RotationMember`), a weekly rota (`RotationWeek`, by Monday) and
day-level overrides (`RotationOverride`) written when a `RotationChange` is approved (ADR 0010). Who presents on a
day is its override, else its week's analyst; `IPresenterLookup` answers that for the brief (who presents a new brief)
and the reminders (the brief-not-ready nudge, and the reminder the business day before a stretch). A change
is a cover (the requester's days to a colleague) or a swap (both ways); only the colleague approves it, every approved
change emails the managers configured on the admin page, and an administrator can undo one. `/rotation/my.ics` is
the signed-in person's brief days as an iCalendar file. A past day's brief is presented by whoever starts it, and
`RecordPastCoverAsync` records that presenter as an already-approved cover when the rota named someone else. It's the brief rotation, not an on-call schedule.

## Search

`SearchService` answers the search page (team edition and above; it shows every TLP, as the article pages do). A
query is split into terms (`SearchText.Terms`: words or quoted phrases), and every term must match one of an
entity's fields. Matching is `LIKE '%term%'` with the wildcards escaped, so it runs the same on SQLite and SQL Server
with no full-text index to install (ADR 0011). A network indicator is looked for both live and defanged
(`SearchText.Forms`), and a term that names ATT&CK techniques also finds the articles tagged with them.

When the whole query is one indicator (`IocObservable.DetectType`) or CVE, the page also does a lookup: the articles
carrying it (an exact, case-insensitive match on the stored live value in its type family), indicators on the same
host (`SearchText.HostOf`: subdomains, and URLs and email addresses on the domain), the actions raised from those
articles with how their hunts went, the `CveRecord` for a CVE, and a hunt query from `HuntQueryBuilder`.

The editor's Related tab asks `RelatedService.ForArticleAsync`, which shares those action and brief queries
(`Search/Lookups`): the other articles that share a CVE, a link (source or another report, stored or canonical) or
the story, newest first and at most five (with a search for the rest), each with its briefs and its open actions and
how late they are. It's team edition and above, like Search; "Mention" puts only an action's number and "(open)" in
why it matters, and only when that part is TLP:AMBER or higher, since action status is internal.

## Exports

`GET /exports/{article|brief|story}/{key}?format=stix|csv&tlp=` (team edition and above) downloads the indicators of
an article, a brief (in running order) or a story. `ExportService.GatherAsync` loads the articles and leaves out any
marked above the requested TLP, whole, as the editions do. `Stix.Write` makes a STIX 2.1 bundle: an identity for the
team, a `report` per article (id `report--{article id}`), and the `indicator`, `vulnerability` and `attack-pattern`
objects the reports point at, with UUIDv5 ids from what they are so re-imports merge. Each object carries the OASIS TLP
2.0 marking-definition id of its article; one shared by several articles takes the highest (ADR 0012).
`IndicatorCsv.Write` has a row per indicator per article, formulas neutralised.

## Trends

`TrendsService.ReportAsync(end)` (team edition and above) builds the trends page and the board scorecard: six months
or five quarters (`Period`: "2026-09", "2026-Q3") ending with `end`. Each `TrendRow` holds, for each period, the ids of
the articles or actions it counted, so a count opens to exactly what it counted and a line that sums others (a
category, a tactic) counts each article once. An article counts in the period of its first brief, or of its writing if
it was never briefed and isn't a draft (ADR 0013). Article lines: all, threats, P1/P2, informational; every scorecard
tag under its category (retired ones only when used); every tactic and each technique seen (sub-techniques rolled up
to their parent unless asked otherwise). Action lines: raised (by kind, by team), closed (done on time or late,
cancelled, by outcome), hunted (a hunt result recorded; with hits, no hits) and open or overdue at the period's end (as of today for the
period still under way).
The page keeps its state in the address (`/trends?p=&view=&line=&at=&subs=`), so a view can be bookmarked or sent.
`GET /trends/export.csv?p=` downloads the table, and `GET /trends/articles.csv?p=&line=` the articles one line counted
in period `p`, with their tags, techniques and tactics.
The Articles list a count opens narrows by the same tests (`ArticleFilter`, `ArticleService.PageAsync`). With a tactic
or technique filter, each row also carries the techniques it was listed for (`ArticleFilter.MatchingTechniques`), read
for the whole page in one query.

## Working together

Two in-memory singletons keep analysts aware of each other (TIDE runs as one process, ADR 0001).

- `ChangeFeed` (Application) tells open pages that something they show changed. `ChangeInterceptor` publishes a topic
  (`article:{id}`, `articles`, `brief:{id}`, `action:{id}`, `actions`, `metrics-day:{date}`, `metrics`, `rotation`, `stories`) after every committed save, with who made it and the circuit
  it came from, so every save path is heard. A page listens through `LiveChanges`, which skips its own circuit's saves
  and waits for a burst to settle (600 ms) before one reload. A page with nothing unsaved re-reads in place and names
  the colleague in a toast; one with unsaved edits keeps them and says so (`ChangedElsewhere`), with a Reload button.
  The numbers form (`MetricEntry`) re-reads a day's numbers in place, keeping a box being typed in (or holding what
  isn't a number) and saying on its row who changed it; the step strip, the edition check, the preview's team edition
  and Present mode's opening slide follow. Today re-reads My actions and the team queue when an action changes, and the
  workload page re-reads its counts. The triage inbox also reloads when a colleague triages (its own `triage` topic,
  published with who did it) or a poll brings items. The articles list re-reads the rows it
  shows, with its filters, on any article save; so do the brief's "Add article (N available)" count and an open Add
  articles picker, which keeps what's ticked. The rotation page and Today's "Coming up for you" re-read on any rotation
  save, saying when a request is now waiting for you or yours was answered; the stories list follows its stories.
- `LiveMarks` (Web) shows what a live reload did to a list, from `LiveDiff` (the rows that arrived, left or changed,
  by key): arrivals slide in under a fading highlight (`tide-live-arrived`) and keep a "New" marker until selected,
  changes get the highlight alone (`tide-live-changed`), and a row that left is a ghost line saying what happened to
  it, which fades and collapses (`tide-live-ghost`). Triage uses all of it, ticks its tab counts, and says it in one
  toast (`TriageNews`) and a polite status line; under an open promote form or attach picker it holds the changes
  behind an "Updates waiting" pill. The actions list, Today's My actions and team queue, and the workload's numbers
  and holders use the highlight, and the board's card for a new action glows in as a moved one does.
- `Presence` knows who has a brief, an article, an action, Present mode or a triage item open: `PresenceNote` shows
  each person as initials in their own hue, "viewing", or "editing" with a pencil while that page has unsaved
  changes. A list row shows who has the item open without joining it. `PresenceCircuit` clears a tab that closed or
  dropped without its page saying so.
Promoting a triage item claims it in one statement first, so two analysts promoting it at once make one article.
Promoting a suggested group as one claims all its reports in that statement, in a transaction: if any was just taken
by someone else, none is claimed.

## Front end

Bootstrap 5.3 (self-hosted) is retinted through `--tide-*` tokens defined for light and dark themes in
`wwwroot/app.css`. The CSP is strict (`script-src 'self'`), so every script is an external file:

- `theme.js`: the theme, sidebar and letter-shortcut state (single-letter shortcuts can be turned off, WCAG 2.1.4;
  the state is on `<html>`, set before paint by `theme-init.js`). The theme and shortcut switches are in the account
  menu (`ProfileMenu`), which calls `tideTheme` and `tideShortcuts`.
- `paste-drop.js`: hands pasted or dropped images to a hidden `InputFile`.
- `present.js`: keyboard navigation and full screen.
- `clipboard.js`: copies the SharePoint view as rich HTML.
- `triage.js`: the triage inbox's keyboard actions.
- `dialog.js`: gives focus back to whatever opened a dialog (the screenshot lightbox) when it closes.

In the layout, personal settings sit under the initials avatar in the
top right (who you are, your roles, theme, letter shortcuts, your rotation calendar), and the administration pages share
one frame (`AdminLayout`) reached from a single "Administration" sidebar entry. The frame has an "Administration"
heading, the setting groups as tabs (Overview, Organization, Intelligence, Brief, People & distribution), a group's
pages as sub-tabs (`TabBar`), and the page itself. `/admin` is the overview: a getting-started checklist read from the
live configuration, and an index of every setting page. On a phone (640px or less) the sidebar becomes a bar across
the top whose menu button (`tideNav.setOpen`) opens the links, and wide tables scroll inside their card.

Shared pieces keep pages consistent with each other:
- `ConfirmDialog` asks before anything that can't be taken back. It's a modal with a button named for the action;
  focus is kept inside by `dialog.js`, and triage's letter keys pause while it's open.
- `Breadcrumbs` gives the way back on a detail page ("Actions › A-12").
- `TabBar` switches between sections, as links or as in-page buttons with counts.
- `EmptyState` says what's missing and what to do; `LoadingBlock` shows placeholder rows with a status for screen
  readers.
- The error boundary's fallback offers Try again, Reload and Today.

Two prompts stay the browser's own. Leaving an article with unsaved changes has to be answered during navigation, and
ending the meeting is asked in present mode, which handles its own keys.

Markdown is rendered by Markdig with raw HTML off, unsafe link schemes neutralised, no autolinks (bullets quote
malicious URLs) and no inline images.
