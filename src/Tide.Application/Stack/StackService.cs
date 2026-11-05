using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Tide.Application.Abstractions;
using Tide.Application.Actions;
using Tide.Application.Articles;
using Tide.Application.Content;
using Tide.Application.Security;
using Tide.Application.Settings;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.Application.Stack;

/// <summary>A product as triage and the editor match it: its id, the name shown, and every name it's reported under.</summary>
public sealed record StackEntry(Guid Id, string Name, IReadOnlyList<string> Terms)
{
    // Each name as matched (" connect secure "), worked out once: matching runs against every article.
    internal IReadOnlyList<string> Keys { get; } = [.. Terms.Select(StackService.Normal).Where(w => w.Length > 0).Select(w => $" {w} ")];
}

/// <summary>What the Our stack form edits.</summary>
public sealed class StackForm
{
    public Guid? Id { get; set; }
    public string Name { get; set; } = "";
    public string? OtherNames { get; set; }
    public string? Versions { get; set; }
    public string? Notes { get; set; }
    public Guid? TeamId { get; set; }
    public DateOnly? CheckedOn { get; set; }

    public static StackForm From(StackProduct p) => new()
    {
        Id = p.Id, Name = p.Name, OtherNames = p.OtherNames, Versions = p.Versions, Notes = p.Notes, TeamId = p.TeamId, CheckedOn = p.CheckedOn,
    };
}

/// <summary>A product on the list: how many articles carry it (tagged or naming it) and how many of their actions are open.</summary>
public sealed record StackRow(StackProduct Product, string? TeamName, int Articles, int OpenActions);

/// <summary>An article in a product's history: tagged with it, or found because it names it.</summary>
public sealed record StackArticle(Guid Id, string Title, DateOnly On, ArticleStatus Status, bool Tagged);

/// <summary>An action in a product's history: raised from one of its articles, or naming it.</summary>
public sealed record StackAction(int Number, string Title, Priority Priority, ActionStatus Status, string TeamName, string? AssigneeName, DateOnly? DueDate);

/// <summary>
/// A product with its history, newest first, held to the team edition's <paramref name="Ceiling"/>: articles above it, and
/// actions raised from them, are counted (<paramref name="ArticlesAbove"/>, <paramref name="ActionsAbove"/>) but not named.
/// </summary>
public sealed record StackDetail(StackProduct Product, string? TeamName, IReadOnlyList<StackArticle> Articles, IReadOnlyList<StackAction> Actions,
    TlpLevel Ceiling, int ArticlesAbove = 0, int ActionsAbove = 0);

/// <summary>
/// A product as present mode shows it over an article's slide (team edition only: its notes are internal): its facts, and
/// the other articles about it, newest first, up to <see cref="StackService.CardArticles"/>.
/// </summary>
public sealed record StackCard(StackProduct Product, string? TeamName, IReadOnlyList<StackArticle> Others);

/// <summary>An article's products: those it's tagged with, and those it names but isn't tagged with (a suggestion).</summary>
public sealed record ArticleStack(IReadOnlyList<StackProduct> Tagged, IReadOnlyList<StackProduct> Suggested);

