using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Tide.Application.Abstractions;
using Tide.Application.Articles;
using Tide.Application.Content;
using Tide.Application.Mitre;
using Tide.Application.Search;
using Tide.Application.Security;
using Tide.Application.Stack;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.Application.Adversaries;

/// <summary>What the profile form edits.</summary>
public sealed class AdversaryForm
{
    public Guid? Id { get; set; }
    public string Name { get; set; } = "";
    public AdversaryKind Kind { get; set; }
    public string? Aliases { get; set; }
    public string? MitreId { get; set; }
    public string? Motivation { get; set; }
    public string? Sectors { get; set; }
    public string? Notes { get; set; }
    public bool Watched { get; set; }

    public static AdversaryForm From(Adversary a) => new()
    {
        Id = a.Id, Name = a.Name, Kind = a.Kind, Aliases = a.Aliases, MitreId = a.MitreId, Motivation = a.Motivation, Sectors = a.Sectors,
        Notes = a.Notes, Watched = a.Watched,
    };
}

/// <summary>A profile on the list: how many articles carry it (tagged or naming it), and when it was last reported.</summary>
public sealed record AdversaryRow(Adversary Adversary, int Articles, DateOnly? LastReported);

/// <summary>An article in a profile's timeline: tagged with it, or found because it names it.</summary>
public sealed record AdversaryArticle(Guid Id, string Title, DateOnly On, ArticleStatus Status, bool Tagged, Priority? Priority);

/// <summary>A technique seen across a profile's articles, and in how many.</summary>
public sealed record AdversaryTechnique(string Id, string Name, int Articles);

/// <summary>An action raised from one of a profile's articles.</summary>
public sealed record AdversaryAction(int Number, string Title, Priority Priority, ActionStatus Status, string TeamName, DateOnly? DueDate);

/// <summary>A profile with its timeline, newest first, the techniques and indicators across it, and how many briefs this quarter.</summary>
public sealed record AdversaryDetail(Adversary Adversary, string? MergedIntoName, IReadOnlyList<AdversaryArticle> Articles, IReadOnlyList<AdversaryAction> Actions,
    IReadOnlyList<AdversaryTechnique> Techniques, int Indicators, int BriefedThisQuarter)
{
    /// <summary>The articles that name it but aren't tagged: for an analyst to link (never linked automatically).</summary>
    public IReadOnlyList<AdversaryArticle> Untagged => Articles.Where(a => !a.Tagged).ToList();
}

/// <summary>An article's profiles: those it's tagged with, and those it names but isn't tagged with (a suggestion).</summary>
public sealed record ArticleAdversaries(IReadOnlyList<Adversary> Tagged, IReadOnlyList<Adversary> Suggested);

/// <summary>Why the article editor's adversary picker lists a profile.</summary>
public enum AdversaryPickReason
{
    /// <summary>The article's text names it (with nothing typed).</summary>
    Named,
    /// <summary>It's watched (with nothing typed).</summary>
    Watched,
    /// <summary>It was tagged on an article lately (with nothing typed).</summary>
    Recent,
    /// <summary>One of its names, or its MITRE id, holds what was typed.</summary>
    Match,
}

/// <summary>
/// A profile the adversary picker lists: <see cref="Alias"/> is the other name that matched what was typed, when its own
/// name didn't ("APT29 (also Cozy Bear)"); a retired one is listed last and asked about before it's tagged.
/// </summary>
public sealed record AdversaryPick(Guid Id, string Name, string? MitreId, bool IsRetired, string? Alias, AdversaryPickReason Reason);

/// <summary>
/// What the picker lists for the words typed. <see cref="Exact"/>: a live profile has exactly that name, alias or MITRE
/// id, so there's no "Add it as a new adversary"; <see cref="AlreadyTagged"/> names the one the article already has, when
/// that's the profile.
/// </summary>
public sealed record AdversaryPicks(IReadOnlyList<AdversaryPick> Found, bool Exact, string? AlreadyTagged)
{
    public static readonly AdversaryPicks None = new([], false, null);
}

/// <summary>What importing MITRE ATT&amp;CK's groups and campaigns did.</summary>
public sealed record MitreImport(int Added, int Updated, int Skipped);

/// <summary>
/// Threat actor and campaign profiles (EP-73), built as Our stack is. Each profile has the names it goes by; an article
/// can be tagged with any number of them, and a profile's page shows its timeline: the articles tagged with it or naming
/// it (title, actors or the start of the bottom line, as whole words, ignoring case), their actions, the ATT&amp;CK
/// techniques across them (exported as a Navigator layer), and their indicators. Analysts keep the profiles; MITRE's
/// groups and campaigns can be imported for their aliases. A profile merged into another moves its tags and names there.
/// </summary>
public sealed class AdversaryService(IAppDbContextFactory dbs, ICurrentUser user, Org.OrgClock clock, Intake.IOutboundHttp http)
{
    public const int MaxAliases = 40;

    /// <summary>MITRE's TAXII 2.1 server: Enterprise ATT&amp;CK's groups and campaigns only (under a megabyte).</summary>
    public const string MitreTaxiiUrl =
        "https://attack-taxii.mitre.org/api/v21/collections/x-mitre-collection--1f5f1533-f617-4ca8-9ab4-6a02367fa019/objects/?match%5Btype%5D=intrusion-set,campaign";

