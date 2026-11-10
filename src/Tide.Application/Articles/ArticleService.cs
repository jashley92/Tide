using Microsoft.EntityFrameworkCore;
using Tide.Application.Abstractions;
using Tide.Application.Files;
using Tide.Application.Indicators;
using Tide.Application.Mitre;
using Tide.Application.Org;
using Tide.Application.Search;
using Tide.Application.Security;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Domain.Observables;

namespace Tide.Application.Articles;

/// <summary>
/// A row on the article list. <paramref name="MatchedTechniques"/> are the article's techniques an ATT&amp;CK filter
/// listed it for (<see cref="ArticleFilter.MatchingTechniques"/>): none without one.
/// </summary>
public sealed record ArticleSummary(
    Guid Id, string Title, string? SourceName, TlpLevel Tlp, Classification Classification, Priority? Priority,
    ArticleStatus Status, string AuthorName, DateTimeOffset CreatedAtUtc, DateTimeOffset? ModifiedAtUtc, int Screenshots,
    IReadOnlyList<DateOnly> BriefDates, IReadOnlyList<string> MatchedTechniques);

/// <summary>A page of the article list, and how many articles match in all.</summary>
public sealed record ArticlePage(IReadOnlyList<ArticleSummary> Rows, int Total);

/// <summary>What the article list sorts by; ties go newest first.</summary>
public enum ArticleSort { Written, Title, Status, Tlp }

/// <summary>Thrown when a save was made from a copy someone else has since changed.</summary>
public sealed class StaleEditException(string message) : Exception(message);

/// <summary>Writing and reading articles: the analyst's write-ups that briefs are built from.</summary>
public sealed class ArticleService(IAppDbContextFactory dbs, ICurrentUser user, ScreenshotService screenshots, OrgClock clock)
{
    public const int MaxTitle = 300;
    public const int MaxBluf = 600;
    public const int MaxBullet = 2000;
    public const int MaxLongText = 8000;

    public async Task<IReadOnlyList<ArticleSummary>> ListAsync(string? search = null, ArticleStatus? status = null,
        int take = 200, CancellationToken ct = default)
    {
        RequireRead();
        await using var db = await dbs.CreateAsync(ct);
        return await SummariesAsync(db, Matching(db, search, status).OrderByDescending(a => a.CreatedAtUtc).Take(take), ct);
    }

    /// <summary>
    /// The first <paramref name="take"/> articles matching <paramref name="search"/> and <paramref name="status"/> in
    /// <paramref name="sort"/>'s order, and how many match in all.
    /// </summary>
    public Task<ArticlePage> PageAsync(string? search, ArticleStatus? status, ArticleSort sort, bool descending, int take,
        CancellationToken ct = default) =>
        PageAsync(new ArticleFilter { Search = search, Status = status }, sort, descending, take, ct: ct);

    /// <summary>
    /// <paramref name="take"/> articles matching <paramref name="filter"/> in <paramref name="sort"/>'s order, after the
    /// first <paramref name="skip"/>, and how many match in all: a page of the article list.
    /// </summary>
    public async Task<ArticlePage> PageAsync(ArticleFilter filter, ArticleSort sort, bool descending, int take, int skip = 0,
        CancellationToken ct = default)
    {
        RequireRead();
        await using var db = await dbs.CreateAsync(ct);
        var q = Narrowed(db, Matching(db, filter.Search, filter.Status), filter);
        var total = await q.CountAsync(ct);
        // Titles ignoring case, as SQL Server's collation compares them and the other lists sort: SQLite's own order is
        // case-sensitive, which put every lower-case title after the last capital one. ToLower runs in the database (lower(),
        // LOWER()), never in .NET, so no culture applies; EF doesn't translate ToLowerInvariant.
#pragma warning disable CA1304, CA1311
        IOrderedQueryable<Article> ordered = (sort, descending) switch
        {
            (ArticleSort.Title, false) => q.OrderBy(a => a.Title.ToLower()),
            (ArticleSort.Title, true) => q.OrderByDescending(a => a.Title.ToLower()),
            (ArticleSort.Status, false) => q.OrderBy(a => a.Status),
            (ArticleSort.Status, true) => q.OrderByDescending(a => a.Status),
            (ArticleSort.Tlp, false) => q.OrderBy(a => a.Tlp),
            (ArticleSort.Tlp, true) => q.OrderByDescending(a => a.Tlp),
            (_, false) => q.OrderBy(a => a.CreatedAtUtc),
            _ => q.OrderByDescending(a => a.CreatedAtUtc),
        };
#pragma warning restore CA1304, CA1311
        if (sort != ArticleSort.Written) ordered = ordered.ThenByDescending(a => a.CreatedAtUtc);
        return new ArticlePage(await SummariesAsync(db, ordered.ThenBy(a => a.Id).Skip(Math.Max(0, skip)).Take(Math.Clamp(take, 1, MaxPage)), ct, filter), total);
    }

