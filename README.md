# TIDE: Threat Intelligence Daily Exchange

> The threat-intelligence team's daily brief, from the morning's feeds to the last follow-up action.

An open-source, on-prem web app for a threat-intelligence team's **daily brief**. The analyst whose turn it is
triages the day's security news and private-source intel and writes it up. The team presents it at a morning
meeting. The brief then goes by email to the security teams and, cut down by **TLP 2.0**, to the whole company. The
**follow-up actions** are tracked to the end, whether a five-minute IOC sweep or a month-long remediation.

It runs on Windows Server with IIS and signs people in with their Windows (Active Directory) account. Its data is in
SQL Server, and it's built to share a server and a SQL instance with other line-of-business apps.

**Documentation:**
- **[docs/architecture.md](docs/architecture.md):** how it's put together.
- **[docs/development.md](docs/development.md):** developing it.
- **[docs/INSTALL.md](docs/INSTALL.md):** installing it.
- **[docs/UPGRADE.md](docs/UPGRADE.md):** releases and upgrades.
- **[docs/decisions](docs/decisions):** the decision records behind it.

## Screenshots

### Today
Where the presenting analyst starts. Today's brief shows a strip of its five steps (**Triage, Write, Order,
Present, Send**), each saying where it stands, with one main button for whichever step is next. Beside it:
- **the running order**, which you drag, or move up and down, with removal you can undo;
- **today's numbers**, each box with the value it's compared with;
- **what each edition will carry**;
- **the meeting**: its time, and who the rotation names to present.

Readiness checks say what's missing on each article before the meeting. A threat whose "why it matters to us" would
reach the company-wide edition is flagged.

Anyone who works actions also gets their **team's queue**: their teams' open actions that are theirs or nobody's,
theirs first, by due date, with *Take it*. An action owner's Today leads with it.

![Today](docs/screenshots/brief-todays-numbers.png)

### Triage
A keyboard-driven inbox of new items from the sources: RSS and Atom feeds and the CISA KEV catalog. Each source has a
default TLP, an Admiralty reliability grade, a polling interval and a handling note. From the inbox, an item can be
shortlisted, dismissed with a reason, marked a duplicate or promoted to a draft article.

A feed can be filtered by its own tags and by title words. Sponsored items are skipped too, on by default: a list of
marker phrases ("Sponsored by", "Partner content", "Advertorial") is looked for as whole words in each item's author,
source, tags, title and the start of its summary, and a list of exceptions ("state-sponsored") keeps the headlines
that only look sponsored. Both lists are edited on Sources, with a Test box that says whether a title or byline would
be skipped and by which marker. A skipped item counts toward the source's "filtered out" figure, which says which
markers matched.

**Sources tab.** The last tab lists the sources TIDE polls: when each was last pulled ("12 min ago", the exact time on
hover), when it's next due, and how its last pull went (items added, or the error). Its count is the number of sources
whose last poll failed, shown as a warning. *Pull now* takes in a source's latest items straight away, once per
analyst pull-now window (15 minutes by default, set on Sources): a source pulled within it, on schedule or by hand,
shows when it can be pulled again instead.

**Reading the article here:**
- **V reads it.** The selected item's article opens in a pane beside the list, fetched once from its page (the
  source's fetch setting and body selector apply; a source set to Never isn't fetched). Inline adverts are taken out:
  blocks whose class or id names an ad slot, the advert selectors and label lines listed on Sources ("Advertisement",
  "Story continues below"), and a source's own *Also remove* selector for one site's odd markup. CVEs, indicators and products
  on Our stack are marked, with the CVE chips under it. While the pane is open it follows J and K, showing a report's
  text when it's already been read and offering to read it when it hasn't. Esc closes it.
- **Kept for a week.** The text is kept on the item for `Intake:ReadingDays` (7), so a colleague reads it without
  fetching again and *Draft from the sources* starts with it after promoting. It's for reading inside TIDE only: it's
  never sent, exported or shown to readers.

**Learning from the team's decisions:**
- **Likely used.** New and Shortlisted can be ordered by what the team promoted from items like each one, from the
  decisions of the last 90 days (or as long as dismissed items are kept). Each row says why in counts: "3 of the 4
  from GitLab releases naming GitLab decided in 90 days were promoted". Items naming what we run, a watched adversary
  or a KEV-listed CVE stay on top. Newest first is the default; the choice is kept in the browser. Nothing is
  trained or stored: the counts are worked out from the items still kept.