/// <summary>
/// Our stack (EP-25): what the organization runs, kept by analysts. Each product has the names it's reported under,
/// versions and notes, an owning team and when it was checked. Triage lifts a new item that names one and says which;
/// an article can be tagged with any number of them (optional), and each product's page lists the articles and actions
/// about it, tagged or found by name. A name matches as whole words, ignoring case, in a title or the start of a summary.
/// </summary>
public sealed partial class StackService(IAppDbContextFactory dbs, ICurrentUser user, Org.OrgClock clock, DistributionSettings editions,
    Briefs.IMeetingsInProgress? meetings = null)
{
    /// <summary>How much of a summary (an item's excerpt, an article's bottom line) is read for a name.</summary>
    public const int SummaryStart = 400;

    public const int MaxOtherNames = 12;

    [GeneratedRegex(@"[^\p{L}\p{N}]+", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex NotWord();

    // --- matching -------------------------------------------------------------------------------------------------

    /// <summary>The products still in use, as entries to match against (no permission check: triage reads them).</summary>
    internal static async Task<IReadOnlyList<StackEntry>> EntriesAsync(IAppDbContext db, CancellationToken ct) =>
        (await InUseAsync(db, ct)).Select(Entry).ToList();

    private static Task<List<StackProduct>> InUseAsync(IAppDbContext db, CancellationToken ct) =>
        db.StackProducts.AsNoTracking().Where(p => !p.IsRetired).OrderByName(p => p.Name).ToListAsync(ct);

    public static StackEntry Entry(StackProduct p) => new(p.Id, p.Name, [p.Name, .. SplitNames(p.OtherNames)]);

    /// <summary>The other names as typed: separated by semicolons, commas or lines, blanks and repeats dropped.</summary>
    public static IReadOnlyList<string> SplitNames(string? text) =>
        [.. (text ?? "").Split([';', ',', '\n', '\r'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>The first product <paramref name="title"/> or the start of <paramref name="summary"/> names, or null.</summary>
    public static StackEntry? Match(IReadOnlyList<StackEntry> entries, string title, string? summary)
    {
        if (entries.Count == 0) return null;
        var words = Words(title, summary);
        return entries.FirstOrDefault(e => Names(e, words));
    }

    /// <summary><see cref="Match"/> for many items against the same entries: each term is normalised once, not once an item.</summary>
    internal static Func<string, string?, StackEntry?> Matcher(IReadOnlyList<StackEntry> entries)
    {
        var terms = entries.Select(e => (Entry: e, Words: e.Terms.Select(Normal).Where(w => w.Length > 0).Select(w => $" {w} ").ToArray())).ToList();
        return (title, summary) =>
        {
            if (terms.Count == 0) return null;
            var words = Words(title, summary);
            return terms.FirstOrDefault(t => Array.Exists(t.Words, w => words.Contains(w, StringComparison.Ordinal))).Entry;
        };
    }

    /// <summary>Every product <paramref name="title"/> or the start of <paramref name="summary"/> names.</summary>
    public static IReadOnlyList<StackEntry> MatchAll(IReadOnlyList<StackEntry> entries, string title, string? summary)
    {
        if (entries.Count == 0) return [];
        var words = Words(title, summary);
        return entries.Where(e => Names(e, words)).ToList();
    }

    internal static string Words(string title, string? summary)
    {
        var start = summary is null ? "" : summary[..Math.Min(summary.Length, SummaryStart)];
        return $" {Normal(title)} {Normal(start)} ";
    }

    internal static bool Names(StackEntry e, string words) => e.Keys.Any(k => words.Contains(k, StringComparison.Ordinal));

    // Lower-case words separated by single spaces, so "Connect-Secure" and "connect secure" read alike.
    internal static string Normal(string text) => NotWord().Replace(text.ToLowerInvariant(), " ").Trim();

    // --- reading ------------------------------------------------------------------------------------------------

    /// <summary>The products in use, by name, without matching them against the articles (the editor's picker).</summary>
    public async Task<IReadOnlyList<StackProduct>> ProductsAsync(CancellationToken ct = default)
    {
        RequireRead();
        await using var db = await dbs.CreateAsync(ct);
        return await InUseAsync(db, ct);
    }

    public async Task<IReadOnlyList<StackRow>> ListAsync(bool includeRetired = false, CancellationToken ct = default)
    {
        RequireRead();
        await using var db = await dbs.CreateAsync(ct);
        var products = await db.StackProducts.AsNoTracking().Include(p => p.Team).Where(p => includeRetired || !p.IsRetired)
            .OrderBy(p => p.IsRetired).ThenByName(p => p.Name).ToListAsync(ct);
        var articles = await ArticlesAsync(db, ct);
        var tags = await db.ArticleProducts.AsNoTracking().Select(t => new { t.ArticleId, t.ProductId }).ToListAsync(ct);
        var open = await db.Actions.AsNoTracking().WhereOpen().Where(a => a.ArticleId != null)
            .Select(a => a.ArticleId!.Value).ToListAsync(ct);
        return products.Select(p =>
        {
            var e = Entry(p);
            var ids = articles.Where(a => Names(e, a.Words)).Select(a => a.Id)
                .Concat(tags.Where(t => t.ProductId == p.Id).Select(t => t.ArticleId)).ToHashSet();
            return new StackRow(p, p.Team?.Name, ids.Count, open.Count(ids.Contains));
        }).ToList();
    }

    public async Task<StackDetail?> GetAsync(Guid id, CancellationToken ct = default)
    {
        RequireRead();
        await using var db = await dbs.CreateAsync(ct);
        var p = await db.StackProducts.AsNoTracking().Include(x => x.Team).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return null;
        // The page is the team edition's readers': an article above its ceiling isn't named, as the brief leaves it out.
        var ceiling = (await editions.GetAsync(ct)).Team;
        var e = Entry(p);
        var history = await HistoryAsync(db, p, ct);
        var ids = history.Select(h => h.Article.Id).ToList();
        var aboveIds = (await db.Articles.AsNoTracking().Where(a => a.Tlp > ceiling).Select(a => a.Id).ToListAsync(ct)).ToHashSet();
        // Only the columns shown: every action is read, to find those that name the product without an article.
        var actions = (await db.Actions.AsNoTracking()
                .Select(a => new { a.ArticleId, a.Number, a.Title, a.Priority, a.Status, Team = a.Team!.Name, a.AssigneeName, a.DueDate })
                .ToListAsync(ct))
            .Where(a => (a.ArticleId is { } aid && ids.Contains(aid)) || Names(e, Words(a.Title, null)))
            .ToList();
        var shownActions = actions.Where(a => a.ArticleId is not { } aid || !aboveIds.Contains(aid))
            .OrderBy(a => ActionItem.IsClosedStatus(a.Status)).ThenBy(a => a.DueDate ?? DateOnly.MaxValue).ThenBy(a => a.Number)
            .Select(a => new StackAction(a.Number, a.Title, a.Priority, a.Status, a.Team ?? "", a.AssigneeName, a.DueDate)).ToList();
        var articles = history.Where(h => h.Tlp <= ceiling).Select(h => h.Article).ToList();
        return new StackDetail(p, p.Team?.Name, articles, shownActions, ceiling, history.Count - articles.Count, actions.Count - shownActions.Count);
    }

    /// <summary>How many other articles a present-mode card lists.</summary>
    public const int CardArticles = 5;

    /// <summary>The longest versions text a present-mode chip carries; longer ones are on its card.</summary>
    public const int ChipVersionsMax = 24;

    /// <summary>The versions a chip shows beside the name: short ones only, the rest are on the card.</summary>
    public static string? ChipVersions(string? versions) =>
        versions?.Trim() is { Length: > 0 and <= ChipVersionsMax } v ? v : null;

    /// <summary>
    /// The products each article is tagged with, for present mode's chips: those in use by name, then retired ones (shown
    /// muted, as the editor's card marks them). An article with none isn't in the result.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<StackProduct>>> ForArticlesAsync(IEnumerable<Guid> articleIds, CancellationToken ct = default)
    {
        var seats = Briefs.MeetingAccess.Seats(user, meetings);
        if (seats.Count == 0) RequireRead();
        var ids = articleIds.Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<Guid, IReadOnlyList<StackProduct>>();
        await using var db = await dbs.CreateAsync(ct);
        // A Visitor, from a seat in a live meeting: the We run chips of that brief's articles only (MeetingAccess).
        if (seats.Count > 0)
        {
            var briefs = seats.Select(x => x.BriefId).ToList();
            var onBrief = await db.BriefItems.AsNoTracking().Where(i => briefs.Contains(i.BriefId) && ids.Contains(i.ArticleId))
                .Select(i => i.ArticleId).Distinct().CountAsync(ct);
            if (onBrief != ids.Count) throw new ForbiddenException("You don't have access to Our stack.");
        }
        var tags = await db.ArticleProducts.AsNoTracking().Where(t => ids.Contains(t.ArticleId))
            .Select(t => new { t.ArticleId, Product = t.Product! }).ToListAsync(ct);
        return tags.GroupBy(t => t.ArticleId).ToDictionary(g => g.Key, g => (IReadOnlyList<StackProduct>)g.Select(t => t.Product)
            .OrderBy(p => p.IsRetired).ThenByName(p => p.Name).ToList());
    }

    /// <summary>
    /// A product's card for present mode: its facts and the other articles about it (tagged or naming it), newest first,
    /// leaving out <paramref name="exceptArticleId"/> (the one on the slide) and any above <paramref name="ceiling"/> (the
    /// team edition's): the card is on the meeting's screen, where the slides leave such an article out, so its title is
    /// too. Null when it's gone.
    /// </summary>
    public async Task<StackCard?> CardAsync(Guid id, TlpLevel ceiling, Guid? exceptArticleId = null, CancellationToken ct = default)
    {
        // A Visitor sees only the card the presenter has open for the room, while they're in the meeting (MeetingAccess).
        if (!Briefs.MeetingAccess.Seats(user, meetings).Any(s => s.Product == id)) RequireRead();
        await using var db = await dbs.CreateAsync(ct);
        var p = await db.StackProducts.AsNoTracking().Include(x => x.Team).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return null;
        var others = (await HistoryAsync(db, p, ct)).Where(h => h.Article.Id != exceptArticleId && h.Tlp <= ceiling)
            .Select(h => h.Article).Take(CardArticles).ToList();
        return new StackCard(p, p.Team?.Name, others);
    }

    // A product's articles, tagged with it or naming it, newest first, each with its TLP (for the ceiling the reader holds).
    private async Task<List<(StackArticle Article, TlpLevel Tlp)>> HistoryAsync(IAppDbContext db, StackProduct p, CancellationToken ct)
    {
        var e = Entry(p);
        var tagged = (await db.ArticleProducts.AsNoTracking().Where(t => t.ProductId == p.Id).Select(t => t.ArticleId).ToListAsync(ct)).ToHashSet();
        return (await ArticlesAsync(db, ct)).Where(a => tagged.Contains(a.Id) || Names(e, a.Words))
            .Select(a => (Article: new StackArticle(a.Id, a.Title, a.On, a.Status, tagged.Contains(a.Id)), a.Tlp))
            .OrderByDescending(h => h.Article.On).ToList();
    }

    /// <summary>An article's products, and those it names but isn't tagged with.</summary>
    public async Task<ArticleStack> ForArticleAsync(Guid articleId, CancellationToken ct = default)
    {
        RequireRead();
        await using var db = await dbs.CreateAsync(ct);
        var article = await db.Articles.AsNoTracking().Where(a => a.Id == articleId).Select(a => new { a.Title, a.Bluf }).FirstOrDefaultAsync(ct);
        if (article is null) return new ArticleStack([], []);
        var tagged = await db.ArticleProducts.AsNoTracking().Where(t => t.ArticleId == articleId).Select(t => t.Product!).OrderBy(p => p.IsRetired).ThenByName(p => p.Name).ToListAsync(ct);
        var products = await db.StackProducts.AsNoTracking().Where(p => !p.IsRetired).ToListAsync(ct);
        var named = MatchAll(products.Select(Entry).ToList(), article.Title, article.Bluf).Select(m => m.Id).ToHashSet();
        var suggested = products.Where(p => named.Contains(p.Id) && tagged.All(t => t.Id != p.Id)).OrderByName(p => p.Name).ToList();
        return new ArticleStack(tagged, suggested);
    }

    // When each article counts in a product's history: its first brief, else the day it was written (the organization's day).
    private sealed record ArticleFact(Guid Id, string Title, string Words, DateOnly On, ArticleStatus Status, TlpLevel Tlp);

    private async Task<List<ArticleFact>> ArticlesAsync(IAppDbContext db, CancellationToken ct)
    {
        var briefed = await ArticleDates.FirstBriefedAsync(db, ct);
        var rows = await db.Articles.AsNoTracking().Select(a => new { a.Id, a.Title, a.Bluf, a.CreatedAtUtc, a.Status, a.Tlp }).ToListAsync(ct);
        return rows.Select(a => new ArticleFact(a.Id, a.Title, Words(a.Title, a.Bluf),
            briefed.TryGetValue(a.Id, out var d) ? d : DateOnly.FromDateTime(clock.ToLocal(a.CreatedAtUtc).DateTime), a.Status, a.Tlp)).ToList();
    }

    // --- changing -----------------------------------------------------------------------------------------------

    /// <summary>Adds or saves a product. Analysts keep the list.</summary>
    public async Task<Guid> SaveAsync(StackForm form, CancellationToken ct = default)
    {
        RequireWrite();
        var name = ArticleService.CleanOrNull(form.Name, StackProduct.MaxName) ?? throw new ArgumentException("A product needs a name.");
        if (!name.Any(char.IsLetterOrDigit)) throw new ArgumentException("A product's name needs a letter or digit.");
        var others = SplitNames(form.OtherNames).Where(n => !string.Equals(n, name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (others.Count > MaxOtherNames) throw new ArgumentException($"Give at most {MaxOtherNames} other names.");
        if (others.FirstOrDefault(n => !n.Any(char.IsLetterOrDigit)) is { } bare) throw new ArgumentException($"'{bare}' needs a letter or digit.");
        var otherText = others.Count == 0 ? null : string.Join("; ", others);
        if (otherText?.Length > StackProduct.MaxOtherNames) throw new ArgumentException("The other names are too long.");

        await using var db = await dbs.CreateAsync(ct);
        // One product per name, case aside: compared here (the list is short), the same on both providers.
        var taken = await db.StackProducts.Where(p => p.Id != form.Id).Select(p => p.Name).ToListAsync(ct);
        if (taken.Exists(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException($"{name} is already on the list.");
        if (form.TeamId is { } team && !await db.Teams.AnyAsync(t => t.Id == team, ct)) throw new ArgumentException("That team no longer exists.");
        StackProduct p;
        if (form.Id is { } id)
        {
            p = await db.StackProducts.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException("That product no longer exists.");
        }
        else
        {
            p = new StackProduct();
            db.StackProducts.Add(p);
        }
        p.Name = name;
        p.OtherNames = otherText;
        p.Versions = ArticleService.CleanOrNull(form.Versions, StackProduct.MaxVersions);
        p.Notes = ArticleService.CleanOrNull(form.Notes, StackProduct.MaxNotes, multiline: true);
        p.TeamId = form.TeamId;
        p.CheckedOn = form.CheckedOn;
        await db.SaveChangesAsync(ct);
        return p.Id;
    }

    /// <summary>Adds a product by name alone (from triage or the editor), or finds it if it's there; its id.</summary>
    public async Task<Guid> AddByNameAsync(string name, CancellationToken ct = default)
    {
        RequireWrite();
        var clean = ArticleService.CleanOrNull(name, StackProduct.MaxName) ?? throw new ArgumentException("A product needs a name.");
        await using (var db = await dbs.CreateAsync(ct))
        {
            var products = await db.StackProducts.ToListAsync(ct);
            if (products.Find(p => string.Equals(p.Name, clean, StringComparison.OrdinalIgnoreCase)) is { } there)
            {
                if (there.IsRetired) { there.IsRetired = false; await db.SaveChangesAsync(ct); }
                return there.Id;
            }
        }
        return await SaveAsync(new StackForm { Name = clean }, ct);
    }

    public async Task SetRetiredAsync(Guid id, bool retired, CancellationToken ct = default)
    {
        RequireWrite();
        await using var db = await dbs.CreateAsync(ct);
        var p = await db.StackProducts.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException("That product no longer exists.");
        p.IsRetired = retired;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Tags an article with a product. Only that tag changes, so a colleague's tags made since the page loaded stay, and
    /// tagging one it already has (two people at once) does nothing.
    /// </summary>
    public async Task TagArticleAsync(Guid articleId, Guid productId, CancellationToken ct = default)
    {
        RequireWrite();
        await using var db = await dbs.CreateAsync(ct);
        if (!await db.Articles.AnyAsync(a => a.Id == articleId, ct)) throw new KeyNotFoundException("That article no longer exists.");
        if (!await db.StackProducts.AnyAsync(p => p.Id == productId, ct)) throw new ArgumentException("That product is no longer on the list.");
        if (await db.ArticleProducts.AnyAsync(t => t.ArticleId == articleId && t.ProductId == productId, ct)) return;
        db.ArticleProducts.Add(new ArticleProduct { ArticleId = articleId, ProductId = productId });
        try { await db.SaveChangesAsync(ct); }
        // Someone tagged it between the check and the save: it's tagged, as asked.
        catch (DbUpdateException) { if (!await TaggedAsync(articleId, productId, ct)) throw; }
    }

    private async Task<bool> TaggedAsync(Guid articleId, Guid productId, CancellationToken ct)
    {
        await using var db = await dbs.CreateAsync(ct);
        return await db.ArticleProducts.AnyAsync(t => t.ArticleId == articleId && t.ProductId == productId, ct);
    }

    /// <summary>Untags a product from an article; nothing else changes, and one already untagged is fine.</summary>
    public async Task UntagArticleAsync(Guid articleId, Guid productId, CancellationToken ct = default)
    {
        RequireWrite();
        await using var db = await dbs.CreateAsync(ct);
        if (await db.ArticleProducts.FirstOrDefaultAsync(t => t.ArticleId == articleId && t.ProductId == productId, ct) is not { } tag) return;
        db.ArticleProducts.Remove(tag);
        try { await db.SaveChangesAsync(ct); }
        // Someone untagged it first.
        catch (DbUpdateConcurrencyException) { }
    }

    private void RequireRead()
    {
        if (!user.Has(Permission.ReadTeamEdition)) throw new ForbiddenException("You don't have access to Our stack.");
    }

    private void RequireWrite()
    {
        if (!user.Has(Permission.WriteArticles)) throw new ForbiddenException("Only analysts can change Our stack.");
    }
}
