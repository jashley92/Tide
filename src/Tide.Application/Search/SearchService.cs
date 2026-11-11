using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Tide.Application.Abstractions;
using Tide.Application.Indicators;
using Tide.Application.Mitre;
using Tide.Application.Security;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Domain.Observables;

namespace Tide.Application.Search;

/// <summary>An article that matched.</summary>
public sealed record ArticleHit(
    Guid Id, string Title, string? SourceName, TlpLevel Tlp, Classification Classification, Priority? Priority,
    ArticleStatus Status, string AuthorName, DateTimeOffset CreatedAtUtc, IReadOnlyList<DateOnly> BriefDates, string? Snippet);

/// <summary>An action that matched, with how its hunts went (null when it has none), its kind and the article it's from.</summary>
public sealed record ActionHit(
    Guid Id, int Number, string Title, ActionStatus Status, Priority Priority, string TeamName, string? AssigneeName,
    DateOnly? DueDate, HuntResult? Hunted, string? Snippet, ActionKind Kind = ActionKind.Other, Guid? ArticleId = null)
{
    public bool IsOpen => !ActionItem.IsClosedStatus(Status);

    /// <summary>A sweep or a hunt: an action that searches logs for indicators.</summary>
    public bool Hunts => Kind is ActionKind.IocSweep or ActionKind.ThreatHunt;
}

/// <summary>A story that matched.</summary>
public sealed record StoryHit(Guid Id, string Name, StoryStatus Status, int Articles, string? Snippet);

/// <summary>Hits of one kind: one page of them, and how many there are in all.</summary>
public sealed record HitList<T>(IReadOnlyList<T> Items, int Total)
{
    public static readonly HitList<T> None = new([], 0);

    /// <summary>There are more than this page shows.</summary>
    public bool More => Total > Items.Count;
}

/// <summary>
/// Which page of each kind of hit to show, from 1, <see cref="Size"/> hits a page (<see cref="SearchService.MaxHits"/>).
/// With <see cref="LookupOnce"/>, the articles a lookup lists are left out of the article hits (and their count), for a
/// page that shows both and lists each article once.
/// </summary>
public sealed record SearchPages(int Articles = 1, int Actions = 1, int Stories = 1, int Size = SearchService.MaxHits, bool LookupOnce = false)
{
    public static readonly SearchPages First = new();
}

/// <summary>A brief an article is on: its day, and whether it was presented (its meeting was ended, or it was sent).</summary>
public sealed record LookupBrief(DateOnly Day, bool Presented);

/// <summary>
/// An article a lookup found, as its one row shows it: its badges, when it was written, the briefs it's on (oldest
/// first), and the story it belongs to, if any.
/// </summary>
public sealed record LookupArticle(
    Guid Id, string Title, TlpLevel Tlp, Classification Classification, Priority? Priority, ArticleStatus Status,
    DateTimeOffset CreatedAtUtc, IReadOnlyList<LookupBrief> Briefs, SeenStory? Story = null);

/// <summary>
/// An article an indicator is on. <see cref="Related"/> is set when it's a different indicator on the same host (a
/// subdomain, a URL or email address on the domain), not the indicator itself.
/// </summary>
public sealed record Sighting(LookupArticle Article, IndicatorType Type, string Value, string? Note, bool Related)
{
    public Guid ArticleId => Article.Id;
}

/// <summary>Another indicator on an article a lookup found.</summary>
public sealed record SeenIndicator(IndicatorType Type, string Value);

/// <summary>The story an article a lookup found belongs to.</summary>
public sealed record SeenStory(Guid Id, string Name);

