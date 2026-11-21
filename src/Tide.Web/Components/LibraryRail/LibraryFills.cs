using System.Text.RegularExpressions;
using Tide.Application.Actions;
using Tide.Domain.Common;
using Tide.Domain.Enums;

namespace Tide.Web.Components.LibraryRail;

/// <summary>A placeholder an entry uses, as written ("{{days}}"), and what an action fills it with.</summary>
public sealed record LibraryFill(string Written, string Means);

/// <summary>A fact about an entry, as the rail's first section lists it ("Platform", "Cortex XSIAM (XQL)").</summary>
public sealed record LibraryFact(string Label, string Value);

/// <summary>The placeholders a saved query or an outreach template uses, each once, in the order they first come, with
/// what an action fills each with: what the rail's "Fills in" lists.</summary>
public static partial class LibraryFills
{
    // A name and a modifier after a colon; for an outreach template, a section's # before it, or a closing ? (optional)
    // or |text (written when it's empty) after it. {{else}} reads as a name no describer knows, so isn't listed.
    [GeneratedRegex(@"\{\{(#)?([A-Za-z][A-Za-z0-9.\-]*)(?::([A-Za-z0-9+\-]*))?(\?)?(\|[^{}\r\n]*)?\}\}", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Pattern();

    /// <summary>A saved query's: <c>{{days}}</c>, <c>{{values}}</c> and a kind's values (<c>{{values:sha256}}</c>).</summary>
    public static IReadOnlyList<LibraryFill> OfQuery(string text) => Found(text, (name, modifier) => name switch
    {
        "days" => "the action's window, in days",
        "values" when modifier is null => "its indicators",
        "values" when HuntValues(modifier) is { } kind => "its " + Application.Actions.HuntValues.Label(kind).ToLowerInvariant(),
        _ => null,
    });

    /// <summary>An outreach template's, in its subject and body, as the template page describes each.</summary>
    public static IReadOnlyList<LibraryFill> OfTemplate(string subject, string body) => Found(subject + "\n" + body, (name, modifier) =>
        modifier is null ? OutreachFill.All.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Description
        : name.Equals(OutreachFill.IndicatorsName, StringComparison.OrdinalIgnoreCase) && HuntValues(modifier) is { } kind
            ? $"The {Application.Actions.HuntValues.Label(kind).ToLowerInvariant()} on the article and the action."
        : name.Equals(OutreachFill.TodayName, StringComparison.OrdinalIgnoreCase)
            ? $"A date {modifier.TrimStart('+', '-')} business days {(modifier.StartsWith('-') ? "before" : "after")} today."
        : null);

    private static HuntValueKind? HuntValues(string name) => EnumNames.TryParseExact(name, out HuntValueKind k, ignoreCase: true) ? k : null;

    // Each placeholder once, those the describer knows; a name it doesn't know is left out (a template's typo is refused
    // when it's saved, so one is only an old entry's).
    private static List<LibraryFill> Found(string text, Func<string, string?, string?> means) =>
        [.. Pattern().Matches(text).DistinctBy(m => m.Value, StringComparer.OrdinalIgnoreCase)
            .Select(m => means(m.Groups[2].Value.ToLowerInvariant(), m.Groups[3].Success ? m.Groups[3].Value : null) is { } said
                ? new LibraryFill(m.Value, said + Note(m)) : null)
            .OfType<LibraryFill>()];

    // What a section, a closing ? or |text adds to the placeholder's description.
    private static string Note(Match m) =>
        m.Groups[1].Success ? " Its section shows only when there's something to fill."
        : m.Groups[4].Success ? " Left out when there's nothing to fill."
        : m.Groups[5].Success ? $" \"{m.Groups[5].Value[1..].Trim()}\" when there's nothing to fill."
        : "";
}
