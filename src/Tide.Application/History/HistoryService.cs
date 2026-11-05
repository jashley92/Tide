using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Tide.Application.Abstractions;
using Tide.Application.Operations;
using Tide.Application.Security;
using Tide.Domain.Common;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.Application.History;

/// <summary>One field a change touched, as text for people: "Title: old → new". A null side is "(none)".</summary>
public sealed record HistoryChange(string Field, string? Before, string? After)
{
    /// <summary>Longest value shown before the "show" toggle.</summary>
    public const int ShortLength = 140;

    /// <summary>Whether either side is long enough to be cut short until it's asked for.</summary>
    public bool IsLong => Before is { Length: > ShortLength } || After is { Length: > ShortLength };

    /// <summary><paramref name="value"/> cut to <see cref="ShortLength"/>, marked when cut.</summary>
    public static string? Short(string? value) => value is { Length: > ShortLength } ? value[..ShortLength] + "…" : value;
}

/// <summary>
/// One save that changed an article or brief, newest first: who, when, what (<see cref="What"/>: "Created", "Changed",
/// "Deleted", or "Added a scorecard tag"), and the fields it touched.
/// </summary>
public sealed record HistoryEntry(Guid Id, DateTimeOffset AtUtc, string UserName, string What, IReadOnlyList<HistoryChange> Changes);

/// <summary>A page of history, whether there's more after it, and where the next page starts (rows of noise alone are
/// read but not shown, so it isn't the count of entries).</summary>
public sealed record HistoryPage(IReadOnlyList<HistoryEntry> Entries, bool HasMore, int Next);

/// <summary>
/// The change history of an article or a brief, read from the audit log (<see cref="AuditLogEntry"/>, written by the
/// audit interceptor on every save). Analysts only: the log holds field values at every marking, so it would show a
/// Reader or an action owner text the editions cut for them. An action has its own readable history (ActionEvent).
/// </summary>
public sealed class HistoryService(IAppDbContextFactory dbs, ICurrentUser user)
{
    /// <summary>How many entries a page holds.</summary>
    public const int PageSize = 50;

    /// <summary>
    /// An article's history: its own fields, and its scorecard tags, ATT&amp;CK techniques and CVEs as they were added
    /// and removed. Bullets, screenshots and indicators are rows of their own, keyed by their own ids, and aren't
    /// included.
    /// </summary>
    public async Task<HistoryPage> ArticleAsync(Guid id, int skip = 0, CancellationToken ct = default)
    {
        RequireAnalyst();
        var key = Key(id);
        var prefix = key + "/";
        await using var db = await dbs.CreateAsync(ct);
        var rows = await Page(db.AuditLog.AsNoTracking().Where(e =>
            (e.EntityType == nameof(Article) && e.EntityKey == key)
            || ((e.EntityType == nameof(ArticleTag) || e.EntityType == nameof(ArticleTechnique) || e.EntityType == nameof(ArticleCve))
                && e.EntityKey.StartsWith(prefix))), skip, ct);
        return Build(rows, skip, await TagNamesAsync(db, rows, ct));
    }

    /// <summary>A brief's history: its own fields (status, the opening note, meeting notes and times). Which articles
    /// are on it, and their order, are rows of their own and aren't included.</summary>
    public async Task<HistoryPage> BriefAsync(Guid id, int skip = 0, CancellationToken ct = default)
    {
        RequireAnalyst();
        var key = Key(id);
        await using var db = await dbs.CreateAsync(ct);
        var rows = await Page(db.AuditLog.AsNoTracking().Where(e => e.EntityType == nameof(Brief) && e.EntityKey == key), skip, ct);
        return Build(rows, skip, _ => null);
    }

    // The audit interceptor writes a Guid key with Convert.ToString, which is the "D" format.
    private static string Key(Guid id) => id.ToString("D", CultureInfo.InvariantCulture);