/// <summary>
/// What else is on the articles a lookup found (the ones carrying the indicator itself, or about the CVE): their other
/// indicators, CVEs, ATT&amp;CK techniques and stories, the most shared first, at most <see cref="SearchService.MaxSeenWith"/> of each.
/// </summary>
public sealed record SeenWith(
    IReadOnlyList<SeenIndicator> Indicators, IReadOnlyList<string> Cves, IReadOnlyList<string> Techniques, IReadOnlyList<SeenStory> Stories)
{
    public static readonly SeenWith None = new([], [], [], []);

    public bool Any => Indicators.Count + Cves.Count + Techniques.Count + Stories.Count > 0;
}

/// <summary>
/// A lookup's answer in a line: how many articles it's on, the last presented brief any of them was on
/// (<see cref="LastBriefed"/>), the latest brief one is on that hasn't been presented (<see cref="OnBrief"/>), the
/// actions raised (how many in all, and how many of those listed are open) and, for an indicator, how the hunts for it
/// went: hits if any found some, else no hits if any came back clean, else not run when a hunt is waiting or a sweep is
/// open, and null when nothing hunts for it. A CVE has no hunt result: a hunt searches for indicators.
/// <see cref="NewInTriage"/> is how many new items in Triage's inbox name it, for someone who triages; null for anyone
/// else, who can't see them.
/// </summary>
public sealed record LookupSummary(int Articles, DateOnly? LastBriefed, DateOnly? OnBrief, int Actions, int OpenActions, HuntResult? Hunted,
    int? NewInTriage = null)
{
    /// <summary>Nothing in TIDE has it: no article, no action, nothing new in Triage. Never said to someone who can't see
    /// Triage, which may have it.</summary>
    public bool Unseen => Articles == 0 && Actions == 0 && NewInTriage == 0;

    public static LookupSummary Of(IReadOnlyCollection<LookupArticle> articles, HitList<ActionHit> actions, bool hunted, int? newInTriage) => new(
        articles.Count,
        Latest(articles, presented: true),
        Latest(articles, presented: false),
        actions.Total,
        actions.Items.Count(a => a.IsOpen),
        !hunted ? null
            : actions.Items.Any(a => a.Hunted == HuntResult.Hits) ? HuntResult.Hits
            : actions.Items.Any(a => a.Hunted == HuntResult.NoHits) ? HuntResult.NoHits
            : actions.Items.Any(a => a.Hunted is not null || (a.IsOpen && a.Hunts)) ? HuntResult.NotRun : null,
        newInTriage);

    private static DateOnly? Latest(IEnumerable<LookupArticle> articles, bool presented) =>
        articles.SelectMany(a => a.Briefs).Where(b => b.Presented == presented).Select(b => (DateOnly?)b.Day).Max();
}

/// <summary>
/// Where an indicator has been seen, and the actions raised from those articles or carrying it themselves.
/// <see cref="Sweeping"/> are the open sweeps and hunts among them that search for the indicator itself: raised from an
/// article that carries it, or carrying it.
/// </summary>
public sealed record IndicatorLookup(
    IndicatorType Type, string Value, string? Host, HitList<Sighting> Sightings, HitList<ActionHit> Actions,
    IReadOnlyList<ActionHit> Sweeping, SeenWith SeenWith, LookupSummary Summary);

/// <summary>What's known about a CVE, the articles about it (newest first), and the actions raised from them.</summary>
public sealed record CveLookup(
    string CveId, CveRecord? Record, HitList<LookupArticle> Articles, HitList<ActionHit> Actions, SeenWith SeenWith, LookupSummary Summary)
{
    /// <summary>The sweeps and hunts among the actions: they searched for the articles' indicators, not for the CVE.</summary>
    public IReadOnlyList<ActionHit> Sweeps => Actions.Items.Where(a => a.Hunts || a.Hunted is not null).ToList();
}

/// <summary>The answer to a search: an indicator or CVE lookup when the query is one, and the text hits.</summary>
public sealed record SearchResults(
    string Query, IReadOnlyList<string> Terms, IndicatorLookup? Indicator, CveLookup? Cve,
    HitList<ArticleHit> Articles, HitList<ActionHit> Actions, HitList<StoryHit> Stories)
{
    public bool Empty => Indicator is null && Cve is null && Articles.Items.Count == 0 && Actions.Items.Count == 0 && Stories.Items.Count == 0;
}

