using Tide.Domain.Enums;

namespace Tide.Application.Actions;

/// <summary>What is done to each of several actions chosen together on the Actions list (<see cref="ActionService.ManyAsync"/>).</summary>
public enum ManyKind
{
    /// <summary>Take each on, as Take it does.</summary>
    Take,

    /// <summary>Give each to one person (or nobody), as the meeting's Assign does.</summary>
    Assign,

    /// <summary>Move each to an open status, or close it Cancelled or Not applicable, as the Status menu does.</summary>
    Move,

    /// <summary>Set when each is due, leaving whoever holds it.</summary>
    SetDue,

    /// <summary>Hand each to another team, with one reason, as Hand off does.</summary>
    HandOff,

    /// <summary>Close each sweep or hunt Done (No hits), which its queries on record must bear out.</summary>
    NoHitsClose,
}

/// <summary>
/// One change for several actions: what (<see cref="Kind"/>) and the one detail it needs, asked once for all of them:
/// the person, the status (with a reason for Cancelled or Not applicable), the due date, or the team and why.
/// </summary>
public sealed record ManyOp(ManyKind Kind)
{
    /// <summary>Assign: the person, or null for nobody.</summary>
    public string? AssigneeId { get; init; }

    /// <summary>Move: the status.</summary>
    public ActionStatus To { get; init; }

    /// <summary>Move: the note for each one's history (the reason it's cancelled or not applicable); HandOff: why.</summary>
    public string? Reason { get; init; }

    /// <summary>SetDue: the date.</summary>
    public DateOnly? Due { get; init; }

    /// <summary>HandOff: the team.</summary>
    public Guid ToTeamId { get; init; }

    public static ManyOp Take() => new(ManyKind.Take);
    public static ManyOp AssignTo(string? assigneeId) => new(ManyKind.Assign) { AssigneeId = assigneeId };
    public static ManyOp MoveTo(ActionStatus to, string? reason = null) => new(ManyKind.Move) { To = to, Reason = reason };
    public static ManyOp DueOn(DateOnly due) => new(ManyKind.SetDue) { Due = due };
    public static ManyOp HandTo(Guid teamId, string reason) => new(ManyKind.HandOff) { ToTeamId = teamId, Reason = reason };
    public static ManyOp NoHits() => new(ManyKind.NoHitsClose);

    /// <summary>Whether a move to <paramref name="to"/> asks why (closing without doing it).</summary>
    public static bool AsksWhy(ActionStatus to) => to is ActionStatus.Cancelled or ActionStatus.NotApplicable;
}

/// <summary>One action the change wasn't made to, and why ("A-31 changed since you looked").</summary>
public sealed record ManyRefusal(Guid Id, int Number, string Why);

/// <summary>
/// What came of a change to several actions: the numbers it was made to and those it wasn't, each with why. Each action is
/// its own piece of work, so one refused leaves the rest done.
/// </summary>
public sealed record ManyResult(IReadOnlyList<int> Done, IReadOnlyList<ManyRefusal> Refused)
{
    /// <summary>"4 done, 1 refused: A-31 changed since you looked".</summary>
    public string Text => $"{Done.Count} done" + (Refused.Count == 0 ? "" : $", {Refused.Count} refused: " + string.Join("; ", Refused.Select(r => r.Why)));
}
