using System.Data.Common;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tide.Application.Abstractions;
using Tide.Application.Actions;
using Tide.Application.Articles;
using Tide.Application.Operations;
using Tide.Application.Security;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.Application.Intake;

/// <summary>A source as the admin form edits it.</summary>
public sealed class SourceForm
{
    public Guid? Id { get; set; }
    public string Name { get; set; } = "";
    public SourceKind Kind { get; set; } = SourceKind.Feed;
    public string Url { get; set; } = "";
    public TlpLevel DefaultTlp { get; set; } = TlpLevel.Clear;
    public SourceReliability Reliability { get; set; } = SourceReliability.F;
    public string? LicenseNote { get; set; }
    public int PollMinutes { get; set; } = 60;
    public bool Enabled { get; set; } = true;

    /// <summary>A feed's tag filter as typed ("Security, Microsoft"); see <see cref="CategoryFilter"/>.</summary>
    public string? OnlyCategories { get; set; }
    public string? SkipCategories { get; set; }

    /// <summary>The title half of the filter as typed ("ransomware, zero-day"); see <see cref="FeedFilter"/>.</summary>
    public string? OnlyTitleWords { get; set; }
    public string? SkipTitleWords { get; set; }

    /// <summary>Whether an article's page may be fetched for drafting, and the selector for its body (see <see cref="Source"/>).</summary>
    public ArticleFetch ArticleFetch { get; set; } = ArticleFetch.TideOnly;
    public string? ArticleBodySelector { get; set; }

    /// <summary>What else to take out of its article pages (see <see cref="Source.ArticleRemoveSelector"/>).</summary>
    public string? ArticleRemoveSelector { get; set; }

    public static SourceForm From(Source s) => new()
    {
        Id = s.Id, Name = s.Name, Kind = s.Kind, Url = s.Url, DefaultTlp = s.DefaultTlp, Reliability = s.Reliability, LicenseNote = s.LicenseNote,
        PollMinutes = s.PollMinutes, Enabled = s.Enabled,
        OnlyCategories = CategoryFilter.Show(s.OnlyCategories), SkipCategories = CategoryFilter.Show(s.SkipCategories),
        OnlyTitleWords = CategoryFilter.Show(s.OnlyTitleWords), SkipTitleWords = CategoryFilter.Show(s.SkipTitleWords),
        ArticleFetch = s.ArticleFetch, ArticleBodySelector = s.ArticleBodySelector, ArticleRemoveSelector = s.ArticleRemoveSelector,
    };
}

/// <summary>What one poll of a source did.</summary>
public sealed record PollResult(int New, int Seen, string? Error)
{
    /// <summary>Items the source's filter left out, the sponsored ones among them.</summary>
    public int FilteredOut { get; init; }

    /// <summary>Of <see cref="FilteredOut"/>, the items left out as sponsored (<see cref="SponsoredFilter"/>).</summary>
    public int Sponsored { get; init; }
}

/// <summary>What the inbox shows. <see cref="Since"/> keeps items published (or, undated, fetched) at or after it.
/// <see cref="Older"/> (the New tab only) shows the items that aged out of New instead of those still in it.
/// <see cref="Sort"/> <see cref="InboxSort.Likely"/> (New and Shortlisted) orders by what the team promoted before (EP-81).
/// <see cref="Cves"/> keeps items naming a CVE (or a KEV-listed one), <see cref="Named"/> those naming a product on Our
/// stack. <see cref="Page"/> is which <see cref="Take"/> rows of them, counting from 1.</summary>
public sealed record InboxFilter(FeedItemStatus Status = FeedItemStatus.New, Guid? SourceId = null, string? Search = null, int Take = InboxFilter.PageSize,
    DateTimeOffset? Since = null, bool Older = false, InboxSort Sort = InboxSort.Newest, int Page = 1, InboxCves Cves = InboxCves.All, bool Named = false)
{
    /// <summary>Rows on a page of the inbox.</summary>
    public const int PageSize = 50;
}

/// <summary>The inbox's CVE filter: every item, those naming any CVE, or those naming one on CISA's KEV list.</summary>
public enum InboxCves
{
    All = 0,
    Any = 1,
    Kev = 2,
}

/// <summary>A page of the inbox: its rows, how many items match in all, and which page it is (the last, when the one
/// asked for is past the end).</summary>
public sealed record InboxPage(IReadOnlyList<InboxRow> Rows, int Total, int Page);

/// <summary>
/// Where a feed item overlaps what's already been covered: an article (and the first brief it was on, if any), the
/// numbers of the actions raised from it that are still open, and whether a report can still be attached to it (it's
/// a draft or ready, and no brief has been presented or sent with it).
/// </summary>
public sealed record Coverage(string Why, Guid ArticleId, string ArticleTitle, DateOnly? BriefDate)
{
    public IReadOnlyList<int> OpenActions { get; init; } = [];
    public bool Attachable { get; init; } = true;
}

/// <summary>An article a triage item can be attached to as another report: a recent draft or ready one, or one on today's brief.</summary>
public sealed record AttachTarget(Guid Id, string Title, ArticleStatus Status, string AuthorName, DateTimeOffset CreatedAtUtc, bool OnBrief);

/// <summary>
/// An inbox row: the item, its source, a similar item it may repeat, past coverage it overlaps, (in the New tab) the
/// other new items that share a CVE with it: one story told by several outlets, and (in New and Shortlisted) the
/// suggested group it's in.
/// </summary>
public sealed record InboxRow(FeedItem Item, Source Source, FeedItem? Similar, IReadOnlyList<Coverage> Coverage,
    IReadOnlyList<Guid>? SameCve = null)
{
    public IReadOnlyList<Guid> SameCve { get; init; } = SameCve ?? [];

    /// <summary>The CVE <see cref="SameCve"/>'s items share with this one (its first, when it shares several), or null.</summary>
    public string? SharedCve { get; init; }

    /// <summary>"Looks like one event": the group <see cref="TriageGroups"/> suggests for the item, the same one on each member's row.</summary>
    public TriageGroup? Group { get; init; }

    /// <summary>The latest split ("Not the same", EP-84) this item was the newest report of, among the items shown: who
    /// split it and when, for its note and Undo.</summary>
    public SplitNote? Split { get; init; }

    /// <summary>The product from Our stack the item names, or null.</summary>
    public Stack.StackEntry? WeRun { get; init; }

    /// <summary>The watched adversary profile the item names, or null (EP-73).</summary>
    public Stack.StackEntry? Watching { get; init; }

    /// <summary>How likely the team is to use it, and why, when sorted by that and there's enough history (EP-81).</summary>
    public Likelihood? Likely { get; init; }

}

/// <summary>A split of a suggested group, as triage notes it: the split's batch (what Undo takes back), who split it and
/// when, and how many of its reports are shown.</summary>
public sealed record SplitNote(Guid Batch, string ByName, DateTimeOffset AtUtc, int Reports);

