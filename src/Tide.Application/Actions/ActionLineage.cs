using Tide.Application.Content;
using Tide.Application.Org;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.Application.Actions;

/// <summary>How a part stands, as the lineage and the timeline colour it.</summary>
public enum LineageState
{
    /// <summary>Closed in any way: Done, Cancelled or Not applicable.</summary>
    Done = 0,
    OnTrack = 1,

    /// <summary>Due today or the next business day (<see cref="SlaState.DueSoon"/>).</summary>
    DueSoon = 2,
    Overdue = 3,

    /// <summary>Followed while another team does it, and not overdue.</summary>
    Tracking = 4,
}

/// <summary>The article an action was raised from, as the lineage starts with it.</summary>
public sealed record LineageArticle(Guid Id, string Title, IReadOnlyList<string> Cves);

/// <summary>
/// One action in a lineage: the parent or one of its parts. <see cref="Started"/> is the day it was created (a split
/// part's is the split's), in the organization's calendar; <see cref="State"/> is worked out once, when the lineage is
/// read; <see cref="HandedFrom"/> is the team it was last handed from (the parent's only).
/// </summary>
public sealed record LineageNode(Guid Id, int Number, string Title, string Team, bool External, ActionStatus Status,
    DateOnly? Due, DateOnly? Closed, DateOnly Started, LineageState State, string? HandedFrom = null)
{
    public bool IsOpen => !ActionItem.IsClosedStatus(Status);
}

/// <summary>
/// An action that was split, from where it came to where it went: its article (if any), the parent and each part, in
/// number order. <see cref="Current"/> is the action whose page shows it (the parent, or one of the parts);
/// <see cref="Rollup"/> is the parent's line from these parts (<see cref="ActionRollup.Of"/>).
/// </summary>
public sealed record ActionLineage(LineageArticle? Article, LineageNode Parent, IReadOnlyList<LineageNode> Parts, Guid Current, string Rollup);

/// <summary>The lineage's rules, kept pure so they're tested on their own.</summary>
public static class LineageRules
{
    /// <summary>The states in the order the legend lists them.</summary>
    public static readonly IReadOnlyList<LineageState> Legend =
        [LineageState.Done, LineageState.OnTrack, LineageState.DueSoon, LineageState.Overdue, LineageState.Tracking];

    /// <summary>
    /// Where a part stands on <paramref name="today"/>. Closed wins; then overdue, even while tracked, since an overdue part
    /// is the one to chase whoever does it; then tracking; then due soon, by the SLA's window. No due date is on track.
    /// </summary>
    public static LineageState StateOf(ActionStatus status, DateOnly? due, DateOnly today, WorkingDays? workingDays = null)
    {
        if (ActionItem.IsClosedStatus(status)) return LineageState.Done;
        var sla = ActionSla.StateOf(status, due, null, today, workingDays);
        if (sla == SlaState.Overdue) return LineageState.Overdue;
        if (status == ActionStatus.Tracking) return LineageState.Tracking;
        return sla == SlaState.DueSoon ? LineageState.DueSoon : LineageState.OnTrack;
    }

    public static string StateName(LineageState s) => s switch
    {
        LineageState.Done => "Done or closed",
        LineageState.DueSoon => "Due soon",
        LineageState.Overdue => "Overdue",
        LineageState.Tracking => "Tracking",
        _ => "On track",
    };

    /// <summary>"Closed Nov 18", "3 days late", "Due Nov 20" or "No due date".</summary>
    public static string DueText(LineageNode n, DateOnly today)
    {
        if (!n.IsOpen) return n.Closed is { } c ? "Closed " + Words.MonthDay(c) : "Closed";
        if (n.Due is not { } d) return "No due date";
        return ActionSla.LateText(d, today) ?? "Due " + Words.MonthDay(d);
    }

    /// <summary>"From Security Operations → Network Engineering" when it was handed over, else null.</summary>
    public static string? HandoffText(LineageNode n) => n.HandedFrom is { } from ? $"From {from} → {n.Team}" : null;
}

/// <summary>One part's row on the timeline. Positions are percentages of the scale's width.</summary>
/// <param name="Start">Where its bar starts: the day it was created, or its due date if that came first.</param>
/// <param name="Width">Its bar's width, to the end of its due day; 0 with no due date.</param>
/// <param name="Over">How far past the due date it ran, to today (open) or to the day it closed (late), drawn in the danger colour.</param>
/// <param name="ClosedAt">The middle of the day it closed, for its mark, when it closed.</param>
/// <param name="Text">The row's dates in words, for everyone and screen readers alike.</param>
public sealed record TimelineRow(LineageNode Part, double Start, double Width, double Over, double? ClosedAt, string Text);