    // One more than a page, to know whether there's another. Newest first, and by id to keep a save's rows in one order.
    private static Task<List<AuditLogEntry>> Page(IQueryable<AuditLogEntry> q, int skip, CancellationToken ct) =>
        q.OrderByDescending(e => e.AtUtc).ThenBy(e => e.Id).Skip(Math.Max(0, skip)).Take(PageSize + 1).ToListAsync(ct);

    /// <summary>The names of the scorecard tags that <paramref name="rows"/> add or remove, by id, read in one query.</summary>
    internal static async Task<Func<Guid, string?>> TagNamesAsync(IAppDbContext db, List<AuditLogEntry> rows, CancellationToken ct)
    {
        var tagIds = rows.Where(r => r.EntityType == nameof(ArticleTag)).Select(r => Guid.TryParse(HistoryFormatter.Part(r.EntityKey), out var g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty).Distinct().ToList();
        var names = tagIds.Count == 0 ? new Dictionary<Guid, string>()
            : await db.ScorecardTags.AsNoTracking().Where(t => tagIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Name, ct);
        return id => names.GetValueOrDefault(id);
    }

    private static HistoryPage Build(List<AuditLogEntry> rows, int skip, Func<Guid, string?> tagName)
    {
        var entries = rows.Take(PageSize)
            .Select(r => HistoryFormatter.Entry(r, tagName))
            .OfType<HistoryEntry>().ToList();
        return new HistoryPage(entries, rows.Count > PageSize, Math.Max(0, skip) + Math.Min(rows.Count, PageSize));
    }

    private void RequireAnalyst()
    {
        if (!user.Has(Permission.WriteArticles)) throw new ForbiddenException("Only analysts can see the change history.");
    }
}

/// <summary>
/// Turns an audit row's before-and-after JSON (<c>{"Title": ["old", "new"]}</c>) into lines for people: friendly field
/// names, TLP markings and dates as the app writes them, and the fields that change on every save left out.
/// </summary>
public static class HistoryFormatter
{
    // Changing on every save, or bookkeeping no one reads in a history (the audit row already says who and when).
    private static readonly HashSet<string> Noise = new(StringComparer.Ordinal)
    {
        nameof(Article.EditVersion), nameof(AuditableEntity.ModifiedAtUtc), nameof(AuditableEntity.ModifiedBy),
        nameof(AuditableEntity.CreatedAtUtc), nameof(AuditableEntity.CreatedBy), nameof(Article.AuthorId), nameof(Brief.AnalystId),
        nameof(ArticleTag.ArticleId), nameof(ArticleTag.TagId), nameof(ArticleTechnique.TechniqueId), nameof(ArticleCve.CveId),
    };

    private static readonly Dictionary<string, string> Labels = new(StringComparer.Ordinal)
    {
        [nameof(Article.Title)] = "Title",
        [nameof(Article.SourceName)] = "Source",
        [nameof(Article.SourceUrl)] = "Link",
        [nameof(Article.PublishedAtUtc)] = "Published",
        [nameof(Article.Tlp)] = "Marking",
        [nameof(Article.Classification)] = "Classification",
        [nameof(Article.Priority)] = "Priority",
        [nameof(Article.Bluf)] = "Bottom line",
        [nameof(Article.WhyItMatters)] = "Why it matters",
        [nameof(Article.WhyItMattersTlp)] = "Why it matters marking",
        [nameof(Article.RecommendedActions)] = "Recommended actions",
        [nameof(Article.Actors)] = "Actors",
        [nameof(Article.Sectors)] = "Sectors",
        [nameof(Article.Status)] = "Status",
        [nameof(Article.StoryId)] = "Story",
        [nameof(Article.AuthorName)] = "Author",
        [nameof(Brief.BriefDate)] = "Date",
        [nameof(Brief.AnalystName)] = "Presenter",
        [nameof(Brief.Intro)] = "Opening note",
        [nameof(Brief.IntroTlp)] = "Opening note marking",
        [nameof(Brief.TeamNote)] = "Team opening note",
        [nameof(Brief.MeetingNotes)] = "Meeting notes",
        [nameof(Brief.MeetingHeldAtUtc)] = "Meeting held",
        [nameof(Brief.PresentedAtUtc)] = "Meeting ended",
    };

