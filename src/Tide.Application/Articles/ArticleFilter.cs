using System.Globalization;
using Tide.Application.Mitre;
using Tide.Application.Trends;
using Tide.Domain.Enums;

namespace Tide.Application.Articles;

/// <summary>The assessments the article list can narrow to: the article lines of the trends report.</summary>
public enum ArticleAssessment
{
    Threat,
    P1P2,
    Informational,
}

/// <summary>A filter chip: what it says, the filter without it, and which filter it is (its address name; the dates are "from").</summary>
public sealed record FilterChip(string Label, ArticleFilter Without, string Key);

/// <summary>
/// What the article list narrows to, all of it in the address (<see cref="ToQuery"/>, <see cref="FromQuery"/>) so a link
/// can open the list prefiltered and Back steps through changes. Every filter holds at once.
/// <para>
/// The dates are when an article counts on the trends report: the day it was first briefed, else (never briefed, and
/// not a draft) the day it was written. A tag or technique of <see cref="None"/> means one with none. A technique counts
/// its sub-techniques only with <see cref="WithSubtechniques"/>, as the report rolls them up; with a tactic too, it's
/// the technique under that tactic (one technique on the article does both).
/// </para>
/// </summary>
public sealed record ArticleFilter
{
    public const string None = "none";

    public string? Search { get; init; }
    public ArticleStatus? Status { get; init; }
    public DateOnly? From { get; init; }
    public DateOnly? To { get; init; }
    public ArticleAssessment? Assessment { get; init; }

    /// <summary>A scorecard tag's key, or <see cref="None"/>.</summary>
    public string? Tag { get; init; }

    /// <summary>A scorecard category's key: any of its tags.</summary>
    public string? Category { get; init; }

    public MitreTactic? Tactic { get; init; }

    /// <summary>A technique id ("T1566", "T1566.001"), or <see cref="None"/>.</summary>
    public string? Technique { get; init; }

    public bool WithSubtechniques { get; init; }

    /// <summary>Whether anything other than the words narrows the list.</summary>
    public bool Narrowed => Status is not null || From is not null || To is not null || Assessment is not null || Tag is not null
                            || Category is not null || Tactic is not null || Technique is not null;

    /// <summary>Whether a tactic or a technique (other than <see cref="None"/>) narrows the list: each article is listed for some of its techniques.</summary>
    public bool MatchesTechniques => Technique != None && (Tactic is not null || Technique is not null);

