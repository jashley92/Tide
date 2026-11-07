using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tide.Application.Abstractions;
using Tide.Application.Articles;
using Tide.Application.Distribution;
using Tide.Application.Org;
using Tide.Application.Security;
using Tide.Application.Settings;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.Application.Actions;

/// <summary>An address the To line offers, and where it comes from ("the template's address").</summary>
public sealed record OutreachRecipient(string Address, string Label);

/// <summary>A template filled for an action: the draft, who it's offered to, and the product it was filled with.</summary>
public sealed record OutreachWritten(OutreachTemplate Template, OutreachDraft Draft, IReadOnlyList<OutreachRecipient> Recipients, StackProduct? Product)
{
    /// <summary>The To line it starts with: the template's fixed address, else the product's contact.</summary>
    public string To => Recipients.Count > 0 ? Recipients[0].Address : "";
}

/// <summary>What the analyst marks sent: the template it came from, to whom, and the subject and body as they sent them.</summary>
public sealed class OutreachSend
{
    public Guid? TemplateId { get; set; }
    public string To { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    public TlpLevel Tlp { get; set; } = TlpLevel.Clear;
}

/// <summary>An Outreach action's emails, newest first, with when a reminder follows up on each still waiting.</summary>
public sealed record OutreachState(IReadOnlyList<OutreachMessage> Messages, int? FollowUpDays)
{
    public static readonly OutreachState None = new([], null);

    /// <summary>The email waiting longest for a reply, if any: the action shows "Waiting for reply since" from it.</summary>
    public OutreachMessage? Oldest => Messages.Where(m => m.IsWaiting).MinBy(m => m.SentAtUtc);

    public bool Waiting => Oldest is not null;
}

/// <summary>
/// The follow-up reminder's dates, worked out one way for the reminder that goes and the action that says when it will,
/// so the two can't disagree.
/// </summary>
public static class OutreachDates
{
    /// <summary>The day, in the organization's zone, an email marked sent at <paramref name="sentAtUtc"/> was sent.</summary>
    public static DateOnly SentOn(DateTimeOffset sentAtUtc, OrgClock clock) => clock.DayOf(sentAtUtc);

