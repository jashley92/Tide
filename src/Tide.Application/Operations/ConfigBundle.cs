using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tide.Application.Operations;

// A portable snapshot of what administrators set up in the app: for promoting a test
// install's setup to production, standing up another server like this one, or keeping a copy. It carries the settings
// changed in the app, the AD group mappings, the sources, the scorecard tags, the query templates and the holidays; never articles,
// briefs, actions, people or the audit trail, and never a secret: a source address that carries a key or a password
// travels with it hidden, and the source comes in turned off until its address is entered again. Enums travel as names.

/// <summary>A setting changed in the app (one of <see cref="SettingsCatalog.InApp"/>).</summary>
public sealed record BundleSetting(string Key, string Value);

public sealed record BundleRoleMapping(string AdGroup, string Role);

/// <summary>A source. <see cref="UrlHidden"/>: its address carried a key or a password, so <see cref="Url"/> has it hidden.
/// <see cref="ArticleFetch"/> and <see cref="ArticleBodySelector"/> are absent from bundles made before drafting help (the
/// source is then fetched by TIDE only, its body found automatically), and <see cref="ArticleRemoveSelector"/> from those
/// made before it was kept (nothing more is taken out).</summary>
public sealed record BundleSource(
    string Name, string Kind, string Url, string DefaultTlp, string Reliability, string? LicenseNote, int PollMinutes, bool Enabled,
    bool IsRetired, string? OnlyCategories, string? SkipCategories, string? OnlyTitleWords, string? SkipTitleWords,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool UrlHidden = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ArticleFetch = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ArticleBodySelector = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ArticleRemoveSelector = null);

public sealed record BundleScorecardTag(string Key, string Name, string? Description, int SortOrder, bool IsRetired);

public sealed record BundleScorecardCategory(string Key, string Name, string? Description, int SortOrder, bool IsRetired, IReadOnlyList<BundleScorecardTag> Tags);

public sealed record BundleQueryTemplate(string Dialect, string Kind, string Template);

/// <summary>A holiday (EP-82). Absent from bundles made before holidays were kept.</summary>
public sealed record BundleHoliday(DateOnly Date, string Name);

public sealed record ConfigBundle(
    string Format, int Version, DateTimeOffset ExportedAtUtc, string ExportedBy, string AppVersion,
    IReadOnlyList<BundleSetting> Settings, IReadOnlyList<BundleRoleMapping> RoleMappings, IReadOnlyList<BundleSource> Sources,
    IReadOnlyList<BundleScorecardCategory> Scorecard, IReadOnlyList<BundleQueryTemplate> QueryTemplates,
    IReadOnlyList<BundleHoliday>? Holidays = null)
{
    public const string FormatName = "tide-config-bundle";
    public const int CurrentVersion = 1;

    /// <summary>The largest bundle file read.</summary>
    public const int MaxBytes = 2 * 1024 * 1024;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>A bundle file read back. Throws <see cref="ArgumentException"/> when it isn't one this version reads.</summary>
    public static ConfigBundle Parse(byte[] content)
    {
        if (content.Length > MaxBytes) throw new ArgumentException($"A bundle is at most {MaxBytes / 1024 / 1024} MB.");
        ConfigBundle? bundle;
        try { bundle = JsonSerializer.Deserialize<ConfigBundle>(content, Json); }
        catch (JsonException) { throw new ArgumentException("That file isn't a TIDE configuration bundle (it isn't JSON of the right shape)."); }
        if (bundle is null || bundle.Format != FormatName) throw new ArgumentException("That file isn't a TIDE configuration bundle.");
        if (bundle.Version != CurrentVersion) throw new ArgumentException($"That bundle is version {bundle.Version}; this TIDE reads version {CurrentVersion}.");
        // A hand-edited file can hold a null where an item should be: it's left out, rather than failing the whole import.
        return bundle with
        {
            Settings = Items(bundle.Settings),
            RoleMappings = Items(bundle.RoleMappings), Sources = Items(bundle.Sources),
            Scorecard = [.. Items(bundle.Scorecard).Select(c => c with { Tags = Items(c.Tags) })],
            QueryTemplates = Items(bundle.QueryTemplates), Holidays = Items(bundle.Holidays),
        };
    }

    private static List<T> Items<T>(IReadOnlyList<T?>? items) where T : class => [.. (items ?? []).OfType<T>()];
}

public enum BundleChangeKind { Add, Update, Unchanged, Skipped }

/// <param name="Area">"Settings", "AD groups", "Sources", "Scorecard", "Query templates", "Holidays".</param>
/// <param name="Warning">The change needs a careful look before importing (it gives Admin, or a role to everyone);
/// <see cref="Detail"/> says why.</param>
public sealed record BundleChange(string Area, string Item, BundleChangeKind Kind, string? Detail = null, bool Warning = false);

/// <summary>What importing a bundle would do (or did), item by item.</summary>
public sealed record BundlePreview(ConfigBundle Bundle, IReadOnlyList<BundleChange> Changes)
{
    public int Count(BundleChangeKind kind) => Changes.Count(c => c.Kind == kind);

    public bool HasChanges => Changes.Any(c => c.Kind is BundleChangeKind.Add or BundleChangeKind.Update);

    /// <summary>The changes flagged for a careful look.</summary>
    public int Warnings => Changes.Count(c => c.Warning);
}