/// <summary>
/// The intake pipeline: administered sources are polled (off until <c>Intake:Enabled</c>), new items land in the
/// triage inbox once each (by canonical address), an item that looks like a recent one is flagged, and an item that
/// mentions a CVE already briefed says so. Analysts shortlist, dismiss, mark duplicates, and promote items to draft
/// articles. Old items nobody kept are pruned.
/// </summary>
public sealed class IntakeService(IAppDbContextFactory dbs, ICurrentUser user, IOutboundHttp http, ArticleService articles,
    IOptionsMonitor<IntakeOptions> options, TimeProvider time, SponsoredLog? sponsoredLog = null, SourcePollGuard? polls = null)
{
    // The app's one guard (a singleton); a service made without one guards only its own polls.
    private readonly SourcePollGuard _polls = polls ?? new SourcePollGuard();

    /// <summary>How far back a new item is compared with others for a similar title.</summary>
    public static readonly TimeSpan SimilarWindow = TimeSpan.FromDays(7);

    /// <summary>The reasons offered for dismissing an item (any short text is accepted).</summary>
    public static readonly IReadOnlyList<string> DismissReasons = ["Not relevant", "Already covered", "Out of scope", "Low quality", "Paywalled"];

    // The longest validators the source's columns hold (AppDbContext). A longer one is dropped rather than cut, since a
    // cut validator matches nothing: the next poll is then a plain one, where writing it would fail the bookkeeping.
    private const int MaxETag = 200, MaxLastModified = 100;

    // --- sources -------------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<Source>> SourcesAsync(bool includeRetired = false, CancellationToken ct = default)
    {
        if (!user.Has(Permission.WriteArticles) && !user.Has(Permission.Administer)) throw new ForbiddenException("You don't have access to the sources.");
        await using var db = await dbs.CreateAsync(ct);
        return await ByName(db.Sources.AsNoTracking().Where(s => includeRetired || !s.IsRetired)).ToListAsync(ct);
    }

    // Sources by name ignoring case, as SQL Server's collation sorts them: SQLite's own order is case-sensitive, which put
    // "alpha blog" after "Zeta wire". ToLower runs in the database (lower(), LOWER()), never in .NET, so no culture applies.
#pragma warning disable CA1304, CA1311
    private static IOrderedQueryable<Source> ByName(IQueryable<Source> sources) => sources.OrderBy(s => s.Name.ToLower()).ThenBy(s => s.Name);
#pragma warning restore CA1304, CA1311

    public async Task<Guid> SaveSourceAsync(SourceForm form, CancellationToken ct = default)
    {
        RequireAdmin();
        var name = ArticleService.Clean(form.Name, 100);
        if (name.Length == 0) throw new ArgumentException("A source needs a name.");
        var url = ArticleService.CheckUrl(form.Url, "The feed address") ?? throw new ArgumentException("A source needs its feed address.");
        if (!Enum.IsDefined(form.Kind) || !Enum.IsDefined(form.DefaultTlp) || !Enum.IsDefined(form.Reliability)) throw new ArgumentException("Choose a kind, a TLP and a reliability.");
        if (!Enum.IsDefined(form.ArticleFetch)) throw new ArgumentException("Choose how articles from it may be fetched.");
        static string? Selector(string? typed, int max) => ArticleService.CleanOrNull(typed, max) is not { } clean ? null
            : Assist.ArticleText.IsSelector(clean) ? clean : throw new ArgumentException($"“{clean}” isn't a CSS selector TIDE can use.");
        var selector = Selector(form.ArticleBodySelector, Source.MaxBodySelector);
        var remove = Selector(form.ArticleRemoveSelector, Source.MaxRemoveSelector);
        var min = MinPollMinutes;
        if (form.PollMinutes < min || form.PollMinutes > 7 * 24 * 60) throw new ArgumentException($"Poll every {min} minutes to once a week.");
        await using var db = await dbs.CreateAsync(ct);
        // Matched ignoring case on both providers (LIKE, as search does: ADR 0011): SQL Server's collation would refuse
        // "news" beside "News" at the unique index anyway, so the friendly message comes first there, and SQLite agrees.
        var exact = Search.SearchText.Literal(name);
        if (await db.Sources.AnyAsync(s => EF.Functions.Like(s.Name, exact, Search.SearchText.Escape) && s.Id != form.Id, ct))
            throw new ArgumentException($"There's already a source called {name}.");
        Source source;
        if (form.Id is { } id) source = await SourceAsync(db, id, ct);
        else db.Sources.Add(source = new Source());
        // The filter applies to feeds (the KEV catalog is a list of CVEs, not news). A new address, or a changed filter,
        // forgets the feed's validators, so the next poll reads it whole and takes in what the old filter left out.
        string? Rule(string? typed) => form.Kind == SourceKind.Feed ? CategoryFilter.Store(typed) : null;
        var (only, skip, onlyWords, skipWords) = (Rule(form.OnlyCategories), Rule(form.SkipCategories), Rule(form.OnlyTitleWords), Rule(form.SkipTitleWords));
        if (source.Url != url || only != source.OnlyCategories || skip != source.SkipCategories || onlyWords != source.OnlyTitleWords
            || skipWords != source.SkipTitleWords)
        {
            source.ETag = null;
            source.LastModified = null;
        }
        (source.OnlyCategories, source.SkipCategories, source.OnlyTitleWords, source.SkipTitleWords) = (only, skip, onlyWords, skipWords);
        source.Name = name;
        source.Kind = form.Kind;
        source.Url = url;
        source.DefaultTlp = Tlp.Checked(form.DefaultTlp);
        source.Reliability = form.Reliability;
        source.LicenseNote = ArticleService.CleanOrNull(form.LicenseNote, 500);
        source.PollMinutes = form.PollMinutes;
        source.Enabled = form.Enabled;
        source.ArticleFetch = form.ArticleFetch;
        source.ArticleBodySelector = selector;
        source.ArticleRemoveSelector = remove;
        await db.SaveChangesAsync(ct);
        return source.Id;
    }

    /// <summary>Switches a source's polling on or off (Source health's "Pause polling"); its items stay.</summary>
    public async Task SetSourceEnabledAsync(Guid id, bool enabled, CancellationToken ct = default)
    {
        RequireAdmin();
        await using var db = await dbs.CreateAsync(ct);
        var source = await SourceAsync(db, id, ct);
        source.Enabled = enabled;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Retires (or brings back) a source. One that never produced an item is deleted instead.</summary>
    public async Task SetSourceRetiredAsync(Guid id, bool retired, CancellationToken ct = default)
    {
        RequireAdmin();
        await using var db = await dbs.CreateAsync(ct);
        var source = await SourceAsync(db, id, ct);
        if (retired && !await db.FeedItems.AnyAsync(i => i.SourceId == id, ct)) db.Sources.Remove(source);
        else
        {
            source.IsRetired = retired;
            if (retired) source.Enabled = false;
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Adds the <see cref="StarterSources"/> named, each paused and TLP:CLEAR, polled no more often than the shortest poll
    /// allowed. One already here, by name (in any case) or address, retired or not, is left as it is. Returns how many
    /// were added.
    /// </summary>
    public async Task<int> AddStarterSourcesAsync(IEnumerable<string> names, CancellationToken ct = default)
    {
        RequireAdmin();
        var picked = names.ToHashSet(StringComparer.Ordinal);
        await using var db = await dbs.CreateAsync(ct);
        var here = await db.Sources.AsNoTracking().Select(s => new { s.Name, s.Url }).ToListAsync(ct);
        var min = MinPollMinutes;
        var added = 0;
        foreach (var s in StarterSources.All.Where(s => picked.Contains(s.Name)))
        {
            if (here.Any(h => string.Equals(h.Name, s.Name, StringComparison.OrdinalIgnoreCase) || h.Url == s.Url)) continue;
            db.Sources.Add(new Source
            {
                Name = s.Name, Kind = s.Kind, Url = s.Url, DefaultTlp = TlpLevel.Clear, Reliability = s.Reliability,
                PollMinutes = Math.Max(s.PollMinutes, min), Enabled = false,
            });
            added++;
        }
        await db.SaveChangesAsync(ct);
        return added;
    }

    private const string NoFeed = "Items come to this source through the API: there's no feed to poll.";
    private const string Busy = "This source is being pulled now. Its new items arrive in a moment.";

    /// <summary>An administrator polls a source now (enabled or not, and whether or not intake is switched on), on
    /// Administration › Sources: not held to the analyst pull-now window. Refused only while it's being polled.</summary>
    public async Task<PollResult> PollNowAsync(Guid id, CancellationToken ct = default)
    {
        RequireAdmin();
        // A source the API proposes to has no feed: "polling" its placeholder address only recorded a failure on it.
        await using (var db = await dbs.CreateAsync(ct))
            if (await db.Sources.AsNoTracking().AnyAsync(s => s.Id == id && s.Kind == SourceKind.Api, ct))
                throw new InvalidOperationException(NoFeed);
        return await ExclusiveAsync(id, () => PollAsync(id, ct)) ?? throw new InvalidOperationException(Busy);
    }

    /// <summary>The analysts' pull-now window (<see cref="IntakeOptions.PullNowMinutes"/>, at least a minute): a source
    /// pulled within it, on schedule or by hand, can't be pulled from Triage again until it's passed.</summary>
    public TimeSpan PullWindow => TimeSpan.FromMinutes(Math.Max(1, options.CurrentValue.PullNowMinutes));

    /// <summary>
    /// The sources that are polled, for Triage's Sources tab: enabled feeds and the KEV catalog, by name. Paused and
    /// retired sources aren't polled and aren't listed, nor are those the API proposes to (they have no feed).
    /// </summary>
    public async Task<IReadOnlyList<PolledSource>> PolledSourcesAsync(CancellationToken ct = default)
    {
        RequireTriage();
        await using var db = await dbs.CreateAsync(ct);
        var sources = await ByName(Polled(db.Sources.AsNoTracking())).ToListAsync(ct);
        var window = PullWindow;
        return [.. sources.Select(s => new PolledSource(s.Id, s.Name, s.Kind, s.LastPolledAtUtc, s.LastPolledAtUtc + Interval(s),
            s.LastSuccessAtUtc is null ? null : s.LastNewItems, s.LastError, s.LastPolledAtUtc + window, _polls.IsRunning(s.Id)))];
    }

    /// <summary>
    /// An analyst pulls a polled source now, from Triage (whether or not intake is switched on). Refused within the
    /// pull-now window of its last pull, scheduled or forced, for everyone (administrators too: theirs without the window
    /// is <see cref="PollNowAsync"/>), and while it's being pulled, so two people pressing at once make one pull.
    /// </summary>
    public async Task<PollResult> PullNowAsync(Guid id, CancellationToken ct = default)
    {
        RequireTriage();
        return await ExclusiveAsync(id, async () =>
        {
            // Read once the source is ours: a pull that finished a moment ago has moved its last pull.
            Source source;
            await using (var db = await dbs.CreateAsync(ct))
                source = await db.Sources.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct) ?? throw new KeyNotFoundException("That source no longer exists.");
            if (source.Kind == SourceKind.Api) throw new InvalidOperationException(NoFeed);
            if (!source.Enabled || source.IsRetired) throw new InvalidOperationException("That source is paused, so it isn't pulled. An administrator turns it on under Administration › Sources.");
            var now = time.GetUtcNow();
            if (source.LastPolledAtUtc + PullWindow is { } pullable && now < pullable)
                throw new PullTooSoonException(PullTimes.TooSoon(source.LastPolledAtUtc!.Value, pullable, now), pullable);
            return await PollAsync(id, ct);
        }) ?? throw new InvalidOperationException(Busy);
    }

    // Runs a poll of the source unless it's being polled already; null when it was.
    private async Task<PollResult?> ExclusiveAsync(Guid sourceId, Func<Task<PollResult>> poll)
    {
        if (!_polls.TryStart(sourceId)) return null;
        try { return await poll(); }
        finally { _polls.Finished(sourceId); }
    }

    private static IQueryable<Source> Polled(IQueryable<Source> sources) =>
        sources.Where(s => s.Enabled && !s.IsRetired && s.Kind != SourceKind.Api);

    // --- polling ----------------------------------------------------------------------------------------------

    /// <summary>Polls every enabled source that's due, when intake is switched on. Returns how many new items came in.</summary>
    public async Task<int> PollDueAsync(CancellationToken ct = default)
    {
        if (!options.CurrentValue.Enabled) return 0;
        var now = time.GetUtcNow();
        List<Guid> due;
        await using (var db = await dbs.CreateAsync(ct))
        {
            var sources = await Polled(db.Sources.AsNoTracking()).ToListAsync(ct);
            due = sources.Where(s => s.LastPolledAtUtc is null || now - s.LastPolledAtUtc >= Interval(s)).Select(s => s.Id).ToList();
        }
        var added = 0;
        // One being pulled by hand at this moment is left to that pull. The pass takes the sources one at a time, so each is
        // read again once it's ours: one pulled (or paused) since the list was read isn't polled again straight after.
        foreach (var id in due)
            added += (await ExclusiveAsync(id, async () => await StillDueAsync(id, ct) ? await PollAsync(id, ct) : NotPolled))?.New ?? 0;
        return added;
    }

    private static readonly PollResult NotPolled = new(0, 0, null);

    private async Task<bool> StillDueAsync(Guid id, CancellationToken ct)
    {
        await using var db = await dbs.CreateAsync(ct);
        var source = await Polled(db.Sources.AsNoTracking()).FirstOrDefaultAsync(s => s.Id == id, ct);
        return source is not null && (source.LastPolledAtUtc is null || time.GetUtcNow() - source.LastPolledAtUtc >= Interval(source));
    }

    /// <summary>A source's interval, doubled for each failure in a row (up to 16 times), so a dead feed is asked less.</summary>
    public TimeSpan Interval(Source s) =>
        TimeSpan.FromMinutes(Math.Max(MinPollMinutes, s.PollMinutes) * (1 << Math.Clamp(s.ConsecutiveFailures, 0, 4)));

    // The shortest poll allowed: the configured one, never under five minutes.
    private int MinPollMinutes => Math.Max(5, options.CurrentValue.MinPollMinutes);

    private static async Task<Source> SourceAsync(IAppDbContext db, Guid id, CancellationToken ct) =>
        await db.Sources.FirstOrDefaultAsync(s => s.Id == id, ct) ?? throw new KeyNotFoundException("That source no longer exists.");

    // A poll's error as the source's column holds it.
    private static string StoredError(string error) => error.Length > 500 ? error[..500] : error;

    // One source's failure is its own: anything the poll didn't expect (a feed that breaks the parser in a new way, a
    // database error) is recorded on the source, which backs off as for a failed fetch, and the other sources still run.
    private async Task<PollResult> PollAsync(Guid sourceId, CancellationToken ct)
    {
        try
        {
            return await PollOnceAsync(sourceId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // An exception's text can quote the address asked for, key and all: scrubbed before it's stored or shown.
            var error = SecretMask.Scrub("The poll failed: " + ex.Message);
            if (!await RecordFailureAsync(sourceId, error, ct) && ex is KeyNotFoundException) throw;   // the source is gone
            return new PollResult(0, 0, error);
        }
    }

    // False when the source no longer exists. A database that can't be reached to record it leaves it unrecorded (the
    // source is tried again next time), not thrown into the loop. Written without the audit trail, as a poll is.
    private async Task<bool> RecordFailureAsync(Guid sourceId, string error, CancellationToken ct)
    {
        try
        {
            await using var db = await dbs.CreateAsync(ct);
            var stored = StoredError(error);
            return await db.Sources.Where(s => s.Id == sourceId).ExecuteUpdateAsync(u => u
                .SetProperty(s => s.LastPolledAtUtc, time.GetUtcNow()).SetProperty(s => s.LastError, stored)
                .SetProperty(s => s.ConsecutiveFailures, s => s.ConsecutiveFailures + 1), ct) > 0;
        }
        catch (Exception ex) when (ex is DbException or DbUpdateException or InvalidOperationException)
        {
            return true;
        }
    }

    private async Task<PollResult> PollOnceAsync(Guid sourceId, CancellationToken ct)
    {
        await using var db = await dbs.CreateAsync(ct);
        // Untracked: what a poll notes on the source is bookkeeping, written at the end without the audit trail, so the
        // trail keeps an administrator's changes to a source and not one row per source per poll.
        var source = await db.Sources.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sourceId, ct) ?? throw new KeyNotFoundException("That source no longer exists.");
        var now = time.GetUtcNow();
        source.LastPolledAtUtc = now;
        var fetch = await http.GetAsync(source.Url, new OutboundRequest(source.ETag, source.LastModified), ct);
        PollResult result;
        if (fetch.Status == FetchStatus.NotModified)
        {
            result = new PollResult(0, 0, null);
        }
        else if (fetch.Status == FetchStatus.Failed || fetch.Content is null)
        {
            result = new PollResult(0, 0, fetch.Error ?? "The fetch failed.");
        }
        else
        {
            try
            {
                using var ms = new MemoryStream(fetch.Content, writable: false);
                var parsed = source.Kind == SourceKind.CisaKev ? FeedParser.KevItems(FeedParser.ParseKev(ms)) : FeedParser.ParseFeed(ms);
                var kept = parsed;
                var sponsored = new List<SponsoredSkip>();
                if (source.Kind == SourceKind.Feed)
                {
                    source.SeenCategories = CategoryFilter.Seen(parsed);
                    var filter = FeedFilter.For(source);
                    var skip = SponsoredFilter.For(options.CurrentValue);
                    kept = [.. parsed.Where(filter.Keeps).Where(p =>
                    {
                        if (skip?.Match(p) is not { } match) return true;
                        sponsored.Add(new SponsoredSkip(p.Title, match.Marker, match.Field));
                        return false;
                    })];
                    sponsoredLog?.Record(source.Id, sponsored);
                }
                var added = await StoreAsync(db, source.Id, kept, now, RetentionCutoff(now), ct);
                source.ETag = fetch.ETag is { Length: <= MaxETag } etag ? etag : null;
                source.LastModified = fetch.LastModified is { Length: <= MaxLastModified } since ? since : null;
                result = new PollResult(added, parsed.Count, null) { FilteredOut = parsed.Count - kept.Count, Sponsored = sponsored.Count };
            }
            catch (FormatException ex)
            {
                result = new PollResult(0, 0, ex.Message);
            }
        }
        if (result.Error is null)
        {
            source.LastSuccessAtUtc = now;
            source.LastError = null;
            source.ConsecutiveFailures = 0;
            source.LastNewItems = result.New;
            if (fetch.Status != FetchStatus.NotModified) source.LastFilteredOut = result.FilteredOut;
        }
        else source.LastError = StoredError(SecretMask.Scrub(result.Error));
        await db.SaveChangesAsync(ct);   // the new items
        var ok = result.Error is null;
        var fetched = ok && fetch.Status != FetchStatus.NotModified;
        await db.Sources.Where(s => s.Id == source.Id).ExecuteUpdateAsync(u =>
        {
            u.SetProperty(s => s.LastPolledAtUtc, source.LastPolledAtUtc).SetProperty(s => s.LastError, source.LastError);
            if (!ok) u.SetProperty(s => s.ConsecutiveFailures, s => s.ConsecutiveFailures + 1);
            else u.SetProperty(s => s.ConsecutiveFailures, 0).SetProperty(s => s.LastSuccessAtUtc, source.LastSuccessAtUtc).SetProperty(s => s.LastNewItems, source.LastNewItems);
            if (fetched)
                u.SetProperty(s => s.LastFilteredOut, source.LastFilteredOut).SetProperty(s => s.ETag, source.ETag)
                    .SetProperty(s => s.LastModified, source.LastModified).SetProperty(s => s.SeenCategories, source.SeenCategories);
        }, ct);
        return result;
    }

    // Adds the items not seen before (by canonical address), flagging ones whose title matches a recent item's. An item
    // published before the retention cutoff isn't added: pruning forgets old items, and a feed still listing one (the KEV
    // catalog lists a year or more) would otherwise bring it back as new, dismissals and all. An item dated after the
    // fetch (a wrong year, a time zone read the wrong way, a post scheduled ahead) is dated when it was fetched: the inbox
    // is newest first, so a date in the future held it at the top of New until it was pruned, above everything that came
    // in after it, and the article promoted from it said it was published on a day still to come.
    private static async Task<int> StoreAsync(IAppDbContext db, Guid sourceId, IReadOnlyList<ParsedItem> parsed, DateTimeOffset now,
        DateTimeOffset retentionCutoff, CancellationToken ct)
    {
        var candidates = parsed
            .Select(p => p.PublishedAtUtc > now ? p with { PublishedAtUtc = now } : p)
            .Where(p => p.PublishedAtUtc is not { } published || published >= retentionCutoff)
            .Select(p => (Item: p, Url: UrlCanonicalizer.Canonicalize(p.Url)))
            .Where(x => x.Url is not null)
            .Select(x => (x.Item, Url: x.Url!, Hash: UrlCanonicalizer.Hash(x.Url!)))
            .DistinctBy(x => x.Hash)
            .ToList();
        if (candidates.Count == 0) return 0;
        var hashes = candidates.Select(c => c.Hash).ToList();
        var seen = (await db.FeedItems.AsNoTracking().Where(i => hashes.Contains(i.UrlHash)).Select(i => i.UrlHash).ToListAsync(ct)).ToHashSet();
        var since = now - SimilarWindow;
        var recent = await db.FeedItems.AsNoTracking().Where(i => i.FetchedAtUtc >= since).Select(i => new { i.Id, i.TitleKey }).ToListAsync(ct);
        var keys = recent.Select(r => (r.Id, r.TitleKey)).ToList();
        var added = 0;
        foreach (var (p, url, hash) in candidates.Where(c => !seen.Contains(c.Hash)))
        {
            var key = TitleMatch.Key(p.Title);
            var similar = keys.FirstOrDefault(k => TitleMatch.Similarity(k.TitleKey, key) >= TitleMatch.Threshold);
            var item = new FeedItem
            {
                SourceId = sourceId, Url = url, UrlHash = hash, Title = p.Title, TitleKey = key.Length > 500 ? key[..500] : key,
                PublishedAtUtc = p.PublishedAtUtc, FetchedAtUtc = now, Excerpt = p.Summary,
                Cves = p.Cves.Count == 0 ? null : string.Join(',', p.Cves).Length > 1000 ? string.Join(',', p.Cves.Take(40)) : string.Join(',', p.Cves),
                SimilarToId = similar.Id == Guid.Empty ? null : similar.Id,
            };
            db.FeedItems.Add(item);
            keys.Add((item.Id, item.TitleKey));
            added++;
        }
        return added;
    }

    // --- the inbox ------------------------------------------------------------------------------------------------

    /// <summary>A page of items in one triage state, newest first, with their source, a similar item and past coverage.</summary>
    public async Task<IReadOnlyList<InboxRow>> InboxAsync(InboxFilter filter, CancellationToken ct = default) => (await InboxPageAsync(filter, ct)).Rows;

    /// <summary>The most a page number is taken as: beyond any real inbox, and small enough that the rows skipped can't overflow.</summary>
    private const int MaxPage = 100_000;

    /// <summary>
    /// A page of the inbox (<see cref="InboxFilter.Page"/>, <see cref="InboxFilter.Take"/> rows), with how many items
    /// match in all: the count is always the whole of it, never a silent cut. A page past the end is the last page.
    /// </summary>
    public async Task<InboxPage> InboxPageAsync(InboxFilter filter, CancellationToken ct = default)
    {
        RequireTriage();
        await using var db = await dbs.CreateAsync(ct);
        var q = db.FeedItems.AsNoTracking().Where(i => i.Status == filter.Status);
        // A retired source's new items leave the inbox (what was shortlisted or decided stays).
        if (filter.Status == FeedItemStatus.New) q = q.Where(i => !i.Source!.IsRetired);
        if (filter.SourceId is { } sid) q = q.Where(i => i.SourceId == sid);
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            // LIKE, not Contains: the same case-insensitive match on SQLite and SQL Server (ADR 0011), wildcards literal.
            var p = Search.SearchText.Contains(filter.Search.Trim());
            q = q.Where(i => EF.Functions.Like(i.Title, p, Search.SearchText.Escape) || EF.Functions.Like(i.Cves!, p, Search.SearchText.Escape));
        }
        // The filters and the page run in the database where they can: a tab that's never pruned (Promoted) grows for years.
        if (filter.Since is { } since) q = q.Where(i => (i.PublishedAtUtc ?? i.FetchedAtUtc) >= since);
        if (filter.Cves != InboxCves.All) q = q.Where(i => i.Cves != null && i.Cves != "");
        // The id breaks ties, so an item is on one page only.
        q = q.OrderByDescending(i => i.PublishedAtUtc ?? i.FetchedAtUtc).ThenByDescending(i => i.Id);
        var take = Math.Clamp(filter.Take, 1, 500);
        var page = Math.Clamp(filter.Page, 1, MaxPage);
        var stack = await Stack.StackService.EntriesAsync(db, ct);
        var weRun = Stack.StackService.Matcher(stack);
        var watched = Stack.StackService.Matcher(await Adversaries.AdversaryService.WatchedAsync(db, ct));
        var likelySort = filter.Sort == InboxSort.Likely && filter.Status is FeedItemStatus.New or FeedItemStatus.Shortlisted;
        // What only this process can tell (which items aged into Older, which name what we run, which CVEs are on KEV,
        // what the team used before) is worked out over every matching item, read light: only what matching, ageing and
        // grouping need. The rows shown are read in full below. Otherwise the database counts and cuts the page.
        var light = filter.Status == FeedItemStatus.New || likelySort || filter.Named || filter.Cves == InboxCves.Kev;
        List<FeedItem> all, pageItems;
        int total;
        Dictionary<Guid, Stack.StackEntry?> named;
        Dictionary<Guid, Likelihood?>? likely = null;
        if (light)
        {
            all = await Light(q).ToListAsync(ct);
            named = all.ToDictionary(i => i.Id, i => weRun(i.Title, i.Excerpt));
            if (filter.Status == FeedItemStatus.New)
            {
                // Untouched items fold into Older after a while (never one naming what we run); what names it comes first.
                var aged = await AgedAsync(db, all.Where(i => named[i.Id] is null).ToList(), ct);
                all = all.Where(i => aged.Contains(i.Id) == filter.Older).OrderBy(i => named[i.Id] is null).ToList();
            }
            var view = all;   // the New tab's "same CVE" looks across the view, past the two filters below
            if (filter.Named) all = all.Where(i => named[i.Id] is not null).ToList();
            HashSet<string>? kev = null;
            if (filter.Cves == InboxCves.Kev)
            {
                // KEV as the CVE records TIDE keeps say it (the row's KEV chip reads the same records).
                kev = await KevAsync(db, all, ct);
                all = all.Where(i => i.CveList.Any(kev.Contains)).ToList();
            }
            if (likelySort)
            {
                // What names a product we run, a watched adversary or a KEV-listed CVE stays on top; the rest by the share
                // promoted from items like it, newest first among equals (the order the list was already in).
                var learned = await LearnedAsync(db, stack, ct);
                var sources = await db.Sources.AsNoTracking().ToDictionaryAsync(s => s.Id, ct);
                likely = all.ToDictionary(i => i.Id, i => learned.For(sources[i.SourceId], named[i.Id]));
                kev ??= await KevAsync(db, all, ct);
                var watches = all.ToDictionary(i => i.Id, i => watched(i.Title, i.Excerpt) is not null);
                all = all.OrderBy(i => named[i.Id] is null).ThenBy(i => !watches[i.Id]).ThenBy(i => !i.CveList.Any(kev.Contains))
                    .ThenByDescending(i => likely[i.Id]?.Score ?? 0.5).ToList();
            }
            total = all.Count;
            page = Math.Min(page, Pages(total, take));
            pageItems = all.Skip((page - 1) * take).Take(take).ToList();
            all = view;
        }
        else
        {
            total = await q.CountAsync(ct);
            page = Math.Min(page, Pages(total, take));
            all = pageItems = await Light(q.Skip((page - 1) * take).Take(take)).ToListAsync(ct);
            named = all.ToDictionary(i => i.Id, i => weRun(i.Title, i.Excerpt));
        }
        var watching = pageItems.ToDictionary(i => i.Id, i => watched(i.Title, i.Excerpt));
        var shownIds = pageItems.Select(i => i.Id).ToList();
        var full = await db.FeedItems.AsNoTracking().Include(i => i.Source).Where(i => shownIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);
        var items = shownIds.Where(full.ContainsKey).Select(id => full[id]).ToList();
        var byCve = filter.Status == FeedItemStatus.New
            ? all.SelectMany(i => i.CveList.Select(c => (Cve: c, i.Id))).ToLookup(x => x.Cve, x => x.Id)
            : Enumerable.Empty<(string Cve, Guid Id)>().ToLookup(x => x.Cve, x => x.Id);
        var similarIds = items.Select(i => i.SimilarToId).OfType<Guid>().Distinct().ToList();
        var similar = await db.FeedItems.AsNoTracking().Where(i => similarIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);
        var coverage = await CoverageAsync(db, items, ct);
        // Reports that look like one event, among the items still to decide: worked out now, from what's shown, and never
        // stored. What's shown is this page: a group's reports came out close together, so they share a page, unless the
        // page ends among them (the rest group on the next). Only what someone split is (EP-84): those reports never group
        // with each other. A split matters here when two or more of its reports are shown.
        var grouping = filter.Status is FeedItemStatus.New or FeedItemStatus.Shortlisted;
        var itemIds = items.Select(i => i.Id).ToList();
        var splits = grouping
            ? (await db.TriageSplits.AsNoTracking().Where(s => itemIds.Contains(s.ItemId)).ToListAsync(ct))
                .GroupBy(s => s.Batch).Where(g => g.Count() >= 2).Select(g => g.ToList()).ToList()
            : [];
        var groups = grouping
            ? TriageGroups.Find(items.Select(i => new GroupCandidate(i.Id, i.Title, i.SourceId, i.Source!.Name, i.PublishedAtUtc ?? i.FetchedAtUtc, i.CveList.ToList())).ToList(),
                options.CurrentValue.Grouping() with { Split = [.. splits.Select(b => b.Select(s => s.ItemId).ToList())] })
            : [];
        var groupOf = groups.SelectMany(g => g.Ids.Select(id => (id, g))).ToDictionary(x => x.id, x => x.g);
        var splitOf = SplitNotes(items, splits);
        var rows = items.Select(i => new InboxRow(i, i.Source!, i.SimilarToId is { } s ? similar.GetValueOrDefault(s) : null,
            coverage.GetValueOrDefault(i.Id) ?? [], i.CveList.SelectMany(c => byCve[c]).Where(id => id != i.Id).Distinct().ToList())
        {
            SharedCve = i.CveList.FirstOrDefault(c => byCve[c].Any(id => id != i.Id)),
            Group = groupOf.GetValueOrDefault(i.Id),
            Split = splitOf.GetValueOrDefault(i.Id),
            WeRun = named[i.Id],
            Watching = watching[i.Id],
            Likely = likely?.GetValueOrDefault(i.Id),
        }).ToList();
        return new InboxPage(rows, total, page);
    }

    // How many pages of take rows total items make (at least one).
    private static int Pages(int total, int take) => Math.Max(1, (total + take - 1) / take);

    // Each split among the items shown, noted on its newest report (where the group's line was): the latest split there.
    private static Dictionary<Guid, SplitNote> SplitNotes(List<FeedItem> items, List<List<TriageSplit>> splits)
    {
        var at = items.ToDictionary(i => i.Id, i => i.PublishedAtUtc ?? i.FetchedAtUtc);
        return splits
            .Select(b => (Newest: b.Select(s => s.ItemId).OrderByDescending(id => at[id]).ThenBy(id => id).First(),
                Note: new SplitNote(b[0].Batch, b[0].SplitByName, b[0].SplitAtUtc, b.Count)))
            .GroupBy(x => x.Newest)
            .ToDictionary(g => g.Key, g => g.MaxBy(x => x.Note.AtUtc).Note);
    }

    // What matching, ageing and grouping read of an item: no source, and only the start of the excerpt Match looks at.
    private static IQueryable<FeedItem> Light(IQueryable<FeedItem> q) => q.Select(i => new FeedItem
    {
        Id = i.Id, SourceId = i.SourceId, Title = i.Title, Cves = i.Cves, PublishedAtUtc = i.PublishedAtUtc, FetchedAtUtc = i.FetchedAtUtc,
        Status = i.Status, Excerpt = i.Excerpt == null ? null : i.Excerpt.Substring(0, Stack.StackService.SummaryStart),
    });

    // The window triage learns from: TriageLearning.WindowDays, or less when dismissed items aren't kept that long.
    private int LearningDays => Math.Min(TriageLearning.WindowDays, Math.Max(7, options.CurrentValue.RetentionDays));

    /// <summary>How long the learned counts are kept, so each keypress in Triage doesn't count the window's decisions again.</summary>
    internal static readonly TimeSpan LearnedFor = TimeSpan.FromMinutes(1);

    // The counts learned last, with the stack and window they were learned for, and when. A decision made through this
    // service drops them at once; one made elsewhere shows within LearnedFor.
    private (TriageLearning Learned, string Key, DateTimeOffset At)? _learned;

    private async Task<TriageLearning> LearnedAsync(IAppDbContext db, IReadOnlyList<Stack.StackEntry> stack, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var key = string.Join('\n', stack.Select(e => $"{e.Id}:{string.Join('|', e.Terms)}").Prepend(LearningDays.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        if (_learned is { } kept && kept.Key == key && now >= kept.At && now - kept.At < LearnedFor) return kept.Learned;
        var learned = await TriageLearning.LearnAsync(db, stack, now, LearningDays, ct);
        _learned = (learned, key, now);
        return learned;
    }

    private static async Task<HashSet<string>> KevAsync(IAppDbContext db, IReadOnlyList<FeedItem> items, CancellationToken ct)
    {
        var cves = items.SelectMany(i => i.CveList).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return cves.Count == 0 ? [] : (await db.CveRecords.AsNoTracking().Where(c => cves.Contains(c.CveId) && c.IsKev)
            .Select(c => c.CveId).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    // --- what the team's decisions say (EP-81) ----------------------------------------------------------------------

    /// <summary>Days with no item promoted or shortlisted, from a source at least this old, before pausing it is suggested.</summary>
    public const int PauseAfterDays = 60;

    /// <summary>Items a source must have sent in <see cref="PauseAfterDays"/> before pausing it is suggested.</summary>
    public const int PauseMinItems = 10;

    /// <summary>Days back the missed-story check looks.</summary>
    public const int MissedDays = 7;

    /// <summary>
    /// Each live source's record over the learning window: items a week, decided and promoted, the last item promoted
    /// (ever), the median time from fetch to decision, and whether pausing it is worth suggesting (it's been on for
    /// <see cref="PauseAfterDays"/> and sent at least <see cref="PauseMinItems"/> items in that time, none of them used).
    /// </summary>
    public async Task<IReadOnlyList<SourceRecord>> SourceHealthAsync(CancellationToken ct = default)
    {
        RequireAdmin();
        await using var db = await dbs.CreateAsync(ct);
        var now = time.GetUtcNow();
        var days = LearningDays;
        var since = now.AddDays(-days);
        var quiet = now.AddDays(-PauseAfterDays);
        var sources = await ByName(db.Sources.AsNoTracking().Where(s => !s.IsRetired)).ToListAsync(ct);
        var items = await db.FeedItems.AsNoTracking().Where(i => i.FetchedAtUtc >= since)
            .Select(i => new { i.SourceId, i.Status, i.FetchedAtUtc, i.StatusAtUtc }).ToListAsync(ct);
        var lastPromoted = (await db.FeedItems.AsNoTracking().Where(i => i.Status == FeedItemStatus.Promoted)
                .Select(i => new { i.SourceId, At = i.StatusAtUtc ?? i.FetchedAtUtc }).ToListAsync(ct))
            .GroupBy(x => x.SourceId).ToDictionary(g => g.Key, g => g.Max(x => x.At));
        var bySource = items.ToLookup(i => i.SourceId);
        return sources.Select(s =>
        {
            var mine = bySource[s.Id].ToList();
            var decided = mine.Where(i => i.Status is FeedItemStatus.Promoted or FeedItemStatus.Dismissed or FeedItemStatus.Duplicate).ToList();
            var waits = decided.Where(i => i.StatusAtUtc >= i.FetchedAtUtc).Select(i => i.StatusAtUtc!.Value - i.FetchedAtUtc).Order().ToList();
            var weeks = Math.Max(1d, Math.Min(days, (now - s.CreatedAtUtc).TotalDays)) / 7d;
            var recent = mine.Where(i => i.FetchedAtUtc >= quiet).ToList();
            var pause = s.Enabled && s.CreatedAtUtc <= quiet && recent.Count >= PauseMinItems
                        && !recent.Any(i => i.Status is FeedItemStatus.Promoted or FeedItemStatus.Shortlisted);
            return new SourceRecord(s, mine.Count / weeks, decided.Count, decided.Count(i => i.Status == FeedItemStatus.Promoted),
                lastPromoted.TryGetValue(s.Id, out var at) ? at : null, waits.Count == 0 ? null : waits[waits.Count / 2], pause);
        }).ToList();
    }

    /// <summary>
    /// Stories that reached triage in the last <see cref="MissedDays"/> days but no brief: dismissed items naming a
    /// KEV-listed CVE no article covers, or a watched adversary no recent article is tagged with.
    /// </summary>
    public async Task<IReadOnlyList<MissedItem>> MissedAsync(CancellationToken ct = default)
    {
        RequireTriage();
        await using var db = await dbs.CreateAsync(ct);
        var since = time.GetUtcNow().AddDays(-MissedDays);
        var dismissed = await db.FeedItems.AsNoTracking().Include(i => i.Source)
            .Where(i => i.Status == FeedItemStatus.Dismissed && i.FetchedAtUtc >= since)
            .OrderByDescending(i => i.FetchedAtUtc).ToListAsync(ct);
        if (dismissed.Count == 0) return [];
        var kev = await KevAsync(db, dismissed, ct);
        var watched = await Adversaries.AdversaryService.WatchedAsync(db, ct);
        var coverage = await CoverageAsync(db, dismissed, ct);
        var watchedIds = watched.Select(w => w.Id).ToList();
        var recently = since.AddDays(-MissedDays);
        var tagged = (await db.ArticleAdversaries.AsNoTracking().Where(l => watchedIds.Contains(l.AdversaryId))
                .Join(db.Articles, l => l.ArticleId, a => a.Id, (l, a) => new { l.AdversaryId, a.CreatedAtUtc })
                .Where(x => x.CreatedAtUtc >= recently).Select(x => x.AdversaryId).ToListAsync(ct))
            .ToHashSet();
        var watching = Stack.StackService.Matcher(watched);
        var missed = new List<MissedItem>();
        foreach (var item in dismissed)
        {
            var covered = coverage.GetValueOrDefault(item.Id)?.Select(c => c.Why).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
            var kevCve = item.CveList.FirstOrDefault(c => kev.Contains(c) && !covered.Contains(c));
            if (kevCve is not null) missed.Add(new(item, item.Source!.Name, $"{kevCve} is KEV-listed and no article covers it"));
            else if (watching(item.Title, item.Excerpt) is { } w && !tagged.Contains(w.Id))
                missed.Add(new(item, item.Source!.Name, $"It names {w.Name} (watched), and no recent article is tagged with it"));
        }
        return missed;
    }

    /// <summary>The EPSS score from which a new item never ages out of New (with KEV-listed ones).</summary>
    public const decimal AgeExemptEpss = 0.10m;

    // Of these new items, the ones that aged out of New: fetched IntakeOptions.AgeDays ago and still untouched, naming no
    // KEV-listed CVE and no CVE with an EPSS score of AgeExemptEpss or more. (A KEV item never hides in a slow week.) The
    // caller leaves out those naming a product on Our stack, which never age either.
    private async Task<HashSet<Guid>> AgedAsync(IAppDbContext db, IReadOnlyList<FeedItem> items, CancellationToken ct)
    {
        var cutoff = AgeCutoff();
        var old = items.Where(i => i.FetchedAtUtc < cutoff).ToList();
        if (old.Count == 0) return [];
        var cves = old.SelectMany(i => i.CveList).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var exempt = cves.Count == 0 ? [] : (await db.CveRecords.AsNoTracking()
            .Where(c => cves.Contains(c.CveId) && (c.IsKev || c.EpssScore >= AgeExemptEpss)).Select(c => c.CveId).ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return old.Where(i => !i.CveList.Any(exempt.Contains)).Select(i => i.Id).ToHashSet();
    }

    // Before this, an untouched new item may have aged into Older (IntakeOptions.AgeDays, at least a day).
    private DateTimeOffset AgeCutoff() => time.GetUtcNow().AddDays(-Math.Max(1, options.CurrentValue.AgeDays));

    /// <summary>How many new items aged out of New into Older (see <see cref="IntakeOptions.AgeDays"/>).</summary>
    public async Task<int> OlderCountAsync(CancellationToken ct = default)
    {
        RequireTriage();
        await using var db = await dbs.CreateAsync(ct);
        return await OlderCount(db, ct);
    }

    // Only items older than the cutoff can have aged, so only those are read.
    private async Task<int> OlderCount(IAppDbContext db, CancellationToken ct)
    {
        var cutoff = AgeCutoff();
        var items = await Light(db.FeedItems.AsNoTracking()
            .Where(i => i.Status == FeedItemStatus.New && !i.Source!.IsRetired && i.FetchedAtUtc < cutoff)).ToListAsync(ct);
        var weRun = Stack.StackService.Matcher(await Stack.StackService.EntriesAsync(db, ct));
        return (await AgedAsync(db, items.Where(i => weRun(i.Title, i.Excerpt) is null).ToList(), ct)).Count;
    }

    /// <summary>How many items wait in each state.</summary>
    public async Task<IReadOnlyDictionary<FeedItemStatus, int>> CountsAsync(CancellationToken ct = default) => (await TallyAsync(ct)).Counts;

    /// <summary>How many items wait in each state, and how many new ones aged into Older: Triage's tabs and its Older
    /// fold, with the aged items worked out once for both.</summary>
    public async Task<(IReadOnlyDictionary<FeedItemStatus, int> Counts, int Older)> TallyAsync(CancellationToken ct = default)
    {
        RequireTriage();
        await using var db = await dbs.CreateAsync(ct);
        var counts = await db.FeedItems.AsNoTracking().Where(i => i.Status != FeedItemStatus.New || !i.Source!.IsRetired)
            .GroupBy(i => i.Status).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        var older = await OlderCount(db, ct);   // New counts what's still in New, not what aged into Older
        return (Enum.GetValues<FeedItemStatus>().ToDictionary(s => s, s => (counts.FirstOrDefault(c => c.Key == s)?.Count ?? 0) - (s == FeedItemStatus.New ? older : 0)), older);
    }

    /// <summary>How many new items wait in the inbox: one count, for the sidebar (no rows loaded).</summary>
    public async Task<int> NewCountAsync(CancellationToken ct = default)
    {
        RequireTriage();
        await using var db = await dbs.CreateAsync(ct);
        return await db.FeedItems.AsNoTracking().CountAsync(i => i.Status == FeedItemStatus.New && !i.Source!.IsRetired, ct) - await OlderCount(db, ct);
    }

    /// <summary>Items as they are now, by id: where the rows that left Triage's list went, and who sent them. An item
    /// pruned since isn't there.</summary>
    public async Task<IReadOnlyList<FeedItem>> ItemsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        RequireTriage();
        if (ids.Count == 0) return [];
        await using var db = await dbs.CreateAsync(ct);
        return await db.FeedItems.AsNoTracking().Where(i => ids.Contains(i.Id)).ToListAsync(ct);
    }

    /// <summary>When the last brief's meeting was held (or recorded), for the inbox's "since the last brief"; null before any.</summary>
    public async Task<DateTimeOffset?> LastBriefAtAsync(CancellationToken ct = default)
    {
        RequireTriage();
        await using var db = await dbs.CreateAsync(ct);
        var held = await db.Briefs.AsNoTracking().Where(b => b.MeetingHeldAtUtc != null || b.PresentedAtUtc != null)
            .Select(b => b.MeetingHeldAtUtc ?? b.PresentedAtUtc).ToListAsync(ct);
        return held.Max();
    }

    // Past coverage: articles about the same CVE, or with the same canonical address, and the brief they were on.
    private static async Task<Dictionary<Guid, IReadOnlyList<Coverage>>> CoverageAsync(IAppDbContext db, IReadOnlyList<FeedItem> items, CancellationToken ct)
    {
        var cves = items.SelectMany(i => i.CveList).Distinct().ToList();
        var byCve = cves.Count == 0 ? [] : await db.ArticleCves.AsNoTracking().Where(c => cves.Contains(c.CveId))
            .Join(db.Articles, c => c.ArticleId, a => a.Id, (c, a) => new { c.CveId, a.Id, a.Title, a.SourceUrl })
            .ToListAsync(ct);
        var since = DateTimeOffset.UtcNow.AddDays(-120);
        var recentLinks = (await db.Articles.AsNoTracking().Where(a => a.SourceUrl != null && a.CreatedAtUtc >= since)
                .Select(a => new { a.Id, a.Title, a.SourceUrl }).ToListAsync(ct))
            .Concat(await db.ArticleLinks.AsNoTracking().Where(l => l.AddedAtUtc >= since)
                .Join(db.Articles, l => l.ArticleId, a => a.Id, (l, a) => new { a.Id, a.Title, SourceUrl = (string?)l.Url }).ToListAsync(ct))
            .Select(a => (a.Id, a.Title, Url: UrlCanonicalizer.Canonicalize(a.SourceUrl))).Where(a => a.Url is not null).Distinct().ToList();
        var articleIds = byCve.Select(x => x.Id).Concat(recentLinks.Select(x => x.Id)).Distinct().ToList();
        var briefs = await ArticleDates.FirstBriefedAsync(db, ct, articleIds);
        var open = (await db.Actions.AsNoTracking()
                .Where(a => a.ArticleId != null && articleIds.Contains(a.ArticleId.Value))
                .WhereOpen()
                .Select(a => new { ArticleId = a.ArticleId!.Value, a.Number }).ToListAsync(ct))
            .GroupBy(a => a.ArticleId).ToDictionary(g => g.Key, g => (IReadOnlyList<int>)g.Select(a => a.Number).Order().ToList());
        var attachable = (await db.Articles.AsNoTracking().Where(a => articleIds.Contains(a.Id)).Where(Unfrozen(db)).Select(a => a.Id).ToListAsync(ct)).ToHashSet();
        Coverage Of(string why, Guid id, string title) => new(why, id, title, briefs.TryGetValue(id, out var d) ? d : null)
        {
            OpenActions = open.GetValueOrDefault(id) ?? [], Attachable = attachable.Contains(id),
        };
        var result = new Dictionary<Guid, IReadOnlyList<Coverage>>();
        foreach (var item in items)
        {
            var list = new List<Coverage>();
            foreach (var a in recentLinks.Where(a => a.Url == item.Url))
                list.Add(Of("Same link", a.Id, a.Title));
            foreach (var cve in item.CveList)
                foreach (var a in byCve.Where(x => x.CveId == cve).DistinctBy(x => x.Id))
                    list.Add(Of(cve, a.Id, a.Title));
            if (list.Count > 0) result[item.Id] = list.DistinctBy(c => (c.Why, c.ArticleId)).Take(5).ToList();
        }
        return result;
    }

    // --- triage ---------------------------------------------------------------------------------------------------

    public Task ShortlistAsync(Guid id, CancellationToken ct = default) => SetAsync(id, FeedItemStatus.Shortlisted, null, null, ct);

    public Task DismissAsync(Guid id, string? reason, CancellationToken ct = default) =>
        SetAsync(id, FeedItemStatus.Dismissed, ArticleService.CleanOrNull(reason, 200) ?? DismissReasons[0], null, ct);

    /// <summary>Marks an item as repeating another (by default the one it was flagged as similar to).</summary>
    public async Task MarkDuplicateAsync(Guid id, Guid? ofId = null, CancellationToken ct = default)
    {
        RequireTriage();
        await using (var db = await dbs.CreateAsync(ct))
        {
            var item = await db.FeedItems.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id, ct) ?? throw new KeyNotFoundException("That item is gone.");
            ofId ??= item.SimilarToId;
            if (ofId == id) throw new ArgumentException("An item can't repeat itself.");
            if (ofId is { } of && !await db.FeedItems.AnyAsync(i => i.Id == of, ct)) throw new KeyNotFoundException("The item it repeats is gone.");
        }
        await SetAsync(id, FeedItemStatus.Duplicate, null, ofId, ct);
    }

    /// <summary>Puts an item back in the inbox as new (undoes a shortlist, dismissal or duplicate; never a promotion).</summary>
    public Task RestoreAsync(Guid id, CancellationToken ct = default) => SetAsync(id, FeedItemStatus.New, null, null, ct);

    /// <summary>Shortlists several items at once (a group's "Shortlist the N"): only those still new, so a colleague's
    /// decision on one since isn't undone. Returns how many changed.</summary>
    public async Task<int> ShortlistManyAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        RequireTriage();
        await using var db = await dbs.CreateAsync(ct);
        return await StampAsync(db.FeedItems.Where(i => ids.Contains(i.Id) && i.Status == FeedItemStatus.New), FeedItemStatus.Shortlisted, null, null, ct);
    }

    /// <summary>Dismisses several items at once (the inbox's "dismiss the rest").</summary>
    public async Task<int> DismissManyAsync(IReadOnlyCollection<Guid> ids, string? reason, CancellationToken ct = default)
    {
        RequireTriage();
        var clean = ArticleService.CleanOrNull(reason, 200) ?? DismissReasons[0];
        await using var db = await dbs.CreateAsync(ct);
        return await StampAsync(db.FeedItems.Where(i => ids.Contains(i.Id) && i.Status == FeedItemStatus.New), FeedItemStatus.Dismissed, clean, null, ct);
    }

    // --- splits (EP-84) ---------------------------------------------------------------------------------------------

    /// <summary>
    /// "Not the same": the reports of a suggested group aren't one event, for everyone from now on. Kept as a row per
    /// report under one batch (each report can still group with others), for <see cref="UndoSplitAsync"/>, and audited.
    /// The batch, or null when every two of them share a split already.
    /// </summary>
    public async Task<Guid?> SplitAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        RequireTriage();
        var distinct = ids.Distinct().ToList();
        if (distinct.Count is < 2 or > TriageGroups.MaxSize) throw new ArgumentException($"Split 2 to {TriageGroups.MaxSize} reports.");
        await using var db = await dbs.CreateAsync(ct);
        if (await db.FeedItems.CountAsync(i => distinct.Contains(i.Id), ct) != distinct.Count) throw new KeyNotFoundException("A report in that group is gone.");
        var had = (await db.TriageSplits.AsNoTracking().Where(s => distinct.Contains(s.ItemId)).Select(s => new { s.Batch, s.ItemId }).ToListAsync(ct))
            .GroupBy(s => s.Batch, s => s.ItemId).Select(g => g.ToHashSet()).ToList();
        if (distinct.SelectMany((a, n) => distinct.Skip(n + 1).Select(b => (a, b))).All(p => had.Any(h => h.Contains(p.a) && h.Contains(p.b)))) return null;
        var batch = Guid.NewGuid();
        var now = time.GetUtcNow();
        var name = Content.Words.Clip(user.DisplayName, TriageSplit.MaxName);
        db.TriageSplits.AddRange(distinct.Select(id => new TriageSplit
        {
            Batch = batch, ItemId = id, SplitAtUtc = now, SplitBy = user.UserId, SplitByName = name,
        }));
        await db.SaveChangesAsync(ct);
        return batch;
    }

    /// <summary>Undoes a split: its reports may group again. Anyone who triages can. How many reports it held (none when
    /// a colleague undid it first).</summary>
    public async Task<int> UndoSplitAsync(Guid batch, CancellationToken ct = default)
    {
        RequireTriage();
        await using var db = await dbs.CreateAsync(ct);
        // Through the tracker, not a bulk delete, so the undo is audited and colleagues' inboxes hear it.
        var members = await db.TriageSplits.Where(s => s.Batch == batch).ToListAsync(ct);
        if (members.Count == 0) return 0;
        db.TriageSplits.RemoveRange(members);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { return 0; }   // a colleague undid it at the same moment
        return members.Count;
    }

    private async Task SetAsync(Guid id, FeedItemStatus status, string? reason, Guid? duplicateOf, CancellationToken ct)
    {
        RequireTriage();
        await using var db = await dbs.CreateAsync(ct);
        if (await StampAsync(db.FeedItems.Where(i => i.Id == id && i.Status != FeedItemStatus.Promoted), status, reason, duplicateOf, ct) > 0) return;
        if (await db.FeedItems.AnyAsync(i => i.Id == id, ct)) throw new InvalidOperationException("That item is already an article.");
        throw new KeyNotFoundException("That item is gone.");
    }

    // A triage decision on the items the query still finds, in one statement whose condition is checked as it writes:
    // read first and saved after, a dismissal landing just after a colleague's promotion (or a "dismiss the rest" racing
    // it) marked the promoted item Dismissed, its article still pointing at it. Feed items aren't audited: who decided,
    // and when, is kept on the item, as Stamp keeps it. Returns how many changed.
    private Task<int> StampAsync(IQueryable<FeedItem> items, FeedItemStatus status, string? reason, Guid? duplicateOf, CancellationToken ct)
    {
        _learned = null;   // a decision changes what's learned
        var dismissReason = status == FeedItemStatus.Dismissed ? reason : null;
        var duplicate = status == FeedItemStatus.Duplicate ? duplicateOf : null;
        var by = status == FeedItemStatus.New ? null : user.DisplayName;
        var at = status == FeedItemStatus.New ? null : (DateTimeOffset?)time.GetUtcNow();
        return items.ExecuteUpdateAsync(u => u.SetProperty(i => i.Status, status).SetProperty(i => i.DismissReason, dismissReason)
            .SetProperty(i => i.DuplicateOfId, duplicate).SetProperty(i => i.StatusByName, by).SetProperty(i => i.StatusAtUtc, at), ct);
    }

    private void Stamp(FeedItem item, FeedItemStatus status, string? reason, Guid? duplicateOf)
    {
        _learned = null;   // a decision changes what's learned
        item.Status = status;
        item.DismissReason = status == FeedItemStatus.Dismissed ? reason : null;
        item.DuplicateOfId = status == FeedItemStatus.Duplicate ? duplicateOf : null;
        item.StatusByName = status == FeedItemStatus.New ? null : user.DisplayName;
        item.StatusAtUtc = status == FeedItemStatus.New ? null : time.GetUtcNow();
    }

    /// <summary>
    /// Makes a draft article from an item: its title, source, link, date and CVEs, and the source's TLP, as an
    /// informational article. Returns the article's id.
    /// </summary>
    public Task<Guid> PromoteAsync(Guid id, CancellationToken ct = default) => PromoteAsync(id, new PromoteForm(), ct);

    /// <summary>
    /// Makes a draft article from an item, with the title, classification, priority and TLP chosen in Triage's promote
    /// form (an empty part takes the item's own); the source, link, date and CVEs come from the item. The form is checked
    /// as an article save checks it, before the item is claimed. An item already promoted returns its article, whatever
    /// the form says. Returns the article's id.
    /// </summary>
    public Task<Guid> PromoteAsync(Guid id, PromoteForm form, CancellationToken ct = default) => PromoteManyAsync([id], form, ct);

    /// <summary>
    /// "Promote as one article": makes one draft from a suggested group's reports (<see cref="TriageGroup.Ids"/>). The
    /// newest report makes the article, as <see cref="PromoteAsync(Guid, PromoteForm, CancellationToken)"/> would, at the
    /// most restrictive of the sources' TLPs unless the form chooses one; each of the others is added to it as another
    /// report, as <see cref="AttachAsync"/> adds one, with its CVEs. Every item is claimed at once or none is: if any has
    /// just been promoted, attached or claimed by someone else, nothing changes and the analyst is told. Returns the
    /// article's id (the one they all made, when that's already happened).
    /// </summary>
    public Task<Guid> PromoteAsOneAsync(IReadOnlyCollection<Guid> ids, PromoteForm form, CancellationToken ct = default)
    {
        if (ids.Distinct().Count() is < 2 or > TriageGroups.MaxSize) throw new ArgumentException($"Promote 2 to {TriageGroups.MaxSize} reports as one article.");
        return PromoteManyAsync(ids, form, ct);
    }

    private async Task<Guid> PromoteManyAsync(IReadOnlyCollection<Guid> ids, PromoteForm form, CancellationToken ct)
    {
        RequireTriage();
        var chosenTitle = form.Title is null ? null : ArticleService.Clean(form.Title, ArticleService.MaxTitle);
        if (chosenTitle is { Length: 0 }) throw new ArgumentException("An article needs a title.");
        if (!Enum.IsDefined(form.Classification)) throw new ArgumentException("That isn't a classification.");
        if (form.Priority is { } p && !Enum.IsDefined(p)) throw new ArgumentException("That isn't a priority.");
        var chosenTlp = form.Tlp is { } t ? Tlp.Checked(t) : (TlpLevel?)null;
        var wanted = ids.Distinct().ToList();
        var single = wanted.Count == 1;
        List<FeedItem> items;
        await using (var db = await dbs.CreateAsync(ct))
        {
            items = await db.FeedItems.AsNoTracking().Include(i => i.Source).Where(i => wanted.Contains(i.Id)).ToListAsync(ct);
            if (items.Count != wanted.Count) throw new KeyNotFoundException(single ? "That item is gone." : "One of those items is gone.");
            if (OneArticle(items) is { } existing) return existing;
            // Claim them first, in one statement: of two analysts promoting the same item at once, one makes the article
            // and the other is sent to it, rather than both making one. A group is claimed whole or not at all (the
            // transaction gives back a part claim), so no report is left in someone else's article and this one too. A
            // claim that never finished (the server stopped part way) can be taken over after a few minutes.
            var now = time.GetUtcNow();
            var stale = now - ClaimTimeout;
            // The transaction runs inside the execution strategy: SQL Server's retries on failure, and a retrying strategy
            // refuses a transaction opened outside it (the whole claim is then retried as one unit).
            var strategy = db.Database.CreateExecutionStrategy();
            var all = await strategy.ExecuteAsync(async () =>
            {
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                var claimed = await db.FeedItems
                    .Where(i => wanted.Contains(i.Id) && (i.Status != FeedItemStatus.Promoted || (i.ArticleId == null && i.StatusAtUtc < stale)))
                    .ExecuteUpdateAsync(u => u.SetProperty(i => i.Status, FeedItemStatus.Promoted).SetProperty(i => i.StatusByName, user.DisplayName)
                        .SetProperty(i => i.StatusAtUtc, now).SetProperty(i => i.DismissReason, (string?)null), ct);
                if (claimed < wanted.Count)
                {
                    await tx.RollbackAsync(ct);
                    return false;
                }
                await tx.CommitAsync(ct);
                return true;
            });
            if (!all)
            {
                var others = await db.FeedItems.AsNoTracking().Where(i => wanted.Contains(i.Id)).ToListAsync(ct);
                if (OneArticle(others) is { } made) return made;
                if (single)
                    throw new InvalidOperationException($"{others[0].StatusByName ?? "Someone"} is promoting this item now. It'll be on the Promoted tab in a moment.");
                throw new InvalidOperationException("Someone has just promoted or attached one of these reports, so none of them was promoted. Look at the group again.");
            }
        }
        var lead = items.OrderByDescending(i => i.PublishedAtUtc ?? i.FetchedAtUtc).ThenBy(i => i.Id).First();
        Guid? articleId = null;
        try
        {
            var title = chosenTitle ?? (lead.Title.Length > ArticleService.MaxTitle ? lead.Title[..(ArticleService.MaxTitle - 1)] + "…" : lead.Title);
            articleId = await articles.CreateAsync(title, lead.Source!.Name, lead.Url, chosenTlp ?? items.Max(i => i.Source!.DefaultTlp), ct);
            await using var db = await dbs.CreateAsync(ct);
            var article = await db.Articles.Include(a => a.Cves).FirstAsync(a => a.Id == articleId, ct);
            article.PublishedAtUtc = lead.PublishedAtUtc;
            // A fresh draft is informational with nothing in "why it matters", so promoting it as a threat starts that
            // at AMBER (or the article's TLP when higher).
            article.Classify(form.Classification);
            article.Priority = form.Classification == Classification.Threat ? form.Priority : null;
            // The excerpt isn't copied into the bottom line (it's the publisher's words, easy to leave in a brief by
            // accident); the editor shows it beside the bottom line to rewrite from.
            var cves = article.Cves.Select(c => c.CveId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var cve in lead.CveList.Where(cves.Add).Take(50))
                db.ArticleCves.Add(new ArticleCve { ArticleId = articleId.Value, CveId = cve });
            // The others, newest first, as further reports of it.
            var known = new HashSet<string?>(StringComparer.Ordinal) { UrlCanonicalizer.Canonicalize(lead.Url) };
            foreach (var item in items.OrderByDescending(i => i.Id == lead.Id).ThenByDescending(i => i.PublishedAtUtc ?? i.FetchedAtUtc))
            {
                var row = await db.FeedItems.Include(i => i.Source).FirstAsync(i => i.Id == item.Id, ct);
                if (item.Id != lead.Id) AddReport(db, row, articleId.Value, known, cves);
                Stamp(row, FeedItemStatus.Promoted, null, null);
                row.ArticleId = articleId;
            }
            await db.SaveChangesAsync(ct);
            return articleId.Value;
        }
        catch
        {
            // Anything after the claim failed: the half-made article goes, and the items are given back as they were, so
            // they can be promoted again rather than stuck "being promoted".
            await using var db = await dbs.CreateAsync(CancellationToken.None);
            if (articleId is { } made && await db.Articles.FirstOrDefaultAsync(a => a.Id == made, CancellationToken.None) is { } orphan)
                db.Articles.Remove(orphan);
            foreach (var item in items)
            {
                var row = await db.FeedItems.FirstAsync(i => i.Id == item.Id, CancellationToken.None);
                row.Status = item.Status;
                row.StatusByName = item.StatusByName;
                row.StatusAtUtc = item.StatusAtUtc;
                row.DismissReason = item.DismissReason;
                row.ArticleId = null;
            }
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }

    // The article every one of these items already made (a promote someone else, or an earlier click, finished).
    private static Guid? OneArticle(List<FeedItem> items) =>
        items.Count > 0 && items.TrueForAll(i => i.Status == FeedItemStatus.Promoted && i.ArticleId is not null && i.ArticleId == items[0].ArticleId)
            ? items[0].ArticleId : null;

    /// <summary>How long a promotion may hold an item before another analyst can take it over (it never finished).</summary>
    internal static readonly TimeSpan ClaimTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Dismisses, as already covered, the other new items the analyst was shown sharing a CVE with <paramref name="id"/>
    /// (<paramref name="shown"/>, the row's <see cref="InboxRow.SameCve"/>): the rest of one story's coverage once one
    /// item has been promoted or attached. Only those, and only while still new and sharing a CVE. Returns how many.
    /// </summary>
    public async Task<int> DismissSameCveAsync(Guid id, IReadOnlyCollection<Guid> shown, CancellationToken ct = default)
    {
        RequireTriage();
        await using var db = await dbs.CreateAsync(ct);
        var item = await db.FeedItems.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id, ct) ?? throw new KeyNotFoundException("That item is gone.");
        var cves = item.CveList.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (cves.Count == 0 || shown.Count == 0) return 0;
        var ids = shown.Where(x => x != id).ToList();
        var others = (await db.FeedItems.AsNoTracking().Where(i => ids.Contains(i.Id) && i.Status == FeedItemStatus.New).ToListAsync(ct))
            .Where(i => i.CveList.Any(cves.Contains)).Select(i => i.Id).ToList();
        if (others.Count == 0) return 0;
        return await StampAsync(db.FeedItems.Where(i => others.Contains(i.Id) && i.Status == FeedItemStatus.New), FeedItemStatus.Dismissed, "Already covered", null, ct);
    }

    /// <summary>
    /// Adds the item's link to an existing article as another report of it (a follow-up, another outlet), instead of a
    /// new article, with the item's CVEs the article doesn't have yet (so later coverage, and "same CVE", know it); the
    /// item is then done, pointing at that article. Adding the same link twice does nothing more. An article already
    /// presented, or on a brief that was presented or sent, is refused unless <paramref name="evenIfPresented"/>: what was
    /// shown stays as it was, and a follow-up to it usually wants its own article.
    /// </summary>
    public async Task AttachAsync(Guid itemId, Guid articleId, bool evenIfPresented = false, CancellationToken ct = default)
    {
        RequireTriage();
        await using var db = await dbs.CreateAsync(ct);
        var item = await db.FeedItems.Include(i => i.Source).FirstOrDefaultAsync(i => i.Id == itemId, ct) ?? throw new KeyNotFoundException("That item is gone.");
        var article = await db.Articles.Include(a => a.Cves).FirstOrDefaultAsync(a => a.Id == articleId, ct) ?? throw new KeyNotFoundException("That article no longer exists.");
        if (item.Status == FeedItemStatus.Promoted && item.ArticleId != articleId)
            throw new InvalidOperationException(item.ArticleId is null ? "That item is being promoted now." : "That item already made another article.");
        if (!evenIfPresented && item.ArticleId != articleId && !await db.Articles.Where(a => a.Id == articleId).Where(Unfrozen(db)).AnyAsync(ct))
            throw new InvalidOperationException("That article has already been presented. Promote this item as a follow-up instead.");
        var known = (await db.ArticleLinks.AsNoTracking().Where(l => l.ArticleId == articleId).Select(l => l.Url).ToListAsync(ct))
            .Append(article.SourceUrl).Select(UrlCanonicalizer.Canonicalize).ToHashSet(StringComparer.Ordinal);
        // The CVEs are part of the editor's form: an editor open on the article now has a stale copy, and is told so,
        // rather than saving over them.
        if (AddReport(db, item, articleId, known, article.Cves.Select(c => c.CveId).ToHashSet(StringComparer.OrdinalIgnoreCase)))
            article.EditVersion = Guid.NewGuid();
        // The item is claimed for the article in one statement that checks it isn't someone else's article now (read
        // above, a colleague's promotion could claim it meanwhile: this save then pointed it here, leaving their article
        // without it), and in the same transaction as the report it adds, so neither is kept without the other.
        var now = time.GetUtcNow();
        var strategy = db.Database.CreateExecutionStrategy();
        var claimed = await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var n = await db.FeedItems.Where(i => i.Id == itemId && (i.Status != FeedItemStatus.Promoted || i.ArticleId == articleId))
                .ExecuteUpdateAsync(u => u.SetProperty(i => i.Status, FeedItemStatus.Promoted).SetProperty(i => i.ArticleId, articleId)
                    .SetProperty(i => i.StatusByName, user.DisplayName).SetProperty(i => i.StatusAtUtc, now)
                    .SetProperty(i => i.DismissReason, (string?)null).SetProperty(i => i.DuplicateOfId, (Guid?)null), ct);
            if (n == 0)
            {
                await tx.RollbackAsync(ct);
                return false;
            }
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return true;
        });
        _learned = null;   // a decision changes what's learned
        if (!claimed) throw new InvalidOperationException("Someone has just promoted or attached that item elsewhere. Look at it again.");
    }

    // Adds an item to an article as another report of it: its link, unless the article already has that address
    // (known, canonical), and the CVEs the article doesn't have yet (cves), up to 50 in all. Both sets take what's
    // added. True when a CVE was added.
    private bool AddReport(IAppDbContext db, FeedItem item, Guid articleId, HashSet<string?> known, HashSet<string> cves)
    {
        if (known.Add(item.Url))
            db.ArticleLinks.Add(new ArticleLink
            {
                ArticleId = articleId, Url = item.Url, Title = item.Title.Length > 500 ? item.Title[..499] + "…" : item.Title,
                SourceName = item.Source?.Name, PublishedAtUtc = item.PublishedAtUtc, AddedAtUtc = time.GetUtcNow(), AddedByName = user.DisplayName,
            });
        var added = item.CveList.Where(c => !cves.Contains(c)).Distinct(StringComparer.OrdinalIgnoreCase).Take(Math.Max(0, 50 - cves.Count)).ToList();
        foreach (var cve in added)
        {
            cves.Add(cve);
            db.ArticleCves.Add(new ArticleCve { ArticleId = articleId, CveId = cve });
        }
        return added.Count > 0;
    }

    /// <summary>
    /// The articles Triage's "Attach to…" offers, newest first: drafts from the last
    /// <see cref="Briefs.BriefService.CandidateDays"/> days, ready articles of any age, and those on the brief for
    /// <paramref name="briefDate"/> (today's); never one already presented or on a brief that was presented or sent.
    /// </summary>
    public async Task<IReadOnlyList<AttachTarget>> AttachTargetsAsync(DateOnly briefDate, CancellationToken ct = default)
    {
        RequireTriage();
        await using var db = await dbs.CreateAsync(ct);
        var since = time.GetUtcNow().AddDays(-Briefs.BriefService.CandidateDays);
        var onBrief = await db.BriefItems.AsNoTracking().Join(db.Briefs.Where(b => b.BriefDate == briefDate), i => i.BriefId, b => b.Id, (i, b) => i.ArticleId)
            .ToListAsync(ct);
        var rows = await db.Articles.AsNoTracking()
            .Where(a => onBrief.Contains(a.Id) || a.Status == ArticleStatus.Ready || (a.Status == ArticleStatus.Draft && a.CreatedAtUtc >= since))
            .Where(Unfrozen(db))
            .Select(a => new AttachTarget(a.Id, a.Title, a.Status, a.AuthorName, a.CreatedAtUtc, onBrief.Contains(a.Id)))
            .ToListAsync(ct);
        return rows.OrderByDescending(r => r.CreatedAtUtc).ToList();
    }

    // Articles still open to another report: not presented, and on no brief that was presented or sent.
    private static Expression<Func<Article, bool>> Unfrozen(IAppDbContext db) => a => a.Status != ArticleStatus.Presented
        && !db.BriefItems.Any(i => i.ArticleId == a.Id && db.Briefs.Any(b => b.Id == i.BriefId && b.Status >= BriefStatus.Presented));

    // --- proposed through the API (EP-75) ----------------------------------------------------------------------

    /// <summary>
    /// An item another system proposes (a SIEM rule, a SOAR playbook): it lands in New under a source named after the
    /// caller, made when first needed and never polled, and an analyst decides it as any other. The same address twice
    /// is one item. Returns the item's id, or null when it was already there. Refused when an administrator has retired
    /// the caller's source: its new items would never show in the inbox.
    /// </summary>
    public async Task<Guid?> ProposeAsync(string caller, string title, string url, string? summary, CancellationToken ct = default)
    {
        RequireTriage();
        title = (title ?? "").Trim();
        if (title.Length is 0 or > 500) throw new ArgumentException("A proposed item needs a title of at most 500 characters.");
        var address = ArticleService.CheckUrl(url, "The item's address") ?? throw new ArgumentException("A proposed item needs its http or https address.");
        summary = string.IsNullOrWhiteSpace(summary) ? null : summary.Trim() is var t && t.Length > FeedItem.MaxExcerpt ? t[..FeedItem.MaxExcerpt] : summary.Trim();
        var sourceId = await ApiSourceAsync(caller, ct);
        var now = time.GetUtcNow();
        var parsed = new ParsedItem(title, address, now, summary, FeedParser.CvesIn(title + " " + summary));
        var hash = UrlCanonicalizer.Hash(UrlCanonicalizer.Canonicalize(address)!);
        await using (var db = await dbs.CreateAsync(ct))
        {
            var added = await StoreAsync(db, sourceId, [parsed], now, RetentionCutoff(now), ct);
            if (added == 0) return null;
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException)
            {
                // The same address proposed twice at once (a playbook's retry): the other call stored it, so it's already there.
                if (await ItemAtAsync(hash, ct) is not null) return null;
                throw;
            }
        }
        return await ItemAtAsync(hash, ct);
    }

    private async Task<Guid?> ItemAtAsync(string urlHash, CancellationToken ct)
    {
        await using var db = await dbs.CreateAsync(ct);
        return await db.FeedItems.AsNoTracking().Where(i => i.UrlHash == urlHash).Select(i => (Guid?)i.Id).FirstOrDefaultAsync(ct);
    }

    // The caller's API source, made the first time it proposes. Its name is matched ignoring case on both providers (a
    // plain == is case-sensitive on SQLite, where "soar" then made a second source beside "SOAR"). Two first proposals at
    // once both find none: the one whose save loses to the unique name reads the other's.
    private async Task<Guid> ApiSourceAsync(string caller, CancellationToken ct)
    {
        var name = (caller.Length > 90 ? caller[..90] : caller) + " (API)";
        var exact = Search.SearchText.Literal(name);
        for (var attempt = 0; ; attempt++)
        {
            await using var db = await dbs.CreateAsync(ct);
            var found = await db.Sources.AsNoTracking().Where(s => EF.Functions.Like(s.Name, exact, Search.SearchText.Escape))
                .Select(s => new { s.Id, s.Kind, s.IsRetired }).FirstOrDefaultAsync(ct);
            if (found is not null)
            {
                if (found.Kind != SourceKind.Api)
                    throw new InvalidOperationException($"There's already a source called {name} that isn't this caller's. An administrator can rename it under Administration > Sources.");
                if (found.IsRetired)
                    throw new InvalidOperationException($"{name} is retired under Administration > Sources, so TIDE isn't taking proposals from it.");
                return found.Id;
            }
            var source = new Source { Name = name, Kind = SourceKind.Api, Url = "https://api.invalid/" + Uri.EscapeDataString(caller), Enabled = false, PollMinutes = 1440 };
            db.Sources.Add(source);
            try
            {
                await db.SaveChangesAsync(ct);
                return source.Id;
            }
            catch (DbUpdateException) when (attempt == 0) { /* made meanwhile by the caller's other proposal: read it */ }
        }
    }

    // --- retention ------------------------------------------------------------------------------------------------

    /// <summary>Deletes items older than <c>Intake:RetentionDays</c> that nobody shortlisted or promoted.</summary>
    public async Task<int> PruneAsync(CancellationToken ct = default)
    {
        var cutoff = RetentionCutoff(time.GetUtcNow());
        await using var db = await dbs.CreateAsync(ct);
        var old = await Prunable(db.FeedItems, cutoff).Select(i => i.Id).ToListAsync(ct);
        if (old.Count == 0) return 0;
        var ids = old.ToHashSet();
        // Items that point at a pruned one keep standing; the link is dropped.
        foreach (var other in await db.FeedItems.Where(i => (i.SimilarToId != null && ids.Contains(i.SimilarToId.Value))
                                                         || (i.DuplicateOfId != null && ids.Contains(i.DuplicateOfId.Value))).ToListAsync(ct))
        {
            if (other.SimilarToId is { } s && ids.Contains(s)) other.SimilarToId = null;
            if (other.DuplicateOfId is { } d && ids.Contains(d)) other.DuplicateOfId = null;
        }
        await db.SaveChangesAsync(ct);
        // Deleted only while still prunable, checked as the statement runs: an item a colleague promoted, attached or
        // shortlisted since it was read above stays (deleted from what was read, it took the promoted item from under its
        // article, or failed the promotion part way and left its draft behind). Its kept text and its place in any split
        // go with it (cascade).
        return await Prunable(db.FeedItems.Where(i => old.Contains(i.Id)), cutoff).ExecuteDeleteAsync(ct);
    }

    private static IQueryable<FeedItem> Prunable(IQueryable<FeedItem> items, DateTimeOffset cutoff) => items
        .Where(i => i.FetchedAtUtc < cutoff && (i.Status == FeedItemStatus.New || i.Status == FeedItemStatus.Dismissed || i.Status == FeedItemStatus.Duplicate));

    private DateTimeOffset RetentionCutoff(DateTimeOffset now) => now.AddDays(-Math.Max(7, options.CurrentValue.RetentionDays));

    private void RequireAdmin()
    {
        if (!user.Has(Permission.Administer)) throw new ForbiddenException("Only an administrator can change the sources.");
    }

    private void RequireTriage()
    {
        if (!user.Has(Permission.WriteArticles)) throw new ForbiddenException("Only analysts triage the inbox.");
    }
}
