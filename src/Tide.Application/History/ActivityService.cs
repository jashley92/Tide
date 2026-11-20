using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Tide.Application.Abstractions;
using Tide.Application.Actions;
using Tide.Application.Content;
using Tide.Application.Preferences;
using Tide.Application.Security;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.Application.History;

/// <summary>Where a line of "Since you last looked" sits: what's for the person, their work others changed, and the day's news.</summary>
public enum SinceGroup
{
    ForYou = 0,
    YourWork = 1,
    TheDay = 2,
}

/// <summary>One line of "Since you last looked": what changed (<paramref name="Title"/>, linking to <paramref name="Href"/>),
/// who and how (<paramref name="Detail"/>), and when the latest of it was.</summary>
public sealed record SinceLine(SinceGroup Group, string Title, string Detail, string Href, DateTimeOffset AtUtc);

/// <summary>
/// What changed for the person since they last marked it seen. <see cref="From"/> is where the lines start: their mark, or
/// <see cref="ActivityService.Reach"/> back when the mark is older (<see cref="HeldToReach"/>). <see cref="ReadAtUtc"/> is
/// when it was read: "Mark all seen" moves the mark to it, not to the moment of the click, so nothing that came in
/// between is passed over unseen. On the first look there's no mark to count from (<see cref="FirstLook"/>).
/// </summary>
public sealed record SinceLast(DateTimeOffset From, DateTimeOffset ReadAtUtc, bool FirstLook, bool HeldToReach, IReadOnlyList<SinceLine> Lines, int More)
{
    public bool UpToDate => Lines.Count == 0;

    public IEnumerable<IGrouping<SinceGroup, SinceLine>> Groups => Lines.GroupBy(l => l.Group).OrderBy(g => g.Key);
}

/// <summary>The "seen.today" preference's value: a UTC time, written and read one way.</summary>
public static class SeenMark
{
    public static string Format(DateTimeOffset at) => at.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    public static DateTimeOffset? Parse(string? value) =>
        DateTimeOffset.TryParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) ? at.ToUniversalTime() : null;
}

/// <summary>
/// Today's "Since you last looked" (ADR 0030): what changed for the signed-in person since they last marked it seen, read
/// from what TIDE already keeps, each record's own times (raised, changed, handed, commented, decided, sent, fetched).
/// Nothing new is recorded about what people look at; the one thing kept is their own mark, a preference row
/// (<see cref="UserPreferences.SeenToday"/>). Their own changes are never listed. Each part is read only for someone the
/// page it links to would let in: actions (<see cref="ActionService.MayRead"/>), articles and briefs (the team edition),
/// triage (writing articles). "Yours" for actions is the personal digest's rule (<see cref="ActionQueries.WhereTheirs"/>).
/// Bounded: at most <see cref="Reach"/> back and <see cref="MaxLines"/> lines, in a fixed number of queries whatever the
/// data (one per part, each capped).
/// </summary>
public sealed class ActivityService(IAppDbContextFactory dbs, ICurrentUser user, UserPreferenceService prefs, TimeProvider time)
{
    /// <summary>The most lines drawn; the rest are "and N more".</summary>
    public const int MaxLines = 50;

    /// <summary>The furthest back it reads, however old the mark.</summary>
    public static readonly TimeSpan Reach = TimeSpan.FromDays(7);

    /// <summary>The newest new triage items read for the KEV line.</summary>
    internal const int MaxTriageItems = 500;

    /// <summary>
    /// What changed since the person's mark. On their first look there is no mark, so nothing is listed: the mark is set
    /// now, and changes from here on are theirs to see. (Listing a guessed stretch, a day or a week, would call things
    /// "since you last looked" that predate the card, and fill a newcomer's first Today with noise.)
    /// </summary>
    public async Task<SinceLast> SinceAsync(CancellationToken ct = default)
    {
        RequireRead();
        var now = time.GetUtcNow();
        if (SeenMark.Parse(await prefs.GetAsync(UserPreferences.SeenToday, ct)) is not { } mark)
        {
            await prefs.SetAsync(UserPreferences.SeenToday, SeenMark.Format(now), ct);
            return new SinceLast(now, now, FirstLook: true, HeldToReach: false, [], 0);
        }
        var floor = now - Reach;
        var from = mark < floor ? floor : mark;
        if (from >= now) return new SinceLast(from, now, false, false, [], 0);

        var me = user.UserId;
        var lines = new List<SinceLine>();
        await using var db = await dbs.CreateAsync(ct);
        if (ActionService.MayRead(user)) lines.AddRange((await ActionsAsync(db, me, from, ct)).Select(ActionLine));
        lines.AddRange((await ArticlesAsync(db, me, from, ct)).Select(ArticleLine));
        lines.AddRange(SendLines(await SendsAsync(db, me, from, ct)));
        if (user.Has(Permission.WriteArticles) && KevLine(await KevItemsAsync(db, from, ct)) is { } kev) lines.Add(kev);
        var (shown, more) = Compose(lines, MaxLines);
        return new SinceLast(from, now, false, mark < floor, shown, more);
    }