/// <summary>
/// Search across articles (every part of one: bullets, indicators, CVEs, techniques, captions), actions and stories,
/// and indicator lookup: paste an IP, domain, URL, hash, email address or CVE and see every article it's on, the
/// indicators on the same host, and what was done about it. Open to the team (it shows every TLP, as the article pages
/// do); Readers have the published archive instead.
/// </summary>
public sealed class SearchService(IAppDbContextFactory dbs, ICurrentUser user)
{
    public const int MaxHits = 50;
    public const int MaxSightings = 200;

    /// <summary>How far into one kind of hit the pages go; past it, the search wants narrowing.</summary>
    public const int MaxTake = 1000;

    /// <summary>The most of each kind (indicators, CVEs, techniques, stories) a lookup's "Seen with" names.</summary>
    public const int MaxSeenWith = 12;

    // The newest articles "Seen with" reads, and the most rows of each kind it reads from them.
    private const int SeenWithArticles = 50, SeenWithRows = 2000;

    public Task<SearchResults> SearchAsync(string? query, CancellationToken ct = default) => SearchAsync(query, SearchPages.First, ct);

    public async Task<SearchResults> SearchAsync(string? query, SearchPages pages, CancellationToken ct = default)
    {
        if (!user.Has(Permission.ReadTeamEdition)) throw new ForbiddenException("Search is open to the team.");
        var q = (query ?? "").Trim();
        var terms = SearchText.Terms(q);
        if (terms.Count == 0) return new SearchResults(q, terms, null, null, HitList<ArticleHit>.None, HitList<ActionHit>.None, HitList<StoryHit>.None);

        await using var db = await dbs.CreateAsync(ct);
        IndicatorLookup? indicator = null;
        CveLookup? cve = null;
        if (terms.Count == 1)
        {
            // Triage's inbox is only for those who triage (who write articles); anyone else is told nothing of it.
            var triages = user.Has(Permission.WriteArticles);
            if (IocExtractor.NormalizeCve(terms[0]) is { } cveId) cve = await CveAsync(db, cveId, triages, ct);
            else indicator = await IndicatorAsync(db, terms[0], triages, ct);
        }
        // A lone digit is an action's number; as text it would match nearly everything, so only actions are looked in.
        var numberOnly = terms is [{ Length: 1 }];
        List<Guid> listed = !pages.LookupOnce ? []
            : (indicator?.Sightings.Items.Select(s => s.ArticleId) ?? cve?.Articles.Items.Select(a => a.Id) ?? []).Distinct().ToList();
        return new SearchResults(q, terms, indicator, cve,
            numberOnly ? HitList<ArticleHit>.None : await ArticlesAsync(db, terms, listed, Skip(pages.Articles, pages.Size), Size(pages.Size), ct),
            await ActionsAsync(db, terms, Skip(pages.Actions, pages.Size), Size(pages.Size), ct),
            numberOnly ? HitList<StoryHit>.None : await StoriesAsync(db, terms, Skip(pages.Stories, pages.Size), Size(pages.Size), ct));
    }

    private static int Size(int size) => Math.Clamp(size, 1, MaxHits);

    // The hits before a page: none for the first, and never past MaxTake (a page there shows the last that fits).
    private static int Skip(int page, int size) => Math.Clamp((page - 1) * Size(size), 0, Math.Max(0, MaxTake - Size(size)));

    // ---- Text search ----

