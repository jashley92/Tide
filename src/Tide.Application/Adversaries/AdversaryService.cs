using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Tide.Application.Abstractions;
using Tide.Application.Articles;
using Tide.Application.Content;
using Tide.Application.Mitre;
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
        var article = await db.Articles.AsNoTracking().Where(a => a.Id == articleId).Select(a => new { a.Title, a.Actors, a.Bluf }).FirstOrDefaultAsync(ct);
        if (article is null) return new ArticleAdversaries([], []);
        var tagged = await db.ArticleAdversaries.AsNoTracking().Where(t => t.ArticleId == articleId).Select(t => t.Adversary!).OrderByName(a => a.Name).ToListAsync(ct);
        var live = await db.Adversaries.AsNoTracking().Where(a => !a.IsRetired).ToListAsync(ct);
        var words = StackService.Words(article.Title + " " + article.Actors, article.Bluf);
        var suggested = live.Where(a => tagged.All(t => t.Id != a.Id) && StackService.Names(Entry(a), words)).OrderByName(a => a.Name).ToList();
        return new ArticleAdversaries(tagged, suggested);
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
