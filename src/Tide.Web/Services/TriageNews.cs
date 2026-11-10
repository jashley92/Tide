using System.Globalization;
using Tide.Application.Intake;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.Web.Services;

/// <summary>
/// What Triage says when the inbox changes under an analyst: the line a row that left leaves behind ("Sam dismissed
/// “…”"), and the one toast for a colleague's batch or a poll ("Sam dismissed 3 items · 2 new from the feeds").
/// </summary>
public static class TriageNews
{
    /// <summary>What a row shows that someone else's work can change: its state and who set it, its group, its
    /// coverage and the items it's like. Two rows with the same signature look the same.</summary>
    public static object Signature(InboxRow row) => string.Join('|',
        row.Item.Status, row.Item.StatusByName, row.Item.Title, row.Item.Cves, row.Item.ArticleId, row.Item.DismissReason, row.Item.DismissKept,
        row.Similar?.Id, row.SameCve.Count, row.Group?.Newest, row.Group?.Ids.Count, row.Split?.Batch,
        string.Join(',', row.Coverage.Select(c => string.Create(CultureInfo.InvariantCulture,
            $"{c.ArticleId}:{c.Why}:{c.BriefDate}:{c.Attachable}:{string.Join('.', c.OpenActions)}"))));

    /// <summary>
    /// The line a row leaves as it goes from <paramref name="tab"/>: who did what to it (by the item as it is now), or
    /// that it left the list (it's still in this state but no longer shown, or it was pruned). <paramref name="colleagues"/>
    /// names who's heard when the item doesn't say (an item put back keeps no name).
    /// <paramref name="attached"/>: the item went to an existing article, not a new one (Change.Attached).
    /// </summary>
    public static string Left(FeedItemStatus tab, string title, FeedItem? now, string colleagues, bool attached = false)
    {
        var quoted = $"“{Short(title)}”";
        if (now is null || now.Status == tab) return $"{quoted} left this list";
        return $"{Who(now, colleagues)} {Did(now.Status, quoted, 1, attached)}";
    }

    /// <summary>
    /// The toast for one live reload of <paramref name="tab"/>, or null when there's nothing to say: what each colleague
    /// did to the rows that left and arrived, then what the feeds brought. In New, an arrival fetched since the rows were
    /// last read (<paramref name="readAt"/>) came from the feeds and any other was put back; elsewhere a poll is told
    /// by how many more New holds (<paramref name="newElsewhere"/>). <paramref name="entered"/>, when known, is how many
    /// items really entered the tab (its count's rise, plus those that left it): a row that only came into the shown
    /// page because one above it left isn't news.
    /// <paramref name="attached"/> holds the items that went to an existing article rather than made one.
    /// </summary>
    public static string? Summary(FeedItemStatus tab, IReadOnlyList<FeedItem?> left, IReadOnlyList<FeedItem> arrived,
        DateTimeOffset readAt, string colleagues, int newElsewhere = 0, int? entered = null, IReadOnlySet<Guid>? attached = null)
    {
        if (entered is { } n && n < arrived.Count) arrived = arrived.OrderByDescending(i => i.FetchedAtUtc).Take(Math.Max(0, n)).ToList();
        var parts = left.OfType<FeedItem>().Where(i => i.Status != tab)
            .GroupBy(i => (Who: Who(i, colleagues), i.Status, Attached: attached?.Contains(i.Id) == true))
            .Select(g => $"{g.Key.Who} {Did(g.Key.Status, Items(g.Count()), g.Count(), g.Key.Attached)}").ToList();
        int feeds;
        if (tab == FeedItemStatus.New)
        {
            feeds = arrived.Count(i => i.FetchedAtUtc >= readAt);
            var back = arrived.Count - feeds;
            if (back > 0) parts.Add($"{colleagues} put back {Items(back)}");
        }
        else
        {
            parts.AddRange(arrived.GroupBy(i => Who(i, colleagues)).Select(g => $"{g.Key} {Did(tab, Items(g.Count()), g.Count())}"));
            feeds = Math.Max(0, newElsewhere);
        }
        if (feeds > 0) parts.Add(parts.Count == 0 ? $"{feeds} new {(feeds == 1 ? "item" : "items")} from the feeds" : $"{feeds} new from the feeds");
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    private static string Who(FeedItem item, string colleagues) =>
        item.Status != FeedItemStatus.New && !string.IsNullOrWhiteSpace(item.StatusByName) ? item.StatusByName : colleagues;

    // "dismissed 3 items", "marked “…” as a duplicate": what was done, to what. An item attached to an existing article
    // reads as promoted, as one that made an article does; the change heard says which it was (Change.Attached).
    private static string Did(FeedItemStatus status, string what, int n, bool attached = false) => status switch
    {
        FeedItemStatus.Shortlisted => $"shortlisted {what}",
        FeedItemStatus.Dismissed => $"dismissed {what}",
        FeedItemStatus.Duplicate => n == 1 ? $"marked {what} as a duplicate" : $"marked {what} as duplicates",
        FeedItemStatus.Promoted when attached => $"added {what} to an article",
        FeedItemStatus.Promoted => $"promoted {what}",
        _ => $"put back {what}",
    };

    private static string Items(int n) => n == 1 ? "1 item" : $"{n} items";

    private static string Short(string s) => s.Length > 60 ? s[..60] + "…" : s;
}
