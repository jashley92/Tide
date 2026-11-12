using Microsoft.EntityFrameworkCore;
using Tide.Application.Abstractions;
using Tide.Application.Security;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.Application.Preferences;

/// <summary>A saved view as the Actions page shows it: its name, and the query to open it with (<c>actions?{Query}</c>).</summary>
public sealed record ActionsSavedView(Guid Id, string Name, string Query);

/// <summary>
/// The signed-in person's own saved views of the Actions page (<see cref="SavedActionView"/>): a name for a combination
/// of its tab, layout and filters, pinned beside its tabs. Like a preference, it only says how the page is shown to the
/// person who saved it, so anyone who can open the page may keep some, Viewers too, and no one can see or change
/// another's: every read and write is of the signed-in person's own rows, and an id that isn't one of theirs is "not found".
/// </summary>
public sealed class SavedViewService(IAppDbContextFactory dbs, ICurrentUser user, TimeProvider time)
{
    /// <summary>How many one person may keep: a row of pills beside the tabs, not a library.</summary>
    public const int MaxViews = 12;

    /// <summary>The list's page, which the address carries but a view doesn't: dropped rather than refused.</summary>
    public const string PageParameter = "page";

    /// <summary>The page's parameters a view may hold, in the order a stored query lists them, each with its longest value.</summary>
    public static readonly IReadOnlyList<(string Name, int Max)> Parameters =
    [
        ("view", 16), ("layout", 16), ("group", 16), ("rule", 36), ("team", 36), ("assignee", 256), ("q", 200), ("kind", 32),
        ("priority", 32), ("due", 16), ("status", 32), ("parent", 10), ("teams", 16), ("raised", 16), ("closed", 2), ("on", 16),
    ];

    /// <summary>The person's views, in their order.</summary>
    public async Task<IReadOnlyList<ActionsSavedView>> ListAsync(CancellationToken ct = default)
    {
        var me = Require();
        await using var db = await dbs.CreateAsync(ct);
        return await db.SavedActionViews.AsNoTracking().Where(v => v.UserId == me)
            .OrderBy(v => v.SortOrder).ThenBy(v => v.CreatedAtUtc)
            .Select(v => new ActionsSavedView(v.Id, v.Name, v.Query)).ToListAsync(ct);
    }

    /// <summary>
    /// Saves <paramref name="query"/> (the page's address after the <c>?</c>) as a view named <paramref name="name"/>, after
    /// the person's others. Throws <see cref="ArgumentException"/> for a name that's empty, too long or already one of
    /// theirs, or a query <see cref="Normalize"/> refuses, and <see cref="InvalidOperationException"/> at
    /// <see cref="MaxViews"/>.
    /// </summary>
    public async Task<ActionsSavedView> SaveAsync(string name, string query, CancellationToken ct = default)
    {
        var me = Require();
        var clean = CleanName(name);
        var stored = Normalize(query);
        await using var db = await dbs.CreateAsync(ct);
        var mine = await db.SavedActionViews.Where(v => v.UserId == me).ToListAsync(ct);
        if (mine.Count >= MaxViews) throw new InvalidOperationException($"You can keep up to {MaxViews} views. Delete one to save another.");
        RefuseTaken(mine, clean, except: null);
        var row = new SavedActionView
        {
            UserId = me, Name = clean, Query = stored, CreatedAtUtc = time.GetUtcNow(),
            SortOrder = mine.Count == 0 ? 0 : mine.Max(v => v.SortOrder) + 1,
        };
        db.SavedActionViews.Add(row);
        await SaveAsync(db, clean, ct);
        return new ActionsSavedView(row.Id, row.Name, row.Query);
    }

    /// <summary>Renames one of the person's views; the same checks on the name as <see cref="SaveAsync(string, string, CancellationToken)"/>.</summary>
    public async Task RenameAsync(Guid id, string name, CancellationToken ct = default)
    {
        var me = Require();
        var clean = CleanName(name);
        await using var db = await dbs.CreateAsync(ct);
        var mine = await db.SavedActionViews.Where(v => v.UserId == me).ToListAsync(ct);
        var row = Find(mine, id);
        RefuseTaken(mine, clean, except: id);
        row.Name = clean;
        await SaveAsync(db, clean, ct);
    }

