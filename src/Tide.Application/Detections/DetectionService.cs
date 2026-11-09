using System.Text;
using Microsoft.EntityFrameworkCore;
using Tide.Application.Abstractions;
using Tide.Application.Actions;
using Tide.Application.Content;
using Tide.Application.Org;
using Tide.Application.Security;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.Application.Detections;

/// <summary>What an import did: rules added, changed, unchanged, and those of the source it no longer contains.</summary>
public sealed record DetectionImport(int Added, int Changed, int Unchanged, int NotInImport, IReadOnlyList<string> Problems);

/// <summary>A source's latest import: when, and how many of its rules are current and enabled.</summary>
public sealed record DetectionImportState(DetectionSource Source, DateTimeOffset At, int Rules, int Enabled);

/// <summary>A rule as listed, with whether its source's latest import still contained it.</summary>
public sealed record DetectionRow(DetectionRule Rule, bool Current);

/// <summary>
/// One API caller's set of pushed rules, for Admin › Detection rules: whose (a token's name or a person), how many rules
/// its latest push held, and when that was. <see cref="Quiet"/> says it hasn't been pushed for a while, which is only worth
/// a look (a pipeline that pushes when its rules change can be quiet for months); <see cref="Counts"/> is false once its
/// token was revoked or expired and no token of that name is active, so nothing can push it again. <see cref="Caller"/>
/// is empty for rules pushed before callers were kept.
/// </summary>
public sealed record DetectionCallerSet(string Caller, string Name, int Rules, DateTimeOffset PushedAtUtc, bool Quiet, bool Counts);

/// <summary>The current rules tagged with a technique (exactly, its parent, or one of its sub-techniques).</summary>
public sealed record TechniqueRules(int Enabled, int Disabled, IReadOnlyList<string> Names)
{
    public string Label => Enabled > 0 ? $"{Enabled} rule{(Enabled == 1 ? "" : "s")}" : Disabled > 0 ? $"{Disabled} disabled" : "no rule";
}

/// <summary>A technique briefed in the window: how often, which watched adversaries use it, its rules, and any gap action.</summary>
public sealed record CoverageRow(string TechniqueId, string Name, int Briefed, IReadOnlyList<string> Watched, TechniqueRules Rules,
    int? ActionNumber, ActionStatus? ActionStatus)
{
    public bool Gap => Rules.Enabled == 0;

    /// <summary>Whether its gap action is still open (a closed one can be raised again).</summary>
    public bool ActionOpen => ActionNumber is not null && ActionStatus is { } s && !ActionItem.IsClosedStatus(s);
}

/// <summary>Reports › Coverage: what was briefed in the window against the SOC's current rules.</summary>
public sealed record CoverageReport(int Days, IReadOnlyList<CoverageRow> Rows, IReadOnlyList<DetectionImportState> Imports)
{
    public int Covered => Rows.Count(r => !r.Gap);
    public int Gaps => Rows.Count(r => r.Gap);
    public int GapsWatched => Rows.Count(r => r.Gap && r.Watched.Count > 0);
    public int GapsWithAction => Rows.Count(r => r.Gap && r.ActionOpen);
}

/// <summary>
/// The SOC's detection rules, referenced (EP-79): imported from a Sentinel export, Sigma files, a CSV or the API, and
/// compared with the ATT&amp;CK techniques TIDE briefs. "Covered" means a current, enabled rule is <em>tagged</em> with the
/// technique, which isn't proof it detects it; the pages say so. Rules are internal and never reach an edition.
/// </summary>
public sealed class DetectionService(IAppDbContextFactory dbs, ICurrentUser user, OrgClock clock, ActionService actions, TimeProvider time)
{
    /// <summary>Days back Coverage looks at briefs.</summary>
    public const int CoverageDays = 90;

    /// <summary>
    /// Days without a push after which Admin › Detection rules marks a caller's set as quiet. It still counts: a pipeline
    /// that pushes only when its rules change is quiet for as long as they don't, and expiring its set would show gaps on
    /// Coverage for techniques it covers. A set stops counting only when its token can't push again (see <see cref="CallersAsync"/>).
    /// </summary>
    public const int PushQuietDays = 30;

    private static readonly string[] SentinelFiles = [".json"], SigmaFiles = [".yml", ".yaml"], CsvFiles = [".csv"];

