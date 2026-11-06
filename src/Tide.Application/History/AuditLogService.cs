using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Tide.Application.Abstractions;
using Tide.Application.Content;
using Tide.Application.Operations;
using Tide.Application.Org;
using Tide.Application.Security;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.Application.History;

/// <summary>What a save did to a record, as the audit log lists it.</summary>
public enum AuditAction
{
    Created,
    Changed,
    Deleted,
}

/// <summary>A filter in force on the audit log, and the filter without it (its chip's remove link).</summary>
public sealed record AuditChip(string Label, AuditFilter Without);

/// <summary>
/// What the audit log narrows to, all of it in the address (<see cref="ToQuery"/>, <see cref="FromQuery"/>) so a view
/// can be bookmarked. Every filter holds at once. A record (<see cref="Key"/>) is one of a kind, so it needs
/// <see cref="Kind"/>; the dates are days in the organization's time zone, both ends included.
/// </summary>
public sealed record AuditFilter
{
    /// <summary>Who saved it: the user id the log keeps ("system" for the background jobs).</summary>
    public string? Person { get; init; }

    /// <summary>The kind of record, by its type's name ("Article", "AppSetting").</summary>
    public string? Kind { get; init; }

    /// <summary>One record of <see cref="Kind"/>, by its key as the log holds it.</summary>
    public string? Key { get; init; }

    public DateOnly? From { get; init; }
    public DateOnly? To { get; init; }
    public AuditAction? Action { get; init; }

    /// <summary>Words looked for in the changed values (and a record's key).</summary>
    public string? Search { get; init; }

    /// <summary>Whether anything other than the words narrows the log.</summary>
    public bool Narrowed => Person is not null || Kind is not null || From is not null || To is not null || Action is not null;

    /// <summary>
    /// Whether a filter the log has no index for is in force (the action, the words) without a person to narrow it: those
    /// are read over a bounded window of days (<see cref="AuditLogService.UnindexedDays"/>), never the whole log. The
    /// person has an index (<c>UserId, AtUtc</c>), so with one chosen any range is read, over that person's rows only.
    /// </summary>
    public bool Unindexed => Person is null && (Action is not null || !string.IsNullOrWhiteSpace(Search));

    /// <summary>The same words, and nothing else.</summary>
    public AuditFilter Cleared() => new() { Search = Search };