    private static async Task<HitList<ArticleHit>> ArticlesAsync(IAppDbContext db, IReadOnlyList<string> terms, List<Guid> except, int skip, int take, CancellationToken ct)
    {
        IQueryable<Article> q = db.Articles.AsNoTracking();
        if (except.Count > 0) q = q.Where(a => !except.Contains(a.Id));
        var adversaryTags = db.ArticleAdversaries;
        foreach (var term in terms)
        {
            // A term that names ATT&CK techniques ("phishing", "T1566") also finds the articles tagged with them.
            var techniques = term.Length >= 4 ? AttackCatalog.Search(term, 30).Select(t => t.Id).ToArray() : [];
            Expression<Func<Article, bool>>? tagged = techniques.Length == 0 ? null : a => a.Techniques.Any(t => techniques.Contains(t.TechniqueId));
            // So do the adversary profiles it's tagged with, by any of their names (the threat actors it's about): the
            // profiles are a short list, matched first, and an article then by its tags' ids.
            var profiles = await db.Adversaries.AsNoTracking()
                .Where(AnyForm<Adversary>((x, p) => EF.Functions.Like(x.Name, p, SearchText.Escape) || EF.Functions.Like(x.Aliases!, p, SearchText.Escape), term))
                .Select(x => x.Id).ToListAsync(ct);
            Expression<Func<Article, bool>>? profiled = profiles.Count == 0 ? null : a => adversaryTags.Any(t => t.ArticleId == a.Id && profiles.Contains(t.AdversaryId));
            q = q.Where(AnyForm<Article>((a, p) =>
                EF.Functions.Like(a.Title, p, SearchText.Escape) || EF.Functions.Like(a.Bluf, p, SearchText.Escape)
                || EF.Functions.Like(a.WhyItMatters!, p, SearchText.Escape) || EF.Functions.Like(a.RecommendedActions!, p, SearchText.Escape)
                || EF.Functions.Like(a.Actors!, p, SearchText.Escape) || EF.Functions.Like(a.Sectors!, p, SearchText.Escape)
                || EF.Functions.Like(a.SourceName!, p, SearchText.Escape)
                || a.Bullets.Any(b => EF.Functions.Like(b.Text, p, SearchText.Escape))
                || a.Indicators.Any(i => EF.Functions.Like(i.Value, p, SearchText.Escape) || EF.Functions.Like(i.Note!, p, SearchText.Escape))
                || a.Cves.Any(c => EF.Functions.Like(c.CveId, p, SearchText.Escape))
                || a.Techniques.Any(t => EF.Functions.Like(t.TechniqueId, p, SearchText.Escape))
                || a.Screenshots.Any(s => EF.Functions.Like(s.Caption!, p, SearchText.Escape)), term, tagged, profiled));
        }
        var total = await q.CountAsync(ct);
        // Title matches first, among the newest MaxTake matches: the same ones whichever page is asked for (and in the same order: ties by id), so a later
        // page neither repeats nor skips what an earlier one ranked differently. Only ids and titles: cheap at this size.
        var candidates = await q.OrderByDescending(a => a.CreatedAtUtc).ThenBy(a => a.Id).Take(MaxTake)
            .Select(a => new { a.Id, a.Title }).ToListAsync(ct);
        var forms = SearchText.AllForms(terms);
        var picked = candidates
            .OrderByDescending(c => forms.Any(f => c.Title.Contains(f, StringComparison.OrdinalIgnoreCase)))
            .Skip(skip).Take(take).Select(c => c.Id).ToList();
        var rows = await db.Articles.AsNoTracking().Where(a => picked.Contains(a.Id))
            .Include(a => a.Bullets).Include(a => a.Indicators).Include(a => a.Screenshots)
            .AsSplitQuery().ToListAsync(ct);
        var dates = await Lookups.BriefDatesAsync(db, picked, ct);
        var actors = await Adversaries.AdversaryService.ActorNamesAsync(db, rows.Select(a => (a.Id, a.Actors)).ToList(), ct);
        var byId = rows.ToDictionary(a => a.Id);
        // An indicator goes in as pages show it (defanged), so a snippet never carries a live link.
        var hits = picked.Select(id => byId[id]).Select(a => new ArticleHit(a.Id, a.Title, a.SourceName, a.Tlp, a.Classification,
            a.Priority, a.Status, a.AuthorName, a.CreatedAtUtc, dates.GetValueOrDefault(a.Id) ?? [],
            SearchText.Snippet(
                new[] { a.Bluf, a.WhyItMatters, a.RecommendedActions }
                    .Concat(a.Bullets.OrderBy(b => b.SortOrder).Select(b => b.Text))
                    .Concat(a.Indicators.SelectMany(i => new[] { IndicatorLabels.Shown(i.Type, i.Value), i.Note }))
                    .Concat(a.Screenshots.Select(s => s.Caption))
                    .Append(string.Join(", ", actors[a.Id])).Append(a.Sectors),
                forms, a.Bluf))).ToList();
        return new HitList<ArticleHit>(hits, total);
    }

