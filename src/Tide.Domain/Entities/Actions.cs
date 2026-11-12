using Tide.Domain.Common;
using Tide.Domain.Enums;

namespace Tide.Domain.Entities;

/// <summary>
/// A team that owns follow-up actions ("Vulnerability Management", "Cyber Defense"). Admin-managed, with a stable
/// <see cref="Key"/>; retired, never deleted, once it owns an action. Its members are the people who may work its
/// actions (with the WorkActions permission); its mailbox, when set, gets its reminders and weekly digest.
/// </summary>
public class Team : AuditableEntity
{
    public const int MaxContactName = 200;
    public const int MaxEngage = 2000;

    /// <summary>The reminder cadence's ranges and defaults, in business days.</summary>
    public const int MaxRemindBefore = 10, DefaultRemindBefore = 2, MinRemindEvery = 1, MaxRemindEvery = 20, DefaultRemindEvery = 3;

    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>The team's shared mailbox, for its digest and for reminders about actions nobody is assigned to.</summary>
    public string? Email { get; set; }

    public int SortOrder { get; set; }
    public bool IsRetired { get; set; }

    /// <summary>Moved by each change someone makes to the team's own fields (its profile, mailbox, escalation contact,
    /// limit, reminders, retiring or bringing it back), so a form opened before a colleague's change can't save over it.
    /// Its place in the list and its members don't move it: no form saves them. Nothing in the background writes a team.</summary>
    public Guid EditVersion { get; set; } = Guid.NewGuid();

    public List<TeamMember> Members { get; set; } = [];

    /// <summary>
    /// The most actions it may have active (open, in progress or blocked) at once, or null for no limit; and what happens
    /// when one would take it over. Set by an administrator.
    /// </summary>
    public int? ActiveLimit { get; set; }
    public ActiveLimitMode LimitMode { get; set; } = ActiveLimitMode.Show;

    /// <summary>
    /// Tracked in TIDE but working elsewhere: no members, so no assignees, and reminded by email at its contact address
    /// (never a TIDE link). Anyone who works actions keeps these; internal teams are the administrators'.
    /// </summary>
    public bool IsExternal { get; set; }

    /// <summary>An external team's contact: who to ask, their mailbox, and how to engage them (their ticket queue, say).</summary>
    public string? ContactName { get; set; }
    public string? ContactEmail { get; set; }
    public string? Engage { get; set; }

    /// <summary>Whether an external team is emailed about its actions due soon or overdue, and how often: this many
    /// business days before the due date (0: on the day only), then every so many while overdue (null: once).</summary>
    public bool RemindersOn { get; set; } = true;
    public int RemindBeforeDays { get; set; } = DefaultRemindBefore;
    public int? RemindOverdueEveryDays { get; set; } = DefaultRemindEvery;

    /// <summary>Who the escalation ladder copies when the team's actions slip (a manager, a duty lead), internal team or
    /// external: their name and email. Optional; with no email, a step that copies them copies no one.</summary>
    public string? EscalationName { get; set; }
    public string? EscalationEmail { get; set; }
}

/// <summary>A person on a team, by user id (as mirrored from AD on sign-in).</summary>
public class TeamMember
{
    public Guid TeamId { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public DateTimeOffset AddedAtUtc { get; set; }
}

/// <summary>
/// A follow-up action from the brief: an IOC sweep, a hunt, a block, a patch, a config change. Owned by a team, due by
/// a date the SLA targets suggest, tracked to an outcome. A long remediation splits into child actions, one per team.
/// </summary>
public class ActionItem : AuditableEntity
{
    public const int MaxTitle = 300;

    /// <summary>A short number people say aloud ("A-42"), unique and never reused.</summary>
    public int Number { get; set; }

    public string Title { get; set; } = string.Empty;

    /// <summary>What to do and why (Markdown).</summary>
    public string? Description { get; set; }

    public ActionKind Kind { get; set; } = ActionKind.Other;
    public Priority Priority { get; set; } = Priority.P3;

    public Guid TeamId { get; set; }
    public Team? Team { get; set; }

    /// <summary>The person working it (user id and name), or none: then it's the team's.</summary>
    public string? AssigneeId { get; set; }
    public string? AssigneeName { get; set; }

    // EF reads and writes the column through this field (its convention for a field named after the property), so
    // loading an action doesn't count as a move; only code setting DueDate does.
    private DateOnly? _dueDate;