- **Missed this week?** New lists dismissed items from the last 7 days that name a KEV-listed CVE no article covers,
  or a watched adversary no recent article is tagged with, each with *Put back in New*.
- **Source health** (Administration) shows each source's items a week, how many were decided and promoted, the last
  item promoted and the median time to triage. A source that sent 10 or more items in 60 days with none promoted or
  shortlisted is suggested for pausing, and *Pause polling* switches it off; its items stay.

**Promoting:**
- **Inline promote form.** Clicking a title opens the promote form in place. It suggests a priority from the
  item's CVE data and says why ("Suggested P1: on KEV, CVSS 10").
- **Back to the same row.** After *Promote and write*, Back returns to the same row.

**What triage already knows:**
- **Stored once.** Each item is kept once, by its cleaned-up address.
- **CVE chips.** These carry CVSS from NVD, EPSS from FIRST and the KEV flag.
- **Repeats.** A reworded repeat of a recent story is flagged.
- **Past coverage.** An item whose CVE or link was already covered says on which brief, and which actions from it
  are still open ("Follow-up to Thu Oct 22: A-13, A-14 open").

**Several reports of one event:**
- **Attach to…** Any item can be kept as another report of a recent draft or ready article, instead of becoming a
  new one, and its CVEs go with it.
- **Looks like one event.** Reports from several feeds that share a CVE, or names and amounts in their headlines
  ("Binance", "$6.3 billion"), are suggested as one line. It opens to the reports themselves, each with its source and
  excerpt. *Promote as one article* makes one draft with every report's link. Nothing is merged without a click, and
  a group has no "dismiss all".

**Two analysts at once:**
- **Live changes, shown.** When a colleague triages or a poll brings items, the inbox updates without a reload and
  shows what changed: new rows slide in with a soft highlight and a *New* marker until you look at them, changed rows
  are highlighted, and a row a colleague took away leaves a line for a moment ("Sam dismissed “…”") before it folds
  away. The tab counts tick, and one toast sums it up ("Sam dismissed 3 items · 2 new from the feeds").
