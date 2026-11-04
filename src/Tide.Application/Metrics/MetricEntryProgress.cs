namespace Tide.Application.Metrics;

/// <summary>How far one group of the day's numbers has got: how many of its metrics have a value.</summary>
public sealed record MetricGroupProgress(Guid GroupId, string Name, int Entered, int Total)
{
    /// <summary>Every one of its numbers is entered (a group with none is never complete).</summary>
    public bool Complete => Total > 0 && Entered == Total;

    /// <summary>"2 of 4".</summary>
    public string Count => $"{Entered} of {Total}";

    /// <summary>What the group's chip says to a screen reader: "Email triage, 2 of 4 entered".</summary>
    public string AccessibleName => $"{Name}, {Count} entered";
}

/// <summary>
/// The day's numbers form's progress and folding (MetricEntry): the counts for its progress bar, and which finished
/// groups are folded to a summary line. A group folds only when it's left (never while focus is still in it, so never
/// under someone typing), only when every number is entered, and never while one of its boxes holds what isn't a number
/// or a colleague's conflicting change. One that stops being complete (a value cleared) or starts holding such a box
/// unfolds by itself.
/// </summary>
public sealed class MetricEntryProgress
{
    private readonly HashSet<Guid> _folded = [];
    private IReadOnlyList<MetricGroupProgress> _groups = [];
    private Dictionary<Guid, Guid> _groupOf = [];
    private Dictionary<Guid, List<Guid>> _metricsOf = [];
    private IReadOnlySet<Guid> _held = new HashSet<Guid>();
    private HashSet<Guid> _valued = [];
    private bool _started;

    /// <summary>Each group's count, in the form's order.</summary>
    public IReadOnlyList<MetricGroupProgress> Groups => _groups;

    /// <summary>How many of the day's numbers have a value.</summary>
    public int Entered => _groups.Sum(g => g.Entered);

    /// <summary>How many numbers the day has.</summary>
    public int Total => _groups.Sum(g => g.Total);

    /// <summary>"18 of 25 entered".</summary>
    public string Summary => $"{Entered} of {Total} entered";

    /// <summary>Counts each group of the day, the way the form shows them.</summary>
    public static IReadOnlyList<MetricGroupProgress> Count(MetricDay day) =>
        [.. day.Groups.Select(g => new MetricGroupProgress(g.Group.Id, g.Group.Name, g.Tiles.Count(t => t.Value is not null), g.Tiles.Count))];

    /// <summary>
    /// Reads the day again (on the first read, or after a save here or a colleague's). <paramref name="held"/> are the
    /// metrics whose box holds what isn't a number, or that a colleague changed while they were being edited here.
    /// The first read folds every group that's finished already; later ones only unfold a group that stopped being
    /// foldable, so nothing folds while someone is in it.
    /// </summary>
    public void Update(MetricDay day, IReadOnlySet<Guid> held)
    {
        _groups = Count(day);
        _held = held;
        _groupOf = day.Groups.SelectMany(g => g.Tiles.Select(t => (t.Metric.Id, g.Group.Id))).ToDictionary(x => x.Item1, x => x.Item2);
        _metricsOf = day.Groups.ToDictionary(g => g.Group.Id, g => g.Tiles.Select(t => t.Metric.Id).ToList());
        _valued = [.. day.Groups.SelectMany(g => g.Tiles).Where(t => t.Value is not null).Select(t => t.Metric.Id)];
        _folded.RemoveWhere(g => !Foldable(g));
        if (_started) return;
        _started = true;
        foreach (var g in _groups.Where(g => Foldable(g.GroupId))) _folded.Add(g.GroupId);
    }

    /// <summary>The group a metric's box is in (null for one the day doesn't have).</summary>
    public Guid? GroupOf(Guid metric) => _groupOf.TryGetValue(metric, out var g) ? g : null;

    /// <summary>A group's metrics, in order.</summary>
    public IReadOnlyList<Guid> MetricsOf(Guid group) => _metricsOf.TryGetValue(group, out var m) ? m : [];

    /// <summary>The group is folded to its summary line.</summary>
    public bool IsFolded(Guid group) => _folded.Contains(group);

    /// <summary>Every number entered, and no box in it holding what isn't a number or a colleague's change.</summary>
    public bool Foldable(Guid group) =>
        _groups.Any(g => g.GroupId == group && g.Complete) && !MetricsOf(group).Any(_held.Contains);

    /// <summary>
    /// Focus has left <paramref name="group"/>: it folds if it's finished. <paramref name="focusGroup"/> is the group
    /// focus is in now, if any; a group focus is still in doesn't fold. True when it folded.
    /// </summary>
    public bool Left(Guid group, Guid? focusGroup)
    {
        if (focusGroup == group || !Foldable(group)) return false;
        return _folded.Add(group);
    }

    /// <summary>Folds a finished group straight away (its heading's button). True when it folded.</summary>
    public bool Fold(Guid group) => Foldable(group) && _folded.Add(group);

    /// <summary>Opens a folded group (its button, or its chip). True when it was folded.</summary>
    public bool Unfold(Guid group) => _folded.Remove(group);

    /// <summary>
    /// The box a group's chip jumps to: its first one without a value, or its first box when all have one (null for a
    /// group the day doesn't have).
    /// </summary>
    public Guid? JumpTarget(Guid group)
    {
        var metrics = MetricsOf(group);
        if (metrics.Count == 0) return null;
        return metrics.FirstOrDefault(m => !_valued.Contains(m), metrics[0]);
    }
}
