using System.Globalization;
using System.IO.Compression;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tide.Application.Abstractions;
using Tide.Application.Briefs;
using Tide.Application.Files;
using Tide.Application.Metrics;
using Tide.Application.Org;
using Tide.Application.Security;
using Tide.Application.Settings;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.Application.Distribution;

/// <summary>Where the app is reached (config section <c>App</c>), for links in email.</summary>
public sealed class AppOptions
{
    /// <summary>The app's address as readers reach it ("https://tide.example.org"); blank leaves links out of email.</summary>
    public string BaseUrl { get; set; } = "";
}

/// <summary>
/// What a send will do, shown in the confirm step and handed back with the send: the send is refused if the edition's
/// marking, recipients or content changed in between, so what goes is what was confirmed. <see cref="Fingerprint"/> is a
/// hash of the email as it would be rendered, so an edit to any bullet, caption, action or number that goes in it counts.
/// <see cref="LastSentAtUtc"/> and <see cref="LastSentBy"/> are the edition's latest real send, if any: the confirmation
/// says it already went, and a send someone else made after the confirmation refuses this one. <see cref="LastOutcomeUnknown"/>
/// is true when that send's outcome isn't known (it may or may not have gone).
/// </summary>
public sealed record SendPlan(DateOnly BriefDate, Edition Edition, TlpLevel Marking, IReadOnlyList<string> Recipients, int Articles,
    int WithheldArticles, int Images, string Subject, bool EmailEnabled, string Fingerprint,
    DateTimeOffset? LastSentAtUtc = null, string? LastSentBy = null, bool LastOutcomeUnknown = false);

/// <summary>The company-wide edition ready to paste into SharePoint, and the images that go with it.</summary>
public sealed record SharePointCopy(BriefEdition Edition, string Html, IReadOnlyList<RenderImage> Images);

