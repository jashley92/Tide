namespace Tide.Application.Trends;

/// <summary>
/// Which lines a trend chart draws, from its <c>?chart=</c> address: a comma-separated list read in order, where each
/// item is a line's key, a group (<see cref="All"/>, <see cref="AllTechniques"/>, or a tactic's <see cref="AllUnder"/>),
/// <see cref="None"/>, or a key after a minus to take that line back off. A group stays one short item however many
/// lines it stands for, so charting everything never makes a long address, and takes in only lines that counted
/// something (a line of zeros adds nothing to a chart). A key on its own is charted even when it's all zeros.
/// </summary>
public static class TrendChart
{
    /// <summary>Every line the view lists at its top level.</summary>
    public const string All = "all";

    /// <summary>Every ATT&amp;CK technique, in the flat list (each once, under its primary tactic).</summary>
    public const string AllTechniques = "all-techniques";

    /// <summary>Nothing: the chart cleared, rather than left to its default lines.</summary>
    public const string None = "none";

    private const string Exclude = "-";

    /// <summary>Every technique under one tactic (its <c>tactic:</c> key).</summary>
    public static string AllUnder(string tacticKey) => tacticKey + ":" + All;

    /// <summary>
    /// The lines a chart address stands for, in order and each once. Keys resolve against every line of the view: the
    /// lines it lists (<paramref name="listed"/>), and under each tactic in them, that tactic's techniques, so a
    /// technique from any tactic can be charted whichever level of the table is showing.
    /// </summary>
    public static IReadOnlyList<TrendRow> Resolve(TrendReport report, IReadOnlyList<TrendRow> listed, string chart)
    {
        var tactics = listed.Where(IsTactic).Select(r => r.Key).ToHashSet(StringComparer.Ordinal);
        var lines = listed.Concat(tactics.SelectMany(report.TechniquesOf)).ToList();
        var picked = new List<TrendRow>();
        foreach (var item in Items(chart))
        {
            if (item.StartsWith(Exclude, StringComparison.Ordinal))
            {
                picked.RemoveAll(r => r.Key == item[Exclude.Length..]);
                continue;
            }
            IEnumerable<TrendRow> rows = item switch
            {
                All => listed.Where(r => r.Any),
                AllTechniques => listed.Where(r => IsTechnique(r) && r.Any),
                _ when item.EndsWith(":" + All, StringComparison.Ordinal) && tactics.Contains(item[..^(All.Length + 1)]) =>
                    report.TechniquesOf(item[..^(All.Length + 1)]).Where(r => r.Any),
                _ => lines.Where(r => r.Key == item).Take(1),
            };
            picked.AddRange(rows.Where(r => !picked.Exists(p => p.Key == r.Key)));
        }
        return picked;
    }

    /// <summary>
    /// The address with one line put on the chart or taken off it. A line a group put there comes off with a minus
    /// item, so the group stays one item; one put back drops its minus, or is added at the end.
    /// </summary>
    public static string Toggle(TrendReport report, IReadOnlyList<TrendRow> listed, string chart, string key)
    {
        var items = Items(chart).Where(i => i != None).ToList();
        bool On() => Resolve(report, listed, string.Join(',', items)).Any(r => r.Key == key);
        if (On())
        {
            items.RemoveAll(i => i == key);
            if (On()) items.Add(Exclude + key);
        }
        else
        {
            items.RemoveAll(i => i == Exclude + key);
            if (!On()) items.Add(key);
        }
        return items.Count == 0 ? None : string.Join(',', items);
    }

    /// <summary>
    /// What a line counts, whichever list it's in: a technique under a tactic (<c>tactic:TA0002:T1059</c>) is the same
    /// technique as in the flat list (<c>technique:T1059</c>), so a table row can show it's on the chart either way.
    /// Any other line is its own key.
    /// </summary>
    public static string Identity(string key)
    {
        const string tactic = "tactic:";
        if (!key.StartsWith(tactic, StringComparison.Ordinal)) return key;
        var colon = key.IndexOf(':', tactic.Length);
        return colon < 0 ? key : "technique:" + key[(colon + 1)..];
    }

    /// <summary>Where a table row's line is on the chart (its own, or the same technique under another tactic), or -1.</summary>
    public static int Slot(IReadOnlyList<TrendRow> charted, TrendRow row)
    {
        var identity = Identity(row.Key);
        var same = -1;
        for (var i = 0; i < charted.Count; i++)
        {
            if (charted[i].Key == row.Key) return i;
            if (same < 0 && Identity(charted[i].Key) == identity) same = i;
        }
        return same;
    }

    private static bool IsTactic(TrendRow r) => r.Key.StartsWith("tactic:", StringComparison.Ordinal);

    private static bool IsTechnique(TrendRow r) => r.Key.StartsWith("technique:", StringComparison.Ordinal) && r.Key != "technique:none";

    private static string[] Items(string chart) => chart.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