    /// <summary>
    /// Moves the person's mark to <paramref name="upTo"/> (when the card they marked was read), never past now and never
    /// back: a second tab marking an older read leaves a newer mark as it is.
    /// </summary>
    public async Task MarkSeenAsync(DateTimeOffset upTo, CancellationToken ct = default)
    {
        RequireRead();
        var now = time.GetUtcNow();
        if (upTo > now) upTo = now;
        if (SeenMark.Parse(await prefs.GetAsync(UserPreferences.SeenToday, ct)) is { } mark && mark >= upTo) return;
        await prefs.SetAsync(UserPreferences.SeenToday, SeenMark.Format(upTo), ct);
    }

    // --- the parts --------------------------------------------------------------------------------------------------

    /// <summary>An open action of the person's that someone else raised, handed to their team, changed or commented on.</summary>
    internal sealed record ActionSeen(int Number, string Title, string TeamName,
        DateTimeOffset? RaisedAt, string? RaisedBy, DateTimeOffset? HandedAt, string? HandedBy,
        DateTimeOffset? ChangedAt, string? ChangedBy, string? LastEvent, int Comments, DateTimeOffset? CommentAt, string? CommentBy);

    // One query: the person's open actions with something from someone else since, and what. Names come from the sign-in
    // mirror (the audit columns hold ids); TIDE's own changes (the reminder sweep's) read as "TIDE".
    private static async Task<List<ActionSeen>> ActionsAsync(IAppDbContext db, string me, DateTimeOffset from, CancellationToken ct) =>
        await db.Actions.AsNoTracking().WhereOpen().WhereTheirs([me])
            .Where(a => (a.CreatedAtUtc >= from && a.CreatedBy != me)
                        || (a.HandedAtUtc >= from && a.HandedById != me)
                        || (a.ModifiedAtUtc >= from && a.ModifiedBy != me)
                        || a.Comments.Any(c => c.AtUtc >= from && c.AuthorId != me))
            .OrderByDescending(a => a.Number).Take(MaxLines + 1)
            .Select(a => new ActionSeen(a.Number, a.Title, a.Team!.Name,
                a.CreatedAtUtc >= from && a.CreatedBy != me ? a.CreatedAtUtc : null,
                a.CreatedBy == SystemId ? Tide : db.Users.Where(u => u.Sid == a.CreatedBy).Select(u => u.DisplayName).FirstOrDefault(),
                a.HandedAtUtc >= from && a.HandedById != me ? a.HandedAtUtc : null,
                a.HandedByName,
                a.ModifiedAtUtc >= from && a.ModifiedBy != me ? a.ModifiedAtUtc : null,
                a.ModifiedBy == SystemId ? Tide : db.Users.Where(u => u.Sid == a.ModifiedBy).Select(u => u.DisplayName).FirstOrDefault(),
                a.Events.Where(e => e.AtUtc >= from).OrderByDescending(e => e.AtUtc).ThenByDescending(e => e.Id).Select(e => e.Text).FirstOrDefault(),
                a.Comments.Count(c => c.AtUtc >= from && c.AuthorId != me),
                a.Comments.Where(c => c.AtUtc >= from && c.AuthorId != me).OrderByDescending(c => c.AtUtc).Select(c => (DateTimeOffset?)c.AtUtc).FirstOrDefault(),
                a.Comments.Where(c => c.AtUtc >= from && c.AuthorId != me).OrderByDescending(c => c.AtUtc).Select(c => c.AuthorName).FirstOrDefault()))
            .ToListAsync(ct);

    /// <summary>An article the person wrote that someone else changed, decided a review of, or commented on in review.</summary>
    internal sealed record ArticleSeen(Guid Id, string Title, DateTimeOffset? ChangedAt, string? ChangedBy,
        ReviewStatus? Decision, DateTimeOffset? DecidedAt, string? DecidedBy, int ReviewComments, DateTimeOffset? CommentAt);

