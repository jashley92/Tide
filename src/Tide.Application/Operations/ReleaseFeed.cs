using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Tide.Application.Operations;

/// <summary>One published release.</summary>
/// <param name="Notes">The release body, as Markdown (capped). Empty when it has none.</param>
/// <param name="BundleUrl">The release bundle (tide-vX.Y.Z.zip), when attached.</param>
/// <param name="Sha256Url">Its .sha256, when attached.</param>
public sealed record AvailableRelease(
    string Tag, ReleaseVersion Version, DateTimeOffset? PublishedAtUtc, string Notes, string PageUrl, string? BundleUrl, string? Sha256Url);

/// <summary>
/// Reads the GitHub Releases API response (<c>GET /repos/{owner}/{repo}/releases</c>) for the update check.
/// The response comes from outside, so it's read strictly: drafts are skipped, only the tags the release workflow
/// makes count, the notes are capped, and a link is kept only when it points at this repository's own release pages.
/// Pre-releases count only when <c>includePrereleases</c> (a server running a pre-release follows them; one running a
/// release doesn't).
/// </summary>
public static partial class ReleaseFeed
{
    public const int MaxNotesChars = 32 * 1024;

    [GeneratedRegex(@"^v\d{1,4}\.\d{1,4}\.\d{1,4}(-(alpha|beta|rc)\.\d{1,4})?$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ReleaseTag();

    [GeneratedRegex(@"^[A-Za-z0-9-]{1,39}/[A-Za-z0-9._-]{1,100}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex RepositoryName();

    /// <summary>True for an "owner/name" repository.</summary>
    public static bool IsRepository(string? repository) => !string.IsNullOrWhiteSpace(repository) && RepositoryName().IsMatch(repository);

    /// <summary>The API address that lists the repository's releases, newest first.</summary>
    public static string ApiUrl(string repository) => $"https://api.github.com/repos/{repository}/releases?per_page=30";

    /// <summary>
    /// The published releases newer than <paramref name="current"/> (all of them when it's null), newest first. Throws
    /// <see cref="JsonException"/> when the response isn't a list of releases.
    /// </summary>
    public static IReadOnlyList<AvailableRelease> Newer(string json, string repository, ReleaseVersion? current, bool includePrereleases)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new JsonException("The releases response isn't a list.");

        var pages = $"https://github.com/{repository}/releases/";
        var found = new Dictionary<ReleaseVersion, AvailableRelease>();
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            if (r.ValueKind != JsonValueKind.Object || Bool(r, "draft")) continue;
            if (Bool(r, "prerelease") && !includePrereleases) continue;

            var tag = Str(r, "tag_name");
            if (tag is null || !ReleaseTag().IsMatch(tag) || ReleaseVersion.Parse(tag) is not { } version) continue;
            if (version.IsPrerelease && !includePrereleases) continue;
            if ((current is not null && version <= current) || found.ContainsKey(version)) continue;

            var page = Str(r, "html_url");
            if (page is null || !page.StartsWith(pages, StringComparison.Ordinal)) page = pages + "tag/" + tag;

            var notes = Str(r, "body") ?? "";
            if (notes.Length > MaxNotesChars) notes = notes[..MaxNotesChars] + "\n\n…";

            DateTimeOffset? published = DateTimeOffset.TryParse(Str(r, "published_at"), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var p) ? p : null;

            var downloads = pages + "download/" + tag + "/";
            found[version] = new AvailableRelease(tag, version, published, notes, page,
                Asset(r, downloads, $"tide-{tag}.zip"), Asset(r, downloads, $"tide-{tag}.zip.sha256"));
        }
        return [.. found.Values.OrderByDescending(x => x.Version)];
    }

    // An asset's download link, kept only when it's the expected file under this release's own download folder.
    private static string? Asset(JsonElement release, string prefix, string name)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;
        foreach (var a in assets.EnumerateArray())
        {
            if (a.ValueKind != JsonValueKind.Object || Str(a, "name") != name) continue;
            var url = Str(a, "browser_download_url");
            return url == prefix + name ? url : null;
        }
        return null;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
