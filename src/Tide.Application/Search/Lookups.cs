using Microsoft.EntityFrameworkCore;
using Tide.Application.Abstractions;
using Tide.Application.Indicators;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Domain.Observables;

namespace Tide.Application.Search;

/// <summary>An action as the lookups read it: who has it, when it's due, how its hunts went, and the article it's from.</summary>
internal sealed record ActionRow(Guid Id, int Number, string Title, ActionStatus Status, Priority Priority, string TeamName,
    string? AssigneeName, DateOnly? DueDate, bool AnyHits, bool AnyNoHits, bool AnyHunt, string? Description, string? OutcomeNote, string? TicketRef,
    string? TicketUrl, Guid? ArticleId, ActionKind Kind, bool TeamExternal)
{
    public ActionHit Hit(string? snippet) => new(Id, Number, Title, Status, Priority, TeamName, AssigneeName, DueDate,
        AnyHits ? HuntResult.Hits : AnyNoHits ? HuntResult.NoHits : AnyHunt ? HuntResult.NotRun : null, snippet, Kind, ArticleId,
        TeamExternal, TicketRef, TicketUrl);
}

/// <summary>The indicator a lookup is for: its type, its value as stored, and the types it's stored under.</summary>
internal sealed record LookedUp(IndicatorType Type, string Value, List<IndicatorType> Family);

/// <summary>
/// The queries Search's lookups, the editor's Related panel (<see cref="RelatedService"/>) and a lookup's export share:
/// what a search looks up and the articles it lists as its own, the actions raised from a set of articles, and the
/// briefs those articles were on.
/// </summary>
internal static class Lookups
{
    /// <summary>The indicator <paramref name="term"/> (one search term, defanged or not) is looked up as, or null when it's
    /// no indicator a lookup is made for.</summary>
    public static LookedUp? IndicatorOf(string term)
    {
        var live = IocObservable.Refang(term);
        var type = IocObservable.DetectType(live);
        if (type is IndicatorType.Other or IndicatorType.CommandLine) return null;
        return new LookedUp(type, IocObservable.Normalize(type, live), IocObservable.FamilyTypes(type).ToList());
    }

    /// <summary>The article indicators that are <paramref name="looked"/> itself (not another on its host).</summary>
    public static IQueryable<ArticleIndicator> Carrying(IAppDbContext db, LookedUp looked)
    {
        var exact = SearchText.Literal(looked.Value);
        return db.ArticleIndicators.AsNoTracking().Where(i => looked.Family.Contains(i.Type) && EF.Functions.Like(i.Value, exact, SearchText.Escape));
    }

    /// <summary>The articles about <paramref name="cveId"/>.</summary>
    public static IQueryable<Guid> About(IAppDbContext db, string cveId) =>
        db.ArticleCves.AsNoTracking().Where(c => c.CveId == cveId).Select(c => c.ArticleId).Distinct();

    /// <summary>
    /// The articles a search for <paramref name="query"/> lists as its lookup's own, all of them: those carrying the
    /// indicator itself, or about the CVE. Null when the search makes no lookup (more than one word, or no indicator).
    /// </summary>
    public static IQueryable<Guid>? ArticlesOf(IAppDbContext db, string? query)
    {
        if (SearchText.Terms(query) is not [var term]) return null;
        if (IocExtractor.NormalizeCve(term) is { } cveId) return About(db, cveId);
        return IndicatorOf(term) is { } looked ? Carrying(db, looked).Select(i => i.ArticleId).Distinct() : null;
    }

    public static Task<List<ActionRow>> ActionRows(IAppDbContext db, IQueryable<ActionItem> q, CancellationToken ct) =>
        q.Select(a => new ActionRow(a.Id, a.Number, a.Title, a.Status, a.Priority, a.Team!.Name, a.AssigneeName, a.DueDate,
                db.HuntQueries.Any(h => h.ActionId == a.Id && h.Result == HuntResult.Hits),
                db.HuntQueries.Any(h => h.ActionId == a.Id && h.Result == HuntResult.NoHits),
                db.HuntQueries.Any(h => h.ActionId == a.Id),
                a.Description, a.OutcomeNote, a.TicketRef, a.TicketUrl, a.ArticleId, a.Kind, a.Team!.IsExternal))
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

    /// <summary>The briefs each article is on, oldest first, each with whether it was presented (or sent).</summary>
    public static async Task<Dictionary<Guid, IReadOnlyList<LookupBrief>>> BriefsAsync(IAppDbContext db, List<Guid> articleIds, CancellationToken ct) =>
        (await db.BriefItems.AsNoTracking().Where(i => articleIds.Contains(i.ArticleId))
            .Join(db.Briefs, i => i.BriefId, b => b.Id, (i, b) => new { i.ArticleId, b.BriefDate, b.Status }).ToListAsync(ct))
        .GroupBy(d => d.ArticleId).ToDictionary(g => g.Key, g => (IReadOnlyList<LookupBrief>)g
            .GroupBy(d => d.BriefDate).OrderBy(day => day.Key)
            .Select(day => new LookupBrief(day.Key, day.Any(d => d.Status is BriefStatus.Presented or BriefStatus.Sent))).ToList());

    /// <summary>The days of the briefs each article was on, oldest first.</summary>
    public static async Task<Dictionary<Guid, IReadOnlyList<DateOnly>>> BriefDatesAsync(IAppDbContext db, List<Guid> articleIds, CancellationToken ct) =>
        (await db.BriefItems.AsNoTracking().Where(i => articleIds.Contains(i.ArticleId))
            .Join(db.Briefs, i => i.BriefId, b => b.Id, (i, b) => new { i.ArticleId, b.BriefDate }).ToListAsync(ct))
        .GroupBy(d => d.ArticleId).ToDictionary(g => g.Key, g => (IReadOnlyList<DateOnly>)g.Select(d => d.BriefDate).Distinct().Order().ToList());
}
