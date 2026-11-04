namespace Tide.Application.Metrics;

/// <summary>
/// One of present mode's metric slides: a group's numbers and chart (<see cref="Group"/>), and on the first of them the
/// groups whose numbers are all 0, each said in a line instead of a slide of its own (<see cref="AllZero"/>). When every
/// group is all 0 there's one slide, with no group of its own and only those lines.
/// </summary>
public sealed record MetricSlide(MetricGroupDay? Group, IReadOnlyList<MetricGroupDay> AllZero)
{
    /// <summary>What the slide is, as every page in a meeting names it alike: "metrics:{group key}", or "metrics" for
    /// the slide of all-0 groups alone. A follower whose page cut the slides differently still lands on the same group.</summary>
    public string Key => Group is { } g ? MetricSlides.KeyPrefix + g.Group.Key : MetricSlides.Key;
}

/// <summary>
/// How present mode cuts the opening metrics into slides: one per group, in the groups' order. A group whose numbers
/// are all 0 (every metric in it has a value today, and each is 0) gets no slide: the first metrics slide says it in a
/// line ("Vulnerabilities: all 0"). That one rule, wherever the group sits, keeps the count the same on every page and
/// the lines in one place the room has just seen.
/// </summary>
public static class MetricSlides
{
    /// <summary>The key of a metrics slide with no group of its own, and of the metrics slides before they were cut
    /// by group (a meeting started on an older page).</summary>
    public const string Key = "metrics";

    public const string KeyPrefix = Key + ":";

    /// <summary>The metric slides for <paramref name="day"/>, in order; none when no metric is defined.</summary>
    public static IReadOnlyList<MetricSlide> Plan(MetricDay? day)
    {
        if (day is not { Any: true }) return [];
        var zero = day.Groups.Where(IsAllZero).ToList();
        var shown = day.Groups.Where(g => !IsAllZero(g)).ToList();
        if (shown.Count == 0) return [new MetricSlide(null, zero)];
        return shown.Select((g, i) => new MetricSlide(g, i == 0 ? zero : [])).ToList();
    }

    /// <summary>Whether every metric in the group has a value and each is 0. A group missing a value keeps its slide:
    /// the gap is something the room should see.</summary>
    public static bool IsAllZero(MetricGroupDay group) => group.Tiles.Count > 0 && group.Tiles.All(t => t.Value == 0);

    /// <summary>
    /// The slide (from 0, among <paramref name="plan"/>) called <paramref name="key"/>; null when it isn't a metrics key.
    /// A group that has no slide here (all 0 on this page, or retired since) is on the first one, where its line is.
    /// </summary>
    public static int? IndexOf(IReadOnlyList<MetricSlide> plan, string? key)
    {
        if (plan.Count == 0 || key is null || (key != Key && !key.StartsWith(KeyPrefix, StringComparison.Ordinal))) return null;
        for (var i = 0; i < plan.Count; i++)
            if (plan[i].Key == key) return i;
        return 0;
    }
}
