using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Tide.Application.Abstractions;
using Tide.Application.Security;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.Application.Preferences;

/// <summary>A saved view as its page shows it: its name, and the query to open it with (<c>{page}?{Query}</c>).</summary>
public sealed record SavedViewEntry(Guid Id, string Name, string Query);

/// <summary>
/// A list page that keeps saved views: the key its views are stored under (<see cref="SavedActionView.Page"/>), which is
/// also its address, who may open it, the parameters a view of it may hold (in the order a stored query lists them, each
/// with its longest value), and the ones its address carries that a view drops rather than refuses (the list's page, the
/// row selected).
/// </summary>
public sealed record SavedViewPage(string Key, string Title, Permission Permission,
    IReadOnlyList<(string Name, int Max)> Parameters, IReadOnlyList<string> Dropped)
{
    public static readonly SavedViewPage Actions = new(SavedActionView.ActionsPage, "Actions", Permission.ReadActions,
    [
        ("view", 16), ("layout", 16), ("group", 16), ("rule", 36), ("team", 36), ("assignee", 256), ("q", 200), ("kind", 32),
        ("priority", 32), ("due", 16), ("status", 32), ("parent", 10), ("teams", 16), ("raised", 16), ("closed", 2), ("on", 16),
    ], ["page"]);

    /// <summary>Triage's tab and filters. Never the row (<c>at=</c>, <c>row=</c>), the page, or a promote form it opens.</summary>
    public static readonly SavedViewPage Triage = new("triage", "Triage", Permission.WriteArticles,
        [("tab", 16), ("source", 36), ("q", 200), ("since", 5), ("cve", 8), ("named", 5), ("older", 5)],
        ["page", "at", "row", "promote"]);

    /// <summary>The article list's filters. Never the page, or the New article form.</summary>
    public static readonly SavedViewPage Articles = new("articles", "Articles", Permission.ReadTeamEdition,
        Tide.Application.Articles.ArticleFilter.Parameters, ["page", "new"]);

    /// <summary>A search's words: a saved search.</summary>
    public static readonly SavedViewPage Search = new("search", "Search", Permission.ReadTeamEdition, [("q", 500)], []);
}

/// <summary>
/// The signed-in person's own saved views of a list page (<see cref="SavedActionView"/>): a name for a combination of
/// its tab, layout and filters, pinned on the page as a pill. Like a preference, it only says how the page is shown to
/// the person who saved it, so anyone who can open the page may keep some, Viewers too, and no one can see or change
/// another's: every read and write is of the signed-in person's own rows of that page, and an id that isn't one of
/// theirs is "not found".
/// </summary>
public sealed class SavedViewService(IAppDbContextFactory dbs, ICurrentUser user, TimeProvider time)
{
    /// <summary>How many one person may keep on one page: a row of pills, not a library.</summary>
    public const int MaxViews = 12;

    /// <summary>The person's views of <paramref name="page"/>, in their order.</summary>
    public async Task<IReadOnlyList<SavedViewEntry>> ListAsync(SavedViewPage page, CancellationToken ct = default)
    {
        var me = Require(page);
        await using var db = await dbs.CreateAsync(ct);
        return await db.SavedActionViews.AsNoTracking().Where(v => v.UserId == me && v.Page == page.Key)
            .OrderBy(v => v.SortOrder).ThenBy(v => v.CreatedAtUtc)
            .Select(v => new SavedViewEntry(v.Id, v.Name, v.Query)).ToListAsync(ct);
    }

    /// <summary>
    /// Saves <paramref name="query"/> (the page's address after the <c>?</c>) as a view of <paramref name="page"/> named
    /// <paramref name="name"/>, after the person's others there. Throws <see cref="ArgumentException"/> for a name that's
    /// empty, too long or already one of theirs on the page, or a query <see cref="Normalize"/> refuses, and
    /// <see cref="InvalidOperationException"/> at <see cref="MaxViews"/>.
    /// </summary>
    public async Task<SavedViewEntry> SaveAsync(SavedViewPage page, string name, string query, CancellationToken ct = default)
    {
        var me = Require(page);
        var clean = CleanName(name);
        var stored = Normalize(page, query);
        await using var db = await dbs.CreateAsync(ct);
        var mine = await Mine(db, me, page, ct);
        if (mine.Count >= MaxViews) throw new InvalidOperationException($"You can keep up to {MaxViews} views. Delete one to save another.");
        RefuseTaken(mine, clean, except: null);
        var row = new SavedActionView
        {
            UserId = me, Page = page.Key, Name = clean, Query = stored, CreatedAtUtc = time.GetUtcNow(),
            SortOrder = mine.Count == 0 ? 0 : mine.Max(v => v.SortOrder) + 1,
        };
        db.SavedActionViews.Add(row);
        await SaveAsync(db, clean, ct);
        return new SavedViewEntry(row.Id, row.Name, row.Query);
    }

