using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tide.Application.Abstractions;
using Tide.Application.Assist;
using Tide.Application.Indicators;
using Tide.Application.Security;
using Tide.Application.Stack;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Domain.Observables;

namespace Tide.Application.Intake;

/// <summary>What the reading pane marks in an article's text.</summary>
public enum ReadingMark { None, Cve, Indicator, WeRun }

/// <summary>A run of the text, marked or not.</summary>
public sealed record ReadingSpan(string Text, ReadingMark Mark);

/// <summary>
/// An item's article text for the reading pane: the text in marked runs, how and when it was fetched, and what it names
/// (CVEs, indicators, products on Our stack). <see cref="Problem"/> says why there's no text, safe to show.
/// </summary>
public sealed record ReadingText(Guid ItemId, IReadOnlyList<ReadingSpan> Spans, int Length, string? How, DateTimeOffset? FetchedAtUtc,
    IReadOnlyList<string> Cves, IReadOnlyList<IocObservable.ParsedIndicator> Indicators, IReadOnlyList<string> WeRun, string? Problem = null)
{
    public bool HasText => Length > 0;

    public static ReadingText Failed(Guid itemId, string problem) => new(itemId, [], 0, null, null, [], [], [], problem);
}

/// <summary>
/// Triage's reading pane (EP-76): an item's article fetched from its page through intake's guarded client, the body
/// found by <see cref="ArticleText"/> with the source's selector, and kept for <see cref="IntakeOptions.ReadingDays"/> so
/// colleagues and the editor reuse it. Analysts only, one item per request, never in bulk. A source set to Never isn't
/// fetched. The AI provider isn't asked: reading is TIDE's own fetch.
/// </summary>
public sealed partial class ReadingService(IAppDbContextFactory dbs, ICurrentUser user, IOutboundHttp http, IOptionsMonitor<IntakeOptions> options, TimeProvider time)
{
    /// <summary>The item's text if it's kept and still fresh; null when it has to be fetched.</summary>
    public async Task<ReadingText?> KeptAsync(Guid itemId, CancellationToken ct = default)
    {
        RequireTriage();
        await using var db = await dbs.CreateAsync(ct);
        var kept = await db.FeedItemTexts.AsNoTracking().FirstOrDefaultAsync(t => t.FeedItemId == itemId && t.FetchedAtUtc >= Cutoff(), ct);
        return kept is null ? null : await MarkAsync(db, kept, ct);
    }

    /// <summary>The item's text: kept, or fetched now (and kept) when it isn't, or when <paramref name="again"/>.</summary>
    public async Task<ReadingText> ReadAsync(Guid itemId, bool again = false, CancellationToken ct = default)
    {
        RequireTriage();
        await using var db = await dbs.CreateAsync(ct);
        var item = await db.FeedItems.AsNoTracking().Include(i => i.Source).FirstOrDefaultAsync(i => i.Id == itemId, ct)
            ?? throw new ArgumentException("That item has gone from the inbox.");
        var kept = await db.FeedItemTexts.FirstOrDefaultAsync(t => t.FeedItemId == itemId, ct);
        if (kept is not null && !again && kept.FetchedAtUtc >= Cutoff()) return await MarkAsync(db, kept, ct);

        if (item.Source?.ArticleFetch == ArticleFetch.Never)
            return ReadingText.Failed(itemId, $"Articles from {item.Source.Name} aren't fetched (its sources setting). Open the site instead.");
        if (Articles.ArticleService.SafeUrl(item.Url) is not { } address)
            return ReadingText.Failed(itemId, "The item has no http or https link to read.");

        var got = await http.GetAsync(address, ct: ct);
        var found = got.Status == FetchStatus.Ok && got.Content is { } bytes ? await ArticleText.ExtractAsync(bytes, item.Source?.ArticleBodySelector, options.CurrentValue.ReadBudget, ArticleCleanup.For(options.CurrentValue, item.Source), ct) : null;
        if (found is not { Usable: true })
            return ReadingText.Failed(itemId, (found?.Problem ?? got.Error ?? "The page couldn't be read.") + " Open the site instead.");

        if (kept is null) db.FeedItemTexts.Add(kept = new FeedItemText { FeedItemId = itemId });
        kept.Text = found.Text;
        kept.How = found.How.Length > FeedItemText.MaxHow ? found.How[..FeedItemText.MaxHow] : found.How;
        kept.FetchedAtUtc = time.GetUtcNow();
        // A colleague reading the same item at the same time keeps theirs first (or the item is pruned meanwhile): this
        // read is still shown, just not kept a second time.
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { }
        return await MarkAsync(db, kept, ct);
    }