    /// <summary>The most one page of the article list holds.</summary>
    public const int MaxPage = 1000;

    // The filters past the words and status, each as the trends report counts it (TrendsService.ReportAsync).
    private IQueryable<Article> Narrowed(IAppDbContext db, IQueryable<Article> q, ArticleFilter f)
    {
        if (f.From is not null || f.To is not null)
        {
            // Counted on its first brief's day, else (never briefed, not a draft) the local day it was written.
            var from = f.From ?? DateOnly.MinValue;
            var to = f.To ?? DateOnly.MaxValue;
            var start = f.From is { } d1 ? clock.StartOfDay(d1) : DateTimeOffset.MinValue;
            var end = f.To is { } d2 && d2 < DateOnly.MaxValue ? clock.StartOfDay(d2.AddDays(1)) : DateTimeOffset.MaxValue;
            // Each test starts from the article's brief items (indexed by article) and reads the brief's date from there.
            // Starting from Briefs instead made SQLite scan every brief for every article: seconds, not milliseconds.
            var briefed = db.BriefItems.Join(db.Briefs, i => i.BriefId, b => b.Id, (i, b) => new { i.ArticleId, b.BriefDate });
            q = q.Where(a =>
                (briefed.Any(x => x.ArticleId == a.Id && x.BriefDate >= from && x.BriefDate <= to)
                 && !briefed.Any(x => x.ArticleId == a.Id && x.BriefDate < from))
                || (!briefed.Any(x => x.ArticleId == a.Id) && a.Status != ArticleStatus.Draft
                    && a.CreatedAtUtc >= start && a.CreatedAtUtc < end));
        }
        q = f.Assessment switch
        {
            ArticleAssessment.Threat => q.Where(a => a.Classification == Classification.Threat),
            ArticleAssessment.Informational => q.Where(a => a.Classification == Classification.Informational),
            ArticleAssessment.P1P2 => q.Where(a => a.Priority == Priority.P1 || a.Priority == Priority.P2),
            _ => q,
        };
        if (f.Tag == ArticleFilter.None) q = q.Where(a => !a.Tags.Any());
        else if (f.Tag is { } tag) q = q.Where(a => a.Tags.Any(t => t.Tag!.Key == tag));
        if (f.Category is { } category) q = q.Where(a => a.Tags.Any(t => t.Tag!.Category!.Key == category));

        if (f.Technique == ArticleFilter.None)
        {
            q = q.Where(a => !a.Techniques.Any());
            if (f.Tactic is not null) q = q.Where(_ => false);
        }
        else if (f.Technique is not null || f.Tactic is not null)
        {
            // One technique on the article does both: it's the technique (or one of its sub-techniques) and under the tactic.
            IQueryable<ArticleTechnique> links = db.ArticleTechniques.AsNoTracking();
            if (f.Technique is { } id)
            {
                var subs = id + ".";
                links = f.WithSubtechniques ? links.Where(t => t.TechniqueId == id || t.TechniqueId.StartsWith(subs)) : links.Where(t => t.TechniqueId == id);
            }
            if (f.Tactic is { } tactic)
            {
                var inTactic = AttackCatalog.All.Where(t => t.Tactics.Contains(tactic)).Select(t => t.Id).ToList();
                links = links.Where(t => inTactic.Contains(t.TechniqueId));
            }
            q = q.Where(a => links.Any(t => t.ArticleId == a.Id));
        }
        return q;
    }