    /// <summary>Renames one of the person's views of the page; the same checks on the name as <see cref="SaveAsync(SavedViewPage, string, string, CancellationToken)"/>.</summary>
    public async Task RenameAsync(SavedViewPage page, Guid id, string name, CancellationToken ct = default)
    {
        var me = Require(page);
        var clean = CleanName(name);
        await using var db = await dbs.CreateAsync(ct);
        var mine = await Mine(db, me, page, ct);
        var row = Find(mine, id);
        RefuseTaken(mine, clean, except: id);
        row.Name = clean;
        await SaveAsync(db, clean, ct);
    }

    /// <summary>Deletes one of the person's views of the page.</summary>
    public async Task DeleteAsync(SavedViewPage page, Guid id, CancellationToken ct = default)
    {
        var me = Require(page);
        await using var db = await dbs.CreateAsync(ct);
        var row = await db.SavedActionViews.FirstOrDefaultAsync(v => v.Id == id && v.UserId == me && v.Page == page.Key, ct) ?? throw NotFound();
        db.SavedActionViews.Remove(row);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Moves one of the person's views of the page <paramref name="by"/> places: negative earlier, positive later,
    /// stopping at either end. Their order there is numbered again from 0.
    /// </summary>
    public async Task MoveAsync(SavedViewPage page, Guid id, int by, CancellationToken ct = default)
    {
        var me = Require(page);
        await using var db = await dbs.CreateAsync(ct);
        var mine = (await Mine(db, me, page, ct)).OrderBy(v => v.SortOrder).ThenBy(v => v.CreatedAtUtc).ToList();
        var row = Find(mine, id);
        var to = Math.Clamp(mine.IndexOf(row) + by, 0, mine.Count - 1);
        mine.Remove(row);
        mine.Insert(to, row);
        for (var i = 0; i < mine.Count; i++) mine[i].SortOrder = i;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// <paramref name="query"/> as it's stored and compared: a leading <c>?</c> dropped, each parameter once, in the order
    /// of the page's <see cref="SavedViewPage.Parameters"/>, its value unescaped, trimmed, checked against its length and
    /// escaped again; an empty value and the page's <see cref="SavedViewPage.Dropped"/> left out. Throws
    /// <see cref="ArgumentException"/> for a parameter the page doesn't have, one given twice, or a value longer than its own.
    /// </summary>
    public static string Normalize(SavedViewPage page, string? query)
    {
        var text = (query ?? "").Trim();
        if (text.StartsWith('?')) text = text[1..];
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in text.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            var key = Unescape(eq < 0 ? part : part[..eq]);
            var value = Unescape(eq < 0 ? "" : part[(eq + 1)..]).Trim();
            if (page.Dropped.Contains(key)) continue;
            var known = page.Parameters.FirstOrDefault(p => p.Name == key);
            if (known.Name is null) throw new ArgumentException($"A view can't hold \"{Cut(key)}\": only the {page.Title} page's own filters.", nameof(query));
            if (value.Length > known.Max) throw new ArgumentException($"The {key} filter is too long to keep (at most {known.Max} characters).", nameof(query));
            if (!values.TryAdd(key, value)) throw new ArgumentException($"The {key} filter is given twice.", nameof(query));
        }
        var stored = string.Join('&', page.Parameters.Where(p => values.TryGetValue(p.Name, out var v) && v.Length > 0)
            .Select(p => p.Name + "=" + Escape(values[p.Name])));
        if (stored.Length > SavedActionView.MaxQuery) throw new ArgumentException("These filters are too long to keep as a view.", nameof(query));
        return stored;
    }

    /// <summary>As <see cref="Normalize"/>, or null for a query it refuses: for comparing an address with a view.</summary>
    public static string? TryNormalize(SavedViewPage page, string? query)
    {
        try { return Normalize(page, query); }
        catch (ArgumentException) { return null; }
    }

    /// <summary>
    /// A page's parameters, as its address would carry them (a null value left out), as a view keeps them; null when
    /// they're more than a view can hold (a link's very long search).
    /// </summary>
    public static string? QueryOf(SavedViewPage page, IEnumerable<KeyValuePair<string, object?>> parameters) => TryNormalize(page, string.Join('&', parameters
        .Where(p => p.Value is not null)
        .Select(p => p.Key + "=" + Uri.EscapeDataString(Convert.ToString(p.Value, CultureInfo.InvariantCulture)!))));

    private static Task<List<SavedActionView>> Mine(IAppDbContext db, string me, SavedViewPage page, CancellationToken ct) =>
        db.SavedActionViews.Where(v => v.UserId == me && v.Page == page.Key).ToListAsync(ct);

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

    // Another person's id, or one of another page's, reads as one that doesn't exist: it says nothing about theirs.
    private static KeyNotFoundException NotFound() => new("That view isn't there any more. Reload the page to see your views.");

    // The person's other tab saved the same name at the same moment: the unique index refused this one.
    private static async Task SaveAsync(IAppDbContext db, string name, CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { throw Taken(name); }
    }

    // Who may keep views of a page is who may open it: the page's own permission, checked here as well as on the page.
    private string Require(SavedViewPage page)
    {
        if (!user.IsAuthenticated) throw new ForbiddenException("Sign in to keep a view.");
        if (!user.Has(page.Permission)) throw new ForbiddenException($"You don't have permission to {Policies.Describe(page.Permission)}.");
        return user.UserId;
    }
}
