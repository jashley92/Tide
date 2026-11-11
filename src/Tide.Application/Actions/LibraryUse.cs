using Microsoft.EntityFrameworkCore;
using Tide.Application.Abstractions;
using Tide.Application.Content;
using Tide.Domain.Enums;

namespace Tide.Application.Actions;

/// <summary>Which of the team's libraries an entry is in.</summary>
public enum LibraryKind { SavedQuery, OutreachTemplate, Playbook }

/// <summary>An action a library entry was used on: its number, title and status, and what came of it, when the library
/// says ("2 hits", "no hits", "replied").</summary>
public sealed record LibraryUse(int Number, string Title, ActionStatus Status, string? Found);

/// <summary>The actions an entry was used on, the latest first, up to <see cref="LibraryUseService.MaxUsedOn"/>, and how many
/// there are in all.</summary>
public sealed record LibraryUses(IReadOnlyList<LibraryUse> Rows, int Total);

/// <summary>An open action the reader works, as an entry can be tried on it, and whether it has indicators to fill one with
/// (its article's or its own).</summary>
public sealed record WorkableAction(Guid Id, int Number, string Title, ActionKind Kind, bool HasIndicators);

/// <summary>
/// What the Library's context rail shows of an entry beyond the entry itself: the actions it was used on (for whoever
/// reads actions; a deleted one isn't listed), and the open actions the reader may try it on (those they work, by the
/// rule <see cref="ActionService"/> holds every step on an action to). Both only read.
/// </summary>
public sealed class LibraryUseService(IAppDbContextFactory dbs, ICurrentUser user)
{
    /// <summary>The most actions listed as an entry's uses.</summary>
    public const int MaxUsedOn = 10;

    /// <summary>The most open actions offered to try an entry on.</summary>
    public const int MaxWorkable = 100;

    /// <summary>The actions <paramref name="id"/> (an entry of <paramref name="kind"/>) was used on: a saved query added
    /// to its hunts, with what its latest run found; a template's email written on it, and whether it was answered; a
    /// playbook its checklist started from.</summary>
    public async Task<LibraryUses> UsedOnAsync(LibraryKind kind, Guid id, CancellationToken ct = default)
    {
        ActionService.RequireRead(user);
        await using var db = await dbs.CreateAsync(ct);
        // The actions' query filter leaves deleted ones out of each join.
        var actions = db.Actions.AsNoTracking();
        var used = kind switch
        {
            LibraryKind.SavedQuery => (await db.HuntQueries.AsNoTracking().Where(h => h.SavedQueryId == id)
                    .Join(actions, h => h.ActionId, a => a.Id, (h, a) => new { a.Number, a.Title, a.Status, h.Result, h.ResultCount, h.CreatedAtUtc })
                    .ToListAsync(ct))
                .GroupBy(u => u.Number).Select(g => g.MaxBy(u => u.CreatedAtUtc)!)
                .Select(u => new LibraryUse(u.Number, u.Title, u.Status, Found(u.Result, u.ResultCount))),
            LibraryKind.OutreachTemplate => (await db.OutreachMessages.AsNoTracking().Where(m => m.TemplateId == id)
                    .Join(actions, m => m.ActionId, a => a.Id, (m, a) => new { a.Number, a.Title, a.Status, m.RepliedAtUtc, m.SentAtUtc })
                    .ToListAsync(ct))
                .GroupBy(u => u.Number).Select(g => g.MaxBy(u => u.SentAtUtc)!)
                .Select(u => new LibraryUse(u.Number, u.Title, u.Status, u.RepliedAtUtc is null ? "waiting for a reply" : "replied")),
            _ => (await actions.Where(a => a.PlaybookId == id).Select(a => new { a.Number, a.Title, a.Status }).ToListAsync(ct))
                .Select(a => new LibraryUse(a.Number, a.Title, a.Status, null)),
        };
        var all = used.OrderByDescending(u => u.Number).ToList();
        return new LibraryUses([.. all.Take(MaxUsedOn)], all.Count);
    }

    private static string Found(HuntResult result, int? count) => result switch
    {
        HuntResult.Hits => count is { } n ? Words.Plural(n, "hit") : "hits",
        HuntResult.NoHits => "no hits",
        _ => "not run yet",
    };

    /// <summary>The open actions the reader works, the newest first: every one for an analyst, their teams' for someone who
    /// works actions, none for anyone else.</summary>
    public async Task<IReadOnlyList<WorkableAction>> WorkableAsync(CancellationToken ct = default)
    {
        if (!user.Has(Permission.WriteArticles) && !user.Has(Permission.WorkActions)) return [];
        await using var db = await dbs.CreateAsync(ct);
        var q = db.Actions.AsNoTracking().WhereOpen();
        if (!user.Has(Permission.WriteArticles))
        {
            var teams = db.TeamMembers.Where(m => m.UserId == user.UserId).Select(m => m.TeamId);
            q = q.Where(a => teams.Contains(a.TeamId));
        }
        return await q.OrderByDescending(a => a.Number).Take(MaxWorkable)
            .Select(a => new WorkableAction(a.Id, a.Number, a.Title, a.Kind, a.Indicators.Any() || (a.Article != null && a.Article.Indicators.Any()))).ToListAsync(ct);
    }

    /// <summary>Whether an entry of <paramref name="kind"/> is tried on an action of <paramref name="action"/>'s kind: a
    /// saved query on a sweep or hunt, a template on an outreach action, a playbook on the kind it's for (or any).</summary>
    public static bool Suits(LibraryKind kind, ActionKind action, ActionKind? playbookFor = null) => kind switch
    {
        LibraryKind.SavedQuery => ActionGuide.IsHunt(action),
        LibraryKind.OutreachTemplate => action == ActionKind.Outreach,
        _ => playbookFor is null || playbookFor == action,
    };
}
