using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tide.Application.Abstractions;
using Tide.Application.Briefs;
using Tide.Application.Org;
using Tide.Application.Security;
using Tide.Application.Settings;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.Application.Distribution;

/// <summary>An article of the send a correction is about: its number as sent, and the article (ADR 0015).</summary>
public sealed record CorrectedSection(int Number, Guid ArticleId);

/// <summary>
/// What the writer asks for: the send, the kind, the articles, and the note with the marking they give it. For a slip,
/// <see cref="NameHeadline"/> says the headline isn't what slipped, so the email may name it; otherwise the article is
/// named by number only.
/// </summary>
public sealed record CorrectionRequest(Guid SendId, bool IsTlpSlip, IReadOnlyList<CorrectedSection> Sections, string? Note,
    TlpLevel NoteTlp = TlpLevel.Clear, bool NameHeadline = false);

/// <summary>An article in the send as it went, for the writer to pick: its number, headline, and the article it is (null in
/// an older send whose headline matches no article on the brief now, which the writer then matches by hand).</summary>
public sealed record CorrectableSection(int Number, string Headline, Guid? ArticleId);

/// <summary>An article on the brief now, for matching an older send's section by hand.</summary>
public sealed record BriefArticleChoice(Guid Id, string Title);

/// <summary>What a correction of a send starts from: the send, its articles as they went, the brief's articles now, and
/// the latest correction of it that went (or may have).</summary>
public sealed record CorrectionStart(BriefSend Send, IReadOnlyList<CorrectableSection> Sections, IReadOnlyList<BriefArticleChoice> BriefArticles,
    BriefCorrection? Last);

/// <summary>
/// What a correction will do, shown in its confirm step and handed back with the send, as <see cref="SendPlan"/> is: the
/// send is refused if its marking, recipients or rendering changed in between. <see cref="Fingerprint"/> is a hash of the
/// rendered correction. <see cref="Recipients"/> are the original send's; <see cref="NewOnList"/> and <see cref="OffList"/>
/// count how the edition's list differs now (no one is added: a new reader gets the next edition). <see cref="LastSentAtUtc"/>
/// is the latest correction of the send that went (or may have, when <see cref="LastOutcomeUnknown"/>).
/// </summary>
public sealed record CorrectionPlan(CorrectionRequest Request, DateOnly BriefDate, Edition Edition, TlpLevel Ceiling, TlpLevel Marking,
    string Subject, IReadOnlyList<string> Recipients, int NewOnList, int OffList, IReadOnlyList<ArticleDiff> Articles, string WebHtml,
    bool EmailEnabled, string Fingerprint, DateTimeOffset? LastSentAtUtc = null, string? LastSentBy = null, bool LastOutcomeUnknown = false);

