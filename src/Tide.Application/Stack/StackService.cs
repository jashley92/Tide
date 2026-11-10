using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Tide.Application.Abstractions;
using Tide.Application.Actions;
using Tide.Application.Articles;
using Tide.Application.Content;
using Tide.Application.Search;
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

/// <summary>
/// Every product's names, indexed by their first word, so a text is matched against them all in one pass over its words
/// rather than once a product. <see cref="In"/> finds exactly the entries <see cref="StackService.Names"/> would.
/// </summary>
internal sealed class StackNames
{
    private readonly Dictionary<string, List<(StackEntry Entry, string Key)>> _byFirstWord = new(StringComparer.Ordinal);

    public StackNames(IEnumerable<StackEntry> entries)
    {
        foreach (var e in entries)
            foreach (var key in e.Keys)
            {
                // A key is " connect secure ": its first word runs from 1 to the next space.
                var first = key[1..key.IndexOf(' ', 1)];
                if (!_byFirstWord.TryGetValue(first, out var keys)) _byFirstWord[first] = keys = [];
                keys.Add((e, key));
            }
    }

    /// <summary>The entries <paramref name="words"/> (as <see cref="StackService.Words"/> makes them) names, each once.</summary>
    public IReadOnlyCollection<StackEntry> In(string words)
    {
        HashSet<StackEntry>? found = null;
        var byWord = _byFirstWord.GetAlternateLookup<ReadOnlySpan<char>>();
        for (int space = words.IndexOf(' '), next; space >= 0 && (next = words.IndexOf(' ', space + 1)) >= 0; space = next)
        {
            if (next == space + 1 || !byWord.TryGetValue(words.AsSpan(space + 1, next - space - 1), out var keys)) continue;
            foreach (var (entry, key) in keys)
                if (string.CompareOrdinal(words, space, key, 0, key.Length) == 0) (found ??= []).Add(entry);
        }
        return found ?? (IReadOnlyCollection<StackEntry>)[];
    }
}

/// <summary>What the Our stack form edits.</summary>
public sealed class StackForm
{
    public Guid? Id { get; set; }

    /// <summary>The product's <see cref="StackProduct.EditVersion"/> when the form opened; a save over a colleague's save
    /// made since is refused. Null skips the check (a new product, or a caller that means to overwrite).</summary>
    public Guid? EditVersion { get; set; }

    public string Name { get; set; } = "";
    public string? OtherNames { get; set; }
    public string? Versions { get; set; }
    public string? Notes { get; set; }
    public Guid? TeamId { get; set; }
    public DateOnly? CheckedOn { get; set; }

    /// <summary>The vendor or support contact an Outreach email about it is offered to.</summary>
    public string? ContactName { get; set; }
    public string? ContactEmail { get; set; }

    public static StackForm From(StackProduct p) => new()
    {
        Id = p.Id, EditVersion = p.EditVersion, Name = p.Name, OtherNames = p.OtherNames, Versions = p.Versions, Notes = p.Notes, TeamId = p.TeamId, CheckedOn = p.CheckedOn,
        ContactName = p.ContactName, ContactEmail = p.ContactEmail,
    };
}

/// <summary>
/// A product on the list: how many articles carry it (tagged or naming it), how many of their actions are open, what is
/// happening to it <paramref name="Now"/>, and whether it's in use with a check that is old or missing
/// (<paramref name="CheckOld"/>, by <see cref="StackService.CheckIsOld"/>).
/// </summary>
public sealed record StackRow(StackProduct Product, string? TeamName, int Articles, int OpenActions, StackNow Now, bool CheckOld)
{
    /// <summary>In use, and no team owns it.</summary>
    public bool NoTeam => !Product.IsRetired && Product.TeamId is null;

    /// <summary>
    /// Whether the product needs a look: it's in use, and something is happening to it right now, its check is old or
    /// missing, or no team owns it. The one definition: the list's order and its "Needs a look" filter both read it.
    /// </summary>
    public bool NeedsLook => !Product.IsRetired && (Now.Any || CheckOld || NoTeam);
}

