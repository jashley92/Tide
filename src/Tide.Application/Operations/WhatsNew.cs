using System.Reflection;
using Tide.Domain.Enums;

namespace Tide.Application.Operations;

/// <summary>Who a release's highlights speak to. Each has a section of its own in a release's highlights file.</summary>
public enum RoleGroup
{
    Analysts = 0,
    Presenters = 1,
    ViewersAndActionOwners = 2,
    Administrators = 3,
}

/// <summary>Which highlights each role reads.</summary>
public static class RoleGroups
{
    /// <summary>The section heading each group has in a highlights file, and the order they're shown in.</summary>
    public static readonly IReadOnlyList<(RoleGroup Group, string Heading)> Headings =
    [
        (RoleGroup.Analysts, "Analysts"),
        (RoleGroup.Presenters, "Presenters"),
        (RoleGroup.Administrators, "Administrators"),
        (RoleGroup.ViewersAndActionOwners, "Viewers and action owners"),
    ];

    /// <summary>"For analysts", as the pop-up and Help head a group's highlights.</summary>
    public static string Title(RoleGroup g) => "For " + Headings.First(h => h.Group == g).Heading.ToLowerInvariant();

    /// <summary>
    /// The groups whose highlights a person with <paramref name="roles"/> reads, in the order they're shown. Analysts
    /// present the brief, so they read the presenters' highlights too; administrators hold every permission, so they read
    /// those and their own. Someone who works as an analyst doesn't also read the viewers' narrower version of the same
    /// pages. Readers and Visitors read none: they see the published archive, or a meeting only from a seat in it.
    /// </summary>
    public static IReadOnlyList<RoleGroup> For(IEnumerable<AppRole> roles)
    {
        var list = roles as ICollection<AppRole> ?? roles.ToList();
        return list.Contains(AppRole.Admin) ? [RoleGroup.Analysts, RoleGroup.Presenters, RoleGroup.Administrators]
            : list.Contains(AppRole.Analyst) ? [RoleGroup.Analysts, RoleGroup.Presenters]
            : list.Any(r => r is AppRole.Viewer or AppRole.ActionOwner) ? [RoleGroup.ViewersAndActionOwners]
            : [];
    }
}

/// <summary>One release's highlights: a short introduction and, for each role group, Markdown bullets.</summary>
public sealed record ReleaseHighlights(ReleaseVersion Version, string Intro, IReadOnlyDictionary<RoleGroup, string> Sections)
{
    /// <summary>The sections <paramref name="groups"/> read, in their order; none when the release has nothing for them.</summary>
    public IReadOnlyList<(RoleGroup Group, string Markdown)> For(IEnumerable<RoleGroup> groups) =>
        groups.Where(Sections.ContainsKey).Select(g => (g, Sections[g])).ToList();

    /// <summary>
    /// Reads a highlights file: an optional <c># title</c>, an introduction, then a <c>## heading</c> per role group
    /// (<see cref="RoleGroups.Headings"/>). A section that isn't a group's, or is empty, is left out. Null when no group
    /// has anything: a release without highlights shows nothing.
    /// </summary>
    public static ReleaseHighlights? Parse(ReleaseVersion version, string markdown)
    {
        var intro = new List<string>();
        var sections = new Dictionary<RoleGroup, List<string>>();
        List<string>? into = intro;
        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.StartsWith("# ", StringComparison.Ordinal)) continue;
            if (raw.StartsWith("## ", StringComparison.Ordinal))
            {
                var heading = raw[3..].Trim();
                var match = RoleGroups.Headings.FirstOrDefault(h => string.Equals(h.Heading, heading, StringComparison.OrdinalIgnoreCase));
                into = match.Heading is null ? null : sections[match.Group] = [];
                continue;
            }
            into?.Add(raw);
        }
        var kept = sections.Select(s => (s.Key, Text: string.Join('\n', s.Value).Trim()))
            .Where(s => s.Text.Length > 0).ToDictionary(s => s.Key, s => s.Text);
        return kept.Count == 0 ? null : new ReleaseHighlights(version, string.Join('\n', intro).Trim(), kept);
    }
}

/// <summary>A release as Help lists it: its full notes (its section of the upgrade notes) and its highlights, if any.</summary>
public sealed record ReleaseEntry(ReleaseVersion Version, string? Notes, ReleaseHighlights? Highlights);

/// <summary>
/// Every release's notes, as built into the app: each release's highlights file (<c>docs/whats-new/vX.Y.Z.md</c>) and
/// the full notes, the <c>### vX.Y.Z</c> sections of <c>docs/UPGRADE.md</c>, the same text a release's GitHub page shows.
/// </summary>
public sealed class ReleaseNotesCatalog
{
    internal const string HighlightsPrefix = "Tide.WhatsNew.", UpgradeNotes = "Tide.Upgrade.md";

    /// <summary>Newest first.</summary>
    public IReadOnlyList<ReleaseEntry> Releases { get; }

    public ReleaseNotesCatalog(IReadOnlyDictionary<ReleaseVersion, string> highlightFiles, string? upgradeNotes)
    {
        var notes = upgradeNotes is null ? new Dictionary<ReleaseVersion, string>() : NotesSections(upgradeNotes);
        var highlights = highlightFiles
            .Select(f => ReleaseHighlights.Parse(f.Key, f.Value)).OfType<ReleaseHighlights>()
            .ToDictionary(h => h.Version);
        Releases = notes.Keys.Union(highlights.Keys)
            .OrderDescending()
            .Select(v => new ReleaseEntry(v, notes.GetValueOrDefault(v), highlights.GetValueOrDefault(v)))
            .ToList();
    }