    private static IQueryable<Article> Matching(IAppDbContext db, string? search, ArticleStatus? status)
    {
        var q = db.Articles.AsNoTracking();
        if (status is { } s) q = q.Where(a => a.Status == s);
        // Every word, anywhere in the title, bottom line, source, CVEs or indicators, ignoring case (LIKE, not instr).
        foreach (var word in (search ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var p = SearchText.Contains(word);
            q = q.Where(a => EF.Functions.Like(a.Title, p, SearchText.Escape) || EF.Functions.Like(a.Bluf, p, SearchText.Escape)
                             || EF.Functions.Like(a.SourceName!, p, SearchText.Escape)
                             || a.Cves.Any(c => EF.Functions.Like(c.CveId, p, SearchText.Escape))
                             || a.Indicators.Any(i => EF.Functions.Like(i.Value, p, SearchText.Escape)));
        }
        return q;
    }

    // With an ATT&CK filter, each row's techniques come in one read for the page, and the filter picks the ones it matched.
    private static async Task<IReadOnlyList<ArticleSummary>> SummariesAsync(IAppDbContext db, IQueryable<Article> q, CancellationToken ct,
        ArticleFilter? filter = null)
    {
        var rows = await q
            .Select(a => new
            {
                a.Id, a.Title, a.SourceName, a.Tlp, a.Classification, a.Priority, a.Status, a.AuthorName, a.CreatedAtUtc,
                a.ModifiedAtUtc, Shots = a.Screenshots.Count,
            })
            .ToListAsync(ct);
        var ids = rows.Select(r => r.Id).ToList();
        var byArticle = await Lookups.BriefDatesAsync(db, ids, ct);
        var techniques = filter?.MatchesTechniques == true
            ? (await db.ArticleTechniques.AsNoTracking().Where(t => ids.Contains(t.ArticleId)).Select(t => new { t.ArticleId, t.TechniqueId }).ToListAsync(ct))
                .ToLookup(t => t.ArticleId, t => t.TechniqueId)
            : null;
        return rows.Select(r => new ArticleSummary(r.Id, r.Title, r.SourceName, r.Tlp, r.Classification, r.Priority, r.Status,
            r.AuthorName, r.CreatedAtUtc, r.ModifiedAtUtc, r.Shots, byArticle.GetValueOrDefault(r.Id) ?? [],
            techniques is null ? [] : filter!.MatchingTechniques(techniques[r.Id]))).ToList();
    }

    /// <summary>The article with everything on it, or null.</summary>
    public async Task<Article?> GetAsync(Guid id, CancellationToken ct = default)
    {
        RequireRead();
        await using var db = await dbs.CreateAsync(ct);
        return await WithEverything(db.Articles.AsNoTracking()).FirstOrDefaultAsync(a => a.Id == id, ct);
    }

    /// <summary>
    /// The publisher's excerpt of the triage item this article was promoted from, if any: shown beside the bottom line as
    /// a reference to rewrite, never copied into it unasked.
    /// </summary>
    public async Task<string?> SourceExcerptAsync(Guid id, CancellationToken ct = default)
    {
        RequireRead();
        await using var db = await dbs.CreateAsync(ct);
        var source = await db.Articles.AsNoTracking().Where(a => a.Id == id).Select(a => a.SourceUrl).FirstOrDefaultAsync(ct);
        return await ExcerptAsync(db, id, source, ct);
    }

    /// <summary>
    /// The publisher's excerpt on the triage items <paramref name="articleId"/> was promoted from, if any. Several items
    /// can point at one article (attached reports, a group promoted as one): the one at <paramref name="sourceUrl"/> first.
    /// </summary>
    internal static Task<string?> ExcerptAsync(IAppDbContext db, Guid articleId, string? sourceUrl, CancellationToken ct) =>
        ExcerptItems(db, articleId, sourceUrl).Select(i => i.Excerpt).FirstOrDefaultAsync(ct);

    /// <summary>The triage items with an excerpt that <paramref name="articleId"/> was promoted from, the one at
    /// <paramref name="sourceUrl"/> first: the first is the one <see cref="ExcerptAsync"/> gives.</summary>
    internal static IQueryable<FeedItem> ExcerptItems(IAppDbContext db, Guid articleId, string? sourceUrl) =>
        db.FeedItems.AsNoTracking().Where(i => i.ArticleId == articleId && i.Excerpt != null && i.Excerpt != "")
            .OrderByDescending(i => i.Url == sourceUrl);

    /// <summary>The other reports of what an article covers, added from triage, oldest first.</summary>
    public async Task<IReadOnlyList<ArticleLink>> LinksAsync(Guid id, CancellationToken ct = default)
    {
        RequireRead();
        await using var db = await dbs.CreateAsync(ct);
        return (await db.ArticleLinks.AsNoTracking().Where(l => l.ArticleId == id).ToListAsync(ct)).OrderBy(l => l.AddedAtUtc).ToList();
    }

    /// <summary>
    /// Adds a report by hand (the editor's "Add a report link"): an http(s) address, and optionally who published it. It's
    /// listed by its address. The article's own link, or one already listed, is refused.
    /// </summary>
    public async Task<ArticleLink> AddLinkAsync(Guid articleId, string? url, string? sourceName, CancellationToken ct = default)
    {
        RequireWrite();
        var clean = (url ?? "").Trim();
        if (clean.Length == 0) throw new ArgumentException("Give the report's address.");
        if (clean.Length > 2000 || !Uri.TryCreate(clean, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("A report link has to be a web address starting with http:// or https://.");
        await using var db = await dbs.CreateAsync(ct);
        var article = await db.Articles.AsNoTracking().Where(a => a.Id == articleId).Select(a => new { a.SourceUrl }).FirstOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException("That article no longer exists.");
        var canonical = Intake.UrlCanonicalizer.Canonicalize(clean) ?? clean;
        var known = (await db.ArticleLinks.AsNoTracking().Where(l => l.ArticleId == articleId).Select(l => l.Url).ToListAsync(ct))
            .Append(article.SourceUrl).Select(u => Intake.UrlCanonicalizer.Canonicalize(u) ?? u);
        if (known.Contains(canonical)) throw new ArgumentException("That report is already on the article.");
        var link = new ArticleLink
        {
            ArticleId = articleId, Url = clean, Title = clean.Length > 500 ? clean[..499] + "…" : clean,
            SourceName = CleanOrNull(sourceName, 200), AddedAtUtc = DateTimeOffset.UtcNow, AddedByName = user.DisplayName,
        };
        db.ArticleLinks.Add(link);
        await db.SaveChangesAsync(ct);
        return link;
    }

    public async Task RemoveLinkAsync(Guid linkId, CancellationToken ct = default)
    {
        RequireWrite();
        await using var db = await dbs.CreateAsync(ct);
        if (await db.ArticleLinks.FirstOrDefaultAsync(l => l.Id == linkId, ct) is not { } link) return;
        db.ArticleLinks.Remove(link);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The days of the briefs an article is on, oldest first.</summary>
    public async Task<IReadOnlyList<DateOnly>> BriefDatesAsync(Guid id, CancellationToken ct = default)
    {
        RequireRead();
        await using var db = await dbs.CreateAsync(ct);
        return await db.BriefItems.AsNoTracking().Where(i => i.ArticleId == id)
            .Join(db.Briefs, i => i.BriefId, b => b.Id, (i, b) => b.BriefDate)
            .OrderBy(d => d).ToListAsync(ct);
    }

    internal static IQueryable<Article> WithEverything(IQueryable<Article> q) => q
        .Include(a => a.Bullets)
        .Include(a => a.Screenshots).ThenInclude(s => s.File)
        .Include(a => a.Tags).ThenInclude(t => t.Tag).ThenInclude(t => t!.Category)
        .Include(a => a.Techniques)
        .Include(a => a.Indicators)
        .Include(a => a.Cves)
        .AsSplitQuery();

    /// <summary>Starts an article. Returns its id.</summary>
    public async Task<Guid> CreateAsync(string title, string? sourceName = null, string? sourceUrl = null,
        TlpLevel tlp = TlpLevel.Clear, CancellationToken ct = default)
    {
        RequireWrite();
        var cleanTitle = Clean(title, MaxTitle);
        if (cleanTitle.Length == 0) throw new ArgumentException("An article needs a title.");
        var article = new Article
        {
            Title = cleanTitle,
            SourceName = CleanOrNull(sourceName, 200),
            SourceUrl = CheckUrl(sourceUrl),
            Tlp = Tlp.Checked(tlp),
            AuthorId = user.UserId,
            AuthorName = user.DisplayName,
        };
        await using var db = await dbs.CreateAsync(ct);
        db.Articles.Add(article);
        await db.SaveChangesAsync(ct);
        return article.Id;
    }

    /// <summary>
    /// Saves the editor's whole form. Refused (<see cref="StaleEditException"/>) when the article changed since the form
    /// was loaded. Returns the new edit version.
    /// </summary>
    public async Task<Guid> SaveAsync(ArticleForm form, CancellationToken ct = default)
    {
        RequireWrite();
        await using var db = await dbs.CreateAsync(ct);
        var a = await WithEverything(db.Articles).FirstOrDefaultAsync(x => x.Id == form.Id, ct)
                ?? throw new KeyNotFoundException("That article no longer exists.");
        if (a.EditVersion != form.EditVersion)
            throw new StaleEditException(SavedMeanwhile);

        a.Title = Clean(form.Title, MaxTitle);
        if (a.Title.Length == 0) throw new ArgumentException("An article needs a title.");
        a.SourceName = CleanOrNull(form.SourceName, 200);
        a.SourceUrl = CheckUrl(form.SourceUrl);
        a.PublishedAtUtc = form.PublishedAtUtc;
        a.Tlp = Tlp.Checked(form.Tlp);
        if (!Enum.IsDefined(form.Classification) || (form.Priority is { } p && !Enum.IsDefined(p)))
            throw new ArgumentException("Choose a classification and a priority from the lists.");
        a.Classification = form.Classification;
        a.Priority = form.Classification == Classification.Threat ? form.Priority : null;
        a.Bluf = Clean(form.Bluf, MaxBluf);
        a.WhyItMatters = CleanOrNull(form.WhyItMatters, MaxLongText, multiline: true);
        a.WhyItMattersTlp = Tlp.Checked(form.WhyItMattersTlp);
        a.RecommendedActions = CleanOrNull(form.RecommendedActions, MaxLongText, multiline: true);
        // Not Actors: the free-text "Threat actors" box was retired for the adversary picker. What it held is kept as the
        // earlier note (AdversaryService), never written from the form.
        a.Sectors = CleanOrNull(form.Sectors, 500);

        SaveBullets(db, a, form.Bullets);
        SaveScreenshotDetails(a, form.Screenshots);
        await SaveTagsAsync(db, a, form.TagIds, ct);
        SaveTechniques(db, a, form.TechniqueIds);
        SaveIndicators(db, a, form.Indicators);
        SaveCves(db, a, form.Cves);

        // A Ready article that lost something it needs goes back to Draft.
        if (a.Status == ArticleStatus.Ready && ArticleChecks.For(a).Any(c => c.Blocking)) a.Status = ArticleStatus.Draft;

        a.EditVersion = Guid.NewGuid();
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException)
        {
            throw new StaleEditException(SavedMeanwhile);
        }
        return a.EditVersion;
    }

    /// <summary>
    /// Saves only an article's scorecard tags and ATT&amp;CK techniques (present mode's tag panel), through the editor's own
    /// <see cref="SaveAsync"/>: the article as saved, with these two swapped in. So the same rules hold as on the article
    /// page: analysts only, refused (<see cref="StaleEditException"/>) when the article changed since
    /// <paramref name="editVersion"/>, a technique change puts an approval out of date as an editor's would, and the save
    /// is audited and heard like any other. Returns the new edit version.
    /// </summary>
    public async Task<Guid> SaveTagsAsync(Guid id, Guid editVersion, IEnumerable<Guid> tagIds, IEnumerable<string> techniqueIds,
        CancellationToken ct = default)
    {
        RequireWrite();
        var a = await GetAsync(id, ct) ?? throw new KeyNotFoundException("That article no longer exists.");
        var form = ArticleForm.From(a);
        form.EditVersion = editVersion;
        form.TagIds = tagIds.ToHashSet();
        form.TechniqueIds = techniqueIds.ToList();
        return await SaveAsync(form, ct);
    }

    /// <summary>
    /// Marks an article's "Why it matters" TLP:AMBER, from the running order of <paramref name="briefId"/>: the one-click
    /// fix for <see cref="ArticleChecks.WhyReachesCompany"/>, so the company-wide edition leaves the paragraph out. Analysts
    /// only, as any edit; refused (<see cref="StaleEditException"/>) when the article changed since
    /// <paramref name="editVersion"/>, the copy the page showed; refused when the article isn't on that brief, or the brief
    /// has been sent (what went out is changed by a correction). It only ever raises that one marking: where the paragraph
    /// already counts as AMBER or stricter (its own marking, or the article's), nothing is saved and it returns false.
    /// The save is the article's own, so it is audited and heard like any other, and moves the edit version.
    /// </summary>
    public async Task<bool> MarkWhyItMattersAmberAsync(Guid id, Guid editVersion, Guid briefId, CancellationToken ct = default)
    {
        RequireWrite();
        await using var db = await dbs.CreateAsync(ct);
        var a = await db.Articles.FirstOrDefaultAsync(x => x.Id == id, ct)
                ?? throw new KeyNotFoundException("That article no longer exists.");
        if (a.EditVersion != editVersion) throw new StaleEditException(SavedMeanwhile);
        var brief = await db.Briefs.AsNoTracking().Where(b => b.Id == briefId && b.Items.Any(i => i.ArticleId == id))
            .Select(b => (BriefStatus?)b.Status).FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("That article is no longer on this brief.");
        if (brief == BriefStatus.Sent)
            throw new InvalidOperationException("This brief has been sent. Send a correction to change what went out.");
        if (a.Effective(a.WhyItMattersTlp) >= TlpLevel.Amber) return false;

        a.WhyItMattersTlp = TlpLevel.Amber;
        a.EditVersion = Guid.NewGuid();
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException)
        {
            throw new StaleEditException(SavedMeanwhile);
        }
        return true;
    }

    private const string SavedMeanwhile = "Someone else saved this article while you were editing. Reload it to see their changes.";

    /// <summary>
    /// Marks an article Ready (refused while a blocking check fails) or back to Draft. Returns its version before and after,
    /// so an editor takes on the new one only if the version it holds was the one changed: otherwise a colleague's save
    /// came in between, and the editor's text isn't theirs.
    /// </summary>
    public async Task<(Guid Was, Guid Now)> SetStatusAsync(Guid id, ArticleStatus status, CancellationToken ct = default)
    {
        RequireWrite();
        if (!Enum.IsDefined(status)) throw new ArgumentException("That isn't an article status.");
        await using var db = await dbs.CreateAsync(ct);
        var a = await WithEverything(db.Articles).FirstOrDefaultAsync(x => x.Id == id, ct)
                ?? throw new KeyNotFoundException("That article no longer exists.");
        if (status == ArticleStatus.Ready && ArticleChecks.For(a).FirstOrDefault(c => c.Blocking) is { } gap)
            throw new InvalidOperationException("It isn't ready yet: " + gap.Message);
        var was = a.EditVersion;
        a.Status = status;
        a.EditVersion = Guid.NewGuid();
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException)
        {
            throw new StaleEditException("Someone else saved this article just now. Reload it and try again.");
        }
        return (was, a.EditVersion);
    }

    /// <summary>Deletes a draft that's on no brief. Anything that was briefed is kept for the record.</summary>
    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        RequireWrite();
        await using var db = await dbs.CreateAsync(ct);
        var a = await db.Articles.Include(x => x.Screenshots).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (a is null) return;
        if (a.Status != ArticleStatus.Draft || await db.BriefItems.AnyAsync(i => i.ArticleId == id, ct))
            throw new InvalidOperationException("Only a draft that isn't on a brief can be deleted.");
        // A deleted action counts: it's kept (an administrator may restore it), and its foreign key holds the article.
        if (await db.Actions.IgnoreQueryFilters([ActionItem.DeletedFilter]).AnyAsync(x => x.ArticleId == id, ct))
            throw new InvalidOperationException("Actions were raised from this article, so it's kept for their record.");
        var fileIds = a.Screenshots.Select(s => s.FileId).ToList();
        // A triage item promoted into this draft goes back to the inbox, rather than staying "promoted" with no article.
        foreach (var item in await db.FeedItems.Where(i => i.ArticleId == id).ToListAsync(ct))
        {
            item.Status = FeedItemStatus.New;
            item.ArticleId = null;
            item.StatusByName = null;
            item.StatusAtUtc = null;
        }
        db.Articles.Remove(a);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex is not DbUpdateConcurrencyException)
        {
            // A brief took it, or an action was raised from it, between the checks above and the delete: their foreign
            // keys refused the delete, so nothing was removed (the brief keeps its article, the audit trail its record).
            await using var now = await dbs.CreateAsync(ct);
            if (await now.BriefItems.AnyAsync(i => i.ArticleId == id, ct))
                throw new InvalidOperationException("Only a draft that isn't on a brief can be deleted.", ex);
            if (await now.Actions.IgnoreQueryFilters([ActionItem.DeletedFilter]).AnyAsync(x => x.ArticleId == id, ct))
                throw new InvalidOperationException("Actions were raised from this article, so it's kept for their record.", ex);
            throw;
        }
        await screenshots.DeleteFilesAsync(fileIds, ct);
    }

    /// <summary>Indicators and CVEs found in <paramref name="text"/> that the article doesn't already carry.</summary>
    public static IocExtractor.Found Suggest(string? text, ArticleForm form) => Suggest(text, form.Indicators, form.Cves);

    /// <summary>Indicators and CVEs found in <paramref name="text"/> that aren't among <paramref name="have"/> and
    /// <paramref name="haveCves"/> (an article's, or an action's on its Hunt queries card).</summary>
    public static IocExtractor.Found Suggest(string? text, IEnumerable<IndicatorForm> have, IEnumerable<string> haveCves)
    {
        var found = IocExtractor.Extract(text);
        var had = have.Select(i => $"{(int)i.Type}|{i.Value}").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hadCves = haveCves.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new IocExtractor.Found(
            found.Indicators.Where(i => !had.Contains($"{(int)i.Type}|{i.Value}")).ToList(),
            found.Cves.Where(c => !hadCves.Contains(c)).ToList());
    }

    /// <summary>
    /// Adds indicators to an article without touching the rest of it, in the caller's save (from an action's Hunt queries
    /// card, whose history line goes with it): normalised and folded as a save does, those it already carries skipped, and
    /// refused past <see cref="IocExtractor.MaxIndicators"/>. The article's edit version moves, so an editor open on the
    /// older copy can't save its own indicators over these. Returns how many were new. It takes writing articles;
    /// <paramref name="refusal"/> is what someone who can't is told. <paramref name="a"/> is tracked by <paramref name="db"/>,
    /// with its indicators.
    /// </summary>
    public int AddIndicators(IAppDbContext db, Article a, IEnumerable<IndicatorForm> indicators, string refusal = WriteRefusal)
    {
        if (!user.Has(Permission.WriteArticles)) throw new ForbiddenException(refusal);
        var added = NewIndicators(indicators, a.Indicators.Select(i => (i.Type, i.Value)), "An article");
        if (added.Count == 0) return 0;
        foreach (var f in added)
            db.ArticleIndicators.Add(new ArticleIndicator { ArticleId = a.Id, Type = f.Type, Value = f.Value, Note = f.Note });
        a.EditVersion = Guid.NewGuid();
        return added.Count;
    }

    /// <summary>
    /// The rule for adding indicators to what already carries <paramref name="have"/> (an article, or an action with no
    /// article): <paramref name="indicators"/> normalised and folded as a save does (<see cref="WantedIndicators"/>), those
    /// already there skipped, and refused past <see cref="IocExtractor.MaxIndicators"/> in all, the refusal naming
    /// <paramref name="carrier"/> ("An article"). The new ones; none is never refused.
    /// </summary>
    internal static List<IndicatorForm> NewIndicators(IEnumerable<IndicatorForm> indicators, IEnumerable<(IndicatorType Type, string Value)> have, string carrier)
    {
        var wanted = WantedIndicators(indicators);
        var had = 0;
        foreach (var (type, value) in have)
        {
            wanted.Remove($"{(int)type}|{value}");
            had++;
        }
        if (wanted.Count == 0) return [];
        if (had + wanted.Count > IocExtractor.MaxIndicators)
            throw new ArgumentException($"{carrier} can carry at most {IocExtractor.MaxIndicators} indicators.");
        return [.. wanted.Values];
    }

    /// <summary>The bullets a save keeps, in order: trimmed, with the blank ones dropped.</summary>
    internal static List<(string Text, TlpLevel Tlp)> KeptBullets(IEnumerable<BulletForm> bullets) =>
        bullets.Select(b => (Text: (b.Text ?? "").Trim(), Tlp: Tlp.Checked(b.Tlp))).Where(b => b.Text.Length > 0).ToList();

    private static void SaveBullets(IAppDbContext db, Article a, List<BulletForm> bullets)
    {
        var kept = KeptBullets(bullets);
        if (kept.Count > Article.MaxBullets) throw new ArgumentException($"An article can have at most {Article.MaxBullets} bullets.");
        if (kept.Any(b => b.Text.Length > MaxBullet)) throw new ArgumentException($"A bullet can be at most {MaxBullet} characters.");
        var existing = a.Bullets.OrderBy(b => b.SortOrder).ToList();
        // Update in place where a bullet is still there, so an unchanged bullet isn't rewritten.
        for (var i = 0; i < kept.Count; i++)
        {
            if (i < existing.Count)
            {
                if (existing[i].Text != kept[i].Text) existing[i].Text = kept[i].Text;
                if (existing[i].Tlp != kept[i].Tlp) existing[i].Tlp = kept[i].Tlp;
                existing[i].SortOrder = i;
            }
            else
            {
                var b = new ArticleBullet { ArticleId = a.Id, SortOrder = i, Text = kept[i].Text, Tlp = kept[i].Tlp };
                a.Bullets.Add(b);
                db.ArticleBullets.Add(b);
            }
        }
        foreach (var gone in existing.Skip(kept.Count))
        {
            a.Bullets.Remove(gone);
            db.ArticleBullets.Remove(gone);
        }
    }

    private static void SaveScreenshotDetails(Article a, List<ScreenshotForm> shots)
    {
        var order = shots.Select((s, i) => (s.Id, i)).ToDictionary(x => x.Id, x => x.i);
        foreach (var s in a.Screenshots)
        {
            if (shots.FirstOrDefault(f => f.Id == s.Id) is not { } f) continue;
            s.Caption = CleanOrNull(f.Caption, 500);
            s.Tlp = Tlp.Checked(f.Tlp);
            s.SortOrder = order[s.Id];
        }
    }

    private static async Task SaveTagsAsync(IAppDbContext db, Article a, HashSet<Guid> tagIds, CancellationToken ct)
    {
        var wanted = tagIds.ToHashSet();
        var current = a.Tags.Select(t => t.TagId).ToHashSet();
        // A retired tag already on the article stays; a new one must be live.
        var adding = wanted.Except(current).ToList();
        if (adding.Count > 0)
        {
            var live = await db.ScorecardTags.Where(t => adding.Contains(t.Id) && !t.IsRetired && !t.Category!.IsRetired)
                .Select(t => t.Id).ToListAsync(ct);
            if (live.Count != adding.Count) throw new ArgumentException("A chosen scorecard tag has been retired. Pick another.");
        }
        foreach (var gone in a.Tags.Where(t => !wanted.Contains(t.TagId)).ToList())
        {
            a.Tags.Remove(gone);
            db.ArticleTags.Remove(gone);
        }
        foreach (var id in adding) db.ArticleTags.Add(new ArticleTag { ArticleId = a.Id, TagId = id });
    }

    private static void SaveTechniques(IAppDbContext db, Article a, List<string> techniqueIds)
    {
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in techniqueIds)
        {
            // One the article already carries is kept even if an ATT&CK upgrade has since retired it, so the article can
            // still be saved; the editor marks it. Only a newly added id has to be in the catalogue.
            if (AttackCatalog.Find(raw) is null && a.Techniques.FirstOrDefault(t => string.Equals(t.TechniqueId, raw.Trim(), StringComparison.OrdinalIgnoreCase)) is { } kept)
            {
                wanted.Add(kept.TechniqueId);
                continue;
            }
            var t = AttackCatalog.Find(raw) ?? throw new ArgumentException(AttackCatalog.Retired(raw) is { } gone
                ? AttackCatalog.RetiredText(gone) + (gone.ReplacedBy is null ? "" : " Add that one instead.")
                : $"'{raw}' isn't an ATT&CK technique in the catalog.");
            wanted.Add(t.Id);
        }
        foreach (var gone in a.Techniques.Where(t => !wanted.Contains(t.TechniqueId)).ToList())
        {
            a.Techniques.Remove(gone);
            db.ArticleTechniques.Remove(gone);
        }
        foreach (var id in wanted.Where(w => !a.Techniques.Any(t => string.Equals(t.TechniqueId, w, StringComparison.OrdinalIgnoreCase))))
            db.ArticleTechniques.Add(new ArticleTechnique { ArticleId = a.Id, TechniqueId = id });
    }

    /// <summary>
    /// The indicators a save keeps, by type and value: each normalised (refanged, canonical), the blank ones dropped and
    /// a repeat (in any case) folded into the first. <paramref name="strict"/> refuses an over-long value or note, as a
    /// save does; otherwise they're kept as they are, for a preview.
    /// </summary>
    internal static Dictionary<string, IndicatorForm> WantedIndicators(IEnumerable<IndicatorForm> indicators, bool strict = true)
    {
        var wanted = new Dictionary<string, IndicatorForm>(StringComparer.OrdinalIgnoreCase);
        foreach (var i in indicators)
        {
            var value = IocObservable.Normalize(i.Type, i.Value);
            if (value.Length == 0) continue;
            if (strict && value.Length > 2000) throw new ArgumentException("An indicator can be at most 2,000 characters.");
            wanted.TryAdd($"{(int)i.Type}|{value}", new IndicatorForm
            {
                Type = i.Type, Value = value, Note = strict ? CleanOrNull(i.Note, 300) : TidyOrNull(i.Note),
            });
        }
        return wanted;
    }

    private static void SaveIndicators(IAppDbContext db, Article a, List<IndicatorForm> indicators)
    {
        var wanted = WantedIndicators(indicators);
        if (wanted.Count > IocExtractor.MaxIndicators)
            throw new ArgumentException($"An article can carry at most {IocExtractor.MaxIndicators} indicators.");
        foreach (var existing in a.Indicators.ToList())
        {
            if (wanted.Remove($"{(int)existing.Type}|{existing.Value}", out var f))
            {
                if (existing.Note != f.Note) existing.Note = f.Note;
                // The match ignores case; the row takes the canonical spelling, so one stored any other way (before the
                // spelling was fixed, or by hand) heals on its next save and the hunts and exports built from it agree.
                if (existing.Value != f.Value) existing.Value = f.Value;
                continue;
            }
            a.Indicators.Remove(existing);
            db.ArticleIndicators.Remove(existing);
        }
        foreach (var f in wanted.Values)
            db.ArticleIndicators.Add(new ArticleIndicator { ArticleId = a.Id, Type = f.Type, Value = f.Value, Note = f.Note });
    }

    private static void SaveCves(IAppDbContext db, Article a, List<string> cves)
    {
        var wanted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in cves)
            wanted.Add(IocExtractor.NormalizeCve(raw) ?? throw new ArgumentException($"'{raw}' isn't a CVE id (CVE-YYYY-NNNN)."));
        foreach (var gone in a.Cves.Where(c => !wanted.Contains(c.CveId)).ToList())
        {
            a.Cves.Remove(gone);
            db.ArticleCves.Remove(gone);
        }
        foreach (var id in wanted.Where(w => a.Cves.All(c => c.CveId != w)))
            db.ArticleCves.Add(new ArticleCve { ArticleId = a.Id, CveId = id });
    }

    /// <summary>A citation link: http or https only, or nothing.</summary>
    internal static string? CheckUrl(string? url, string what = "The source link")
    {
        var u = (url ?? "").Trim();
        if (u.Length == 0) return null;
        return UrlProblem(u, what) is { } problem ? throw new ArgumentException(problem) : u;
    }

    /// <summary>The link <see cref="CheckUrl"/> would keep, or null where it would refuse it: for a preview.</summary>
    internal static string? SafeUrl(string? url)
    {
        var u = (url ?? "").Trim();
        return u.Length == 0 || UrlProblem(u, "") is not null ? null : u;
    }

    private static string? UrlProblem(string u, string what)
    {
        if (u.Length > 2000) return "The link is too long.";
        if (!Uri.TryCreate(u, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https"))
            return $"{what} must be a web address (http or https).";
        return null;
    }

    internal static string Clean(string? text, int max, bool multiline = false)
    {
        var t = Tidy(text, multiline);
        if (t.Length > max) throw new ArgumentException($"That's longer than the {max:N0} characters allowed.");
        return t;
    }

    internal static string? CleanOrNull(string? text, int max, bool multiline = false) =>
        Clean(text, max, multiline) is { Length: > 0 } t ? t : null;

    /// <summary>What <see cref="Clean"/> stores, without its length limit: trimmed, and on one line unless
    /// <paramref name="multiline"/>.</summary>
    internal static string Tidy(string? text, bool multiline = false)
    {
        var t = (text ?? "").Trim();
        return multiline ? t : Content.MarkdownService.OneLine(t);
    }

    internal static string? TidyOrNull(string? text, bool multiline = false) => Tidy(text, multiline) is { Length: > 0 } t ? t : null;

    private void RequireRead()
    {
        if (!user.Has(Permission.ReadTeamEdition)) throw new ForbiddenException("You don't have access to articles.");
    }

    private const string WriteRefusal = "Only analysts can write articles.";

    private void RequireWrite()
    {
        if (!user.Has(Permission.WriteArticles)) throw new ForbiddenException(WriteRefusal);
    }
}
