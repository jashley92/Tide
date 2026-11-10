using Microsoft.EntityFrameworkCore;
using Tide.Application.Abstractions;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.Application.Search;

/// <summary>An action as the lookups read it: who has it, when it's due, how its hunts went, and the article it's from.</summary>
internal sealed record ActionRow(Guid Id, int Number, string Title, ActionStatus Status, Priority Priority, string TeamName,
    string? AssigneeName, DateOnly? DueDate, bool AnyHits, bool AnyNoHits, bool AnyHunt, string? Description, string? OutcomeNote, string? TicketRef,
    Guid? ArticleId, ActionKind Kind)
{
    public ActionHit Hit(string? snippet) => new(Id, Number, Title, Status, Priority, TeamName, AssigneeName, DueDate,
        AnyHits ? HuntResult.Hits : AnyNoHits ? HuntResult.NoHits : AnyHunt ? HuntResult.NotRun : null, snippet, Kind, ArticleId);
}

/// <summary>
/// The queries Search's lookups and the editor's Related panel (<see cref="RelatedService"/>) share: the actions raised
/// from a set of articles, and the briefs those articles were on.
/// </summary>
internal static class Lookups
{
    public static Task<List<ActionRow>> ActionRows(IAppDbContext db, IQueryable<ActionItem> q, CancellationToken ct) =>
        q.Select(a => new ActionRow(a.Id, a.Number, a.Title, a.Status, a.Priority, a.Team!.Name, a.AssigneeName, a.DueDate,
                db.HuntQueries.Any(h => h.ActionId == a.Id && h.Result == HuntResult.Hits),
                db.HuntQueries.Any(h => h.ActionId == a.Id && h.Result == HuntResult.NoHits),
                db.HuntQueries.Any(h => h.ActionId == a.Id),
                a.Description, a.OutcomeNote, a.TicketRef, a.ArticleId, a.Kind))
            .ToListAsync(ct);

    /// <summary>
    /// The actions raised from <paramref name="articleIds"/>, and those named by <paramref name="actionIds"/> (the ones
    /// carrying a looked-up indicator themselves), newest first: the first <paramref name="take"/>, and how many in all.
    /// </summary>
    public static async Task<HitList<ActionHit>> ActionsOnAsync(IAppDbContext db, List<Guid> articleIds, List<Guid> actionIds, int take, CancellationToken ct)
    {
        if (articleIds.Count == 0 && actionIds.Count == 0) return HitList<ActionHit>.None;
        var q = db.Actions.AsNoTracking().Where(a => (a.ArticleId != null && articleIds.Contains(a.ArticleId.Value)) || actionIds.Contains(a.Id));
        var rows = await ActionRows(db, q.OrderByDescending(a => a.Number).Take(take + 1), ct);
        var total = rows.Count > take ? await q.CountAsync(ct) : rows.Count;
        return new HitList<ActionHit>(rows.Take(take).Select(r => r.Hit(null)).ToList(), total);
    }

    /// <summary>The days of the briefs each article was on, oldest first.</summary>
    public static async Task<Dictionary<Guid, IReadOnlyList<DateOnly>>> BriefDatesAsync(IAppDbContext db, List<Guid> articleIds, CancellationToken ct) =>
        (await db.BriefItems.AsNoTracking().Where(i => articleIds.Contains(i.ArticleId))
            .Join(db.Briefs, i => i.BriefId, b => b.Id, (i, b) => new { i.ArticleId, b.BriefDate }).ToListAsync(ct))
        .GroupBy(d => d.ArticleId).ToDictionary(g => g.Key, g => (IReadOnlyList<DateOnly>)g.Select(d => d.BriefDate).Distinct().Order().ToList());
}