    // One query, the review parts as subqueries of each article (scalar ones: SQLite has no lateral join).
    private static async Task<List<ArticleSeen>> ArticlesAsync(IAppDbContext db, string me, DateTimeOffset from, CancellationToken ct)
    {
        var decided = db.ArticleReviews.Where(r => r.DecidedAtUtc >= from && r.ReviewerId != me
                                                   && (r.Status == ReviewStatus.Approved || r.Status == ReviewStatus.ChangesAsked));
        var comments = db.ReviewComments.Where(c => c.CreatedAtUtc >= from && c.AuthorId != me);
        return await db.Articles.AsNoTracking().Where(a => a.AuthorId == me)
            .Where(a => (a.ModifiedAtUtc >= from && a.ModifiedBy != me)
                        || decided.Any(r => r.ArticleId == a.Id)
                        || comments.Any(c => db.ArticleReviews.Any(r => r.Id == c.ReviewId && r.ArticleId == a.Id)))
            .OrderByDescending(a => a.ModifiedAtUtc).ThenByDescending(a => a.CreatedAtUtc).ThenBy(a => a.Id).Take(MaxLines + 1)
            .Select(a => new ArticleSeen(a.Id, a.Title,
                a.ModifiedAtUtc >= from && a.ModifiedBy != me ? a.ModifiedAtUtc : null,
                a.ModifiedBy == SystemId ? Tide : db.Users.Where(u => u.Sid == a.ModifiedBy).Select(u => u.DisplayName).FirstOrDefault(),
                decided.Where(r => r.ArticleId == a.Id).OrderByDescending(r => r.DecidedAtUtc).Select(r => (ReviewStatus?)r.Status).FirstOrDefault(),
                decided.Where(r => r.ArticleId == a.Id).OrderByDescending(r => r.DecidedAtUtc).Select(r => r.DecidedAtUtc).FirstOrDefault(),
                decided.Where(r => r.ArticleId == a.Id).OrderByDescending(r => r.DecidedAtUtc).Select(r => r.ReviewerName).FirstOrDefault(),
                comments.Count(c => db.ArticleReviews.Any(r => r.Id == c.ReviewId && r.ArticleId == a.Id)),
                comments.Where(c => db.ArticleReviews.Any(r => r.Id == c.ReviewId && r.ArticleId == a.Id))
                    .OrderByDescending(c => c.CreatedAtUtc).Select(c => (DateTimeOffset?)c.CreatedAtUtc).FirstOrDefault()))
            .ToListAsync(ct);
    }

    /// <summary>An edition of a brief someone else sent.</summary>
    internal sealed record SendSeen(DateOnly BriefDate, Edition Edition, string SentBy, DateTimeOffset SentAt);

    private static async Task<List<SendSeen>> SendsAsync(IAppDbContext db, string me, DateTimeOffset from, CancellationToken ct) =>
        await db.BriefSends.AsNoTracking()
            .Where(s => s.SentAtUtc >= from && !s.IsTest && s.Status == SendStatus.Sent && s.SentById != me)
            .OrderByDescending(s => s.SentAtUtc).ThenBy(s => s.Id).Take(MaxLines)
            .Select(s => new SendSeen(s.BriefDate, s.Edition, s.SentByName, s.SentAtUtc))
            .ToListAsync(ct);

    /// <summary>A new triage item that names a CVE on CISA's KEV list.</summary>
    internal sealed record KevSeen(string Title, DateTimeOffset FetchedAt);

    // The new items fetched since, with CVEs, then which of their CVEs are on KEV (as Today's Triage step counts them):
    // two queries, the second only when there are any.
    private static async Task<List<KevSeen>> KevItemsAsync(IAppDbContext db, DateTimeOffset from, CancellationToken ct)
    {
        var items = (await db.FeedItems.AsNoTracking()
            .Where(i => i.FetchedAtUtc >= from && i.Status == FeedItemStatus.New && !i.Source!.IsRetired && i.Cves != null && i.Cves != "")
            .OrderByDescending(i => i.FetchedAtUtc).ThenBy(i => i.Id).Take(MaxTriageItems)
            .Select(i => new { i.Title, i.Cves, i.FetchedAtUtc }).ToListAsync(ct))
            .Select(i => new FeedItem { Title = i.Title, Cves = i.Cves, FetchedAtUtc = i.FetchedAtUtc }).ToList();
        var cves = items.SelectMany(i => i.CveList).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (cves.Count == 0) return [];
        var kev = (await db.CveRecords.AsNoTracking().Where(c => cves.Contains(c.CveId) && c.IsKev).Select(c => c.CveId).ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. items.Where(i => i.CveList.Any(kev.Contains)).Select(i => new KevSeen(i.Title, i.FetchedAtUtc))];
    }

    // --- the lines (pure) -------------------------------------------------------------------------------------------

    // Who the reminder sweep and other background work are (CurrentUser outside a sign-in), and how they read; someone
    // missing from the sign-in mirror reads as a colleague.
    private const string SystemId = "system", Tide = "TIDE", Someone = "a colleague";

