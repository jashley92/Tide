using System.Globalization;
using Tide.Application.Articles;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.Application.Intake;

/// <summary>
/// The new items naming a CVE on CISA's KEV list, as Triage's KEV round-up would promote them
/// (<see cref="IntakeService.KevRoundupAsync"/>): the newest <see cref="KevRoundup.MaxItems"/>, how many there are in all,
/// the KEV-listed CVEs those name, and the most restrictive of their sources' TLPs, which the draft starts at.
/// </summary>
public sealed record KevRoundupItems(IReadOnlyList<Guid> Ids, int Total, IReadOnlyList<string> Cves, TlpLevel Tlp);

/// <summary>
/// The KEV round-up: the day's KEV additions, often all from one feed, promoted as one article. The draft gets a bullet
/// per KEV-listed CVE from TIDE's own CVE records (no AI: these are facts TIDE keeps) and one recommended action.
/// </summary>
public static class KevRoundup
{
    /// <summary>
    /// How many items a round-up promotes as one. Higher than a suggested group's <see cref="TriageGroups.MaxSize"/>: some
    /// days CISA lists ten or more, and a round-up's items are one feed's short notices, not reports to compare. The claim
    /// is still one statement and the draft one save, so twenty is well within what a promote does safely.
    /// </summary>
    public const int MaxItems = 20;

    /// <summary>The round-up's recommended action, which present mode can raise as an action.</summary>
    public const string Recommended = "Patch the KEV additions we run";

    /// <summary>
    /// A bullet per KEV-listed CVE in <paramref name="records"/> (those not on KEV are left out, and a CVE given twice is
    /// one bullet): "**CVE-2026-1234**: Microsoft Exchange Server. CVSS 9.8, EPSS 94%, due 2026-12-05, ransomware use."
    /// What a record doesn't know is said ("CVSS not known") rather than left out, so every bullet reads the same way.
    /// The soonest due first, then the highest CVSS, then by id. At most <see cref="Article.MaxBullets"/>: past that, the
    /// last bullet names the rest.
    /// </summary>
    public static IReadOnlyList<string> Bullets(IEnumerable<CveRecord> records)
    {
        var kev = records.Where(r => r.IsKev).DistinctBy(r => r.CveId, StringComparer.OrdinalIgnoreCase)
            .OrderBy(r => r.KevDueDate ?? DateOnly.MaxValue).ThenByDescending(r => r.CvssScore ?? -1)
            .ThenBy(r => r.CveId, StringComparer.OrdinalIgnoreCase).ToList();
        if (kev.Count <= Article.MaxBullets) return [.. kev.Select(Bullet)];
        var shown = kev.Take(Article.MaxBullets - 1).Select(Bullet).ToList();
        var rest = kev.Skip(Article.MaxBullets - 1).Select(r => r.CveId).ToList();
        shown.Add(Content.Words.Ellipsis($"And {rest.Count} more on KEV: {string.Join(", ", rest)}.", ArticleService.MaxBullet));
        return shown;
    }

    private static string Bullet(CveRecord r)
    {
        var affected = $"{r.Vendor} {r.Product}".Trim();
        var facts = new List<string>
        {
            r.CvssScore is { } cvss ? "CVSS " + cvss.ToString("0.0", CultureInfo.InvariantCulture) : "CVSS not known",
            r.EpssScore is { } epss ? "EPSS " + epss.ToString("P0", CultureInfo.InvariantCulture).Replace(" ", "", StringComparison.Ordinal) : "EPSS not known",
            r.KevDueDate is { } due ? "due " + due.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "no due date",
        };
        if (r.KevRansomware) facts.Add("ransomware use");
        return $"**{r.CveId}**: {(affected.Length > 0 ? affected : "Product not known")}. {string.Join(", ", facts)}.";
    }
}