    /// <summary>The day the reminder follows up an email sent at <paramref name="sentAtUtc"/>: <paramref name="days"/> business days on.</summary>
    public static DateOnly FollowUpOn(DateTimeOffset sentAtUtc, int days, OrgClock clock) =>
        ActionSla.DueDate(SentOn(sentAtUtc, clock), Math.Max(days, 1), clock.Days)!.Value;
}

/// <summary>
/// Outreach actions' emails (ADR 0019). TIDE writes the email and never sends it: "Write the email" fills a template from
/// the action (<see cref="WriteAsync"/>) for the analyst to edit and copy into their own mail; they mark it sent to whom
/// (<see cref="MarkSentAsync"/>), and the action waits for the reply, with a follow-up reminder
/// (<see cref="ReminderKind.OutreachFollowUp"/>), until they record it (<see cref="RecordReplyAsync"/>), which closes the
/// action with an outcome by the same rule as any close. Whoever works the action does all of it; each step is a line in
/// its history.
/// </summary>
public sealed class OutreachService(IAppDbContextFactory dbs, ICurrentUser user, OrgClock clock, ActionService actions,
    IOptions<AppOptions> app, IOptionsMonitor<ReminderOptions> reminders)
{
    /// <summary>The most addresses one email is marked sent to.</summary>
    public const int MaxRecipients = 10;

    /// <summary>The action's emails and the follow-up wait in force (null when the reminder is off).</summary>
    public async Task<OutreachState> ForActionAsync(Guid actionId, CancellationToken ct = default)
    {
        ActionService.RequireRead(user);
        await using var db = await dbs.CreateAsync(ct);
        var messages = await db.OutreachMessages.AsNoTracking().Where(m => m.ActionId == actionId).ToListAsync(ct);
        return new OutreachState([.. messages.OrderByDescending(m => m.SentAtUtc)], FollowUpDays);
    }

    /// <summary>How many business days after it's sent an unanswered email is followed up, or null when the reminder is off.</summary>
    public int? FollowUpDays => reminders.CurrentValue.OutreachFollowUp is { Enabled: true } f ? ReminderOptions.FollowUpOptions.Clamp(f.Days) : null;

    /// <summary>The day a reminder follows up <paramref name="m"/>, or null when it won't (answered, or the reminder is off).</summary>
    public DateOnly? FollowUpOn(OutreachMessage m) =>
        m.IsWaiting && FollowUpDays is { } days ? OutreachDates.FollowUpOn(m.SentAtUtc, days, clock) : null;

    /// <summary>
    /// The products from Our stack the action is about, live ones only: those its article is tagged with, then those its
    /// title or description (or its article's title or bottom line) names. The first is the one an email is filled with
    /// unless another is picked.
    /// </summary>
    public async Task<IReadOnlyList<StackProduct>> ProductsAsync(Guid actionId, CancellationToken ct = default)
    {
        ActionService.RequireRead(user);
        await using var db = await dbs.CreateAsync(ct);
        var a = await db.Actions.AsNoTracking().Include(x => x.Article).FirstOrDefaultAsync(x => x.Id == actionId, ct)
                ?? throw new KeyNotFoundException("That action no longer exists.");
        return await ProductsAsync(db, a, ct);
    }

    private static async Task<IReadOnlyList<StackProduct>> ProductsAsync(IAppDbContext db, ActionItem a, CancellationToken ct)
    {
        var tagged = a.ArticleId is { } article
            ? await db.ArticleProducts.AsNoTracking().Where(t => t.ArticleId == article).Select(t => t.ProductId).ToListAsync(ct)
            : [];
        // The live products once, with their teams (an email names the owner): matched against here, and returned from here.
        var live = await Stack.StackService.InUse(db).Include(p => p.Team).ToListAsync(ct);
        var entries = live.Select(Stack.StackService.Entry).ToList();
        var named = Stack.StackService.MatchAll(entries, a.Title, a.Description)
            .Concat(a.Article is { } art ? Stack.StackService.MatchAll(entries, art.Title, art.Bluf) : [])
            .Select(e => e.Id);
        var ids = tagged.Concat(named).Distinct().ToList();
        return [.. ids.Select(id => live.Find(p => p.Id == id)).OfType<StackProduct>()];
    }

    /// <summary>
    /// Fills <paramref name="templateId"/> for the action, for whoever works it: the article cut to the template's ceiling
    /// (<see cref="OutreachFill.Cut"/>), the indicators a hunt on the action would search, of those the ceiling lets
    /// through (<see cref="ActionService.HuntIndicators"/>), <paramref name="productId"/> (else the first product the
    /// action names), and the action and its sender. Saves nothing. A retired template isn't used. <paramref name="products"/> is the action's
    /// products as <see cref="ProductsAsync(Guid, CancellationToken)"/> gave them, when the caller already holds them;
    /// left out, they're read here.
    /// </summary>
    public async Task<OutreachWritten> WriteAsync(Guid actionId, Guid templateId, Guid? productId = null, IReadOnlyList<StackProduct>? products = null,
        CancellationToken ct = default)
    {
        await using var db = await dbs.CreateAsync(ct);
        var a = await ActionService.HuntActionAsync(db, actionId, tracked: false, ct);
        await ActionService.RequireWork(db, user, a, ct);
        var template = await db.OutreachTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == templateId, ct)
                       ?? throw new KeyNotFoundException("That template no longer exists.");
        if (template.IsRetired) throw new InvalidOperationException($"{template.Name} is retired. Restore it on Outreach templates to use it.");

        // The article's parts the cut reads, beside the indicators the hunt loader brought.
        if (a.Article is { } article)
        {
            article.Bullets = await db.ArticleBullets.AsNoTracking().Where(b => b.ArticleId == article.Id).ToListAsync(ct);
            article.Cves = await db.ArticleCves.AsNoTracking().Where(c => c.ArticleId == article.Id).ToListAsync(ct);
        }
        var cut = OutreachFill.Cut(a.Article, a.Indicators.Count, template.Ceiling);
        // Only what the ceiling lets through is merged, as a hunt merges the article's indicators and the action's own.
        var indicators = ActionService.HuntIndicators(cut.Article is null ? [] : a.Article!.Indicators, cut.OwnIndicators ? a.Indicators : []);

        var cveIds = cut.Article?.Article.Cves.Select(c => c.CveId).ToList() ?? [];
        var cves = cveIds.Count == 0 ? new Dictionary<string, CveRecord>()
            : await db.CveRecords.AsNoTracking().Where(c => cveIds.Contains(c.CveId)).ToDictionaryAsync(c => c.CveId, StringComparer.OrdinalIgnoreCase, ct);

        products ??= await ProductsAsync(db, a, ct);
        StackProduct? product = null;
        if (productId is { } pid)
            product = products.FirstOrDefault(p => p.Id == pid)
                      ?? await db.StackProducts.AsNoTracking().Include(p => p.Team).FirstOrDefaultAsync(p => p.Id == pid && !p.IsRetired, ct)
                      ?? throw new ArgumentException("That product is no longer on Our stack.");
        else product = products.Count > 0 ? products[0] : null;

        var baseUrl = app.Value.BaseUrl?.TrimEnd('/');
        var facts = new OutreachFacts
        {
            Ceiling = template.Ceiling,
            FromArticle = a.Article is not null,
            Article = cut.Article,
            Cves = cves,
            Indicators = indicators,
            IndicatorsTlp = cut.IndicatorsTlp,
            IndicatorsHeldBack = cut.IndicatorsHeldBack,
            Product = product,
            ActionNumber = a.Number,
            ActionLink = string.IsNullOrWhiteSpace(baseUrl) ? null : $"{baseUrl}/actions/{a.Number.ToString(CultureInfo.InvariantCulture)}",
            Due = a.DueDate,
            SenderName = user.DisplayName,
            SenderTeam = await SenderTeamAsync(db, a, ct),
            Today = clock.Today,
            Days = clock.Days,
            LeftOut = cut.LeftOut,
        };
        var draft = OutreachFill.Fill(template.Subject, template.Body, facts);

        var to = new List<OutreachRecipient>();
        if (template.To is { } fixedTo) to.Add(new(fixedTo, "the template's address"));
        foreach (var p in products.Prepend(product).OfType<StackProduct>().DistinctBy(p => p.Id))
            if (!string.IsNullOrWhiteSpace(p.ContactEmail) && !to.Exists(r => string.Equals(r.Address, p.ContactEmail, StringComparison.OrdinalIgnoreCase)))
                to.Add(new(p.ContactEmail, $"{p.Name}'s contact{(p.ContactName is { } n ? ", " + n : "")}"));
        return new OutreachWritten(template, draft, to, product);
    }