    private static string Day(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>The address's query: every filter set, by name; null for the rest (so they drop out of a link).</summary>
    public IReadOnlyDictionary<string, object?> ToQuery() => new Dictionary<string, object?>
    {
        ["q"] = string.IsNullOrWhiteSpace(Search) ? null : Search.Trim(),
        ["who"] = Person,
        ["kind"] = Kind,
        ["key"] = Kind is null ? null : Key,
        ["from"] = From is { } f ? Day(f) : null,
        ["to"] = To is { } t ? Day(t) : null,
        ["action"] = Action?.ToString().ToLowerInvariant(),
    };

    /// <summary>Reads <see cref="ToQuery"/>'s names back; anything it doesn't recognise is left out.</summary>
    public static AuditFilter FromQuery(Func<string, string?> get)
    {
        static string? Text(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim() is var t && t.Length <= max ? t : t[..max];
        static DateOnly? Date(string? s) =>
            DateOnly.TryParseExact(s?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
        var kind = Text(get("kind"), 64);
        return new AuditFilter
        {
            Search = Text(get("q"), 200),
            Person = Text(get("who"), 256),
            Kind = kind,
            Key = kind is null ? null : Text(get("key"), 100),
            From = Date(get("from")),
            To = Date(get("to")),
            Action = Domain.Common.EnumNames.TryParse<AuditAction>(get("action"), out var a, ignoreCase: true) ? a : null,
        };
    }

    /// <summary>
    /// A chip for each filter other than the words (the filter box shows those). <paramref name="personName"/> names a
    /// user id and <paramref name="recordName"/> the record, else the id or key is shown.
    /// </summary>
    public IReadOnlyList<AuditChip> Chips(Func<string, string?> personName, string? recordName)
    {
        var chips = new List<AuditChip>();
        if (Person is { } p) chips.Add(new("By: " + (personName(p) ?? p), this with { Person = null }));
        if (Kind is { } k)
        {
            chips.Add(new("Kind: " + AuditKinds.Name(k), this with { Kind = null, Key = null }));
            if (Key is { } key) chips.Add(new("Only: " + (recordName ?? key), this with { Key = null }));
        }
        if (Action is { } a) chips.Add(new("Action: " + a.ToString().ToLowerInvariant(), this with { Action = null }));
        if (From is not null || To is not null)
        {
            static string Long(DateOnly d) => d.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
            var label = (From, To) switch
            {
                ({ } f, { } t) => $"{Long(f)} to {Long(t)}",
                ({ } f, null) => "From " + Long(f),
                (null, { } t) => "Up to " + Long(t),
                _ => "",
            };
            chips.Add(new(label, this with { From = null, To = null }));
        }
        return chips;
    }
}

/// <summary>The kinds of record the audit log holds, by a name for people ("Role mapping" for AdGroupRoleMapping).</summary>
public static class AuditKinds
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.Ordinal)
    {
        [nameof(Article)] = "Article",
        [nameof(ArticleBullet)] = "Article bullet",
        [nameof(ArticleScreenshot)] = "Screenshot",
        [nameof(ArticleTag)] = "Article scorecard tag",
        [nameof(ArticleAdversary)] = "Article threat actor",
        [nameof(ArticleProduct)] = "Article product we run",
        [nameof(ArticleTechnique)] = "Article technique",
        [nameof(ArticleIndicator)] = "Indicator",
        [nameof(ArticleCve)] = "Article CVE",
        [nameof(ArticleLink)] = "Related link",
        [nameof(Brief)] = "Brief",
        [nameof(BriefItem)] = "Article on a brief",
        [nameof(BriefSend)] = "Brief send",
        [nameof(BriefSendImage)] = "Sent image",
        [nameof(BriefCorrection)] = "Correction",
        [nameof(ActionItem)] = "Action",
        [nameof(ActionComment)] = "Action comment",
        [nameof(ActionEvent)] = "Action event",
        [nameof(ActionCommentFile)] = "Comment attachment",
        [nameof(ActionFile)] = "Action file",
        [nameof(ActionChecklistItem)] = "Checklist item",
        [nameof(HuntQuery)] = "Hunt query",
        [nameof(ActionIndicator)] = "Action indicator",
        [nameof(Playbook)] = "Playbook",
        [nameof(PlaybookStep)] = "Playbook step",
        [nameof(ActionCardRule)] = "Card rule",
        [nameof(QueryTemplate)] = "Query template",
        [nameof(SavedQuery)] = "Saved query",
        [nameof(OutreachTemplate)] = "Outreach template",
        [nameof(OutreachMessage)] = "Outreach email",
        [nameof(Story)] = "Story",
        [nameof(TriageSplit)] = "Triage split",
        [nameof(Source)] = "Source",
        [nameof(Team)] = "Team",
        [nameof(TeamMember)] = "Team member",
        [nameof(AppSetting)] = "Setting",
        [nameof(AdGroupRoleMapping)] = "Role mapping",
        [nameof(ScorecardCategory)] = "Scorecard category",
        [nameof(ScorecardTag)] = "Scorecard tag",
        [nameof(MetricGroup)] = "Metric group",
        [nameof(MetricDefinition)] = "Metric",
        [nameof(MetricValue)] = "Metric value",
        [nameof(MetricImport)] = "Metric import",
        [nameof(RotationMember)] = "Rotation member",
        [nameof(RotationWeek)] = "Rotation week",
        [nameof(RotationChange)] = "Rotation change",
        [nameof(RotationOverride)] = "Rotation cover",
        [nameof(StoredFile)] = "File",
    };

    /// <summary>Every kind, by name for people, as the kind filter offers them.</summary>
    public static readonly IReadOnlyList<(string Type, string Name)> All =
        [.. Names.OrderBy(kv => kv.Value, StringComparer.Ordinal).Select(kv => (kv.Key, kv.Value))];

    /// <summary>A type's name for people; one not listed split at its capitals ("FooBar" → "Foo bar").</summary>
    public static string Name(string entityType) => Names.TryGetValue(entityType, out var n) ? n : HistoryFormatter.Label(entityType);
}

/// <summary>
/// One save to one record, for the audit log: when, who, what it did, the kind of record and which one (its title or
/// name where it was cheap to find, else its key), the page it's on (null when there's none, or it's gone), and the
/// fields it changed, with secrets masked (<see cref="HistoryFormatter.AuditChanges"/>).
/// </summary>
public sealed record AuditRow(Guid Id, DateTimeOffset AtUtc, string UserId, string UserName, AuditAction Action, string EntityType,
    string EntityKey, string Kind, string Record, string? Href, IReadOnlyList<HistoryChange> Changes)
{
    /// <summary>The action as a word ("created").</summary>
    public string What => Action.ToString().ToLowerInvariant();
}

/// <summary>Rows of the audit log, newest first; how many match; and, when the filters were read over a window of days
/// the person didn't choose, the first day of it.</summary>
public sealed record AuditPage(IReadOnlyList<AuditRow> Rows, int Total, DateOnly? Since);

/// <summary>Someone the log can be narrowed to.</summary>
public sealed record AuditPerson(string UserId, string Name);

/// <summary>
/// Administration › System › Audit log: every change the audit interceptor recorded, across the whole system, newest
/// first. Administrators only (the page's policy, and here): the log holds field values at every marking.
/// <para>
/// The log is indexed by when (<c>AtUtc</c>), by record (<c>EntityType, EntityKey</c>) and by person (<c>UserId,
/// AtUtc</c>). The kind, record and person filters use those; the action and the words have no index, so without a
/// person they're read over at most <see cref="UnindexedDays"/> days (the chosen dates, cut to that, or the days up to
/// today), never the whole log.
/// </para>
/// </summary>
public sealed class AuditLogService(IAppDbContextFactory dbs, ICurrentUser user, OrgClock clock)
{
    public const int PageSize = 50;

