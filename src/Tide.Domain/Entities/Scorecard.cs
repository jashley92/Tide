using Tide.Domain.Common;

namespace Tide.Domain.Entities;

/// <summary>
/// A board-scorecard category ("Third-party risk") holding the tags analysts put on articles. Admin-managed, with a
/// stable <see cref="Key"/> so a rename keeps the history. Retired, never deleted, once anything uses it.
/// </summary>
public class ScorecardCategory : AuditableEntity
{
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int SortOrder { get; set; }
    public bool IsRetired { get; set; }

    public List<ScorecardTag> Tags { get; set; } = [];
}

/// <summary>A tag under a <see cref="ScorecardCategory"/> ("Ransomware"), rolled up to it on the scorecard.</summary>
public class ScorecardTag : AuditableEntity
{
    public Guid CategoryId { get; set; }
    public ScorecardCategory? Category { get; set; }

    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int SortOrder { get; set; }
    public bool IsRetired { get; set; }
}
