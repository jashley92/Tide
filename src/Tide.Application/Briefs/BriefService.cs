using Microsoft.EntityFrameworkCore;
using Tide.Application.Abstractions;
using Tide.Application.Articles;
using Tide.Application.Org;
using Tide.Application.Security;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.Application.Briefs;

/// <summary>A row on the brief list.</summary>
public sealed record BriefSummary(Guid Id, DateOnly BriefDate, string PresentedBy, BriefStatus Status, int Articles,
    int Threats, DateTimeOffset? PresentedAtUtc, DateTimeOffset? DistributedAtUtc = null, DateTimeOffset? SharePointPostedAtUtc = null,
    bool SharePointRequired = false, bool Emailed = false, DateTimeOffset? NotSentAtUtc = null, string? NotSentByName = null,
    string? NotSentReason = null);

/// <summary>What reopening a brief's meeting (<see cref="BriefService.ReopenAsync"/>) does: <see cref="Carried"/>
/// articles the meeting carried come back from the brief for <see cref="CarriedTo"/>. <see cref="Blocked"/> is why it
/// can't be reopened, or null when it can.</summary>
public sealed record Reopening(int Carried, DateOnly? CarriedTo, string? Blocked = null);

/// <summary>An article that could go on a brief: recent, and not on it yet. <see cref="LastBriefed"/> is the latest
/// other brief it's on, if any (on a past day that can be a later one).</summary>
public sealed record BriefCandidate(Guid Id, string Title, Classification Classification, Priority? Priority, TlpLevel Tlp,
    ArticleStatus Status, string AuthorName, DateTimeOffset CreatedAtUtc, DateOnly? LastBriefed, string AuthorId = "")
{
    /// <summary>Whether it's still to be briefed: Draft or Ready, as an article carried over from an earlier brief's "not
    /// discussed" stays. "Add article (N ready, M drafts)" counts these; a presented one is shown only on request.</summary>
    public bool Available => Status is ArticleStatus.Draft or ArticleStatus.Ready;
}

/// <summary>An article on a brief, in its place in the running order: what the article page's stepper walks.</summary>
public sealed record BriefStop(Guid ArticleId, string Title);

