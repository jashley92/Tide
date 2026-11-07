using Microsoft.EntityFrameworkCore;
using Tide.Application.Abstractions;
using Tide.Application.Articles;
using Tide.Application.Security;
using Tide.Domain.Common;
using Tide.Domain.Enums;

namespace Tide.Application.Actions;

/// <summary>The sentences a team library refuses with, in its own words ("this saved query", "a template").</summary>
internal sealed record LibraryWords(string Missing, string Stale, string Refusal, string NotTheirs);

/// <summary>
/// The rules the team's libraries share (saved queries, outreach templates, playbooks): anyone who works actions saves,
/// edits, retires and restores an entry, and deletes one they saved; an administrator deletes any. An edit begun on an
/// older copy is refused. Each library's service keeps its own checks and its own reads; this is the part that's the same.
/// </summary>
internal sealed class TeamLibrary<T>(IAppDbContextFactory dbs, ICurrentUser user, Func<IAppDbContext, DbSet<T>> entries, LibraryWords words)
    where T : class, ITeamLibraryEntry
{
    public bool CanWrite => user.Has(Permission.WorkActions);

    /// <summary>Whether the current user may delete an entry <paramref name="savedBy"/> saved: an administrator, or them.</summary>
    public bool CanDelete(string? savedBy) => user.Has(Permission.Administer) || (CanWrite && savedBy is not null && savedBy == user.UserId);

    public void RequireWrite()
    {
        if (!CanWrite) throw new ForbiddenException(words.Refusal);
    }

    /// <summary>A new entry, saved by the current user.</summary>
    public T Added(IAppDbContext db, T entry)
    {
        entry.CreatedByName = user.DisplayName;
        entries(db).Add(entry);
        return entry;
    }

    /// <summary>
    /// The entry an edit begun on <paramref name="began"/> changes, read from <paramref name="from"/> (with what it
    /// carries); refused when it's gone, or when someone has changed it since.
    /// </summary>
    public async Task<T> ForEditAsync(IQueryable<T> from, Guid id, Guid began, CancellationToken ct)
    {
        var entry = await from.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException(words.Missing);
        if (entry.EditVersion != began) throw new StaleEditException(words.Stale);
        return entry;
    }

    /// <summary>Marks an entry changed by the current user: their name on it, and a new version.</summary>
    public void Changed(T entry)
    {
        entry.ModifiedByName = user.DisplayName;
        entry.EditVersion = Guid.NewGuid();
    }

    /// <summary>Saves an edit. One that lost the race to another edit (the version moved under it) is refused as stale, not as a database error.</summary>
    public async Task SaveEditAsync(IAppDbContext db, CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw new StaleEditException(words.Stale); }
    }

    /// <summary>Retires an entry (it's no longer offered on actions, and stays on the list to read) or restores it.</summary>
    public async Task SetRetiredAsync(Guid id, bool retired, CancellationToken ct)
    {
        RequireWrite();
        await using var db = await dbs.CreateAsync(ct);
        var entry = await entries(db).FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException(words.Missing);
        if (entry.IsRetired == retired) return;
        entry.IsRetired = retired;
        entry.ModifiedByName = user.DisplayName;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Deletes an entry: whoever saved it, or an administrator. <paramref name="mustKeep"/>, when given, refuses one that
    /// has to stay (by throwing) before anything is removed.
    /// </summary>
    public async Task DeleteAsync(Guid id, Func<IAppDbContext, T, CancellationToken, Task>? mustKeep, CancellationToken ct)
    {
        await using var db = await dbs.CreateAsync(ct);
        var entry = await entries(db).FirstOrDefaultAsync(x => x.Id == id, ct);
        // One rule (CanDelete), applied to one that's gone too, as if it were their own: whoever could delete their own
        // is let by (it's gone, as asked), and anyone else is refused as for any other.
        if (!CanDelete(entry is null ? user.UserId : entry.CreatedBy)) throw new ForbiddenException(words.NotTheirs);
        if (entry is null) return;
        if (mustKeep is not null) await mustKeep(db, entry, ct);
        entries(db).Remove(entry);
        try { await db.SaveChangesAsync(ct); }
        // Someone deleted it first: it's gone, as asked.
        catch (DbUpdateConcurrencyException) { }
    }
}