    /// <summary>
    /// The business day it's due, in the organization's time zone. Setting it keeps the record honest: the first date it's
    /// given becomes <see cref="OriginalDueDate"/> (set once, never moved), and every later change, cleared or moved
    /// either way, adds one to <see cref="DueDateMoves"/>. Setting the date it already has changes nothing.
    /// </summary>
    public DateOnly? DueDate
    {
        get => _dueDate;
        set
        {
            if (value == _dueDate) return;
            if (OriginalDueDate is null) OriginalDueDate = value;
            else DueDateMoves++;
            _dueDate = value;
        }
    }

    /// <summary>
    /// The first due date it was given, kept when the date moves, so the team commitments scorecard can judge it against
    /// the date first promised as well as the one it has now. Null only while it has never had one. Actions from before
    /// it was kept start from the date they had then.
    /// </summary>
    public DateOnly? OriginalDueDate { get; private set; }

    /// <summary>How many times its due date has changed since it was first set (moved later or earlier, or cleared).
    /// Like <see cref="OriginalDueDate"/>, only <see cref="DueDate"/>'s setter writes it.</summary>
    public int DueDateMoves { get; private set; }

    /// <summary>
    /// The business day the action belongs to, when it was raised after the fact (while writing up a past day's
    /// brief): reporting counts it then. Null means the day it was created. Its SLA still runs from when it was
    /// recorded, since that's when the team got it.
    /// </summary>
    public DateOnly? RaisedOn { get; set; }

    /// <summary>
    /// The line of its article's "Recommended actions" it was raised from (as the line read then, one line of plain
    /// text), or null for one raised by hand. The slide shows it against that line rather than among the article's other
    /// actions; a line edited or removed since leaves it among them. Set once, when it's raised.
    /// </summary>
    public string? RecommendationLine { get; set; }

    // EF reads and writes the column through this field, as DueDate's, so loading an action isn't a change of status.
    private ActionStatus _status = ActionStatus.Open;

