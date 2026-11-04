using Microsoft.EntityFrameworkCore;
using Tide.Application.Abstractions;
using Tide.Application.Articles;
using Tide.Application.Org;
using Tide.Application.Security;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.Application.Briefs;

/// <summary>A row on the brief list.</summary>
public sealed record BriefSummary(Guid Id, DateOnly BriefDate, string AnalystName, BriefStatus Status, int Articles,
    int Threats, DateTimeOffset? PresentedAtUtc, DateTimeOffset? DistributedAtUtc = null, DateTimeOffset? SharePointPostedAtUtc = null,
    bool SharePointRequired = false, bool Emailed = false);

/// <summary>An article that could go on a brief: recent, and not on it yet. <see cref="LastBriefed"/> is the latest
/// other brief it's on, if any (on a past day that can be a later one).</summary>
public sealed record BriefCandidate(Guid Id, string Title, Classification Classification, Priority? Priority, TlpLevel Tlp,
    ArticleStatus Status, string AuthorName, DateTimeOffset CreatedAtUtc, DateOnly? LastBriefed, string AuthorId = "")
{
    /// <summary>Whether it's still to be briefed: Draft or Ready, as an article carried over from an earlier brief's "not
    /// discussed" stays. "Add article (N available)" counts these; a presented one is shown only on request.</summary>
    public bool Available => Status is ArticleStatus.Draft or ArticleStatus.Ready;
}

/// <summary>An article on a brief, in its place in the running order: what the article page's stepper walks.</summary>
public sealed record BriefStop(Guid ArticleId, string Title);

/// <summary>Building the daily brief: which articles, in what order, who presents it, and the meeting itself.</summary>
public sealed class BriefService(IAppDbContextFactory dbs, ICurrentUser user, OrgClock clock, Rotation.IPresenterLookup? presenters = null)
{
    /// <summary>How far back an article is offered for a brief.</summary>
    public const int CandidateDays = 14;

    /// <summary>The newest <paramref name="take"/> briefs, or every one (the archive: a row a day stays small).</summary>
    public async Task<IReadOnlyList<BriefSummary>> ListAsync(int? take = 60, CancellationToken ct = default)
    {
        RequireRead();
        await using var db = await dbs.CreateAsync(ct);
        var q = db.Briefs.AsNoTracking().OrderByDescending(b => b.BriefDate);
        return await (take is { } n ? q.Take(n) : q)
            .Select(b => new BriefSummary(b.Id, b.BriefDate, b.AnalystName, b.Status, b.Items.Count,
                b.Items.Count(i => i.Article!.Classification == Classification.Threat), b.PresentedAtUtc, b.DistributedAtUtc, b.SharePointPostedAtUtc, b.SharePointRequired,
                db.BriefSends.Any(s => s.BriefId == b.Id && !s.IsTest && s.Status == SendStatus.Sent)))
            .ToListAsync(ct);
    }

    /// <summary>The brief for <paramref name="date"/> with its articles and everything on them, or null.</summary>
    public async Task<Brief?> GetAsync(DateOnly date, CancellationToken ct = default)
    {
        RequireRead();
        await using var db = await dbs.CreateAsync(ct);
        return await Load(db.Briefs.AsNoTracking()).FirstOrDefaultAsync(b => b.BriefDate == date, ct);
    }

    internal static IQueryable<Brief> Load(IQueryable<Brief> q) => q
        .Include(b => b.Items).ThenInclude(i => i.Article).ThenInclude(a => a!.Bullets)
        .Include(b => b.Items).ThenInclude(i => i.Article).ThenInclude(a => a!.Screenshots).ThenInclude(s => s.File)
        .Include(b => b.Items).ThenInclude(i => i.Article).ThenInclude(a => a!.Tags).ThenInclude(t => t.Tag)
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
            db.Briefs.Add(new Brief { BriefDate = date, AnalystId = presenter?.Id ?? user.UserId, AnalystName = presenter?.Name ?? user.DisplayName });
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
        // The presenter and opening note went out with the send, as the running order did; the page hides the form too.
        if (brief.Status == BriefStatus.Sent) throw new InvalidOperationException("This brief has been sent; its details are fixed.");
        brief.AnalystId = analystId.Trim();
        brief.AnalystName = ArticleService.Clean(analystName, 200);
        brief.Intro = ArticleService.CleanOrNull(intro, 2000, multiline: true);
        brief.IntroTlp = Tlp.Checked(introTlp);
        Touch(brief);
        await db.SaveChangesAsync(ct);
    }

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
        if (DateOnly.FromDateTime(clock.ToLocal(held).DateTime) < brief.BriefDate) throw new ArgumentException("The meeting can't be held before the brief's day.");
        if (held > now.AddMinutes(5)) throw new ArgumentException("The meeting can't be in the future.");
        // Ended already (from present mode, as the brief page's End was clicked): an article that end carried to a later
        // brief is that brief's to present, so it keeps its status.
        HashSet<Guid> onLater = brief.PresentedAtUtc is null ? [] : await LaterBriefedAsync(db, brief, ct);
        brief.MeetingHeldAtUtc ??= held;
        brief.PresentedAtUtc ??= now;
        // An article carried to the next brief wasn't presented; it keeps its status for that meeting.
        foreach (var a in brief.Items.Where(i => !carried.Contains(i.ArticleId) && !onLater.Contains(i.ArticleId)).Select(i => i.Article!))
            a.Status = ArticleStatus.Presented;
        Touch(brief);
        if (carried.Count > 0) await CarryAsync(db, later, next, carried, ct);
        await db.SaveChangesAsync(ct);
        return carried.Count == 0 ? null : next;
    }

    // Puts the carried articles at the end of the next brief's running order, starting that brief if there's none yet, as
    // GetOrStartAsync would; saved with the meeting's end.
    private async Task CarryAsync(IAppDbContext db, Brief? later, DateOnly next, IReadOnlyList<Guid> carried, CancellationToken ct)
    {
        if (later is null)
        {
            var presenter = presenters is null || IsPast(next) ? null : await presenters.PresenterAsync(next, ct);
            later = new Brief { BriefDate = next, AnalystId = presenter?.Id ?? user.UserId, AnalystName = presenter?.Name ?? user.DisplayName };
            db.Briefs.Add(later);
        }
        var ordered = later.Items.OrderBy(i => i.SortOrder).ToList();
        foreach (var id in carried.Where(id => later.Items.All(i => i.ArticleId != id)))
        {
            var item = new BriefItem { BriefId = later.Id, ArticleId = id };
            ordered.Add(item);
            later.Items.Add(item);
        }
        Number(ordered);
        Touch(later);
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
        RequireRead();
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
