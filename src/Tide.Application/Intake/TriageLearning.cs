using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Tide.Application.Abstractions;
using Tide.Application.Stack;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.Application.Intake;

/// <summary>How the inbox is ordered: newest first (the default), or by what the team used before (EP-81).</summary>
public enum InboxSort
{
    Newest = 0,
    Likely = 1,
}

/// <summary>
/// Why an item is likely to be used: of the team's decided items like it (promoted, dismissed or marked duplicate in
/// the window), how many were promoted, and what "like it" was. The counts are always shown, so thin history reads as thin.
/// </summary>
public sealed record Likelihood(int Promoted, int Decided, string Why)
{
    /// <summary>The share promoted, pulled towards a half while there's little history (one in one isn't certainty).</summary>
    public double Score => (Promoted + 1d) / (Decided + 2d);
}

/// <summary>A source's record over the window, for Administration › Source health.</summary>
public sealed record SourceRecord(Source Source, double PerWeek, int Decided, int Promoted, DateTimeOffset? LastPromotedAtUtc,
    TimeSpan? MedianToTriage, bool SuggestPause)
{
    /// <summary>The share of decided items that were promoted, or null with nothing decided.</summary>
    public double? PromotedShare => Decided == 0 ? null : (double)Promoted / Decided;
}

/// <summary>A story that reached triage but no brief: a dismissed item naming a KEV-listed CVE or a watched adversary.</summary>
public sealed record MissedItem(FeedItem Item, string SourceName, string Why);

/// <summary>
/// What the team's own triage decisions say (EP-81): counts per source, per product on Our stack, and per source and
/// product together, read from the items still kept. Nothing is trained or stored; the counts are worked out when asked.
/// </summary>
public sealed class TriageLearning
{
    /// <summary>The longest window counted, in days (capped by how long dismissed items are kept).</summary>
    public const int WindowDays = 90;

    /// <summary>Decided items needed before a count is used as a reason.</summary>
    public const int MinHistory = 3;

    private readonly Dictionary<Guid, (int P, int N)> _bySource = [];
    private readonly Dictionary<Guid, (int P, int N)> _byProduct = [];
    private readonly Dictionary<(Guid Source, Guid Product), (int P, int N)> _byBoth = [];

    /// <summary>The window counted, in days.</summary>
    public int Days { get; }

    private TriageLearning(int days) => Days = days;

    /// <summary>Counts the items decided in the last <paramref name="days"/> days, by source and by the product they name.</summary>
    internal static async Task<TriageLearning> LearnAsync(IAppDbContext db, IReadOnlyList<StackEntry> stack, DateTimeOffset now, int days, CancellationToken ct)
    {
        var learned = new TriageLearning(days);
        var since = now.AddDays(-days);
        var decided = await db.FeedItems.AsNoTracking()
            .Where(i => i.FetchedAtUtc >= since && (i.Status == FeedItemStatus.Promoted || i.Status == FeedItemStatus.Dismissed || i.Status == FeedItemStatus.Duplicate))
            .Select(i => new { i.SourceId, i.Title, Excerpt = i.Excerpt == null ? null : i.Excerpt.Substring(0, StackService.SummaryStart), i.Status })
            .ToListAsync(ct);
        var match = StackService.Matcher(stack);
        foreach (var i in decided)
        {
            var promoted = i.Status == FeedItemStatus.Promoted;
            Add(learned._bySource, i.SourceId, promoted);
            if (match(i.Title, i.Excerpt) is { } product)
            {
                Add(learned._byProduct, product.Id, promoted);
                Add(learned._byBoth, (i.SourceId, product.Id), promoted);
            }
        }
        return learned;
    }

    private static void Add<TKey>(Dictionary<TKey, (int P, int N)> counts, TKey key, bool promoted) where TKey : notnull
    {
        var (p, n) = counts.GetValueOrDefault(key);
        counts[key] = (p + (promoted ? 1 : 0), n + 1);
    }

    /// <summary>
    /// The most specific count with enough history for an item from <paramref name="source"/> naming <paramref name="product"/>:
    /// the source and product together, then the product, then the source. Null when none has <see cref="MinHistory"/>.
    /// </summary>
    public Likelihood? For(Source source, StackEntry? product)
    {
        if (product is not null)
        {
            if (_byBoth.GetValueOrDefault((source.Id, product.Id)) is var (bp, bn) && bn >= MinHistory)
                return new(bp, bn, Say(bp, bn, $"from {source.Name} naming {product.Name}"));
            if (_byProduct.GetValueOrDefault(product.Id) is var (pp, pn) && pn >= MinHistory)
                return new(pp, pn, Say(pp, pn, $"naming {product.Name}"));
        }
        return _bySource.GetValueOrDefault(source.Id) is var (sp, sn) && sn >= MinHistory
            ? new(sp, sn, Say(sp, sn, $"from {source.Name}"))
            : null;
    }

    private string Say(int promoted, int decided, string what) =>
        string.Create(CultureInfo.InvariantCulture, $"{promoted} of the {decided} {what} decided in {Days} days {(promoted == 1 ? "was" : "were")} promoted");
}