    /// <summary>Deletes one of the person's views.</summary>
    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var me = Require();
        await using var db = await dbs.CreateAsync(ct);
        var row = await db.SavedActionViews.FirstOrDefaultAsync(v => v.Id == id && v.UserId == me, ct) ?? throw NotFound();
        db.SavedActionViews.Remove(row);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Moves one of the person's views <paramref name="by"/> places: negative earlier, positive later, stopping at either
    /// end. Their order is numbered again from 0.
    /// </summary>
    public async Task MoveAsync(Guid id, int by, CancellationToken ct = default)
    {
        var me = Require();
        await using var db = await dbs.CreateAsync(ct);
        var mine = (await db.SavedActionViews.Where(v => v.UserId == me).ToListAsync(ct))
            .OrderBy(v => v.SortOrder).ThenBy(v => v.CreatedAtUtc).ToList();
        var row = Find(mine, id);
        var to = Math.Clamp(mine.IndexOf(row) + by, 0, mine.Count - 1);
        mine.Remove(row);
        mine.Insert(to, row);
        for (var i = 0; i < mine.Count; i++) mine[i].SortOrder = i;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// <paramref name="query"/> as it's stored and compared: a leading <c>?</c> dropped, each parameter once, in the order
    /// of <see cref="Parameters"/>, its value unescaped, trimmed, checked against its length and escaped again; an empty
    /// value and the list's page left out. Throws <see cref="ArgumentException"/> for a parameter the page doesn't have,
    /// one given twice, or a value longer than its own.
    /// </summary>
    public static string Normalize(string? query)
    {
        var text = (query ?? "").Trim();
        if (text.StartsWith('?')) text = text[1..];
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in text.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            var key = Unescape(eq < 0 ? part : part[..eq]);
            var value = Unescape(eq < 0 ? "" : part[(eq + 1)..]).Trim();
            if (key == PageParameter) continue;
            var known = Parameters.FirstOrDefault(p => p.Name == key);
            if (known.Name is null) throw new ArgumentException($"A view can't hold \"{Cut(key)}\": only the Actions page's own filters.", nameof(query));
            if (value.Length > known.Max) throw new ArgumentException($"The {key} filter is too long to keep (at most {known.Max} characters).", nameof(query));
            if (!values.TryAdd(key, value)) throw new ArgumentException($"The {key} filter is given twice.", nameof(query));
        }
        var stored = string.Join('&', Parameters.Where(p => values.TryGetValue(p.Name, out var v) && v.Length > 0)
            .Select(p => p.Name + "=" + Escape(values[p.Name])));
        if (stored.Length > SavedActionView.MaxQuery) throw new ArgumentException("These filters are too long to keep as a view.", nameof(query));
        return stored;
    }

    /// <summary>As <see cref="Normalize"/>, or null for a query it refuses: for comparing an address with a view.</summary>
    public static string? TryNormalize(string? query)
    {
        try { return Normalize(query); }
        catch (ArgumentException) { return null; }
    }

    // A comma stays as it is, so "priority=p1,p2" reads as the page writes it.
    private static string Escape(string value) => Uri.EscapeDataString(value).Replace("%2C", ",", StringComparison.Ordinal);

    private static string Unescape(string value)
    {
        try { return Uri.UnescapeDataString(value.Replace('+', ' ')); }
        catch (UriFormatException) { return value; }
    }

    private static string Cut(string text) => text.Length > 40 ? text[..40] + "…" : text;

    private static string CleanName(string? name)
    {
        var clean = (name ?? "").Trim();
        if (clean.Length == 0) throw new ArgumentException("Give the view a name.", nameof(name));
        if (clean.Length > SavedActionView.MaxName) throw new ArgumentException($"A view's name can be at most {SavedActionView.MaxName} characters.", nameof(name));
        return clean;
    }

    private static void RefuseTaken(IEnumerable<SavedActionView> mine, string name, Guid? except)
    {
        if (mine.Any(v => v.Id != except && string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw Taken(name);
    }

    private static ArgumentException Taken(string name) => new($"You already have a view named \"{name}\".", nameof(name));

    private static SavedActionView Find(IEnumerable<SavedActionView> mine, Guid id) => mine.FirstOrDefault(v => v.Id == id) ?? throw NotFound();

    // Another person's id reads as one that doesn't exist: it says nothing about theirs.
    private static KeyNotFoundException NotFound() => new("That view isn't there any more. Reload the page to see your views.");

    // The person's other tab saved the same name at the same moment: the unique index refused this one.
    private static async Task SaveAsync(IAppDbContext db, string name, CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { throw Taken(name); }
    }

    private string Require()
    {
        if (!user.IsAuthenticated) throw new ForbiddenException("Sign in to keep a view.");
        if (!user.Has(Permission.ReadActions)) throw new ForbiddenException($"You don't have permission to {Policies.Describe(Permission.ReadActions)}.");
        return user.UserId;
    }
}