    // The sender's team: the action's when they're on it, else the first of theirs by name, else the organization's.
    private async Task<string> SenderTeamAsync(IAppDbContext db, ActionItem a, CancellationToken ct)
    {
        var mine = await db.TeamMembers.AsNoTracking().Where(m => m.UserId == user.UserId)
            .Join(db.Teams.Where(t => !t.IsRetired), m => m.TeamId, t => t.Id, (m, t) => new { t.Id, t.Name }).ToListAsync(ct);
        return mine.Find(t => t.Id == a.TeamId)?.Name ?? mine.Select(t => t.Name).Order(StringComparer.OrdinalIgnoreCase).FirstOrDefault() ?? clock.TeamName;
    }

    /// <summary>
    /// Records that the analyst sent <paramref name="send"/> from their own mail: to one or more valid addresses, with the
    /// subject and body as sent (a marker left unfilled is refused). The action then waits for the reply. Whoever works an
    /// Outreach action does it; the history says to whom and from which template.
    /// </summary>
    public async Task<OutreachMessage> MarkSentAsync(Guid actionId, OutreachSend send, CancellationToken ct = default)
    {
        var to = Recipients(send.To);
        var subject = OutreachFill.CleanSubject(send.Subject, OutreachMessage.MaxSubject,
            "Give the email's subject.", $"A subject can be at most {OutreachMessage.MaxSubject} characters.");
        var body = OutreachFill.CleanBody(send.Body, OutreachMessage.MaxBody,
            "Give the email's body.", $"The body can be at most {OutreachMessage.MaxBody:N0} characters.");
        if (OutreachFill.HasMarker(subject) || OutreachFill.HasMarker(body))
            throw new ArgumentException("The email still has a [[…]] marker where TIDE had nothing to fill. Fill it in or take it out first.");
        var tlp = Tlp.Checked(send.Tlp);

        await using var db = await dbs.CreateAsync(ct);
        var a = await OutreachActionAsync(db, actionId, ct);
        string? templateName = null;
        if (send.TemplateId is { } tid)
            templateName = await db.OutreachTemplates.AsNoTracking().Where(t => t.Id == tid).Select(t => t.Name).FirstOrDefaultAsync(ct)
                           ?? throw new KeyNotFoundException("That template no longer exists.");
        var m = new OutreachMessage
        {
            ActionId = a.Id, TemplateId = send.TemplateId, TemplateName = templateName, To = to, Subject = subject, Body = body, Tlp = tlp,
            SentAtUtc = DateTimeOffset.UtcNow, SentById = user.UserId, SentByName = user.DisplayName,
        };
        db.OutreachMessages.Add(m);
        db.ActionEvents.Add(ActionService.Event(user, a.Id,
            $"Sent an email to {to} ({Tlp.Label(tlp)}): \"{subject}\"{(templateName is null ? "" : $", from the template \"{templateName}\"")}."));
        await db.SaveChangesAsync(ct);
        return m;
    }

