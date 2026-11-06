using Microsoft.EntityFrameworkCore;
using Tide.Application.Abstractions;
using Tide.Application.Articles;
using Tide.Application.Security;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.Application.Actions;

/// <summary>A saved query as its form edits it. <see cref="EditVersion"/> is the version the edit began on.</summary>
public sealed class SavedQueryForm
{
    public Guid? Id { get; set; }
    public Guid EditVersion { get; set; }
    public string Name { get; set; } = "";
    public string Dialect { get; set; } = "";
    public string? Description { get; set; }
    public string Text { get; set; } = "";

    public static SavedQueryForm From(SavedQuery q) => new()
    {
        Id = q.Id, EditVersion = q.EditVersion, Name = q.Name, Dialect = q.Dialect, Description = q.Description, Text = q.Text,
    };
}

/// <summary>A saved query on the list, with how many actions have had it added.</summary>
public sealed record SavedQueryRow(SavedQuery Query, int Actions);

/// <summary>
/// The team's library of hunt queries (ADR 0006): a tuned sweep or a behavioural hunt kept so a colleague can add it to
/// their own action (<see cref="ActionService.AddSavedHuntAsync"/>) rather than write it again. Anyone who works actions
/// saves, edits, retires and restores them, and deletes one they saved; an administrator deletes any. Each change is in
/// the audit trail and on the change feed, like any team setting. An edit begun on an older copy is refused.
/// </summary>
public sealed class SavedQueryService(IAppDbContextFactory dbs, ICurrentUser user)
{
    /// <summary>Whether the current user may save, edit, retire and restore saved queries.</summary>
    public bool CanWrite => user.Has(Permission.WorkActions);

    /// <summary>Whether the current user may delete <paramref name="q"/>: an administrator, or whoever saved it.</summary>
    public bool CanDelete(SavedQuery q) => CanDelete(q.CreatedBy);

    private bool CanDelete(string? savedBy) => user.Has(Permission.Administer) || (CanWrite && savedBy is not null && savedBy == user.UserId);

