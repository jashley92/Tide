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

    /// <summary>An optional opening note, shown on the title slide.</summary>
    public string? Intro { get; set; }

    /// <summary>The TLP of <see cref="Intro"/>: an edition whose ceiling is below it leaves the note out, and one that
    /// carries it is marked at least this high.</summary>
    public TlpLevel IntroTlp { get; set; } = TlpLevel.Clear;

    /// <summary>The meeting's notes from before they were kept per person (<see cref="MeetingNote"/>). The upgrade moves
    /// them into one note each and empties this; nothing writes it now.</summary>
    public string? MeetingNotes { get; set; }

    /// <summary>When the meeting was held. For a brief written after the fact this is the day it was for, not the day
    /// someone recorded it.</summary>
    public DateTimeOffset? MeetingHeldAtUtc { get; set; }

    /// <summary>When the meeting was recorded as over ("End the meeting"), by the clock.</summary>
    public DateTimeOffset? PresentedAtUtc { get; set; }

    /// <summary>When someone marked the brief distributed without email (email is off on the server), and who. Null for
    /// a brief that was emailed, or isn't finished.</summary>
    public DateTimeOffset? DistributedAtUtc { get; set; }
    public string? DistributedById { get; set; }
    public string? DistributedByName { get; set; }

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
}

/// <summary>
/// Someone at a brief's meeting: recorded when they join it in TIDE, or added by the presenter for someone in the room
/// without a laptop (no <see cref="UserId"/>, no times). One per person per brief; joining again keeps the first join.
/// </summary>
public class MeetingAttendee : Entity
{
    public Guid BriefId { get; set; }

    /// <summary>The user, or empty for someone the presenter added by name.</summary>
    public string UserId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    /// <summary>When they first joined, and when they last left (null while they're there, or when added by name).</summary>
    public DateTimeOffset? JoinedAtUtc { get; set; }
    public DateTimeOffset? LeftAtUtc { get; set; }
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
