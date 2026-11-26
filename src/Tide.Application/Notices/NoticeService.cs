using Microsoft.EntityFrameworkCore;
using Tide.Application.Abstractions;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.Application.Notices;

/// <summary>
/// One of the signed-in person's For you notices, as the top bar's panel and the toast show it.
/// </summary>
/// <param name="Line">What happened, in words: "Sam handed you an article".</param>
/// <param name="Title">What it's about ("A-7 Sweep proxy logs…"); null when that's gone, or no longer the person's to open.</param>
/// <param name="Link">Where it opens, relative to the app; null when it's gone.</param>
public sealed record ForYouNotice(Guid Id, NoticeKind Kind, string Line, string? Title, string? Link, string? Detail,
    DateTimeOffset AtUtc, bool Seen);

/// <summary>
/// The signed-in person's For you notices (<see cref="Notice"/>): what colleagues handed them or asked of them. The
/// notices themselves are written by the saves they're about; here they're read, counted and marked seen, each person
/// only their own. A kind the person can no longer act on (their role changed since) isn't shown or counted.
/// </summary>
public sealed class NoticeService(IAppDbContextFactory dbs, ICurrentUser user, TimeProvider time)
{
    /// <summary>How long a notice is kept once it's seen.</summary>
    public const int KeepSeenDays = 30;

    /// <summary>How long one that's never seen is kept: someone away that long has Today and their email.</summary>
    public const int KeepUnseenDays = 90;

    /// <summary>The most the panel lists.</summary>
    public const int MaxListed = 50;