/// <summary>
/// Corrections after a send (ADR 0015): a short, frozen email about one real send the relay took, to that send's
/// recipients, with the writer's note and a diff of each article as sent against the article now. The "as sent" side is
/// the article's section of the send's frozen web copy, less any line that's above the ceiling now; the "now" side is the
/// article cut by <see cref="BriefEditions.Build"/> at the lower of the send's ceiling and the edition's current one. A TLP
/// slip carries no diff, has fixed wording, and once confirmed (frozen, whatever becomes of its email) withdraws a
/// company-wide send, and its other corrections, from the published briefs; the send can't be corrected after that, only
/// slipped again. Same permission as a send; one
/// correction of a send at a time; refused if anything confirmed changed.
/// </summary>
public sealed class CorrectionService(
    IAppDbContextFactory dbs,
    ICurrentUser user,
    IEmailSender email,
    DistributionSettings settings,
    BriefRenderer renderer,
    OrgClock clock,
    IOptions<AppOptions> app,
    PublishedContentCache published)
{
    /// <summary>The longest note.</summary>
    public const int MaxNote = 4000;

    // --- starting, planning ---------------------------------------------------------------------------------------

    /// <summary>What a correction of send <paramref name="sendId"/> starts from. Refused for a send nobody is known to have.</summary>
    public async Task<CorrectionStart> StartAsync(Guid sendId, CancellationToken ct = default)
    {
        RequireSend();
        await using var db = await dbs.CreateAsync(ct);
        var send = await CorrectableAsync(db, sendId, ct);
        var brief = await BriefService.Load(db.Briefs.AsNoTracking()).FirstOrDefaultAsync(b => b.Id == send.BriefId, ct)
                    ?? throw new KeyNotFoundException("That brief no longer exists.");
        var articles = brief.Items.OrderBy(i => i.SortOrder).Where(i => i.Article is not null)
            .Select(i => new BriefArticleChoice(i.ArticleId, i.Article!.Title)).ToList();
        var sections = SentSections.Parse(send.WebHtml)
            .Select(s => new CorrectableSection(s.Number, s.Headline, s.ArticleId ?? ByHeadline(articles, s.Headline))).ToList();
        var last = await LastAsync(db, sendId, ct);
        send.EmailHtml = null;
        send.WebHtml = null;
        return new CorrectionStart(send, sections, articles, last);
    }

    /// <summary>What sending the correction <paramref name="request"/> asks for would do.</summary>
    public async Task<CorrectionPlan> PlanAsync(CorrectionRequest request, CancellationToken ct = default)
    {
        RequireSend();
        return (await BuildAsync(request, ct)).Plan;
    }

    private sealed record Built(CorrectionPlan Plan, RenderedCorrection Rendered, BriefSend Send);

    private async Task<Built> BuildAsync(CorrectionRequest request, CancellationToken ct)
    {
        await using var db = await dbs.CreateAsync(ct);
        var send = await CorrectableAsync(db, request.SendId, ct);
        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        if (!request.IsTlpSlip && note is null) throw new InvalidOperationException("Say what changed: a correction needs a note.");
        if (note is { Length: > MaxNote }) throw new InvalidOperationException($"The note is too long (at most {MaxNote} characters).");
        if (!request.IsTlpSlip && request.Sections.Count == 0) throw new InvalidOperationException("Pick the article or articles the correction is about.");
        // A send a TLP slip withdrew is the team's only: a correction's "as sent" side would put its words back in front
        // of everyone it went to, and on the published briefs. Another slip of it carries no words, so it may still go.
        if (!request.IsTlpSlip && await DistributionService.Withdrawn(db).AnyAsync(s => s.Id == send.Id, ct))
            throw new InvalidOperationException("A TLP slip withdrew that send, so a correction would repeat what slipped. Put what's right in the next edition instead.");
        Tlp.Checked(request.NoteTlp);

        var brief = await BriefService.Load(db.Briefs.AsNoTracking()).FirstOrDefaultAsync(b => b.Id == send.BriefId, ct)
                    ?? throw new KeyNotFoundException("That brief no longer exists.");
        var (publicMax, teamMax) = await settings.GetAsync(ct);
        var current = send.Edition == Edition.Public ? publicMax : teamMax;
        var ceiling = current < send.Ceiling ? current : send.Ceiling;
        if (note is not null && request.NoteTlp > ceiling)
            throw new InvalidOperationException($"The note is marked {Tlp.Label(request.NoteTlp)}, above what this correction may carry ({Tlp.Label(ceiling)}). Mark it lower, or leave out what needs the higher marking.");

        var sent = SentSections.Parse(send.WebHtml);
        var articles = brief.Items.Where(i => i.Article is not null).Select(i => new BriefArticleChoice(i.ArticleId, i.Article!.Title)).ToList();
        var picked = Picked(request, sent, articles);

        var actions = send.Edition == Edition.Team ? await BriefEditions.ActionsAsync(db, brief, ct) : null;
        var cut = BriefEditions.Build(brief, send.Edition, ceiling, actions);
        // A send that carried more than the ceiling allows now (an administrator lowered it since) is quoted from nowhere:
        // not a line of it in a correction, and not a headline in a slip, which names the article by number instead.
        var lowered = send.Marking > ceiling;
        var diffs = request.IsTlpSlip
            ? picked.Select(p => new ArticleDiff(p.Section.Number, p.ArticleId, request.NameHeadline && !lowered ? p.Section.Headline : null, CorrectedState.Unchanged, [])).ToList()
            : Diffs(picked, brief, cut, actions, lowered);
        // EP-80: a correction's "Now:" lines are the article's words as they are now, so when review is required one that
        // adds any waits for the article's approval as the edition's send would. A slip carries no words (and it's how what
        // slipped is withdrawn, so it's never held); an article unchanged, only cut down, or gone from the edition adds none.
        if (!request.IsTlpSlip)
            await Reviews.ReviewService.RequireApprovedAsync(db, send.BriefDate,
                diffs.Where(d => d.Lines.Any(l => l.Kind == DiffKind.Added)).Select(d => d.ArticleId).ToHashSet(), "this correction goes", ct);

        var shown = cut.Articles.Where(a => !request.IsTlpSlip && picked.Any(p => p.ArticleId == a.Article.Id)).Select(a => a.Marking);
        var marking = shown.Append(send.Marking).Append(note is null ? TlpLevel.Clear : request.NoteTlp).Max();
        if (marking > ceiling) marking = ceiling;   // a lowered ceiling: what's above it was left out of both sides

        var content = new CorrectionContent(clock.TeamName, app.Value.BaseUrl, send.Edition, send.BriefDate, marking, request.IsTlpSlip, note, diffs);
        var rendered = CorrectionRenderer.Render(content);
        var subject = CorrectionRenderer.Subject(send.Subject, send.Marking, marking);
        var recipients = Split(send.Recipients);
        var list = await settings.GetRecipientsAsync(send.Edition, ct);
        var had = recipients.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var now = list.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var last = await LastAsync(db, send.Id, ct);

        var plan = new CorrectionPlan(request, send.BriefDate, send.Edition, ceiling, marking, subject, recipients,
            list.Count(a => !had.Contains(a)), recipients.Count(a => !now.Contains(a)), diffs, rendered.WebHtml, email.Enabled,
            Fingerprint(request, subject, rendered), last?.SentAtUtc, last?.SentByName, last?.Status == SendStatus.Unknown);
        return new Built(plan, rendered, send);
    }

    private sealed record Pick(SentSection Section, Guid ArticleId);

    // The sections asked for, each the article it is: named by the heading, or (an older send) matched by headline or by
    // the writer to an article on the brief now.
    private static List<Pick> Picked(CorrectionRequest request, IReadOnlyList<SentSection> sent, IReadOnlyList<BriefArticleChoice> articles)
    {
        if (request.Sections.Select(s => s.Number).Distinct().Count() != request.Sections.Count)
            throw new InvalidOperationException("An article is picked twice.");
        var picks = new List<Pick>();
        foreach (var asked in request.Sections.OrderBy(s => s.Number))
        {
            var section = sent.FirstOrDefault(s => s.Number == asked.Number)
                          ?? throw new InvalidOperationException($"There's no article {asked.Number} in that send.");
            var known = section.ArticleId ?? ByHeadline(articles, section.Headline);
            if (known is { } id ? id != asked.ArticleId : articles.All(a => a.Id != asked.ArticleId))
                throw new InvalidOperationException($"Article {asked.Number} of that send isn't the article picked. Pick it again.");
            picks.Add(new Pick(section, asked.ArticleId));
        }
        return picks;
    }

    private static Guid? ByHeadline(IReadOnlyList<BriefArticleChoice> articles, string headline) =>
        articles.FirstOrDefault(a => string.Equals(a.Title.Trim(), headline, StringComparison.OrdinalIgnoreCase))?.Id;

    // Each article as sent against the article now. Neither side holds anything above the ceiling: the "now" side is cut
    // by BriefEditions.Build, and a line that went but is above the ceiling now (a part raised since) is left out of the
    // "as sent" side and counted, found by rendering the article uncut and taking away what the cut keeps.
    // When an administrator has <paramref name="lowered"/> the ceiling below what the send carried, nothing of the "as
    // sent" side is quoted, its headline included: what went can't be told line by line from what may go now (a raised
    // part's paragraph or caption carries no mark of its own, and the line may since be reworded, deleted or lowered),
    // so the correction gives each article as it is now, under its headline now.
    private List<ArticleDiff> Diffs(IReadOnlyList<Pick> picks, Brief brief, BriefEdition cut, ILookup<Guid, EditionAction>? actions, bool lowered)
    {
        var ctx = new BriefRenderContext(clock.TeamName, null, null, _ => "");
        var now = SentSections.Parse(renderer.Semantic(cut, ctx)).Where(s => s.ArticleId is not null).ToDictionary(s => s.ArticleId!.Value);
        var whole = SentSections.Parse(renderer.Semantic(BriefEditions.Build(brief, cut.Edition, TlpLevel.Red, actions), ctx))
            .Where(s => s.ArticleId is not null).ToDictionary(s => s.ArticleId!.Value);
        var diffs = new List<ArticleDiff>();
        foreach (var p in picks)
        {
            if (!now.TryGetValue(p.ArticleId, out var nowSection))
            {
                // Off the brief, or above the ceiling now: not even its headline is repeated.
                diffs.Add(new ArticleDiff(p.Section.Number, p.ArticleId, null, CorrectedState.LeftTheEdition, []));
                continue;
            }
            var nowLines = SentSections.TextLines(nowSection.Html);
            var restricted = whole.TryGetValue(p.ArticleId, out var all) ? Without(SentSections.TextLines(all.Html), nowLines) : [];
            var was = SentSections.TextLines(p.Section.Html);
            var kept = lowered ? [] : was.Where(l => !restricted.Contains(Unmarked(l))).ToList();
            var lines = SentSections.Diff(kept, nowLines);
            var state = lines.All(l => l.Kind == DiffKind.Same) ? CorrectedState.Unchanged : CorrectedState.Changed;
            // Nothing is counted as restricted when nothing of what went is quoted: most of it is there again, as it is now.
            diffs.Add(new ArticleDiff(p.Section.Number, p.ArticleId, lowered ? nowSection.Headline : p.Section.Headline, state, lines, lowered ? 0 : was.Count - kept.Count));
        }
        return diffs;
    }

    // The lines of the uncut article that the cut doesn't keep, without their TLP marks (a part's mark can differ from
    // when it went: CLEAR then, AMBER now).
    private static HashSet<string> Without(IReadOnlyList<string> whole, IReadOnlyList<string> kept)
    {
        var left = whole.Select(Unmarked).GroupBy(l => l, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        foreach (var k in kept.Select(Unmarked))
            if (left.TryGetValue(k, out var n)) left[k] = n - 1;
        return left.Where(kv => kv.Value > 0).Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal);
    }

    // "• a bullet (TLP:AMBER)" is "• a bullet".
    private static string Unmarked(string line)
    {
        var at = line.LastIndexOf(" (TLP:", StringComparison.Ordinal);
        return at > 0 && line.EndsWith(')') ? line[..at] : line;
    }

    private static string Fingerprint(CorrectionRequest request, string subject, RenderedCorrection rendered) =>
        SendOutcome.Fingerprint(subject, rendered.EmailHtml, rendered.Text, rendered.WebHtml, request.IsTlpSlip ? "slip" : "fix",
            string.Join(",", request.Sections.OrderBy(s => s.Number).Select(s => $"{s.Number}:{s.ArticleId}")));

    // --- sending ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Sends the correction as confirmed in <paramref name="confirmed"/> to the original send's recipients. It's frozen
    /// first, then the email goes and its outcome is kept. Refused if anything the confirmation showed has changed since,
    /// while another correction of the send is with the relay, and for a slip unless <paramref name="acknowledgedNoRecall"/>
    /// (the confirm step's second tick: it can't be recalled).
    /// </summary>
    public Task<BriefCorrection> SendAsync(CorrectionPlan confirmed, bool acknowledgedNoRecall = false, CancellationToken ct = default) =>
        SendAsync(confirmed, acknowledgedNoRecall, retried: false, ct);

    private async Task<BriefCorrection> SendAsync(CorrectionPlan confirmed, bool acknowledgedNoRecall, bool retried, CancellationToken ct)
    {
        RequireSend();
        if (!email.Enabled) throw new InvalidOperationException(SendOutcome.EmailOff);
        if (confirmed.Request.IsTlpSlip && !acknowledgedNoRecall)
            throw new InvalidOperationException("Tick that the email can't be recalled before sending the slip.");
        var built = await BuildAsync(confirmed.Request, ct);
        var now = built.Plan;
        if (now.Marking != confirmed.Marking || now.Ceiling != confirmed.Ceiling
            || !string.Equals(now.Fingerprint, confirmed.Fingerprint, StringComparison.Ordinal)
            || now.LastSentAtUtc != confirmed.LastSentAtUtc
            || !SendOutcome.SameRecipients(now.Recipients, confirmed.Recipients))
            throw new InvalidOperationException("The correction or its recipients changed since you confirmed. Check it again and confirm.");
        if (now.Recipients.Count == 0) throw new InvalidOperationException("That send went to no one, so there's no one to correct.");

        var send = built.Send;
        var row = new BriefCorrection
        {
            CorrectsSendId = send.Id, BriefId = send.BriefId, BriefDate = send.BriefDate, Edition = send.Edition, IsTlpSlip = confirmed.Request.IsTlpSlip,
            Note = string.IsNullOrWhiteSpace(confirmed.Request.Note) ? null : confirmed.Request.Note.Trim(),
            ArticleIds = string.Join(";", now.Articles.Select(a => a.ArticleId)), Ceiling = now.Ceiling, Marking = now.Marking,
            Subject = now.Subject, Recipients = string.Join("; ", now.Recipients), RecipientCount = now.Recipients.Count,
            SentAtUtc = DateTimeOffset.UtcNow, SentById = user.UserId, SentByName = user.DisplayName,
            EmailHtml = built.Rendered.EmailHtml, WebHtml = built.Rendered.WebHtml,
        };

        // Freeze first. One correction of a send at a time: refused while another is with the relay, and the brief's edit
        // version moves in the same save, so of two writers confirming at once only the first freezes.
        var retry = false;
        await using (var db = await dbs.CreateAsync(ct))
        {
            var brief = await db.Briefs.FirstOrDefaultAsync(b => b.Id == send.BriefId, ct)
                        ?? throw new KeyNotFoundException("That brief no longer exists.");
            var since = DateTimeOffset.UtcNow - SendOutcome.InFlight;
            var sending = await db.BriefCorrections.AsNoTracking()
                .Where(c => c.CorrectsSendId == send.Id && c.Status == SendStatus.Sending)
                .Select(c => new { c.SentAtUtc, c.SentByName })
                .ToListAsync(ct);
            if (sending.FirstOrDefault(c => c.SentAtUtc > since) is { } other)
                throw new InvalidOperationException($"{other.SentByName} is sending a correction of this send now. Wait for it to finish, then check the send log.");
            brief.EditVersion = Guid.NewGuid();
            db.BriefCorrections.Add(row);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateConcurrencyException) when (!retried) { retry = true; }
            catch (DbUpdateConcurrencyException)
            {
                throw new InvalidOperationException("Someone else sent a correction or changed the brief as you confirmed. Check the send log, then confirm again if it still needs to go.");
            }
        }
        // Frozen, a slip has withdrawn the send, whatever becomes of its email: its words stop matching the filter.
        if (!retry && row.IsTlpSlip) published.Remove(send.Id);
        if (retry) return await SendAsync(confirmed, acknowledgedNoRecall, retried: true, ct);

        var (status, error) = await SendOutcome.DeliverAsync(email, new OutgoingEmail(now.Recipients, now.Subject, row.EmailHtml, built.Rendered.Text, []));
        await SendOutcome.RecordAsync(() => RecordOutcomeAsync(row.Id, status, error));
        row.Status = status;
        row.Error = error;
        return row;
    }

    private async Task RecordOutcomeAsync(Guid id, SendStatus status, string? error)
    {
        await using var db = await dbs.CreateAsync(CancellationToken.None);
        var row = await db.BriefCorrections.FirstAsync(c => c.Id == id, CancellationToken.None);
        row.Status = status;
        row.Error = error;
        await db.SaveChangesAsync(CancellationToken.None);
    }

    // --- the log ------------------------------------------------------------------------------------------------

    /// <summary>Every correction of the sends of <paramref name="date"/>, newest first (without their frozen copies).</summary>
    public async Task<IReadOnlyList<BriefCorrection>> CorrectionsAsync(DateOnly date, CancellationToken ct = default)
    {
        RequireRead();
        await using var db = await dbs.CreateAsync(ct);
        var rows = await db.BriefCorrections.AsNoTracking().Where(c => c.BriefDate == date)
            .Select(c => new BriefCorrection
            {
                Id = c.Id, CorrectsSendId = c.CorrectsSendId, BriefId = c.BriefId, BriefDate = c.BriefDate, Edition = c.Edition, IsTlpSlip = c.IsTlpSlip,
                Note = c.Note, ArticleIds = c.ArticleIds, Ceiling = c.Ceiling, Marking = c.Marking, Subject = c.Subject, Recipients = c.Recipients,
                RecipientCount = c.RecipientCount, SentAtUtc = c.SentAtUtc, SentById = c.SentById, SentByName = c.SentByName, Status = c.Status, Error = c.Error,
            })
            .ToListAsync(ct);
        foreach (var c in rows.Where(c => SendOutcome.Seen(c.Status, c.SentAtUtc) != c.Status))
            (c.Status, c.Error) = (SendStatus.Unknown, SendOutcome.StaleCorrectionError);
        return rows.OrderByDescending(c => c.SentAtUtc).ToList();
    }

    /// <summary>
    /// Settles a correction whose outcome isn't known (its relay failed partway, or it was left "sending" past the
    /// in-flight time), once someone has checked with a recipient, as <see cref="DistributionService.SettleAsync"/> settles
    /// a send: <paramref name="went"/>, it's Sent, so a company-wide one shows with the published brief; otherwise it's
    /// Failed. A TLP slip withdrew the sends it covers when it was frozen, and does either way.
    /// </summary>
    public async Task<BriefCorrection> SettleAsync(Guid correctionId, bool went, CancellationToken ct = default)
    {
        RequireSend();
        await using var db = await dbs.CreateAsync(ct);
        var row = await db.BriefCorrections.FirstOrDefaultAsync(c => c.Id == correctionId, ct)
                  ?? throw new KeyNotFoundException("That correction no longer exists.");
        if (SendOutcome.Seen(row.Status, row.SentAtUtc) != SendStatus.Unknown)
            throw new InvalidOperationException(row.Status == SendStatus.Sending
                ? "That correction is still with the relay. Wait for it to finish, then check the send log."
                : "Only a correction whose outcome isn't known can be marked.");
        row.Status = went ? SendStatus.Sent : SendStatus.Failed;
        row.Error = SendOutcome.Settled(user.DisplayName, went);
        var brief = await db.Briefs.FirstOrDefaultAsync(b => b.Id == row.BriefId, ct);
        if (brief is not null) brief.EditVersion = Guid.NewGuid();   // a confirmation made before this is refused
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException)
        {
            throw new InvalidOperationException("The brief changed as you marked it. Check the send log, then mark it again.");
        }
        return row;
    }

    /// <summary>A frozen correction (the team's: either edition, a slip too).</summary>
    public async Task<BriefCorrection?> GetAsync(Guid id, CancellationToken ct = default)
    {
        RequireRead();
        await using var db = await dbs.CreateAsync(ct);
        return await db.BriefCorrections.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
    }

    // A send that can be corrected: a real one the relay took. A test, a failed send and one whose outcome isn't known
    // can't be, since nobody is known to have it.
    private static async Task<BriefSend> CorrectableAsync(IAppDbContext db, Guid sendId, CancellationToken ct)
    {
        var send = await db.BriefSends.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sendId, ct)
                   ?? throw new KeyNotFoundException("That send no longer exists.");
        if (send.IsTest || send.Status != SendStatus.Sent || send.WebHtml is null)
            throw new InvalidOperationException("Only a send the relay took can be corrected: nobody is known to have a test, a failed send or one whose outcome isn't known.");
        return send;
    }

    private static async Task<BriefCorrection?> LastAsync(IAppDbContext db, Guid sendId, CancellationToken ct)
    {
        var went = await db.BriefCorrections.AsNoTracking()
            .Where(c => c.CorrectsSendId == sendId && c.Status != SendStatus.Failed)
            .Select(c => new BriefCorrection { Id = c.Id, SentAtUtc = c.SentAtUtc, SentByName = c.SentByName, Status = c.Status, IsTlpSlip = c.IsTlpSlip })
            .ToListAsync(ct);
        // One left "sending" past the in-flight time may have gone; one still with the relay is refused at send.
        foreach (var c in went) c.Status = SendOutcome.Seen(c.Status, c.SentAtUtc);
        return went.Where(c => c.Status != SendStatus.Sending).MaxBy(c => c.SentAtUtc);   // DateTimeOffset doesn't order on SQLite
    }

    private static List<string> Split(string recipients) =>
        recipients.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private void RequireRead()
    {
        if (!user.Has(Permission.ReadTeamEdition)) throw new ForbiddenException("You don't have access to briefs.");
    }

    private void RequireSend()
    {
        if (!user.Has(Permission.PresentBriefs)) throw new ForbiddenException("Only analysts can send a correction.");
    }
}
