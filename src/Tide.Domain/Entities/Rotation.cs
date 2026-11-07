using Tide.Domain.Common;
using Tide.Domain.Enums;

namespace Tide.Domain.Entities;

/// <summary>An analyst in the brief rotation, in the order weeks are handed out.</summary>
public class RotationMember
{
    public string UserId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>Who presents the brief for a week (Monday to Friday), by its Monday. Days can be overridden by an approved swap or cover.</summary>
public class RotationWeek
{
    public DateOnly WeekStart { get; set; }
    public string AnalystId { get; set; } = string.Empty;
    public string AnalystName { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public string UpdatedBy { get; set; } = string.Empty;
}

/// <summary>
/// A request to change who presents the brief. A cover hands the requester's days (<see cref="GiveFrom"/> to <see cref="GiveTo"/>) to
/// the partner; a swap also hands the partner's days (<see cref="TakeFrom"/> to <see cref="TakeTo"/>) to the requester.
/// Only the partner approves; the manager is told. Once approved, the days become <see cref="RotationOverride"/>s.
/// </summary>
public class RotationChange : Entity
{
    public RotationChangeKind Kind { get; set; }

    /// <summary>Whose duty is being given away, and who's taking it.</summary>
    public string RequesterId { get; set; } = string.Empty;
    public string RequesterName { get; set; } = string.Empty;
    public string PartnerId { get; set; } = string.Empty;
    public string PartnerName { get; set; } = string.Empty;

    /// <summary>The requester's days the partner takes on (business days in the range).</summary>
    public DateOnly GiveFrom { get; set; }
    public DateOnly GiveTo { get; set; }

    /// <summary>For a swap: the partner's days the requester takes on in return.</summary>
    public DateOnly? TakeFrom { get; set; }
    public DateOnly? TakeTo { get; set; }

    public string? Note { get; set; }
    public RotationChangeStatus Status { get; set; } = RotationChangeStatus.Pending;

    /// <summary>Who asked (the requester, or an administrator arranging cover for them).</summary>
    public string CreatedById { get; set; } = string.Empty;
    public string CreatedByName { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset? DecidedAtUtc { get; set; }
    public string? DecidedByName { get; set; }
    public string? DecisionNote { get; set; }
}

/// <summary>
/// A day the team doesn't work besides weekends (Administration › Organization settings): no brief is held or expected,
/// nothing is sent, and it doesn't count towards an action's deadline.
/// </summary>
public class Holiday
{
    public const int MaxName = 100;

    public DateOnly Date { get; set; }
    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// One business day's presenting analyst when it differs from the week's: from an approved change, or, with no change,
/// a day kept for whoever presented it when an administrator gave the week to someone else afterwards.
/// </summary>
public class RotationOverride
{
    public DateOnly Date { get; set; }
    public string AnalystId { get; set; } = string.Empty;
    public string AnalystName { get; set; } = string.Empty;

    /// <summary>The approved change that moved the day; null for a day kept when its week changed hands.</summary>
    public Guid? ChangeId { get; set; }
}