    /// <summary>
    /// Of an article's <paramref name="techniqueIds"/>, the ones this filter lists it for, by id, as the list narrows
    /// (ArticleService.Narrowed): the technique, or with <see cref="WithSubtechniques"/> one of its sub-techniques too; and
    /// under the tactic, by the catalogue's tactics for that very id (a sub-technique by its own). None without
    /// <see cref="MatchesTechniques"/>.
    /// </summary>
    public IReadOnlyList<string> MatchingTechniques(IEnumerable<string> techniqueIds)
    {
        if (!MatchesTechniques) return [];
        var tactic = Tactic is { } t ? AttackCatalog.All.Where(c => c.Tactics.Contains(t)).Select(c => c.Id).ToHashSet(StringComparer.Ordinal) : null;
        return techniqueIds.Distinct(StringComparer.Ordinal)
            .Where(id => (Technique is not { } technique || id == technique || (WithSubtechniques && id.StartsWith(technique + ".", StringComparison.Ordinal)))
                         && (tactic is null || tactic.Contains(id)))
            .Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>The same words, and nothing else.</summary>
    public ArticleFilter Cleared() => new() { Search = Search };

    private static string Day(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Long(DateOnly d) => d.ToString("d MMM yyyy", CultureInfo.InvariantCulture);

    /// <summary>The address's query: every filter set, by name; null for the rest (so they drop out of a link).</summary>
    public IReadOnlyDictionary<string, object?> ToQuery() => new Dictionary<string, object?>
    {
        ["q"] = string.IsNullOrWhiteSpace(Search) ? null : Search.Trim(),
        ["status"] = Status?.ToString().ToLowerInvariant(),
        ["from"] = From is { } f ? Day(f) : null,
        ["to"] = To is { } t ? Day(t) : null,
        ["assess"] = Assessment switch
        {
            ArticleAssessment.Threat => "threat",
            ArticleAssessment.P1P2 => "p1-p2",
            ArticleAssessment.Informational => "informational",
            _ => null,
        },
        ["tag"] = Tag,
        ["category"] = Category,
        ["tactic"] = Tactic is { } tactic ? AttackMatrix.TacticId(tactic) : null,
        ["technique"] = Technique,
        ["withsubs"] = WithSubtechniques && Technique is not null and not None ? "true" : null,
    };

    /// <summary>Reads <see cref="ToQuery"/>'s names back; anything it doesn't recognise is left out.</summary>
    public static ArticleFilter FromQuery(Func<string, string?> get)
    {
        static string? Text(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        static DateOnly? Date(string? s) =>
            DateOnly.TryParseExact(s?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
        var technique = ParseTechnique(get("technique"));
        return new ArticleFilter
        {
            Search = Text(get("q")),
            Status = Enum.TryParse<ArticleStatus>(get("status"), ignoreCase: true, out var s) && Enum.IsDefined(s) ? s : null,
            From = Date(get("from")),
            To = Date(get("to")),
            Assessment = Text(get("assess"))?.ToLowerInvariant() switch
            {
                "threat" => ArticleAssessment.Threat,
                "p1-p2" => ArticleAssessment.P1P2,
                "informational" => ArticleAssessment.Informational,
                _ => null,
            },
            Tag = Text(get("tag"))?.ToLowerInvariant(),
            Category = Text(get("category"))?.ToLowerInvariant(),
            Tactic = ParseTactic(get("tactic")),
            Technique = technique,
            WithSubtechniques = technique is not null and not None && bool.TryParse(get("withsubs"), out var subs) && subs,
        };
    }

    /// <summary>A tactic by its id ("TA0001"), as the report and the address write it.</summary>
    public static MitreTactic? ParseTactic(string? id) =>
        AttackMatrix.Tactics.Cast<MitreTactic?>().FirstOrDefault(t => string.Equals(AttackMatrix.TacticId(t!.Value), id?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>A technique id in the catalogue's spelling ("t1566" is T1566), <see cref="None"/>, or null for anything else.</summary>
    public static string? ParseTechnique(string? text)
    {
        var id = text?.Trim() ?? "";
        if (id.Equals(None, StringComparison.OrdinalIgnoreCase)) return None;
        if (AttackCatalog.Find(id) is { } known) return known.Id;
        return TechniqueId(id) ? id.ToUpperInvariant() : null;
    }

    // A technique id the catalogue may have retired: T and four digits, then maybe a dot and three.
    private static bool TechniqueId(string id) =>
        id.Length is 5 or 9 && (id[0] is 'T' or 't') && id[1..5].All(char.IsAsciiDigit)
        && (id.Length == 5 || (id[5] == '.' && id[6..].All(char.IsAsciiDigit)));

    /// <summary>
    /// The dates as people read them: a whole month or quarter by its name ("Oct 2026", "Q3 2026"), whole months as a
    /// span ("May 2026 – Oct 2026"), else the days. Null when there are none.
    /// </summary>
    public string? DatesLabel
    {
        get
        {
            if (From is { } f && To is { } t)
            {
                var month = Period.Of(PeriodKind.Month, f);
                var quarter = Period.Of(PeriodKind.Quarter, f);
                if (f == month.Start && t == month.End) return month.Label;
                if (f == quarter.Start && t == quarter.End) return quarter.Label;
                if (f == month.Start && Period.Of(PeriodKind.Month, t) is var last && t == last.End && t > f) return $"{month.Label} – {last.Label}";
                return $"{Long(f)} – {Long(t)}";
            }
            if (From is { } from) return "From " + Long(from);
            if (To is { } to) return "Until " + Long(to);
            return null;
        }
    }

    public static string AssessmentName(ArticleAssessment a) => a switch
    {
        ArticleAssessment.Threat => "Threats",
        ArticleAssessment.P1P2 => "P1 and P2 threats",
        _ => "Informational",
    };

    /// <summary>A technique as a chip names it: "T1566 Phishing", or its id when the catalogue doesn't know it.</summary>
    public static string TechniqueName(string id) => $"{id} {AttackCatalog.Find(id)?.Name}".TrimEnd();

    /// <summary>
    /// A chip for each filter other than the words (the filter box shows those), in the order the filter row has them.
    /// <paramref name="tagName"/> and <paramref name="categoryName"/> name a key, else the key is shown.
    /// </summary>
    public IReadOnlyList<FilterChip> Chips(Func<string, string?> tagName, Func<string, string?> categoryName)
    {
        var chips = new List<FilterChip>();
        if (DatesLabel is { } dates) chips.Add(new(dates, this with { From = null, To = null }, "from"));
        if (Status is { } s) chips.Add(new("Status: " + s, this with { Status = null }, "status"));
        if (Assessment is { } a) chips.Add(new("Assessment: " + AssessmentName(a), this with { Assessment = null }, "assess"));
        if (Category is { } c) chips.Add(new("Category: " + (categoryName(c) ?? c), this with { Category = null }, "tag"));
        if (Tag is { } tag) chips.Add(new(tag == None ? "No scorecard tag" : "Tag: " + (tagName(tag) ?? tag), this with { Tag = null }, "tag"));
        if (Tactic is { } tactic) chips.Add(new("Tactic: " + AttackMatrix.TacticName(tactic), this with { Tactic = null }, "tactic"));
        if (Technique is { } t)
            chips.Add(new(t == None ? "No technique" : "Technique: " + TechniqueName(t) + (WithSubtechniques ? " and sub-techniques" : ""),
                this with { Technique = null, WithSubtechniques = false }, "technique"));
        return chips;
    }
}
