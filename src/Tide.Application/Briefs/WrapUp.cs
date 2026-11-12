namespace Tide.Application.Briefs;

/// <summary>The actions one team and one person in it hold, as the wrap-up lists them: <see cref="HolderName"/> is null for
/// the team's actions nobody has yet. <see cref="Twin"/> numbers people of one name on one team (1, 2, …), 0 otherwise.</summary>
public sealed record HolderGroup(string TeamName, string? HolderName, IReadOnlyList<EditionAction> Actions, int Twin = 0)
{
    /// <summary>"Robin · Cyber Defense", "Robin (2) · Cyber Defense" beside another Robin, or "Cyber Defense · nobody yet".</summary>
    public string Who => HolderName is null ? $"{TeamName} · nobody yet"
        : Twin > 0 ? $"{HolderName} ({Twin}) · {TeamName}" : $"{HolderName} · {TeamName}";
}

/// <summary>The meeting's wrap-up, worked out from what the edition shows (so nothing above its ceiling is in it).</summary>
public static class WrapUp
{
    /// <summary>Who is doing what: <paramref name="actions"/> (the edition's, on its articles) by team, then by the person
    /// holding them, the team's actions nobody has yet last; each group's in number order. Two people of one name on one
    /// team are told apart by a number, nothing more (the room sees no more of them than their names elsewhere).</summary>
    public static IReadOnlyList<HolderGroup> WhoHasWhat(IEnumerable<EditionAction> actions)
    {
        var groups = actions.GroupBy(a => (a.TeamName, a.AssigneeId))
            .Select(g => new HolderGroup(g.Key.TeamName, g.Key.AssigneeId is null ? null : g.First().AssigneeName ?? g.Key.AssigneeId,
                g.OrderBy(a => a.Number).ToList()))
            .OrderBy(g => g.TeamName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(g => g.HolderName is null)
            .ThenBy(g => g.HolderName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(g => g.Actions[0].Number)
            .ToList();
        // Numbered in the order above (by their first action), only where a name repeats within a team.
        return groups.GroupBy(g => (g.TeamName, g.HolderName))
            .SelectMany(same => same.Key.HolderName is null || same.Count() == 1 ? same : same.Select((g, i) => g with { Twin = i + 1 }))
            .ToList();
    }
}