    /// <summary>The largest file of groups an analyst may upload instead (a site that can't reach MITRE).</summary>
    public const int MaxUploadBytes = 8 * 1024 * 1024;

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static StackEntry Entry(Adversary a) => new(a.Id, a.Name, [a.Name, .. StackService.SplitNames(a.Aliases)]);

    /// <summary>The live profiles that are watched, as entries to match triage items against (no permission check).</summary>
    internal static async Task<IReadOnlyList<StackEntry>> WatchedAsync(IAppDbContext db, CancellationToken ct) =>
        (await db.Adversaries.AsNoTracking().Where(a => a.Watched && !a.IsRetired).OrderByName(a => a.Name).ToListAsync(ct)).Select(Entry).ToList();

    // --- reading ------------------------------------------------------------------------------------------------

    /// <summary>The live profiles, without matching them against the articles (the editor's picker).</summary>
    public async Task<IReadOnlyList<Adversary>> ProfilesAsync(CancellationToken ct = default)
    {
        RequireRead();
        await using var db = await dbs.CreateAsync(ct);
        return await db.Adversaries.AsNoTracking().Where(a => !a.IsRetired).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<AdversaryRow>> ListAsync(bool includeRetired = false, CancellationToken ct = default)
    {
        RequireRead();
        await using var db = await dbs.CreateAsync(ct);
        var profiles = await db.Adversaries.AsNoTracking().Where(a => includeRetired || !a.IsRetired).ToListAsync(ct);
        var articles = await ArticlesAsync(db, ct);
        var tags = await db.ArticleAdversaries.AsNoTracking().Select(t => new { t.ArticleId, t.AdversaryId }).ToListAsync(ct);
        return profiles.Select(p =>
            {
                var mine = Of(p, articles, tags.Where(t => t.AdversaryId == p.Id).Select(t => t.ArticleId).ToHashSet()).ToList();
                return new AdversaryRow(p, mine.Count, mine.Count == 0 ? null : mine.Max(a => a.On));
            })
            .OrderBy(r => r.Adversary.IsRetired).ThenByDescending(r => r.Adversary.Watched).ThenByDescending(r => r.LastReported).ThenByName(r => r.Adversary.Name)
            .ToList();
    }

    public async Task<AdversaryDetail?> GetAsync(Guid id, CancellationToken ct = default)
    {
        RequireRead();
        await using var db = await dbs.CreateAsync(ct);
        var p = await db.Adversaries.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return null;
        var merged = p.MergedIntoId is { } into ? await db.Adversaries.Where(x => x.Id == into).Select(x => x.Name).FirstOrDefaultAsync(ct) : null;
        var tagged = (await db.ArticleAdversaries.AsNoTracking().Where(t => t.AdversaryId == id).Select(t => t.ArticleId).ToListAsync(ct)).ToHashSet();
        var articles = Of(p, await ArticlesAsync(db, ct), tagged)
            .Select(a => new AdversaryArticle(a.Id, a.Title, a.On, a.Status, tagged.Contains(a.Id), a.Priority)).OrderByDescending(a => a.On).ToList();
        var ids = articles.Select(a => a.Id).ToList();
        var actions = await db.Actions.AsNoTracking().Include(a => a.Team).Where(a => a.ArticleId != null && ids.Contains(a.ArticleId.Value)).ToListAsync(ct);
        var techniques = (await db.ArticleTechniques.AsNoTracking().Where(t => ids.Contains(t.ArticleId)).ToListAsync(ct))
            .GroupBy(t => t.TechniqueId)
            .Select(g => new AdversaryTechnique(g.Key, AttackCatalog.Find(g.Key)?.Name ?? g.Key, g.Select(t => t.ArticleId).Distinct().Count()))
            .OrderByDescending(t => t.Articles).ThenBy(t => t.Id, StringComparer.Ordinal).ToList();
        var indicators = await db.ArticleIndicators.AsNoTracking().Where(i => ids.Contains(i.ArticleId)).Select(i => new { i.Type, i.Value }).Distinct().CountAsync(ct);
        var quarterStart = new DateOnly(clock.Today.Year, (clock.Today.Month - 1) / 3 * 3 + 1, 1);
        var briefed = await db.BriefItems.AsNoTracking().Where(i => ids.Contains(i.ArticleId))
            .Join(db.Briefs, i => i.BriefId, b => b.Id, (i, b) => new { b.BriefDate, b.Status }).Where(x => x.BriefDate >= quarterStart && x.Status >= BriefStatus.Presented)
            .Select(x => x.BriefDate).Distinct().CountAsync(ct);
        return new AdversaryDetail(p, merged, articles,
            actions.OrderBy(a => ActionItem.IsClosedStatus(a.Status)).ThenBy(a => a.DueDate ?? DateOnly.MaxValue).ThenBy(a => a.Number)
                .Select(a => new AdversaryAction(a.Number, a.Title, a.Priority, a.Status, a.Team?.Name ?? "", a.DueDate)).ToList(),
            techniques, indicators, briefed);
    }

    /// <summary>An article's profiles, and those it names (title, actors, bottom line) but isn't tagged with.</summary>
    public async Task<ArticleAdversaries> ForArticleAsync(Guid articleId, CancellationToken ct = default)
    {
        RequireRead();
        await using var db = await dbs.CreateAsync(ct);
        return await CardAsync(db, articleId, ct) is { } card ? Suggest(card) : new ArticleAdversaries([], []);
    }

    /// <summary>The most profiles the article editor's picker lists at once.</summary>
    public const int PickLimit = 8;

    // How many profiles a typed search reads from the database before ordering them: the best matches by the database's
    // own reckoning (exact, then a name starting with it), so however many profiles there are, only these are read.
    private const int PickCandidates = 50;

    /// <summary>
    /// What the adversary picker offers before anything is typed, at most <see cref="PickLimit"/>: the profiles the
    /// article's text names, then the watched ones, then those tagged on any article most recently (by the audit trail);
    /// never one the article already has, nor a retired one.
    /// </summary>
    public async Task<IReadOnlyList<AdversaryPick>> OfferedAsync(Guid articleId, CancellationToken ct = default)
    {
        RequireRead();
        await using var db = await dbs.CreateAsync(ct);
        return await CardAsync(db, articleId, ct) is { } card ? await OfferAsync(db, card, ct) : [];
    }

    /// <summary>
    /// The article editor's Adversaries card in one read: <see cref="ForArticleAsync"/>, and (when <paramref name="offer"/>,
    /// for an analyst's picker) <see cref="OfferedAsync"/>, from the one load of the article, its tags and the live profiles.
    /// </summary>
    public async Task<(ArticleAdversaries Mine, IReadOnlyList<AdversaryPick> Offered)> ForArticleCardAsync(Guid articleId, bool offer,
        CancellationToken ct = default)
    {
        RequireRead();
        await using var db = await dbs.CreateAsync(ct);
        if (await CardAsync(db, articleId, ct) is not { } card) return (new ArticleAdversaries([], []), []);
        return (Suggest(card), offer ? await OfferAsync(db, card, ct) : []);
    }

    // What the card is drawn from: the profiles the article is tagged with (by name), the live ones, and the article's
    // words (title, actors, bottom line) that name them. None when the article has gone.
    private sealed record ArticleCard(List<Adversary> Tagged, List<Adversary> Live, string Words);

    private static async Task<ArticleCard?> CardAsync(IAppDbContext db, Guid articleId, CancellationToken ct)
    {
        var article = await db.Articles.AsNoTracking().Where(a => a.Id == articleId).Select(a => new { a.Title, a.Actors, a.Bluf }).FirstOrDefaultAsync(ct);
        if (article is null) return null;
        var tagged = await db.ArticleAdversaries.AsNoTracking().Where(t => t.ArticleId == articleId).Select(t => t.Adversary!).OrderByName(a => a.Name).ToListAsync(ct);
        var live = await db.Adversaries.AsNoTracking().Where(a => !a.IsRetired).ToListAsync(ct);
        return new ArticleCard(tagged, live, StackService.Words(article.Title + " " + article.Actors, article.Bluf));
    }

    private static ArticleAdversaries Suggest(ArticleCard card) => new(card.Tagged,
        card.Live.Where(a => card.Tagged.TrueForAll(t => t.Id != a.Id) && StackService.Names(Entry(a), card.Words)).OrderByName(a => a.Name).ToList());

    private static async Task<IReadOnlyList<AdversaryPick>> OfferAsync(IAppDbContext db, ArticleCard card, CancellationToken ct)
    {
        var tagged = card.Tagged.Select(a => a.Id).ToHashSet();
        var picks = new List<AdversaryPick>();
        void Offer(IEnumerable<Adversary> profiles, AdversaryPickReason why)
        {
            foreach (var a in profiles)
                if (picks.Count < PickLimit && !tagged.Contains(a.Id) && picks.TrueForAll(p => p.Id != a.Id))
                    picks.Add(new AdversaryPick(a.Id, a.Name, a.MitreId, false, null, why));
        }
        var live = card.Live;
        Offer(live.Where(a => StackService.Names(Entry(a), card.Words)).OrderByName(a => a.Name), AdversaryPickReason.Named);
        Offer(live.Where(a => a.Watched).OrderByName(a => a.Name), AdversaryPickReason.Watched);
        if (picks.Count < PickLimit)
        {
            // An article's tag is keyed "article/profile" in the trail; the newest few, each profile once.
            var keys = await db.AuditLog.AsNoTracking()
                .Where(e => e.EntityType == nameof(ArticleAdversary) && e.Action == nameof(EntityState.Added))
                .OrderByDescending(e => e.AtUtc).ThenBy(e => e.Id).Select(e => e.EntityKey).Take(PickCandidates).ToListAsync(ct);
            var byId = live.ToDictionary(a => a.Id);
            var recent = keys.Select(k => Guid.TryParse(k[(k.IndexOf('/') + 1)..], out var id) ? id : Guid.Empty)
                .Where(byId.ContainsKey).Distinct().Select(id => byId[id]);
            Offer(recent, AdversaryPickReason.Recent);
        }
        return picks;
    }

    /// <summary>
    /// The profiles the adversary picker lists for what's typed, at most <see cref="PickLimit"/>: any whose name, other
    /// names or MITRE id (G0016) holds it, ignoring case; exact matches first, then those starting with it, then the rest,
    /// retired ones last. The search runs in the database and reads a bounded few, never every profile. Those the article
    /// is already tagged with aren't listed.
    /// </summary>
    public async Task<AdversaryPicks> FindForArticleAsync(Guid articleId, string? words, CancellationToken ct = default)
    {
        RequireRead();
        var text = (words ?? "").Trim();
        if (text.Length > Adversary.MaxName) text = text[..Adversary.MaxName];
        if (text.Length == 0) return AdversaryPicks.None;
        await using var db = await dbs.CreateAsync(ct);
        var taggedProfiles = await db.ArticleAdversaries.AsNoTracking().Where(t => t.ArticleId == articleId).Select(t => t.Adversary!).ToListAsync(ct);
        var tagged = taggedProfiles.Select(a => a.Id).ToList();
        var (any, exact) = (SearchText.Contains(text), SearchText.Literal(text));
        var starts = exact + "%";
        // Other names are kept "one; two; three", so an alias is the whole field, or sits between "; " and ";".
        var (aliasFirst, aliasLast, aliasMiddle, aliasStarts) = (exact + ";%", "%; " + exact, "%; " + exact + ";%", "%; " + starts);
        var candidates = await db.Adversaries.AsNoTracking()
            .Where(a => !tagged.Contains(a.Id))
            .Where(a => EF.Functions.Like(a.Name, any, SearchText.Escape) || EF.Functions.Like(a.Aliases!, any, SearchText.Escape)
                || EF.Functions.Like(a.MitreId!, any, SearchText.Escape))
            .OrderBy(a => a.IsRetired)
            .ThenBy(a => EF.Functions.Like(a.Name, exact, SearchText.Escape) || EF.Functions.Like(a.MitreId!, exact, SearchText.Escape)
                || EF.Functions.Like(a.Aliases!, exact, SearchText.Escape) || EF.Functions.Like(a.Aliases!, aliasFirst, SearchText.Escape)
                || EF.Functions.Like(a.Aliases!, aliasLast, SearchText.Escape) || EF.Functions.Like(a.Aliases!, aliasMiddle, SearchText.Escape) ? 0
                : EF.Functions.Like(a.Name, starts, SearchText.Escape) || EF.Functions.Like(a.Aliases!, starts, SearchText.Escape)
                    || EF.Functions.Like(a.Aliases!, aliasStarts, SearchText.Escape) || EF.Functions.Like(a.MitreId!, starts, SearchText.Escape) ? 1 : 2)
            .ThenBy(a => a.Name)
            .Take(PickCandidates).ToListAsync(ct);
        var matched = candidates.Select(a => (Profile: a, Match: Matching(a, text))).Where(x => x.Match.Rank < NoMatch).ToList();
        var found = matched.OrderBy(x => x.Profile.IsRetired).ThenBy(x => x.Match.Rank).ThenByName(x => x.Profile.Name).Take(PickLimit)
            .Select(x => new AdversaryPick(x.Profile.Id, x.Profile.Name, x.Profile.MitreId, x.Profile.IsRetired, x.Match.Alias, AdversaryPickReason.Match))
            .ToList();
        var already = taggedProfiles.Find(a => !a.IsRetired && Matching(a, text).Rank == ExactMatch)?.Name;
        var isExact = already is not null || matched.Exists(x => !x.Profile.IsRetired && x.Match.Rank == ExactMatch);
        return new AdversaryPicks(found, isExact, already);
    }

    private const int ExactMatch = 0, NoMatch = 3;

    // How well a profile matches what was typed (exact, starts with it, holds it, or not at all), and the other name that
    // did when its own name matched less well. The name wins a tie, then the MITRE id, then the first alias.
    internal static (int Rank, string? Alias) Matching(Adversary a, string text)
    {
        int Rank(string term) => string.Equals(term, text, StringComparison.OrdinalIgnoreCase) ? ExactMatch
            : term.StartsWith(text, StringComparison.OrdinalIgnoreCase) ? 1
            : term.Contains(text, StringComparison.OrdinalIgnoreCase) ? 2 : NoMatch;
        var best = (Rank: Rank(a.Name), Alias: (string?)null);
        if (a.MitreId is { } mitre && Rank(mitre) < best.Rank) best = (Rank(mitre), null);
        foreach (var alias in StackService.SplitNames(a.Aliases))
            if (Rank(alias) < best.Rank) best = (Rank(alias), alias);
        return best;
    }

    /// <summary>
    /// The profile's techniques as an ATT&amp;CK Navigator layer (JSON), scored by how many of its articles show each, for
    /// opening in the Navigator or comparing with detection coverage. Coverage is only in an analyst's layer, as the rules are.
    /// </summary>
    public async Task<string?> NavigatorLayerAsync(Guid id, CancellationToken ct = default)
    {
        if (await GetAsync(id, ct) is not { } d) return null;
        var max = Math.Max(1, d.Techniques.Count == 0 ? 1 : d.Techniques.Max(t => t.Articles));
        // With rules imported (EP-79), each technique is coloured by whether a current, enabled rule is tagged with it; for
        // analysts only, as Coverage and the profile page are.
        IReadOnlyDictionary<string, Detections.TechniqueRules>? rules;
        await using (var db = await dbs.CreateAsync(ct))
            rules = await Detections.DetectionService.ForTechniquesAsync(db, user, d.Techniques.Select(t => t.Id), clock.Now, ct);
        var layer = new
        {
            name = d.Adversary.Name + " (TIDE)",
            versions = new { attack = AttackCatalog.Version.TrimStart('v').Split('.')[0], navigator = "5.1.0", layer = "4.5" },
            domain = "enterprise-attack",
            description = $"Techniques in TIDE's articles about {d.Adversary.Name}, scored by how many articles show each.",
            techniques = d.Techniques.Select(t => rules?.GetValueOrDefault(t.Id) is { } r
                ? (object)new { techniqueID = t.Id, score = t.Articles, color = r.Enabled > 0 ? Tagged : Untagged, comment = $"{t.Articles} article{(t.Articles == 1 ? "" : "s")}; {r.Label} tagged" }
                : new { techniqueID = t.Id, score = t.Articles, comment = $"{t.Articles} article{(t.Articles == 1 ? "" : "s")}" }),
            gradient = new { colors = new[] { "#d8eef1", "#0f7c8c" }, minValue = 0, maxValue = max },
            legendItems = rules is null ? Array.Empty<object>() : [new { label = "A rule is tagged with it", color = Tagged }, new { label = "No enabled rule tagged", color = Untagged }],
        };
        return JsonSerializer.Serialize(layer, Indented);
    }

    // The Navigator layer's coverage colours: briefed and tagged in a rule, briefed and not.
    private const string Tagged = "#8fd19e", Untagged = "#f4a09a";

    private sealed record ArticleFact(Guid Id, string Title, string Words, DateOnly On, ArticleStatus Status, Priority? Priority);

    private static IEnumerable<ArticleFact> Of(Adversary p, IEnumerable<ArticleFact> articles, IReadOnlySet<Guid> tagged)
    {
        var e = Entry(p);
        return articles.Where(a => tagged.Contains(a.Id) || StackService.Names(e, a.Words));
    }

    // When each article counts: its first brief, else the day it was written (the organization's day); and the words it's matched by.
    private async Task<List<ArticleFact>> ArticlesAsync(IAppDbContext db, CancellationToken ct)
    {
        var briefed = await ArticleDates.FirstBriefedAsync(db, ct);
        var rows = await db.Articles.AsNoTracking().Select(a => new { a.Id, a.Title, a.Actors, a.Bluf, a.CreatedAtUtc, a.Status, a.Priority }).ToListAsync(ct);
        return rows.Select(a => new ArticleFact(a.Id, a.Title, StackService.Words(a.Title + " " + a.Actors, a.Bluf),
            briefed.TryGetValue(a.Id, out var d) ? d : DateOnly.FromDateTime(clock.ToLocal(a.CreatedAtUtc).DateTime), a.Status, a.Priority)).ToList();
    }

    // --- changing -----------------------------------------------------------------------------------------------

    public async Task<Guid> SaveAsync(AdversaryForm form, CancellationToken ct = default)
    {
        RequireWrite();
        var name = ArticleService.CleanOrNull(form.Name, Adversary.MaxName) ?? throw new ArgumentException("A profile needs a name.");
        if (!name.Any(char.IsLetterOrDigit)) throw new ArgumentException("A profile's name needs a letter or digit.");
        if (!Enum.IsDefined(form.Kind)) throw new ArgumentException("Choose actor or campaign.");
        var aliases = StackService.SplitNames(form.Aliases).Where(n => !string.Equals(n, name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (aliases.Count > MaxAliases) throw new ArgumentException($"Give at most {MaxAliases} other names.");
        var aliasText = aliases.Count == 0 ? null : string.Join("; ", aliases);
        if (aliasText?.Length > Adversary.MaxAliases) throw new ArgumentException("The other names are too long.");
        var mitre = ArticleService.CleanOrNull(form.MitreId, 16)?.ToUpperInvariant();
        if (mitre is not null && !System.Text.RegularExpressions.Regex.IsMatch(mitre, @"^[GC]\d{4}$", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1)))
            throw new ArgumentException("A MITRE id is G and four digits for a group (G1015), or C and four for a campaign.");

        await using var db = await dbs.CreateAsync(ct);
        var taken = await db.Adversaries.Where(a => a.Id != form.Id && !a.IsRetired).Select(a => a.Name).ToListAsync(ct);
        if (taken.Exists(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException($"{name} already has a profile.");
        Adversary p;
        if (form.Id is { } id) p = await db.Adversaries.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException("That profile no longer exists.");
        else db.Adversaries.Add(p = new Adversary());
        p.Name = name;
        p.Kind = form.Kind;
        p.Aliases = aliasText;
        p.MitreId = mitre;
        p.Motivation = ArticleService.CleanOrNull(form.Motivation, Adversary.MaxMotivation);
        p.Sectors = ArticleService.CleanOrNull(form.Sectors, Adversary.MaxSectors);
        p.Notes = ArticleService.CleanOrNull(form.Notes, Adversary.MaxNotes, multiline: true);
        p.Watched = form.Watched;
        await db.SaveChangesAsync(ct);
        return p.Id;
    }

    /// <summary>Finds the live profile with this name or alias, or adds one by name alone; its id.</summary>
    public async Task<Guid> AddByNameAsync(string name, CancellationToken ct = default)
    {
        RequireWrite();
        var clean = ArticleService.CleanOrNull(name, Adversary.MaxName) ?? throw new ArgumentException("A profile needs a name.");
        await using (var db = await dbs.CreateAsync(ct))
        {
            var live = await db.Adversaries.Where(a => !a.IsRetired).ToListAsync(ct);
            if (live.Find(a => Entry(a).Terms.Any(t => string.Equals(t, clean, StringComparison.OrdinalIgnoreCase))) is { } there) return there.Id;
        }
        return await SaveAsync(new AdversaryForm { Name = clean }, ct);
    }

    /// <summary>
    /// Retires a profile, or brings one back. One is brought back only while no live profile has its name (a new one
    /// can be made under a retired one's name; two live ones of a name couldn't either be saved again), and one that was
    /// merged comes back as a profile of its own: no longer "merged into" the other.
    /// </summary>
    public async Task SetRetiredAsync(Guid id, bool retired, CancellationToken ct = default)
    {
        RequireWrite();
        await using var db = await dbs.CreateAsync(ct);
        var a = await db.Adversaries.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException("That profile no longer exists.");
        if (!retired && a.IsRetired)
        {
            var live = await db.Adversaries.Where(x => x.Id != id && !x.IsRetired).Select(x => x.Name).ToListAsync(ct);
            if (live.Exists(n => string.Equals(n, a.Name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"There's already a live profile called {a.Name}. Rename or retire it first.");
            a.MergedIntoId = null;
        }
        a.IsRetired = retired;
        await db.SaveChangesAsync(ct);
    }

    public async Task SetWatchedAsync(Guid id, bool watched, CancellationToken ct = default) => await ChangeAsync(id, a => a.Watched = watched, ct);

    private async Task ChangeAsync(Guid id, Action<Adversary> change, CancellationToken ct)
    {
        RequireWrite();
        await using var db = await dbs.CreateAsync(ct);
        var a = await db.Adversaries.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException("That profile no longer exists.");
        change(a);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Tags an article with a profile. Only that tag changes, so a colleague's tags made since the page loaded stay, and
    /// tagging one it already has (two people at once) does nothing.
    /// </summary>
    public async Task TagArticleAsync(Guid articleId, Guid id, CancellationToken ct = default)
    {
        RequireWrite();
        await using var db = await dbs.CreateAsync(ct);
        if (!await db.Articles.AnyAsync(a => a.Id == articleId, ct)) throw new KeyNotFoundException("That article no longer exists.");
        if (!await db.Adversaries.AnyAsync(a => a.Id == id, ct)) throw new ArgumentException("That profile no longer exists.");
        if (await db.ArticleAdversaries.AnyAsync(t => t.ArticleId == articleId && t.AdversaryId == id, ct)) return;
        db.ArticleAdversaries.Add(new ArticleAdversary { ArticleId = articleId, AdversaryId = id });
        try { await db.SaveChangesAsync(ct); }
        // Someone tagged it between the check and the save: it's tagged, as asked.
        catch (DbUpdateException) { if (!await TaggedAsync(articleId, id, ct)) throw; }
    }

    private async Task<bool> TaggedAsync(Guid articleId, Guid id, CancellationToken ct)
    {
        await using var db = await dbs.CreateAsync(ct);
        return await db.ArticleAdversaries.AnyAsync(t => t.ArticleId == articleId && t.AdversaryId == id, ct);
    }

    /// <summary>Untags a profile from an article; nothing else changes, and one already untagged is fine.</summary>
    public async Task UntagArticleAsync(Guid articleId, Guid id, CancellationToken ct = default)
    {
        RequireWrite();
        await using var db = await dbs.CreateAsync(ct);
        if (await db.ArticleAdversaries.FirstOrDefaultAsync(t => t.ArticleId == articleId && t.AdversaryId == id, ct) is not { } tag) return;
        db.ArticleAdversaries.Remove(tag);
        try { await db.SaveChangesAsync(ct); }
        // Someone untagged it first.
        catch (DbUpdateConcurrencyException) { }
    }

    /// <summary>Tags these articles (ones that name it, an analyst checked them) with the profile. Returns how many were tagged.</summary>
    public async Task<int> LinkAsync(Guid id, IReadOnlyCollection<Guid> articleIds, CancellationToken ct = default)
    {
        RequireWrite();
        for (var attempt = 0; ; attempt++)
        {
            await using var db = await dbs.CreateAsync(ct);
            if (!await db.Adversaries.AnyAsync(a => a.Id == id, ct)) throw new KeyNotFoundException("That profile no longer exists.");
            var have = (await db.ArticleAdversaries.Where(t => t.AdversaryId == id).Select(t => t.ArticleId).ToListAsync(ct)).ToHashSet();
            var add = await db.Articles.Where(a => articleIds.Contains(a.Id)).Select(a => a.Id).ToListAsync(ct);
            var adding = add.Where(a => !have.Contains(a)).ToList();
            db.ArticleAdversaries.AddRange(adding.Select(a => new ArticleAdversary { ArticleId = a, AdversaryId = id }));
            try
            {
                await db.SaveChangesAsync(ct);
                return adding.Count;
            }
            // A colleague tagged one of them (or linked them all) between the read and the save: read again and tag the
            // rest, rather than failing the whole link on the tag that's already there.
            catch (DbUpdateException) when (attempt == 0) { }
        }
    }

    /// <summary>
    /// Merges <paramref name="fromId"/> into <paramref name="intoId"/>: its tags move, its name and aliases become the
    /// other's aliases, and it's retired pointing at it. For two profiles that turn out to be one group.
    /// </summary>
    public async Task MergeAsync(Guid fromId, Guid intoId, CancellationToken ct = default)
    {
        RequireWrite();
        if (fromId == intoId) throw new ArgumentException("Choose another profile to merge into.");
        await using var db = await dbs.CreateAsync(ct);
        var from = await db.Adversaries.FirstOrDefaultAsync(a => a.Id == fromId, ct) ?? throw new KeyNotFoundException("That profile no longer exists.");
        var into = await db.Adversaries.FirstOrDefaultAsync(a => a.Id == intoId, ct) ?? throw new KeyNotFoundException("The profile to merge into no longer exists.");
        // The page offered both as live: a colleague retiring or merging either since would otherwise move the tags onto a
        // retired profile, or merge one that's already gone into another.
        if (from.IsRetired) throw new InvalidOperationException($"{from.Name} has been retired or merged meanwhile. Look at it again.");
        if (into.IsRetired) throw new InvalidOperationException($"{into.Name} has been retired or merged meanwhile. Choose another profile to merge into.");
        var names = StackService.SplitNames(string.Join(";", new[] { into.Aliases, from.Name, from.Aliases }.Where(s => s is not null)))
            .Where(n => !string.Equals(n, into.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        // Whole names only, as many as the form takes: a name cut part way would match words it isn't, and a profile with
        // more names than the form allows couldn't be saved again.
        into.Aliases = Fit(names);
        into.MitreId ??= from.MitreId;
        into.Watched |= from.Watched;
        var moving = await db.ArticleAdversaries.Where(t => t.AdversaryId == fromId).ToListAsync(ct);
        var already = (await db.ArticleAdversaries.Where(t => t.AdversaryId == intoId).Select(t => t.ArticleId).ToListAsync(ct)).ToHashSet();
        db.ArticleAdversaries.RemoveRange(moving);
        db.ArticleAdversaries.AddRange(moving.Where(t => !already.Contains(t.ArticleId)).Select(t => new ArticleAdversary { ArticleId = t.ArticleId, AdversaryId = intoId }));
        from.IsRetired = true;
        from.Watched = false;
        from.MergedIntoId = intoId;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Fetches Enterprise ATT&amp;CK's groups and campaigns from MITRE's TAXII server, through intake's guarded client,
    /// and imports them (see <see cref="ImportMitreAsync"/>).
    /// </summary>
    public async Task<MitreImport> ImportFromMitreAsync(CancellationToken ct = default)
    {
        RequireWrite();
        var got = await http.GetAsync(MitreTaxiiUrl, new Intake.OutboundRequest(Headers: new Dictionary<string, string> { ["Accept"] = "application/taxii+json;version=2.1" }), ct);
        if (got.Status != Intake.FetchStatus.Ok || got.Content is not { } bytes)
            throw new InvalidOperationException("MITRE's server couldn't be reached" + (got.Error is { } e ? $": {e.TrimEnd('.')}." : ".") + " Save the groups from it and upload the file instead.");
        using var stream = new MemoryStream(bytes);
        return await ImportMitreAsync(stream, ct);
    }

    /// <summary>
    /// Imports the groups and campaigns in STIX from MITRE ATT&amp;CK (its TAXII envelope, or a bundle): a profile
    /// with the same MITRE id, name or alias gains the names it lacks; any other is added, not watched. Revoked and
    /// deprecated objects are skipped. Notes, motivation and sectors are left as analysts wrote them.
    /// </summary>
    public async Task<MitreImport> ImportMitreAsync(Stream bundle, CancellationToken ct = default)
    {
        RequireWrite();
        JsonDocument doc;
        try { doc = await JsonDocument.ParseAsync(bundle, cancellationToken: ct); }
        catch (JsonException) { throw new ArgumentException("That isn't MITRE's STIX JSON. Use enterprise-attack.json from the attack-stix-data repository."); }
        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("objects", out var objects) || objects.ValueKind != JsonValueKind.Array)
                throw new ArgumentException("That isn't a STIX bundle: it has no objects.");
            await using var db = await dbs.CreateAsync(ct);
            var profiles = await db.Adversaries.Where(a => !a.IsRetired).ToListAsync(ct);
            var (added, updated, skipped) = (0, 0, 0);
            var groups = new List<MitreGroup>();
            foreach (var o in objects.EnumerateArray())
            {
                var type = Str(o, "type");
                if (type is not ("intrusion-set" or "campaign")) continue;
                if (Bool(o, "revoked") || Bool(o, "x_mitre_deprecated")) { skipped++; continue; }
                var name = Str(o, "name")?.Trim();
                if (string.IsNullOrEmpty(name) || name.Length > Adversary.MaxName) { skipped++; continue; }
                var aliases = Strings(o, "aliases").Where(a => a.Length <= Adversary.MaxName && !string.Equals(a, name, StringComparison.OrdinalIgnoreCase)).ToList();
                groups.Add(new MitreGroup(type == "campaign" ? AdversaryKind.Campaign : AdversaryKind.Actor, name, MitreIdOf(o), aliases));
            }
            var matches = MatchGroups(groups, profiles);
            foreach (var g in groups)
            {
                var (name, mitre, aliases) = (g.Name, g.MitreId, g.Aliases);
                var all = new[] { name }.Concat(aliases).ToList();
                if (!matches.TryGetValue(g, out var match))
                {
                    if (profiles.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
                    {
                        skipped++;   // a live profile of that name is another MITRE group: two of one name couldn't be saved
                        continue;
                    }
                    var a = new Adversary { Name = name, Kind = g.Kind, MitreId = mitre };
                    a.Aliases = Fit(aliases);
                    db.Adversaries.Add(a);
                    profiles.Add(a);
                    added++;
                    continue;
                }
                var have = Entry(match).Terms;
                var missing = all.Where(n => !have.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList();
                if (missing.Count == 0 && (match.MitreId is not null || mitre is null)) continue;
                match.Aliases = Fit(StackService.SplitNames(match.Aliases).Concat(missing).ToList());
                match.MitreId ??= mitre;
                updated++;
            }
            await db.SaveChangesAsync(ct);
            return new MitreImport(added, updated, skipped);
        }
    }

    private sealed record MitreGroup(AdversaryKind Kind, string Name, string? MitreId, IReadOnlyList<string> Aliases);

    // Which profile each of MITRE's groups is, in passes over the whole bundle from the surest to the loosest, each taking
    // only profiles no earlier pass took, so the result doesn't depend on the order of the bundle or of the profiles: by
    // MITRE id; by name; a profile named by one of the group's aliases; a profile that lists the group's name among its
    // other names (an analyst's "Sandworm" listing "Sandworm Team"). Never into a profile of another MITRE id: separate
    // groups share aliases (UAC-0056 is both Ember Bear's and Saint Bear's).
    private static Dictionary<MitreGroup, Adversary> MatchGroups(IReadOnlyList<MitreGroup> groups, IReadOnlyList<Adversary> profiles)
    {
        var matches = new Dictionary<MitreGroup, Adversary>(ReferenceEqualityComparer.Instance);
        var taken = new HashSet<Adversary>(ReferenceEqualityComparer.Instance);
        void Pass(Func<MitreGroup, Adversary, bool> fits, bool byId = false)
        {
            foreach (var g in groups.Where(g => !matches.ContainsKey(g)))
                if (profiles.FirstOrDefault(p => !taken.Contains(p) && (byId || p.MitreId is null || g.MitreId is null) && fits(g, p)) is { } p)
                {
                    matches[g] = p;
                    taken.Add(p);
                }
        }
        var same = StringComparer.OrdinalIgnoreCase;
        Pass((g, p) => g.MitreId is not null && p.MitreId == g.MitreId, byId: true);
        Pass((g, p) => same.Equals(p.Name, g.Name));
        Pass((g, p) => g.Aliases.Contains(p.Name, same));
        Pass((g, p) => StackService.SplitNames(p.Aliases).Contains(g.Name, same));
        return matches;
    }

    private static string? Fit(IReadOnlyList<string> names)
    {
        var kept = new List<string>();
        foreach (var n in names.Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxAliases))
        {
            if (string.Join("; ", kept.Append(n)).Length > Adversary.MaxAliases) break;
            kept.Add(n);
        }
        return kept.Count == 0 ? null : string.Join("; ", kept);
    }

    private static string? MitreIdOf(JsonElement o) =>
        o.TryGetProperty("external_references", out var refs) && refs.ValueKind == JsonValueKind.Array
            ? refs.EnumerateArray().Where(r => Str(r, "source_name") == "mitre-attack").Select(r => Str(r, "external_id")).FirstOrDefault(id => id is { Length: 5 })
            : null;

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static IEnumerable<string> Strings(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!.Trim()).Where(s => s.Length > 0)
            : [];

    private void RequireRead()
    {
        if (!user.Has(Permission.ReadTeamEdition)) throw new ForbiddenException("You don't have access to the adversary profiles.");
    }

    private void RequireWrite()
    {
        if (!user.Has(Permission.WriteArticles)) throw new ForbiddenException("Only analysts can change the adversary profiles.");
    }
}