/// <summary>The Our stack list: its rows, those that need a look first, and the team edition's ceiling "Right now" is held to.</summary>
public sealed record StackList(IReadOnlyList<StackRow> Rows, TlpLevel Ceiling);

/// <summary>A CVE in a product's "Right now", with what's known about it (null until it's enriched).</summary>
public sealed record StackCve(string Id, CveRecord? Record);

/// <summary>A CVE on a recent brief's article about a product; <paramref name="Above"/> when that article is above the ceiling.</summary>
public sealed record CveSighting(string Cve, bool Above, CveRecord? Record);

/// <summary>
/// What is happening to a product right now: the CVEs on its articles from the last <see cref="StackService.RecentBriefDays"/>
/// days' briefs, the worst first (on KEV, then by CVSS, then by EPSS); how many more come only from articles above the
/// team edition's ceiling, counted and not named (<paramref name="CvesAbove"/>); and how many new triage items name it
/// (<paramref name="TriageItems"/>, null for someone who can't triage).
/// </summary>
public sealed record StackNow(IReadOnlyList<StackCve> Cves, int CvesAbove, int? TriageItems)
{
    public bool Any => Cves.Count > 0 || CvesAbove > 0 || TriageItems > 0;
}

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
    Briefs.IMeetingsInProgress? meetings = null) : IArticleTagSource
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

    private static Task<List<StackProduct>> InUseAsync(IAppDbContext db, CancellationToken ct) => InUse(db).ToListAsync(ct);

    /// <summary>The products still in use, by name: the order matching tries them in.</summary>
    internal static IQueryable<StackProduct> InUse(IAppDbContext db) =>
        db.StackProducts.AsNoTracking().Where(p => !p.IsRetired).OrderByName(p => p.Name);

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

    /// <summary>How old a product's "Checked on" date may be before the list flags it. No setting holds it.</summary>
    public const int CheckOldAfterDays = 90;

    /// <summary>How many days back a brief counts as recent for the list's "Right now".</summary>
    public const int RecentBriefDays = 7;

    /// <summary>Whether a product's check is old: never made, or more than <see cref="CheckOldAfterDays"/> days before <paramref name="today"/>.</summary>
    public static bool CheckIsOld(DateOnly? checkedOn, DateOnly today) => checkedOn is not { } on || on.AddDays(CheckOldAfterDays) < today;

    /// <summary>
    /// A product's "Right now" from the CVEs seen on its recent articles. A CVE is named when at least one of those
    /// articles is at or below the ceiling; one seen only above it is counted, and nothing else about it is kept.
    /// </summary>
    public static StackNow RightNow(IEnumerable<CveSighting> sightings, int? triageItems)
    {
        var byCve = sightings.GroupBy(s => s.Cve, StringComparer.OrdinalIgnoreCase).ToList();
        var named = byCve.Where(g => g.Any(s => !s.Above)).Select(g => new StackCve(g.Key, g.First(s => !s.Above).Record))
            .OrderByDescending(c => c.Record?.IsKev == true).ThenByDescending(c => c.Record?.CvssScore ?? -1).ThenByDescending(c => c.Record?.EpssScore ?? -1)
            .ThenBy(c => c.Id, StringComparer.Ordinal).ToList();
        return new StackNow(named, byCve.Count - named.Count, triageItems);
    }

    /// <summary>
    /// The products with their counts, by name, retired ones last: what the API lists. "Right now" isn't read (each
    /// row's is empty), so a row's <see cref="StackRow.NeedsLook"/> here speaks only of its check and its team.
    /// </summary>
    public async Task<IReadOnlyList<StackRow>> ListAsync(bool includeRetired = false, CancellationToken ct = default)
    {
        RequireRead();
        return await RowsAsync(includeRetired, null, ct);
    }

    /// <summary>
    /// The list page: each product with its counts and its "Right now"; those that need a look first, then by name,
    /// retired ones last. Read in a fixed number of queries however many products there are: every article is matched
    /// against every name in one pass (<see cref="StackNames"/>), and the new triage items once, as triage matches
    /// them. "Right now" is held to the team edition's ceiling, and the triage count is only for those who triage.
    /// </summary>
    public async Task<StackList> OverviewAsync(bool includeRetired = false, CancellationToken ct = default)
    {
        RequireRead();
        var ceiling = (await editions.GetAsync(ct)).Team;
        // A stable sort: the rows are read by name with the retired last, and a retired product never needs a look.
        return new StackList((await RowsAsync(includeRetired, ceiling, ct)).OrderBy(r => !r.NeedsLook).ToList(), ceiling);
    }

    // The rows by name, retired last; with "Right now" held to nowCeiling when one is given.
    private async Task<List<StackRow>> RowsAsync(bool includeRetired, TlpLevel? nowCeiling, CancellationToken ct)
    {
        var today = clock.Today;
        await using var db = await dbs.CreateAsync(ct);
        var products = await db.StackProducts.AsNoTracking().Include(p => p.Team).Where(p => includeRetired || !p.IsRetired)
            .OrderBy(p => p.IsRetired).ThenByName(p => p.Name).ToListAsync(ct);

        // Each product's articles: those naming it, then those tagged with it.
        var articles = await db.Articles.AsNoTracking().Select(a => new { a.Id, a.Title, Bluf = a.Bluf.Substring(0, SummaryStart), a.Tlp }).ToListAsync(ct);
        var mine = products.ToDictionary(p => p.Id, _ => new HashSet<Guid>());
        var names = new StackNames(products.Select(Entry));
        foreach (var a in articles)
            foreach (var e in names.In(Words(a.Title, a.Bluf))) mine[e.Id].Add(a.Id);
        foreach (var t in await db.ArticleProducts.AsNoTracking().Select(t => new { t.ArticleId, t.ProductId }).ToListAsync(ct))
            if (mine.TryGetValue(t.ProductId, out var ids)) ids.Add(t.ArticleId);
        var open = (await db.Actions.AsNoTracking().WhereOpen().Where(a => a.ArticleId != null).GroupBy(a => a.ArticleId!.Value)
            .Select(g => new { ArticleId = g.Key, Count = g.Count() }).ToListAsync(ct)).ToDictionary(x => x.ArticleId, x => x.Count);

        var sightings = Enumerable.Empty<(Guid ArticleId, CveSighting Cve)>().ToLookup(s => s.ArticleId, s => s.Cve);
        Dictionary<Guid, int>? triage = null;
        if (nowCeiling is { } ceiling)
        {
            // The CVEs on the recent briefs' articles, each with what's known about it and whether its article is above the ceiling.
            var from = today.AddDays(-RecentBriefDays);
            var recent = db.BriefItems.Where(i => db.Briefs.Any(b => b.Id == i.BriefId && b.BriefDate >= from)).Select(i => i.ArticleId);
            var above = articles.Where(a => a.Tlp > ceiling).Select(a => a.Id).ToHashSet();
            sightings = (await (from c in db.ArticleCves.AsNoTracking()
                                where recent.Contains(c.ArticleId)
                                join r in db.CveRecords.AsNoTracking() on c.CveId equals r.CveId into records
                                from r in records.DefaultIfEmpty()
                                select new { c.ArticleId, c.CveId, Record = r }).ToListAsync(ct))
                .ToLookup(c => c.ArticleId, c => new CveSighting(c.CveId, above.Contains(c.ArticleId), c.Record));
            if (user.Has(Permission.WriteArticles)) triage = await NewItemsNamingAsync(db, products.Where(p => !p.IsRetired).Select(Entry).ToList(), ct);
        }

        return products.Select(p =>
        {
            var ids = mine[p.Id];
            return new StackRow(p, p.Team?.Name, ids.Count, ids.Sum(id => open.GetValueOrDefault(id)),
                RightNow(ids.SelectMany(id => sightings[id]), triage?.GetValueOrDefault(p.Id)), !p.IsRetired && CheckIsOld(p.CheckedOn, today));
        }).ToList();
    }

    // How many new triage items name each product, as triage says it: each item goes to the first product it names (its
    // "We run" mark), matched once over the items. Items from a retired source aren't in the inbox, so they aren't here.
    private static async Task<Dictionary<Guid, int>> NewItemsNamingAsync(IAppDbContext db, IReadOnlyList<StackEntry> inUse, CancellationToken ct)
    {
        if (inUse.Count == 0) return [];
        var items = await db.FeedItems.AsNoTracking().Where(i => i.Status == FeedItemStatus.New && !i.Source!.IsRetired)
            .Select(i => new { i.Title, Excerpt = i.Excerpt == null ? null : i.Excerpt.Substring(0, SummaryStart) }).ToListAsync(ct);
        var match = Matcher(inUse);
        return items.Select(i => match(i.Title, i.Excerpt)).Where(e => e is not null).GroupBy(e => e!.Id).ToDictionary(g => g.Key, g => g.Count());
    }

    public async Task<StackDetail?> GetAsync(Guid id, CancellationToken ct = default)
    {
        RequireRead();
        await using var db = await dbs.CreateAsync(ct);
        var p = await db.StackProducts.AsNoTracking().Include(x => x.Team).FirstOrDefaultAsync(x => x.Id == id, ct);
        return p is null ? null : await DetailAsync(db, p, await DetailFactsAsync(db, ct), ct);
    }

    /// <summary>
    /// <see cref="GetAsync"/> for each of <paramref name="ids"/> (an article's tags, on the editor's card), the articles
    /// and actions read once for them all rather than once a product. One that's gone isn't in the result.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, StackDetail>> DetailsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        RequireRead();
        var details = new Dictionary<Guid, StackDetail>();
        if (ids.Count == 0) return details;
        await using var db = await dbs.CreateAsync(ct);
        var products = await db.StackProducts.AsNoTracking().Include(x => x.Team).Where(x => ids.Contains(x.Id)).ToListAsync(ct);
        var facts = await DetailFactsAsync(db, ct);
        foreach (var p in products) details[p.Id] = await DetailAsync(db, p, facts, ct);
        return details;
    }

    // What every product's page reads alike: the team edition's ceiling, the articles, those above it, and every action
    // (only the columns shown: every action is read, to find those that name the product without an article).
    private sealed record ActionFact(Guid? ArticleId, int Number, string Title, Priority Priority, ActionStatus Status, string? Team, string? AssigneeName, DateOnly? DueDate);

    private sealed record DetailFacts(TlpLevel Ceiling, List<ArticleFact> Articles, HashSet<Guid> Above, List<ActionFact> Actions);

    private async Task<DetailFacts> DetailFactsAsync(IAppDbContext db, CancellationToken ct)
    {
        // The page is the team edition's readers': an article above its ceiling isn't named, as the brief leaves it out.
        var ceiling = (await editions.GetAsync(ct)).Team;
        var articles = await ArticlesAsync(db, ct);
        var aboveIds = (await db.Articles.AsNoTracking().Where(a => a.Tlp > ceiling).Select(a => a.Id).ToListAsync(ct)).ToHashSet();
        var actions = await db.Actions.AsNoTracking()
            .Select(a => new ActionFact(a.ArticleId, a.Number, a.Title, a.Priority, a.Status, a.Team!.Name, a.AssigneeName, a.DueDate))
            .ToListAsync(ct);
        return new DetailFacts(ceiling, articles, aboveIds, actions);
    }

    private static async Task<StackDetail> DetailAsync(IAppDbContext db, StackProduct p, DetailFacts facts, CancellationToken ct)
    {
        var (ceiling, aboveIds) = (facts.Ceiling, facts.Above);
        var e = Entry(p);
        var history = await HistoryAsync(db, p, facts.Articles, ct);
        var ids = history.Select(h => h.Article.Id).ToList();
        var actions = facts.Actions
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
        if (!Briefs.MeetingAccess.Seats(user, meetings).Any(s => s.Card == Briefs.MeetingCard.ForProduct(id))) RequireRead();
        await using var db = await dbs.CreateAsync(ct);
        var p = await db.StackProducts.AsNoTracking().Include(x => x.Team).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return null;
        var others = (await HistoryAsync(db, p, await ArticlesAsync(db, ct), ct)).Where(h => h.Article.Id != exceptArticleId && h.Tlp <= ceiling)
            .Select(h => h.Article).Take(CardArticles).ToList();
        return new StackCard(p, p.Team?.Name, others);
    }

    // A product's articles, tagged with it or naming it, newest first, each with its TLP (for the ceiling the reader holds).
    private static async Task<List<(StackArticle Article, TlpLevel Tlp)>> HistoryAsync(IAppDbContext db, StackProduct p, List<ArticleFact> articles,
        CancellationToken ct)
    {
        var e = Entry(p);
        var tagged = (await db.ArticleProducts.AsNoTracking().Where(t => t.ProductId == p.Id).Select(t => t.ArticleId).ToListAsync(ct)).ToHashSet();
        return articles.Where(a => tagged.Contains(a.Id) || Names(e, a.Words))
            .Select(a => (Article: new StackArticle(a.Id, a.Title, a.On, a.Status, tagged.Contains(a.Id)), a.Tlp))
            .OrderByDescending(h => h.Article.On).ToList();
    }

    /// <summary>An article's products, and those it names but isn't tagged with.</summary>
    public async Task<ArticleStack> ForArticleAsync(Guid articleId, CancellationToken ct = default)
    {
        RequireRead();
        await using var db = await dbs.CreateAsync(ct);
        return await MineAsync(db, articleId, ct) ?? None;
    }

    private static readonly ArticleStack None = new([], []);

    // The article's products and those it names, from the one context; null when the article has gone.
    private static async Task<ArticleStack?> MineAsync(IAppDbContext db, Guid articleId, CancellationToken ct)
    {
        var article = await db.Articles.AsNoTracking().Where(a => a.Id == articleId).Select(a => new { a.Title, a.Bluf }).FirstOrDefaultAsync(ct);
        if (article is null) return null;
        var tagged = await db.ArticleProducts.AsNoTracking().Where(t => t.ArticleId == articleId).Select(t => t.Product!).OrderBy(p => p.IsRetired).ThenByName(p => p.Name).ToListAsync(ct);
        var products = await db.StackProducts.AsNoTracking().Where(p => !p.IsRetired).ToListAsync(ct);
        var named = MatchAll(products.Select(Entry).ToList(), article.Title, article.Bluf).Select(m => m.Id).ToHashSet();
        var suggested = products.Where(p => named.Contains(p.Id) && tagged.All(t => t.Id != p.Id)).OrderByName(p => p.Name).ToList();
        return new ArticleStack(tagged, suggested);
    }

    /// <summary>The most products the article editor's "We run" picker lists at once.</summary>
    public const int PickLimit = NamedSearch.PickLimit;

    // What the "We run" picker matches a product by (its name and other names), and shows beside it (its versions).
    private static readonly NamedFields<StackProduct> Picked = new(p => p.Id, p => p.Name, p => p.OtherNames, p => p.IsRetired, p => p.Versions);

    /// <summary>
    /// The article editor's "We run" card in one read: <see cref="ForArticleAsync"/>, and (when <paramref name="offer"/>,
    /// for an analyst's picker) what the picker offers before anything is typed, at most <see cref="PickLimit"/>: the
    /// products the article's text names (its "matches in the text"), then those tagged on any article most recently (by
    /// the audit trail); never one the article already has, nor a retired one.
    /// </summary>
    public async Task<(ArticleStack Mine, IReadOnlyList<TagPick> Offered)> ForArticleCardAsync(Guid articleId, bool offer, CancellationToken ct = default)
    {
        RequireRead();
        await using var db = await dbs.CreateAsync(ct);
        if (await MineAsync(db, articleId, ct) is not { } mine) return (None, []);
        return (mine, offer ? await OfferAsync(db, mine, ct) : []);
    }

    private static async Task<IReadOnlyList<TagPick>> OfferAsync(IAppDbContext db, ArticleStack mine, CancellationToken ct)
    {
        var offers = new NamedSearch.Offers(mine.Tagged.Select(p => p.Id));
        offers.Add(mine.Suggested, Picked, PickReason.Named);
        if (!offers.Full)
        {
            var ids = await History.AuditKeys.RecentTaggedAsync(db, nameof(ArticleProduct), NamedSearch.PickCandidates, ct);
            var byId = (await db.StackProducts.AsNoTracking().Where(p => ids.Contains(p.Id) && !p.IsRetired).ToListAsync(ct)).ToDictionary(p => p.Id);
            offers.Add(ids.Where(byId.ContainsKey).Select(id => byId[id]), Picked, PickReason.Recent);
        }
        return offers.Picks;
    }

    /// <summary>
    /// The products the "We run" picker lists for what's typed, at most <see cref="PickLimit"/>: any whose name or other
    /// names hold it, ignoring case; exact matches first, then those starting with it, then the rest, retired ones last.
    /// The search runs in the database and reads a bounded few, never the whole list. Those the article is already
    /// tagged with aren't listed.
    /// </summary>
    public async Task<TagPicks> FindForArticleAsync(Guid articleId, string? words, CancellationToken ct = default)
    {
        RequireRead();
        if (NamedSearch.Text(words, StackProduct.MaxName) is not { } text) return TagPicks.None;
        await using var db = await dbs.CreateAsync(ct);
        var tagged = await db.ArticleProducts.AsNoTracking().Where(t => t.ArticleId == articleId).Select(t => t.Product!).ToListAsync(ct);
        return await NamedSearch.FindAsync(db.StackProducts.AsNoTracking(), tagged, Picked, text, ct);
    }

    // How well a product matches what was typed, and the other name that did when its own name matched less well.
    internal static (int Rank, string? Other) Matching(StackProduct p, string text) => Picked.Match(p, text);

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
        var contactEmail = ArticleService.Tidy(form.ContactEmail) is { Length: > 0 } typed ? typed : null;
        if (contactEmail is not null && (contactEmail.Length > StackProduct.MaxContactEmail || !DistributionSettings.IsAddress(contactEmail)))
            throw new ArgumentException($"The contact's address isn't an email address: {contactEmail}");

        await using var db = await dbs.CreateAsync(ct);
        // One product per name, case aside: compared here (the list is short), the same on both providers.
        var taken = await db.StackProducts.Where(p => p.Id != form.Id).Select(p => p.Name).ToListAsync(ct);
        if (taken.Exists(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException($"{name} is already on the list.");
        if (form.TeamId is { } team && !await db.Teams.AnyAsync(t => t.Id == team, ct)) throw new ArgumentException("That team no longer exists.");
        StackProduct p;
        if (form.Id is { } id)
        {
            p = await db.StackProducts.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException("That product no longer exists.");
            // The page keeps a form someone is typing in when a colleague saves the product: saving it then would put
            // back everything they changed.
            if (form.EditVersion is { } began && began != p.EditVersion) throw new StaleEditException(Stale(p));
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
        p.ContactName = ArticleService.CleanOrNull(form.ContactName, StackProduct.MaxContactName);
        p.ContactEmail = contactEmail;
        p.EditVersion = Guid.NewGuid();
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw new StaleEditException(Stale(p)); }
        return p.Id;
    }

    private static string Stale(StackProduct p) =>
        $"Someone else saved {p.Name} since you opened it. Reload to see their changes, then make yours again.";

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
