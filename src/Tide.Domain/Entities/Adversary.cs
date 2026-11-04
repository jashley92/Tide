using Tide.Domain.Common;

namespace Tide.Domain.Entities;

/// <summary>Whether a profile is a threat actor (a group) or a campaign (a run of activity).</summary>
public enum AdversaryKind
{
    Actor = 0,
    Campaign = 1,
}

/// <summary>
/// A threat actor or campaign the team reports on (EP-73): one profile for every name it goes by ("Scattered Spider",
/// "UNC3944", "Octo Tempest"), so what's been said about it is in one place. Aliases come from MITRE ATT&amp;CK when its
/// groups are imported, and analysts add their own. Notes are internal (AMBER). Retired, never deleted; a profile merged
/// into another points at it.
/// </summary>
public class Adversary : AuditableEntity
{
    public const int MaxName = 100;
    public const int MaxAliases = 1000;
    public const int MaxMotivation = 100;
    public const int MaxSectors = 300;
    public const int MaxNotes = 4000;

    public string Name { get; set; } = string.Empty;
    public AdversaryKind Kind { get; set; } = AdversaryKind.Actor;

    /// <summary>Other names it goes by, separated by semicolons.</summary>
    public string? Aliases { get; set; }

    /// <summary>Its MITRE ATT&amp;CK id ("G1015", or "C0027" for a campaign), when known.</summary>
    public string? MitreId { get; set; }

    public string? Motivation { get; set; }

    /// <summary>The sectors it targets, as typed.</summary>
    public string? Sectors { get; set; }

    /// <summary>Notes in Markdown: what we've seen, what to watch for. Internal (AMBER).</summary>
    public string? Notes { get; set; }

    /// <summary>Whether triage marks new items that name it.</summary>
    public bool Watched { get; set; }

    public bool IsRetired { get; set; }

    /// <summary>The profile this one was merged into, when it was.</summary>
    public Guid? MergedIntoId { get; set; }
}

/// <summary>An article tagged with an adversary profile (optional, any number).</summary>
public class ArticleAdversary
{
    public Guid ArticleId { get; set; }
    public Guid AdversaryId { get; set; }
    public Adversary? Adversary { get; set; }
}