/// <summary>Building the daily brief: which articles, in what order, who presents it, and the meeting itself.</summary>
public sealed class BriefService(IAppDbContextFactory dbs, ICurrentUser user, OrgClock clock, Rotation.IPresenterLookup? presenters = null,
    IMeetingsInProgress? meetings = null)
{
    /// <summary>Why Back to ready is refused while the brief's meeting runs in present mode.</summary>
    public const string MeetingLive = "This brief's meeting is running in present mode. End the meeting first.";

    private readonly IMeetingsInProgress _meetings = meetings ?? NoMeetingsInProgress.Instance;

    /// <summary>How far back an article is offered for a brief.</summary>
    public const int CandidateDays = 14;

    /// <summary>The longest the team's opening note can be.</summary>
    public const int TeamNoteMax = 4000;

    /// <summary>The longest <see cref="Brief.PresentedByName"/> kept.</summary>
    public const int PresentedByMax = 512;

    /// <summary>Who presented <paramref name="brief"/>, as its pages and editions say: whoever held the live meeting's
    /// presenter's seat, else (presented without a live meeting, or not yet) the presenter it was given.</summary>
    public static string PresentedBy(Brief brief) => brief.PresentedByName ?? brief.AnalystName;

    /// <summary>Whether <paramref name="brief"/>'s meeting has been held: recorded as held or ended, or the brief presented
    /// or sent. Back to ready (<see cref="ReopenAsync"/>) makes it not held again.</summary>
    public static bool MeetingHeld(Brief brief) =>
        brief.MeetingHeldAtUtc is not null || brief.PresentedAtUtc is not null || brief.Status is BriefStatus.Presented or BriefStatus.Sent;

    // "Pat", "Pat and Sam", "Pat, Sam and Robin".
    private static string Names(IReadOnlyList<string> names) =>
        names.Count == 1 ? names[0] : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];

    /// <summary>The newest <paramref name="take"/> briefs, or every one (the archive: a row a day stays small).</summary>
    public async Task<IReadOnlyList<BriefSummary>> ListAsync(int? take = 60, CancellationToken ct = default)
    {
        RequireRead();
        await using var db = await dbs.CreateAsync(ct);
        var q = db.Briefs.AsNoTracking().OrderByDescending(b => b.BriefDate);
        // The presenter shown is BriefService.PresentedBy, written out so the query can run it.
        return await (take is { } n ? q.Take(n) : q)
            .Select(b => new BriefSummary(b.Id, b.BriefDate, b.PresentedByName ?? b.AnalystName, b.Status, b.Items.Count,
                b.Items.Count(i => i.Article!.Classification == Classification.Threat), b.PresentedAtUtc, b.DistributedAtUtc, b.SharePointPostedAtUtc, b.SharePointRequired,
                db.BriefSends.Any(s => s.BriefId == b.Id && !s.IsTest && s.Status == SendStatus.Sent),
                b.NotSentAtUtc, b.NotSentByName, b.NotSentReason))
            .ToListAsync(ct);
    }

    /// <summary>The brief for <paramref name="date"/> with its articles and everything on them, or null. A Visitor gets it
    /// only from a seat in its live meeting (<see cref="MeetingAccess"/>).</summary>
    public async Task<Brief?> GetAsync(DateOnly date, CancellationToken ct = default)
    {
        var visitor = MeetingAccess.SeatedOn(user, _meetings, date);
        if (!visitor) RequireRead();
        await using var db = await dbs.CreateAsync(ct);
        var brief = await Load(db.Briefs.AsNoTracking()).FirstOrDefaultAsync(b => b.BriefDate == date, ct);
        // The day's brief is the meeting's: one put in its place since isn't the Visitor's to read.
        return visitor && brief is not null && !MeetingAccess.SeatedFor(user, _meetings, brief.Id)
            ? throw new ForbiddenException("You don't have access to briefs.") : brief;
    }

    internal static IQueryable<Brief> Load(IQueryable<Brief> q) => q
        .Include(b => b.Items).ThenInclude(i => i.Article).ThenInclude(a => a!.Bullets)
        .Include(b => b.Items).ThenInclude(i => i.Article).ThenInclude(a => a!.Screenshots).ThenInclude(s => s.File)
        .Include(b => b.Items).ThenInclude(i => i.Article).ThenInclude(a => a!.Tags).ThenInclude(t => t.Tag).ThenInclude(t => t!.Category)
        .Include(b => b.Items).ThenInclude(i => i.Article).ThenInclude(a => a!.Techniques)
        .Include(b => b.Items).ThenInclude(i => i.Article).ThenInclude(a => a!.Indicators)
        .Include(b => b.Items).ThenInclude(i => i.Article).ThenInclude(a => a!.Cves)
        .AsSplitQuery();

    /// <summary>The articles on the brief for <paramref name="date"/>, in its running order (none when there's no brief).</summary>
    public async Task<IReadOnlyList<BriefStop>> RunningOrderAsync(DateOnly date, CancellationToken ct = default)
    {
        RequireRead();
        await using var db = await dbs.CreateAsync(ct);
        return await db.Briefs.AsNoTracking().Where(b => b.BriefDate == date).SelectMany(b => b.Items).OrderBy(i => i.SortOrder)
            .Select(i => new BriefStop(i.ArticleId, i.Article!.Title)).ToListAsync(ct);
    }

    /// <summary>The brief for <paramref name="date"/>, started if there's none: presented by whoever the brief rotation names for that day, or the
    /// current analyst when the rotation names nobody. A past day's brief, written after the fact, is presented by the
    /// analyst starting it: whoever writes it up is the one who held that meeting far more often than the rota says.</summary>
    public async Task<Brief> GetOrStartAsync(DateOnly date, CancellationToken ct = default)
    {
        if (await GetAsync(date, ct) is { } existing) return existing;
        RequireWrite();
        if (clock.Days.DayOffReason(date) is { } why) throw new ArgumentException($"{why}, and briefs are held on working days.");
        await using (var db = await dbs.CreateAsync(ct))
        {
            var presenter = presenters is null || IsPast(date) ? null : await presenters.PresenterAsync(date, ct);
            db.Briefs.Add(new Brief
            {
                BriefDate = date, AnalystId = presenter?.Id ?? user.UserId, AnalystName = presenter?.Name ?? user.DisplayName,
                TeamNote = await PreviousTeamNoteAsync(db, date, ct),
            });
            // Two analysts opening today's brief at once: the date is unique, so the second save fails and reads the first.
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException) { /* read below: someone else's, or the error again if there's none */ }
        }
        return await GetAsync(date, ct) ?? throw new InvalidOperationException("The brief couldn't be started.");
    }

    /// <summary>Recent articles not on this brief, newest first; ready ones before drafts. A draft that has been on a brief
    /// (carried over from one, say) is offered however old it is.</summary>
    public async Task<IReadOnlyList<BriefCandidate>> CandidatesAsync(Guid briefId, CancellationToken ct = default)
    {
        RequireWrite();
        await using var db = await dbs.CreateAsync(ct);
        var since = DateTimeOffset.UtcNow.AddDays(-CandidateDays);
        var rows = await db.Articles.AsNoTracking()
            .Where(a => !db.BriefItems.Any(i => i.BriefId == briefId && i.ArticleId == a.Id))
            .Where(a => a.CreatedAtUtc >= since || a.Status == ArticleStatus.Ready
                        || (a.Status == ArticleStatus.Draft && db.BriefItems.Any(i => i.ArticleId == a.Id)))
            .Select(a => new BriefCandidate(a.Id, a.Title, a.Classification, a.Priority, a.Tlp, a.Status, a.AuthorName,
                a.CreatedAtUtc, null, a.AuthorId))
            .ToListAsync(ct);
        var ids = rows.Select(r => r.Id).ToList();
        var on = await db.BriefItems.AsNoTracking().Where(i => ids.Contains(i.ArticleId))
            .Join(db.Briefs, i => i.BriefId, b => b.Id, (i, b) => new
            {
                i.ArticleId, b.BriefDate,
                NoMeeting = b.Status == BriefStatus.Sent && b.MeetingHeldAtUtc == null && b.PresentedAtUtc == null,
            })
            .ToListAsync(ct);
        var briefed = on.GroupBy(x => x.ArticleId).ToDictionary(g => g.Key, g => g.Max(x => x.BriefDate));
        // An article that went out on a brief sent without a meeting was briefed, though one sent before that finished
        // its articles (DistributionService) is still marked Draft or Ready: it isn't offered as still to be briefed.
        var went = on.Where(x => x.NoMeeting).Select(x => x.ArticleId).ToHashSet();
        rows = rows.Select(r => briefed.TryGetValue(r.Id, out var d) ? r with { LastBriefed = d } : r)
            .Select(r => went.Contains(r.Id) && r.Available ? r with { Status = ArticleStatus.Presented } : r).ToList();
        return rows.OrderBy(r => r.Status == ArticleStatus.Ready ? 0 : 1).ThenByDescending(r => r.CreatedAtUtc).ToList();
    }

    /// <summary>Adds an article to the end of the running order, or at position <paramref name="at"/> (from 0) when
    /// given: where it was, when a removal is undone.</summary>
    public async Task AddArticleAsync(Guid briefId, Guid articleId, int? at = null, CancellationToken ct = default)
    {
        RequireWrite();
        await using var db = await dbs.CreateAsync(ct);
        var brief = await Editable(db, briefId, ct);
        if (brief.Items.Any(i => i.ArticleId == articleId)) return;
        if (!await db.Articles.AnyAsync(a => a.Id == articleId, ct)) throw new KeyNotFoundException("That article no longer exists.");
        var ordered = brief.Items.OrderBy(i => i.SortOrder).ToList();
        var item = new BriefItem { BriefId = brief.Id, ArticleId = articleId };
        ordered.Insert(Math.Clamp(at ?? ordered.Count, 0, ordered.Count), item);
        Number(ordered);
        db.BriefItems.Add(item);
        Touch(brief);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex is not DbUpdateConcurrencyException)
        {
            // Deleted (a draft) between the check above and this save: the article's foreign key refused the item.
            await using var now = await dbs.CreateAsync(ct);
            if (!await now.Articles.AnyAsync(a => a.Id == articleId, ct)) throw new KeyNotFoundException("That article no longer exists.", ex);
            // A colleague added the same article at the same moment: it's on the brief, as asked.
            if (await now.BriefItems.AnyAsync(i => i.BriefId == briefId && i.ArticleId == articleId, ct)) return;
            throw;
        }
    }

    public async Task RemoveItemAsync(Guid briefId, Guid itemId, CancellationToken ct = default)
    {
        RequireWrite();
        await using var db = await dbs.CreateAsync(ct);
        var brief = await Editable(db, briefId, ct);
        if (brief.Items.FirstOrDefault(i => i.Id == itemId) is not { } item) return;
        db.BriefItems.Remove(item);
        brief.Items.Remove(item);
        Number(brief.Items.OrderBy(i => i.SortOrder));
        Touch(brief);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Moves an article up (negative) or down (positive) the running order.</summary>
    public async Task MoveItemAsync(Guid briefId, Guid itemId, int by, CancellationToken ct = default)
    {
        RequireWrite();
        await using var db = await dbs.CreateAsync(ct);
        var brief = await Editable(db, briefId, ct);
        var ordered = brief.Items.OrderBy(i => i.SortOrder).ToList();
        var at = ordered.FindIndex(i => i.Id == itemId);
        // At an end already (or not on the brief): nothing moves, and the brief isn't touched.
        if (at < 0 || Math.Clamp(at + by, 0, ordered.Count - 1) == at) return;
        AdminLists.Move(ordered, at, by, (item, n) => item.SortOrder = n);
        Touch(brief);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Puts the articles in the order given (the ids of this brief's items).</summary>
    public async Task ReorderAsync(Guid briefId, IReadOnlyList<Guid> itemIds, CancellationToken ct = default)
    {
        RequireWrite();
        await using var db = await dbs.CreateAsync(ct);
        var brief = await Editable(db, briefId, ct);
        var rank = itemIds.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
        Number(brief.Items.OrderBy(i => rank.GetValueOrDefault(i.Id, int.MaxValue)).ThenBy(i => i.SortOrder));
        Touch(brief);
        await db.SaveChangesAsync(ct);
    }

    public async Task SetDetailsAsync(Guid briefId, string analystId, string analystName, string? intro, TlpLevel introTlp = TlpLevel.Clear,
        CancellationToken ct = default)
    {
        RequireWrite();
        await using var db = await dbs.CreateAsync(ct);
        var brief = await db.Briefs.FirstOrDefaultAsync(b => b.Id == briefId, ct) ?? throw new KeyNotFoundException("That brief no longer exists.");
        // The presenter and title slide line went out with the send, as the running order did; the page hides the form too.
        if (brief.Status == BriefStatus.Sent) throw new InvalidOperationException("This brief has been sent; its details are fixed.");
        brief.AnalystId = analystId.Trim();
        brief.AnalystName = ArticleService.Clean(analystName, 200);
        brief.Intro = ArticleService.CleanOrNull(intro, 2000, multiline: true);
        brief.IntroTlp = Tlp.Checked(introTlp);
        Touch(brief);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Saves the team's opening note (Markdown; blank clears it), and marks it reviewed for this brief: saved unchanged,
    /// it's kept as still current. Refused once the brief is sent, as its other content is: the note went out in the team
    /// email as it was then. With <paramref name="editVersion"/> (the brief's <see cref="Brief.EditVersion"/> when the note
    /// was opened to edit), it's refused (<see cref="StaleEditException"/>) if the brief has been saved since, so a
    /// colleague's note isn't overwritten unseen; null skips the check.
    /// </summary>
    public async Task SetTeamNoteAsync(Guid briefId, string? note, Guid? editVersion = null, CancellationToken ct = default)
    {
        RequireWrite();
        await using var db = await dbs.CreateAsync(ct);
        var brief = await db.Briefs.FirstOrDefaultAsync(b => b.Id == briefId, ct) ?? throw new KeyNotFoundException("That brief no longer exists.");
        if (brief.Status == BriefStatus.Sent) throw new InvalidOperationException("This brief has been sent; its title slide line is fixed.");
        if (editVersion is { } v && v != brief.EditVersion) throw new StaleEditException(NoteSavedMeanwhile);
        var text = ArticleService.CleanOrNull(note, TeamNoteMax, multiline: true);
        if (text == brief.TeamNote && brief.TeamNoteReviewedAtUtc is not null) return;
        brief.TeamNote = text;
        brief.TeamNoteReviewedAtUtc = clock.Now.ToUniversalTime();   // who saved it is in the brief's audit trail
        Touch(brief);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) when (editVersion is not null) { throw new StaleEditException(NoteSavedMeanwhile); }
    }

    private const string NoteSavedMeanwhile = "Someone else saved this brief since you opened the note. Reload to see theirs, then save yours again.";

    /// <summary>
    /// Whether <paramref name="brief"/>'s opening note is the previous brief's, carried forward and not yet looked at for
    /// this one (neither changed nor kept as still current), while there's still a meeting to read it at.
    /// </summary>
    public static bool TeamNoteCarriedOver(Brief brief) =>
        !string.IsNullOrWhiteSpace(brief.TeamNote) && brief.TeamNoteReviewedAtUtc is null
        && brief.Status is BriefStatus.Draft or BriefStatus.Ready && brief.PresentedAtUtc is null && brief.MeetingHeldAtUtc is null;

    // The team note a brief for date starts with: the latest earlier brief's, so standing items roll forward. A note
    // cleared on that brief stays cleared, rather than an older one coming back. It's flagged until it's reviewed
    // (TeamNoteCarriedOver).
    private static Task<string?> PreviousTeamNoteAsync(IAppDbContext db, DateOnly date, CancellationToken ct) =>
        db.Briefs.AsNoTracking().Where(b => b.BriefDate < date).OrderByDescending(b => b.BriefDate)
            .Select(b => b.TeamNote).FirstOrDefaultAsync(ct);

    /// <summary>Marks the brief Ready for the meeting (refused while an article on it isn't ready).</summary>
    public async Task MarkReadyAsync(Guid briefId, bool ready, CancellationToken ct = default)
    {
        RequireWrite();
        await using var db = await dbs.CreateAsync(ct);
        var brief = await Load(db.Briefs).FirstOrDefaultAsync(b => b.Id == briefId, ct) ?? throw new KeyNotFoundException("That brief no longer exists.");
        if (brief.Status is BriefStatus.Presented or BriefStatus.Sent) return;
        if (ready)
        {
            if (brief.Items.Count == 0) throw new InvalidOperationException("Add an article first.");
            var gaps = brief.Items.Select(i => i.Article!).Where(a => ArticleChecks.For(a).Any(c => c.Blocking)).Select(a => a.Title).ToList();
            if (gaps.Count > 0) throw new InvalidOperationException("Not ready yet: " + string.Join("; ", gaps.Take(3)) + (gaps.Count > 3 ? "…" : ""));
        }
        brief.Status = ready ? BriefStatus.Ready : BriefStatus.Draft;
        Touch(brief);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Ticks an article off (or back on) during the meeting.</summary>
    public async Task SetDiscussedAsync(Guid itemId, bool discussed, CancellationToken ct = default)
    {
        RequirePresent();
        await using var db = await dbs.CreateAsync(ct);
        if (await db.BriefItems.FirstOrDefaultAsync(i => i.Id == itemId, ct) is not { } item) return;
        item.Discussed = discussed;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Records that the meeting happened: the brief and its articles are Presented. Two times are kept: when the meeting
    /// was held (<paramref name="heldAtUtc"/>; by default now, or the usual meeting time on a past day's brief) and when
    /// this was recorded (the clock). The meeting can't be held before its day or in the future.
    /// <para>Articles in <paramref name="carry"/> that weren't discussed go on the next business day's brief instead of
    /// being marked presented; the result is that day, or null when nothing was carried.</para>
    /// </summary>
    public async Task<DateOnly?> MarkPresentedAsync(Guid briefId, DateTimeOffset? heldAtUtc = null, IReadOnlyCollection<Guid>? carry = null,
        CancellationToken ct = default)
    {
        RequirePresent();
        if (carry is { Count: > 0 }) RequireWrite();
        // The meeting's end and the carry are one save: a colleague editing, starting or sending the next brief at the same
        // moment fails it whole, and it's tried again on what's there now, so the meeting never ends with its carry lost.
        for (var attempt = 1; ; attempt++)
        {
            try { return await MarkPresentedOnceAsync(briefId, heldAtUtc, carry, ct); }
            catch (DbUpdateException) when (attempt < 3) { /* read again and redo */ }
        }
    }

    private async Task<DateOnly?> MarkPresentedOnceAsync(Guid briefId, DateTimeOffset? heldAtUtc, IReadOnlyCollection<Guid>? carry, CancellationToken ct)
    {
        await using var db = await dbs.CreateAsync(ct);
        var brief = await db.Briefs.Include(b => b.Items).ThenInclude(i => i.Article).FirstOrDefaultAsync(b => b.Id == briefId, ct)
                    ?? throw new KeyNotFoundException("That brief no longer exists.");
        if (brief.Status == BriefStatus.Sent) return null;
        var carried = brief.Items.Where(i => !i.Discussed && carry?.Contains(i.ArticleId) == true).OrderBy(i => i.SortOrder).Select(i => i.ArticleId).ToList();
        var next = clock.Days.Next(brief.BriefDate);
        var later = carried.Count == 0 ? null : await db.Briefs.Include(b => b.Items).FirstOrDefaultAsync(b => b.BriefDate == next, ct);
        if (later?.Status == BriefStatus.Sent)
            throw new InvalidOperationException($"The brief for {next:ddd MMM d} has been sent, so nothing can be carried to it.");

        brief.Status = BriefStatus.Presented;
        var now = clock.Now.ToUniversalTime();
        var held = heldAtUtc?.ToUniversalTime() ?? (IsPast(brief.BriefDate) ? clock.MeetingStart(brief.BriefDate).ToUniversalTime() : now);
        if (MeetingDay.Before(clock, brief.BriefDate, held)) throw new ArgumentException(MeetingDay.TooEarly);
        if (held > now.AddMinutes(5)) throw new ArgumentException("The meeting can't be in the future.");
        // Ended already (from present mode, as the brief page's End was clicked): an article that end carried to a later
        // brief is that brief's to present, so it keeps its status.
        HashSet<Guid> onLater = brief.PresentedAtUtc is null ? [] : await LaterBriefedAsync(db, brief, ct);
        brief.MeetingHeldAtUtc ??= held;
        brief.PresentedAtUtc ??= now;
        // Ended from its live meeting: whoever presented it there, which may not be the presenter it was given.
        if (_meetings.PresentersOf(briefId) is { Count: > 0 } presentedBy) brief.PresentedByName = ArticleService.Clean(Names(presentedBy), PresentedByMax);
        // An article carried to the next brief wasn't presented; it keeps its status for that meeting. What each article
        // was before this meeting marked it is kept on its item, so reopening the meeting can put it back.
        foreach (var item in brief.Items.Where(i => !carried.Contains(i.ArticleId) && !onLater.Contains(i.ArticleId)))
        {
            if (item.Article!.Status == ArticleStatus.Presented) continue;
            item.StatusBeforeMeeting ??= item.Article.Status;
            item.Article.Status = ArticleStatus.Presented;
        }
        Touch(brief);
        if (carried.Count > 0) await CarryAsync(db, brief.Id, later, next, carried, ct);
        await db.SaveChangesAsync(ct);
        return carried.Count == 0 ? null : next;
    }

    // Puts the carried articles at the end of the next brief's running order, starting that brief if there's none yet, as
    // GetOrStartAsync would; saved with the meeting's end.
    private async Task CarryAsync(IAppDbContext db, Guid from, Brief? later, DateOnly next, IReadOnlyList<Guid> carried, CancellationToken ct)
    {
        if (later is null)
        {
            var presenter = presenters is null || IsPast(next) ? null : await presenters.PresenterAsync(next, ct);
            later = new Brief
            {
                BriefDate = next, AnalystId = presenter?.Id ?? user.UserId, AnalystName = presenter?.Name ?? user.DisplayName,
                TeamNote = await PreviousTeamNoteAsync(db, next, ct),
            };
            db.Briefs.Add(later);
        }
        var ordered = later.Items.OrderBy(i => i.SortOrder).ToList();
        foreach (var id in carried.Where(id => later.Items.All(i => i.ArticleId != id)))
        {
            var item = new BriefItem { BriefId = later.Id, ArticleId = id, CarriedFromBriefId = from };
            ordered.Add(item);
            later.Items.Add(item);
        }
        Number(ordered);
        Touch(later);
    }

    /// <summary>
    /// What reopening the brief's meeting would do (<see cref="ReopenAsync"/>), or why it can't be: the confirmation
    /// says how many articles come back, and from which day.
    /// </summary>
    public async Task<Reopening> ReopeningAsync(Guid briefId, CancellationToken ct = default)
    {
        RequirePresent();
        await using var db = await dbs.CreateAsync(ct);
        var brief = await db.Briefs.AsNoTracking().FirstOrDefaultAsync(b => b.Id == briefId, ct) ?? throw new KeyNotFoundException("That brief no longer exists.");
        return Plan(brief, await CarriedOnAsync(db, briefId, ct), await SendPendingAsync(db, briefId, ct), _meetings.IsLive(briefId));
    }

    /// <summary>
    /// Reopens the brief's meeting: a Presented brief goes back to Ready, so it can be presented again. The meeting's
    /// times are cleared (the audit trail keeps them); each article the meeting marked presented goes back to what it
    /// was (one presented before, on an earlier brief, is left alone); and the articles it carried come off the next
    /// brief and are this one's again. Something a colleague put on that brief by hand stays, as do that brief and its
    /// note. Refused once the brief is sent or finished as not sent, and once the brief the articles were carried to
    /// has been presented or sent. The meeting's attendees, notes and discussed ticks are kept for when it resumes.
    /// </summary>
    public async Task<Reopening> ReopenAsync(Guid briefId, CancellationToken ct = default)
    {
        RequirePresent();
        // One save, as the meeting's end is: a colleague editing the next brief at the same moment fails it whole, and
        // it's tried again on what's there now.
        for (var attempt = 1; ; attempt++)
        {
            try { return await ReopenOnceAsync(briefId, ct); }
            catch (DbUpdateConcurrencyException) when (attempt < 3) { /* read again and redo */ }
        }
    }

    private async Task<Reopening> ReopenOnceAsync(Guid briefId, CancellationToken ct)
    {
        await using var db = await dbs.CreateAsync(ct);
        var brief = await db.Briefs.Include(b => b.Items).ThenInclude(i => i.Article).FirstOrDefaultAsync(b => b.Id == briefId, ct)
                    ?? throw new KeyNotFoundException("That brief no longer exists.");
        var later = await CarriedOnAsync(db, briefId, ct);
        var plan = Plan(brief, later, await SendPendingAsync(db, briefId, ct), _meetings.IsLive(briefId));
        if (plan.Blocked is { } why) throw new InvalidOperationException(why);
        if (plan.Carried > 0) RequireWrite();

        // An article presented on a later brief since (put on that one by hand) stays presented.
        var ids = brief.Items.Select(i => i.ArticleId).ToList();
        var elsewhere = (await db.BriefItems.AsNoTracking()
            .Where(i => ids.Contains(i.ArticleId) && i.BriefId != briefId)
            .Join(db.Briefs, i => i.BriefId, b => b.Id, (i, b) => new { i.ArticleId, b.BriefDate, b.Status })
            .Where(x => x.BriefDate > brief.BriefDate && (x.Status == BriefStatus.Presented || x.Status == BriefStatus.Sent))
            .Select(x => x.ArticleId).ToListAsync(ct)).ToHashSet();
        foreach (var item in brief.Items.Where(i => i.StatusBeforeMeeting is not null))
        {
            if (item.Article is { Status: ArticleStatus.Presented } a && !elsewhere.Contains(a.Id)) a.Status = item.StatusBeforeMeeting!.Value;
            item.StatusBeforeMeeting = null;
        }
        foreach (var next in later)
        {
            foreach (var item in next.Items.Where(i => i.CarriedFromBriefId == briefId).ToList())
            {
                next.Items.Remove(item);
                db.BriefItems.Remove(item);
            }
            Number(next.Items.OrderBy(i => i.SortOrder));
            Touch(next);
        }
        brief.Status = BriefStatus.Ready;
        brief.MeetingHeldAtUtc = null;
        brief.PresentedAtUtc = null;
        brief.PresentedByName = null;
        Touch(brief);
        await db.SaveChangesAsync(ct);
        return plan;
    }

    // The briefs holding articles this brief's meeting carried to them, earliest first, with their items.
    private static Task<List<Brief>> CarriedOnAsync(IAppDbContext db, Guid briefId, CancellationToken ct) =>
        db.Briefs.Include(b => b.Items).Where(b => b.Items.Any(i => i.CarriedFromBriefId == briefId))
            .OrderBy(b => b.BriefDate).ToListAsync(ct);

    // Whether a real send of the brief is with the relay, or went without its outcome being known: when it lands (or is
    // settled as gone) it finishes the brief, so a meeting reopened meanwhile would be finished as if none was held (and
    // one finished as not sent meanwhile, as if it never went). A send freezing after this read moves the brief's edit
    // version, so the reopen's (or the finish's) save fails and reads it again.
    internal static Task<bool> SendPendingAsync(IAppDbContext db, Guid briefId, CancellationToken ct) =>
        db.BriefSends.AnyAsync(s => s.BriefId == briefId && !s.IsTest && (s.Status == SendStatus.Sending || s.Status == SendStatus.Unknown), ct);

    // What reopening would bring back, or why it can't be done.
    private static Reopening Plan(Brief brief, IReadOnlyList<Brief> later, bool sendPending, bool live)
    {
        var carried = later.Sum(b => b.Items.Count(i => i.CarriedFromBriefId == brief.Id));
        var to = later.Select(b => (DateOnly?)b.BriefDate).FirstOrDefault();
        var moved = later.FirstOrDefault(b => b.Status is BriefStatus.Presented or BriefStatus.Sent);
        var blocked = brief.Status switch
        {
            BriefStatus.Sent when brief.NotSentAtUtc is not null => "This brief was finished as not sent, so its meeting can't be reopened.",
            BriefStatus.Sent => "This brief has been sent, so its meeting can't be reopened.",
            not BriefStatus.Presented => "The meeting hasn't been ended, so there's nothing to reopen.",
            _ when live => MeetingLive,
            _ when sendPending =>
                "An edition of this brief is being sent, or whether it went isn't known yet, so the meeting can't be reopened. Check the send log first.",
            _ when moved is not null =>
                $"The brief for {moved.BriefDate.ToString("ddd MMM d", System.Globalization.CultureInfo.InvariantCulture)} has been "
                + $"{(moved.Status == BriefStatus.Sent ? "sent" : "presented")}, so the articles carried to it can't come back and the meeting can't be reopened.",
            _ => null,
        };
        return new Reopening(carried, to, blocked);
    }

    // The brief's articles that are also on a later brief not yet sent.
    internal static async Task<HashSet<Guid>> LaterBriefedAsync(IAppDbContext db, Brief brief, CancellationToken ct)
    {
        var ids = brief.Items.Select(i => i.ArticleId).ToList();
        return (await db.BriefItems.AsNoTracking().Where(i => ids.Contains(i.ArticleId) && i.BriefId != brief.Id)
            .Join(db.Briefs, i => i.BriefId, b => b.Id, (i, b) => new { i.ArticleId, b.BriefDate, b.Status })
            .Where(x => x.BriefDate > brief.BriefDate && x.Status != BriefStatus.Sent)
            .Select(x => x.ArticleId).ToListAsync(ct)).ToHashSet();
    }

    /// <summary>What each article on the brief still needs, by article id; with the company-wide edition's ceiling, the
    /// checks that depend on it too.</summary>
    public static IReadOnlyDictionary<Guid, IReadOnlyList<ArticleCheck>> Readiness(Brief brief, TlpLevel? companyCeiling = null) =>
        brief.Items.Where(i => i.Article is not null).ToDictionary(i => i.ArticleId, i => ArticleChecks.For(i.Article!, companyCeiling));

    /// <summary>The brief date "today" means, in the organization's time zone.</summary>
    public DateOnly CurrentDate => clock.CurrentBriefDate;

    /// <summary>A day before today: its brief is being written after the fact.</summary>
    public bool IsPast(DateOnly date) => date < clock.Today;

    /// <summary>The actions raised from the brief's articles, as the team edition lists them.</summary>
    public async Task<ILookup<Guid, EditionAction>> EditionActionsAsync(Brief brief, CancellationToken ct = default)
    {
        if (!MeetingAccess.SeatedFor(user, _meetings, brief.Id)) RequireRead();
        await using var db = await dbs.CreateAsync(ct);
        return await BriefEditions.ActionsAsync(db, brief, ct);
    }

    private static async Task<Brief> Editable(IAppDbContext db, Guid briefId, CancellationToken ct)
    {
        var brief = await db.Briefs.Include(b => b.Items).FirstOrDefaultAsync(b => b.Id == briefId, ct)
                    ?? throw new KeyNotFoundException("That brief no longer exists.");
        if (brief.Status == BriefStatus.Sent) throw new InvalidOperationException("This brief has been sent; its articles are fixed.");
        return brief;
    }

    // The running order as given, numbered from 0.
    private static void Number(IEnumerable<BriefItem> ordered)
    {
        var i = 0;
        foreach (var item in ordered.ToList()) item.SortOrder = i++;
    }

    private static void Touch(Brief brief) => brief.EditVersion = Guid.NewGuid();

    private void RequireRead()
    {
        if (!user.Has(Permission.ReadTeamEdition)) throw new ForbiddenException("You don't have access to briefs.");
    }

    private void RequireWrite()
    {
        if (!user.Has(Permission.WriteArticles)) throw new ForbiddenException("Only analysts can build briefs.");
    }

    private void RequirePresent()
    {
        if (!user.Has(Permission.PresentBriefs)) throw new ForbiddenException("Only analysts can run the meeting.");
    }
}
