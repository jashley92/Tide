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
    public bool CanDelete(SavedQuery q) => user.Has(Permission.Administer) || (CanWrite && q.CreatedBy == user.UserId);

    /// <summary>
    /// Every saved query, retired ones too when <paramref name="includeRetired"/>, by language (as the languages are
    /// listed) then name, each with how many actions it was added to.
    /// </summary>
    public async Task<IReadOnlyList<SavedQueryRow>> ListAsync(bool includeRetired = false, CancellationToken ct = default)
    {
        RequireRead();
        await using var db = await dbs.CreateAsync(ct);
        var all = await db.SavedQueries.AsNoTracking().Where(q => includeRetired || !q.IsRetired).ToListAsync(ct);
        // Each action once, however many times the query went on it.
        var used = (await db.HuntQueries.AsNoTracking().Where(h => h.SavedQueryId != null)
                .Select(h => new { h.SavedQueryId, h.ActionId }).Distinct().ToListAsync(ct))
            .GroupBy(h => h.SavedQueryId!.Value).ToDictionary(g => g.Key, g => g.Count());
        return all.OrderBy(q => DialectOrder(q.Dialect)).ThenBy(q => q.Name, StringComparer.OrdinalIgnoreCase)
            .Select(q => new SavedQueryRow(q, used.GetValueOrDefault(q.Id))).ToList();
    }

    public async Task<SavedQuery?> GetAsync(Guid id, CancellationToken ct = default)
    {
        RequireRead();
        await using var db = await dbs.CreateAsync(ct);
        return await db.SavedQueries.AsNoTracking().FirstOrDefaultAsync(q => q.Id == id, ct);
    }

    /// <summary>
    /// Whether <paramref name="q"/> matches what was typed: every word in its name, description, language or text,
    /// ignoring case. The list is the team's own and short, so it's matched here rather than in the database.
    /// </summary>
    public static bool Matches(SavedQuery q, string? words)
    {
        var all = $"{q.Name} {q.Description} {HuntQueryBuilder.DialectName(q.Dialect)} {q.Text}";
        return (words ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .All(w => all.Contains(w, StringComparison.OrdinalIgnoreCase));
    }

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
        if (form.Id is not null)
        {
            q.ModifiedByName = user.DisplayName;
            q.EditVersion = Guid.NewGuid();
        }
        await SaveEditAsync(db, ct);
        return q.Id;
    }

    /// <summary>
    /// Saves a hunt on an action for the team: its language, and its text as it stands (unless <paramref name="text"/>
    /// says otherwise), without the first line TIDE wrote on it.
    /// </summary>
    public async Task<Guid> SaveFromHuntAsync(Guid huntId, string name, string? description, string? text = null, CancellationToken ct = default)
    {
        RequireWrite();
        HuntQuery hunt;
        await using (var db = await dbs.CreateAsync(ct))
            hunt = await db.HuntQueries.AsNoTracking().FirstOrDefaultAsync(h => h.Id == huntId, ct) ?? throw new KeyNotFoundException("That hunt query no longer exists.");
        return await SaveAsync(new SavedQueryForm
        {
            Name = name, Dialect = hunt.Dialect, Description = description, Text = text ?? HuntQueryBuilder.WithoutHeader(hunt.Dialect, hunt.Text),
        }, ct);
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
        if (!CanWrite && !user.Has(Permission.Administer)) throw new ForbiddenException(Refusal);
        await using var db = await dbs.CreateAsync(ct);
        if (await db.SavedQueries.FirstOrDefaultAsync(x => x.Id == id, ct) is not { } q) return;
        if (!CanDelete(q)) throw new ForbiddenException("Only the person who saved it, or an administrator, can delete a saved query. Retire it instead.");
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