    /// <summary>The most rows one read returns.</summary>
    public const int MaxPage = 1000;

    /// <summary>The most rows an export holds.</summary>
    public const int MaxExport = 10_000;

    /// <summary>The widest window, in days, the filters without an index are read over.</summary>
    public const int UnindexedDays = 92;

    /// <summary>The user id the log keeps for the background jobs.</summary>
    public const string SystemUser = "system";

    /// <summary>Rows matching <paramref name="filter"/>, newest first: <paramref name="take"/> of them after the first
    /// <paramref name="skip"/> (a page), and how many match.</summary>
    public async Task<AuditPage> PageAsync(AuditFilter filter, int take = PageSize, int skip = 0, CancellationToken ct = default)
    {
        RequireAdministrator();
        await using var db = await dbs.CreateAsync(ct);
        var (query, since) = await QueryAsync(db, filter, ct);
        var total = await query.CountAsync(ct);
        var rows = await Newest(query).Skip(Math.Max(0, skip)).Take(Math.Clamp(take, 1, MaxPage)).ToListAsync(ct);
        return new AuditPage(await DescribeAsync(db, rows, ct), total, since);
    }

    /// <summary>The rows matching <paramref name="filter"/> for an export, newest first, at most <see cref="MaxExport"/>.</summary>
    public async Task<IReadOnlyList<AuditRow>> ExportAsync(AuditFilter filter, CancellationToken ct = default)
    {
        RequireAdministrator();
        await using var db = await dbs.CreateAsync(ct);
        var (query, _) = await QueryAsync(db, filter, ct);
        return await DescribeAsync(db, await Newest(query).Take(MaxExport).ToListAsync(ct), ct);
    }