    // --- importing ----------------------------------------------------------------------------------------------

    /// <summary>
    /// Reads an uploaded file (or a zip of them) as <paramref name="source"/> rules and stores them: new ones are added,
    /// known ones (by their id in the source) refreshed, and the source's rules missing from it are kept but no longer
    /// count. Administrators only.
    /// </summary>
    public async Task<DetectionImport> ImportAsync(DetectionSource source, string fileName, byte[] bytes, CancellationToken ct = default)
    {
        if (!user.Has(Permission.Administer)) throw new ForbiddenException("Only an administrator can import detection rules.");
        var problems = new List<string>();
        var rules = new List<ParsedRule>();
        switch (source)
        {
            case DetectionSource.Sentinel:
                foreach (var (name, text) in DetectionParsers.Files(fileName, bytes, SentinelFiles, problems)) rules.AddRange(DetectionParsers.Sentinel(name, text, problems));
                break;
            case DetectionSource.Sigma:
                foreach (var (name, text) in DetectionParsers.Files(fileName, bytes, SigmaFiles, problems)) rules.AddRange(DetectionParsers.Sigma(name, text, problems));
                break;
            case DetectionSource.Csv:
                foreach (var (name, text) in DetectionParsers.Files(fileName, bytes, CsvFiles, problems)) rules.AddRange(DetectionParsers.Csv(name, text, problems));
                break;
            default:
                throw new ArgumentException("Rules from the API are pushed, not uploaded.");
        }
        if (rules.Count == 0) throw new ArgumentException("No rules were found in the file." + (problems.Count > 0 ? " " + problems[0] : ""));
        return await StoreAsync(source, rules, problems, ct, file: fileName);
    }

    /// <summary>
    /// The API's push (EP-75's <c>detections:write</c>): the full set of rules the caller manages, replacing its last push.
    /// Each caller's rules are a set of their own (<see cref="CallerMark"/>): another caller's push neither replaces them
    /// nor stops them counting, and the same id from two callers is two rules.
    /// </summary>
    public async Task<DetectionImport> PushAsync(IReadOnlyList<ParsedRule> rules, CancellationToken ct = default)
    {
        if (!user.Has(Permission.WriteArticles)) throw new ForbiddenException("This token can't push detection rules.");
        var problems = new List<string>();
        var caller = Caller();
        var clean = rules.Where(r => !string.IsNullOrWhiteSpace(r.Name))
            .Select(r => DetectionParsers.Rule(Owned(caller, string.IsNullOrWhiteSpace(r.ExternalId) ? r.Name : r.ExternalId, r.Name, problems),
                r.Name, r.Techniques, r.Platform, r.Enabled, problems)).ToList();
        if (clean.Count == 0) throw new ArgumentException("Send at least one rule with a name.");
        return await StoreAsync(DetectionSource.Api, clean, problems, ct, caller);
    }

    // An API rule's ExternalId is "<caller><CallerMark><its id>": a control character, so no id read before it was added
    // (EP-79's first pushes, which had no caller) is taken for one. Callers are no table of their own; the mark is the set.
    private const char CallerMark = '\u001F';

    // Whose push this is: a token by its name, so a token issued again under the same name (as each expiry needs) carries
    // on its set; a person by their id. Lower case, so one caller is one set whatever the database's collation.
    private string Caller() =>
        (user.UserId.StartsWith("api:", StringComparison.Ordinal) ? "api:" + user.DisplayName : user.UserId).Replace(CallerMark, ' ').ToLowerInvariant();