/// <summary>A date on the scale and where it falls.</summary>
public sealed record TimelineTick(DateOnly Day, double At);

/// <summary>
/// A parent's parts on one scale, from the first one's creation to the last due date, close or today, whichever is
/// latest; <see cref="Today"/> is where today's marker goes.
/// </summary>
public sealed record RemediationTimeline(DateOnly From, DateOnly To, double Today, IReadOnlyList<TimelineRow> Rows, IReadOnlyList<TimelineTick> Ticks)
{
    /// <summary>The most dates the scale is labelled with, its ends included.</summary>
    public const int MaxTicks = 5;

    /// <summary>The timeline of <paramref name="parts"/> on <paramref name="today"/>, or null when none has a due date.</summary>
    public static RemediationTimeline? Build(IReadOnlyList<LineageNode> parts, DateOnly today)
    {
        if (!parts.Any(p => p.Due is not null)) return null;
        var from = parts.Min(p => p.Due is { } d && d < p.Started ? d : p.Started);
        if (today < from) from = today;
        var to = new[] { today }.Concat(parts.Select(p => p.Due).OfType<DateOnly>()).Concat(parts.Select(p => p.Closed).OfType<DateOnly>()).Max();
        // Each day is a cell: a bar runs to the end of its due day, and a mark sits in the middle of its day.
        double days = to.DayNumber - from.DayNumber + 1;
        double At(DateOnly d) => Math.Round((d.DayNumber - from.DayNumber) / days * 100, 2);
        double Mid(DateOnly d) => Math.Round((d.DayNumber - from.DayNumber + 0.5) / days * 100, 2);
        var rows = parts.Select(p => Row(p, today, At, Mid)).ToList();
        return new RemediationTimeline(from, to, Mid(today), rows, ScaleTicks(from, to, Mid));
    }

    private static TimelineRow Row(LineageNode p, DateOnly today, Func<DateOnly, double> at, Func<DateOnly, double> mid)
    {
        var closedAt = p.Closed is { } c0 ? mid(c0) : (double?)null;
        if (p.Due is not { } due) return new TimelineRow(p, at(p.Started), 0, 0, closedAt, Words(p, today));
        var start = due < p.Started ? due : p.Started;
        var end = at(due.AddDays(1));
        // An open part past due runs on to today; a part closed late, to the day it closed.
        var overTo = p.IsOpen ? (today > due ? today : (DateOnly?)null) : p.Closed is { } c && c > due ? c : null;
        var over = overTo is { } o ? Math.Round(at(o.AddDays(1)) - end, 2) : 0;
        return new TimelineRow(p, at(start), Math.Round(end - at(start), 2), over, closedAt, Words(p, today));
    }

    /// <summary>"Started Nov 2 · due Nov 20 · closed Nov 18, 2 days early", "… · 3 days late", or "… · no due date".</summary>
    public static string Words(LineageNode p, DateOnly today)
    {
        var parts = new List<string> { "Started " + Content.Words.MonthDay(p.Started), p.Due is { } d ? "due " + Content.Words.MonthDay(d) : "no due date" };
        if (!p.IsOpen && p.Closed is { } c)
        {
            var early = p.Due is { } due ? due.DayNumber - c.DayNumber : 0;
            parts.Add("closed " + Content.Words.MonthDay(c) + early switch
            {
                0 when p.Due is not null => ", on the day",
                0 => "",
                > 0 => $", {Content.Words.Plural(early, "day")} early",
                _ => $", {Content.Words.Plural(-early, "day")} late",
            });
        }
        else if (p.IsOpen && p.Due is { } due && ActionSla.LateText(due, today) is { } late) parts.Add(late);
        return string.Join(" · ", parts);
    }

    // The scale's ends and evenly spaced days between them, at most MaxTicks, each day once.
    private static List<TimelineTick> ScaleTicks(DateOnly from, DateOnly to, Func<DateOnly, double> at)
    {
        var span = to.DayNumber - from.DayNumber;
        var steps = Math.Min(MaxTicks - 1, Math.Max(1, span));
        return Enumerable.Range(0, steps + 1).Select(i => from.AddDays((int)Math.Round(span * (double)i / steps)))
            .Distinct().Select(d => new TimelineTick(d, at(d))).ToList();
    }
}