    /// <summary>The people the log can be narrowed to: everyone who has signed in, and the background jobs.</summary>
    public async Task<IReadOnlyList<AuditPerson>> PeopleAsync(CancellationToken ct = default)
    {
        RequireAdministrator();
        await using var db = await dbs.CreateAsync(ct);
        var people = await db.Users.AsNoTracking().Select(u => new AuditPerson(u.Sid, u.DisplayName)).ToListAsync(ct);
        return [.. people.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase), new AuditPerson(SystemUser, "System (background jobs)")];
    }

    /// <summary>
    /// The days <paramref name="filter"/> is read over, in the organization's zone: its own, except that a filter without
    /// an index is held to <see cref="UnindexedDays"/> ending on its last day (or today). <c>Since</c> is the first day
    /// when that cut it short.
    /// </summary>
    public (DateOnly? From, DateOnly? To, DateOnly? Since) Window(AuditFilter filter)
    {
        var (from, to) = (filter.From, filter.To);
        if (!filter.Unindexed) return (from, to, null);
        var earliest = (to ?? clock.Today).AddDays(-(UnindexedDays - 1));
        return from is { } f && f >= earliest ? (from, to, null) : (earliest, to, earliest);
    }

    private static IQueryable<AuditLogEntry> Newest(IQueryable<AuditLogEntry> q) => q.OrderByDescending(e => e.AtUtc).ThenBy(e => e.Id);

    private async Task<(IQueryable<AuditLogEntry> Query, DateOnly? Since)> QueryAsync(IAppDbContext db, AuditFilter filter, CancellationToken ct)
    {
        var q = db.AuditLog.AsNoTracking();
        if (filter.Kind is { } kind)
        {
            q = q.Where(e => e.EntityType == kind);
            if (filter.Key is { } key) q = q.Where(e => e.EntityKey == key);
        }
        var (from, to, since) = Window(filter);
        if (from is { } f)
        {
            var start = clock.StartOfDay(f);
            q = q.Where(e => e.AtUtc >= start);
        }
        if (to is { } t)
        {
            var end = clock.StartOfDay(t.AddDays(1));
            q = q.Where(e => e.AtUtc < end);
        }
        if (filter.Person is { } person) q = q.Where(e => e.UserId == person);
        if (filter.Action is { } action)
        {
            var stored = Stored(action);
            q = q.Where(e => e.Action == stored);
        }
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            // The values are kept as JSON, so the words are looked for as JSON writes them (an apostrophe is '). A
            // secret setting's row is matched by its key only: its value is masked when it's written and shown, and a
            // match on it would tell what it is.
            var pattern = "%" + Escape(JsonText(filter.Search.Trim())) + "%";
            var settingType = nameof(AppSetting);
            var settingKeys = await db.AuditLog.AsNoTracking().Where(e => e.EntityType == settingType).Select(e => e.EntityKey).Distinct().ToListAsync(ct);
            var secret = settingKeys.Where(SettingsCatalog.IsSecret).ToList();
            q = q.Where(e => EF.Functions.Like(e.EntityKey, pattern, LikeEscape)
                             || (e.ChangesJson != null && EF.Functions.Like(e.ChangesJson, pattern, LikeEscape)
                                 && !(e.EntityType == settingType && secret.Contains(e.EntityKey))));
        }
        return (q, since);
    }

    private const string LikeEscape = "!";

    private static string Stored(AuditAction action) => action switch
    {
        AuditAction.Created => "Added",
        AuditAction.Deleted => "Deleted",
        _ => "Modified",
    };

    private static AuditAction Read(string stored) => stored switch
    {
        "Added" => AuditAction.Created,
        "Deleted" => AuditAction.Deleted,
        _ => AuditAction.Changed,
    };

    // The words as the audit interceptor's JSON holds them, without the quotes.
    private static string JsonText(string text) => JsonSerializer.Serialize(text)[1..^1];

    /// <summary><paramref name="text"/> as a LIKE pattern's literal text, with <c>!</c> as the escape (SQL Server's
    /// brackets too).</summary>
    public static string Escape(string text) =>
        text.Replace("!", "!!", StringComparison.Ordinal).Replace("%", "!%", StringComparison.Ordinal)
            .Replace("_", "!_", StringComparison.Ordinal).Replace("[", "![", StringComparison.Ordinal);

    // The rows for people: the records named in a few queries for the whole page (one per kind of record on it, and one
    // for the scorecard tags), never one per row.
    private static async Task<IReadOnlyList<AuditRow>> DescribeAsync(IAppDbContext db, List<AuditLogEntry> rows, CancellationToken ct)
    {
        var records = await AuditRecords.NameAsync(db, rows, ct);
        var tagName = await HistoryService.TagNamesAsync(db, rows, ct);
        return rows.Select(r =>
        {
            var (name, href) = records[r.Id];
            return new AuditRow(r.Id, r.AtUtc, r.UserId, r.UserName, Read(r.Action), r.EntityType, r.EntityKey, AuditKinds.Name(r.EntityType),
                name, href, HistoryFormatter.AuditChanges(r, tagName));
        }).ToList();
    }

    private void RequireAdministrator()
    {
        if (!user.Has(Permission.Administer)) throw new ForbiddenException("Only administrators can read the audit log.");
    }
}