    // The order fields are listed in: the editor's, then any other by name.
    private static readonly Dictionary<string, int> Order =
        Labels.Keys.Select((k, i) => (k, i)).ToDictionary(x => x.k, x => x.i, StringComparer.Ordinal);

    // A link row is added or removed whole: what it names, by type.
    private static readonly Dictionary<string, string> Parts = new(StringComparer.Ordinal)
    {
        [nameof(ArticleTag)] = "scorecard tag",
        [nameof(ArticleTechnique)] = "technique",
        [nameof(ArticleCve)] = "CVE",
    };

    /// <summary>
    /// <paramref name="row"/> as a history entry, or null when nothing in it is worth showing (a change of noise fields
    /// only). <paramref name="tagName"/> names a scorecard tag by id, for a tag added or removed.
    /// </summary>
    public static HistoryEntry? Entry(AuditLogEntry row, Func<Guid, string?>? tagName = null)
    {
        if (Parts.TryGetValue(row.EntityType, out var part))
        {
            var value = Part(row.EntityKey);
            if (row.EntityType == nameof(ArticleTag) && Guid.TryParse(value, out var tagId)) value = tagName?.Invoke(tagId) ?? "a tag since removed";
            var what = row.Action == "Deleted" ? $"Removed a {part}" : $"Added a {part}";
            return new HistoryEntry(row.Id, row.AtUtc, row.UserName, what, [new HistoryChange(Capital(part), null, value)]);
        }
        var changes = Changes(row.ChangesJson);
        if (row.Action == "Modified" && changes.Count == 0) return null;
        var verb = row.Action switch { "Added" => "Created", "Deleted" => "Deleted", _ => "Changed" };
        return new HistoryEntry(row.Id, row.AtUtc, row.UserName, verb, row.Action == "Deleted" ? [] : changes);
    }

    /// <summary>The fields in an audit row's JSON, in a stable order (as the editor shows them), noise left out.</summary>
    public static IReadOnlyList<HistoryChange> Changes(string? json) => Changes(json, Noise, null);

    // The whole-system audit log also leaves out the other records' stamps (a setting's or a mapping's UpdatedAtUtc and
    // UpdatedBy) and the keys a row is found by (a feed item's address hash): the row says who and when.
    private static readonly HashSet<string> AuditNoise = new(Noise, StringComparer.Ordinal)
    {
        "UpdatedAtUtc", "UpdatedBy", "UrlHash", "TitleKey",
    };

    /// <summary>What a secret shows as in the audit log: whether it was set, never its value.</summary>
    public const string SecretSet = "(set)";

    /// <summary>What a secret that was set and then set again shows as, after the change.</summary>
    public const string SecretChanged = "(changed)";

    /// <summary>
    /// The fields an audit row changed, for the whole-system audit log: as <see cref="Changes(string?)"/> (a link row as
    /// what it names), with the record bookkeeping left out too, and whatever could hold a credential masked here, at
    /// display, whatever the row holds: a secret setting's value only as <see cref="SecretSet"/> or
    /// <see cref="SecretChanged"/>, a source's address without its key or sign-in, and an error's credentials hidden.
    /// </summary>
    public static IReadOnlyList<HistoryChange> AuditChanges(AuditLogEntry row, Func<Guid, string?>? tagName = null)
    {
        if (Parts.ContainsKey(row.EntityType)) return Entry(row, tagName)?.Changes ?? [];
        return Changes(row.ChangesJson, AuditNoise, Mask(row));
    }