- **Nothing moves under you.** The selected row stays selected (if a colleague took it, the next one is selected and
  you're told), and the scroll stays put. While a promote form or the attach picker is open, changes wait behind an
  *Updates waiting* pill. With reduced motion the highlight only fades; nothing slides or folds.

![Triage inbox](docs/screenshots/triage-inbox.png)

### The article editor
Section pills jump to each part of the article, and a sticky save bar sits along the bottom. Beside the editor is
**what readers get**: the article as the team or company-wide edition carries it, with what that edition leaves out
marked for the writer.

An article carries:
- a bottom line (BLUF);
- Markdown bullets;
- "why it matters to us";
- recommended actions;
- pasted screenshots (Ctrl+V anywhere, or drag and drop);
- a **Threat** (P1 to P4) or **Informational** classification;
- board-scorecard tags;
- MITRE ATT&CK techniques from the embedded Enterprise catalog;
- threat actors and sectors;
- indicators (shown defanged, stored live) and CVEs.

The **indicator finder** pulls URLs, addresses, hashes, email addresses, domains and CVE ids out of a pasted article
or a vendor's IOC appendix, defanged or not, and you tick the ones to keep.

**Draft from the sources** (optional, off until an administrator sets up a provider) gives the analyst a first draft to
check rather than a blank form:
- **The text to draft from.** Paste it, or **Fetch the article** from its link. TIDE reads the page through intake's
  guarded client and finds the article body whatever the site's design: a selector the source's administrator set,
  the text the page publishes for search engines, its article markup, or reader-mode scoring.
- **Several reports at once.** An article promoted from a group lists its other reports under *Other reports*, with
  the text already read in triage where there is one. *Fetch it* (or *Fetch all*, one at a time) gets the rest, and
  *Add another report* takes any link. Every report with text is a numbered source of the draft, each cut to a fair
  share so one long report can't crowd out the others, and the draft goes at the strictest TLP among them.
- **When TIDE can't read the page.** If the source is marked Open and the article is TLP:CLEAR, the AI provider's web
  search may look it up instead (Azure: Grounding with Bing, off by default).
- **The suggestions.** The provider suggests the bottom line, bullets, recommended actions, ATT&CK techniques,
  scorecard tags, indicators and CVEs, plus "why it matters" from our notes on what we run when the TLP limit allows
  AMBER.
- **A source for every claim.** Each suggested bullet carries markers such as [1] [3] for the sources it came from,
  and a short quote from the first of them. Clicking a marker shows the quote. A bullet that cites nothing, or whose
  quote isn't in the source it cites, is flagged and offered only as *Add anyway*. Where sources disagree on a fact
  (a count, a date, who did it), the draft lists the disagreement instead of picking a side. The markers are for the
  editor only; they never reach the emails.
- **The checks.** TIDE checks every suggestion before you see it:
  - techniques must be in the catalog;
  - tags must be configured ones;
  - each indicator is marked as in the text or not;
  - Markdown links are removed.
- **Your decision.** Each suggestion goes into the form only when you accept it.
- **The limit.** Nothing above the TLP limit (`Ai:MaxTlp`, GREEN by default) is ever sent.
- **With no provider.** **Copy a prompt** gives the text to run in an AI tool your organisation has approved, and its
  pasted answer goes through the same checks.

Every draft is recorded with its provider, model, prompt version and a hash of what was sent.

A **Related** tab lists other articles with the same CVE, link or story, when each was briefed, and its open actions.
Each is one click from mentioning an action or joining the story.

**TLP everywhere.** The article, each bullet, each screenshot and "why it matters to us" carry a TLP 2.0 marking,
and a part never counts lower than its article. On a threat, "why it matters to us" starts at AMBER, since it usually
names our own systems. Analysts can see each article's and each brief's **History**: who changed which field, and
when.

![Article editor](docs/screenshots/article-editor.png)

### Present mode
A full-screen, keyboard-driven slide deck: a title slide with the agenda, the team's opening note (what's coming up,
the week's phishing trends; team edition only, carried from the previous brief), the opening numbers, one slide per article,
and a wrap-up with every recommended action not yet raised. Screenshots zoom, and articles are ticked off as
discussed, and the editions can be switched on screen.

**Start the meeting** and it's live for the team: a banner on every page, and a Join button on the brief, invite
everyone on the team edition in (or paste the copied link). People who join follow the presenter's slide, can browse
off on their own and come back, and show in the strip with the slide they're on. They raise a hand on an article, take
notes (each person's own, against the article on screen), raise actions and take or assign them. The presenter can
bring everyone back or lock everyone to their slide, clear hands and hand presenting to another analyst; if the
presenter drops out, another analyst can take over. Attendance is recorded as people join, and the presenter can add people who were in the room. The
brief's page keeps who attended and the notes, by article.

Each of an article's recommended actions is **raised in one click** from its slide. It's raised at the article's
priority, as the kind its wording suggests and for the team that last took that kind; both can be changed first.
Articles not discussed carry to the next brief.

![Opening metrics slide](docs/screenshots/present-metrics-slide.png)

### The two editions
Both editions go through your SMTP relay to administered recipient lists, styled so Outlook shows them as intended,
with screenshots embedded rather than fetched.
- **The team edition** opens with the day's numbers, and lists each article's indicators and the actions raised.
- **The company-wide edition** leaves out anything above its TLP ceiling (articles, bullets, screenshots and
  captions) rather than hiding it. Administrators set the ceilings.

**Sending:**
- **Before it goes.** You can preview the email exactly as it will go and send a test copy to yourself. The send
  itself needs an explicit confirmation that shows the marking and every recipient.
- **A record of what went.** Each send freezes a copy of what went, HTML and images, and logs who sent it to whom.

![Team edition email](docs/screenshots/email-team.png)

![Company-wide edition email](docs/screenshots/email-company-wide.png)

### Review before send
An analyst can ask a colleague who writes articles (an Analyst or an Admin) to review an article before it goes (this
week's presenter is suggested). The reviewer gets it on Today and opens the review page: the article as each edition will carry it, comments on a part (the title,
the bottom line, a bullet, why it matters), optionally quoting the words selected, a short checklist (TLP right for
every part, every claim sourced, nothing internal in the company-wide edition, the bottom line stands alone), and
*Approve* or *Ask for changes*. The author sees the comments in the editor and resolves them. A change to what the
article says after approval (a screenshot added or removed included) puts the review out of date; marking it Ready
doesn't, and an approval is refused if the article changed while the reviewer was reading it. The Send dialog lists
the brief's articles that haven't been reviewed. Review is optional; an administrator can require it (Access &
distribution) for anything AMBER or above, for threats, or both, and then an edition holding such an
article waits until it's approved.

### Corrections after a send
Email can't be recalled, so a mistake found after the brief went out gets a **correction**. That's a short email of
its own to the people who had the original:
- a required note on what changed;
- for each article, a diff of the article as sent against the article now, cut to the edition's TLP ceiling.

A **TLP slip** (something went to an audience it shouldn't have) works differently:
- the correction tells recipients to delete the email, and repeats nothing that leaked;
- the send is withdrawn from the Reader archive as soon as the slip is confirmed, whatever becomes of its email;
- so is every company-wide send of that day made before the slip, a re-send that put it right included. To publish
  the corrected version, send the edition again after the slip; the send log says so under each withdrawn send.

Corrections are frozen and logged like a send, and listed under the original.

### Copy for SharePoint
The company-wide edition as clean HTML in one click, plus a zip of its images, for a SharePoint page.
An analyst marks it posted (who and when are kept). An administrator can require that under Access & distribution;
then a brief isn't finished until it's marked.

With email off (`Email:Enabled`), the Send step becomes **Distribute**: once the brief has gone out another way, an
analyst marks it distributed. It's finished as a send would finish it (its numbers lock), but nothing is frozen, so it
isn't listed in Published briefs.

![Copy for SharePoint](docs/screenshots/sharepoint-copy.png)

### Published briefs
What Readers see, and all they see: the sent company-wide editions, frozen as sent. Each shows its headlines, and a
filter matches the words each edition went out with. A corrected day shows its corrections above the edition.

![Published archive](docs/screenshots/published-archive.png)

### Actions and hunting
Follow-up work from the brief: IOC sweep, hunt, block, patch, config change, awareness or vendor inquiry. Each action
is owned by a team and optionally assigned. It's due by a date the SLA targets suggest, and closed with an outcome
that fits its kind.
- **Raising.** Raise one from an article, from the board, or in the meeting without leaving the slide.
- **Splitting.** A long remediation splits into parts, one per team.
- **Around it.** An action can have an external ticket link, comments (from any team) and a readable history.
- **How late.** An overdue action says how late it is.

**Hunt queries:**
- **Where they come from.** They're generated from the article's indicators, or from every article in its story.
- **Which SIEMs.** **Cortex XSIAM (XQL)**, **Microsoft Sentinel / Defender (KQL)** and **CrowdStrike Falcon / Next-Gen
  SIEM**.
- **Templates.** Administrators edit a template per kind of indicator.
- **Running them.** Copy the query and run it in your SIEM; TIDE never signs in to a SIEM. Then record hits or no
  hits, with a screenshot.
- **Closing a clean sweep.** A sweep whose query came back clean closes in one step, *No hits, close*, once the query
  was copied. The days it searched and any values it left out go on record.

**Keeping track:**
- **Lists.** Views by me, my teams, all open, overdue and recently closed.
- **Reminders.** Reminder emails for due soon and overdue, and a weekly team digest. Each is off until enabled.

![Action with a hunt query](docs/screenshots/action-hunt.png)

### Workload
Each team's open actions, what's late, and who holds what, with per-person and P1 counts.

![Workload](docs/screenshots/workload.png)

### Search and indicator lookup
One box at the top of every page.
- **Words.** Words or "phrases" search across articles, actions and stories. That covers bullets, indicators, CVEs,
  ATT&CK techniques by name and captions, with the matches marked.
- **An indicator.** Paste an IP address, domain, URL, hash or email address, defanged or not, to see:
  - every article it's on, and when it was briefed;
  - other indicators on the same host;
  - the actions raised, and whether their hunts found anything;
  - a ready hunt query.
- **A CVE.** Paste a CVE for its CVSS, EPSS and KEV status, the articles about it, and what was done.

**Go to** (`Ctrl+K`) jumps to a page, starts an article or action, or opens one by title or number ("A-12"). `?`
lists the page's keyboard shortcuts, with the switch that turns single-letter shortcuts off.

![Indicator lookup](docs/screenshots/search-lookup.png)

### Threat stories
Stories link articles across days into a timeline, with their CVEs, actors, techniques and actions: a campaign, a
vulnerability being exploited, or an actor followed over weeks. An article joins one from the editor, which suggests
the story of a related article.

### Adversary profiles
**Intelligence › Adversaries** keeps one profile per threat actor or campaign, under every name it goes by, so
Scattered Spider, UNC3944 and Octo Tempest are one. A profile's page has:
- a **timeline** of the articles tagged with it or naming it (in the title, the actors or the bottom line), and their
  actions and indicators, with how many briefs it was in this quarter;
- the **ATT&CK techniques** across those articles, darker where more articles show them, downloadable as an ATT&CK
  Navigator layer scored by count;
- **our notes** (internal, AMBER), motivation and sectors.

Articles that name a profile but aren't tagged are listed for an analyst to check and tag: nothing is linked
automatically. The editor's Related tab tags an article with a profile by any of its names, or adds one. A **watched**
profile marks triage items that name it ("Watching: Scattered Spider"). Two profiles that turn out to be one group can
be merged. MITRE ATT&CK's groups and campaigns can be imported for their ids and aliases: fetched from MITRE's TAXII server, or uploaded from a saved copy where the server has no internet.

### Detection coverage
**Intelligence › Coverage** compares the ATT&CK techniques the team briefed in the last 90 days with the SOC's
detection rules. An administrator imports the rules under Administration › Detection rules, from a Sentinel
analytics-rules export, Sigma YAML (a file or a zip of a folder) or a CSV, or another tool pushes them through the API.
TIDE keeps each rule's name, id, technique tags, platform and whether it's enabled; it never reads, stores or runs a
query. A technique counts as covered when a current, enabled rule is *tagged* with it, its parent or a sub-technique.
- **Gaps first.** Techniques with no enabled rule are listed first, those a watched adversary uses at the top, each
  with how many articles showed it. *Raise action* opens a Config change action for the chosen team (one named for
  detection is picked first), linked to the latest article, and the row shows it until it's closed.
- **Where analysts write.** The editor's technique chips and an adversary profile's techniques say "2 rules", "1
  disabled" or "no rule", and the profile's Navigator layer is coloured by it.
- **Internal.** Rule names and counts never reach an edition, an export or a webhook. See
  [ADR 0017](docs/decisions/0017-detection-rules-are-referenced-and-tagged.md).

### Indicator exports
The indicators of an article, a day's brief or a story can be exported in two formats:
- **STIX 2.1:** a bundle of reports, indicators, CVEs and ATT&CK techniques, each with its TLP 2.0 marking.
- **CSV.**

You choose the highest TLP to include, and articles above it are left out whole.

![Export indicators](docs/screenshots/export-indicators.png)

### Threat trends and the board scorecard
Month on month or quarter on quarter, Trends counts:
- the scorecard tags, rolled up to their categories;
- ATT&CK tactics and techniques;
- the follow-up work: actions raised by kind and team, done on time or late, outcomes, hunts and their hits, and
  what was open or overdue when each period ended.

Chart any line, as many as you like. Click any number to see the articles or actions behind it, or open them as a
pre-filtered Articles list, ready for the monthly or quarterly threat trends report. Opened from a tactic or
technique, each article in the list shows the techniques it was counted for. The table, or a period's articles with
their tags and techniques, downloads as CSV.

![Trends](docs/screenshots/trends.png)

### Opening metrics
Many teams open the brief with a round of operational numbers. TIDE holds your own groups of them. Each metric says
whether higher is better, worse or neutral, and can have warning and critical thresholds. Nothing is built in, and a
generic example set can be imported.

**Entering them:**
- **The form.** A fast "Today's numbers" form, with the value each box is compared with shown before you type. A
  colleague's numbers appear as they save them, without a reload; a box you're typing in keeps what you typed and
  says they changed it.
- **Imports.** Values can also come from CSV, as an upload or from a drop folder.

**Where they appear:**
- **The meeting.** A slide per group: its numbers as large tiles over the chart an administrator picks for it (a
  30-day trend, share of the whole, side by side, or none). A group whose numbers are all 0 is a one-line note on the
  first metrics slide instead of a slide of its own.
- **The team email.** A block at the top: each value with its change from the previous business day, and a
  four-week sparkline.
- **History.** A history page with a chart and a CSV export.

![Metrics history](docs/screenshots/metrics-history.png)

### Brief rotation
A calendar of who presents the brief each business day, built from a weekly rota. (It's the brief rotation, not an
on-call schedule.)
- **Swaps and cover.** Swap days or whole weeks with a colleague, or ask one to cover. Only the colleague taking the
  days approves, and the manager is emailed a notice of every approved change. A request shows at once for the
  colleague it's waiting on, and the answer at once for the one who asked, without a reload. Approved after some of
  its days have passed, only the days still to come change hands; a swap needs days to come on both sides.
- **Coming up for you.** Your next brief days and the requests waiting for you, an optional reminder the business day
  before you present, and your brief days as a calendar file for Outlook.

![Brief rotation calendar](docs/screenshots/rotation-calendar.png)

### The API, for the SOC's other tools
A versioned API under `/api/v1`, described at `/api/v1/openapi.json`, lets a SOAR playbook close the action it
finished (with a note, under its own name in the history), Power BI read the metrics, and a SIEM rule propose an item
for triage, and a detection pipeline push its rules for Coverage. Briefs come cut exactly as the team edition is, at the caller's TLP ceiling; a Sent one says how it was finished (`finishedAs`: emailed, distributed or notSent). A caller signs in with an API
token issued under Administration > API access (scoped, expiring, shown once, stored as a hash) or as a server's gMSA
with Windows. Webhooks post, signed with HMAC-SHA256, when an action is raised or becomes overdue and when a brief is
sent (checked every minute; a raise or a send is posted once it's at least 30 seconds old). See [ADR 0016](docs/decisions/0016-api-tokens-scopes-and-signed-webhooks.md).

### Administration
One Administration entry opens every setting page, grouped into tabs, with a getting-started checklist read from
the live configuration. It covers:
- the teams, their members and the SLA targets;
- playbooks and card rules;
- the sources and CVE enrichment;
- the scorecard taxonomy (tags are retired, never deleted, once used);
- the query templates;
- the opening metrics and their imports;
- the AD group-to-role mappings, the edition ceilings and the recipient lists;
- the wording of the reminder and rotation emails, with a live preview and a test send;
- diagnostics, the server configuration, and a check for newer releases;
- the audit log: every change saved across TIDE (settings, role mappings, teams, sources, recipients, ceilings,
  articles and the rest), who made it, when, and each field before and after. It filters by person, kind of record,
  one record, dates, action and words, keeps the filters in the address, and exports to CSV. Secrets show only as
  set or changed. Administrators only, since it holds text at every TLP marking.

> Screenshots use local dev sign-in and the seeded sample data.

## Stack

- **.NET 10**, ASP.NET Core, **Blazor Server** (interactive)
- **EF Core**: SQLite for local development (zero-install), SQL Server in production
- **Windows Integrated Authentication** (AD group → role mapping), with a dev sign-in for local use
- **Bootstrap 5**, retinted through TIDE's own design tokens, under a strict CSP (`script-src 'self'`)

## Architecture

```
src/
  Tide.Domain                 Entities, enums, the indicator normalizer (no dependencies)
  Tide.Application            The services pages call: articles, briefs, distribution, actions, intake, metrics, trends
  Tide.Infrastructure         EF Core, the audit interceptor, SQLite migrations, file store, email, AD lookups
  Tide.Migrations.SqlServer   The production SQL Server migrations, in their own assembly
  Tide.Web                    Blazor Server UI, authentication, endpoints, background pollers
tests/
  Tide.UnitTests              Edition cutting and the TLP leak tests, renderers, SLA and metric rules, hunt dialects
  Tide.IntegrationTests       Services against SQLite with the real migrations, and the whole app over HTTP
```

Dependencies point inward. **`BriefEditions.Build`** is the one place that decides what an audience sees. The
previews, present mode, the email, the SharePoint copy and corrections all go through it.

The architecture is described in **[docs/architecture.md](docs/architecture.md)**, and the decisions behind it in
**[docs/decisions](docs/decisions)**.

## Run locally

```bash
dotnet run --project src/Tide.Web --launch-profile http
```

Then browse to <http://localhost:5110>. On first run the SQLite database is created, migrated, and seeded with sample
articles and today's brief. Local development signs you in as a "Dev Analyst" holding the Analyst and Admin roles;
add `?as=Someone` to the address to try a second person. Email is written as `.eml` files under
`src/Tide.Web/App_Data/mail` instead of being sent. See [docs/development.md](docs/development.md).

## Test

```bash
dotnet test
```

## Configuration (`appsettings.json`)

| Key | Purpose |
|---|---|
| `Auth:Mode` | `Dev` (local sign-in) or `Windows` (production Integrated Auth) |
| `Database:Provider` | `Sqlite` (dev) or `SqlServer` (prod); `ApplyMigrations=false` when a DBA applies the schema |
| `ConnectionStrings:Default` | The database connection string |
| `RoleMapping:Groups` | AD groups → roles, seeded on first run, then edited in Administration |
| `Files:RootPath` | Screenshots on disk (kept **outside** the web root) |
| `Distribution` | Default TLP ceilings for the editions |
| `Email` | The SMTP relay (`Enabled=false` sends nothing) |
| `Intake` / `Cve` | The proxy, limits and addresses for feed polling and CVE enrichment |
| `Ai` | Optional AI drafting help: `Provider` (`AzureOpenAI`), `Endpoint`, `Deployment`, the highest TLP sent (`MaxTlp`), and `WebGrounding`. The key goes in the `Ai__ApiKey` environment variable |

The operational settings are defaults here and changed in the app, where each change is audited: the organization's
time zone, meeting time and team name, the reminders, polling and CVE enrichment, the metrics drop folder, and the
update check. Sign-in, hosting, the database, files, email and the outbound limits stay on the server
(**Administration › System › Server configuration**).

Every key in `appsettings.json` has a `"//"` line saying what it does.

### Going to production

The full step-by-step for a Windows Server host with IIS and SQL Server is in **[docs/INSTALL.md](docs/INSTALL.md)**,
with the tooling in [`deploy/`](deploy/). In brief:

1. `deploy/Install-Database.ps1` creates the database and the app-pool login on your SQL instance.
2. `deploy/Install-Tide.ps1` installs the app with its own IIS site and pool. The site gets its own host name and an
   SNI binding, so other sites on the server are left alone.
3. `deploy/Verify-Install.ps1` checks it.

Each version tag on `main` publishes a **release bundle** (the published app and these scripts) on the
[Releases](https://github.com/jashley92/Tide/releases) page, so a server needs no .NET SDK.

`deploy/Upgrade-Tide.ps1 -Version latest` upgrades an install in place:
- it checks the server and the database's migrations;
- it backs up the database, the settings and the app;
- it swaps the app in, and rolls it back if the new build doesn't start.

`deploy/Backup-Tide.ps1` and `deploy/Test-TideRestore.ps1` take and test backups. See
[docs/UPGRADE.md](docs/UPGRADE.md).

## Roles

| Role | What it does |
|---|---|
| `Analyst` | Writes articles, builds and presents the brief, and sends it |
| `ActionOwner` | Works their teams' actions and reads the team edition |
| `Reader` | Reads the published company-wide editions |
| `Admin` | Administers TIDE |

Each role is granted by AD groups and is a bundle of code-defined permissions. The permissions are enforced on the
page and again in the service layer.

## Roadmap

| Milestone | What it adds |
|---|---|
| Metric connectors | Pull the opening metrics instead of typing them: a generic HTTP JSON source, then optional product-specific ones, with the analyst able to override |
| M5: Intelligence | Drafting help is in (Azure OpenAI, or copy a prompt and paste the answer back). Next: one-line summaries in triage (EP-70 B), and more providers |

## License

Copyright © 2026 James Ashley

TIDE is free software, licensed under the **GNU Affero General Public License v3.0** (AGPL-3.0). You may use, study,
share, and modify it under those terms; there is **no warranty**. Because the AGPL covers network use, anyone who runs
a modified version as a hosted service must make their modified source available to its users. See
[LICENSE](LICENSE) for the full text.