/// <summary>
/// Which record each audit row is about, for people: its title or name and its page, found for a whole page of rows in
/// one query per kind of record. A part of something (an article's bullet, an action's comment) is named by what it's
/// part of. A record that's gone is named from its own row's values where they hold a title or name, else by its key.
/// </summary>
internal static class AuditRecords
{
    // A part kept in a table of its own, by its own id: what it's part of, and the field its row records that in.
    private static readonly Dictionary<string, (string Parent, string Field)> Children = new(StringComparer.Ordinal)
    {
        [nameof(ArticleBullet)] = (nameof(Article), nameof(ArticleBullet.ArticleId)),
        [nameof(ArticleScreenshot)] = (nameof(Article), nameof(ArticleScreenshot.ArticleId)),
        [nameof(ArticleIndicator)] = (nameof(Article), nameof(ArticleIndicator.ArticleId)),
        [nameof(ArticleLink)] = (nameof(Article), nameof(ArticleLink.ArticleId)),
        [nameof(BriefItem)] = (nameof(Brief), nameof(BriefItem.BriefId)),
        [nameof(BriefSend)] = (nameof(Brief), nameof(BriefSend.BriefId)),
        [nameof(BriefCorrection)] = (nameof(Brief), nameof(BriefCorrection.BriefId)),
        [nameof(ActionEvent)] = (nameof(ActionItem), nameof(ActionEvent.ActionId)),
        [nameof(ActionComment)] = (nameof(ActionItem), nameof(ActionComment.ActionId)),
        [nameof(ActionChecklistItem)] = (nameof(ActionItem), nameof(ActionChecklistItem.ActionId)),
        [nameof(ActionFile)] = (nameof(ActionItem), nameof(ActionFile.ActionId)),
        [nameof(HuntQuery)] = (nameof(ActionItem), nameof(HuntQuery.ActionId)),
        [nameof(ActionIndicator)] = (nameof(ActionItem), nameof(ActionIndicator.ActionId)),
        [nameof(OutreachMessage)] = (nameof(ActionItem), nameof(OutreachMessage.ActionId)),
        [nameof(PlaybookStep)] = (nameof(Playbook), nameof(PlaybookStep.PlaybookId)),
        [nameof(MetricValue)] = (nameof(MetricDefinition), nameof(MetricValue.DefinitionId)),
    };

    // A link row, keyed "parent/other": what the first part is.
    private static readonly Dictionary<string, string> Links = new(StringComparer.Ordinal)
    {
        [nameof(ArticleTag)] = nameof(Article),
        [nameof(ArticleAdversary)] = nameof(Article),
        [nameof(ArticleProduct)] = nameof(Article),
        [nameof(ArticleTechnique)] = nameof(Article),
        [nameof(ArticleCve)] = nameof(Article),
        [nameof(TeamMember)] = nameof(Team),
    };

