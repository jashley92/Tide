using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tide.Application.Abstractions;
using Tide.Application.Actions;
using Tide.Application.Org;
using Tide.Application.Security;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using static Tide.Application.Distribution.EmailComposer;

namespace Tide.Application.Distribution;

/// <summary>One template as the editor shows it: its parts as used (saved or default), whether any is saved, and a stamp
/// of what's saved so a stale page can't write over a newer change.</summary>
public sealed record EmailTemplateView(EmailTemplate Template, string Subject, string Heading, string Body, bool IsCustomized, string Stamp)
{
    public string Text(EmailPart part) => part switch { EmailPart.Subject => Subject, EmailPart.Heading => Heading, _ => Body };
}

/// <summary>An email shown in the editor's preview.</summary>
public sealed record EmailPreview(string Subject, string Html);

/// <summary>
/// Administration › Email templates: reads and saves the wording of the notification emails.
/// Each part is a <c>Settings</c> row, audited like any change; a part saved as its default has no
/// row, so a reset is removing them. A save takes effect at once. Administrators only.
/// </summary>
public sealed class EmailTemplateService(IAppDbContextFactory dbs, ICurrentUser user, EmailTemplateStore store, IEmailSender email,
    OrgClock clock, IOptions<AppOptions> app, TimeProvider time)
{
    public async Task<IReadOnlyList<EmailTemplateView>> GetAllAsync(CancellationToken ct = default)
    {
        RequireAdmin();
        var saved = await SavedAsync(null, ct);
        return [.. EmailTemplateCatalog.All.Select(t => View(t, saved))];
    }

    /// <summary>
    /// Saves a template's three parts, each checked (<see cref="EmailTemplateCatalog.Problem(EmailTemplate, EmailPart, string?)"/>).
    /// <paramref name="stamp"/> is the template's <see cref="EmailTemplateView.Stamp"/> when the editor opened it: the save
    /// writes the whole template, so one someone changed since is refused rather than undone.
    /// </summary>
    public async Task SaveAsync(string id, string? subject, string? heading, string? body, string stamp, CancellationToken ct = default)
    {
        RequireAdmin();
        var t = EmailTemplateCatalog.ById(id) ?? throw new KeyNotFoundException("There's no such email template.");
        var parts = new Dictionary<EmailPart, string?> { [EmailPart.Subject] = subject, [EmailPart.Heading] = heading, [EmailPart.Body] = body };
        foreach (var (part, text) in parts)
            if (EmailTemplateCatalog.Problem(t, part, text) is { } problem) throw new ArgumentException(problem);

        await using (var db = await dbs.CreateAsync(ct))
        {
            var keys = Keys(t);
            var rows = await db.Settings.Where(s => keys.Contains(s.Key)).ToListAsync(ct);
            if (StampOf(rows) != stamp) throw Stale(t);
            foreach (var (part, text) in parts)
            {
                var key = EmailTemplateCatalog.Key(t.Id, part);
                var value = EmailTemplateCatalog.Normalize(part, text);
                var row = rows.FirstOrDefault(r => string.Equals(r.Key, key, StringComparison.OrdinalIgnoreCase));
                if (value == t.Default(part))
                {
                    if (row is not null) db.Settings.Remove(row);
                    continue;
                }
                if (row?.Value == value) continue;
                if (row is null) db.Settings.Add(row = new AppSetting { Key = key });
                row.Value = value;
                row.UpdatedAtUtc = time.GetUtcNow();
                row.UpdatedBy = user.UserId;
            }
            try { await db.SaveChangesAsync(ct); }
            // Another administrator's first change to this template went in between the read and this save.
            catch (DbUpdateException ex) { throw Stale(t, ex); }
        }
        await store.RefreshAsync(dbs, ct);
    }

    /// <summary>Puts a template back to its defaults (nothing happens when none of it is saved).</summary>
    public async Task ResetAsync(string id, CancellationToken ct = default)
    {
        RequireAdmin();
        var t = EmailTemplateCatalog.ById(id) ?? throw new KeyNotFoundException("There's no such email template.");
        await using (var db = await dbs.CreateAsync(ct))
        {
            var keys = Keys(t);
            var rows = await db.Settings.Where(s => keys.Contains(s.Key)).ToListAsync(ct);
            if (rows.Count == 0) return;
            db.Settings.RemoveRange(rows);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateConcurrencyException) { /* someone else removed them first: it's reset either way */ }
        }
        await store.RefreshAsync(dbs, ct);
    }

    /// <summary>The draft on screen, filled in with sample values, as it would arrive. Nothing is saved.</summary>
    public EmailPreview Preview(string id, string? subject, string? heading, string? body)
    {
        RequireAdmin();
        var t = EmailTemplateCatalog.ById(id) ?? throw new KeyNotFoundException("There's no such email template.");
        var r = EmailTemplateRenderer.Render(t, EmailTemplateCatalog.Normalize(EmailPart.Subject, subject),
            EmailTemplateCatalog.Normalize(EmailPart.Heading, heading), EmailTemplateCatalog.Normalize(EmailPart.Body, body), Samples(t));
        var footer = "An automatic message from TIDE. Replies aren't read.";
        return new EmailPreview(r.Subject, Shell(r.Heading, Subtitle(t), r.BodyHtml, footer));
    }

    /// <summary>Sends the draft on screen, with sample values, to the administrator's own address only. Nothing is saved.</summary>
    public async Task<EmailSendResult> SendTestAsync(string id, string? subject, string? heading, string? body, CancellationToken ct = default)
    {
        RequireAdmin();
        var t = EmailTemplateCatalog.ById(id) ?? throw new KeyNotFoundException("There's no such email template.");
        foreach (var (part, text) in new[] { (EmailPart.Subject, subject), (EmailPart.Heading, heading), (EmailPart.Body, body) })
            if (EmailTemplateCatalog.Problem(t, part, text) is { } problem) throw new ArgumentException(problem);
        if (string.IsNullOrWhiteSpace(user.Email)) throw new InvalidOperationException("Your directory account has no email address (the mail attribute), so a test can't be sent to you.");
        var preview = Preview(id, subject, heading, body);
        return await email.SendAsync(new OutgoingEmail([user.Email], "[TEST] " + preview.Subject, preview.Html, ToText(preview.Html), []), ct);
    }

    private static List<string> Keys(EmailTemplate t) => [.. Enum.GetValues<EmailPart>().Select(p => EmailTemplateCatalog.Key(t.Id, p))];

    private async Task<List<AppSetting>> SavedAsync(EmailTemplate? only, CancellationToken ct)
    {
        await using var db = await dbs.CreateAsync(ct);
        var prefix = only is null ? EmailTemplateCatalog.KeyPrefix : EmailTemplateCatalog.KeyPrefix + only.Id + ":";
        return await db.Settings.AsNoTracking().Where(s => s.Key.StartsWith(prefix)).ToListAsync(ct);
    }

    private static EmailTemplateView View(EmailTemplate t, IReadOnlyList<AppSetting> saved)
    {
        var mine = saved.Where(s => EmailTemplateCatalog.Parse(s.Key)?.Template == t).ToList();
        string Part(EmailPart part) => mine.FirstOrDefault(s => EmailTemplateCatalog.Parse(s.Key)?.Part == part) is { } row
                                       && EmailTemplateCatalog.Problem(t, part, row.Value) is null
            ? EmailTemplateCatalog.Normalize(part, row.Value) : t.Default(part);
        return new EmailTemplateView(t, Part(EmailPart.Subject), Part(EmailPart.Heading), Part(EmailPart.Body), mine.Count > 0, StampOf(mine));
    }

    // What's saved for one template, as a short hash: the editor opened with it, and a save checks it hasn't moved.
    private static string StampOf(IEnumerable<AppSetting> rows) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\u001f',
            rows.OrderBy(r => r.Key, StringComparer.OrdinalIgnoreCase).Select(r => r.Key.ToLowerInvariant() + "\u001e" + r.Value)))))[..16];

    private static InvalidOperationException Stale(EmailTemplate t, Exception? inner = null) =>
        new($"Someone changed “{t.Title}” since you opened it, so nothing was saved: it would have undone their change. " +
            "Reload the page, then make yours again.", inner);

    private string Subtitle(EmailTemplate t) => t.Id switch
    {
        "digest" => $"{clock.TeamName} · week of {SampleMonday:MMMM d}",
        "brief-not-ready" => $"{clock.TeamName} · the meeting is at {clock.MeetingTime.ToString("h:mm tt", CultureInfo.InvariantCulture)}",
        "presenting" => $"{clock.TeamName} · from {SampleDay:dddd, MMMM d}",
        _ when t.Id.StartsWith("rotation-", StringComparison.Ordinal) => clock.TeamName + " brief rotation",
        _ => $"{clock.TeamName} · {clock.Today.ToString("dddd, MMMM d", CultureInfo.InvariantCulture)}",
    };

    private DateOnly SampleDay => clock.Days.Next(clock.Today);
    private DateOnly SampleMonday => clock.Today.AddDays(-(((int)clock.Today.DayOfWeek + 6) % 7));

    // Values that look like the real thing, for the preview and a test send.
    private Dictionary<string, string> Samples(EmailTemplate t)
    {
        var today = clock.Today;
        var baseUrl = string.IsNullOrWhiteSpace(app.Value.BaseUrl) ? "https://tide.example" : app.Value.BaseUrl.TrimEnd('/');
        string Link(string text, string path) => $"<a style=\"color:{Palette.Link};\" href=\"{Esc(baseUrl + "/" + path)}\">{Esc(text)}</a>";
        var sample = SampleActions(today);
        var day = SampleDay;
        var change = "Robin covers Sam's brief days " + day.ToString("ddd MMM d", CultureInfo.InvariantCulture) + ".";
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Actions"] = "2 actions",
            ["OverdueActions"] = "2 overdue actions",
            ["ActionList"] = ReminderService.ActionTable(sample, today, baseUrl, clock.Days),
            ["Team"] = "Detection engineering",
            ["WeekOf"] = SampleMonday.ToString("MMM d", CultureInfo.InvariantCulture),
            ["OpenCount"] = "2",
            ["OverdueCount"] = ReminderService.OverdueCount(1),
            ["ClosedCount"] = "1",
            ["OpenList"] = ReminderService.ListHeading("Open", top: 8) + ReminderService.ActionTable(sample, today, baseUrl, clock.Days),
            ["ClosedList"] = ReminderService.ListHeading("Closed in the last week", top: 14) + ReminderService.ActionTable(SampleClosed(today), today, baseUrl, clock.Days),
            ["Status"] = "Today's brief has 4 articles and isn't marked ready.",
            ["BriefLink"] = " " + Link("Open it in TIDE", "briefs/" + today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)) + ".",
            ["MeetingTime"] = clock.MeetingTime.ToString("h:mm tt", CultureInfo.InvariantCulture),
            ["Day"] = day.ToString("dddd, MMMM d", CultureInfo.InvariantCulture),
            ["FirstDay"] = day.ToString("ddd MMM d", CultureInfo.InvariantCulture),
            ["RotationLink"] = t == EmailTemplateCatalog.Presenting ? " " + Link("See the rotation", "rotation") + "." : " (" + Link("open it", "rotation") + ")",
            ["Requester"] = "Sam",
            ["Partner"] = "Robin",
            ["Ask"] = "cover their brief days",
            ["Change"] = change,
            ["ChangeShort"] = "Robin covers Sam " + day.ToString("ddd MMM d", CultureInfo.InvariantCulture),
            ["Note"] = "<p style=\"margin:0 0 12px;\">“I'm out at a conference that day.”</p>",
            ["DecisionNote"] = t == EmailTemplateCatalog.RotationDeclined ? " “I'm on leave that week.”" : ": “Happy to.”",
            ["RecordedBy"] = "Robin",
        };
        return values;
    }

    private static List<ActionItem> SampleActions(DateOnly today) =>
    [
        new() { Number = 41, Title = "Block the phishing domains from today's brief at the mail gateway", Kind = ActionKind.Block, Priority = Priority.P2, AssigneeName = "Robin", DueDate = today, Status = ActionStatus.Open },
        new() { Number = 42, Title = "Hunt for the loader's scheduled task across endpoints", Kind = ActionKind.ThreatHunt, Priority = Priority.P1, AssigneeName = "Sam", DueDate = today.AddDays(-2), Status = ActionStatus.InProgress },
    ];

    private static List<ActionItem> SampleClosed(DateOnly today) =>
    [
        new() { Number = 39, Title = "Patch the VPN appliances against the edge zero-day", Kind = ActionKind.PatchRemediate, Priority = Priority.P1, AssigneeName = "Robin", DueDate = today.AddDays(-3), ClosedDate = today.AddDays(-1), Outcome = ActionOutcome.Remediated, Status = ActionStatus.Done },
    ];

    private void RequireAdmin()
    {
        if (!user.Has(Permission.Administer)) throw new ForbiddenException("Only an administrator can change the email templates.");
    }
}
