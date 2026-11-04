using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.Application.Actions;

/// <summary>Filters on actions that run in the database.</summary>
internal static class ActionQueries
{
    /// <summary>The first day of "the last 7 days" (Workload, the digest): today and the six days before, in the org's
    /// calendar (closed dates are org days, and every day counts, working or not).</summary>
    public static DateOnly LastSevenDaysFrom(DateOnly today) => today.AddDays(-6);

    /// <summary>The open actions: open, in progress or blocked (<see cref="ActionItem.IsOpen"/>, which the database can't
    /// translate).</summary>
    public static IQueryable<ActionItem> WhereOpen(this IQueryable<ActionItem> q) =>
        q.Where(a => a.Status == ActionStatus.Open || a.Status == ActionStatus.InProgress || a.Status == ActionStatus.Blocked);

    /// <summary>The open actions, and those closed on or after <paramref name="since"/>: a team's load and its week.</summary>
    public static IQueryable<ActionItem> WhereOpenOrClosedSince(this IQueryable<ActionItem> q, DateOnly since) =>
        q.Where(a => a.Status == ActionStatus.Open || a.Status == ActionStatus.InProgress || a.Status == ActionStatus.Blocked
                     || (a.ClosedDate != null && a.ClosedDate >= since));
}