    private static async Task<HitList<ActionHit>> ActionsAsync(IAppDbContext db, IReadOnlyList<string> terms, int skip, int take, CancellationToken ct)
    {
        IQueryable<ActionItem> q = db.Actions.AsNoTracking();
        foreach (var term in terms)
        {
            if (term.Length == 1)
            {
                // A lone digit: that action alone, not every action with the digit somewhere in its words.
                var only = SearchText.ActionNumber(term);
                q = q.Where(a => a.Number == only);
                continue;
            }
            Expression<Func<ActionItem, bool>>? numbered = SearchText.ActionNumber(term) is { } n ? a => a.Number == n : null;
            q = q.Where(AnyForm<ActionItem>((a, p) =>
                EF.Functions.Like(a.Title, p, SearchText.Escape) || EF.Functions.Like(a.Description!, p, SearchText.Escape)
                || EF.Functions.Like(a.OutcomeNote!, p, SearchText.Escape) || EF.Functions.Like(a.TicketRef!, p, SearchText.Escape)
                || EF.Functions.Like(a.AssigneeName!, p, SearchText.Escape)
                || db.ActionComments.Any(c => c.ActionId == a.Id && EF.Functions.Like(c.Text, p, SearchText.Escape)), term, numbered));
        }
        var total = await q.CountAsync(ct);
        var rows = await Lookups.ActionRows(db, q.OrderBy(a => ActionItem.ClosedStatuses.Contains(a.Status)).ThenByDescending(a => a.Number).Skip(skip).Take(take), ct);
        var forms = SearchText.AllForms(terms);
        var comments = await CommentsAsync(db, rows.Select(r => r.Id).ToList(), ct);
        var hits = rows.Select(r => r.Hit(SearchText.Snippet(
            new[] { r.Description, r.OutcomeNote, r.TicketRef }.Concat(comments.GetValueOrDefault(r.Id) ?? []), forms, r.Description))).ToList();
        return new HitList<ActionHit>(hits, total);
    }

    private static async Task<HitList<StoryHit>> StoriesAsync(IAppDbContext db, IReadOnlyList<string> terms, int skip, int take, CancellationToken ct)
    {
        IQueryable<Story> q = db.Stories.AsNoTracking();
        foreach (var term in terms)
            q = q.Where(AnyForm<Story>((s, p) => EF.Functions.Like(s.Name, p, SearchText.Escape) || EF.Functions.Like(s.Summary!, p, SearchText.Escape), term));
        var total = await q.CountAsync(ct);
        var rows = await q.OrderBy(s => s.Status).ThenByDescending(s => s.CreatedAtUtc).ThenBy(s => s.Id).Skip(skip).Take(take)
            .Select(s => new { s.Id, s.Name, s.Status, s.Summary, Articles = db.Articles.Count(a => a.StoryId == s.Id) })
            .ToListAsync(ct);
        var forms = SearchText.AllForms(terms);
        var hits = rows.Select(s => new StoryHit(s.Id, s.Name, s.Status, s.Articles, SearchText.Snippet([s.Summary], forms, s.Summary))).ToList();
        return new HitList<StoryHit>(hits, total);
    }

