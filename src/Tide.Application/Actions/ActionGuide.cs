using Tide.Application.Content;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.Application.Actions;

/// <summary>The one button an action's page offers first. Each is a flow the page has anyway.</summary>
public enum PrimaryStep
{
    /// <summary>Nothing to offer: the action is closed, or the viewer can't work it.</summary>
    None = 0,

    /// <summary>Opens the Outreach tool's "Write the email".</summary>
    WriteEmail,

    /// <summary>Records the reply to the email waiting longest (the Outreach tool's "Record the reply").</summary>
    RecordReply,

    /// <summary>Opens Record an update.</summary>
    RecordUpdate,

    /// <summary>Takes it, as the Take it button does.</summary>
    TakeIt,

    /// <summary>Moves it from Open to In progress.</summary>
    StartWork,

    /// <summary>Moves it from Blocked back to In progress.</summary>
    Unblock,

    /// <summary>Moves it to Done, which asks for the outcome first.</summary>
    CloseWithOutcome,
}

/// <summary>The tools in the action page's side drawer, each an existing panel.</summary>
public enum ActionTool { Checklist = 0, Timeline, Hunt, Outreach }

/// <summary>What the page's Next line is about. The order is the order of importance.</summary>
public enum NextKind { PartLate = 0, NoReply, Escalated, Blocked, Overdue }

/// <summary>What a Next line's link does: each is a flow the page has anyway.</summary>
public enum NextLink
{
    /// <summary>The late part's own page, where its team is chased.</summary>
    ChasePart,

    /// <summary>The late part's page with Record an update open, for their reply.</summary>
    PartReply,

    RecordReply,
    WriteEmail,
    RecordUpdate,
    HandOff,

    /// <summary>The comment box, to say what it's waiting on.</summary>
    Comment,

    /// <summary>The edit form, to move the due date.</summary>
    Edit,
}

/// <summary>What the page knows about an action and who's looking, to choose its primary button.</summary>
/// <param name="CanWork">Whether the viewer may work it (ActionDetail.CanWork).</param>
/// <param name="Assigned">Whether someone has it.</param>
/// <param name="External">Whether its team works outside TIDE (no members, so nobody takes it).</param>
/// <param name="OpenParts">How many of its parts are still open.</param>
/// <param name="Emails">How many emails it has (an Outreach action's), sent or replied.</param>
/// <param name="Waiting">Whether one of them is waiting for a reply.</param>
public sealed record PrimaryFacts(ActionStatus Status, ActionKind Kind, bool CanWork, bool Assigned, bool External,
    int OpenParts = 0, int Emails = 0, bool Waiting = false);

/// <summary>An email waiting for its reply, as the Next line names it: to whom, the day it went, and the day its
/// follow-up reminder goes (null when the reminder is off).</summary>
public sealed record WaitingEmail(string To, DateOnly SentOn, DateOnly? FollowUpOn);

/// <summary>What the page knows about an action on <paramref name="Today"/>, to choose its Next line.</summary>
/// <param name="Parts">The parts of the action whose page this is (none on a part's page, or with none).</param>
/// <param name="EscalatedOn">The day it was escalated, while it still is.</param>
public sealed record NextFacts(ActionStatus Status, DateOnly? Due, DateOnly Today, bool CanWork,
    IReadOnlyList<LineageNode>? Parts = null, WaitingEmail? Waiting = null, DateOnly? EscalatedOn = null);

/// <summary>The page's Next line: what needs doing, in words, and the links that do it.</summary>
/// <param name="Part">The late part's number (<see cref="NextKind.PartLate"/>), for its links.</param>
public sealed record NextStep(NextKind Kind, string Text, IReadOnlyList<(NextLink Link, string Label)> Links, int? Part = null);

/// <summary>
/// What an action's page puts first: its primary button (<see cref="Primary"/>), its Next line (<see cref="Next"/>) and
/// which tools its drawer has (<see cref="Tools"/>). Kept pure, so the rules are tested on their own; the page maps each
/// answer to a flow it already has.
/// </summary>
public static class ActionGuide
{
    /// <summary>
    /// The primary button, by the first rule that applies: nothing for a closed action or a viewer who can't work it;
    /// an Outreach action with no email yet writes one; an email waiting records its reply; work followed rather than
    /// done here (Tracking, an external team's, or a parent with open parts) records an update; an open action nobody
    /// has is taken; otherwise the status move that fits: Open starts, Blocked unblocks, and In progress closes with
    /// an outcome.
    /// </summary>
    public static PrimaryStep Primary(PrimaryFacts f)
    {
        if (!f.CanWork || ActionItem.IsClosedStatus(f.Status)) return PrimaryStep.None;
        if (f.Kind == ActionKind.Outreach && f.Emails == 0) return PrimaryStep.WriteEmail;
        if (f.Waiting) return PrimaryStep.RecordReply;
        if (f.Status == ActionStatus.Tracking || f.External || f.OpenParts > 0) return PrimaryStep.RecordUpdate;
        if (!f.Assigned) return PrimaryStep.TakeIt;
        return f.Status switch
        {
            ActionStatus.Open => PrimaryStep.StartWork,
            ActionStatus.Blocked => PrimaryStep.Unblock,
            _ => PrimaryStep.CloseWithOutcome,
        };
    }

