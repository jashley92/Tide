using Microsoft.EntityFrameworkCore;
using Tide.Application.Abstractions;
using Tide.Application.Content;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.Application.Actions;

/// <summary>
/// A change refused because it would take a team over its active limit. <see cref="NeedsReason"/> when the team's mode
/// is Warn and no reason was given: the caller asks for one and tries again with it. Otherwise the team refuses (Refuse).
/// </summary>
public sealed class ActiveLimitException(string message, string team, int active, int limit, bool needsReason)
    : InvalidOperationException(message)
{
    public string Team { get; } = team;
    public int Active { get; } = active;
    public int Limit { get; } = limit;
    public bool NeedsReason { get; } = needsReason;
}

/// <summary>A team's active count against its limit, for "4 of 5 active".</summary>
public sealed record TeamActive(Guid TeamId, string Team, int Active, int Limit, ActiveLimitMode Mode)
{
    public bool Over => Active > Limit;
    public string Text => ActiveLimits.Text(Active, Limit);
}

/// <summary>
/// A team's optional limit on how much it has active at once: its open, in progress and blocked actions (any level,
/// owned by it). Tracking doesn't count: others are doing it. A team goes over when one of its actions is created, moved
/// into an active status, or handed (or moved) to it; every such change goes through <see cref="CheckChangeAsync"/>.
/// </summary>
public static class ActiveLimits
{
    /// <summary>The longest reason accepted for going over.</summary>
    public const int MaxReason = 500;

    /// <summary>"4 of 5 active".</summary>
    public static string Text(int active, int limit) => $"{active} of {limit} active";

    /// <summary>A team's limit, its mode and how many actions it has active (but <paramref name="except"/>), in one read.</summary>
    private sealed record Count(Guid Id, string Name, int? Limit, ActiveLimitMode Mode, int Active);

    private static Task<Count?> CountAsync(IAppDbContext db, Guid teamId, Guid? except, CancellationToken ct)
    {
        var skip = except ?? Guid.Empty;
        return db.Teams.AsNoTracking().Where(t => t.Id == teamId)
            .Select(t => new Count(t.Id, t.Name, t.ActiveLimit, t.LimitMode,
                db.Actions.Count(a => a.TeamId == t.Id && a.Id != skip && ActionItem.ActiveStatuses.Contains(a.Status))))
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>The team's count against its limit, or null when it has none.</summary>
    internal static async Task<TeamActive?> OfAsync(IAppDbContext db, Guid teamId, CancellationToken ct) =>
        await CountAsync(db, teamId, null, ct) is { Limit: { } limit } c ? new TeamActive(c.Id, c.Name, c.Active, limit, c.Mode) : null;

    /// <summary>
    /// Checks a change to an action (<paramref name="actionId"/>, null for a new one) from <paramref name="before"/> (its
    /// team and status; nulls for a new one) to <paramref name="after"/>. It adds to the receiving team's active count
    /// when it goes into an active status, or to another team while active; then the team's limit holds it. Returns what
    /// goes after the action's history line: "" when it's within the limit (or the team has none, or only shows it), or
    /// " Over …'s limit (…): reason" when it goes over with a reason. Throws <see cref="ActiveLimitException"/> when it's
    /// refused, or a reason is needed and wasn't given. <paramref name="team"/> is the receiving team when the caller
    /// has it already: with no limit to hold, nothing more is read.
    /// </summary>
    internal static async Task<string> CheckChangeAsync(IAppDbContext db, (Guid? TeamId, ActionStatus? Status) before,
        (Guid TeamId, ActionStatus Status) after, Guid? actionId, string? reason, Team? team, CancellationToken ct)
    {
        if (!Adds(before, after)) return "";
        if (team is not null && (team.ActiveLimit is null || team.LimitMode == ActiveLimitMode.Show)) return "";
        if (await CountAsync(db, after.TeamId, actionId, ct) is not { Limit: { } limit } c) return "";
        return Suffix(Decide(c.Name, c.Active, limit, c.Mode, reason));
    }

    /// <summary>Whether a change adds an action to the receiving team's active count.</summary>
    public static bool Adds((Guid? TeamId, ActionStatus? Status) before, (Guid TeamId, ActionStatus Status) after) =>
        ActionItem.IsActiveStatus(after.Status)
        && (before.TeamId != after.TeamId || before.Status is not { } was || !ActionItem.IsActiveStatus(was));

    /// <summary>
    /// The teams of several new actions at once (a split), each adding one: their active counts in one grouped read, then
    /// <see cref="Decide"/> for each. Returns each team's history-line suffix, as <see cref="CheckChangeAsync"/> does.
    /// </summary>
    internal static async Task<Dictionary<Guid, string>> CheckNewAsync(IAppDbContext db, IEnumerable<Team> teams, string? reason, CancellationToken ct)
    {
        var limited = teams.Where(t => t.ActiveLimit is not null && t.LimitMode != ActiveLimitMode.Show).ToList();
        var ids = limited.Select(t => t.Id).ToList();
        var counts = ids.Count == 0 ? [] : await db.Actions.AsNoTracking().WhereActive().Where(a => ids.Contains(a.TeamId))
            .GroupBy(a => a.TeamId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.N, ct);
        return teams.ToDictionary(t => t.Id, t => limited.Contains(t)
            ? Suffix(Decide(t.Name, counts.GetValueOrDefault(t.Id), t.ActiveLimit!.Value, t.LimitMode, reason))
            : "");
    }

    private static string Suffix(string? line) => line is null ? "" : " " + line;

    /// <summary>The rule, on its own: a team with <paramref name="active"/> active taking one more. Returns the history line
    /// when it goes over with a reason (Warn), null when it's within (or the team only shows the count).</summary>
    public static string? Decide(string team, int active, int limit, ActiveLimitMode mode, string? reason)
    {
        var after = active + 1;
        if (mode == ActiveLimitMode.Show || after <= limit) return null;
        if (mode == ActiveLimitMode.Refuse)
            throw new ActiveLimitException($"{team} is at its limit of {Words.Plural(limit, "active action")} ({Text(active, limit)}). " +
                "Finish or move one of its actions on, or ask an administrator to raise the limit.", team, active, limit, needsReason: false);
        var why = Articles.ArticleService.CleanOrNull(reason, MaxReason, multiline: false);
        if (why is null)
            throw new ActiveLimitException($"This takes {team} over its limit of {limit} active ({Text(after, limit)}). Say why to go ahead.",
                team, active, limit, needsReason: true);
        return $"Over {team}'s limit ({Text(after, limit)}): {why}";
    }
}