    // ---- Lookup ----

    private static async Task<IndicatorLookup?> IndicatorAsync(IAppDbContext db, string term, bool triages, CancellationToken ct)
    {
        var live = IocObservable.Refang(term);
        var type = IocObservable.DetectType(live);
        if (type is IndicatorType.Other or IndicatorType.CommandLine) return null;
        var value = IocObservable.Normalize(type, live);
        var family = IocObservable.FamilyTypes(type).ToList();
        var exact = SearchText.Literal(value);
        var matching = db.ArticleIndicators.AsNoTracking()
            .Where(i => family.Contains(i.Type) && EF.Functions.Like(i.Value, exact, SearchText.Escape));
        var rows = await matching.Take(MaxSightings + 1).ToListAsync(ct);
        var exactTotal = rows.Count > MaxSightings ? await matching.CountAsync(ct) : rows.Count;

        // The same host under another indicator: a subdomain, a URL or email address on the domain, the domain of a URL.
        var host = SearchText.HostOf(type, value);
        var related = new List<ArticleIndicator>();
        if (host is not null)
        {
            var hostTypes = new[] { IndicatorType.Domain, IndicatorType.Url, IndicatorType.EmailAddress, IndicatorType.IpAddress };
            var near = SearchText.Contains(host);
            var seen = rows.Select(r => r.Id).ToHashSet();
            related = (await db.ArticleIndicators.AsNoTracking()
                    .Where(i => hostTypes.Contains(i.Type) && EF.Functions.Like(i.Value, near, SearchText.Escape))
                    .Take(MaxSightings * 3).ToListAsync(ct))
                .Where(i => !seen.Contains(i.Id) && SearchText.HostOf(i.Type, i.Value) is { } h && SearchText.SameOrUnder(h, host))
                .ToList();
        }

        var all = rows.Select(r => (Row: r, Related: false)).Concat(related.Select(r => (Row: r, Related: true))).ToList();
        var articleIds = all.Select(x => x.Row.ArticleId).Distinct().ToList();
        var articles = (await LookupArticlesAsync(db, db.Articles.AsNoTracking().Where(a => articleIds.Contains(a.Id)), ct)).ToDictionary(a => a.Id);
        var sightings = all.Where(x => articles.ContainsKey(x.Row.ArticleId))
            .Select(x => (x, a: articles[x.Row.ArticleId]))
            .OrderBy(t => t.x.Related).ThenByDescending(t => t.a.CreatedAtUtc).ThenBy(t => t.x.Row.Value, StringComparer.OrdinalIgnoreCase)
            .Select(t => new Sighting(t.a, t.x.Row.Type, t.x.Row.Value, t.x.Row.Note, t.x.Related))
            .ToList();
        // The articles carrying the indicator itself, newest first: what the first line counts and "Seen with" reads.
        var on = sightings.Where(s => !s.Related).Select(s => s.Article).DistinctBy(a => a.Id).ToList();

        // An action can carry indicators of its own (one raised with no article): it's on this one too.
        var carrying = await db.ActionIndicators.AsNoTracking()
            .Where(i => family.Contains(i.Type) && EF.Functions.Like(i.Value, exact, SearchText.Escape))
            .Select(i => i.ActionId).Distinct().Take(MaxHits).ToListAsync(ct);
        var actions = await Lookups.ActionsOnAsync(db, articleIds, carrying, MaxHits, ct);
        var sweeping = actions.Items
            .Where(a => a.IsOpen && a.Hunts && (carrying.Contains(a.Id) || on.Any(x => x.Id == a.ArticleId)))
            .ToList();
        // The first line counts what was done about the indicator itself, as Sweeping does: a sweep raised from an article
        // on a related host searched for that host, not this one. The list below it still names them all.
        var onIds = on.Select(a => a.Id).ToList();
        var itself = onIds.Count == articleIds.Count ? actions : await Lookups.ActionsOnAsync(db, onIds, carrying, MaxHits, ct);
        var seenWith = await SeenWithAsync(db, on, i => family.Contains(i.Type) && string.Equals(i.Value, value, StringComparison.OrdinalIgnoreCase), null, ct);
        return new IndicatorLookup(type, value, host,
            new HitList<Sighting>(sightings.Take(MaxSightings).ToList(), sightings.Count + exactTotal - rows.Count),
            // Triage keeps no indicators of an item's own (it reads CVEs from a title and summary, not indicators), so for
            // someone who triages there are none there to count.
            actions, sweeping, seenWith, LookupSummary.Of(on, itself, hunted: true, triages ? 0 : null));
    }

