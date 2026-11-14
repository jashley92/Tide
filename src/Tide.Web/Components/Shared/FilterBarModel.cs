namespace Tide.Web.Components.Shared;

// What a page tells its FilterBar: the filters, what each is set to now and how to set it, and the pills that are
// presets of them. The page keeps its own filter state and address; the bar draws them and applies what's chosen.

/// <summary>How a filter in the "+ Filter" menu is chosen.</summary>
public enum FilterKind
{
    /// <summary>One of its options.</summary>
    Single,

    /// <summary>Any of its options, applied together.</summary>
    Multi,

    /// <summary>A typed value (words, an id, a date).</summary>
    Text,
}

/// <summary>A choice of a filter: its value as the page's address holds it, and its name. A group heads the options under it.</summary>
public sealed record FilterOption(string Value, string Label, string? Group = null)
{
    /// <summary>An option per member of <typeparamref name="T"/>, by its name (the value) and <paramref name="name"/>'s words for it.</summary>
    public static IReadOnlyList<FilterOption> ForEnum<T>(Func<T, string>? name = null) where T : struct, Enum =>
        [.. Enum.GetValues<T>().Select(v => new FilterOption(v.ToString(), name?.Invoke(v) ?? v.ToString()))];
}

/// <summary>
/// A quick filter: a preset of the filter <paramref name="FilterKey"/>. It's on while that filter holds exactly
/// <paramref name="Values"/>; pressing it sets them, or clears the filter when it's on. Pills of one filter take turns.
/// </summary>
public sealed record FilterPill(string Key, string Label, string FilterKey, IReadOnlyList<string> Values)
{
    public string? Title { get; init; }
    public bool Disabled { get; init; }

    /// <summary>How many it would show, said beside its label; none when the page doesn't count it.</summary>
    public int? Count { get; init; }

    /// <summary>Whether it's on: the filter holds its values, in any order.</summary>
    public bool OnFor(FilterDef? filter) => filter is not null && filter.Values.Count == Values.Count && !Values.Except(filter.Values, StringComparer.Ordinal).Any();

    /// <summary>What pressing it sets the filter to: its values, or none when it's on.</summary>
    public IReadOnlyList<string> Pressed(FilterDef filter) => OnFor(filter) ? [] : Values;
}

/// <summary>
/// A filter: offered under "+ Filter" (unless <see cref="InMenu"/> is false), a chip while set unless a pill on it is
/// on. <paramref name="Apply"/> sets it to the values chosen (none clears it); the bar then calls the page's refilter.
/// </summary>
public sealed record FilterDef(string Key, string Label, FilterKind Kind, Action<IReadOnlyList<string>> Apply)
{
    public IReadOnlyList<FilterOption> Options { get; init; } = [];

    /// <summary>What it's set to now, as option values; none when it isn't set.</summary>
    public IReadOnlyList<string> Values { get; init; } = [];

    /// <summary>False for a filter set only by a pill or a link (the parts of one action): never in the menu.</summary>
    public bool InMenu { get; init; } = true;

    /// <summary>What its chip says, when "Label: value" doesn't read well.</summary>
    public string? ChipText { get; init; }

    /// <summary>The heading it's listed under in "+ Filter" ("Who", "Where", "What", "When"); filters with none are
    /// listed unheaded. Each group appears in the order its first filter does.</summary>
    public string? Section { get; init; }

    /// <summary>The options past which a box to find one is shown above them (a long list of people, say).</summary>
    public const int FindAbove = 8;

    /// <summary>Whether its options get a box to find one: many of them, for a single or multi filter.</summary>
    public bool Findable => Kind != FilterKind.Text && Options.Count > FindAbove;

    /// <summary>A text filter's box: its input type ("date"), placeholder, suggestions, and a line of help under it.</summary>
    public string InputType { get; init; } = "text";
    public string? Placeholder { get; init; }
    public IReadOnlyList<FilterOption> Suggestions { get; init; } = [];
    public string? Hint { get; init; }

    public bool IsSet => Values.Count > 0;

    /// <summary>The values by their options' names ("P1, P2"); a value no option has, as it is.</summary>
    public string Shown => string.Join(", ", Values.Select(v => Options.FirstOrDefault(o => o.Value == v)?.Label ?? v));

    /// <summary>What its chip says: "Team: Network Engineering".</summary>
    public string Text => ChipText ?? $"{Label}: {Shown}";
}

/// <summary>
/// A chip in the bar: what it says, the filter it is (choosing the chip opens that filter's options when it's in the
/// menu), and, on a page whose filters are links, the address without it.
/// </summary>
public sealed record FilterChipView(string Text, string Key, string? RemoveHref = null);

/// <summary>Helpers for pages' filters: several values in one address parameter, one value, and the chips the filters make.</summary>
public static class FilterValues
{
    /// <summary>"p1,p2" as its values, blanks and repeats dropped.</summary>
    public static IReadOnlyList<string> Split(string? text) =>
        string.IsNullOrWhiteSpace(text) ? [] : text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The values as one parameter ("p1,p2"), or null for none, so it drops out of the address.</summary>
    public static string? Join(IEnumerable<string> values)
    {
        var all = values.ToList();
        return all.Count == 0 ? null : string.Join(",", all);
    }

    /// <summary>A value, or none.</summary>
    public static IReadOnlyList<string> Of(string? value) => string.IsNullOrEmpty(value) ? [] : [value];

    /// <summary>A flag as a one-value filter: "true", or none.</summary>
    public static IReadOnlyList<string> Of(bool on) => on ? ["true"] : [];

    /// <summary>The first value; null for none.</summary>
    public static string? First(IReadOnlyList<string> values) => values.Count > 0 ? values[0] : null;

    /// <summary>A member of <typeparamref name="T"/> by its name; null for none or a name it hasn't.</summary>
    public static T? Enum<T>(IReadOnlyList<string> values) where T : struct, Enum =>
        Tide.Domain.Common.EnumNames.OrNull<T>(First(values), ignoreCase: true);

    // An address's parameters are read as text and parsed here: Blazor fails the whole page on a typed parameter
    // ([SupplyParameterFromQuery] Guid?, int?, bool?) whose value doesn't parse ("?named=1", a link cut short), where a
    // value that isn't one should be ignored, as an unknown ?status= is.

    /// <summary>An address parameter as an id; null for none or a value that isn't one.</summary>
    public static Guid? Id(string? text) => Guid.TryParse(text, out var id) ? id : null;

    /// <summary>An address parameter as a whole number; null for none or a value that isn't one (or too big).</summary>
    public static int? Number(string? text) =>
        int.TryParse(text, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : null;

    /// <summary>An address parameter as a switch: on only for "true" (any case), as the pages write it.</summary>
    public static bool Flag(string? text) => bool.TryParse(text, out var on) && on;

    /// <summary>A switch the address may leave out: null for none or a value that isn't one.</summary>
    public static bool? FlagOrNull(string? text) => bool.TryParse(text, out var on) ? on : null;

    /// <summary>A chip for each filter that's set.</summary>
    public static IReadOnlyList<FilterChipView> Chips(IEnumerable<FilterDef> filters) =>
        filters.Where(f => f.IsSet).Select(f => new FilterChipView(f.Text, f.Key)).ToList();
}