    // The fields a row's own values name it by, when it's gone.
    private static readonly string[] NameFields = ["Title", "Name", nameof(AdGroupRoleMapping.AdGroup)];

    public static async Task<Dictionary<Guid, (string Name, string? Href)>> NameAsync(IAppDbContext db, List<AuditLogEntry> rows, CancellationToken ct)
    {
        var target = new Dictionary<Guid, (string Type, Guid Id)>();
        foreach (var r in rows)
            if (Target(r) is { } t) target[r.Id] = t;

        // A part whose row doesn't say what it's part of (a change to a bullet's text): read from its table, one query
        // per kind.
        foreach (var group in rows.Where(r => Children.ContainsKey(r.EntityType) && !target.ContainsKey(r.Id)).GroupBy(r => r.EntityType))
        {
            var parents = await ParentsAsync(db, group.Key, group.Select(r => Id(r.EntityKey)).Distinct().ToList(), ct);
            foreach (var r in group.Where(r => parents.ContainsKey(Id(r.EntityKey))))
                target[r.Id] = (Children[group.Key].Parent, parents[Id(r.EntityKey)]);
        }

        var names = new Dictionary<(string, Guid), (string Name, string? Href)>();
        foreach (var group in target.Values.Distinct().GroupBy(t => t.Type))
        {
            var found = await NamesAsync(db, group.Key, group.Select(t => t.Id).ToList(), ct);
            foreach (var (id, name) in found) names[(group.Key, id)] = name;
        }

        return rows.ToDictionary(r => r.Id, r => target.TryGetValue(r.Id, out var t) && names.TryGetValue(t, out var named) ? named : Unresolved(r));
    }

    private static Guid Id(string key) => Guid.TryParse(key, out var g) ? g : Guid.Empty;

    // A row's record, or what it's part of, as (kind, id), where the row itself says: null for a part whose row doesn't.
    private static (string Type, Guid Id)? Target(AuditLogEntry r)
    {
        if (Links.TryGetValue(r.EntityType, out var parent))
            return Guid.TryParse(r.EntityKey.Split('/')[0], out var pid) ? (parent, pid) : null;
        if (Children.TryGetValue(r.EntityType, out var child))
            return Field(r.ChangesJson, child.Field) is { } raw && Guid.TryParse(raw, out var pid) ? (child.Parent, pid) : null;
        return Guid.TryParse(r.EntityKey, out var id) ? (r.EntityType, id) : null;
    }

    // A row whose record wasn't found: a setting by the catalogue, a record that's gone by its own values, else its key.
    private static (string, string?) Unresolved(AuditLogEntry r)
    {
        if (r.EntityType == nameof(AppSetting)) return Setting(r.EntityKey);
        if (!Children.ContainsKey(r.EntityType) && !Links.ContainsKey(r.EntityType) && OwnName(r.ChangesJson) is { } own) return (own, null);
        return (r.EntityKey, null);
    }

    private static (string, string?) Setting(string key) =>
        SettingsCatalog.Find(key) is { } info ? ($"{info.Group}: {info.Label}", info.EditedIn) : (key, null);

    private static string? OwnName(string? json)
    {
        foreach (var field in NameFields)
        {
            var values = Values(json, field);
            if (values is { Length: 2 } && (values[1] ?? values[0]) is { Length: > 0 } name) return name;
        }
        return null;
    }

    // The value a row recorded for one field: the new one, or the old one on a delete.
    private static string? Field(string? json, string field) => Values(json, field) is { Length: 2 } v ? v[1] ?? v[0] : null;