    /// <summary>Where it stands. Setting another status records when (<see cref="StatusChangedAtUtc"/>), whichever path
    /// moves it, by the wall clock: the service that moves it then dates it by the app's clock, and seeding sets it after.
    /// Setting the one it has changes nothing.</summary>
    public ActionStatus Status
    {
        get => _status;
        set
        {
            if (value == _status) return;
            _status = value;
            StatusChangedAtUtc = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>
    /// When it moved to the status it has (the board's "In column 3d"): when it was raised, until it moves. Derived, like
    /// <see cref="OriginalDueDate"/>, so it adds no history line. Null only for a row from before it was kept that the
    /// upgrade couldn't date.
    /// </summary>
    public DateTimeOffset? StatusChangedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public ActionOutcome Outcome { get; set; } = ActionOutcome.None;
    public string? OutcomeNote { get; set; }

    /// <summary>When it was closed (Done, Cancelled or Not applicable), and the local day that was.</summary>
    public DateTimeOffset? ClosedAtUtc { get; set; }
    public DateOnly? ClosedDate { get; set; }

    /// <summary>A ticket in another system (ITSM) that tracks the same work.</summary>
    public string? TicketRef { get; set; }
    public string? TicketUrl { get; set; }

    /// <summary>The article it came from, if any.</summary>
    public Guid? ArticleId { get; set; }
    public Article? Article { get; set; }

    /// <summary>The action this one is part of, for a remediation split across teams.</summary>
    public Guid? ParentId { get; set; }

    public Guid EditVersion { get; set; } = Guid.NewGuid();

    /// <summary>The playbook its checklist was started from (and its name then), if any. Later edits to the playbook
    /// don't reach the checklist: its steps were copied.</summary>
    public Guid? PlaybookId { get; set; }
    public string? PlaybookName { get; set; }

    public List<ActionChecklistItem> Checklist { get; set; } = [];
    public List<ActionComment> Comments { get; set; } = [];
    public List<ActionEvent> Events { get; set; } = [];
    public List<HuntQuery> Hunts { get; set; } = [];

    /// <summary>Indicators added on the action itself, for one with no article to carry them; its hunts use them.</summary>
    public List<ActionIndicator> Indicators { get; set; } = [];

    /// <summary>The emails sent about it (an Outreach action), and their replies.</summary>
    public List<OutreachMessage> Outreach { get; set; } = [];

    /// <summary>Files added to the action itself (removed ones included); its comments' files are on the comments.</summary>
    public List<ActionFile> Files { get; set; } = [];

    /// <summary>Not to be in its external team's reminder emails (it matters only while its team is external).</summary>
    public bool NoReminders { get; set; }

    public const int MaxHandoffReason = 500;

    /// <summary>The last hand-off: the team it came from, who handed it over, when and why. Its history keeps every one.</summary>
    public Guid? HandedFromTeamId { get; set; }
    public string? HandedById { get; set; }
    public string? HandedByName { get; set; }
    public DateTimeOffset? HandedAtUtc { get; set; }
    public string? HandoffReason { get; set; }

    /// <summary>
    /// The due date a step of the escalation ladder flagged it escalated for, and when. It stays escalated only while it's
    /// open and still due then (<see cref="IsEscalated"/>): closing it, or moving its date either way, ends it, with
    /// nothing to clear, as the ladder's marks are kept per due date too.
    /// </summary>
    public DateOnly? EscalatedForDue { get; set; }
    public DateTimeOffset? EscalatedAtUtc { get; set; }

    public const int MaxDeleteReason = 500;

    /// <summary>
    /// When it was deleted (a mistake or a duplicate), by whom and why; null while it stands. A deleted action is kept,
    /// with its number, comments, files and history, but the database's query filter leaves it (and them) out of every
    /// read, so it's gone from every list, count, reminder, digest, webhook, export and search; an administrator lists
    /// and restores it. Cancelled and Not applicable are for work that won't be done; this is for an action that
    /// shouldn't exist.
    /// </summary>
    public DateTimeOffset? DeletedAtUtc { get; set; }
    public string? DeletedById { get; set; }
    public string? DeletedByName { get; set; }
    public string? DeleteReason { get; set; }

    /// <summary>The name of the query filter that leaves deleted actions (and what's only theirs) out, for the few reads
    /// that must see them: <c>IgnoreQueryFilters([ActionItem.DeletedFilter])</c>, so any other filter added later
    /// still applies there.</summary>
    public const string DeletedFilter = "Deleted";

    public bool IsOpen => !IsClosedStatus(Status);

    /// <summary>Whether it's escalated now (<see cref="EscalatedFor"/>).</summary>
    public bool IsEscalated => EscalatedFor(Status, DueDate, EscalatedForDue);

    /// <summary>When it was escalated, while it still is; null otherwise.</summary>
    public DateTimeOffset? EscalatedSince => IsEscalated ? EscalatedAtUtc : null;

    /// <summary>The one rule for escalated: open, and flagged for the due date it has. Its database twin is
    /// <c>ActionQueries.WhereEscalated</c>.</summary>
    public static bool EscalatedFor(ActionStatus status, DateOnly? due, DateOnly? escalatedForDue) =>
        escalatedForDue is not null && escalatedForDue == due && !IsClosedStatus(status);

    /// <summary>The closed statuses: Done, Cancelled and Not applicable. The one list; the queries use it too
    /// (<c>ActionQueries.WhereClosed</c>), so open and closed mean the same in C# and in the database.</summary>
    public static readonly ActionStatus[] ClosedStatuses = [ActionStatus.Done, ActionStatus.Cancelled, ActionStatus.NotApplicable];

    /// <summary>The statuses that count toward a team's active limit: Open, In progress and Blocked (not Tracking, which
    /// others are doing).</summary>
    public static readonly ActionStatus[] ActiveStatuses = [ActionStatus.Open, ActionStatus.InProgress, ActionStatus.Blocked];

    /// <summary>The open statuses: the active ones and Tracking.</summary>
    public static readonly ActionStatus[] OpenStatuses = [.. ActiveStatuses, ActionStatus.Tracking];

    public static bool IsClosedStatus(ActionStatus s) => ClosedStatuses.Contains(s);

    /// <summary>Whether an action at <paramref name="s"/> counts toward its team's active limit.</summary>
    public static bool IsActiveStatus(ActionStatus s) => ActiveStatuses.Contains(s);

    /// <summary>The statuses in the order people read them (the board's columns, the status buttons): the open ones, then
    /// the closed. The enum's own order is its stored numbers, which put Tracking last.</summary>
    public static readonly IReadOnlyList<ActionStatus> StatusOrder =
    [
        ActionStatus.Open, ActionStatus.InProgress, ActionStatus.Blocked, ActionStatus.Tracking,
        ActionStatus.Done, ActionStatus.Cancelled, ActionStatus.NotApplicable,
    ];
}

/// <summary>A comment on an action (Markdown), in order.</summary>
public class ActionComment : Entity
{
    public Guid ActionId { get; set; }
    public string Text { get; set; } = string.Empty;
    public string AuthorId { get; set; } = string.Empty;
    public string AuthorName { get; set; } = string.Empty;
    public DateTimeOffset AtUtc { get; set; }

    /// <summary>When its author last changed it; null if never.</summary>
    public DateTimeOffset? EditedAtUtc { get; set; }

    /// <summary>Who last changed it (its author or an administrator); null if never.</summary>
    public string? EditedByName { get; set; }

    /// <summary>Moves on every edit, so an edit begun on an older copy is refused, as an action's is.</summary>
    public Guid EditVersion { get; set; } = Guid.NewGuid();

    /// <summary>Screenshots and evidence files attached to it, in the order they were added.</summary>
    public List<ActionCommentFile> Files { get; set; } = [];
}

/// <summary>
/// What a file on an action and a file on one of its comments share: a name, a description, what makes it risky, and
/// whether (and by whom, when and why) it was removed. Removing hides it and keeps its bytes, hash and record.
/// </summary>
public interface IRemovableFile
{
    string FileName { get; }
    string? Description { get; set; }
    string? RiskReason { get; }
    DateTimeOffset? RemovedAtUtc { get; set; }
    string? RemovedById { get; set; }
    string? RemovedByName { get; set; }
    string? RemovedReason { get; set; }
    bool IsRemoved { get; }
}

/// <summary>
/// A file on an action's comment: a screenshot (a checked raster image, shown inline) or an evidence file of any kind
/// (a log extract, a pcap, an email), only ever downloaded. Its name and size are copied here to list it.
/// </summary>
public class ActionCommentFile : Entity, IRemovableFile
{
    public const int MaxPerComment = 10;

    public Guid CommentId { get; set; }
    public Guid FileId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public bool IsImage { get; set; }
    public int SortOrder { get; set; }

    /// <summary>What the file is, as whoever works the action describes it (up to <see cref="ActionFile.MaxDescription"/>).</summary>
    public string? Description { get; set; }

    /// <summary>Why the file can run, or carry something that runs, when opened (judged at upload by its name, the type
    /// the browser declared and its first bytes); null when it can't, or for a file added before this was recorded (its
    /// name is judged then).</summary>
    public string? RiskReason { get; set; }

    /// <summary>When it was removed from its comment (hidden, its bytes, hash and record kept), by whom and why; null
    /// while it's on the comment. An administrator can restore it.</summary>
    public DateTimeOffset? RemovedAtUtc { get; set; }
    public string? RemovedById { get; set; }
    public string? RemovedByName { get; set; }
    public string? RemovedReason { get; set; }

    public bool IsRemoved => RemovedAtUtc is not null;
}

/// <summary>
/// A file added to an action itself (not to one of its comments): a log extract, a report, a screenshot, a sample. Its
/// bytes are a <see cref="StoredFile"/>; its name, size, hash and what was found about its type are copied here to list
/// it. Removing it hides it from the action but keeps the bytes, the hash and this record, with who removed it, when
/// and why; an administrator can restore it.
/// </summary>
public class ActionFile : Entity, IRemovableFile
{
    public const int MaxDescription = 500;
    public const int MaxReason = 500;

    /// <summary>The reason a file is kept as removed when its comment is deleted.</summary>
    public const string CommentDeletedReason = "Its comment was deleted";

    /// <summary>The most files an action holds (not counting removed ones, or those on its comments).</summary>
    public const int MaxPerAction = 100;

    /// <summary>The most added at once.</summary>
    public const int MaxPerUpload = 10;

    public Guid ActionId { get; set; }
    public Guid FileId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public long SizeBytes { get; set; }

    /// <summary>Lower-case hex SHA-256 of the bytes, as they were received.</summary>
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>The type the uploader's browser declared, as it was sent (never trusted, only recorded and judged).</summary>
    public string? DeclaredType { get; set; }

    /// <summary>Whether the bytes are a raster image (PNG, JPEG, GIF or WebP), whatever the name or declared type said.</summary>
    public bool IsImage { get; set; }

    /// <summary>When the name or declared type said one thing and the bytes another ("Named as a PNG image, but it isn't
    /// one."); null when they agree.</summary>
    public string? TypeNote { get; set; }

    /// <summary>Why it can run, or carry something that runs, when opened; null when it can't.</summary>
    public string? RiskReason { get; set; }

    public string? Description { get; set; }

    public string AddedById { get; set; } = string.Empty;
    public string AddedByName { get; set; } = string.Empty;
    public DateTimeOffset AddedAtUtc { get; set; }

    public DateTimeOffset? RemovedAtUtc { get; set; }
    public string? RemovedById { get; set; }
    public string? RemovedByName { get; set; }
    public string? RemovedReason { get; set; }

    public bool IsRemoved => RemovedAtUtc is not null;

    /// <summary>For a file kept from a comment that was deleted: who wrote that comment and when (the file was attached to
    /// it). Null for a file added to the action.</summary>
    public string? FromCommentBy { get; set; }
    public DateTimeOffset? FromCommentAtUtc { get; set; }

    /// <summary>Whether it's shown inline: a real raster image that nothing about suggests it can run.</summary>
    public bool ShowsInline => ShowsInlineFor(IsImage, TypeNote, RiskReason);

    /// <summary>The rule for showing any uploaded file inline: a real raster image, named and sent as what it is (no
    /// <paramref name="typeNote"/>), that can't run (no <paramref name="riskReason"/>). The inline image endpoint's query
    /// (ScreenshotService) says the same in SQL.</summary>
    public static bool ShowsInlineFor(bool isImage, string? typeNote, string? riskReason) => isImage && typeNote is null && riskReason is null;
}

/// <summary>One thing to do on an action's checklist: copied from a playbook's step, or added by hand.</summary>
public class ActionChecklistItem : Entity
{
    public const int MaxText = 300;
    public const int MaxPerAction = 50;

    public Guid ActionId { get; set; }
    public int SortOrder { get; set; }

    /// <summary>When it was put on the checklist. Two added at the same moment take the same place (the highest plus
    /// one); this, then the id, puts them in the same order on every read (<c>ActionService.InOrder</c>).</summary>
    public DateTimeOffset AddedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public string Text { get; set; } = string.Empty;
    public bool IsDone { get; set; }
    public string? DoneByName { get; set; }
    public DateTimeOffset? DoneAtUtc { get; set; }
}

/// <summary>
/// A playbook the team keeps: named, ordered steps for a kind of action (or any kind). Whoever creates an action, or
/// works one, can start its checklist from one; the steps are copied, so editing a playbook changes only the actions
/// started from it afterwards. Anyone who works actions keeps the library, as they keep the saved hunt queries; one an
/// action was started from is retired, never deleted.
/// </summary>
public class Playbook : AuditableEntity, ITeamLibraryEntry
{
    public const int MaxName = 100;
    public const int MaxDescription = 1000;
    public const int MaxSteps = 40;

    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>The kind of action it's for; null offers it for every kind.</summary>
    public ActionKind? Kind { get; set; }

    /// <summary>Retired: no longer offered on actions, and still read on the Playbooks page and in their history.</summary>
    public bool IsRetired { get; set; }

    /// <summary>Its place in the pickers.</summary>
    public int SortOrder { get; set; }

    public string CreatedByName { get; set; } = string.Empty;
    public string? ModifiedByName { get; set; }

    public Guid EditVersion { get; set; } = Guid.NewGuid();

    public List<PlaybookStep> Steps { get; set; } = [];
}

/// <summary>One step of a <see cref="Playbook"/>, in order.</summary>
public class PlaybookStep : Entity
{
    public Guid PlaybookId { get; set; }
    public int Order { get; set; }
    public string Text { get; set; } = string.Empty;
}

/// <summary>
/// An administrator's rule for action cards and rows: those that match every condition set get its colour (the first
/// matching rule's, in order) and its label; one shown as a filter is also a chip that narrows the board and list to its
/// matches. A condition left empty (null, or false for the yes/no ones) matches everything.
/// </summary>
public class ActionCardRule : AuditableEntity
{
    public string Name { get; set; } = string.Empty;
    public CardColour Colour { get; set; } = CardColour.Blue;
    public bool IsActive { get; set; } = true;
    public bool ShowAsFilter { get; set; } = true;
    public int SortOrder { get; set; }

    public ActionKind? Kind { get; set; }

    /// <summary>This priority or a higher one (P1 is highest).</summary>
    public Priority? PriorityAtLeast { get; set; }
    public Guid? TeamId { get; set; }
    public ActionStatus? Status { get; set; }
    public CardRuleSla Sla { get; set; } = CardRuleSla.Any;
    public bool Unassigned { get; set; }
    public bool HasChecklist { get; set; }

    /// <summary>A part of another action (a child).</summary>
    public bool IsPart { get; set; }
}

/// <summary>One line of an action's history as people read it ("Status: Open → In progress"). The audit log keeps the
/// full record; this is the readable timeline on the action.</summary>
public class ActionEvent : Entity
{
    public Guid ActionId { get; set; }
    public DateTimeOffset AtUtc { get; set; }
    public string ByName { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
}

/// <summary>
/// A hunt query on an action: generated from the source article's indicators in one dialect, edited by the analyst,
/// run by them in their SIEM (TIDE never signs in to one, ADR 0006), and its result recorded here.
/// </summary>
public class HuntQuery : Entity
{
    /// <summary>The longest a query is: one TIDE wrote grows with the indicators (a sweep over hundreds of hashes).</summary>
    public const int MaxText = 200_000;

    /// <summary>The most a person writes: all of a hand-written query, or added to what TIDE wrote.</summary>
    public const int MaxWritten = 20_000;

    public Guid ActionId { get; set; }

    /// <summary>The dialect's key ("xql", "kql", "fql").</summary>
    public string Dialect { get; set; } = string.Empty;

    /// <summary>The query as generated, and as the analyst edited it (what was actually run).</summary>
    public string GeneratedText { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; set; }
    public string CreatedByName { get; set; } = string.Empty;

    public HuntResult Result { get; set; } = HuntResult.NotRun;
    public int? ResultCount { get; set; }
    public string? Notes { get; set; }
    public string? RunById { get; set; }
    public string? RunByName { get; set; }
    public DateTimeOffset? RunAtUtc { get; set; }

    /// <summary>When <see cref="Text"/> was last changed after its result was recorded (the result was for the earlier text).</summary>
    public DateTimeOffset? EditedAfterResultAtUtc { get; set; }

    /// <summary>A screenshot of the result, as evidence.</summary>
    public Guid? EvidenceFileId { get; set; }

    /// <summary>The saved query it was added from, and its name then: the name stays when the saved query is deleted.</summary>
    public Guid? SavedQueryId { get; set; }
    public string? SavedQueryName { get; set; }
}

/// <summary>
/// An indicator added on an action that has no article (one raised from the board, say). An action raised from an article
/// adds its indicators to the article instead, so the brief, exports and other actions see them too.
/// </summary>
public class ActionIndicator : Entity
{
    public Guid ActionId { get; set; }
    public IndicatorType Type { get; set; }
    public string Value { get; set; } = string.Empty;
    public string? Note { get; set; }
    public DateTimeOffset AddedAtUtc { get; set; }
    public string AddedByName { get; set; } = string.Empty;
}

/// <summary>
/// A hunt query kept for the team to reuse (a tuned sweep, a behavioural hunt with no indicators): its name, language,
/// text and what it's for. <c>{{days}}</c> and <c>{{values}}</c> in the text are filled from an action when it's added to one. Anyone
/// who works actions keeps the library; it's retired rather than deleted while it's still useful to read.
/// </summary>
public class SavedQuery : AuditableEntity, ITeamLibraryEntry
{
    public const int MaxName = 100;
    public const int MaxDescription = 300;

    /// <summary>Under a hunt's 20,000: room for the line TIDE writes over it on an action.</summary>
    public const int MaxText = 19500;

    public string Name { get; set; } = string.Empty;

    /// <summary>The dialect's key ("xql", "kql", "fql").</summary>
    public string Dialect { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>The names <see cref="AuditableEntity.CreatedBy"/> and <see cref="AuditableEntity.ModifiedBy"/> had then.</summary>
    public string CreatedByName { get; set; } = string.Empty;
    public string? ModifiedByName { get; set; }

    public bool IsRetired { get; set; }

    /// <summary>Moves on every edit, so an edit begun on an older copy is refused, as an action's is.</summary>
    public Guid EditVersion { get; set; } = Guid.NewGuid();
}

/// <summary>
/// An email the team keeps for asking someone for information on an Outreach action (a vendor, a partner, an ISAC, an
/// internal team): its name, subject and body, with <c>{{article.title}}</c>, <c>{{indicators}}</c> and the like filled
/// from the action when it's written, and an optional fixed To address. <see cref="Ceiling"/> is the highest TLP the
/// filled email may carry: parts above it are left out. Anyone who works actions keeps the library, as they keep the
/// saved hunt queries; TIDE never sends one.
/// </summary>
public class OutreachTemplate : AuditableEntity, ITeamLibraryEntry
{
    public const int MaxName = 100;
    public const int MaxDescription = 300;
    public const int MaxSubject = 300;
    public const int MaxBody = 8000;

    /// <summary>One address: an RFC 5321 path is at most 254 characters.</summary>
    public const int MaxTo = 254;

    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;

    /// <summary>Who it always goes to (an ISAC's address), or null to take the product's contact or one typed.</summary>
    public string? To { get; set; }

    /// <summary>The highest TLP the filled email may carry (an email to a vendor: CLEAR or GREEN).</summary>
    public TlpLevel Ceiling { get; set; } = TlpLevel.Clear;

    /// <summary>The names <see cref="AuditableEntity.CreatedBy"/> and <see cref="AuditableEntity.ModifiedBy"/> had then.</summary>
    public string CreatedByName { get; set; } = string.Empty;
    public string? ModifiedByName { get; set; }

    public bool IsRetired { get; set; }

    /// <summary>Moves on every edit, so an edit begun on an older copy is refused, as a saved query's is.</summary>
    public Guid EditVersion { get; set; } = Guid.NewGuid();
}

/// <summary>
/// An email an analyst sent from their own mail about an Outreach action, as they marked it sent: to whom, the subject
/// and body as sent (after their edits), its TLP, and the reply once it's recorded. TIDE never sends it; the action
/// waits for the reply while one is outstanding, and the follow-up reminder counts from <see cref="SentAtUtc"/>.
/// </summary>
public class OutreachMessage : Entity
{
    /// <summary>A few addresses, separated by semicolons.</summary>
    public const int MaxTo = 1000;
    public const int MaxSubject = 300;

    /// <summary>Room for a template's 8,000 characters with an action's indicators filled in.</summary>
    public const int MaxBody = 40000;
    public const int MaxNotes = 4000;

    public Guid ActionId { get; set; }

    /// <summary>The template it was written from, and its name then: the name stays when the template is deleted.</summary>
    public Guid? TemplateId { get; set; }
    public string? TemplateName { get; set; }

    public string To { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;

    /// <summary>The marking of what was filled in (the TLP line written for it).</summary>
    public TlpLevel Tlp { get; set; } = TlpLevel.Clear;

    public DateTimeOffset SentAtUtc { get; set; }
    public string SentById { get; set; } = string.Empty;
    public string SentByName { get; set; } = string.Empty;

    /// <summary>When the reply was recorded, who recorded it, what it said, and the file kept with it (the stored file of an
    /// <see cref="ActionFile"/> on the action), if any. Null until then: the action is waiting.</summary>
    public DateTimeOffset? RepliedAtUtc { get; set; }
    public string? ReplyRecordedByName { get; set; }
    public string? ReplyNotes { get; set; }
    public Guid? ReplyFileId { get; set; }

    /// <summary>Moved when its reply is recorded: of two people recording the reply at once, the second is refused rather
    /// than writing over the first one's notes.</summary>
    public Guid EditVersion { get; set; } = Guid.NewGuid();

    public bool IsWaiting => RepliedAtUtc is null;
}

/// <summary>
/// An administrator's version of a dialect's query for one kind of value (IPv4, SHA-256, domain…), replacing the
/// built-in default: field and dataset names differ by environment. <c>{{values}}</c> stands for the values, written
/// as the dialect writes a list. An empty template leaves that kind out of generated hunts.
/// </summary>
public class QueryTemplate
{
    public string Dialect { get; set; } = string.Empty;
    public HuntValueKind Kind { get; set; }
    public string Template { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public string UpdatedBy { get; set; } = string.Empty;
}

/// <summary>That a reminder or digest went, so a scan sends it once: a kind and a key ("due-soon", "{action}:{due}").</summary>
public class NotificationMark
{
    public string Kind { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public DateTimeOffset SentAtUtc { get; set; }
}
