using System.Globalization;
using Tide.Application.Content;
using Tide.Application.Org;
using Tide.Domain.Enums;

namespace Tide.Application.Actions;

/// <summary>Which open actions the escalation ladder climbs. One choice for the whole ladder.</summary>
public enum EscalationScope
{
    All = 0,

    /// <summary>Only the parts of a split (an action with a parent).</summary>
    PartsOnly = 1,

    /// <summary>Only external teams' actions.</summary>
    ExternalOnly = 2,
}

/// <summary>What a step of the ladder does, from a fixed menu; a step may do several.</summary>
[Flags]
public enum EscalationDoes
{
    None = 0,

    /// <summary>Email the owning team: an external team's reminder, or the assignee (else the team mailbox, else its members).</summary>
    Remind = 1,

    /// <summary>Email the owning team's escalation contact.</summary>
    CopyContact = 2,

    /// <summary>Move its priority up one, never above P1.</summary>
    RaisePriority = 4,

    /// <summary>Mark it escalated: a badge, Today's Escalated list and the meeting's overdue list.</summary>
    Flag = 8,
}

/// <summary>
/// One step of the ladder: when, as <paramref name="Offset"/> business days from the due date (negative before it, 0 the
/// due day, positive after it), and what it does.
/// </summary>
public sealed record EscalationStep(int Offset, EscalationDoes Does)
{
    /// <summary>As the setting stores it: "before 2: remind", "due: remind", "after 7: raise, flag".</summary>
    public string Text => (Offset switch
    {
        < 0 => "before " + (-Offset).ToString(CultureInfo.InvariantCulture),
        0 => "due",
        _ => "after " + Offset.ToString(CultureInfo.InvariantCulture),
    }) + ": " + string.Join(", ", EscalationLadder.Menu.Where(m => Does.HasFlag(m.Does)).Select(m => m.Word));

    /// <summary>When it runs, as the page says it: "2 business days before", "On the due day", "7 business days after".</summary>
    public string When => Offset switch
    {
        < 0 => Words.Plural(-Offset, "business day") + " before",
        0 => "On the due day",
        _ => Words.Plural(Offset, "business day") + " after",
    };

    /// <summary>What it does, as the page says it: "Raise priority and flag escalated".</summary>
    public string What
    {
        get
        {
            var names = EscalationLadder.Menu.Where(m => Does.HasFlag(m.Does))
                .Select((m, i) => i == 0 ? m.Name : char.ToLowerInvariant(m.Name[0]) + m.Name[1..]).ToList();
            return names.Count <= 1 ? string.Concat(names) : string.Join(", ", names[..^1]) + " and " + names[^1];
        }
    }

    /// <summary>Its part of a mark's key: the same step is the same key, wherever it sits in the list.</summary>
    internal string Key => Offset.ToString("+0;-0;0", CultureInfo.InvariantCulture) + "." + ((int)Does).ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// The escalation ladder's settings (config section <c>Notifications:Escalation</c>, and Teams &amp; SLA in the app). Off by
/// default; its steps are the defaults until an administrator saves others.
/// </summary>
public sealed class EscalationOptions
{
    public bool Enabled { get; set; }
    public EscalationScope Scope { get; set; } = EscalationScope.All;

    /// <summary>The steps as text (<see cref="EscalationLadder.Parse"/>), in order.</summary>
    public string Steps { get; set; } = EscalationLadder.DefaultText;
}

/// <summary>
/// The escalation ladder's rules, kept pure so they're tested on their own: how its steps are written and checked, which
/// step is due on a day, and what one does to an action. Days are the organization's working days, holidays included.
/// </summary>
public static class EscalationLadder
{
    public const int MaxSteps = 8, MaxDays = 60;

    /// <summary>The fixed menu of what a step does: its flag, the word the setting stores, and its name on the page.</summary>
    public static readonly IReadOnlyList<(EscalationDoes Does, string Word, string Name)> Menu =
    [
        (EscalationDoes.Remind, "remind", "Remind the team"),
        (EscalationDoes.CopyContact, "contact", "Copy the escalation contact"),
        (EscalationDoes.RaisePriority, "raise", "Raise priority"),
        (EscalationDoes.Flag, "flag", "Flag escalated"),
    ];

    /// <summary>What an administrator gets on turning it on: remind before the date, then copy the escalation contact,
    /// then raise and flag, then flag again (a no-op if it already is) so the meeting sees it.</summary>
    public static readonly IReadOnlyList<EscalationStep> Defaults =
    [
        new(-2, EscalationDoes.Remind),
        new(3, EscalationDoes.CopyContact),
        new(7, EscalationDoes.RaisePriority | EscalationDoes.Flag),
        new(14, EscalationDoes.Flag),
    ];

    public static string DefaultText => Format(Defaults);

    public static string Format(IEnumerable<EscalationStep> steps) => string.Join("; ", steps.Select(s => s.Text));