    private static string?[]? Values(string? json, string field)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<Dictionary<string, string?[]>>(json)?.GetValueOrDefault(field); }
        catch (JsonException) { return null; }
    }

    private static async Task<Dictionary<Guid, Guid>> ParentsAsync(IAppDbContext db, string type, List<Guid> ids, CancellationToken ct) => type switch
    {
        nameof(ArticleBullet) => await db.ArticleBullets.AsNoTracking().Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.ArticleId, ct),
        nameof(ArticleScreenshot) => await db.ArticleScreenshots.AsNoTracking().Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.ArticleId, ct),
        nameof(ArticleIndicator) => await db.ArticleIndicators.AsNoTracking().Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.ArticleId, ct),
        nameof(ArticleLink) => await db.ArticleLinks.AsNoTracking().Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.ArticleId, ct),
        nameof(BriefItem) => await db.BriefItems.AsNoTracking().Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.BriefId, ct),
        nameof(BriefSend) => await db.BriefSends.AsNoTracking().Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.BriefId, ct),
        nameof(BriefCorrection) => await db.BriefCorrections.AsNoTracking().Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.BriefId, ct),
        nameof(ActionEvent) => await db.ActionEvents.AsNoTracking().Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.ActionId, ct),
        nameof(ActionComment) => await db.ActionComments.AsNoTracking().Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.ActionId, ct),
        nameof(ActionFile) => await db.ActionFiles.AsNoTracking().Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.ActionId, ct),
        nameof(ActionChecklistItem) => await db.ActionChecklistItems.AsNoTracking().Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.ActionId, ct),
        nameof(HuntQuery) => await db.HuntQueries.AsNoTracking().Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.ActionId, ct),
        nameof(ActionIndicator) => await db.ActionIndicators.AsNoTracking().Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.ActionId, ct),
        nameof(OutreachMessage) => await db.OutreachMessages.AsNoTracking().Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.ActionId, ct),
        nameof(PlaybookStep) => await db.PlaybookSteps.AsNoTracking().Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.PlaybookId, ct),
        nameof(MetricValue) => await db.MetricValues.AsNoTracking().Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.DefinitionId, ct),
        _ => [],
    };

    private static string BriefName(DateOnly d) => "Brief for " + d.ToString("ddd MMM d, yyyy", CultureInfo.InvariantCulture);

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // The records of one kind, by id: a name for people and the page it's on. A kind not listed here is named by its key.
    private static async Task<Dictionary<Guid, (string Name, string? Href)>> NamesAsync(IAppDbContext db, string type, List<Guid> ids, CancellationToken ct)
    {
        switch (type)
        {
            case nameof(Article):
                return (await db.Articles.AsNoTracking().Where(x => ids.Contains(x.Id)).Select(x => new { x.Id, x.Title }).ToListAsync(ct))
                    .ToDictionary(x => x.Id, x => (x.Title, (string?)$"articles/{x.Id}"));
            case nameof(Brief):
                return (await db.Briefs.AsNoTracking().Where(x => ids.Contains(x.Id)).Select(x => new { x.Id, x.BriefDate }).ToListAsync(ct))
                    .ToDictionary(x => x.Id, x => (BriefName(x.BriefDate), (string?)("briefs/" + Iso(x.BriefDate))));
            case nameof(ActionItem):
                return (await db.Actions.AsNoTracking().Where(x => ids.Contains(x.Id)).Select(x => new { x.Id, x.Number, x.Title }).ToListAsync(ct))
                    .ToDictionary(x => x.Id, x => ($"A-{x.Number} {x.Title}", (string?)$"actions/{x.Number}"));
            case nameof(Story):
                return (await db.Stories.AsNoTracking().Where(x => ids.Contains(x.Id)).Select(x => new { x.Id, x.Name }).ToListAsync(ct))
                    .ToDictionary(x => x.Id, x => (x.Name, (string?)$"stories/{x.Id}"));
            case nameof(Source):
                return Named(await db.Sources.AsNoTracking().Where(x => ids.Contains(x.Id)).Select(x => new { x.Id, x.Name }).ToListAsync(ct), x => x.Id, x => x.Name, "admin/sources");
            case nameof(Team):
                return Named(await db.Teams.AsNoTracking().Where(x => ids.Contains(x.Id)).Select(x => new { x.Id, x.Name }).ToListAsync(ct), x => x.Id, x => x.Name, "admin/teams");
            case nameof(ScorecardTag):
                return Named(await db.ScorecardTags.AsNoTracking().Where(x => ids.Contains(x.Id)).Select(x => new { x.Id, x.Name }).ToListAsync(ct), x => x.Id, x => x.Name, "admin/scorecard");
            case nameof(ScorecardCategory):
                return Named(await db.ScorecardCategories.AsNoTracking().Where(x => ids.Contains(x.Id)).Select(x => new { x.Id, x.Name }).ToListAsync(ct), x => x.Id, x => x.Name, "admin/scorecard");
            case nameof(Playbook):
                return Named(await db.Playbooks.AsNoTracking().Where(x => ids.Contains(x.Id)).Select(x => new { x.Id, x.Name }).ToListAsync(ct), x => x.Id, x => x.Name, "admin/playbooks");
            case nameof(SavedQuery):
                return Named(await db.SavedQueries.AsNoTracking().Where(x => ids.Contains(x.Id)).Select(x => new { x.Id, x.Name }).ToListAsync(ct), x => x.Id, x => x.Name, "hunts/saved");
            case nameof(OutreachTemplate):
                return Named(await db.OutreachTemplates.AsNoTracking().Where(x => ids.Contains(x.Id)).Select(x => new { x.Id, x.Name }).ToListAsync(ct), x => x.Id, x => x.Name, "outreach/templates");
            case nameof(ActionCardRule):
                return Named(await db.ActionCardRules.AsNoTracking().Where(x => ids.Contains(x.Id)).Select(x => new { x.Id, x.Name }).ToListAsync(ct), x => x.Id, x => x.Name, "admin/card-rules");
            case nameof(MetricDefinition):
                return Named(await db.MetricDefinitions.AsNoTracking().Where(x => ids.Contains(x.Id)).Select(x => new { x.Id, x.Name }).ToListAsync(ct), x => x.Id, x => x.Name, "admin/metrics");
            case nameof(MetricGroup):
                return Named(await db.MetricGroups.AsNoTracking().Where(x => ids.Contains(x.Id)).Select(x => new { x.Id, x.Name }).ToListAsync(ct), x => x.Id, x => x.Name, "admin/metrics");
            case nameof(AdGroupRoleMapping):
                return (await db.RoleMappings.AsNoTracking().Where(x => ids.Contains(x.Id)).Select(x => new { x.Id, x.AdGroup, x.RoleName }).ToListAsync(ct))
                    .ToDictionary(x => x.Id, x => ($"{x.AdGroup} as {x.RoleName}", (string?)"admin/access"));
            default:
                return [];
        }
    }

    private static Dictionary<Guid, (string, string?)> Named<T>(List<T> rows, Func<T, Guid> id, Func<T, string> name, string href) =>
        rows.ToDictionary(id, x => (name(x), (string?)href));
}

/// <summary>The audit log as CSV: one line per field a save changed (a save that changed none still has its line), with
/// any cell a spreadsheet would read as a formula neutralised (<see cref="Csv.Cell"/>).</summary>
public static class AuditCsv
{
    public const string Header = "when,when_utc,who,user_id,action,kind,record,key,field,before,after";

    public static string Write(IEnumerable<AuditRow> rows, OrgClock clock)
    {
        var sb = new StringBuilder(Header).Append("\r\n");
        foreach (var r in rows)
        {
            var when = clock.ToLocal(r.AtUtc).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            var utc = r.AtUtc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
            var changes = r.Changes.Count == 0 ? [null] : r.Changes.Cast<HistoryChange?>();
            foreach (var c in changes)
            {
                sb.AppendJoin(',', Csv.Cell(when), Csv.Cell(utc), Csv.Cell(r.UserName), Csv.Cell(r.UserId), Csv.Cell(r.What), Csv.Cell(r.Kind),
                    Csv.Cell(r.Record), Csv.Cell(r.EntityKey), Csv.Cell(c?.Field), Csv.Cell(c?.Before), Csv.Cell(c?.After));
                sb.Append("\r\n");
            }
        }
        return sb.ToString();
    }
}