    /// <summary>
    /// Records the reply to <paramref name="messageId"/>: what it said, and the file kept with it (one already added to the
    /// action's files, by its stored file's id), if any. With <paramref name="close"/>, the action closes Done with <paramref name="outcome"/> in the
    /// same save, by the rule every close follows (<see cref="ActionService.OutcomesFor"/>); without it the action stays
    /// as it is (another email is still waiting, say). A reply already recorded is refused.
    /// </summary>
    public async Task RecordReplyAsync(Guid messageId, string? notes, bool close, ActionOutcome outcome = ActionOutcome.None, Guid? fileId = null,
        CancellationToken ct = default)
    {
        var clean = ArticleService.CleanOrNull(notes, OutreachMessage.MaxNotes, multiline: true);
        await using var db = await dbs.CreateAsync(ct);
        var m = await db.OutreachMessages.FirstOrDefaultAsync(x => x.Id == messageId, ct) ?? throw new KeyNotFoundException("That email is no longer on the action.");
        var a = await OutreachActionAsync(db, m.ActionId, ct);
        if (!m.IsWaiting) throw new StaleEditException($"{m.ReplyRecordedByName ?? "Someone"} already recorded the reply to this email.");
        string? file = null;
        if (fileId is { } fid)
            file = await db.ActionFiles.AsNoTracking().Where(f => f.FileId == fid && f.ActionId == a.Id && f.RemovedAtUtc == null).Select(f => f.FileName).FirstOrDefaultAsync(ct)
                   ?? throw new ArgumentException("That file isn't on this action.");
        m.RepliedAtUtc = DateTimeOffset.UtcNow;
        m.ReplyRecordedByName = user.DisplayName;
        m.ReplyNotes = clean;
        m.ReplyFileId = fileId;
        var lead = $"Reply from {m.To} recorded{(file is null ? "" : $" (with {file})")}.";
        if (close)
        {
            if (!a.IsOpen) throw new InvalidOperationException($"A-{a.Number} is already closed. Record the reply without closing it.");
            actions.ApplyStatus(db, a, ActionStatus.Done, outcome, clean, lead);
        }
        else db.ActionEvents.Add(ActionService.Event(user, a.Id, lead[..^1] + (clean is null ? "." : $": {clean}")));
        await ActionService.SaveEditAsync(db, ct);
    }

    /// <summary>Takes back an email marked sent by mistake, while it's still waiting for its reply; the history keeps that it was.</summary>
    public async Task WithdrawAsync(Guid messageId, CancellationToken ct = default)
    {
        await using var db = await dbs.CreateAsync(ct);
        if (await db.OutreachMessages.FirstOrDefaultAsync(x => x.Id == messageId, ct) is not { } m) return;
        var a = await OutreachActionAsync(db, m.ActionId, ct);
        if (!m.IsWaiting) throw new InvalidOperationException("Its reply is recorded, so it stays.");
        db.OutreachMessages.Remove(m);
        db.ActionEvents.Add(ActionService.Event(user, a.Id, $"Took back the email to {m.To} (\"{m.Subject}\"): not sent after all."));
        try { await db.SaveChangesAsync(ct); }
        // Someone took it back first.
        catch (DbUpdateConcurrencyException) { }
    }

    // The action, tracked, for whoever works it, and only an Outreach one: the others don't track emails.
    private async Task<ActionItem> OutreachActionAsync(IAppDbContext db, Guid actionId, CancellationToken ct)
    {
        var a = await ActionService.FindAsync(db, actionId, ct);
        await ActionService.RequireWork(db, user, a, ct);
        if (a.Kind != ActionKind.Outreach) throw new InvalidOperationException($"Only an {ActionService.KindName(ActionKind.Outreach)} action keeps its emails. Change its kind first.");
        return a;
    }

    /// <summary>The addresses an email went to as they're kept ("a@x.org; b@x.org"): at least one, each a valid address.</summary>
    internal static string Recipients(string? text)
    {
        var value = DistributionSettings.CheckedAddresses(text,
            MaxRecipients, $"At most {MaxRecipients} addresses; use a distribution list for more.",
            OutreachMessage.MaxTo, "Those addresses are too long; use a distribution list.");
        // Nothing given passes every check there, so it's refused here: an email went to someone.
        return value.Length > 0 ? value : throw new ArgumentException("Say who it went to: an email address.");
    }
}