    /// <summary>The primary button's words.</summary>
    public static string Label(PrimaryStep s) => s switch
    {
        PrimaryStep.WriteEmail => "Write the email",
        PrimaryStep.RecordReply => "Record the reply",
        PrimaryStep.RecordUpdate => "Record an update",
        PrimaryStep.TakeIt => "Take it",
        PrimaryStep.StartWork => "Start work",
        PrimaryStep.Unblock => "Unblock",
        PrimaryStep.CloseWithOutcome => "Close with outcome",
        _ => "",
    };

    /// <summary>
    /// The Next line, or null when nothing needs doing (or the viewer can't work it, or it's closed). Only the most
    /// important one is said, in this order: a part overdue (the latest of them, with how many more), an email past its
    /// follow-up day with no reply, escalated, blocked, then past its own due date.
    /// </summary>
    public static NextStep? Next(NextFacts f)
    {
        if (!f.CanWork || ActionItem.IsClosedStatus(f.Status)) return null;
        var late = (f.Parts ?? []).Where(p => p.IsOpen && p.State == LineageState.Overdue)
            .OrderByDescending(p => ActionSla.DaysLate(p.Due, f.Today)).ThenBy(p => p.Number).ToList();
        if (late.Count > 0)
        {
            var p = late[0];
            var more = late.Count > 1 ? $" {Words.Plural(late.Count - 1, "more part")} overdue." : "";
            return new NextStep(NextKind.PartLate, $"{p.Team} is {ActionSla.LateText(p.Due, f.Today)} on A-{p.Number}.{more}",
                [(NextLink.ChasePart, "Chase them"), (NextLink.PartReply, "Record their reply")], p.Number);
        }
        if (f.Waiting is { FollowUpOn: { } followUp } w && followUp <= f.Today)
            return new NextStep(NextKind.NoReply,
                $"No reply from {w.To} since {Words.MonthDay(w.SentOn)}; its follow-up was due {Words.MonthDay(followUp)}.",
                [(NextLink.RecordReply, "Record the reply"), (NextLink.WriteEmail, "Write a follow-up")]);
        if (f.EscalatedOn is { } escalated)
        {
            var due = ActionSla.LateText(f.Due, f.Today) ?? (f.Due is { } d ? "due " + Words.MonthDay(d) : "no due date");
            return new NextStep(NextKind.Escalated, $"Escalated on {Words.MonthDay(escalated)}: {due}.",
                [(NextLink.RecordUpdate, "Record an update"), (NextLink.HandOff, "Hand off")]);
        }
        if (f.Status == ActionStatus.Blocked)
            return new NextStep(NextKind.Blocked, "It's blocked.",
                [(NextLink.Comment, "Say what it's waiting on"), (NextLink.HandOff, "Hand off")]);
        if (ActionSla.LateText(f.Due, f.Today) is { } overdue)
            return new NextStep(NextKind.Overdue, $"It's {overdue}: it was due {Words.MonthDay(f.Due!.Value)}.",
                [(NextLink.RecordUpdate, "Record an update"), (NextLink.Edit, "Move the due date")]);
        return null;
    }

    /// <summary>A tool's key in the address (<c>?tool=hunt</c>) and the tab's id.</summary>
    public static string Key(ActionTool t) => t.ToString().ToLowerInvariant();

    /// <summary>A tool's tab name.</summary>
    public static string Name(ActionTool t) => t == ActionTool.Hunt ? "Hunt queries" : t.ToString();

    /// <summary>
    /// The drawer's tools, in order: the checklist always; the timeline on a parent whose parts have a due date; hunt
    /// queries for a sweep or hunt, or when it has any; outreach for an Outreach action, or when it has emails.
    /// </summary>
    public static IReadOnlyList<ActionTool> Tools(ActionKind kind, bool datedParts, int hunts, int emails)
    {
        var tools = new List<ActionTool> { ActionTool.Checklist };
        if (datedParts) tools.Add(ActionTool.Timeline);
        if (hunts > 0 || kind is ActionKind.IocSweep or ActionKind.ThreatHunt) tools.Add(ActionTool.Hunt);
        if (emails > 0 || kind == ActionKind.Outreach) tools.Add(ActionTool.Outreach);
        return tools;
    }

    /// <summary>
    /// The tool the drawer opens on: the one the address asks for (<paramref name="asked"/>, a <see cref="Key"/>) when it's
    /// there, else the kind's own (outreach for Outreach, hunt queries for a sweep or hunt), else the checklist.
    /// </summary>
    public static ActionTool Opening(string? asked, ActionKind kind, IReadOnlyList<ActionTool> tools)
    {
        var wanted = tools.FirstOrDefault(t => string.Equals(Key(t), asked?.Trim(), StringComparison.OrdinalIgnoreCase), (ActionTool)(-1));
        if (wanted >= 0) return wanted;
        var own = kind switch
        {
            ActionKind.Outreach => ActionTool.Outreach,
            ActionKind.IocSweep or ActionKind.ThreatHunt => ActionTool.Hunt,
            _ => ActionTool.Checklist,
        };
        return tools.Contains(own) ? own : ActionTool.Checklist;
    }
}
