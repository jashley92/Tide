using Tide.Application.Actions;

namespace Tide.Web.Components.Pages.Admin;

/// <summary>Before, on or after the due date: how a ladder step's "when" is chosen on the page.</summary>
public enum StepWhen
{
    Before = 0,
    Due = 1,
    After = 2,
}

/// <summary>One step of the ladder as Teams &amp; SLA edits it: a number of business days, before, on or after the due
/// date, and a box for each thing on the menu (<see cref="EscalationLadder.Menu"/>).</summary>
public sealed class StepRow
{
    public int Days { get; set; }
    public StepWhen When { get; set; } = StepWhen.After;
    public EscalationDoes Does { get; set; }

    public bool Has(EscalationDoes d) => Does.HasFlag(d);

    /// <summary>Ticks or unticks one thing it does.</summary>
    public void Set(EscalationDoes d, bool on) => Does = on ? Does | d : Does & ~d;

    public static StepRow From(EscalationStep s) => new()
    {
        Days = Math.Abs(s.Offset),
        When = s.Offset switch { < 0 => StepWhen.Before, 0 => StepWhen.Due, _ => StepWhen.After },
        Does = s.Does,
    };

    /// <summary>Business days from the due date: negative before it.</summary>
    public int Offset => When switch { StepWhen.Before => -Days, StepWhen.Due => 0, _ => Days };

    /// <summary>The step it says; days below 0 aren't one (the setting's check refuses the rest).</summary>
    public EscalationStep ToStep(int n) => Days >= 0
        ? new EscalationStep(Offset, Does)
        : throw new ArgumentException($"Step {n}: a step is 0 to {EscalationLadder.MaxDays} business days from the due date.");
}

/// <summary>
/// The escalation ladder as Teams &amp; SLA edits it: on or off, which actions it climbs, and its steps in order (added,
/// removed and moved here; checked when saved, by the setting's own check).
/// </summary>
public sealed class EscalationForm
{
    public bool On { get; set; }
    public EscalationScope Scope { get; set; }
    public List<StepRow> Steps { get; private set; } = [];

    public static EscalationForm From(EscalationOptions o) => new()
    {
        On = o.Enabled,
        Scope = o.Scope,
        Steps = [.. EscalationLadder.ParseOrNone(o.Steps).Select(StepRow.From)],
    };

    /// <summary>Whether another step may be added (<see cref="EscalationLadder.MaxSteps"/>).</summary>
    public bool CanAdd => Steps.Count < EscalationLadder.MaxSteps;

    /// <summary>A new step after the last: a business day after the latest one so far, doing nothing yet.</summary>
    public void Add()
    {
        if (!CanAdd) return;
        var last = Steps.Count == 0 ? 0 : Steps.Max(s => s.Offset);
        Steps.Add(new StepRow { When = StepWhen.After, Days = Math.Clamp(last + 1, 1, EscalationLadder.MaxDays) });
    }

    public void Remove(StepRow row) => Steps.Remove(row);

    /// <summary>Moves a step up (-1) or down (1) the list.</summary>
    public void Move(StepRow row, int by)
    {
        var at = Steps.IndexOf(row);
        var to = at + by;
        if (at < 0 || to < 0 || to >= Steps.Count) return;
        (Steps[at], Steps[to]) = (Steps[to], Steps[at]);
    }

    /// <summary>Back to the defaults (<see cref="EscalationLadder.Defaults"/>).</summary>
    public void UseDefaults() => Steps = [.. EscalationLadder.Defaults.Select(StepRow.From)];

    /// <summary>The steps as the setting stores them; throws <see cref="ArgumentException"/> for one that can't be.</summary>
    public string StepsText() => EscalationLadder.Format(Steps.Select((s, i) => s.ToStep(i + 1)));

    /// <summary>The ladder's scopes and how the page names them.</summary>
    public static readonly IReadOnlyList<(EscalationScope Scope, string Text)> Scopes =
    [
        (EscalationScope.All, "All actions"),
        (EscalationScope.PartsOnly, "Only parts of a split"),
        (EscalationScope.ExternalOnly, "Only external teams' actions"),
    ];
}