    /// <summary>Deletes kept texts older than <see cref="IntakeOptions.ReadingDays"/> (the daily prune).</summary>
    public async Task<int> PruneAsync(CancellationToken ct = default)
    {
        var cutoff = Cutoff();
        await using var db = await dbs.CreateAsync(ct);
        var old = await db.FeedItemTexts.Where(t => t.FetchedAtUtc < cutoff).ToListAsync(ct);
        db.FeedItemTexts.RemoveRange(old);
        await db.SaveChangesAsync(ct);
        return old.Count;
    }

    private DateTimeOffset Cutoff() => time.GetUtcNow().AddDays(-Math.Clamp(options.CurrentValue.ReadingDays, 1, 30));

    private static async Task<ReadingText> MarkAsync(IAppDbContext db, FeedItemText kept, CancellationToken ct)
    {
        var stack = await StackService.EntriesAsync(db, ct);
        return Mark(kept.FeedItemId, kept.Text, kept.How, kept.FetchedAtUtc, stack);
    }

    /// <summary>The text in runs, with its CVEs, indicators and the products on Our stack it names marked.</summary>
    public static ReadingText Mark(Guid itemId, string text, string? how, DateTimeOffset? at, IReadOnlyList<StackEntry> stack)
    {
        var found = IocExtractor.Extract(text);
        var marks = new List<(int At, int Length, ReadingMark Mark)>();
        foreach (Match m in CveRx().Matches(text)) marks.Add((m.Index, m.Length, ReadingMark.Cve));
        foreach (var indicator in found.Indicators)
            foreach (Match m in Regex.Matches(text, AsWritten(indicator.Value), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
                marks.Add((m.Index, m.Length, ReadingMark.Indicator));
        var weRun = new List<string>();
        foreach (var e in stack)
        {
            var named = false;
            foreach (var term in e.Terms.Where(t => t.Trim().Length > 1))
                foreach (Match m in Regex.Matches(text, $@"(?<![\w-]){Regex.Escape(term.Trim())}(?![\w-])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
                {
                    marks.Add((m.Index, m.Length, ReadingMark.WeRun));
                    named = true;
                }
            if (named) weRun.Add(e.Name);
        }

        // Earliest first, the longest where two start together; a run inside one already taken is dropped.
        var spans = new List<ReadingSpan>();
        var pos = 0;
        foreach (var (at0, length, mark) in marks.OrderBy(m => m.At).ThenByDescending(m => m.Length))
        {
            if (at0 < pos) continue;
            if (at0 > pos) spans.Add(new(text[pos..at0], ReadingMark.None));
            spans.Add(new(text.Substring(at0, length), mark));
            pos = at0 + length;
        }
        if (pos < text.Length) spans.Add(new(text[pos..], ReadingMark.None));
        return new(itemId, spans, text.Length, how, at, found.Cves, found.Indicators, weRun);
    }

    // A pattern for an indicator as an author may have written it: any dot, colon or "@" plain or defanged ("[.]",
    // "(dot)", "[:]", "[at]"), and http as hxxp.
    private static string AsWritten(string value)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (i == 0 && value.StartsWith("http", StringComparison.OrdinalIgnoreCase)) { sb.Append("h(?:tt|xx)p"); i += 3; }
            else if (c == '.') sb.Append(@"(?:\.|[\[\(\{]\s*(?:\.|dot)\s*[\]\)\}])");
            else if (c == ':') sb.Append(@"(?::|[\[\(\{]\s*:\s*[\]\)\}])");
            else if (c == '@') sb.Append(@"(?:@|[\[\(\{]\s*(?:@|at)\s*[\]\)\}])");
            else sb.Append(Regex.Escape(c.ToString()));
        }
        return sb.ToString();
    }

    [GeneratedRegex(@"\bCVE-\d{4}-\d{4,7}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CveRx();

    private void RequireTriage()
    {
        if (!user.Has(Permission.WriteArticles)) throw new ForbiddenException("Only analysts triage the inbox.");
    }
}
