namespace Tide.Application.Briefs;

/// <summary>The actions one team and one person in it hold, as the wrap-up lists them: <see cref="HolderName"/> is null for
/// the team's actions nobody has yet.</summary>
public sealed record HolderGroup(string TeamName, string? HolderName, IReadOnlyList<EditionAction> Actions)
{
    /// <summary>"Robin · Cyber Defense", or "Cyber Defense · nobody yet".</summary>
    public string Who => HolderName is null ? $"{TeamName} · nobody yet" : $"{HolderName} · {TeamName}";
}

/// <summary>The meeting's wrap-up, worked out from what the edition shows (so nothing above its ceiling is in it).</summary>
public static class WrapUp
{
    /// <summary>Who is doing what: <paramref name="actions"/> (the edition's, on its articles) by team, then by the person
    /// holding them, the team's actions nobody has yet last; each group's in number order.</summary>
    public static IReadOnlyList<HolderGroup> WhoHasWhat(IEnumerable<EditionAction> actions) =>
        actions.GroupBy(a => (a.TeamName, a.AssigneeId))
            .Select(g => new HolderGroup(g.Key.TeamName, g.Key.AssigneeId is null ? null : g.First().AssigneeName ?? g.Key.AssigneeId,
                g.OrderBy(a => a.Number).ToList()))
            .OrderBy(g => g.TeamName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(g => g.HolderName is null)
            .ThenBy(g => g.HolderName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
}