    /// <summary>An action's line: why it's here (handed, raised, changed) and any comments, all for the person.</summary>
    internal static SinceLine ActionLine(ActionSeen a)
    {
        var parts = new List<string>();
        if (a.HandedAt is not null) parts.Add($"handed to {a.TeamName} by {a.HandedBy ?? Someone}");
        else if (a.RaisedAt is not null) parts.Add($"raised by {a.RaisedBy ?? Someone}");
        else if (a.ChangedAt is not null) parts.Add($"changed by {a.ChangedBy ?? Someone}" + (Clip(a.LastEvent, 80) is { Length: > 0 } e ? $": {e}" : ""));
        if (a.Comments > 0)
            parts.Add(a.Comments == 1 ? $"a comment from {a.CommentBy}" : $"{a.Comments} comments, the latest from {a.CommentBy}");
        var at = Latest(a.HandedAt, a.RaisedAt, a.ChangedAt, a.CommentAt);
        return new SinceLine(SinceGroup.ForYou, $"A-{a.Number} {a.Title}", Capital(string.Join(" · ", parts)), $"actions/{a.Number}", at);
    }

    /// <summary>
    /// An article's line: a review decided or commented on is for the person (they have something to do or to know); an
    /// edit alone is their work, changed by someone else.
    /// </summary>
    internal static SinceLine ArticleLine(ArticleSeen a)
    {
        var parts = new List<string>();
        if (a.Decision is { } d && a.DecidedAt is not null)
            parts.Add(d == ReviewStatus.Approved ? $"approved by {a.DecidedBy}" : $"{a.DecidedBy} asked for changes");
        if (a.ReviewComments > 0) parts.Add(a.ReviewComments == 1 ? "a review comment" : $"{a.ReviewComments} review comments");
        var reviewed = parts.Count > 0;
        if (a.ChangedAt is not null) parts.Add($"edited by {a.ChangedBy ?? Someone}");
        var at = Latest(a.DecidedAt, a.CommentAt, a.ChangedAt);
        return new SinceLine(reviewed ? SinceGroup.ForYou : SinceGroup.YourWork, a.Title, Capital(string.Join(" · ", parts)),
            reviewed ? $"articles/{a.Id}/review" : $"articles/{a.Id}", at);
    }

    /// <summary>The briefs sent: one line a brief, naming its editions and who sent them.</summary>
    internal static IEnumerable<SinceLine> SendLines(IEnumerable<SendSeen> sends) =>
        sends.GroupBy(s => s.BriefDate).Select(g =>
        {
            var editions = g.Select(s => s.Edition).Distinct().OrderBy(e => e).Select(e => e == Edition.Team ? "team edition" : "company-wide").ToList();
            var by = g.Select(s => s.SentBy).Distinct().ToList();
            var day = g.Key.ToString("ddd MMM d", CultureInfo.InvariantCulture);
            return new SinceLine(SinceGroup.TheDay, $"Brief for {day} sent",
                Capital($"{string.Join(" and ", editions)}, by {string.Join(" and ", by)}"), $"briefs/{g.Key:yyyy-MM-dd}", g.Max(s => s.SentAt));
        });

    /// <summary>The new triage items on KEV, as one line to Triage's KEV filter; none when there are none.</summary>
    internal static SinceLine? KevLine(IReadOnlyList<KevSeen> items) =>
        items.Count == 0 ? null
            : new SinceLine(SinceGroup.TheDay, $"{Words.Plural(items.Count, "new triage item")} on KEV",
                "The latest: " + Clip(items.MaxBy(i => i.FetchedAt)!.Title, 80), "triage?cve=kev", items.Max(i => i.FetchedAt));

    /// <summary>
    /// The lines in the card's order (for you, your work, the day; the newest first in each), at most
    /// <paramref name="max"/>, and how many more there are.
    /// </summary>
    internal static (IReadOnlyList<SinceLine> Shown, int More) Compose(IEnumerable<SinceLine> lines, int max)
    {
        var all = lines.OrderBy(l => l.Group).ThenByDescending(l => l.AtUtc).ThenBy(l => l.Title, StringComparer.Ordinal).ToList();
        return (all.Take(max).ToList(), Math.Max(0, all.Count - max));
    }

    private static DateTimeOffset Latest(params DateTimeOffset?[] times) => times.Where(t => t is not null).Max() ?? DateTimeOffset.MinValue;

    private static string? Clip(string? text, int max) =>
        text is null ? null : (text = text.ReplaceLineEndings(" ").Trim()).Length <= max ? text : text[..(max - 1)].TrimEnd() + "…";

    private static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private void RequireRead()
    {
        if (!user.Has(Permission.ReadTeamEdition)) throw new ForbiddenException("Only the team sees what changed since they last looked.");
    }
}