    /// <summary>The catalog built into this assembly.</summary>
    public static ReleaseNotesCatalog Embedded { get; } = FromAssembly(typeof(ReleaseNotesCatalog).Assembly);

    internal static ReleaseNotesCatalog FromAssembly(Assembly asm)
    {
        var files = new Dictionary<ReleaseVersion, string>();
        string? upgrade = null;
        foreach (var name in asm.GetManifestResourceNames())
        {
            if (name == UpgradeNotes) upgrade = Read(asm, name);
            else if (name.StartsWith(HighlightsPrefix, StringComparison.Ordinal) && name.EndsWith(".md", StringComparison.Ordinal)
                     && ReleaseVersion.Parse(name[HighlightsPrefix.Length..^3]) is { } v)
                files[v] = Read(asm, name);
        }
        return new ReleaseNotesCatalog(files, upgrade);
    }

    private static string Read(Assembly asm, string name)
    {
        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>The newest release with notes or highlights; null when there are none.</summary>
    public ReleaseVersion? Newest => Releases.Count == 0 ? null : Releases[0].Version;

    /// <summary>
    /// Each <c>### vX.Y.Z</c> section of the upgrade notes, read as <c>tools/ci/extract-release-notes.sh</c> reads it:
    /// up to the next <c>###</c>, <c>##</c> or <c>---</c> line, without the blank lines around it. A heading that isn't a
    /// version (<c>### Versions</c>) isn't a release's.
    /// </summary>
    public static Dictionary<ReleaseVersion, string> NotesSections(string markdown)
    {
        var found = new Dictionary<ReleaseVersion, string>();
        ReleaseVersion? on = null;
        var lines = new List<string>();
        void Close()
        {
            if (on is not null && string.Join('\n', lines).Trim() is { Length: > 0 } text) found.TryAdd(on, text);
            on = null;
            lines.Clear();
        }
        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.StartsWith("### ", StringComparison.Ordinal) || raw.StartsWith("## ", StringComparison.Ordinal) || raw.TrimEnd() == "---")
            {
                Close();
                if (raw.StartsWith("### v", StringComparison.Ordinal)) on = ReleaseVersion.Parse(raw[4..].Trim());
                continue;
            }
            if (on is not null) lines.Add(raw);
        }
        Close();
        return found;
    }

    /// <summary>
    /// The releases whose highlights are due for someone who reads <paramref name="groups"/>, running
    /// <paramref name="current"/>, who last saw <paramref name="seen"/>'s: each release after it, up to the one running,
    /// with highlights for them, newest first. Someone who has seen none is shown only the newest, not every release
    /// before it. Empty when there's nothing new for them: a patch or an operator-only release has no highlights.
    /// </summary>
    public IReadOnlyList<ReleaseHighlights> Due(ReleaseVersion current, ReleaseVersion? seen, IReadOnlyCollection<RoleGroup> groups)
    {
        var due = Releases
            .Where(r => r.Version <= current && (seen is null || r.Version > seen))
            .Select(r => r.Highlights).OfType<ReleaseHighlights>()
            .Where(h => h.For(groups).Count > 0)
            .ToList();
        return seen is null ? due.Take(1).ToList() : due;
    }

    /// <summary>The release this build is: the version it was released as, or, for a development build, the newest
    /// with notes (so what's new can be tried before a release).</summary>
    public ReleaseVersion? Running(ReleaseVersion? built) => built ?? Newest;
}

/// <summary>
/// "What's new" for the signed-in person: the releases whose highlights they haven't seen, by their role groups, and
/// which one they last saw (the <see cref="Preferences.UserPreferences.WhatsNewSeen"/> preference, saved as any other).
/// </summary>
public sealed class WhatsNewService(Preferences.UserPreferenceService prefs, Abstractions.ICurrentUser user, ReleaseNotesCatalog catalog,
    Org.OrgClock clock)
{
    /// <summary>The release running (<see cref="ReleaseNotesCatalog.Running"/>).</summary>
    public ReleaseVersion? Running => catalog.Running(AppVersionInfo.Current);

    /// <summary>The groups whose highlights the person reads; none for a Reader or a Visitor.</summary>
    public IReadOnlyList<RoleGroup> Groups => RoleGroups.For(user.Roles);

    /// <summary>Every release, newest first, for Help.</summary>
    public IReadOnlyList<ReleaseEntry> Releases => catalog.Releases;

    /// <summary>The highlights the person hasn't seen yet, newest first (<see cref="ReleaseNotesCatalog.Due"/>).</summary>
    public async Task<IReadOnlyList<ReleaseHighlights>> DueAsync(CancellationToken ct = default)
    {
        var groups = Groups;
        // No highlights built in for this person's groups (a Reader, a Visitor): no need to ask what they've seen.
        if (Running is not { } running || catalog.Due(running, null, groups).Count == 0) return [];
        var seen = ReleaseVersion.Parse(await prefs.GetAsync(Preferences.UserPreferences.WhatsNewSeen, ct));
        return catalog.Due(running, seen, groups);
    }

    /// <summary>Records that the person has seen what's new up to the release running, so it isn't shown again; and
    /// from today, a page the release changed shows its Show me around link for a while (<see cref="GuideLinkService"/>).</summary>
    public async Task MarkSeenAsync(CancellationToken ct = default)
    {
        if (Groups.Count == 0 || Running is not { } running) return;
        await prefs.SetAsync(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [Preferences.UserPreferences.WhatsNewSeen] = running.ToString(),
            [GuideLinkService.Release] = running + "@" + GuideLinkService.Day(clock.Today),
        }, ct);
    }
}