    // The rule's id with its caller in front. One too long for the column keeps its start and ends in a hash of the whole
    // id instead, so two long ids that differ only near the end stay two rules (clipping alone made them one).
    private static string Owned(string caller, string id, string name, List<string> problems)
    {
        id = id.Trim();
        var prefix = caller + CallerMark;
        if (prefix.Length + id.Length <= DetectionRule.MaxExternalId) return prefix + id;
        var hash = "#" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(id)))[..16];
        problems.Add($"{Clip(name.Trim(), 60)}: its id is longer than {DetectionRule.MaxExternalId - prefix.Length} characters, so TIDE keeps its start and a hash of the rest.");
        return prefix + id[..Math.Max(0, DetectionRule.MaxExternalId - prefix.Length - hash.Length)] + hash;
    }

    // The caller whose push holds an API rule; null for an imported rule and an API rule pushed before callers were kept.
    private static string? CallerOf(DetectionRule r) =>
        r.Source == DetectionSource.Api && r.ExternalId.IndexOf(CallerMark) is var i and > 0 ? r.ExternalId[..i] : null;

    private async Task<DetectionImport> StoreAsync(DetectionSource source, IReadOnlyList<ParsedRule> rules, List<string> problems, CancellationToken ct,
        string? caller = null, string? file = null)
    {
        var now = time.GetUtcNow();
        await using var db = await dbs.CreateAsync(ct);
        // The rules this import replaces: the source's, or for a push only its caller's.
        var existing = (await db.DetectionRules.Where(r => r.Source == source).ToListAsync(ct)).Where(r => CallerOf(r) == caller)
            .ToDictionary(r => r.ExternalId, StringComparer.OrdinalIgnoreCase);
        int added = 0, changed = 0, unchanged = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rules.Reverse().DistinctBy(r => r.ExternalId, StringComparer.OrdinalIgnoreCase).Reverse())
        {
            seen.Add(r.ExternalId);
            var techniques = Joined(r.Techniques);
            if (existing.TryGetValue(r.ExternalId, out var rule))
            {
                if (rule.Name == r.Name && rule.Techniques == techniques && rule.Platform == r.Platform && rule.Enabled == r.Enabled) unchanged++;
                else changed++;
            }
            else
            {
                rule = new DetectionRule { Source = source, ExternalId = r.ExternalId };
                db.DetectionRules.Add(rule);
                added++;
            }
            (rule.Name, rule.Techniques, rule.Platform, rule.Enabled, rule.LastSeenAtUtc) = (r.Name, techniques, r.Platform, r.Enabled, now);
        }
        var missing = existing.Keys.Count(k => !seen.Contains(k));
        // Rules aren't audited one by one (every push would write a row for each), so the import or push is one entry:
        // whose, from where, and what it did.
        var changes = new Dictionary<string, string?[]>
        {
            ["Name"] = [null, caller is null ? $"Rules imported from {SourceName(source)}" : "Rules pushed by " + CallerName(caller, new Dictionary<string, string> { [caller] = user.DisplayName })],
        };
        if (file is not null) changes["File"] = [null, Clip(file, 200)];
        foreach (var (field, n) in new[] { ("Added", added), ("Changed", changed), ("Unchanged", unchanged), ("Not in this " + (caller is null ? "import" : "push"), missing) })
            changes[field] = [null, n.ToString(System.Globalization.CultureInfo.InvariantCulture)];
        db.AuditLog.Add(new AuditLogEntry
        {
            AtUtc = now, UserId = user.UserId, UserName = user.DisplayName, Action = "Modified", EntityType = nameof(DetectionRule),
            EntityKey = Clip(caller ?? SourceName(source), 100, ""), ChangesJson = System.Text.Json.JsonSerializer.Serialize(changes),
        });
        await db.SaveChangesAsync(ct);
        return new DetectionImport(added, changed, unchanged, missing, problems.Take(20).ToList());
    }

    // As many technique ids as fit the column, in order.
    private static string Joined(IReadOnlyList<string> techniques)
    {
        var sb = new StringBuilder();
        foreach (var t in techniques)
        {
            if (sb.Length + t.Length + 1 > DetectionRule.MaxTechniques) break;
            if (sb.Length > 0) sb.Append(',');
            sb.Append(t);
        }
        return sb.ToString();
    }

    // --- reading ------------------------------------------------------------------------------------------------

    /// <summary>The rules, matching <paramref name="search"/> in their name or techniques, and each source's latest import.</summary>
    public async Task<(IReadOnlyList<DetectionRow> Rules, IReadOnlyList<DetectionImportState> Imports)> ListAsync(string? search = null, CancellationToken ct = default)
    {
        RequireRead();
        await using var db = await dbs.CreateAsync(ct);
        var all = await db.DetectionRules.AsNoTracking().OrderByName(r => r.Name).ToListAsync(ct);
        var latest = await LatestAsync(db, all, time.GetUtcNow(), ct);
        var q = search?.Trim();
        var rows = all.Where(r => string.IsNullOrEmpty(q) || r.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || r.Techniques.Contains(q, StringComparison.OrdinalIgnoreCase))
            .Select(r => new DetectionRow(r, IsCurrent(r, latest))).ToList();
        return (rows, Imports(all, latest));
    }

    /// <summary>The current rules for each technique in <paramref name="techniqueIds"/>; null when no rules have been imported.</summary>
    public async Task<IReadOnlyDictionary<string, TechniqueRules>?> ForTechniquesAsync(IEnumerable<string> techniqueIds, CancellationToken ct = default)
    {
        RequireRead();
        await using var db = await dbs.CreateAsync(ct);
        return await ForTechniquesAsync(db, user, techniqueIds, time.GetUtcNow(), ct);
    }

    /// <summary>
    /// As <see cref="ForTechniquesAsync(IEnumerable{string}, CancellationToken)"/>, for the Navigator layer, which anyone
    /// reading the team edition can download: null, as when no rules are imported, for someone who may not see the rules.
    /// </summary>
    internal static async Task<IReadOnlyDictionary<string, TechniqueRules>?> ForTechniquesAsync(IAppDbContext db, ICurrentUser user, IEnumerable<string> techniqueIds,
        DateTimeOffset now, CancellationToken ct)
    {
        if (!user.Has(Permission.WriteArticles)) return null;
        var current = await CurrentAsync(db, now, ct);
        if (current is null) return null;
        return techniqueIds.Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(t => t, t => For(current, t), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Every ATT&amp;CK technique on an article first briefed in the last <see cref="CoverageDays"/> days, with how many
    /// articles, the watched adversaries whose tagged articles show it, its current rules, and its gap action if one was
    /// raised. Gaps first: those a watched adversary uses, then the most briefed.
    /// </summary>
    public async Task<CoverageReport> CoverageAsync(CancellationToken ct = default)
    {
        RequireRead();
        await using var db = await dbs.CreateAsync(ct);
        var today = clock.Today;
        var from = today.AddDays(-CoverageDays);
        var briefed = await db.BriefItems.AsNoTracking().Join(db.Briefs, i => i.BriefId, b => b.Id, (i, b) => new { i.ArticleId, b.BriefDate })
            .Where(x => x.BriefDate >= from && x.BriefDate <= today).Select(x => x.ArticleId).Distinct().ToListAsync(ct);
        var techniques = (await db.ArticleTechniques.AsNoTracking().Where(t => briefed.Contains(t.ArticleId)).ToListAsync(ct))
            .GroupBy(t => t.TechniqueId, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Select(t => t.ArticleId).Distinct().Count(), StringComparer.OrdinalIgnoreCase);
        var watched = await (from l in db.ArticleAdversaries.AsNoTracking()
                             join a in db.Adversaries on l.AdversaryId equals a.Id
                             join t in db.ArticleTechniques on l.ArticleId equals t.ArticleId
                             where a.Watched && !a.IsRetired
                             select new { t.TechniqueId, a.Name }).Distinct().ToListAsync(ct);
        var byTechnique = watched.ToLookup(w => w.TechniqueId, w => w.Name, StringComparer.OrdinalIgnoreCase);
        var all = await db.DetectionRules.AsNoTracking().ToListAsync(ct);
        var latest = await LatestAsync(db, all, time.GetUtcNow(), ct);
        var current = all.Where(r => IsCurrent(r, latest)).ToList();
        var gaps = await (from g in db.DetectionGaps.AsNoTracking()
                          join a in db.Actions on g.ActionId equals a.Id
                          select new { g.TechniqueId, a.Number, a.Status }).ToListAsync(ct);
        var gapOf = gaps.ToDictionary(g => g.TechniqueId, StringComparer.OrdinalIgnoreCase);
        var rows = techniques.Select(t =>
        {
            var gap = gapOf.GetValueOrDefault(t.Key);
            return new CoverageRow(t.Key, Mitre.AttackCatalog.Find(t.Key)?.Name ?? t.Key, t.Value,
                byTechnique[t.Key].Distinct().Order(StringComparer.OrdinalIgnoreCase).ToList(), For(current, t.Key), gap?.Number, gap?.Status);
        }).OrderByDescending(r => r.Gap).ThenByDescending(r => r.Watched.Count > 0).ThenByDescending(r => r.Briefed).ThenBy(r => r.TechniqueId, StringComparer.Ordinal).ToList();
        return new CoverageReport(CoverageDays, rows, Imports(all, latest));
    }

    // --- API callers' sets -------------------------------------------------------------------------------------------

    /// <summary>Each API caller's pushed set, the latest push first. Administrators only.</summary>
    public async Task<IReadOnlyList<DetectionCallerSet>> CallersAsync(CancellationToken ct = default)
    {
        RequireAdmin();
        await using var db = await dbs.CreateAsync(ct);
        var pushed = await db.DetectionRules.AsNoTracking().Where(r => r.Source == DetectionSource.Api).ToListAsync(ct);
        if (pushed.Count == 0) return [];
        var people = await PeopleAsync(db, ct);
        var now = time.GetUtcNow();
        var gone = await GoneAsync(db, now, ct);
        return pushed.GroupBy(r => CallerOf(r) ?? "").Select(g =>
        {
            var at = g.Max(r => r.LastSeenAtUtc);
            return new DetectionCallerSet(g.Key, CallerName(g.Key, people), g.Count(r => r.LastSeenAtUtc >= at), at, at < now.AddDays(-PushQuietDays), !gone.Contains(g.Key));
        }).OrderByDescending(s => s.PushedAtUtc).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Removes an API caller's pushed rules (<see cref="DetectionCallerSet.Caller"/>; empty for those pushed before callers
    /// were kept), so they stop counting at once, and records it in the audit log. A later push from the caller starts a
    /// new set. Returns how many rules went. Administrators only.
    /// </summary>
    public async Task<int> RemoveCallerAsync(string caller, CancellationToken ct = default)
    {
        RequireAdmin();
        await using var db = await dbs.CreateAsync(ct);
        var rules = (await db.DetectionRules.Where(r => r.Source == DetectionSource.Api).ToListAsync(ct)).Where(r => (CallerOf(r) ?? "") == caller).ToList();
        if (rules.Count == 0) throw new KeyNotFoundException("That caller has no pushed rules now.");
        db.DetectionRules.RemoveRange(rules);
        // Rules aren't audited one by one (each push would write a row for every rule), so the removal, an administrator's
        // act, is recorded as one entry.
        db.AuditLog.Add(new AuditLogEntry
        {
            AtUtc = time.GetUtcNow(), UserId = user.UserId, UserName = user.DisplayName, Action = "Deleted", EntityType = nameof(DetectionRule),
            EntityKey = Clip(caller.Length == 0 ? "api" : caller, 100, ""),
            ChangesJson = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string?[]>
            {
                ["Name"] = ["Rules pushed by " + CallerName(caller, await PeopleAsync(db, ct)), null],
                ["Rules"] = [rules.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), null],
            }),
        });
        await db.SaveChangesAsync(ct);
        return rules.Count;
    }

    private static async Task<Dictionary<string, string>> PeopleAsync(IAppDbContext db, CancellationToken ct) =>
        (await db.Users.AsNoTracking().Select(u => new { u.Sid, u.DisplayName }).ToListAsync(ct))
        .DistinctBy(u => u.Sid, StringComparer.OrdinalIgnoreCase).ToDictionary(u => u.Sid, u => u.DisplayName, StringComparer.OrdinalIgnoreCase);

    // A caller as the page says it: a token by its name, a person by theirs where TIDE knows them.
    private static string CallerName(string caller, IReadOnlyDictionary<string, string> people) =>
        caller.Length == 0 ? "an earlier version, before callers were kept"
        : caller.StartsWith("api:", StringComparison.Ordinal) ? $"the token “{caller[4..]}”"
        : people.TryGetValue(caller, out var n) && n.Length > 0 ? n : caller;

    // --- raising a gap ------------------------------------------------------------------------------------------

    /// <summary>
    /// Raises an action for <paramref name="teamId"/> to add or tag a rule for a briefed technique with none, linked to
    /// the latest article that shows it, and remembers it on the Coverage row. Refused while an earlier one is open.
    /// A team at its active limit that warns needs <paramref name="overLimitReason"/>, as creating any action does.
    /// Returns the action's number.
    /// </summary>
    public async Task<int> RaiseAsync(string techniqueId, Guid teamId, string? overLimitReason = null, CancellationToken ct = default)
    {
        RequireRead();
        var id = DetectionParsers.Technique(techniqueId) ?? throw new ArgumentException("That isn't a technique id.");
        var report = await CoverageAsync(ct);
        var row = report.Rows.FirstOrDefault(r => string.Equals(r.TechniqueId, id, StringComparison.OrdinalIgnoreCase))
                  ?? throw new KeyNotFoundException($"{id} wasn't briefed in the last {CoverageDays} days.");
        if (row.ActionOpen) throw new InvalidOperationException($"A-{row.ActionNumber} is already open for {id}.");
        List<(Guid Id, string Title)> articles;
        await using (var db = await dbs.CreateAsync(ct))
        {
            var since = clock.Today.AddDays(-CoverageDays);
            articles = (await (from t in db.ArticleTechniques.AsNoTracking()
                               join i in db.BriefItems on t.ArticleId equals i.ArticleId
                               join b in db.Briefs on i.BriefId equals b.Id
                               join a in db.Articles on t.ArticleId equals a.Id
                               where t.TechniqueId == row.TechniqueId && b.BriefDate >= since
                               select new { a.Id, a.Title, b.BriefDate }).ToListAsync(ct))
                .OrderByDescending(x => x.BriefDate).DistinctBy(x => x.Id).Select(x => (x.Id, x.Title)).ToList();
        }
        var name = row.Name == row.TechniqueId ? row.TechniqueId : $"{row.TechniqueId} {row.Name}";
        var about = new StringBuilder();
        about.Append(System.Globalization.CultureInfo.InvariantCulture, $"Briefed in {row.Briefed} article{(row.Briefed == 1 ? "" : "s")} in the last {CoverageDays} days, with no enabled detection rule tagged with it");
        about.Append(row.Rules.Disabled > 0 ? $" ({row.Rules.Disabled} disabled)." : ".");
        if (row.Watched.Count > 0) about.Append(" Used by ").Append(string.Join(", ", row.Watched)).Append(" (watched).");
        about.Append("\n\n");
        foreach (var (_, title) in articles.Take(5)) about.Append("- ").Append(title).Append('\n');
        about.Append("\nAdd or tag a rule for it, then close this action. Coverage shows it as covered after the next rule import.");
        var number = await actions.CreateAsync(new ActionForm
        {
            Title = Clip($"Detection gap: {name}", ActionItem.MaxTitle), Description = about.ToString(), Kind = ActionKind.ConfigChange,
            Priority = row.Watched.Count > 0 ? Priority.P2 : Priority.P3, TeamId = teamId, ArticleId = articles.Count > 0 ? articles[0].Id : null,
            OverLimitReason = overLimitReason,
        }, ct);
        await using (var db = await dbs.CreateAsync(ct))
        {
            var actionId = await db.Actions.Where(a => a.Number == number).Select(a => a.Id).FirstAsync(ct);
            // The technique's gap row, even one whose action was deleted (its key is the technique): it takes the new action.
            var gap = await db.DetectionGaps.IgnoreQueryFilters([ActionItem.DeletedFilter]).FirstOrDefaultAsync(g => g.TechniqueId == row.TechniqueId, ct);
            if (gap is null) db.DetectionGaps.Add(gap = new DetectionGap { TechniqueId = row.TechniqueId });
            (gap.ActionId, gap.RaisedAtUtc) = (actionId, time.GetUtcNow());
            await db.SaveChangesAsync(ct);
        }
        return number;
    }

    // --- helpers ------------------------------------------------------------------------------------------------

    /// <summary>A source's name as the pages say it.</summary>
    public static string SourceName(DetectionSource s) => s switch
    {
        DetectionSource.Sentinel => "Sentinel",
        DetectionSource.Sigma => "Sigma",
        DetectionSource.Csv => "CSV",
        _ => "API",
    };

    private static string Clip(string s, int max, string more = "…") => s.Length <= max ? s : s[..(max - more.Length)] + more;

    // When each source was last imported, and each API caller last pushed: the latest LastSeenAtUtc among their rules.
    // And the callers whose token can't push again.
    private sealed record LatestImports(Dictionary<DetectionSource, DateTimeOffset> Sources, Dictionary<string, DateTimeOffset> Callers, HashSet<string> Gone);

    private static async Task<LatestImports> LatestAsync(IAppDbContext db, IReadOnlyList<DetectionRule> all, DateTimeOffset now, CancellationToken ct) => new(
        all.GroupBy(r => r.Source).ToDictionary(g => g.Key, g => g.Max(r => r.LastSeenAtUtc)),
        all.Where(r => CallerOf(r) is not null).GroupBy(r => CallerOf(r)!).ToDictionary(g => g.Key, g => g.Max(r => r.LastSeenAtUtc)),
        await GoneAsync(db, now, ct));

    // The token callers ("api:<name>") with tokens of that name but none active: each was revoked or has expired, so no
    // push can refresh their set. A token issued again under the name brings the set back at once. A person's set, and a
    // token's whose rows are gone, isn't judged here: it counts, and the admin page shows how long it has been quiet.
    private static async Task<HashSet<string>> GoneAsync(IAppDbContext db, DateTimeOffset now, CancellationToken ct) =>
        (await db.ApiTokens.AsNoTracking().Select(t => new { t.Name, t.RevokedAtUtc, t.ExpiresAtUtc }).ToListAsync(ct))
        .GroupBy(t => ("api:" + t.Name).Replace(CallerMark, ' ').ToLowerInvariant())
        .Where(g => g.All(t => t.RevokedAtUtc is not null || t.ExpiresAtUtc <= now)).Select(g => g.Key).ToHashSet();

    // In its source's latest import, or for a pushed rule its caller's latest push, unless that caller's token can't push
    // again. A rule pushed before callers were kept counts until the next push, as every push replaced the last then. An
    // uploaded import counts until the next one of its source. Time alone retires nothing: a quiet pipeline isn't a gap.
    private static bool IsCurrent(DetectionRule r, LatestImports latest) =>
        CallerOf(r) is { } c ? r.LastSeenAtUtc >= latest.Callers[c] && !latest.Gone.Contains(c) : r.LastSeenAtUtc >= latest.Sources[r.Source];

    // One line per source; the API's adds up each caller's latest push.
    private static List<DetectionImportState> Imports(IReadOnlyList<DetectionRule> all, LatestImports latest) =>
        latest.Sources.OrderBy(l => l.Key).Select(l =>
        {
            var current = all.Where(r => r.Source == l.Key && IsCurrent(r, latest)).ToList();
            return new DetectionImportState(l.Key, l.Value, current.Count, current.Count(r => r.Enabled));
        }).ToList();

    private static async Task<List<DetectionRule>?> CurrentAsync(IAppDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        var all = await db.DetectionRules.AsNoTracking().ToListAsync(ct);
        if (all.Count == 0) return null;
        var latest = await LatestAsync(db, all, now, ct);
        return all.Where(r => IsCurrent(r, latest)).ToList();
    }

    /// <summary>Whether a rule tagged <paramref name="tag"/> counts for <paramref name="technique"/>: the same, its parent, or a sub-technique of it.</summary>
    public static bool Covers(string tag, string technique) =>
        string.Equals(tag, technique, StringComparison.OrdinalIgnoreCase)
        || string.Equals(tag, Parent(technique), StringComparison.OrdinalIgnoreCase)
        || string.Equals(Parent(tag), technique, StringComparison.OrdinalIgnoreCase);

    private static string Parent(string id) => id.IndexOf('.') is var dot and > 0 ? id[..dot] : id;

    private static TechniqueRules For(IReadOnlyList<DetectionRule> current, string technique)
    {
        var rules = current.Where(r => r.TechniqueList.Any(t => Covers(t, technique))).ToList();
        return new TechniqueRules(rules.Count(r => r.Enabled), rules.Count(r => !r.Enabled),
            rules.OrderByDescending(r => r.Enabled).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).Select(r => r.Name).ToList());
    }

    private void RequireRead()
    {
        if (!user.Has(Permission.WriteArticles)) throw new ForbiddenException("Only analysts see the detection rules.");
    }

    private void RequireAdmin()
    {
        if (!user.Has(Permission.Administer)) throw new ForbiddenException("Only an administrator can manage pushed detection rules.");
    }
}
