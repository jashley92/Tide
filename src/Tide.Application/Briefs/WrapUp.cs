using Tide.Application.Actions;

namespace Tide.Application.Briefs;

/// <summary>The actions one team and one person in it hold, as the wrap-up lists them: <see cref="HolderName"/> is null for
/// the team's actions nobody has yet. <see cref="Twin"/> numbers people of one name on one team (1, 2, …), 0 otherwise.
/// An external team's actions nobody in TIDE holds are grouped by their ticket instead, <see cref="Outside"/> naming it
/// (or saying there's no ticket yet).</summary>
public sealed record HolderGroup(string TeamName, string? HolderName, IReadOnlyList<EditionAction> Actions, int Twin = 0)
{
    /// <summary>For an external team's actions nobody in TIDE holds: their ticket, or that there's none yet.</summary>
    public HolderLabel? Outside { get; init; }

    /// <summary>"Robin · Cyber Defense", "Robin (2) · Cyber Defense" beside another Robin, or "Cyber Defense · nobody yet";
    /// an external team's "INC-4411 · Hosting Provider", or "With Hosting Provider · no ticket yet".</summary>
    public string Who => Outside is { Kind: HolderKind.NoTicket } none ? none.Text
        : Outside is { } ticket ? $"{ticket.Text} · {TeamName}"
        : HolderName is null ? $"{TeamName} · nobody yet"
        : Twin > 0 ? $"{HolderName} ({Twin}) · {TeamName}" : $"{HolderName} · {TeamName}";
}

/// <summary>The meeting's wrap-up, worked out from what the edition shows (so nothing above its ceiling is in it).</summary>
public static class WrapUp
{
    /// <summary>Who is doing what: <paramref name="actions"/> (the edition's, on its articles) by team, then by the person
    /// holding them, then (an external team's) by ticket, the team's actions nobody has yet (or with no ticket yet) last;
    /// each group's in number order. Two people of one name on one team are told apart by a number, nothing more (the room
    /// sees no more of them than their names elsewhere).</summary>
    public static IReadOnlyList<HolderGroup> WhoHasWhat(IEnumerable<EditionAction> actions)
    {
        var groups = actions.GroupBy(a => (a.TeamName, a.AssigneeId, Ticket: Outside(a)?.Text))
            .Select(g => new HolderGroup(g.Key.TeamName, g.Key.AssigneeId is null ? null : g.First().AssigneeName ?? g.Key.AssigneeId,
                g.OrderBy(a => a.Number).ToList()) { Outside = Outside(g.First()) })
            .OrderBy(g => g.TeamName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(g => g.HolderName is null)
            .ThenBy(g => g.Outside is null or { Kind: HolderKind.NoTicket })
            .ThenBy(g => g.HolderName ?? g.Outside?.Text, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(g => g.Actions[0].Number)
            .ToList();
        // Numbered in the order above (by their first action), only where a name repeats within a team.
        return groups.GroupBy(g => (g.TeamName, g.HolderName))
            .SelectMany(same => same.Key.HolderName is null || same.Count() == 1 ? same : same.Select((g, i) => g with { Twin = i + 1 }))
            .ToList();
    }

    // An external team's action nobody in TIDE holds, by its ticket; null for anything else.
    private static HolderLabel? Outside(EditionAction a) =>
        a.Holder("") is { Kind: HolderKind.Ticket or HolderKind.NoTicket } outside ? outside : null;
}