    // How a row's values may be shown: a secret setting's as set or changed; an address or an error scrubbed. Null: as
    // they are.
    private static Func<string, string?, string?, (string?, string?)>? Mask(AuditLogEntry row) => row.EntityType switch
    {
        nameof(AppSetting) when SettingsCatalog.IsSecret(row.EntityKey) => (field, before, after) => field == nameof(AppSetting.Value)
            ? (Set(before), Set(before) is null || Set(after) is null ? Set(after) : SecretChanged)
            : (before, after),
        nameof(AppSetting) when SettingsCatalog.Find(row.EntityKey) is { Kind: not SettingKind.Plain } info => (field, before, after) =>
            field == nameof(AppSetting.Value) ? (SecretMask.Display(row.EntityKey, info.Kind, before), SecretMask.Display(row.EntityKey, info.Kind, after)) : (before, after),
        nameof(Source) => (field, before, after) => field switch
        {
            nameof(Source.Url) => (Address(before), Address(after)),
            nameof(Source.LastError) => (Scrubbed(before), Scrubbed(after)),
            _ => (before, after),
        },
        _ => null,
    };

    private static string? Set(string? value) => string.IsNullOrEmpty(value) ? null : SecretSet;

    private static string? Address(string? value) => string.IsNullOrEmpty(value) ? value : SecretMask.Url(value);

    private static string? Scrubbed(string? value) => string.IsNullOrEmpty(value) ? value : SecretMask.Scrub(value);

    private static IReadOnlyList<HistoryChange> Changes(string? json, HashSet<string> noise, Func<string, string?, string?, (string?, string?)>? mask)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        Dictionary<string, string?[]>? raw;
        try { raw = JsonSerializer.Deserialize<Dictionary<string, string?[]>>(json); }
        catch (JsonException) { return []; }
        if (raw is null) return [];
        return raw.Where(kv => !noise.Contains(kv.Key) && kv.Value is { Length: 2 })
            .OrderBy(kv => Order.TryGetValue(kv.Key, out var i) ? i : int.MaxValue).ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv =>
            {
                var (before, after) = mask is null ? (kv.Value[0], kv.Value[1]) : mask(kv.Key, kv.Value[0], kv.Value[1]);
                return new HistoryChange(Label(kv.Key), Value(kv.Key, before), Value(kv.Key, after));
            })
            .Where(c => c.Before != c.After)
            .ToList();
    }

    /// <summary>A field's name for people: the known ones by hand, any other split at its capitals ("FooBar" → "Foo bar"),
    /// without a time's "Utc" (its value says UTC).</summary>
    public static string Label(string field)
    {
        if (Labels.TryGetValue(field, out var label)) return label;
        if (field.Length > 3 && field.EndsWith("Utc", StringComparison.Ordinal)) field = field[..^3];
        var words = new System.Text.StringBuilder();
        foreach (var c in field)
        {
            if (char.IsUpper(c) && words.Length > 0) words.Append(' ').Append(char.ToLowerInvariant(c));
            else words.Append(c);
        }
        return words.ToString();
    }

    /// <summary>A stored value as the app writes it: a TLP as its marking, a time in UTC to the minute, a blank as none.</summary>
    public static string? Value(string field, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (field.EndsWith(nameof(Article.Tlp), StringComparison.Ordinal) && EnumNames.OrNull<TlpLevel>(value) is { } tlp) return Tlp.Label(tlp);
        if (field.EndsWith("Utc", StringComparison.Ordinal)
            && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
            return at.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
        if (field.EndsWith("Id", StringComparison.Ordinal) && Guid.TryParse(value, out _)) return "set";
        return value;
    }

    /// <summary>The second part of a link row's key ("articleId/T1566" → "T1566").</summary>
    public static string Part(string entityKey)
    {
        var slash = entityKey.IndexOf('/', StringComparison.Ordinal);
        return slash < 0 ? entityKey : entityKey[(slash + 1)..];
    }

    private static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
