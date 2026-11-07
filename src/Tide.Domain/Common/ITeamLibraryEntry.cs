namespace Tide.Domain.Common;

/// <summary>
/// An entry in one of the team's libraries (saved queries, outreach templates, playbooks): kept by whoever works actions,
/// named with who saved and last changed it, retired rather than lost, and versioned so an edit begun on an older copy
/// is refused.
/// </summary>
public interface ITeamLibraryEntry
{
    Guid Id { get; }

    /// <summary>Who saved it (<see cref="AuditableEntity.CreatedBy"/>): they may delete it.</summary>
    string CreatedBy { get; }

    /// <summary>The names <see cref="AuditableEntity.CreatedBy"/> and <see cref="AuditableEntity.ModifiedBy"/> had then.</summary>
    string CreatedByName { get; set; }
    string? ModifiedByName { get; set; }

    bool IsRetired { get; set; }

    /// <summary>Moves on every edit, so an edit begun on an older copy is refused.</summary>
    Guid EditVersion { get; set; }
}