/// <summary>
/// Sending a brief: each edition is cut by <see cref="BriefEditions.Build"/>, rendered by <see cref="BriefRenderer"/>, and
/// emailed through the relay to its administered recipient list, after an explicit confirmation. Every real send freezes
/// what went (HTML and copies of the images) and logs who sent it to whom; the brief becomes Sent. The archive of sent
/// company-wide editions is what Readers see.
/// </summary>
public sealed class DistributionService(
    IAppDbContextFactory dbs,
    ICurrentUser user,
    IFileStore store,
    IEmailSender email,
    DistributionSettings settings,
    MetricsService metrics,
    BriefRenderer renderer,
    OrgClock clock,
    IOptions<AppOptions> app,
    PublishedContentCache published,
    IOptionsMonitor<DistributionOptions> options,
    IMeetingsInProgress? meetings = null)
{
    /// <summary>The longest reason a brief finished as not sent can give.</summary>
    public const int NotSentReasonMax = 500;

    /// <summary>Why a brief can't be finished as not sent while a real send of it is with the relay or its outcome isn't
    /// known: when it lands (or is settled as gone) it sends the brief.</summary>
    public const string NotSentWhileSending =
        "An edition of this brief is being sent, or whether it went isn't known yet, so it can't be finished as not sent. Check the send log first.";

    /// <summary>Whether email is on (<c>Email:Enabled</c>). Off, a brief is marked distributed instead of sent.</summary>
    public bool EmailEnabled => email.Enabled;

    /// <summary>Whether a brief isn't finished until someone confirms its SharePoint copy was posted (an administrator's
    /// setting, off by default).</summary>
    public bool SharePointRequired => options.CurrentValue.RequireSharePoint;

    // --- planning, previews -------------------------------------------------------------------------------------

    /// <summary>What sending <paramref name="edition"/> of the brief for <paramref name="date"/> would do.</summary>
    public async Task<SendPlan> PlanAsync(DateOnly date, Edition edition, CancellationToken ct = default)
    {
        RequireSend();
        var cut = await EditionAsync(date, edition, ct);
        var recipients = await settings.GetRecipientsAsync(edition, ct);
        return await PlanForAsync(cut, recipients, ct);
    }

    /// <summary>
    /// The edition as <see cref="PlanAsync"/> cuts it now, for the send dialog's "as readers get it" preview: read with the
    /// plan, so the preview beside a confirmation is the edition that would go.
    /// </summary>
    public Task<BriefEdition> EditionToSendAsync(DateOnly date, Edition edition, CancellationToken ct = default)
    {
        RequireSend();
        return EditionAsync(date, edition, ct);
    }

    private async Task<SendPlan> PlanForAsync(BriefEdition cut, IReadOnlyList<string> recipients, CancellationToken ct)
    {
        BriefSend? last;
        await using (var db = await dbs.CreateAsync(ct))
        {
            var sent = await db.BriefSends.AsNoTracking()
                .Where(s => s.BriefId == cut.Brief.Id && s.Edition == cut.Edition && !s.IsTest && s.Status != SendStatus.Failed)
                .Select(s => new BriefSend { SentAtUtc = s.SentAtUtc, SentByName = s.SentByName, Status = s.Status })
                .ToListAsync(ct);
            // One left "sending" past the in-flight time may have gone (SendOutcome.Seen); one still with the relay is
            // refused at send by the in-flight rule.
            foreach (var s in sent) s.Status = SendOutcome.Seen(s.Status, s.SentAtUtc);
            last = sent.Where(s => s.Status != SendStatus.Sending).MaxBy(s => s.SentAtUtc);   // DateTimeOffset doesn't order on SQLite, so the latest is picked here
        }
        // A hash of the edition as it would be emailed (the same rendering the preview shows, with cid: images), and the
        // images that go in it: computed the same way at plan and at send, so it differs only when what goes does.
        var rendered = await RenderEmailAsync(cut, await ReadImagesAsync(cut, HeaderBytes, ct), ct);
        var fingerprint = SendOutcome.Fingerprint(rendered.Subject, rendered.EmailHtml, rendered.Text, string.Join(",", rendered.Images.Select(i => i.Shot.FileId)));
        return new SendPlan(cut.Brief.BriefDate, cut.Edition, cut.Marking, recipients, cut.Articles.Count, cut.WithheldArticles,
            rendered.Images.Count, BriefRenderer.Subject(cut, clock.TeamName), email.Enabled,
            fingerprint, last?.SentAtUtc, last?.SentByName, last?.Status == SendStatus.Unknown);
    }

    /// <summary>The email as it would be sent now, its images pointed at <paramref name="imageUrl"/> for a browser.</summary>
    public async Task<string> PreviewEmailAsync(DateOnly date, Edition edition, Func<Guid, string> imageUrl, CancellationToken ct = default)
    {
        RequireRead();
        var cut = await EditionAsync(date, edition, ct);
        var files = await ReadImagesAsync(cut, HeaderBytes, ct);
        var sizes = SizesOf(files);
        return renderer.Email(cut, await ContextAsync(cut, img => imageUrl(img.Shot.FileId), sizes, ct), Sendable(cut, sizes, files));
    }

    /// <summary>
    /// The company-wide edition as clean HTML for SharePoint, with the image files named as in the zip. It puts the edition
    /// in front of the company as a send does, so it's held for review as the send is.
    /// </summary>
    public async Task<SharePointCopy> SharePointAsync(DateOnly date, Func<Guid, string> imageUrl, CancellationToken ct = default)
    {
        RequireRead();
        var cut = await EditionAsync(date, Edition.Public, ct);
        await RequireReviewedAsync(cut, "this edition is copied for SharePoint", ct);
        var ctx = await ContextAsync(cut, img => imageUrl(img.Shot.FileId), null, ct);
        // Only the images the zip holds (a screenshot whose file is gone isn't in it), numbered as it names them.
        var files = await ReadImagesAsync(cut, HeaderBytes, ct);
        var images = Sendable(cut, SizesOf(files), files);
        return new SharePointCopy(cut, renderer.Semantic(cut, ctx, images, img => img.FileName), images);
    }

    /// <summary>A zip of the company-wide edition's images, named as the SharePoint copy names them, and held as it is.</summary>
    public async Task<byte[]> SharePointImagesZipAsync(DateOnly date, CancellationToken ct = default)
    {
        RequireRead();
        var cut = await EditionAsync(date, Edition.Public, ct);
        await RequireReviewedAsync(cut, "this edition is copied for SharePoint", ct);
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var files = await ReadImagesAsync(cut, HeaderBytes, ct);
            foreach (var img in Sendable(cut, SizesOf(files), files))
            {
                await using var src = store.OpenRead(img.Shot.FileId);
                if (src is null) continue;
                var entry = zip.CreateEntry(img.FileName, CompressionLevel.NoCompression);
                await using var dst = await entry.OpenAsync(ct);
                await src.CopyToAsync(dst, ct);
            }
        }
        return ms.ToArray();
    }

    // --- sending ------------------------------------------------------------------------------------------------

    /// <summary>Where <see cref="SendTestAsync"/> sends: the current user's address (see <see cref="OwnAddress"/>), or null.</summary>
    public Task<string?> TestAddressAsync(CancellationToken ct = default) => OwnAddress.ForAsync(user, dbs, ct);

    /// <summary>Emails a test copy of the edition to the current user only. Logged; not archived; the brief isn't marked.</summary>
    public async Task<EmailSendResult> SendTestAsync(DateOnly date, Edition edition, CancellationToken ct = default)
    {
        RequireSend();
        // AD's address this session, or the one saved from it on an earlier visit when AD can't be reached.
        var to = await OwnAddress.ForAsync(user, dbs, ct)
            ?? throw new InvalidOperationException("Your directory account has no email address (the mail attribute), so a test copy can't be sent to you.");
        if (!email.Enabled) throw new InvalidOperationException(SendOutcome.EmailOff);
        var cut = await EditionAsync(date, edition, ct);
        var (rendered, files) = await RenderForEmailAsync(cut, ct);
        var result = await email.SendAsync(new OutgoingEmail([to], "[TEST] " + rendered.Subject, rendered.EmailHtml, rendered.Text,
            Inline(rendered, files)), ct);
        await using var db = await dbs.CreateAsync(ct);
        db.BriefSends.Add(NewSend(cut, rendered.Subject, [to], isTest: true, result));
        await db.SaveChangesAsync(ct);
        return result;
    }

    /// <summary>
    /// Sends the edition to its recipient list, as confirmed in <paramref name="confirmed"/>. The rendering and its images
    /// are frozen first, then the email goes; the brief becomes Sent when the relay takes it. Refused if anything the
    /// confirmation showed has changed since.
    /// <para>A brief goes from its own day, and once its meeting has been ended, so its articles are marked presented and
    /// the undiscussed ones carry over. A team that sends without meeting says so with <paramref name="withoutMeeting"/>.
    /// Once one edition has gone, the other follows on the same terms.</para>
    /// </summary>
    public Task<BriefSend> SendAsync(SendPlan confirmed, bool withoutMeeting = false, CancellationToken ct = default) =>
        SendAsync(confirmed, withoutMeeting, retried: false, ct);

    /// <summary>Why a real send of the brief for <paramref name="day"/> can't go now, or null when it can.</summary>
    public string? SendBlocked(DateOnly day, Brief brief, bool withoutMeeting)
    {
        if (clock.Today < day)
            return $"The brief for {day.ToString("ddd MMM d", CultureInfo.InvariantCulture)} can be sent from that day. Prepare it now and send it after the meeting.";
        var held = brief.MeetingHeldAtUtc is not null || brief.PresentedAtUtc is not null || brief.Status == BriefStatus.Sent;
        if (held) return null;
        // There is a meeting: it's running in present mode. Sent "without" one, the brief would record no meeting time.
        if (withoutMeeting && MeetingLive(brief.Id)) return BriefService.MeetingLive;
        return withoutMeeting ? null : "End the meeting first, or tick \"Sent without a meeting\" if there wasn't one.";
    }

    /// <summary>Whether the brief's meeting is running in present mode now, when it can't be sent without a meeting.</summary>
    public bool MeetingLive(Guid briefId) => (meetings ?? NoMeetingsInProgress.Instance).IsLive(briefId);

    private async Task<BriefSend> SendAsync(SendPlan confirmed, bool withoutMeeting, bool retried, CancellationToken ct)
    {
        RequireSend();
        if (!email.Enabled) throw new InvalidOperationException(SendOutcome.EmailOff);
        var cut = await EditionAsync(confirmed.BriefDate, confirmed.Edition, ct);
        if (SendBlocked(confirmed.BriefDate, cut.Brief, withoutMeeting) is { } blocked) throw new InvalidOperationException(blocked);
        await RequireReviewedAsync(cut, "this edition goes", ct);
        var recipients = await settings.GetRecipientsAsync(confirmed.Edition, ct);
        var now = await PlanForAsync(cut, recipients, ct);
        if (now.Marking != confirmed.Marking || now.Articles != confirmed.Articles || now.Images != confirmed.Images
            || !string.Equals(now.Fingerprint, confirmed.Fingerprint, StringComparison.Ordinal)
            || now.LastSentAtUtc != confirmed.LastSentAtUtc
            || !SendOutcome.SameRecipients(now.Recipients, confirmed.Recipients))
            throw new InvalidOperationException("The edition or its recipients changed since you confirmed. Check it again and confirm.");
        if (recipients.Count == 0)
            throw new InvalidOperationException($"No one is set to receive the {BriefRenderer.EditionName(confirmed.Edition).ToLowerInvariant()}. An administrator sets the list under Access & distribution.");
        if (cut.Articles.Count == 0) throw new InvalidOperationException("There's nothing in this edition to send.");

        var (rendered, files) = await RenderForEmailAsync(cut, ct);

        // Freeze first: copies of the images and the rendering, so the archive holds exactly what went.
        var send = NewSend(cut, rendered.Subject, recipients, isTest: false, null);
        send.EmailHtml = rendered.EmailHtml;
        send.WebHtml = rendered.WebHtml;
        var written = new List<Guid>();
        var retry = false;
        await using (var db = await dbs.CreateAsync(ct))
        {
            // One real send of an edition at a time: refused while another is with the relay, and the brief's edit version
            // moves in the same save, so of two analysts confirming at once only the first freezes; the second is refused.
            var brief = await db.Briefs.FirstOrDefaultAsync(b => b.Id == cut.Brief.Id, ct);
            if (brief is null || brief.EditVersion != cut.Brief.EditVersion)
                throw new InvalidOperationException("The brief changed since you confirmed. Check it again and confirm.");
            var since = DateTimeOffset.UtcNow - SendOutcome.InFlight;
            var sending = await db.BriefSends.AsNoTracking()
                .Where(s => s.BriefId == brief.Id && s.Edition == confirmed.Edition && !s.IsTest && s.Status == SendStatus.Sending)
                .Select(s => new { s.SentAtUtc, s.SentByName })
                .ToListAsync(ct);
            if (sending.FirstOrDefault(s => s.SentAtUtc > since) is { } other)
                throw new InvalidOperationException($"{other.SentByName} is sending this edition now. Wait for it to finish, then check the send log.");
            brief.EditVersion = Guid.NewGuid();
            try
            {
                foreach (var img in rendered.Images)
                {
                    if (!files.TryGetValue(img.Shot.FileId, out var f)) continue;
                    var copy = await ScreenshotService.WriteAsync(store, img.Shot.File?.FileName ?? img.FileName, f.ContentType, f.Bytes, ct);
                    written.Add(copy.Id);
                    db.Files.Add(copy);
                    send.Images.Add(new BriefSendImage
                    {
                        ContentId = img.ContentId, FileId = copy.Id, SourceFileId = img.Shot.FileId, Caption = img.Shot.Caption, SortOrder = img.Number,
                    });
                }
                db.BriefSends.Add(send);
                await db.SaveChangesAsync(ct);
            }
            catch (Exception e)
            {
                // Nothing was frozen, so the image copies are deleted. When the brief moved as this froze (another
                // edition's send, or a send or edit of this one), every check runs again once, so only a real conflict
                // (this edition already going, or its content changed) refuses it.
                foreach (var id in written) store.Delete(id);
                if (e is not DbUpdateConcurrencyException) throw;
                if (retried)
                    throw new InvalidOperationException("Someone else sent or changed the brief as you confirmed. Check the send log, then confirm again if it still needs to go.");
                retry = true;
            }
        }
        if (retry) return await SendAsync(confirmed, withoutMeeting, retried: true, ct);

        // Then send, and keep the outcome whatever happens (SendOutcome).
        var (status, error) = await SendOutcome.DeliverAsync(email, new OutgoingEmail(recipients, rendered.Subject, rendered.EmailHtml,
            rendered.Text, Inline(rendered, files)));
        await SendOutcome.RecordAsync(() => RecordOutcomeAsync(send.Id, cut.Brief.Id, status, error));
        send.Status = status;
        send.Error = error;
        return send;
    }

    /// <summary>
    /// Finishes the brief for <paramref name="date"/> without email, for a server with email off: it went out another way
    /// (SharePoint, a meeting, a chat). It becomes Sent as an emailed one does, on the same terms (from its own day, once
    /// the meeting was ended or <paramref name="withoutMeeting"/>, and reviewed where review is required), so its numbers
    /// lock and its articles are finished. Who and when are kept on the brief. Nothing is frozen, so it isn't published.
    /// </summary>
    public async Task MarkDistributedAsync(DateOnly date, bool withoutMeeting = false, CancellationToken ct = default)
    {
        RequireSend();
        if (email.Enabled) throw new InvalidOperationException("Email is on: send the brief instead.");
        await using var db = await dbs.CreateAsync(ct);
        var brief = await WithArticles(db).FirstOrDefaultAsync(b => b.BriefDate == date, ct)
                    ?? throw new KeyNotFoundException("There's no brief for that day.");
        if (brief.Status == BriefStatus.Sent) throw new InvalidOperationException("This brief is already distributed.");
        if (brief.Items.Count == 0) throw new InvalidOperationException("There's nothing on this brief to distribute.");
        if (SendBlocked(date, brief, withoutMeeting) is { } blocked) throw new InvalidOperationException(blocked);
        await Reviews.ReviewService.RequireApprovedAsync(db, date, brief.Items.Select(i => i.ArticleId).ToHashSet(), "the brief is marked distributed", ct);
        await FinishAsync(db, brief, ct);
        brief.DistributedAtUtc = DateTimeOffset.UtcNow;
        brief.DistributedById = user.UserId;
        brief.DistributedByName = user.DisplayName;
        brief.EditVersion = Guid.NewGuid();
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException)
        {
            throw new InvalidOperationException("Someone else changed the brief just now. Check it again.");
        }
    }

    /// <summary>
    /// Finishes the brief for <paramref name="date"/> without sending it, on a server with email on: a day it won't go
    /// out (a holiday, an outage). <see cref="MarkDistributedAsync"/>'s sibling: the same terms as a send (from its own
    /// day, once the meeting was ended or <paramref name="withoutMeeting"/>, and reviewed where review is required), and
    /// it becomes Sent as a send makes it, so its numbers lock and its articles are finished. Who, when and
    /// <paramref name="reason"/> (required, at most <see cref="NotSentReasonMax"/> characters) are kept on the brief.
    /// Nothing is frozen or published, and no SharePoint copy is waited for. It can still be emailed after: a real send
    /// then makes it an ordinary sent brief. Refused while a real send of it is with the relay or its outcome isn't known
    /// (<see cref="NotSentWhileSending"/>).
    /// </summary>
    public async Task FinishNotSentAsync(DateOnly date, string? reason, bool withoutMeeting = false, CancellationToken ct = default)
    {
        RequireSend();
        if (!email.Enabled) throw new InvalidOperationException("Email is off: mark the brief distributed instead.");
        var why = Articles.ArticleService.CleanOrNull(reason, NotSentReasonMax)
                  ?? throw new ArgumentException("Say why the brief isn't being sent.");
        await using var db = await dbs.CreateAsync(ct);
        var brief = await WithArticles(db).FirstOrDefaultAsync(b => b.BriefDate == date, ct)
                    ?? throw new KeyNotFoundException("There's no brief for that day.");
        if (brief.Status == BriefStatus.Sent) throw new InvalidOperationException("This brief is already finished.");
        if (brief.Items.Count == 0) throw new InvalidOperationException("There's nothing on this brief to finish.");
        // As Back to ready waits: a send freezing after this read moves the edit version, so the save below fails.
        if (await BriefService.SendPendingAsync(db, brief.Id, ct)) throw new InvalidOperationException(NotSentWhileSending);
        if (SendBlocked(date, brief, withoutMeeting) is { } blocked) throw new InvalidOperationException(blocked);
        await Reviews.ReviewService.RequireApprovedAsync(db, date, brief.Items.Select(i => i.ArticleId).ToHashSet(), "the brief is finished as not sent", ct);
        await FinishAsync(db, brief, ct);
        brief.SharePointRequired = false;   // nothing went, so there's no copy to post
        brief.NotSentAtUtc = DateTimeOffset.UtcNow;
        brief.NotSentById = user.UserId;
        brief.NotSentByName = user.DisplayName;
        brief.NotSentReason = why;
        brief.EditVersion = Guid.NewGuid();
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException)
        {
            // A send that started in between says so, as one already going does.
            await using var now = await dbs.CreateAsync(ct);
            if (await BriefService.SendPendingAsync(now, brief.Id, ct)) throw new InvalidOperationException(NotSentWhileSending);
            throw new InvalidOperationException("Someone else changed the brief just now. Check it again.");
        }
    }

    /// <summary>
    /// Records that the company-wide copy of the brief for <paramref name="date"/> was posted to SharePoint, by whom and
    /// when (<paramref name="posted"/>), or takes that back. Where an administrator requires it, the brief isn't finished
    /// until it's recorded. It isn't content, so it doesn't move the brief's edit version.
    /// </summary>
    public async Task MarkSharePointPostedAsync(DateOnly date, bool posted = true, CancellationToken ct = default)
    {
        RequireSend();
        await using var db = await dbs.CreateAsync(ct);
        var brief = await db.Briefs.FirstOrDefaultAsync(b => b.BriefDate == date, ct)
                    ?? throw new KeyNotFoundException("There's no brief for that day.");
        if (posted == (brief.SharePointPostedAtUtc is not null)) return;
        if (posted && !await db.BriefItems.AnyAsync(i => i.BriefId == brief.Id, ct))
            throw new InvalidOperationException("There's nothing on this brief to post.");
        brief.SharePointPostedAtUtc = posted ? DateTimeOffset.UtcNow : null;
        brief.SharePointPostedById = posted ? user.UserId : null;
        brief.SharePointPostedByName = posted ? user.DisplayName : null;
        await db.SaveChangesAsync(ct);
    }

    // EP-80: when an administrator requires review, an article in the edition without a current approval holds it.
    private async Task RequireReviewedAsync(BriefEdition cut, string before, CancellationToken ct)
    {
        await using var db = await dbs.CreateAsync(ct);
        await Reviews.ReviewService.RequireApprovedAsync(db, cut.Brief.BriefDate, cut.Articles.Select(a => a.Article.Id).ToHashSet(), before, ct);
    }

    private async Task RecordOutcomeAsync(Guid sendId, Guid briefId, SendStatus status, string? error)
    {
        await using var db = await dbs.CreateAsync(CancellationToken.None);
        var row = await db.BriefSends.FirstAsync(s => s.Id == sendId, CancellationToken.None);
        row.Status = status;
        row.Error = error;
        row.OutcomeAtUtc = DateTimeOffset.UtcNow;
        if (status == SendStatus.Sent)
        {
            var brief = await WithArticles(db).FirstAsync(b => b.Id == briefId, CancellationToken.None);
            await FinishAsync(db, brief, CancellationToken.None);
            brief.EditVersion = Guid.NewGuid();
        }
        await db.SaveChangesAsync(CancellationToken.None);
    }

    // A brief with its articles, as FinishAsync needs it.
    private static IQueryable<Brief> WithArticles(IAppDbContext db) => db.Briefs.Include(b => b.Items).ThenInclude(i => i.Article);

    // A real send went: the brief is Sent. Sent without a meeting (none was ended), its articles are finished as ending
    // it would have (nothing is carried over, and no meeting is recorded), or they'd be offered on every later brief.
    // After a meeting, so is an article added since it ended: it went out with the brief, but no meeting marked it. One
    // the meeting carried to a later brief not yet sent is that brief's to present, and keeps its status.
    // A brief finished as not sent and then emailed after all waits for its SharePoint copy as a sent one does.
    private async Task FinishAsync(IAppDbContext db, Brief brief, CancellationToken ct)
    {
        if (brief.Status != BriefStatus.Sent || brief.NotSentAtUtc is not null) brief.SharePointRequired = options.CurrentValue.RequireSharePoint;
        var open = brief.Items.Select(i => i.Article).OfType<Article>().Where(a => a.Status is ArticleStatus.Draft or ArticleStatus.Ready).ToList();
        if (open.Count > 0 && (brief.MeetingHeldAtUtc is not null || brief.PresentedAtUtc is not null))
        {
            var later = await BriefService.LaterBriefedAsync(db, brief, ct);
            open.RemoveAll(a => later.Contains(a.Id));
        }
        foreach (var a in open) a.Status = ArticleStatus.Presented;
        brief.Status = BriefStatus.Sent;
    }

    private BriefSend NewSend(BriefEdition cut, string subject, IReadOnlyList<string> to, bool isTest, EmailSendResult? result) => new()
    {
        BriefId = cut.Brief.Id, BriefDate = cut.Brief.BriefDate, Edition = cut.Edition, IsTest = isTest, Ceiling = cut.Ceiling,
        Marking = cut.Marking, Subject = subject, Recipients = string.Join("; ", to), RecipientCount = to.Count,
        SentAtUtc = DateTimeOffset.UtcNow, SentById = user.UserId, SentByName = user.DisplayName,
        Status = result is null ? SendStatus.Sending : result.Status == EmailSendStatus.Sent ? SendStatus.Sent : SendStatus.Failed,
        Error = result is null ? null : SendOutcome.Error(result),
    };

    private static List<InlineImage> Inline(RenderedBrief rendered, IReadOnlyDictionary<Guid, (byte[] Bytes, string ContentType)> files) =>
        rendered.Images.Where(i => files.ContainsKey(i.Shot.FileId))
            .Select(i => new InlineImage(i.ContentId, i.FileName, files[i.Shot.FileId].ContentType, files[i.Shot.FileId].Bytes)).ToList();

    // The rendering with cid: images, and the image bytes (only real raster images; a missing file is left out).
    private async Task<(RenderedBrief, IReadOnlyDictionary<Guid, (byte[] Bytes, string ContentType)>)> RenderForEmailAsync(BriefEdition cut, CancellationToken ct)
    {
        var files = await ReadImagesAsync(cut, int.MaxValue, ct);
        return (await RenderEmailAsync(cut, files, ct), files);
    }

    // The edition rendered for email with cid: images, of only the screenshots in files: an image whose file is gone (or
    // isn't a raster image) isn't referenced from the email (a broken image in Outlook says "can't be shown").
    private async Task<RenderedBrief> RenderEmailAsync(BriefEdition cut, IReadOnlyDictionary<Guid, (byte[] Bytes, string ContentType)> files, CancellationToken ct)
    {
        var sizes = SizesOf(files);
        var ctx = await ContextAsync(cut, img => "cid:" + img.ContentId, sizes, ct);
        return renderer.Render(cut, ctx, Sendable(cut, sizes, files));
    }

    private static IReadOnlyList<RenderImage> Sendable(BriefEdition cut, IReadOnlyDictionary<Guid, (int, int)> sizes,
        IReadOnlyDictionary<Guid, (byte[] Bytes, string ContentType)> files) =>
        BriefRenderer.ImagesOf(cut, sizes, s => files.ContainsKey(s.FileId));

    private async Task<BriefRenderContext> ContextAsync(BriefEdition cut, Func<RenderImage, string> src,
        IReadOnlyDictionary<Guid, (int, int)>? sizes, CancellationToken ct)
    {
        var day = cut.Edition == Edition.Team ? await metrics.GetDayAsync(cut.Brief.BriefDate, ct) : null;
        // The overdue actions were read with the cut (EditionAsync), under its ceiling, so its marking counts them.
        return new BriefRenderContext(clock.TeamName, day, app.Value.BaseUrl, src, sizes, cut.Edition == Edition.Team ? cut.Open : null);
    }

    // The start of a file: enough for every format's header, so its type and size.
    private const int HeaderBytes = 256 * 1024;

    // The edition's screenshots that can go in an email (a raster image whose file is in the store), each read up to
    // max bytes.
    private async Task<Dictionary<Guid, (byte[] Bytes, string ContentType)>> ReadImagesAsync(BriefEdition cut, int max, CancellationToken ct)
    {
        var files = new Dictionary<Guid, (byte[] Bytes, string ContentType)>();
        foreach (var s in cut.Articles.SelectMany(a => a.Screenshots))
        {
            if (files.ContainsKey(s.FileId) || await ReadAsync(s.FileId, max, ct) is not { } bytes) continue;
            if (ImageSniffer.RasterContentType(bytes) is { } type) files[s.FileId] = (bytes, type);
        }
        return files;
    }

    private static Dictionary<Guid, (int, int)> SizesOf(IReadOnlyDictionary<Guid, (byte[] Bytes, string ContentType)> files)
    {
        var sizes = new Dictionary<Guid, (int, int)>();
        foreach (var (id, f) in files)
            if (ImageSniffer.Dimensions(f.Bytes) is { } size) sizes[id] = size;
        return sizes;
    }

    private async Task<byte[]?> ReadAsync(Guid fileId, int max, CancellationToken ct)
    {
        await using var stream = store.OpenRead(fileId);
        if (stream is null) return null;
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while (ms.Length < max && (read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, max - ms.Length)), ct)) > 0)
            await ms.WriteAsync(buffer.AsMemory(0, read), ct);
        return ms.ToArray();
    }

    private async Task<BriefEdition> EditionAsync(DateOnly date, Edition edition, CancellationToken ct)
    {
        await using var db = await dbs.CreateAsync(ct);
        var brief = await BriefService.Load(db.Briefs.AsNoTracking()).FirstOrDefaultAsync(b => b.BriefDate == date, ct)
                    ?? throw new KeyNotFoundException("There's no brief for that day.");
        var (publicMax, teamMax) = await settings.GetAsync(ct);
        if (edition == Edition.Public) return BriefEditions.Build(brief, edition, publicMax);
        var cut = BriefEditions.Build(brief, edition, teamMax, await BriefEditions.ActionsAsync(db, brief, ct));
        return cut with { Open = await Actions.ActionService.Outstanding(db, brief.BriefDate, teamMax, ct) };
    }

    // --- the log and the archive --------------------------------------------------------------------------------

    /// <summary>The sends of <paramref name="date"/> a TLP slip withdrew from the published briefs (for the send log).</summary>
    public async Task<IReadOnlySet<Guid>> WithdrawnSendsAsync(DateOnly date, CancellationToken ct = default)
    {
        RequireRead();
        await using var db = await dbs.CreateAsync(ct);
        return (await Withdrawn(db).Where(s => s.BriefDate == date).Select(s => s.Id).ToListAsync(ct)).ToHashSet();
    }

    /// <summary>Every send of the brief for <paramref name="date"/> (tests too), newest first.</summary>
    public async Task<IReadOnlyList<BriefSend>> SendsAsync(DateOnly date, CancellationToken ct = default)
    {
        RequireRead();
        await using var db = await dbs.CreateAsync(ct);
        var rows = await db.BriefSends.AsNoTracking().Where(s => s.BriefDate == date)
            .Select(s => new BriefSend
            {
                Id = s.Id, BriefId = s.BriefId, BriefDate = s.BriefDate, Edition = s.Edition, IsTest = s.IsTest, Ceiling = s.Ceiling,
                Marking = s.Marking, Subject = s.Subject, Recipients = s.Recipients, RecipientCount = s.RecipientCount, SentAtUtc = s.SentAtUtc,
                SentById = s.SentById, SentByName = s.SentByName, Status = s.Status, Error = s.Error,
            })
            .ToListAsync(ct);
        foreach (var s in rows.Where(s => !s.IsTest && SendOutcome.Seen(s.Status, s.SentAtUtc) != s.Status))
            (s.Status, s.Error) = (SendStatus.Unknown, SendOutcome.StaleError);
        return rows.OrderByDescending(s => s.SentAtUtc).ToList();
    }

    /// <summary>
    /// Settles a real send whose outcome isn't known (its relay failed partway, or it was left "sending" past the
    /// in-flight time), once someone has checked with a recipient: <paramref name="went"/>, it's Sent, so it's archived
    /// (and published, for the company-wide edition), can be corrected, and the brief is Sent; otherwise it's Failed.
    /// </summary>
    public async Task<BriefSend> SettleAsync(Guid sendId, bool went, CancellationToken ct = default)
    {
        RequireSend();
        await using var db = await dbs.CreateAsync(ct);
        var send = await db.BriefSends.FirstOrDefaultAsync(s => s.Id == sendId, ct) ?? throw new KeyNotFoundException("That send no longer exists.");
        if (send.IsTest || SendOutcome.Seen(send.Status, send.SentAtUtc) != SendStatus.Unknown)
            throw new InvalidOperationException(send.Status == SendStatus.Sending
                ? "That send is still with the relay. Wait for it to finish, then check the send log."
                : "Only a send whose outcome isn't known can be marked.");
        send.Status = went ? SendStatus.Sent : SendStatus.Failed;
        send.Error = SendOutcome.Settled(user.DisplayName, went);
        send.OutcomeAtUtc = DateTimeOffset.UtcNow;
        var brief = await WithArticles(db).FirstAsync(b => b.Id == send.BriefId, ct);
        if (went) await FinishAsync(db, brief, ct);
        brief.EditVersion = Guid.NewGuid();   // a confirmation made before this is refused
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException)
        {
            // The brief or one of its articles was saved meanwhile (an edit, the rota moving its presenter).
            throw new InvalidOperationException("The brief changed as you marked it. Check the send log, then mark it again.");
        }
        return send;
    }

    /// <summary>A frozen send with its images (team only: a send of either edition, tests included).</summary>
    public async Task<BriefSend?> GetSendAsync(Guid id, CancellationToken ct = default)
    {
        RequireRead();
        await using var db = await dbs.CreateAsync(ct);
        return await db.BriefSends.AsNoTracking().Include(s => s.Images).FirstOrDefaultAsync(s => s.Id == id, ct);
    }

    /// <summary>The sent company-wide editions, newest first: one per brief, the latest send of it (the newest <paramref name="take"/>).</summary>
    public async Task<IReadOnlyList<BriefSend>> PublishedAsync(int take = 120, CancellationToken ct = default)
    {
        RequireReadPublished();
        await using var db = await dbs.CreateAsync(ct);
        var rows = await Published(db)
            .Select(s => new BriefSend { Id = s.Id, BriefDate = s.BriefDate, Subject = s.Subject, Marking = s.Marking, SentAtUtc = s.SentAtUtc })
            .ToListAsync(ct);
        return rows.GroupBy(s => s.BriefDate).Select(g => g.OrderByDescending(s => s.SentAtUtc).First())
            .OrderByDescending(s => s.BriefDate).Take(take).ToList();
    }

    /// <summary>
    /// The sent company-wide editions (as <see cref="PublishedAsync"/>), each with the headlines it went out with, narrowed
    /// to those whose words match <paramref name="query"/>: the newest <paramref name="take"/> of them, so a page asks for
    /// one more than it shows to know whether there are older ones. A filter looks through every published brief, however
    /// old. Both come from the frozen HTML of the send, never from the articles, so nothing the company wasn't sent can
    /// show (or match). Each send is parsed once (<see cref="PublishedContentCache"/>).
    /// </summary>
    public async Task<IReadOnlyList<PublishedBrief>> PublishedBriefsAsync(string? query = null, int take = 120, CancellationToken ct = default)
    {
        var sends = await PublishedAsync(int.MaxValue, ct);
        await using var db = await dbs.CreateAsync(ct);

        // A day whose every send was withdrawn by a TLP slip still shows, with the slip's notice and nothing of the send.
        var withdrawn = (await Withdrawn(db).Select(s => new BriefSend { Id = s.Id, BriefDate = s.BriefDate, SentAtUtc = s.SentAtUtc, Marking = s.Marking })
                .ToListAsync(ct))
            .Where(w => sends.All(s => s.BriefDate != w.BriefDate))
            .GroupBy(w => w.BriefDate).Select(g => g.OrderByDescending(s => s.SentAtUtc).First()).ToList();
        var all = sends.Concat(withdrawn).OrderByDescending(s => s.BriefDate).ToList();
        // Without a filter, only the days asked for are read; a filter reads every day.
        var shown = PublishedContent.Words(query).Count == 0 ? all.Take(take).ToList() : all;

        var missing = shown.Where(s => !withdrawn.Contains(s) && !published.TryGet(s.Id, out _)).Select(s => s.Id).ToList();
        foreach (var chunk in missing.Chunk(100))
            foreach (var h in await Published(db).Where(s => chunk.Contains(s.Id)).Select(s => new { s.Id, s.WebHtml }).ToListAsync(ct))
                published.Add(h.Id, h.WebHtml);

        var days = shown.Select(s => s.BriefDate).ToHashSet();
        var corrections = Notices(await PublishedCorrections(db)
                .Select(c => new BriefCorrection { Id = c.Id, CorrectsSendId = c.CorrectsSendId, BriefDate = c.BriefDate, IsTlpSlip = c.IsTlpSlip, SentAtUtc = c.SentAtUtc, Status = c.Status })
                .ToListAsync(ct))
            .Where(c => days.Contains(c.BriefDate))
            .ToLookup(c => c.BriefDate);
        var unread = corrections.SelectMany(g => g).Where(c => !published.TryGet(c.Id, out _)).Select(c => c.Id).ToList();
        foreach (var chunk in unread.Chunk(100))
            foreach (var h in await db.BriefCorrections.AsNoTracking().Where(c => chunk.Contains(c.Id)).Select(c => new { c.Id, c.WebHtml }).ToListAsync(ct))
                published.Add(h.Id, h.WebHtml);

        var list = new List<PublishedBrief>();
        foreach (var s in shown)
        {
            if (list.Count >= take) break;
            var isWithdrawn = withdrawn.Contains(s);
            var fixes = corrections[s.BriefDate].OrderByDescending(c => c.SentAtUtc).ToList();
            var own = isWithdrawn ? PublishedContent.Empty : published.TryGet(s.Id, out var c) ? c : PublishedContent.Empty;
            var content = own.With(fixes.Select(f => published.TryGet(f.Id, out var fc) ? fc : PublishedContent.Empty));
            if (content.Matches(query)) list.Add(new(s, own.Headlines, fixes, isWithdrawn));
        }
        return list;
    }

    /// <summary>
    /// The corrections sent of the company-wide sends of <paramref name="date"/>, newest first, with their web copies: what
    /// the published page shows above the edition (a TLP slip shows only its notice, which is all its copy holds; of a send
    /// it withdrew, nothing else).
    /// </summary>
    public async Task<IReadOnlyList<BriefCorrection>> PublishedCorrectionsAsync(DateOnly date, CancellationToken ct = default)
    {
        RequireReadPublished();
        await using var db = await dbs.CreateAsync(ct);
        var rows = await PublishedCorrections(db)
            .Where(c => c.BriefDate == date)
            .Select(c => new BriefCorrection
            {
                Id = c.Id, CorrectsSendId = c.CorrectsSendId, BriefDate = c.BriefDate, Edition = c.Edition, IsTlpSlip = c.IsTlpSlip,
                Marking = c.Marking, SentAtUtc = c.SentAtUtc, Status = c.Status, WebHtml = c.WebHtml,
            })
            .ToListAsync(ct);
        return Notices(rows).OrderByDescending(c => c.SentAtUtc).ToList();
    }

    /// <summary>The latest sent company-wide edition of the brief for <paramref name="date"/>, with its images, or null.</summary>
    public async Task<BriefSend?> GetPublishedAsync(DateOnly date, CancellationToken ct = default)
    {
        RequireReadPublished();
        await using var db = await dbs.CreateAsync(ct);
        var sends = await Published(db).Include(s => s.Images).Where(s => s.BriefDate == date).ToListAsync(ct);
        return sends.OrderByDescending(s => s.SentAtUtc).FirstOrDefault();
    }

    /// <summary>
    /// Only a real send of the company-wide edition that the relay took is ever published, and never one withdrawn by a
    /// TLP slip correction (ADR 0015): its content, its words and its images are then the team's only.
    /// </summary>
    internal static IQueryable<BriefSend> Published(IAppDbContext db)
    {
        var slipped = Slipped(db);
        return Sent(db).Where(Expression.Lambda<Func<BriefSend, bool>>(Expression.Not(slipped.Body), slipped.Parameters));
    }

    /// <summary>
    /// The corrections of company-wide sends that Readers see: the ones sent, but of a send a TLP slip withdrew only the
    /// slip. Its other corrections quote the send's words ("Was: …", "Now: …"), which are the team's only once it's withdrawn.
    /// A slip shows whatever became of its email, since it's what says why the send is gone (<see cref="Notices"/>).
    /// </summary>
    internal static IQueryable<BriefCorrection> PublishedCorrections(IAppDbContext db)
    {
        var published = Published(db);
        return db.BriefCorrections.AsNoTracking().Where(c => c.Edition == Edition.Public
            && (c.IsTlpSlip || (c.Status == SendStatus.Sent && published.Any(s => s.Id == c.CorrectsSendId))));
    }

    // A slip whose email the relay refused, and that was sent again, is one notice: the later one.
    private static IEnumerable<BriefCorrection> Notices(IEnumerable<BriefCorrection> rows)
    {
        var list = rows.ToList();
        return list.Where(c => !(c.IsTlpSlip && c.Status == SendStatus.Failed
            && list.Any(o => o.IsTlpSlip && o.CorrectsSendId == c.CorrectsSendId && o.SentAtUtc > c.SentAtUtc)));
    }

    /// <summary>The company-wide sends that went and were then withdrawn by a TLP slip (<see cref="Slipped"/>).</summary>
    internal static IQueryable<BriefSend> Withdrawn(IAppDbContext db) =>
        Sent(db).Where(Slipped(db));

    // A slip withdraws the day's company-wide edition as it stood when the slip went: the send it names, and every other
    // send of that day made before the slip, earlier or later than the one named. A later one may be a re-send of
    // unchanged content for late recipients, or one put right; which it is can't be told for certain from the frozen
    // copies (a slip doesn't say which line slipped), so it goes too. Only a send made after the slip (the edition sent
    // again, put right) isn't withdrawn by it: that's how a corrected edition is published. A slip withdraws as soon as
    // it's frozen, whatever becomes of its email (with the relay, refused, or of unknown outcome): the team has confirmed
    // the content slipped, and what Readers see can't wait on an email to the recipients.
    private static Expression<Func<BriefSend, bool>> Slipped(IAppDbContext db) => s =>
        db.BriefCorrections.Any(c => c.IsTlpSlip && c.Edition == Edition.Public && c.BriefDate == s.BriefDate
            && c.SentAtUtc > s.SentAtUtc);

    private static IQueryable<BriefSend> Sent(IAppDbContext db) =>
        db.BriefSends.AsNoTracking().Where(s => s.Edition == Edition.Public && !s.IsTest && s.Status == SendStatus.Sent);

    /// <summary>A frozen HTML with its <c>cid:</c> images pointed at <paramref name="imageUrl"/> (by the copy's file id).</summary>
    public static string WithImageUrls(string html, IEnumerable<BriefSendImage> images, Func<Guid, string> imageUrl) =>
        images.Aggregate(html, (h, i) => h.Replace("\"cid:" + i.ContentId + "\"", "\"" + imageUrl(i.FileId) + "\"", StringComparison.Ordinal));

    private void RequireRead()
    {
        if (!user.Has(Permission.ReadTeamEdition)) throw new ForbiddenException("You don't have access to briefs.");
    }

    private void RequireReadPublished()
    {
        if (!user.Has(Permission.ReadPublished)) throw new ForbiddenException("You don't have access to the published briefs.");
    }

    private void RequireSend()
    {
        if (!user.Has(Permission.PresentBriefs)) throw new ForbiddenException("Only analysts can send the brief.");
    }
}