    private static async Task<CveLookup> CveAsync(IAppDbContext db, string cveId, bool triages, CancellationToken ct)
    {
        var record = await db.CveRecords.AsNoTracking().FirstOrDefaultAsync(c => c.CveId == cveId, ct);
        var articleIds = await db.ArticleCves.AsNoTracking().Where(c => c.CveId == cveId).Select(c => c.ArticleId).Distinct().ToListAsync(ct);
        List<LookupArticle> newest = articleIds.Count == 0 ? [] : await LookupArticlesAsync(db,
            db.Articles.AsNoTracking().Where(a => articleIds.Contains(a.Id)).OrderByDescending(a => a.CreatedAtUtc).ThenBy(a => a.Id).Take(MaxSightings), ct);
        var actions = await Lookups.ActionsOnAsync(db, articleIds, [], MaxHits, ct);
        return new CveLookup(cveId, record, new HitList<LookupArticle>(newest, articleIds.Count), actions,
            await SeenWithAsync(db, newest, null, cveId, ct), LookupSummary.Of(newest, actions, hunted: false, triages ? await NewInTriageAsync(db, cveId, ct) : null));
    }

    // The new items in Triage's inbox (of a source still read) whose title or summary names the CVE, up to MaxSightings.
    // Their CVEs are a comma-separated list, so a longer id that starts with this one is told apart here.
    private static async Task<int> NewInTriageAsync(IAppDbContext db, string cveId, CancellationToken ct) =>
        (await db.FeedItems.AsNoTracking()
            .Where(i => i.Status == FeedItemStatus.New && !i.Source!.IsRetired && EF.Functions.Like(i.Cves!, SearchText.Contains(cveId), SearchText.Escape))
            .Select(i => i.Cves!).Take(MaxSightings).ToListAsync(ct))
        .Count(cves => cves.Split(',').Contains(cveId, StringComparer.OrdinalIgnoreCase));

    // The articles of <paramref name="q"/> as a lookup lists them, in its order.
    private static async Task<List<LookupArticle>> LookupArticlesAsync(IAppDbContext db, IQueryable<Article> q, CancellationToken ct)
    {
        var rows = await q.Select(a => new
        {
            a.Id, a.Title, a.Tlp, a.Classification, a.Priority, a.Status, a.CreatedAtUtc, a.StoryId,
            StoryName = db.Stories.Where(s => s.Id == a.StoryId).Select(s => s.Name).FirstOrDefault(),
        }).ToListAsync(ct);
        var dates = await Lookups.BriefsAsync(db, rows.Select(a => a.Id).ToList(), ct);
        return rows.Select(a => new LookupArticle(a.Id, a.Title, a.Tlp, a.Classification, a.Priority, a.Status, a.CreatedAtUtc,
            dates.GetValueOrDefault(a.Id) ?? [], a.StoryId is { } story && a.StoryName is { } name ? new SeenStory(story, name) : null)).ToList();
    }