    /// <summary>
    /// Every saved query, retired ones too when <paramref name="includeRetired"/>, by language (as the languages are
    /// listed) then name, each with how many actions it was added to.
    /// </summary>
    public async Task<IReadOnlyList<SavedQueryRow>> ListAsync(bool includeRetired = false, CancellationToken ct = default)
    {
        RequireRead();
        await using var db = await dbs.CreateAsync(ct);
        var all = await db.SavedQueries.AsNoTracking().Where(q => includeRetired || !q.IsRetired).ToListAsync(ct);
        var ids = all.Select(q => q.Id).ToList();
        // Counted in the database, for the queries read: each action once, however many times the query went on it.
        var used = await db.HuntQueries.Where(h => h.SavedQueryId != null && ids.Contains(h.SavedQueryId.Value))
            .GroupBy(h => h.SavedQueryId!.Value)
            .Select(g => new { g.Key, Actions = g.Select(h => h.ActionId).Distinct().Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Actions, ct);
        return Ordered(all).Select(q => new SavedQueryRow(q, used.GetValueOrDefault(q.Id))).ToList();
    }

    /// <summary>The saved queries an action's Hunt queries card offers (not retired), in the list's order, without the counts.</summary>
    public async Task<IReadOnlyList<SavedQuery>> OfferedAsync(CancellationToken ct = default)
    {
        RequireRead();
        await using var db = await dbs.CreateAsync(ct);
        return Ordered(await db.SavedQueries.AsNoTracking().Where(q => !q.IsRetired).ToListAsync(ct)).ToList();
    }

    // By language (as the languages are listed), then name.
    private static IOrderedEnumerable<SavedQuery> Ordered(IEnumerable<SavedQuery> queries) =>
        queries.OrderBy(q => DialectOrder(q.Dialect)).ThenBy(q => q.Name, StringComparer.OrdinalIgnoreCase);

    public async Task<SavedQuery?> GetAsync(Guid id, CancellationToken ct = default)
    {
        RequireRead();
        await using var db = await dbs.CreateAsync(ct);
        return await db.SavedQueries.AsNoTracking().FirstOrDefaultAsync(q => q.Id == id, ct);
    }

    /// <summary>
    /// Whether <paramref name="q"/> matches the words typed (a filter split into words, as the tables split theirs):
    /// each in its name, description, language or text, ignoring case. The list is the team's own and short, so it's
    /// matched here rather than in the database.
    /// </summary>
    public static bool Matches(SavedQuery q, IReadOnlyList<string> words) => words.All(w =>
        q.Name.Contains(w, StringComparison.OrdinalIgnoreCase)
        || (q.Description?.Contains(w, StringComparison.OrdinalIgnoreCase) ?? false)
        || HuntQueryBuilder.DialectName(q.Dialect).Contains(w, StringComparison.OrdinalIgnoreCase)
        || q.Text.Contains(w, StringComparison.OrdinalIgnoreCase));

    /// <summary>Saves a new query, or changes one; its id. A change begun on a copy someone has since changed is refused.</summary>
    public async Task<Guid> SaveAsync(SavedQueryForm form, CancellationToken ct = default)
    {
        RequireWrite();
        var d = QueryDialects.Find(form.Dialect) ?? throw new ArgumentException("Choose a query language.");
        var name = ArticleService.Clean(form.Name, SavedQuery.MaxName);
        if (name.Length == 0) throw new ArgumentException("A saved query needs a name.");
        var description = ArticleService.CleanOrNull(form.Description, SavedQuery.MaxDescription);
        var text = (form.Text ?? "").Replace("\r\n", "\n").Trim();
        if (text.Length == 0) throw new ArgumentException("The query is empty.");
        if (text.Length > SavedQuery.MaxText) throw new ArgumentException($"A saved query can be at most {SavedQuery.MaxText:N0} characters.");
        _ = HuntQueryBuilder.ValuesPlaceholders(text);   // a kind misspelt in {{values:…}} is refused now, not when it's used

        await using var db = await dbs.CreateAsync(ct);
        SavedQuery q;
        if (form.Id is { } id)
        {
            q = await db.SavedQueries.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException("That saved query no longer exists.");
            if (q.EditVersion != form.EditVersion) throw new StaleEditException(Stale);
            if (q.Name == name && q.Dialect == d.Key && q.Description == description && q.Text == text) return q.Id;
            q.ModifiedByName = user.DisplayName;
            q.EditVersion = Guid.NewGuid();
        }
        else
        {
            q = new SavedQuery { CreatedByName = user.DisplayName };
            db.SavedQueries.Add(q);
        }
        // One name per language, ignoring case on both providers (LIKE, as the playbooks' names are).
        var exact = Search.SearchText.Literal(name);
        if (await db.SavedQueries.AnyAsync(x => x.Id != q.Id && x.Dialect == d.Key && EF.Functions.Like(x.Name, exact, Search.SearchText.Escape), ct))
            throw new ArgumentException($"There's already a {d.Name} saved query called {name}.");
        q.Name = name;
        q.Dialect = d.Key;
        q.Description = description;
        q.Text = text;
        await SaveEditAsync(db, ct);
        return q.Id;
    }

    /// <summary>
    /// What Save for the team offers to save of <paramref name="hunt"/>: for one added from a saved query that's still
    /// there, that query's own text (its <c>{{values}}</c> and <c>{{days}}</c>, not this action's values and window, nor
    /// the lines naming what it left out); for any other, its text as it stands without the first line TIDE wrote on it.
    /// </summary>
    public async Task<string> DraftTextAsync(HuntQuery hunt, CancellationToken ct = default)
    {
        RequireWrite();
        if (hunt.SavedQueryId is { } id)
        {
            await using var db = await dbs.CreateAsync(ct);
            if (await db.SavedQueries.AsNoTracking().Where(q => q.Id == id).Select(q => q.Text).FirstOrDefaultAsync(ct) is { } template) return template;
        }
        return HuntQueryBuilder.WithoutHeader(hunt.Dialect, hunt.Text);
    }

    /// <summary>
    /// Saves a hunt on an action for the team, in its language, as <paramref name="text"/>: what
    /// <see cref="DraftTextAsync"/> offered, as edited.
    /// </summary>
    public async Task<Guid> SaveFromHuntAsync(Guid huntId, string name, string? description, string text, CancellationToken ct = default)
    {
        RequireWrite();
        string dialect;
        await using (var db = await dbs.CreateAsync(ct))
            dialect = await db.HuntQueries.AsNoTracking().Where(h => h.Id == huntId).Select(h => h.Dialect).FirstOrDefaultAsync(ct)
                      ?? throw new KeyNotFoundException("That hunt query no longer exists.");
        return await SaveAsync(new SavedQueryForm { Name = name, Dialect = dialect, Description = description, Text = text }, ct);
    }

    /// <summary>Retires a query (it's no longer offered on actions, and stays on the list to read) or restores it.</summary>
    public async Task SetRetiredAsync(Guid id, bool retired, CancellationToken ct = default)
    {
        RequireWrite();
        await using var db = await dbs.CreateAsync(ct);
        var q = await db.SavedQueries.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException("That saved query no longer exists.");
        if (q.IsRetired == retired) return;
        q.IsRetired = retired;
        q.ModifiedByName = user.DisplayName;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Deletes a query: whoever saved it, or an administrator. Hunts added from it keep its name.</summary>
    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbs.CreateAsync(ct);
        var q = await db.SavedQueries.FirstOrDefaultAsync(x => x.Id == id, ct);
        // One rule (CanDelete), applied to one that's gone too, as if it were their own: whoever could delete their own
        // is let by (it's gone, as asked), and anyone else is refused as for any other.
        if (!CanDelete(q is null ? user.UserId : q.CreatedBy)) throw new ForbiddenException("Only the person who saved it, or an administrator, can delete a saved query. Retire it instead.");
        if (q is null) return;
        db.SavedQueries.Remove(q);
        try { await db.SaveChangesAsync(ct); }
        // Someone deleted it first: it's gone, as asked.
        catch (DbUpdateConcurrencyException) { }
    }

    private const string Stale = "Someone else changed this saved query since you opened it. Reload to see their changes.";
    private const string Refusal = "Only people who work actions can change the saved queries.";

    // A save that lost the race to another edit (the version moved under it) is refused as stale, not as a database error.
    private static async Task SaveEditAsync(IAppDbContext db, CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw new StaleEditException(Stale); }
    }

    private static int DialectOrder(string key)
    {
        for (var i = 0; i < QueryDialects.All.Count; i++)
            if (QueryDialects.All[i].Key == key) return i;
        return QueryDialects.All.Count;
    }

    private void RequireRead()
    {
        if (!user.Has(Permission.ReadActions)) throw new ForbiddenException("You don't have access to the saved queries.");
    }

    private void RequireWrite()
    {
        if (!CanWrite) throw new ForbiddenException(Refusal);
    }
}