    // The permission each kind needs: the one its page needs.
    private static Permission Needs(NoticeKind kind) => kind switch
    {
        NoticeKind.ActionHandedToTeam or NoticeKind.ActionAssigned => Permission.ReadActions,
        NoticeKind.ArticleHandedToYou or NoticeKind.ReviewAsked or NoticeKind.RotationAsked or NoticeKind.RotationDecided => Permission.WriteArticles,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "A notice kind with no permission."),
    };

    /// <summary>Whether the person can be sent any notice at all, so whether the bell is shown to them.</summary>
    public bool CanReceive => user.IsAuthenticated && (user.Has(Permission.ReadActions) || user.Has(Permission.WriteArticles));

    private List<NoticeKind> Allowed() => [.. Enum.GetValues<NoticeKind>().Where(k => user.Has(Needs(k)))];

    private IQueryable<Notice> Mine(IAppDbContext db, List<NoticeKind> kinds) =>
        db.Notices.AsNoTracking().Where(n => n.UserId == user.UserId && kinds.Contains(n.Kind));

    /// <summary>How many of the person's notices are new: the bell's count.</summary>
    public async Task<int> UnseenCountAsync(CancellationToken ct = default)
    {
        if (!CanReceive) return 0;
        await using var db = await dbs.CreateAsync(ct);
        return await Mine(db, Allowed()).CountAsync(n => n.SeenAtUtc == null, ct);
    }

    /// <summary>The person's notices, newest first: every new one and those seen in the last <see cref="KeepSeenDays"/>
    /// days, at most <see cref="MaxListed"/>.</summary>
    public async Task<IReadOnlyList<ForYouNotice>> ListAsync(CancellationToken ct = default)
    {
        if (!CanReceive) return [];
        await using var db = await dbs.CreateAsync(ct);
        var since = time.GetUtcNow().AddDays(-KeepSeenDays);
        var rows = await Mine(db, Allowed()).Where(n => n.SeenAtUtc == null || n.SeenAtUtc >= since)
            .OrderByDescending(n => n.CreatedAtUtc).Take(MaxListed).ToListAsync(ct);
        return await ShownAsync(db, rows, ct);
    }

    /// <summary>The person's new notices made after <paramref name="afterUtc"/>, oldest first: what a toast announces
    /// when the live topic says one came.</summary>
    public async Task<IReadOnlyList<ForYouNotice>> NewSinceAsync(DateTimeOffset afterUtc, CancellationToken ct = default)
    {
        if (!CanReceive) return [];
        await using var db = await dbs.CreateAsync(ct);
        var rows = await Mine(db, Allowed()).Where(n => n.SeenAtUtc == null && n.CreatedAtUtc > afterUtc)
            .OrderBy(n => n.CreatedAtUtc).Take(MaxListed).ToListAsync(ct);
        return await ShownAsync(db, rows, ct);
    }

    /// <summary>Marks one of the person's notices seen (they clicked it). Someone else's, or one that's gone, is left as it is.</summary>
    public async Task SeenAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbs.CreateAsync(ct);
        var n = await db.Notices.FirstOrDefaultAsync(x => x.Id == id && x.UserId == user.UserId && x.SeenAtUtc == null, ct);
        if (n is null) return;
        n.SeenAtUtc = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Marks all the person's new notices seen; returns how many were.</summary>
    public async Task<int> AllSeenAsync(CancellationToken ct = default)
    {
        await using var db = await dbs.CreateAsync(ct);
        var unseen = await db.Notices.Where(x => x.UserId == user.UserId && x.SeenAtUtc == null).ToListAsync(ct);
        var now = time.GetUtcNow();
        foreach (var n in unseen) n.SeenAtUtc = now;
        // One save, so the person's other tabs hear it once.
        if (unseen.Count > 0) await db.SaveChangesAsync(ct);
        return unseen.Count;
    }

    /// <summary>
    /// Removes notices seen more than <see cref="KeepSeenDays"/> days ago and those never seen after
    /// <see cref="KeepUnseenDays"/>, in the reminder job's daily prune (<c>ReminderService.PruneMarksAsync</c>). Returns how many went.
    /// </summary>
    public static async Task<int> PruneAsync(IAppDbContext db, DateTimeOffset now, CancellationToken ct = default)
    {
        var (seen, unseen) = (now.AddDays(-KeepSeenDays), now.AddDays(-KeepUnseenDays));
        // Two statements, each a plain range on one column.
        return await db.Notices.Where(n => n.SeenAtUtc < seen).ExecuteDeleteAsync(ct)
            + await db.Notices.Where(n => n.SeenAtUtc == null && n.CreatedAtUtc < unseen).ExecuteDeleteAsync(ct);
    }

    // What each notice is about, read now: a renamed article shows its new title, a deleted one shows as gone.
    private static async Task<IReadOnlyList<ForYouNotice>> ShownAsync(IAppDbContext db, List<Notice> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return [];
        var ids = rows.Select(n => n.SubjectId).Distinct().ToList();
        var articles = await db.Articles.AsNoTracking().Where(a => ids.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Title, ct);
        var actions = await db.Actions.AsNoTracking().Where(a => ids.Contains(a.Id))
            .Select(a => new { a.Id, a.Number, a.Title, Team = a.Team!.Name }).ToDictionaryAsync(a => a.Id, ct);
        var changes = await db.RotationChanges.AsNoTracking().Where(c => ids.Contains(c.Id))
            .Select(c => new { c.Id, c.Kind, c.Status, c.GiveFrom, c.GiveTo }).ToDictionaryAsync(c => c.Id, ct);

        return [.. rows.Select(n =>
        {
            var by = n.ActorName;
            var action = actions.GetValueOrDefault(n.SubjectId);
            var number = action is null ? "an action" : $"A-{action.Number}";
            var change = changes.GetValueOrDefault(n.SubjectId);
            var what = change?.Kind == RotationChangeKind.Swap ? "swap" : "cover";
            var (line, title, link) = n.Kind switch
            {
                NoticeKind.ArticleHandedToYou => ($"{by} handed you an article", articles.GetValueOrDefault(n.SubjectId), $"articles/{n.SubjectId}"),
                NoticeKind.ReviewAsked => ($"{by} asked you to review", articles.GetValueOrDefault(n.SubjectId), $"articles/{n.SubjectId}/review"),
                NoticeKind.ActionHandedToTeam => ($"{by} handed {number} to {action?.Team ?? "your team"}", action is null ? null : $"{number} {action.Title}", $"actions/{action?.Number}"),
                NoticeKind.ActionAssigned => ($"{by} assigned {number} to you", action is null ? null : $"{number} {action.Title}", $"actions/{action?.Number}"),
                NoticeKind.RotationAsked => ($"{by} asked you to {what} brief days", change is null ? null : Days(change.GiveFrom, change.GiveTo), "rotation"),
                _ => ($"{by} {(change?.Status == RotationChangeStatus.Approved ? "approved" : "declined")} your {what} request",
                    change is null ? null : Days(change.GiveFrom, change.GiveTo), "rotation"),
            };
            return new ForYouNotice(n.Id, n.Kind, line, title, title is null ? null : link, n.Detail, n.CreatedAtUtc, n.SeenAtUtc is not null);
        })];
    }

    // "Thu Nov 26", or "Mon Nov 23 to Fri Nov 27": the requester's days the partner takes on.
    private static string Days(DateOnly from, DateOnly to) =>
        from == to ? Actions.ActionService.Day(from) : $"{Actions.ActionService.Day(from)} to {Actions.ActionService.Day(to)}";
}
