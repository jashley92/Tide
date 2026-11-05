using Tide.Application.Search;
using Tide.Domain.Entities;

namespace Tide.Application.Scorecard;

/// <summary>
/// The article editor's and present mode's "Add a tag" search over the scorecard tags. The tags are a short,
/// admin-managed list already read with the editor, so it's matched here in memory rather than in the database.
/// What's typed matches a tag's name or its category's name, ignoring case: a match at the start of the name comes
/// first, then one at the start of a word in it, then one anywhere in it. A category matched by name offers all its
/// tags. Retired tags and categories, and the tags already chosen, aren't offered.
/// </summary>
public static class ScorecardTagSearch
{
    /// <summary>A tag offered, under its category. <see cref="First"/> starts its category's group in the list.</summary>
    public sealed record Hit(ScorecardTag Tag, ScorecardCategory Category, bool First);

    /// <summary>The chosen tags of one category, in the category's order.</summary>
    public sealed record Group(ScorecardCategory Category, IReadOnlyList<ScorecardTag> Tags);

    /// <summary>How well <paramref name="text"/> matches <paramref name="name"/>: 0 at its start, 1 at a word's start,
    /// 2 anywhere, or null if not at all (<see cref="NameRank.Placed"/>, the words trimmed).</summary>
    public static int? Rank(string name, string text) =>
        string.IsNullOrWhiteSpace(text) ? null : NameRank.Placed(name, text.Trim());

    /// <summary>The tags matching <paramref name="text"/>, grouped by category: the groups with the best match first,
    /// then in the categories' own order; in a group, the best matches first, then in the tags' own order.
    /// Nothing typed offers nothing.</summary>
    public static IReadOnlyList<Hit> Find(IEnumerable<ScorecardCategory> categories, string? text, IReadOnlySet<Guid>? chosen = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        text = text.Trim();
        var groups = new List<(int Best, int Order, ScorecardCategory Category, List<ScorecardTag> Tags)>();
        var order = 0;
        foreach (var cat in categories)
        {
            order++;
            if (cat.IsRetired) continue;
            var byCategory = NameRank.Placed(cat.Name, text);
            var tags = cat.Tags
                .Where(t => !t.IsRetired && chosen?.Contains(t.Id) != true)
                .Select((t, i) => (Tag: t, Index: i, Rank: Best(NameRank.Placed(t.Name, text), byCategory)))
                .Where(t => t.Rank is not null)
                .OrderBy(t => t.Rank).ThenBy(t => t.Index)
                .ToList();
            if (tags.Count > 0) groups.Add((tags[0].Rank!.Value, order, cat, tags.Select(t => t.Tag).ToList()));
        }
        return groups.OrderBy(g => g.Best).ThenBy(g => g.Order)
            .SelectMany(g => g.Tags.Select((t, i) => new Hit(t, g.Category, i == 0)))
            .ToList();
    }

    /// <summary>The chosen tags still offered, by category in the categories' order. Ids not among them (a retired
    /// tag still on the article) are left to <see cref="Gone"/>.</summary>
    public static IReadOnlyList<Group> Chosen(IEnumerable<ScorecardCategory> categories, IReadOnlySet<Guid> chosen) =>
        categories.Select(c => new Group(c, c.Tags.Where(t => chosen.Contains(t.Id)).ToList()))
            .Where(g => g.Tags.Count > 0)
            .ToList();

    /// <summary>The chosen ids that aren't among <paramref name="categories"/>' tags: retired tags still on the article.</summary>
    public static IReadOnlyList<Guid> Gone(IEnumerable<ScorecardCategory> categories, IEnumerable<Guid> chosen)
    {
        var live = categories.SelectMany(c => c.Tags).Select(t => t.Id).ToHashSet();
        return chosen.Where(id => !live.Contains(id)).ToList();
    }

    /// <summary>"3 chosen".</summary>
    public static string Summary(int count) => $"{count} chosen";

    private static int? Best(int? a, int? b) => a is null ? b : b is null ? a : Math.Min(a.Value, b.Value);
}
