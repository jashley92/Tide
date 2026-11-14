using Tide.Domain.Common;
using Tide.Domain.Enums;

namespace Tide.Domain.Entities;

/// <summary>
/// One business day's brief: the articles the presenting analyst takes the morning meeting through, in order. There's at
/// most one per <see cref="BriefDate"/>.
/// </summary>
public class Brief : AuditableEntity
{
    /// <summary>The day it's for, in the organization's time zone.</summary>
    public DateOnly BriefDate { get; set; }

    /// <summary>The analyst presenting it (user id), and their name.</summary>
    public string AnalystId { get; set; } = string.Empty;
    public string AnalystName { get; set; } = string.Empty;

    public BriefStatus Status { get; set; } = BriefStatus.Draft;

    /// <summary>An optional one-line note shown on the title slide (the "title slide line"), with a TLP of its own. Not the
    /// team's opening note (<see cref="TeamNote"/>).</summary>
    public string? Intro { get; set; }

    /// <summary>The TLP of <see cref="Intro"/>: an edition whose ceiling is below it leaves the note out, and one that
    /// carries it is marked at least this high.</summary>
    public TlpLevel IntroTlp { get; set; } = TlpLevel.Clear;

    /// <summary>The team's opening note, in Markdown: what's coming up and the week's phishing trends, read at the start
    /// of the meeting. The security team edition only (its own slide, a section of its email); never the company-wide
    /// edition. A new brief starts with the previous brief's, so standing items roll forward.</summary>
    public string? TeamNote { get; set; }

    /// <summary>When someone last saved the opening note on this brief, changed or kept as still current (who is in the
    /// audit trail). Null on a brief that started with the previous brief's note and hasn't been looked at since: Today
    /// flags it.</summary>
    public DateTimeOffset? TeamNoteReviewedAtUtc { get; set; }

    /// <summary>The meeting's notes from before they were kept per person (<see cref="MeetingNote"/>). The upgrade moves
    /// them into one note each and empties this; nothing writes it now.</summary>
    public string? MeetingNotes { get; set; }

    /// <summary>When the meeting was held. For a brief written after the fact this is the day it was for, not the day
    /// someone recorded it.</summary>
    public DateTimeOffset? MeetingHeldAtUtc { get; set; }

    /// <summary>When the meeting was recorded as over ("End the meeting"), by the clock.</summary>
    public DateTimeOffset? PresentedAtUtc { get; set; }

    /// <summary>Who presented the live meeting, as said ("Pat" or "Pat and Sam" when it changed hands): whoever held the
    /// presenter's seat. Null for a brief presented without a live meeting, which names <see cref="AnalystName"/>.</summary>
    public string? PresentedByName { get; set; }

    /// <summary>When someone marked the brief distributed without email (email is off on the server), and who. Null for
    /// a brief that was emailed, or isn't finished.</summary>
    public DateTimeOffset? DistributedAtUtc { get; set; }
    public string? DistributedById { get; set; }
    public string? DistributedByName { get; set; }

    /// <summary>When someone finished the brief without sending it while email was on ("Not sent"), who, and why. It's
    /// Sent as a distributed one is, so its numbers lock and its articles finish; nothing went, so nothing is published.
    /// It can still be emailed afterwards: a real send then makes it an ordinary sent brief, and these are kept as
    /// history. Null for a brief that wasn't finished that way.</summary>
    public DateTimeOffset? NotSentAtUtc { get; set; }
    public string? NotSentById { get; set; }
    public string? NotSentByName { get; set; }
    public string? NotSentReason { get; set; }

    /// <summary>When someone confirmed the company-wide copy was posted to SharePoint, and who. An administrator can
    /// require it before the brief counts as finished (<c>Distribution:RequireSharePoint</c>).</summary>
    public DateTimeOffset? SharePointPostedAtUtc { get; set; }

    /// <summary>Whether its SharePoint copy was required when it went out. Only those wait for it: turning the requirement
    /// on doesn't make every brief already sent look unfinished.</summary>
    public bool SharePointRequired { get; set; }
    public string? SharePointPostedById { get; set; }
    public string? SharePointPostedByName { get; set; }

    public Guid EditVersion { get; set; } = Guid.NewGuid();

    public List<BriefItem> Items { get; set; } = [];
}

/// <summary>An article's place on a brief. An article can be carried onto a later brief (an update).</summary>
public class BriefItem : Entity
{
    public Guid BriefId { get; set; }
    public Guid ArticleId { get; set; }
    public Article? Article { get; set; }
    public int SortOrder { get; set; }

    /// <summary>Ticked off during the meeting.</summary>
    public bool Discussed { get; set; }

    /// <summary>The brief whose meeting carried the article here (it wasn't discussed there), or null for an article
    /// put on this brief by hand. Reopening that meeting takes back only these.</summary>
    public Guid? CarriedFromBriefId { get; set; }

    /// <summary>The article's status before this brief's meeting marked it presented, or null when the meeting didn't
    /// change it (it was presented already, on an earlier brief, or carried on). Reopening the meeting puts it back.</summary>
    public ArticleStatus? StatusBeforeMeeting { get; set; }
}

/// <summary>
/// Someone at a brief's meeting: recorded when they join it in TIDE, or added by someone in the meeting for someone there
/// without a laptop (no <see cref="UserId"/>, no times). One per person per brief; joining again keeps the first join.
/// </summary>
public class MeetingAttendee : Entity
{
    public Guid BriefId { get; set; }

    /// <summary>The user, or empty for someone added by name.</summary>
    public string UserId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    /// <summary>When they first joined, and when they last left (null while they're there, or when added by name).</summary>
    public DateTimeOffset? JoinedAtUtc { get; set; }
    public DateTimeOffset? LeftAtUtc { get; set; }

    /// <summary>Who added them by name (the presenter, or someone following the meeting), or null for someone who joined,
    /// or added before this was kept. Whoever added a name may take it off; the presenter, any added by name.</summary>
    public string? AddedById { get; set; }
}

/// <summary>One person's note at a brief's meeting, against the article on screen when they wrote it.</summary>
public class MeetingNote : Entity
{
    public Guid BriefId { get; set; }
    public string AuthorId { get; set; } = string.Empty;
    public string AuthorName { get; set; } = string.Empty;

    /// <summary>The article on screen, or null for a note on the meeting as a whole.</summary>
    public Guid? ArticleId { get; set; }

    public DateTimeOffset AtUtc { get; set; }
    public DateTimeOffset? EditedAtUtc { get; set; }
    public string Text { get; set; } = string.Empty;
}
