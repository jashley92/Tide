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
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>The team's shared mailbox, for its digest and for reminders about actions nobody is assigned to.</summary>
    public string? Email { get; set; }

    public int SortOrder { get; set; }
    public bool IsRetired { get; set; }

    public List<TeamMember> Members { get; set; } = [];
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

    /// <summary>The business day it's due, in the organization's time zone.</summary>
    public DateOnly? DueDate { get; set; }

    /// <summary>
    /// The business day the action belongs to, when it was raised after the fact (while writing up a past day's
    /// brief): reporting counts it then. Null means the day it was created. Its SLA still runs from when it was
    /// recorded, since that's when the team got it.
    /// </summary>
    public DateOnly? RaisedOn { get; set; }

    public ActionStatus Status { get; set; } = ActionStatus.Open;
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

    public bool IsOpen => Status is ActionStatus.Open or ActionStatus.InProgress or ActionStatus.Blocked;

    public static bool IsClosedStatus(ActionStatus s) => s is ActionStatus.Done or ActionStatus.Cancelled or ActionStatus.NotApplicable;
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
/// A file on an action's comment: a screenshot (a checked raster image, shown inline) or an evidence file of any kind
/// (a log extract, a pcap, an email), only ever downloaded. Its name and size are copied here to list it.
/// </summary>
public class ActionCommentFile : Entity
{
    public const int MaxPerComment = 10;

    public Guid CommentId { get; set; }
    public Guid FileId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public bool IsImage { get; set; }
    public int SortOrder { get; set; }
}

/// <summary>One thing to do on an action's checklist: copied from a playbook's step, or added by hand.</summary>
public class ActionChecklistItem : Entity
{
    public const int MaxText = 300;
    public const int MaxPerAction = 50;

    public Guid ActionId { get; set; }
    public int SortOrder { get; set; }
    public string Text { get; set; } = string.Empty;
    public bool IsDone { get; set; }
    public string? DoneByName { get; set; }
    public DateTimeOffset? DoneAtUtc { get; set; }
}

/// <summary>
/// An administrator's playbook: named, ordered steps for a kind of action (or any kind). Whoever creates
/// an action, or works one, can start its checklist from one; the steps are copied, so editing a playbook changes only
/// the actions started from it afterwards. Retired (inactive), never deleted, once used.
/// </summary>
public class Playbook : AuditableEntity
{
    public const int MaxSteps = 40;

    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>The kind of action it's for; null offers it for every kind.</summary>
    public ActionKind? Kind { get; set; }

    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }

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
