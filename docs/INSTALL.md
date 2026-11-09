# Installing TIDE

TIDE installs as an IIS site of its own. It can share a Windows Server and a SQL Server instance with other apps: it
gets its own database, app pool, host name, web root and data folder, and the installer never changes another
site's bindings or certificates. If a host name of its own is hard to get, it can instead be installed under a path of
an existing site, such as `https://intranet.contoso.com/tide` (see [Under another site](#under-another-site-optional)).

## 1. What you need

- **Web server.** Windows Server 2019 or later with IIS. You need the Web-Server role and the Windows Authentication
  feature:

  ```powershell
  Install-WindowsFeature Web-Server, Web-Windows-Auth -IncludeManagementTools
  ```

- **WebSockets.** IIS's **WebSocket Protocol** feature. Every open TIDE page keeps one connection to the server; without
  this feature it silently falls back to long polling, where each click and keystroke is a request of its own, so
  typing lags and the server does far more work:

  ```powershell
  Install-WindowsFeature Web-WebSockets
  # On Windows 10/11 (a test machine): Enable-WindowsOptionalFeature -Online -FeatureName IIS-WebSockets -All
  ```

  `Install-Tide.ps1` checks for it. Run interactively, it offers to install it; with `-InstallWebSockets` (or
  `InstallWebSockets = $true` in the answers file) it installs it without asking; run from an answers file or a
  non-interactive session without that, it warns, gives the command, and carries on (TIDE then uses long polling
  until the feature is installed). It also turns WebSockets on for TIDE's site if
  `applicationHost.config` or `web.config` has them off (`system.webServer/webSocket`). Windows rarely needs a restart
  for it; the installer says when it does.

  **A reverse proxy or load balancer** in front of IIS must pass WebSockets through too: the `Upgrade` and `Connection`
  headers, and long-lived connections (an idle timeout of a few minutes at least). One that strips them gives long
  polling just as a missing feature does.

- **.NET.** The **.NET 10 Hosting Bundle**, and the .NET 10 SDK if you publish on the server.
- **SQL Server.** 2019 or later. TIDE uses a database of its own on your existing instance.
- **Service account.** One account for the app pool. A gMSA (`CONTOSO\svc-tide$`) is best, because it has no
  password to manage.
- **AD groups.** Groups for the roles:

  | Role | Who | Can |
  |---|---|---|
  | Analyst | The CTI team | Write articles, build and present briefs |
  | Action owner | Other security teams (Vulnerability Management, Cyber Defense) | Read the team edition and work their actions. In the app they see articles at every TLP; the edition ceilings limit what's sent, the API and webhooks |
  | Reader | Everyone else | Read the published (company-wide) archive |
  | Admin | TIDE's administrators | Everything, including role mappings, scorecard tags and edition ceilings |
  | Viewer | Managers, auditors, anyone who should see the team's work without changing it | Read everything the team edition shows, every action included, and change nothing. Join meetings, raise a hand and add notes |
  | Visitor | Colleagues from elsewhere who sit in on the meeting | Read the published archive. In a live meeting, see what the room sees, team edition included, only while they're in it |

  The installer's answers file names groups for the first four; map Viewer and Visitor groups on the Role mappings
  page (Administration > Access & distribution), or in a configuration bundle. See
  [what each role can do](#what-each-role-can-do).

- **What TIDE reads from AD.** For each person who signs in, TIDE looks up their display name and email address
  (the `mail` attribute) in their own domain, as the app-pool account, and keeps them in its user list. Test copies,
  reminders, digests and rotation emails go to that address. A person whose account has no `mail` gets none of them; a
  UPN that looks like an address isn't used in its place. TIDE asks AD about each person again once every 24 hours (when
  they're next seen), so a new name or address reaches it within a day; while AD can't be reached, the name and address
  found before are still used. A test copy for someone signed in while AD is down goes to the address saved on an
  earlier visit.
- **DNS and TLS.** A host name such as `tide.contoso.com` pointing at the server, and a TLS certificate for it in
  `LocalMachine\My`.

### What each role can do

The same table is on the Role mappings page (Administration > Access & distribution). "Only in a live meeting" and
the like mean the role gets it only there; a Visitor's team-edition access lasts only while they're seated in that
brief's live meeting.

<!-- capabilities:start (generated from Capabilities.Markdown(); CapabilitiesTests checks it) -->
| Activity | Administrator | Analyst | Action owner | Viewer | Visitor | Reader |
| --- | --- | --- | --- | --- | --- | --- |
| **Briefs and meetings** | | | | | | |
| Read the company-wide archive (published briefs) | Yes | Yes | Yes | Yes | Yes | Yes |
| Read the team edition: briefs, the brief archive, Today | Yes | Yes | Yes | Yes | only its live meeting, while in it | No |
| Join and follow a live meeting | Yes | Yes | Yes | Yes | Yes | No |
| Raise a hand in a meeting | Yes | Yes | Yes | Yes | Yes | No |
| Add meeting notes | Yes | Yes | Yes | Yes | No | No |
| Present, start and end a meeting | Yes | Yes | No | No | No | No |
| Build a brief (its articles, order, team note) | Yes | Yes | No | No | No | No |
| Reopen a brief | Yes | Yes | No | No | No | No |
| Send a brief, finish one not sent, mark it posted | Yes | Yes | No | No | No | No |
| Read the brief rotation | Yes | Yes | Yes | Yes | No | No |
| Ask for a rotation swap or cover | Yes | Yes | No | No | No | No |
| **Articles and intake** | | | | | | |
| Read articles, stories and search | Yes | Yes | Yes | Yes | No | No |
| Triage the inbox | Yes | Yes | No | No | No | No |
| Write and edit articles and stories | Yes | Yes | No | No | No | No |
| Review articles | Yes | Yes | No | No | No | No |
| **Actions** | | | | | | |
| Read actions: their pages, the board, Workload | Yes | Yes | Yes | Yes | the one shown, in a live meeting | No |
| Comment on actions | Yes | Yes | Yes | No | No | No |
| Raise actions and edit every field | Yes | Yes | No | No | No | No |
| Work actions: status, outcome, assignee, checklist, hunts | Yes | Yes | own team's | No | No | No |
| Add and describe files | Yes | Yes | own team's | No | No | No |
| Remove files | Yes | Yes | own files | No | No | No |
| Restore removed files | Yes | No | No | No | No | No |
| Add indicators for an action's hunts | Yes | Yes | own team's, with no article | No | No | No |
| Hand an action off to another team | Yes | Yes | own team's | No | No | No |
| Split an action to several teams | Yes | Yes | No | No | No | No |
| Add, edit and retire external teams (teams that work outside TIDE) | Yes | Yes | Yes | No | No | No |
| Set a team's active limit | Yes | No | No | No | No | No |
| Read the saved hunt queries | Yes | Yes | Yes | Yes | No | No |
| Save, edit, retire and restore saved hunt queries | Yes | Yes | Yes | No | No | No |
| Delete saved hunt queries | Yes | own ones | own ones | No | No | No |
| Write an Outreach action's email, mark it sent and record the reply | Yes | Yes | own team's | No | No | No |
| Read the outreach templates | Yes | Yes | Yes | Yes | No | No |
| Save, edit, retire and restore outreach templates | Yes | Yes | Yes | No | No | No |
| Delete outreach templates | Yes | own ones | own ones | No | No | No |
| Read the playbooks | Yes | Yes | Yes | Yes | No | No |
| Add, edit, reorder, retire and restore playbooks | Yes | Yes | Yes | No | No | No |
| Delete playbooks no action was started from | Yes | own ones | own ones | No | No | No |
| Read the board rules (the card rules, read-only) | Yes | Yes | Yes | Yes | No | No |
| Choose my own notifications (reminders, digests, rotation emails) | Yes | Yes | Yes | Yes | No | No |
| **Numbers** | | | | | | |
| Enter the day's numbers | Yes | Yes | No | No | No | No |
| Read the metrics history | Yes | Yes | Yes | Yes | No | No |
| **Our stack and adversaries** | | | | | | |
| Read Our stack and adversaries | Yes | Yes | Yes | Yes | the card shown, in a live meeting | No |
| Edit Our stack and adversaries | Yes | Yes | No | No | No | No |
| **Reports** | | | | | | |
| Reports, trends and exports | Yes | Yes | Yes | Yes | No | No |
| Team commitments scorecard and its CSV | Yes | Yes | Yes | Yes | No | No |
| Coverage of detection rules | Yes | Yes | No | No | No | No |
| **Administration** | | | | | | |
| Organization, holidays, teams and SLAs, rotation | Yes | No | No | No | No | No |
| Card rules, query templates | Yes | No | No | No | No | No |
| Sources, source health, scorecard tags, detection rules | Yes | No | No | No | No | No |
| Opening metrics and metric imports | Yes | No | No | No | No | No |
| Role mappings, edition ceilings, recipients, email templates | Yes | No | No | No | No | No |
| API tokens and webhooks | Yes | No | No | No | No | No |
| Diagnostics, configuration, audit log, updates | Yes | No | No | No | No | No |
<!-- capabilities:end -->

## 2. The answers file

Run `deploy/New-TideConfig.ps1`: it asks for each value, with defaults and checks, and writes
`deploy/tide.config.psd1`. It runs anywhere, a workstation included; checks of AD groups, certificates and IIS sites
it can't make from there only warn. Or copy `deploy/tide.config.template.psd1` to `deploy/tide.config.psd1` and fill
it in by hand.

```powershell
.\deploy\New-TideConfig.ps1 -OutFile .\deploy\tide.config.psd1
```

The file is git-ignored, since it names your groups, hosts and accounts. Both installers read it, and a parameter
passed on the command line wins over it. A password never goes in it. Once TIDE is running, **Administration ›
System › Configuration bundle** downloads one filled in from the server, for repairs, upgrades or a second server; it
also exports the setup made in the app (sources, AD group mappings, scorecard tags, query templates, and the settings
changed in the app) to import into another TIDE. **Diagnostics**, **Server configuration** and **Setting sources** beside it show whether
the install is healthy and where each setting comes from, secrets shown only as set or not.

## 3. The database

Run this as a sysadmin on the SQL instance, or from a machine that can reach it:

```powershell
.\deploy\Install-Database.ps1 -ConfigFile .\deploy\tide.config.psd1
```

It does two things:

- Creates the `Tide` database, with FULL recovery and read-committed snapshot isolation.
- Adds a Windows login and user for the app-pool account. There are no SQL logins or passwords.

It runs in one of two modes:

- **AppMigrates** (the default) makes the account `db_owner`, and the app applies its schema at startup.
- **DbaApplies** grants only read, write and EXECUTE, and a DBA applies the schema.

### The SQL Server certificate

The connection to SQL Server is always encrypted. By default, though, neither the installer nor the app checks the
instance's certificate (`TrustServerCertificate=True`), because most instances use the self-signed one SQL Server
makes for itself. That leaves the connection open to an attacker on the network who can pose as the server.

If the instance has a certificate that chains to a root the SQL and web servers trust, and that names the host in
`SqlInstance`, turn the check on. Set `ValidateServerCertificate = $true` in the answers file (`New-TideConfig.ps1`
asks), or pass `-ValidateServerCertificate` to both installers. `Install-Database.ps1` then connects without
`-TrustServerCertificate` (or sqlcmd's `-C`), and `Install-Tide.ps1` writes `TrustServerCertificate=False` into the
connection string. A certificate that doesn't pass stops `Install-Database.ps1` at its first connection, before it
changes anything, and stops the app from starting (500.30, with the reason in the Application event log). To turn the check on for a running install, set `TrustServerCertificate=False` in
`ConnectionStrings:Default` in `appsettings.Production.json` and recycle the app pool.

`Install-Database.ps1` runs its SQL through `Invoke-Sqlcmd` when that can make this connection, and through `sqlcmd.exe`
when it can't. The SqlServer module (version 22 or later) always can. The older SQLPS module, which SQL Server setup
installs on the SQL host, can't encrypt a connection without checking the certificate, so with the default setting it
uses `sqlcmd.exe` instead. With neither, the script stops before it connects and says what to install
(`Install-Module SqlServer -Scope CurrentUser`).

## 4. The app

Run this on the web server, elevated:

```powershell
.\deploy\Install-Tide.ps1 -ConfigFile .\deploy\tide.config.psd1
```

For a non-gMSA account, add `-AppPoolCredential (Get-Credential)`. Run from the repository, it publishes the app,
which needs the .NET 10 SDK and NuGet access. Run from the `deploy` folder of a release bundle (`tide-<version>.zip`
from the [Releases](https://github.com/jashley92/Tide/releases) page), it copies the bundle's already-published app
instead and needs neither ([UPGRADE.md](UPGRADE.md#installing-or-upgrading-from-a-bundle)). The installer comes
inside the bundle, so it can't vouch for it: check the bundle first, with `Test-TideBundle.ps1` from an earlier
release's deploy folder or against the SHA-256 on the release page
([deploy/README.md](../deploy/README.md#checking-a-bundle)). Otherwise, publish elsewhere and copy the output in:

```bash
dotnet publish src/Tide.Web -c Release -o out
```

Then copy `out` into the site folder and add `-SkipPublish` to the install command.

The installer:

- **Data folder.** Creates the data root (`files` for screenshots, `dp-keys` for the sign-in key ring) outside the
  web root. Nothing is inherited: only SYSTEM, Administrators and the app-pool account can open it.
- **Settings.** Writes `appsettings.Production.json` into the site. A re-run keeps the existing file and writes the
  new one beside it as `.new`.
- **App pool.** Creates the pool: No Managed Code, your account, always running, no scheduled or overlapping
  recycles, and one worker process (`processModel.maxProcesses = 1`, Maximum Worker Processes in IIS Manager). It
  reads that back and stops if the pool won't take it. Don't make the pool a web garden: each worker would keep its
  own sign-ins and live pages. The background jobs run in one process whatever the pool says: the processes on one
  database share a lease (the `JobLeases` table), and only its holder runs them (see
  [backup-and-recovery.md](operations/backup-and-recovery.md#hosted-services-inside-the-app)).
- **Site.** Creates the site with its host name and, given a certificate, an SNI binding on 443. Windows
  Authentication is on and Anonymous off, except `/health` for monitors (Windows off there) and `/api/v1` for API
  tokens (Windows left on, so a gMSA still signs in).

### Under another site (optional)

Use this only when TIDE can't have a host name of its own. Add the existing site's IIS name and a path to the answers
file (or pass them as `-ParentSite` and `-AppPath`):

```powershell
ParentSite = 'Intranet'
AppPath    = 'tide'
```

The installer then creates an IIS application at `/tide` under that site instead of a site:

- **Still TIDE's own:** the app pool and account, the folder (`SitePath`), the data root and the database. `SitePath`
  must be outside the other site's folder: an upgrade of that app may mirror over its own folder and would delete TIDE.
  If the parent site keeps a `<site folder>-deploy` folder beside it, keep it out of that too (the installer refuses
  both), and out of any rollback snapshot the parent site keeps.
- **Left alone:** the site's bindings, certificate, its own app pool and its authentication. Windows Authentication and
  the anonymous `/health` and `/api/v1` are set on `/tide` only.
- **Taken from the site:** the host name (unless you pass `Hostname`) and HTTPS, if the site has an HTTPS binding.
  `App:BaseUrl` is written as `https://<host>/tide`, so email links include the path. `AllowedHosts` lists every host
  name on the site's bindings (such as `intranet;intranet.contoso.com`), so TIDE answers at each name the site does.
- **Refused:** a path that is already another application, or a folder in the site that TIDE would hide.

TIDE serves itself under the path IIS gives it, with no setting to change. Its `web.config` removes a handler a parent
ASP.NET Core site may pass down, which would otherwise stop TIDE with **500.19**. Behind a reverse proxy other than IIS
that forwards a sub-path, set `App:PathBase` (such as `/tide`) instead.

What sharing a host name means:

- **One origin.** TIDE and the other app are the same web origin. Each app's cookies for `/` reach the other, and a
  script flaw in either app could reach the other's pages. TIDE scopes its own cookies to its path and keeps its own
  sign-in key ring, but installing it this way means trusting the other app as much as TIDE. A script in one app can
  call the other's endpoints as the signed-in user, which is why TIDE's CSP allows no inline or outside script. Both
  apps also share browser storage; TIDE's keys start with `tide-`, so they don't collide with the other app's.
- **One Kerberos SPN.** The host name has one `HTTP/<host>` SPN. With IIS's defaults (kernel-mode authentication,
  `useAppPoolCredentials` off) the server's machine account decrypts tickets and both apps sign in with Kerberos. If
  the site has `useAppPoolCredentials` on, with the SPN on its own pool account, TIDE inherits that and its account
  can't decrypt the tickets: sign-in falls back to NTLM, or fails where NTLM is off. The installer warns when it sees
  this. Run TIDE's pool as the same account, or turn the setting off and move the SPN to the computer account.
- **The other app loses the path.** Anything it served under `/tide` now goes to TIDE.
- **Verify with the path and the parent site:**
  `.\deploy\Verify-Install.ps1 -Url https://intranet.contoso.com/tide/ -AppPoolName TIDE -SiteName Intranet`

## 5. Check it

```powershell
.\deploy\Verify-Install.ps1 -Url https://tide.contoso.com/
```

Besides the pool, the site, the health endpoints and the sign-in, it checks WebSockets: the IIS WebSocket Protocol
feature is installed, `system.webServer/webSocket` isn't off for the site, and a real WebSocket to the app (signed in
as you, through any proxy on the way) gets its Upgrade. To check WebSockets another way:

- **Administration > System > Diagnostics** shows the connection your own page is using right now (WebSockets,
  Server-Sent Events or Long polling; anything but WebSockets is flagged), and how many connections since the app
  started used each, so you can see whether other people are falling back.
- **The browser's Network tab** (F12, then reload a TIDE page): one `websocket` row for `_blazor` with status
  **101** is good. Many `xhr` rows for `_blazor?id=...`, a new one on every keystroke, mean long polling.

Then sign in as a member of the Admin group:

1. Under **Administration > Access & distribution**, confirm the role mappings, set the edition ceilings, and set
   who each edition is emailed to.
2. Under **Administration > Scorecard tags**, edit the starter taxonomy to match your board's.
3. Under **Administration > Metrics**, define the opening metrics your meeting reads out (or import the generic
   example set and rename it).
4. Under **Administration > Teams & SLA**, add the teams that own follow-up actions, put people on them (they appear
   once they've signed in), and check the SLA targets. Under **Query templates**, change the dataset and field names
   in the hunt queries to match your SIEM, add templates for the kinds of indicator your SIEM can search that have
   none, and turn off any it can't.
5. Under **Administration > Sources**, add the feeds the team reads (and, if you like, the CISA KEV catalog), then turn
   polling on (see Intake and CVE data below).
6. Under **Administration > Rotation**, add the analysts to the brief rotation (who presents the daily brief) in order, fill the coming weeks, and
   set the managers who are told about swaps and cover.

## Email

The brief is emailed through your organization's SMTP relay. In `appsettings.Production.json`, under `Email`:

| Key | What to set |
|---|---|
| `Enabled` | `true` to send. While `false`, the send buttons say email is off. |
| `SmtpHost`, `SmtpPort` | The relay. |
| `EnableSsl` | `true` (STARTTLS) unless the relay is on this host or doesn't offer it. |
| `SmtpUseDefaultCredentials` | `true` to sign in as the app-pool account (on-premises Exchange), with no password anywhere. |
| `SmtpUsername` | An account to sign in as instead. Put its password in the `Email__SmtpPassword` environment variable on the app pool, not in the file. Leave both blank for a relay that accepts this host without signing in. |
| `From`, `FromName` | The sender. The relay must allow the app to send as it. |

Set `App:BaseUrl` to the site's address so the email links back to TIDE. Then send yourself a test copy from the **Send**
step on Today (or on any day's brief).

### Reminders and digests

Each is off until an administrator turns it on in **Administration › Organization › Teams & SLA**, which also sets
their times and who's told when there's no brief. The `Notifications` section in `appsettings.json` holds the defaults
until then (times are in the organization's time zone, set on **Organization settings**):

| Key | Sends |
|---|---|
| `DueSoon:Enabled`, `Overdue:Enabled` | At `ReminderTime` on business days, one email per person listing their actions due today or the next business day, or past due. An unassigned action goes to its team's mailbox, else its members. |
| `Digest:Enabled`, `Day`, `Time` | Each team's week: open, overdue and closed actions, to its mailbox or members. |
| `BriefNotReady:Enabled`, `Time`, `Recipients` | If today's brief isn't marked ready by `Time`, a nudge to its analyst (or, with no brief yet, whoever the brief rotation names), else to `Recipients`. |
| `Presenting:Enabled` | At `ReminderTime`, a reminder to whoever presents the brief the next business day (once per stretch). |
| `PersonalDigest:Enabled` | On by default: people may choose, on **My notifications**, a digest of their own actions, daily at `ReminderTime` on business days or weekly on `Digest:Day` at `Digest:Time`. Nothing goes until someone does. |
| `OutreachFollowUp:Enabled`, `Days` | At `ReminderTime` on business days, a reminder to whoever marked an Outreach action's email sent, once it has had no reply recorded for `Days` business days (1 to 30; 5 by default) and the action is still open. Once per email. |
| `Escalation:Enabled`, `Scope`, `Steps` | The escalation ladder: at `ReminderTime` on business days, each open action runs the step it has reached as it slips (remind its team, copy its escalation contact, raise its priority, flag it escalated), once per due date. Edited on Teams & SLA as a list of steps; set each team's escalation contact on its form first. |

Each thing is sent once. **Administration > Teams & SLA** shows each scan's last run and can run one now.

People turn off the kinds they don't want on **My notifications** (the account menu): due-soon and overdue
reminders, the team digest when it comes to members, outreach follow-ups, rotation reminders, and rotation requests and
notices. They can only turn off what an administrator has on. A team mailbox, the brief-not-ready fallback recipients and the
rotation's managers are addresses, not people, so they always get theirs; so does everyone the brief is sent to.
Teams & SLA shows how many people opted out of each kind, and who.

## Intake and CVE data

Both reach out to the internet, so both are off until an administrator turns them on in **Administration › Sources**,
which also sets how long feed items are kept, the shortest poll, and the CVE refresh interval. The proxy, the network
and size limits, the CVE addresses and the NVD key stay on the server. The keys below are the server's defaults:

| Key | What it does |
|---|---|
| `Intake:Enabled` | Polls each enabled source on its own interval. `Intake:Proxy` sets an outbound proxy (read at startup); `AllowPrivateNetworks` lets feeds and CVE mirrors on private addresses be fetched (loopback and link-local never are); `TimeoutSeconds`, `MaxResponseKB` and `MinPollMinutes` bound each request; `RetentionDays` is how long items nobody kept are retained; an item published longer ago than that isn't taken in, so a pruned item doesn't come back; `SkipSponsored` (on), `SponsoredMarkers` and `SponsoredExceptions` (semicolon-separated phrases) skip feed items that read as sponsored; `ReadingDays` (7, 1 to 30) is how long an article read in triage's reading pane is kept for colleagues and the editor (it's never sent or exported); `ReadSeconds` (6, 2 to 60) is how long reading an article's page may take before it's refused (raise it on a slow server); `PullNowMinutes` (15, 1 to 1440) is the analyst pull-now window, how soon after a source's last poll an analyst may pull it again from Triage's Sources tab; `GroupWindowHours` (48, 12 to 96) is how far apart two reports of one event can be for triage to group them, and `GroupIgnoredWords` (semicolon-separated) adds words triage's grouping ignores besides the built-in ones. An administrator changes all but the proxy, private-network and request limits on Sources. |
| `Cve:Enabled` | Refreshes CISA KEV and EPSS every `RefreshHours`, and NVD weekly, for the CVEs on articles and recent feed items. Put an NVD API key in the `Cve__NvdApiKey` environment variable for faster NVD lookups. `KevUrl`, `EpssUrl` and `NvdUrl` can point at an internal mirror. |

The server needs outbound HTTPS to each source, `www.cisa.gov`, `api.first.org` and `services.nvd.nist.gov` (or your
proxy or mirror). **Administration > Sources** shows each source's last poll and error, polls one by hand, and refreshes
the CVE data now.

## AI drafting help

The article editor's **Draft from the sources** is off until a provider is set (decision record 0004). With none,
analysts can still copy a prompt to run in an AI tool your organisation has approved, and paste the answer back.

| Key | What it does |
|---|---|
| `Ai:Provider` | `AzureOpenAI`, or blank for none. |
| `Ai:Endpoint` | The Azure OpenAI resource, `https://<name>.openai.azure.com`. TIDE calls its v1 Responses API (`/openai/v1/responses`). |
| `Ai:Deployment` | The model deployment's name. It must support structured outputs (for example `gpt-5-mini` or `gpt-4.1`). |
| `Ai:ApiKey` | Set it as the `Ai__ApiKey` environment variable, or in protected configuration, never in `appsettings.json`. |
| `Ai:MaxTlp` | The highest TLP ever sent: `Green` by default. Our notes on what we run count as AMBER, so they go with a draft only at `Amber` or above. |
| `Ai:WebGrounding` | `false` by default. When `true`, and TIDE can't fetch an article from a source marked Open, Azure's web search looks it up. Only the article's address and title are sent, and only for TLP:CLEAR articles. It uses Grounding with Bing, which is billed separately and handles data **outside** your Azure compliance and geographic boundary, so check it with whoever approves your data flows. |
| `Ai:TimeoutSeconds` | How long a draft may take (default 120). |

Requests go through the intake proxy (`Intake:Proxy`) if one is set, and ask Azure not to store them (`store: false`).
**Administration › Sources** sets, per source, whether articles may be fetched (TIDE only, Open, or Never), and an
optional CSS selector for the article body, with a Test.

## The API and webhooks

Nothing is open until an administrator issues a token under **Administration > API access** (decision record 0016).
- **Tokens.** Each has scopes, a TLP ceiling and an expiry, and is shown once. Give a SOAR server its own token, or
  let it call as its gMSA with Windows sign-in, held to the roles its AD groups give it.
- **Firewall.** The API is on the app's own address (`/api/v1`). Callers need HTTPS to it, as browsers do.
- **IIS authentication.** A token carries no Windows sign-in, so the installer (and the upgrade, on an older site)
  turns Anonymous on at `/api/v1` and leaves Windows Authentication on there too. IIS then passes every call to the
  app, which still refuses one with neither a token nor a Windows sign-in, and a gMSA signs in with Negotiate as before.
- **Webhooks** post from the TIDE server to an https address you add, so allow that outbound. A redirect isn't followed.
  The job runs every minute and posts an action raised or a brief sent once it's at least 30 seconds old.
- **Keys.** Webhook secrets are kept encrypted with the data protection keys (`DataProtection:KeyPath`). Back that
  folder up with the database: without it the secrets can't be read, and each webhook has to be added again.

## Metric imports

The opening metrics can be read from CSV instead of typed (ADR 0014). An administrator can upload a file on
**Administration > Metric imports** at any time. To have a scheduled export feed them, turn on the drop folder on the
same page (or set `Metrics:Import:DropFolderEnabled` to `true` as the server's default). TIDE then reads CSV files left
in `metrics-inbox` under the data root's `files` folder every few minutes (5 by default, set on the page). Each file it reads is moved to `processed`, or to
`rejected` if it was refused as a whole, and deleted from there after 90 days.

The data root grants access only to the app-pool account, so whichever account the export runs as also needs modify
access to `metrics-inbox`. Grant it on that folder alone, not on the data root, so it can't read screenshots. The page
shows the file format and what each import did.

## Logs

On IIS, TIDE's log goes to the Windows **Application** event log (source `.NET Runtime`). By default only warnings
and errors are written there; information-level entries go to the console, which IIS doesn't keep. Each entry lists
its category and event id, then its context: for a request, `TraceId`, `SpanId`, `RequestId` and `RequestPath`.
When something fails, the error page shows the reader a reference such as `00-4bf92f3577b34da6a3ce929d0e0e4736-...`;
search the event log for the 32-character part after `00-` (the `TraceId`) to find the matching error. When an action
fails unexpectedly, its message ends with a shorter reference, such as `(reference 9C41F07A)`: search for that, and the
`Tide.Errors` entry with that reference names who it happened to and holds the full exception. An error inside a page
(the "Something went wrong on this page" card) has no reference; it's logged as `Unhandled exception rendering
component`, so look for it by time.

To keep more, set the level for the event log alone in `appsettings.Production.json`, so the console's stays as it is:

```json
"Logging": {
  "EventLog": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning",
      "Microsoft.EntityFrameworkCore.Database.Command": "Warning"
    }
  }
}
```

Keep `Microsoft.EntityFrameworkCore.Database.Command` at `Warning`: below that every SQL statement is logged. TIDE never
logs parameter values, passwords, email addresses or subjects. Recycle the app pool after changing the file.

## 6. Back up

Back up the database and the data root together. Screenshots and the files on actions and their comments are files
under `files`, so a database backup alone loses them, and `dp-keys` keeps sign-ins and open forms valid after a restore. The database is in the FULL recovery
model, so its transaction log is cleared only by log backups: without them it grows until the disk is full.

**If your organization's backup product backs up SQL Server and file shares**, use it: the database (with log backups)
and the whole data root, on the same schedule, and nothing below.

**Otherwise**, `Backup-Tide.ps1` (in `deploy`) backs TIDE up as one set, and `Test-TideRestore.ps1` proves a set
restores. Run both as an account that can back up and restore the database and read the data root: sysadmin, or
`db_backupoperator` in TIDE's database plus `dbcreator` on the server (checking a backup with `RESTORE VERIFYONLY`, and
the drill's restore, need `CREATE DATABASE`) and `SELECT` on `msdb.dbo.backupset`. `Install-Database.ps1
-BackupAccount <account>` (or `BackupAccount` in the answers file) grants exactly those: give it the gMSA the tasks run
as, or, for tasks run as SYSTEM, the web server's computer account (`CONTOSO\WEB01$`). SQL Server writes the `.bak`
itself, so its service account needs write access to the destination too. Each site's tasks are named for it (`TIDE
nightly backup (tide)`), so two sites on one server keep their own. A gMSA given as `-TaskAccount` is given read on the
site's `<site folder>-deploy` folder the tasks run from (kept through upgrades) and on the screenshots and key ring
folders (kept when `Install-Tide.ps1` is run again to repair the install).

```powershell
# A nightly set at 01:30 and a log backup every 15 minutes, as scheduled tasks (SYSTEM, or a gMSA with -TaskAccount):
.\deploy\Backup-Tide.ps1 -SitePath D:\inetpub\tide -Destination \\backup01\tide -Register
# The restore drill every fourth Sunday:
.\deploy\Test-TideRestore.ps1 -SitePath D:\inetpub\tide -Destination \\backup01\tide -Register
```

Each set is a folder `TIDE-<server>-<site folder>-<yyyyMMdd-HHmm>` holding the database (`db\`, a full backup with checksums, read back with
`RESTORE VERIFYONLY`), the screenshots and the key ring (`files\`), the site's `appsettings.Production.json`
(`files\site\`) and `backup-set.json`, which lists them and names the server and site. The newest 14 of this install's
sets are kept (`-Keep`), so production and a test install can share one destination: neither prunes the other's sets,
and the restore drill takes only its own. Log backups go to `log\<server>-<site folder>\`.
A failed run removes its unfinished set and exits with code 1, so the scheduled task shows it failed. Beside another
product, add `-CopyOnly` (and no `-Log`) so its backup chain isn't disturbed. The set holds the key ring: lock the
destination down as tightly as the data root. The tasks run the copy of the scripts kept beside the site
(`<site folder>-deploy`), which each upgrade refreshes.

`Test-TideRestore.ps1` restores the newest set (or `-BackupSet`) into a scratch database (`Tide_RestoreDrill_<server>_<site folder>`, on
`-SqlInstance` if you want to test a recovery server), runs `DBCC CHECKDB`, checks every screenshot the database
lists is in the set at its recorded size and that a sample of 200 (`-HashAll` for all) match their SHA-256, then drops
the scratch database. Its findings are saved in the set as `restore-check-<time>.json`, and a failure exits with code 2.

To restore for real: restore the `.bak` (and the log backups after it) over the database, copy `files\files` and
`files\dp-keys` back to the data root's `files` and `dp-keys`, and start the app pool.

`Upgrade-Tide.ps1` takes its own `COPY_ONLY` database backup and a snapshot of the site before every upgrade
([UPGRADE.md](UPGRADE.md#upgrading-with-upgrade-tideps1)), so it doesn't disturb the scheduled ones.

## Troubleshooting

| You see | What it means |
|---|---|
| **500.30** or **500.37** | The app didn't start. The reason is in the Application event log. Common causes are a wrong connection string, the app-pool account lacking its SQL login, or `Org:TimeZone` not being a zone the server knows. |
| **401** for everyone | Windows Authentication isn't on for the site, or the browser isn't sending credentials. Add the host to the Local Intranet zone. |
| **"You don't have access"** | Signed in, but the account is in no mapped group. Map a group in Administration, or in `RoleMapping` before the first start. |
| **"The mail relay couldn't be reached"** | `Email:SmtpHost` or `SmtpPort` is wrong, or a firewall blocks the server from the relay. After three failures in two minutes TIDE waits a minute (then longer) before trying again. |
| **Typing lags; the Network tab shows many `xhr` rows for `_blazor?id=`** | Long polling: WebSockets aren't getting through. Install the IIS WebSocket Protocol feature (`Install-WindowsFeature Web-WebSockets`, or re-run `Install-Tide.ps1 -InstallWebSockets`) and check any reverse proxy or load balancer passes the `Upgrade` and `Connection` headers. `Verify-Install.ps1` and Administration > System > Diagnostics say which it is. |
| **"The mail relay refused the sign-in"** | Check `Email:SmtpUsername` and `Email__SmtpPassword`, or that the relay accepts the app-pool account. |