    /// <summary>
    /// What else is on <paramref name="articles"/> (newest first; the newest <see cref="SeenWithArticles"/> are read): one
    /// query each for their indicators, CVEs and techniques, whatever their number. The value looked up is left out
    /// (<paramref name="lookedUp"/>, <paramref name="cveId"/>).
    /// </summary>
    private static async Task<SeenWith> SeenWithAsync(IAppDbContext db, IReadOnlyList<LookupArticle> articles,
        Func<SeenIndicator, bool>? lookedUp, string? cveId, CancellationToken ct)
    {
        var read = articles.Take(SeenWithArticles).ToList();
        if (read.Count == 0) return SeenWith.None;
        var ids = read.Select(a => a.Id).ToList();
        var indicators = (await db.ArticleIndicators.AsNoTracking().Where(i => ids.Contains(i.ArticleId))
                .Select(i => new { i.Type, i.Value }).Take(SeenWithRows).ToListAsync(ct))
            .Select(i => new SeenIndicator(i.Type, i.Value.Trim()))
            .Where(i => i.Value.Length > 0 && lookedUp?.Invoke(i) != true);
        var cves = (await db.ArticleCves.AsNoTracking().Where(c => ids.Contains(c.ArticleId)).Select(c => c.CveId).Take(SeenWithRows).ToListAsync(ct))
            .Where(c => c != cveId);
        var techniques = await db.ArticleTechniques.AsNoTracking().Where(t => ids.Contains(t.ArticleId)).Select(t => t.TechniqueId).Take(SeenWithRows).ToListAsync(ct);
        return new SeenWith(
            MostShared(indicators, i => (i.Type, i.Value.ToLowerInvariant()), i => i.Value),
            MostShared(cves, c => c, c => c),
            MostShared(techniques, t => t, t => t),
            MostShared(read.Select(a => a.Story).OfType<SeenStory>(), s => s.Id, s => s.Name));
    }

    // Each of <paramref name="items"/> once, those on the most articles first, then by name: the first MaxSeenWith.
    private static List<T> MostShared<T, TKey>(IEnumerable<T> items, Func<T, TKey> key, Func<T, string> name) => items
        .GroupBy(key).OrderByDescending(g => g.Count()).ThenBy(g => name(g.First()), StringComparer.OrdinalIgnoreCase)
        .Take(MaxSeenWith).Select(g => g.First()).ToList();

    // ---- Shared ----

    private static async Task<Dictionary<Guid, List<string>>> CommentsAsync(IAppDbContext db, List<Guid> actionIds, CancellationToken ct) =>
        (await db.ActionComments.AsNoTracking().Where(c => actionIds.Contains(c.ActionId)).OrderBy(c => c.AtUtc)
            .Select(c => new { c.ActionId, c.Text }).ToListAsync(ct))
        .GroupBy(c => c.ActionId).ToDictionary(g => g.Key, g => g.Select(c => c.Text).ToList());

    /// <summary>
    /// <paramref name="match"/> (an entity and a LIKE pattern) for any of <paramref name="term"/>'s forms, or
    /// <paramref name="extra"/>. Each pattern goes in as a query parameter.
    /// </summary>
    private static Expression<Func<T, bool>> AnyForm<T>(Expression<Func<T, string, bool>> match, string term, params Expression<Func<T, bool>>?[] extras)
    {
        var entity = match.Parameters[0];
        Expression? body = null;
        foreach (var form in SearchText.Forms(term))
        {
            var pattern = Expression.Property(Expression.Constant(new Pattern(SearchText.Contains(form))), nameof(Pattern.Value));
            var one = new Swap(match.Parameters[1], pattern).Visit(match.Body);
            body = body is null ? one : Expression.OrElse(body, one);
        }
        foreach (var extra in extras.OfType<Expression<Func<T, bool>>>())
            body = Expression.OrElse(body!, new Swap(extra.Parameters[0], entity).Visit(extra.Body));
        return Expression.Lambda<Func<T, bool>>(body!, entity);
    }

    private sealed record Pattern(string Value);

    private sealed class Swap(ParameterExpression from, Expression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : base.VisitParameter(node);
    }
}
