using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.Application.Actions;

/// <summary>Filters on actions that run in the database, each from <see cref="ActionItem"/>'s one list of statuses.</summary>
internal static class ActionQueries
{
    /// <summary>The first day of "the last <paramref name="days"/> days": today and the days before it, in the org's
    /// calendar (closed and due dates are org days, and every day counts, working or not).</summary>
    public static DateOnly LastDaysFrom(DateOnly today, int days) => today.AddDays(1 - days);

    /// <summary>The first day of "the last 7 days" (Workload, the digest).</summary>
    public static DateOnly LastSevenDaysFrom(DateOnly today) => LastDaysFrom(today, 7);

    /// <summary>The open actions: open, in progress, blocked or tracking (<see cref="ActionItem.IsOpen"/>).</summary>
    public static IQueryable<ActionItem> WhereOpen(this IQueryable<ActionItem> q) => q.Where(a => ActionItem.OpenStatuses.Contains(a.Status));

    /// <summary>The actions that met their due date: Done on or before it (<see cref="SlaState.Met"/>, as
    /// <c>ActionSla.StateOf</c> reads it, a Done with no closed date included). The database's twin of that rule, so a
    /// count in SQL and a state in C# agree. Cancelled and not applicable ones neither met nor missed it.</summary>
    public static IQueryable<ActionItem> WhereMet(this IQueryable<ActionItem> q) =>
        q.Where(a => a.DueDate != null && a.Status == ActionStatus.Done && (a.ClosedDate == null || a.ClosedDate <= a.DueDate));

    /// <summary>The actions late on <paramref name="today"/>: Done after their due date (<see cref="SlaState.Missed"/>), or
    /// open past it (<see cref="SlaState.Overdue"/>), as <c>ActionSla.StateOf</c> reads them.</summary>
    public static IQueryable<ActionItem> WhereLate(this IQueryable<ActionItem> q, DateOnly today) =>
        q.Where(a => (a.Status == ActionStatus.Done && a.ClosedDate > a.DueDate)
                     || (ActionItem.OpenStatuses.Contains(a.Status) && a.DueDate < today));

    /// <summary>The open actions past their due date on <paramref name="today"/> (<see cref="SlaState.Overdue"/>).</summary>
    public static IQueryable<ActionItem> WhereOverdue(this IQueryable<ActionItem> q, DateOnly today) =>
        q.WhereOpen().Where(a => a.DueDate < today);

    /// <summary>The escalated actions: open, and flagged by the escalation ladder for the due date they have
    /// (<see cref="ActionItem.EscalatedFor"/>). Served by the filtered index on <c>EscalatedForDue</c>.</summary>
    public static IQueryable<ActionItem> WhereEscalated(this IQueryable<ActionItem> q) =>
        q.Where(a => a.EscalatedForDue != null && a.EscalatedForDue == a.DueDate).WhereOpen();

    /// <summary>The closed actions: done, cancelled or not applicable.</summary>
    public static IQueryable<ActionItem> WhereClosed(this IQueryable<ActionItem> q) => q.Where(a => ActionItem.ClosedStatuses.Contains(a.Status));

    /// <summary>The actions that count toward their team's active limit (<see cref="ActionItem.IsActiveStatus"/>).</summary>
    public static IQueryable<ActionItem> WhereActive(this IQueryable<ActionItem> q) => q.Where(a => ActionItem.ActiveStatuses.Contains(a.Status));

    /// <summary>The open actions, and those closed on or after <paramref name="since"/>: a team's load and its week.</summary>
    public static IQueryable<ActionItem> WhereOpenOrClosedSince(this IQueryable<ActionItem> q, DateOnly since) =>
        q.Where(a => ActionItem.OpenStatuses.Contains(a.Status) || (a.ClosedDate != null && a.ClosedDate >= since));

    /// <summary>The open actions, and those closed at or after <paramref name="since"/>: the board's cards.</summary>
    public static IQueryable<ActionItem> WhereOpenOrClosedSinceUtc(this IQueryable<ActionItem> q, DateTimeOffset since) =>
        q.Where(a => ActionItem.OpenStatuses.Contains(a.Status) || (a.ClosedAtUtc != null && a.ClosedAtUtc >= since));
}