    /// <summary>
    /// The steps written as "before 2: remind; after 3: contact; after 7: raise, flag" (steps apart by semicolons or lines),
    /// checked (<see cref="Check"/>). Blank is no steps. Throws <see cref="ArgumentException"/> saying what's wrong.
    /// </summary>
    public static IReadOnlyList<EscalationStep> Parse(string? text)
    {
        var parts = (text ?? "").Split([';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return Check([.. parts.Select((p, i) => ParseStep(p, i + 1))]);
    }

    /// <summary>The steps of a saved value, or none when it no longer reads (a hand-edited server value): the ladder then
    /// climbs nothing rather than guessing.</summary>
    public static IReadOnlyList<EscalationStep> ParseOrNone(string? text)
    {
        try { return Parse(text); }
        catch (ArgumentException) { return []; }
    }

    /// <summary>The value as it's stored (the setting's check).</summary>
    public static string Normalize(string? text) => Format(Parse(text));

    /// <summary>
    /// The steps if they can be saved: at most <see cref="MaxSteps"/>, days 0 to <see cref="MaxDays"/>, each doing
    /// something, and no two the same. Throws <see cref="ArgumentException"/> naming the step.
    /// </summary>
    public static IReadOnlyList<EscalationStep> Check(IReadOnlyList<EscalationStep> steps)
    {
        if (steps.Count > MaxSteps) throw new ArgumentException($"The ladder has at most {MaxSteps} steps.");
        for (var i = 0; i < steps.Count; i++)
        {
            var s = steps[i];
            if (Math.Abs(s.Offset) > MaxDays) throw OutOfRange(i + 1);
            if (s.Does == EscalationDoes.None || (s.Does & ~All) != 0) throw new ArgumentException($"Step {i + 1} does nothing: choose what it does.");
            var same = steps.Take(i).ToList().FindIndex(t => t == s);
            if (same >= 0) throw new ArgumentException($"Steps {same + 1} and {i + 1} are the same: remove one.");
        }
        return steps;
    }

    private const EscalationDoes All = EscalationDoes.Remind | EscalationDoes.CopyContact | EscalationDoes.RaisePriority | EscalationDoes.Flag;

    private static ArgumentException OutOfRange(int n) => new($"Step {n}: a step is 0 to {MaxDays} business days from the due date.");

    // "after 7: raise, flag".
    private static EscalationStep ParseStep(string text, int n)
    {
        var colon = text.IndexOf(':');
        if (colon < 0) throw new ArgumentException($"Step {n} (\"{text}\") is a when and what it does, such as \"after 3: contact\".");
        var when = text[..colon].Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var offset = when switch
        {
            ["due"] => 0,
            ["before", var d] => -Days(d, n),
            ["after", var d] => Days(d, n),
            _ => throw new ArgumentException($"Step {n}: when is \"before N\", \"due\" or \"after N\" business days."),
        };
        var does = EscalationDoes.None;
        foreach (var word in text[(colon + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var m = Menu.FirstOrDefault(x => x.Word.Equals(word, StringComparison.OrdinalIgnoreCase));
            if (m.Word is null) throw new ArgumentException($"Step {n}: \"{word}\" isn't one of {string.Join(", ", Menu.Select(x => x.Word))}.");
            does |= m.Does;
        }
        return new EscalationStep(offset, does);
    }

    // A number of days; whether it's in range is Check's.
    private static int Days(string text, int n) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var d) ? d : throw OutOfRange(n);

    /// <summary>
    /// The day a step comes due for an action due <paramref name="due"/>: so many working days before it (counting from
    /// the working day it falls on, or the one before when it's a day off, as an external team's reminder does), the due
    /// day itself the same way, or so many working days after it.
    /// </summary>
    public static DateOnly DayOf(EscalationStep step, DateOnly due, WorkingDays days)
    {
        if (step.Offset <= 0) return ExternalCadence.WindowStart(due, -step.Offset, days);
        var d = due;
        for (var i = 0; i < step.Offset; i++) d = days.Next(d);
        return d;
    }

    /// <summary>
    /// The steps to run on <paramref name="today"/>: those whose day is the latest to have come (more than one when they
    /// share it). Earlier ones it passed over (the ladder turned on, or the date moved, after their day) never run, so
    /// turning it on doesn't send a backlog of emails at once; and for a fixed due date an earlier step can't be the latest
    /// again, so they need no mark. The caller leaves out steps already marked.
    /// </summary>
    public static IReadOnlyList<EscalationStep> Plan(IReadOnlyList<EscalationStep> steps, DateOnly due, DateOnly today, WorkingDays days)
    {
        var come = steps.Select(s => (Step: s, Day: DayOf(s, due, days))).Where(x => x.Day <= today).ToList();
        if (come.Count == 0) return [];
        var latest = come.Max(x => x.Day);
        return [.. come.Where(x => x.Day == latest).Select(x => x.Step)];
    }

    /// <summary>One priority up, never above P1.</summary>
    public static Priority Raised(Priority p) => p <= Priority.P1 ? Priority.P1 : p - 1;

    /// <summary>Where an action stands on the ladder's day, for its history: "2 business days before its due date", "on its
    /// due date", "7 business days late".</summary>
    public static string Lateness(DateOnly due, DateOnly today, WorkingDays days) => ActionSla.BusinessDaysUntil(today, due, days) switch
    {
        > 0 and var n => Words.Plural(n, "business day") + " before its due date",
        0 => "on its due date",
        var n => Words.Plural(-n, "business day") + " late",
    };
}
