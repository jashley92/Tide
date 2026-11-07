# Glossary

Every domain term TIDE uses, what it means, and its name in the code, so a word on a screen can be found in the
source and a class name can be understood on a screen. It's for anyone reading TIDE's pages, docs or code. Terms are
in alphabetical order; *italic* words inside a definition have their own entry.

Code names are C# types and members (`Namespace` folders under `src/`). An entity named here is also an EF Core table;
the tables and columns are in [data model](../architecture/data-model.md).

**Jump to:** [A](#a) · [B](#b) · [C](#c) · [D](#d) · [E](#e) · [F](#f) · [G](#g) · [H](#h) · [I](#i) · [K](#k) ·
[L](#l) · [M](#m) · [N](#n) · [O](#o) · [P](#p) · [R](#r) · [S](#s) · [T](#t) · [U](#u) · [V](#v) · [W](#w)

## A

| Term | Meaning | Code name |
|---|---|---|
| **Action** | A piece of follow-up work from the brief: owned by a *team*, optionally assigned to a person, due by an *SLA* date, and closed with an *outcome*. Numbered "A-12". | `ActionItem`, `ActionService`; label `Ui.ActionLabel` |
| **Action kind** | What sort of follow-up an action is. IOC sweep (search logs for an article's indicators), Threat hunt (hypothesis-led, beyond known indicators), Block, Patch / remediate, Config change, Awareness (tell people), *Outreach* (ask someone), Other. The kind decides which outcomes fit and can override the SLA. | `ActionKind`: `IocSweep`, `ThreatHunt`, `Block`, `PatchRemediate`, `ConfigChange`, `Awareness`, `Outreach`, `Other` |
| **Action owner** | The role for another team's member: reads the team edition and works the actions given to their team. Also used loosely for anyone who works actions. | `AppRole.ActionOwner` |
| **Action status** | Where an action stands: Open, In progress, Blocked, *Tracking*, Done, Cancelled, Not applicable. | `ActionStatus` |
| **Active limit** | A team's optional cap on its active actions (Open, In progress, Blocked; not Tracking). At it, a change is only counted (Show), asks why (Warn) or is refused (Refuse). Shown as "4 of 5 active". | `Team.ActiveLimit`, `ActiveLimitMode`, `ActiveLimits` |
| **Admiralty grade** | How far a *source* is trusted, on the Admiralty (NATO) source-reliability scale: A completely reliable, B usually, C fairly, D not usually, E unreliable, F can't be judged. TIDE grades reliability only, not information credibility. | `SourceReliability` (`A` to `F`), `Source.Reliability` |
| **Adversary profile** | One threat actor or campaign, under every name it goes by (Scattered Spider, UNC3944, Octo Tempest), with a MITRE id, motivation, sectors and internal notes. Its page is a timeline of articles tagged with it or naming it. | `Adversary` (`AdversaryKind`: `Actor`, `Campaign`), `AdversaryService` |
| **AI draft** | A first draft of an article's fields suggested by an AI provider (*Draft from the sources*), recorded with its provider, model, prompt version and a hash of what was sent. Opt-in and held to `Ai:MaxTlp`. | `AiDraft`, `AssistService`, `IThreatIntelAssistant`; [ADR 0004](../decisions/0004-opt-in-ai-with-tlp-ceiling.md) |
| **Analyst** | The role for the CTI team: triages, writes, presents and sends the brief, and works any action. | `AppRole.Analyst` |
| **API token** | A credential another tool uses to call `/api/v1`: scoped, expiring, with its own TLP ceiling, shown once and stored as a hash. | `ApiToken`, `src/Tide.Application/Api/ApiTokens.cs`; [ADR 0016](../decisions/0016-api-tokens-scopes-and-signed-webhooks.md) |
| **Article** | One threat or item of news written up for the brief: title, source, *BLUF*, bullets, *why it matters*, recommended actions, screenshots, indicators, CVEs, ATT&CK techniques, tags, and a TLP on the article and on each part. | `Article`, `ArticleService`, `ArticleForm` |
| **Article status** | Draft (being written), Ready (ready to brief), Presented (briefed at a meeting). | `ArticleStatus` |
| **Assessment** | The Articles list's filter for threat, P1 or P2, or informational. | `ArticleAssessment` |
| **ATT&CK technique** | A MITRE ATT&CK Enterprise technique tagged on an article, picked from the catalog embedded in TIDE. Rolled up to tactics in Trends. | `ArticleTechnique`, `src/Tide.Application/Mitre/`, `MitreTactic` |
| **Attach** | In triage: keep an item as another report of a recent draft or ready article instead of making a new one. Its CVEs go to the article, and its address becomes one of the article's *links*. | `IntakeService.AttachAsync`, `AttachTargetsAsync`, `ArticleLink` |
| **Attendee** | Someone recorded as being at a brief's meeting, as they join or added by the presenter. | `MeetingAttendee` |
| **Audit log** | Every change saved across TIDE: who, when, what, and each field before and after, written in the same save. Administrators read all of it; analysts see an article's or brief's *History*. | `AuditLogEntry`, `AuditInterceptor`, `AuditLogService` |

## B

| Term | Meaning | Code name |
|---|---|---|
| **BLUF** | "Bottom line up front": the one or two sentences at the top of an article that say what happened and what it means. Shown as "Bottom line". | `Article.Bluf` |
| **Board** | The Actions page drawn as columns by status, with drag and drop. The other layout is the list; each person's choice is saved. | `ActionsBoard`, `ActionKanban`; preference `actions.layout` |
| **Board scorecard** | See *scorecard*. | |
| **Brief** | One business day's set of articles, in a *running order*, presented at the meeting and sent as two *editions*. One per day. | `Brief`, `BriefService` |
| **Brief archive** | The team's list of every day's brief and its status. Not the *Reader archive*. | `/briefs`, `BriefsArchive` |
| **Brief day** | The day a brief is for: today if it's a working day, else the next working day. | `OrgClock.CurrentBriefDate` |
| **Brief status** | Draft, Ready, Presented, Sent. A brief finished as distributed or not sent is Sent too. | `BriefStatus` |
| **Brief steps** | Today's five steps: Triage, Write, Order, Present, Send (Distribute when email is off). | `BriefSteps`, `StepStrip` |

## C

| Term | Meaning | Code name |
|---|---|---|
| **Card rule** | An administrator's rule that colours and labels actions on the board and list, and may be offered as a filter on the Actions page. Everyone who reads actions reads them on **Board rules**. | `ActionCardRule`, `CardColour`, `CardRuleSla` |
| **Carry-over** | Articles on a brief that weren't discussed at the meeting go on the next business day's brief when the meeting ends. Reopening the meeting takes them back. | `BriefService.MarkPresentedAsync` (`carry`), `BriefItem.CarriedFromBriefId` |
| **Ceiling** | The highest TLP an edition, an API token, an export, an outreach template or the AI provider may carry. Anything above it is left out whole, never hidden. The defaults are AMBER+STRICT for the team edition and GREEN for the company-wide edition. A ceiling governs what leaves TIDE, not what people see in it. | `DistributionSettings` (`Distribution:TeamMaxTlp`, `PublicMaxTlp`), `BriefSend.Ceiling`, `Ai:MaxTlp` |
| **Change feed** | The in-memory notice board that tells open pages something they show was saved, so they update live. | `ChangeFeed`, `ChangeInterceptor`, `LiveChanges` |
| **Classification** | Whether an article asks something of the organization: a **Threat** (with a *priority*) or **Informational**. | `Classification` |
| **Company-wide edition** | The edition for the whole company: email to everyone, the SharePoint copy, and the *Reader archive*. Carries only what's at or below its ceiling (GREEN by default), and never indicators, actions, metrics or the team note. Called "public" in code. | `Edition.Public` |
| **Configuration bundle** | The app's in-app setup (settings, templates, teams and the like) as a file to copy to another TIDE, plus the install answers file. Carries no secrets. | `ConfigBundleService`, `/admin/system/bundle` |
| **Correction** | A short email of its own, sent after a brief went out, to the people who had the original: a required note on what changed and a diff of each article as sent against now. Frozen and logged like a *send*. | `BriefCorrection`, `CorrectionService`, `CorrectionRenderer`; [ADR 0015](../decisions/0015-corrections-after-send.md) |
| **Cover** | A *rotation change* that gives the requester's brief days to a colleague. | `RotationChangeKind.Cover` |
| **Coverage** | The page that compares the ATT&CK techniques briefed in the last 90 days with the SOC's *detection rules*. A technique is covered when a current, enabled rule is tagged with it, its parent or a sub-technique. "Covered" means tagged, not proven to fire. | `CoveragePage`, `DetectionService`, `DetectionGap`; [ADR 0017](../decisions/0017-detection-rules-are-referenced-and-tagged.md) |
| **CVE record** | What TIDE knows about one CVE: CVSS from NVD, EPSS from FIRST, and the KEV flag and dates, refreshed in the background. Shown as a CVE chip. | `CveRecord`, `CveService`, `CveChip` |
| **CVSS** | Common Vulnerability Scoring System: NVD's 0 to 10 severity score for a CVE, with its severity and vector. | `CveRecord.CvssScore`, `CvssSeverity`, `CvssVector` |

## D

| Term | Meaning | Code name |
|---|---|---|
| **Defang / refang** | Writing an indicator so it can't be clicked (`hxxp://evil[.]com`) / turning it back. TIDE stores indicators live and shows them defanged. | `IocObservable` (`src/Tide.Domain/Observables/`) |
| **Detection rule** | A reference to one of the SOC's rules: name, id, technique tags, platform, enabled. Imported from Sentinel, Sigma or CSV, or pushed through the API. Its query is never read, stored or run. | `DetectionRule`, `DetectionSource` (`Sentinel`, `Sigma`, `Csv`) |
| **Dialect** | One SIEM's hunt query language and how TIDE writes a query in it: XQL (Cortex XSIAM), KQL (Sentinel / Defender), CrowdStrike. An administrator's *query template* can override a block. | `IQueryDialect`, `HuntQueryBuilder`, `QueryTemplate`; [ADR 0006](../decisions/0006-query-dialects-are-templates.md) |
| **Discussed** | A brief item ticked off in the meeting. Items not discussed are *carried over*. | `BriefItem.Discussed` |
| **Dismiss** | In triage: set an item aside, with an optional reason. Dismissed items are kept for a while and can be put back. | `FeedItemStatus.Dismissed`, `IntakeService.DismissAsync` |
| **Distribute** | Finishing a brief when email is off: an analyst marks it distributed once it went out another way. Its numbers lock but nothing is frozen or published. | `Brief.DistributedAtUtc`, `DistributionService.MarkDistributedAsync`; API `finishedAs: "distributed"` |
| **Drop folder** | A server folder where CSV files of metric values are left for TIDE to import on a schedule. | `MetricDropFolder`, `MetricImportHostedService`, `MetricImportOrigin.DropFolder` |
| **Duplicate** | In triage: an item marked as the same story as an existing article or item. | `FeedItemStatus.Duplicate`, `FeedItem.DuplicateOfId` |

## E

| Term | Meaning | Code name |
|---|---|---|
| **Escalated** | An open action a step of the escalation ladder flagged for the due date it still has: a red badge, Today's Escalated card, first in the meeting's overdue list. Closing it or moving its date ends it. | `ActionItem.EscalatedForDue`, `IsEscalated`, `ActionQueries.WhereEscalated`, `EscalatedBadge`, `ActionService.EscalatedAsync` |
| **Escalation contact** | Who the escalation ladder copies when a team's actions slip: a name and email on a team, internal or external (a manager, a duty lead). An internal team's is set by an administrator, an external team's by whoever works actions. | `Team.EscalationName`, `Team.EscalationEmail`, `TeamService.SetEscalationContactAsync`; the `escalation` email template |
| **Escalation ladder** | An administrator's short list of steps that run by themselves on an open action as it slips: each a number of business days before, on or after the due date, and what it does (remind the team, copy the escalation contact, raise priority, flag escalated). Once per action and due date, in the reminder sweep. | `EscalationLadder`, `EscalationStep`, `EscalationOptions`, `ReminderService.EscalateAsync`; [ADR 0021](../decisions/0021-escalation-is-an-administered-ladder.md) |
| **External team** | A team tracked in TIDE that works elsewhere: no members or assignees, kept by anyone who works actions, and emailed about its actions due soon or overdue at its contact address, with no TIDE links. | `Team.IsExternal`, `TeamService.AddExternalAsync`, `ExternalCadence`; [ADR 0020](../decisions/0020-external-teams-are-emailed-without-tide.md) |
| **Earlier note** | What an article's old free-text Threat actors box held, kept after the field became an *adversary* picker. Names a profile goes by are offered as one-click tags; the rest stays visible until cleared. | `Article.Actors`, `AdversaryService.ForArticleAsync` |
| **Edit version** | A concurrency token on articles, briefs, comments, saved queries and templates. A save from a stale copy is refused. | `EditVersion`, `StaleEditException` |
| **Edition** | One audience's cut of a brief: the *team edition* or the *company-wide edition*. Both are built from the same brief by TLP, in one place. | `Edition`, `BriefEdition`, `BriefEditions.Build`; [ADR 0005](../decisions/0005-editions-from-one-model.md) |
| **Edition check** | Today's panel saying what each edition will carry and what it leaves out. | `EditionCheck` |
| **EPSS** | Exploit Prediction Scoring System: FIRST's daily probability that a CVE will be exploited in the next 30 days, with its percentile. A high EPSS suggests (never sets) a priority. | `CveRecord.EpssScore`, `EpssPercentile` |

## F

| Term | Meaning | Code name |
|---|---|---|
| **Feed item** | See *triage item*. | `FeedItem` |
| **File risk** | TIDE's judgement of an uploaded file from its name, declared type and first bytes. A file that can run downloads in a zip with the password `infected`; an image whose bytes don't match isn't shown inline. | `FileRisk`, `ActionFile.RiskReason`, `TypeNote`; setting `Files:ZipDownloads` |
| **Filter bar** | The bar above every filtered list: its search box, *pills* (one-click filters, on or off), a *chip* for each other filter in force (choose it to change it, ✕ to remove it), the **+ Filter** menu for the rest, the count and Clear. The filters stay in the address. | `FilterBar`, `FilterPill`, `FilterDef`, `FilterValues` |
| **Freeze** | What a *send* does before the relay is asked: keep a copy of exactly what went (the email HTML, a semantic web copy, and copies of its images), so the record never changes when the article does. | `BriefSend.EmailHtml`, `WebHtml`, `BriefSendImage`; [ADR 0007](../decisions/0007-frozen-sends-and-cid-email.md) |

## G

| Term | Meaning | Code name |
|---|---|---|
| **Group (triage)** | "Looks like one event": triage items from two or more feeds that share a CVE or distinctive headline names and amounts, suggested as one line. Worked out each time the inbox loads; nothing is stored or merged without a click. | `TriageGroups.Find`, `InboxRow.Group`, `IntakeService.PromoteAsOneAsync` |
| **Group (metrics)** | See *metric group*. | |

## H

| Term | Meaning | Code name |
|---|---|---|
| **Hand-off** | Giving an action to another team, with a short reason kept in its history and shown on it ("Handed from … by …: …"). The receiving team is emailed unless it's external. | `HandoffService.HandOffAsync`, `ActionItem.HandedFromTeamId` |
| **History** | The readable record of changes on one article or brief (from the audit log), or on one action (its own events). | `HistoryService`, `HistoryPanel`; `ActionEvent` |
| **Holiday** | A dated day off, kept by an administrator. It counts as a weekend does for every rule that counts days. | `Holiday`, `HolidayService`, `WorkingDays`; [ADR 0018](../decisions/0018-holidays-are-days-off-like-weekends.md) |
| **Hunt (hunt query)** | A query on an action, generated from indicators, written by hand, or added from a *saved query*, that the analyst runs in their own SIEM. Its result (not run, no hits, hits) and an optional screenshot are recorded. | `HuntQuery`, `HuntResult`, `HuntQueryBuilder` |
| **Hunt value kind** | The kind of value a hunt block searches: an indicator type, with IPv4 and IPv6, and MD5, SHA-1, SHA-256 and SHA-512, split apart because SIEMs keep them in different fields. | `HuntValueKind`, `HuntValues` |

## I

| Term | Meaning | Code name |
|---|---|---|
| **Indicator (IOC)** | An observable on an article or action: IP address, domain, URL, file hash, file name, email address, host, account, process, registry key, command line, crypto wallet. Stored live, shown defanged. | `ArticleIndicator`, `ActionIndicator`, `IndicatorType` |
| **Indicator finder** | Pulls indicators and CVE ids out of pasted text, defanged or not, for the analyst to tick. | `IocExtractor` |
| **Informational** | An article that's interesting but asks nothing of the organization. | `Classification.Informational` |
| **Intake** | Polling the *sources* into the triage inbox, in the background, only when an administrator has turned it on. | `IntakeService`, `IntakeHostedService`; [ADR 0009](../decisions/0009-intake-reaches-out-only-when-asked.md) |
| **Intro (title slide line)** | An optional one-line note on the brief's title slide, with its own TLP. Not the *team note*. | `Brief.Intro`, `Brief.IntroTlp` |

## K

| Term | Meaning | Code name |
|---|---|---|
| **KEV** | CISA's Known Exploited Vulnerabilities catalog. TIDE polls it as a source (each newly listed CVE is a triage item) and flags every CVE record on it, with the date added, the due date and whether it's used in ransomware. | `SourceKind.CisaKev`, `CveRecord.IsKev`, `KevAddedDate`, `KevDueDate`, `KevRansomware` |

## L

| Term | Meaning | Code name |
|---|---|---|
| **Lineage** | Where a split action came from and went: its article (with its CVEs), the parent action and each part, drawn left to right on the parent's and each part's page and coloured by how each part stands. | `ActionLineage`, `LineageRules`, `ActionLineageFlow` |
| **Link (report)** | Another report of the same event kept on an article, from *attach*, *promote as one*, or added by hand. Counts as the article's link for coverage. | `ArticleLink` |
| **Live meeting** | A brief's meeting while the presenter runs it in present mode: who presents, their slide, who's on which slide, raised hands. In memory, one per brief day. | `LiveMeetings`, `IMeetingsInProgress` |

## M

| Term | Meaning | Code name |
|---|---|---|
| **Meeting note** | A note taken in the meeting by one person, against the article on screen as they began it. Each is its author's own. | `MeetingNote`, `MeetingService` |
| **Metric** | One opening number, with whether higher is better, worse or neutral, a format, and optional warning and critical thresholds. One value per metric per brief date. | `MetricDefinition`, `MetricValue`, `MetricDirection`, `MetricFormat`, `MetricLevel` (`Normal`, `Warn`, `Critical`) |
| **Metric group** | A set of metrics shown together: one slide in the meeting, with the chart an administrator picks for it. | `MetricGroup`, `MetricChart` |
| **Metric import** | One read of a CSV of metric values, by upload or from the *drop folder*. Never replaces a typed value or touches a sent day. | `MetricImport`, `MetricImportService`, `MetricCsv`; [ADR 0014](../decisions/0014-metric-values-from-csv.md) |

## N

| Term | Meaning | Code name |
|---|---|---|
| **Next line** | On an action's page, the one thing that most needs doing (a late part, an email past its follow-up, escalated, blocked, late), with links that do it. | `ActionGuide.Next`, `ActionNextStrip` |
| **Not sent** | Finishing a brief that won't go, when email is on, with a required reason. It counts as Sent but nothing is frozen or published. | `Brief.NotSentReason`, `DistributionService.FinishNotSentAsync`; API `finishedAs: "notSent"` |
| **Notification mark** | A record that one reminder email went, so a scan that stops partway never sends it twice. | `NotificationMark` |

## O

| Term | Meaning | Code name |
|---|---|---|
| **Opening metrics** | The operational numbers a team opens the brief with. See *metric*. | `MetricsService`, `MetricEntry` |
| **Operational setting** | A setting changed in the app and audited (time zone, meeting time, reminders, polling and the like), laid over the server's value. Security-sensitive and infrastructure settings stay on the server. | `OperationalSettings`, `SettingsCatalog.InApp`, `SavedSettings` |
| **Our stack** | The products the organization runs, kept by analysts, with versions, owning team, notes and a vendor contact. An article is tagged with them under *We run*. | `StackProduct`, `ArticleProduct`, `StackService` |
| **Outcome** | What an action found or achieved when closed: No hits, Hits escalated, Remediated, Blocked, Risk accepted, Not affected, Answered, Other. Which ones are offered depends on the *action kind*. | `ActionOutcome`, `ActionService.KindOutcomes` |
| **Outreach** | An action kind that asks a vendor, partner, ISAC, internal team or site owner for information. TIDE writes the email from a template, held to its ceiling; the analyst sends it from their own mail. Was "Vendor inquiry". | `ActionKind.Outreach`, `OutreachTemplate`, `OutreachMessage`, `OutreachService`, `OutreachFill`; [ADR 0019](../decisions/0019-outreach-emails-are-written-never-sent.md) |

## P

| Term | Meaning | Code name |
|---|---|---|
| **Period** | A week, month or quarter that Trends and Reports count by ("2026-W44", "2026-10", "2026-Q4"). | `Period`, `PeriodKind` |
| **Permission** | One thing a role allows: read published, read the team edition, write articles, present briefs, work actions, read actions, enter metrics, meeting notes, join meetings, administer. Each has a policy of the same name. | `Permission`, `Policies`; [ADR 0002](../decisions/0002-permissions-as-code.md) |
| **Personal digest** | One person's own email of their open actions, daily or weekly, chosen on My notifications. | `ReminderKind.PersonalDigest`, preference `notify.digest` |
| **Playbook** | A checklist for a kind of action, offered when one is created or worked. The team's library, kept by whoever works actions. | `Playbook`, `PlaybookStep`, `ActionChecklistItem` |
| **Presence** | Who else has a brief, article, action or triage item open, and whether they're editing. In memory. | `Presence`, `PresenceNote`, `PresenceCircuit` |
| **Presenter** | The analyst who presents a day's brief: the rotation's choice for the day (an *override*, else the week's analyst), recorded on the brief. Presenting can be handed to another analyst in the meeting. | `IPresenterLookup`, `Brief.AnalystName` |
| **Primary button** | The one button an action's page offers first, chosen by where the action stands and its kind (Write the email, Record the reply, Record an update, Take it, Start work, Unblock, Close with outcome); everything else is under *More*. | `ActionGuide.Primary`, `ActionFlow` |
| **Priority** | How urgent a threat is, P1 (most urgent) to P4. An action raised in the meeting takes its article's priority. | `Priority` |
| **Promote** | In triage: turn an item into a draft article, claimed in one statement so two analysts can't make two. *Promote as one article* does it for a whole *group*. | `IntakeService.PromoteAsync`, `PromoteAsOneAsync`, `FeedItemStatus.Promoted` |

## R

| Term | Meaning | Code name |
|---|---|---|
| **Reader** | The role for anyone else in the company: reads the published company-wide archive only. | `AppRole.Reader` |
| **Reader archive (Published briefs)** | The sent company-wide editions, shown exactly as frozen, and nothing else. A send withdrawn by a *TLP slip* isn't shown. | `/published`, `Published`, `DistributionService.Published`, `PublishedContent` |
| **Readiness checks** | What's missing on an article before the meeting, and the warning when a threat's "why it matters" would reach the company-wide edition, with a one-click fix. | `ArticleChecks.For`, `ArticleChecks.WhyReachesCompany`, `ArticleFix` |
| **Reading pane** | Triage's pane that shows the selected item's article text, fetched once and kept for `Intake:ReadingDays` (7). For reading inside TIDE only. | `FeedItemText` |
| **Record an update** | Recording on an action what its team said outside TIDE (an emailed reply): the reply kept as a comment, the status, a new due date and why, and a note, in one history line. | `ActionService.RecordUpdateAsync`, `TeamUpdate`, `RecordUpdateDialog` |
| **Remediation timeline** | A parent action's parts on one date scale: a bar each from creation to due date, the run past due, the day each closed, and today. | `RemediationTimeline`, `RemediationTimelineCard` |
| **Reminder** | A scheduled email: due soon, overdue, the weekly team digest, the personal digest, brief not ready, presenting next, outreach follow-up. Each is off until enabled. | `ReminderService`, `ReminderKind`, `ReminderHostedService` |
| **Review** | An optional check of an article by a colleague before it goes: comments on parts, a checklist, Approve or Ask for changes. An administrator can require it for AMBER and above, threats, or both. | `ArticleReview`, `ReviewComment`, `ReviewStatus` (`Asked`, `Approved`, `ChangesAsked`), `ReviewService` |
| **Role** | A fixed bundle of permissions granted by AD groups: Analyst, Action owner, Reader, Admin, Viewer, Visitor. | `AppRole`, `RoleDefinitions`, `AdGroupRoleMapping` |
| **Rotation (brief rotation)** | Who presents the brief each business day: an ordered roster, a weekly rota, and day-level overrides from approved changes. Not an on-call schedule. | `RotationService`, `RotationMember`, `RotationWeek`, `RotationOverride`; [ADR 0010](../decisions/0010-rotation-changes-approved-by-the-colleague.md) |
| **Rotation change** | A request to *swap* or get *cover*, approved only by the colleague taking the days; the managers are told of every approved one. | `RotationChange`, `RotationChangeKind`, `RotationChangeStatus` (`Pending`, `Approved`, `Declined`, `Cancelled`) |
| **Running order** | The order of the articles on a brief, dragged or moved up and down on Today. The slides and emails follow it. | `BriefItem.SortOrder`, `BriefService.ReorderAsync`, `RunningOrder` |

## S

| Term | Meaning | Code name |
|---|---|---|
| **Saved query** | A hunt query kept in the team's library, by language, with `{{days}}` and `{{values}}` placeholders, to add to any action. | `SavedQuery`, `SavedQueryService`, `/hunts/saved` |
| **Scorecard** | The board's taxonomy of what the team reports on: categories and tags articles are counted by in Trends. Tags are retired, never deleted, once used. | `ScorecardCategory`, `ScorecardTag`, `ScorecardService` |
| **Seat** | A place in a live meeting. A *Visitor* reads team-edition content only from a seat, only for that meeting's brief, and only while it runs. | `LiveMeetings.Join`, `MeetingAccess` |
| **Send** | Emailing one edition of a brief to its recipient list, after a confirmation showing the marking and every recipient. It's frozen first; only a send the relay took marks the brief Sent. A test send goes to the sender only and isn't frozen. | `BriefSend`, `SendStatus` (`Sending`, `Sent`, `Failed`, `Unknown`), `DistributionService`, `SendPlan`, `BriefSend.IsTest` |
| **SharePoint copy** | The company-wide edition as clean HTML plus a zip of its images, to paste into a SharePoint page. Can be required to be marked posted before a brief is finished. | `SharePointPage`, `Brief.SharePointPostedAtUtc`, `Brief.SharePointRequired` |
| **Shortlist** | In triage: keep an item for later in the day without promoting it yet. | `FeedItemStatus.Shortlisted` |
| **SLA** | The target for closing an action, in business days per priority (P1 1, P2 3, P3 10, P4 20 by default), optionally per kind. It suggests the due date; an action is on track, due soon (today or the next business day), overdue, met or missed. | `SlaOptions`, `SlaSettings`, `ActionSla`, `SlaState` |
| **Source** | Where triage items come from: an RSS or Atom feed, the CISA KEV catalog, or another system through the API. Each has a default TLP, an *Admiralty grade*, a polling interval, a handling note, filters and a fetch setting. | `Source`, `SourceKind` (`Feed`, `CisaKev`, `Api`), `ArticleFetch` |
| **Source health** | How much each source sends and how much the team uses, with feeds worth pausing. | `/admin/source-health` |
| **Split** | "Not the same": breaking up a suggested triage *group* for everyone, with Undo. For actions, *Split to teams* raises a part of an action for each of several teams at once (`HandoffService.SplitAsync`). | `TriageSplit`, `IntakeService.SplitAsync`, `UndoSplitAsync` |
| **Sponsored filter** | Leaves out feed items that read as sponsored, by marker phrases matched as whole words, with exceptions ("state-sponsored") and states named as sponsors kept. On by default; a skipped item counts as filtered out. | `SponsoredFilter`, `Intake:SkipSponsored`, `Intake:SponsoredMarkers`, `Intake:SponsoredExceptions` |
| **STIX** | Structured Threat Information Expression 2.1: the JSON format of TIDE's indicator export, a bundle of reports, indicators, vulnerabilities and attack patterns, each with its TLP 2.0 marking definition and a stable id. | `Stix.Write`, `ExportService`; [ADR 0012](../decisions/0012-exports-carry-tlp-and-stable-ids.md) |
| **Story** | A thread linking articles across days into a timeline: a campaign, a vulnerability being exploited, an actor followed over weeks. Active, Monitoring or Closed. | `Story`, `StoryStatus`, `Article.StoryId`, `StoryService` |

## T

| Term | Meaning | Code name |
|---|---|---|
| **Team** | A group that owns actions, with members, an optional mailbox and SLA targets. Being on the team is what lets an Action owner work its actions. | `Team` (`Team.Email` is the mailbox), `TeamMember`, `TeamService`; [ADR 0008](../decisions/0008-teams-own-actions.md) |
| **Team commitments** | The scorecard of each team against the due dates on its actions over the last 30, 90, 180 or 365 days: actions due, the share done on time, average days late, open and overdue now. An action counts in the window its due date falls in. | `TeamScorecardService`, `TeamScore`, `/reports/teams` |
| **Team edition** | The edition for the security teams: everything up to its ceiling (AMBER+STRICT by default), with the day's numbers, the team note, indicators and the actions raised. | `Edition.Team` |
| **Team note (opening note)** | The team's note at the start of the meeting (what's coming up, the week's phishing trends), in Markdown. Team edition only; a new brief starts with the previous one's. | `Brief.TeamNote`, `BriefService.SetTeamNoteAsync` |
| **Team queue** | On Today, a person's teams' open actions that are theirs or nobody's, theirs first, by due date, with *Take it*. | `TeamQueue`, `ActionService.TeamQueueAsync` |
| **Tool drawer** | On an action's page, the card of tabs beside the work holding its checklist, parts' timeline, hunt queries and outreach, opening on the kind's own (or `?tool=`). | `ActionToolDrawer`, `ActionGuide.Tools`, `ActionTool` |
| **Tracking** | An action status: handed to others and followed. Open (its due date and SLA apply) but not active, so not counted against its team's limit. | `ActionStatus.Tracking` |
| **Threat** | An article that asks something of the organization, with a *priority*. Its "why it matters" starts at AMBER. | `Classification.Threat` |
| **TLP 2.0** | The Traffic Light Protocol, version 2.0 (FIRST), on every article and part. **CLEAR**: no limit. **GREEN**: the community, not public. **AMBER**: the organization and its clients, need to know. **AMBER+STRICT**: the organization only. **RED**: the named recipients only. A part never counts lower than its article. | `TlpLevel` (`Clear`, `Green`, `Amber`, `AmberStrict`, `Red`), `Article.Effective`, `Article.HighestTlp` |
| **TLP marking** | The highest TLP anything in an edition, email or export carries: what it's marked as. | `BriefEdition` (highest marking), `BriefSend.Marking` |
| **TLP slip** | A *correction* for something that went to an audience it shouldn't have. It tells recipients to delete the email, repeats nothing, and withdraws that day's company-wide sends made before it from the Reader archive. | `BriefCorrection.IsTlpSlip` |
| **Trends** | Month-on-month or quarter-on-quarter counts of scorecard tags, ATT&CK tactics and techniques, and actions. An article counts once, in the period of its first brief. | `TrendsService`, `TrendRow`; [ADR 0013](../decisions/0013-trends-count-articles-by-first-brief.md) |
| **Triage item** | One item from a source in the triage inbox, stored once by its canonical address: New, Shortlisted, Dismissed, Duplicate or Promoted. | `FeedItem`, `FeedItemStatus`, `UrlCanonicalizer` |

## U

| Term | Meaning | Code name |
|---|---|---|
| **User preference** | One person's own choice of how a page is shown, or which emails they get, stored per user so it follows them. Not audited. | `UserPreference`, `UserPreferences`, `UserPreferenceService` |

## V

| Term | Meaning | Code name |
|---|---|---|
| **Viewer** | The role that reads everything the team does, every action included, and changes nothing but their own meeting notes. | `AppRole.Viewer` |
| **Visitor** | The role for a colleague from elsewhere: the published archive, and a live meeting only from a *seat* while it runs. | `AppRole.Visitor` |

## W

| Term | Meaning | Code name |
|---|---|---|
| **Watched** | An adversary profile the team follows closely. Triage marks items that name it ("Watching: Scattered Spider"), and Coverage puts the gaps it uses first. | `Adversary.Watched` |
| **We run** | The *Our stack* products an article is about, tagged under "why it matters". | `ArticleProduct`, `SearchCombobox` |
| **Webhook** | An address TIDE posts to, signed with HMAC-SHA256, when an action is raised or becomes overdue and when a brief is sent. | `Webhook`, `WebhookService` |
| **Why it matters to us** | The article's paragraph on what it means for this organization, usually naming our own systems, so it has its own TLP, starting at AMBER on a threat. | `Article.WhyItMatters`, `Article.WhyItMattersTlp`, `Article.DefaultWhyItMattersTlp` |
| **Working days** | Monday to Friday less *holidays*. Every rule that counts days uses it. | `WorkingDays`, `OrgClock.Days` |
| **Workload** | Each team's open actions, what's late, and who holds what. | `/workload`, `ActionService` (`TeamLoad`) |
