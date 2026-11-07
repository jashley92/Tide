using Tide.Domain.Entities;

namespace Tide.Application.Actions;

/// <summary>Filters on actions that run in the database, each from <see cref="ActionItem"/>'s one list of statuses.</summary>
internal static class ActionQueries
{
    /// <summary>The first day of "the last 7 days" (Workload, the digest): today and the six days before, in the org's
    /// calendar (closed dates are org days, and every day counts, working or not).</summary>
    public static DateOnly LastSevenDaysFrom(DateOnly today) => today.AddDays(-6);

    /// <summary>The open actions: open, in progress, blocked or tracking (<see cref="ActionItem.IsOpen"/>).</summary>
    public static IQueryable<ActionItem> WhereOpen(this IQueryable<ActionItem> q) => q.Where(a => ActionItem.OpenStatuses.Contains(a.Status));

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
