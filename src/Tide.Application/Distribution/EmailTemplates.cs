using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Tide.Application.Abstractions;
using Tide.Application.Content;
using static Tide.Application.Distribution.EmailComposer;

namespace Tide.Application.Distribution;

/// <summary>
/// A value a template may name as <c>{{Name}}</c>. A plain one is text (HTML-encoded in the body, so an action's title
/// can't add markup); an <see cref="Html"/> one is markup TIDE writes (a table of actions, a link) and goes only in the
/// body. A <see cref="Required"/> one is what the email is for, so the body can't leave it out.
/// </summary>
public sealed record EmailToken(string Name, string Description, bool Html = false, bool Required = false);

/// <summary>The three parts of a template an administrator edits.</summary>
public enum EmailPart
{
    Subject = 0,
    Heading = 1,
    Body = 2,
}

/// <summary>
/// One notification email whose subject, heading and body an administrator may change.
/// The body is Markdown. TIDE keeps the rest: the shell (header, colours, footer), the line under the
/// heading, the TLP marking a reminder carries in its subject and at its top, and the plain-text copy.
/// </summary>
public sealed record EmailTemplate(
    string Id, string Title, string Description, string DefaultSubject, string DefaultHeading, string DefaultBody,
    IReadOnlyList<EmailToken> Tokens, string Footer = EmailTemplateCatalog.DefaultFooter)
{
    public string Default(EmailPart part) => part switch
    {
        EmailPart.Subject => DefaultSubject,
        EmailPart.Heading => DefaultHeading,
        _ => DefaultBody,
    };

    public EmailToken? Token(string name) => Tokens.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// The notification emails an administrator may reword, with their defaults (so one can always be reset). A change is a
/// row in the <c>Settings</c> table, <c>EmailTemplate:{id}:{Subject|Heading|Body}</c>, audited like any setting; a part
/// left as its default has no row. The brief's own email isn't here: it's written from the brief.
/// </summary>
public static partial class EmailTemplateCatalog
{
    public const string KeyPrefix = "EmailTemplate:";

    /// <summary>The footer under every notification email but one written for people outside TIDE (its own, by template).
    /// It's TIDE's, not an administrator's to reword; it may name the template's plain values.</summary>
    public const string DefaultFooter = "An automatic message from TIDE. Replies aren't read.";
    public const int MaxLine = 200;
    public const int MaxBody = 4000;   // the Settings table's value column

    public static string Key(string id, EmailPart part) => $"{KeyPrefix}{id}:{part}";

    private static readonly EmailToken RotationLink = new("RotationLink", "\" (open it)\" linking to the rotation page. Blank when this server's address isn't set.", Html: true);
    private static readonly EmailToken Change = new("Change", "The change in a sentence: \"Robin covers Sam's brief days Mon Nov 2 to Wed Nov 4.\"");
    private static readonly EmailToken ChangeShort = new("ChangeShort", "The change in a few words, for a subject: \"Robin covers Sam Mon Nov 2\".");
    private static readonly EmailToken Requester = new("Requester", "Who asked for the swap or cover.");
    private static readonly EmailToken Partner = new("Partner", "Who was asked to take the days.");
    private static readonly EmailToken ActionList = new("ActionList", "The actions: number, title, kind, priority, who has it and when it's due. Written by TIDE.", Html: true, Required: true);
    private static readonly EmailToken MeetingTime = new("MeetingTime", "When the brief meets, for example 9:30 AM.");

    public static readonly EmailTemplate DueSoon = new(
        "due-soon", "Actions due soon",
        "Sent at the reminder time to each action's assignee (or its team) when actions are due today or the next business day. Needs due-soon reminders on (Teams & SLA).",
        "TIDE: {{Actions}} due soon", "{{Actions}} due soon",
        "These actions are due today or on the next business day.\n\n{{ActionList}}",
        [new("Actions", "How many, in words: \"2 actions\"."), ActionList]);

    public static readonly EmailTemplate Overdue = new(
        "overdue", "Overdue actions",
        "Sent at the reminder time to each action's assignee (or its team) when actions are past their due date. Needs overdue reminders on (Teams & SLA).",
        "TIDE: {{OverdueActions}}", "{{OverdueActions}}",
        "These actions are past their due date. Close them with an outcome, or move the date.\n\n{{ActionList}}",
        [new("OverdueActions", "How many, in words: \"2 overdue actions\"."), ActionList]);

    public static readonly EmailTemplate Digest = new(
        "digest", "Weekly team digest",
        "Sent to each team (its mailbox, or its members) on the digest day: its open actions and those closed in the last week. Needs the digest on (Teams & SLA).",
        "TIDE: {{Team}} actions, week of {{WeekOf}}", "{{Team}}: the week's actions",
        "**{{OpenCount}}** open, {{OverdueCount}} overdue, **{{ClosedCount}}** closed in the last week.\n\n{{OpenList}}\n\n{{ClosedList}}",
        [
            new("Team", "The team's name."),
            new("WeekOf", "The Monday the week starts, for example Nov 2."),
            new("OpenCount", "How many of its actions are open."),
            new("OverdueCount", "How many of them are overdue, in bold (red when there are any).", Html: true),
            new("ClosedCount", "How many were closed in the last week."),
            new("OpenList", "\"Open\" and the open actions, soonest due first. Blank when there are none.", Html: true),
            new("ClosedList", "\"Closed in the last week\" and those actions. Blank when there are none.", Html: true),
        ]);

    public static readonly EmailTemplate PersonalDigest = new(
        "personal-digest", "Personal digest",
        "Sent to each person who chose a digest in My notifications: daily at the reminder time on business days, or weekly on the digest day. Their actions that are overdue or due soon, and those new to them since the last one. Needs the personal digest offered (Teams & SLA).",
        "TIDE: your actions, {{Period}}", "Your actions",
        "{{OverdueCount}} overdue, **{{DueSoonCount}}** due soon, **{{NewCount}}** new to you.\n\n{{OverdueList}}\n\n{{DueSoonList}}\n\n{{NewList}}\n\nYou get this {{Cadence}} digest because you chose it in My notifications.",
        [
            new("Name", "The person's name."),
            new("Period", "The day (Thursday, November 5) or the week (the week of November 2)."),
            new("Cadence", "\"daily\" or \"weekly\"."),
            new("OverdueCount", "How many of their actions are overdue, in bold (red when there are any).", Html: true),
            new("DueSoonCount", "How many are due today or the next business day."),
            new("NewCount", "How many others are new to them since their last digest."),
            new("OverdueList", "\"Overdue\" and those actions. Blank when there are none.", Html: true),
            new("DueSoonList", "\"Due today or the next business day\" and those actions. Blank when there are none.", Html: true),
            new("NewList", "\"New to you since your last digest\" and those actions. Blank when there are none.", Html: true),
        ]);

    public static readonly EmailTemplate OutreachFollowUp = new(
        "outreach-follow-up", "Outreach follow-up",
        "Sent at the reminder time to whoever marked an Outreach action's email sent, once it has waited the set business days with no reply recorded and the action is still open. Needs the outreach follow-up on (Teams & SLA).",
        "TIDE: no reply yet on {{Actions}}", "No reply yet",
        "You emailed about these actions {{Days}} ago or more, and no reply is recorded yet. Follow up, or record the reply on the action.\n\n{{WaitingList}}\n\n{{ActionList}}",
        [
            new("Actions", "How many, in words: \"2 actions\"."),
            new("Days", "How long an email waits before this reminder: \"5 business days\"."),
            new("WaitingList", "Each email: the action, who it went to and when it was sent. Written by TIDE.", Html: true),
            ActionList,
        ]);

    public static readonly EmailTemplate Handoff = new(
        "handoff", "Action handed to a team",
        "Sent when an action is handed to a team: to its mailbox, or to its members when it has none (each may turn it off in My notifications). Not sent to an external team.",
        "TIDE: an action handed to {{Team}}", "Handed to {{Team}}",
        "{{By}} handed this action from {{FromTeam}} to {{Team}}: “{{Reason}}”\n\n{{ActionList}}",
        [
            new("Team", "The team it was handed to."),
            new("FromTeam", "The team it was handed from."),
            new("By", "Who handed it over."),
            new("Reason", "Why, as they wrote it."),
            ActionList,
        ]);

    // An external team doesn't use TIDE: its email names no TIDE page and says nothing only TIDE's users would follow.
    public static readonly EmailTemplate ExternalReminder = new(
        "external-reminder", "External team reminder",
        "Sent at the reminder time to an external team's contact email: its requests due soon or overdue, as its own reminder cadence sets. It goes outside TIDE, so it has no TIDE links, and replies go to whoever follows the work. Needs external team reminders on (Teams & SLA), and the team's reminders on.",
        "{{From}}: {{Requests}} due soon or overdue", "Requests due soon or overdue",
        "Hello {{Contact}},\n\n{{From}} is following these requests with {{Team}}. Each is due soon or past its due date.\n\n{{RequestList}}\n\nPlease reply to this email with an update on each: where it stands, and when you expect it to be done.",
        [
            new("Team", "The external team's name."),
            new("Contact", "Their contact's name, or the team's name when none is set."),
            new("From", "Who is asking: this server's team name (Organization settings)."),
            new("Requests", "How many, in words: \"2 requests\"."),
            new("RequestList", "Each request: its title, what's asked (its details), when it's due and how overdue, and its ticket link. Written by TIDE, with no TIDE links.", Html: true, Required: true),
        ], Footer: "Sent by {{From}} to follow up its requests to {{Team}}. Reply to this email to answer.");

    public static readonly EmailTemplate BriefNotReady = new(
        "brief-not-ready", "Brief not ready",
        "Sent on a business day, at the brief-not-ready time, to the presenting analyst (or the listed recipients) when today's brief isn't marked ready. Needs it on (Teams & SLA).",
        "TIDE: today's brief isn't ready", "Today's brief isn't ready",
        "{{Status}}{{BriefLink}}",
        [
            new("Status", "\"There's no brief for today yet.\" or \"Today's brief has 4 articles and isn't marked ready.\""),
            new("BriefLink", "\" Open it in TIDE.\" linking to the brief. Blank when this server's address isn't set.", Html: true),
            MeetingTime,
        ]);

    public static readonly EmailTemplate Presenting = new(
        "presenting", "Presenting next",
        "Sent at the reminder time to whoever presents the brief the next business day (and doesn't today). Needs presenting reminders on (Teams & SLA).",
        "TIDE: you're presenting the brief from {{FirstDay}}", "You're presenting the brief next",
        "You're presenting the daily brief from **{{Day}}**; the meeting is at {{MeetingTime}}.{{RotationLink}}",
        [
            new("Day", "The day they present, for example Monday, November 2."),
            new("FirstDay", "The same day, short, for a subject: Mon Nov 2."),
            MeetingTime,
            RotationLink with { Description = "\" See the rotation.\" linking to the rotation page. Blank when this server's address isn't set." },
        ]);

    public static readonly EmailTemplate RotationRequest = new(
        "rotation-request", "Swap or cover request",
        "Sent to the analyst asked to swap brief days or cover them.",
        "TIDE: {{Requester}} asks you to {{Ask}}", "A swap or cover needs your approval",
        "{{Change}}\n\n{{Note}}\n\nApprove or decline it on the rotation page{{RotationLink}}.",
        [
            Requester, Partner,
            new("Ask", "\"swap brief days\" or \"cover their brief days\"."),
            Change,
            new("Note", "The requester's note, quoted. Blank when they wrote none.", Html: true),
            RotationLink,
        ]);

    public static readonly EmailTemplate RotationApproved = new(
        "rotation-approved", "Swap or cover approved",
        "Sent to the requester when a swap or cover is approved. The rotation's managers get a copy headed \"for your information\".",
        "TIDE: brief rotation changed: {{ChangeShort}}", "Approved: brief rotation change",
        "{{Change}}\n\n{{Partner}} approved it{{DecisionNote}}. The rotation is updated{{RotationLink}}.",
        [
            Requester, Partner, Change, ChangeShort,
            new("DecisionNote", "\": “the note”\" when the approver wrote one, else blank."),
            RotationLink,
        ]);

    public static readonly EmailTemplate RotationRecorded = new(
        "rotation-recorded", "Past cover recorded",
        "Sent to the two analysts when a past day's brief is recorded as presented by someone other than the rota's analyst. The rotation's managers get a copy headed \"for your information\".",
        "TIDE: brief rotation changed: {{ChangeShort}}", "Recorded: brief rotation change",
        "{{Change}}\n\n{{RecordedBy}} recorded it after the fact from the day's brief. The rotation is updated{{RotationLink}}.",
        [Requester, Partner, Change, ChangeShort, new("RecordedBy", "Who recorded it."), RotationLink]);

    public static readonly EmailTemplate RotationDeclined = new(
        "rotation-declined", "Swap or cover declined",
        "Sent to the requester when the analyst asked declines.",
        "TIDE: {{Partner}} declined your brief rotation request", "Declined: brief rotation change",
        "{{Change}}{{DecisionNote}}",
        [Requester, Partner, Change, new("DecisionNote", "\" “the note”\" when they wrote one, else blank.")]);

    public static readonly IReadOnlyList<EmailTemplate> All =
        [DueSoon, Overdue, Digest, PersonalDigest, OutreachFollowUp, Handoff, ExternalReminder, BriefNotReady, Presenting, RotationRequest, RotationApproved,
            RotationRecorded, RotationDeclined];

    public static EmailTemplate? ById(string? id) => All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The template and part a settings key holds, or null when it isn't one.</summary>
    public static (EmailTemplate Template, EmailPart Part)? Parse(string? key)
    {
        if (key is null || !key.StartsWith(KeyPrefix, StringComparison.OrdinalIgnoreCase)) return null;
        var rest = key[KeyPrefix.Length..].Split(':');
        return rest.Length == 2 && ById(rest[0]) is { } t && Enum.TryParse<EmailPart>(rest[1], ignoreCase: true, out var part) && Enum.IsDefined(part)
            ? (t, part) : null;
    }

    public static bool IsKey(string? key) => Parse(key) is not null;

    [GeneratedRegex(@"\{\{\s*([A-Za-z][A-Za-z0-9]*)\s*\}\}", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    internal static partial Regex TokenRegex();

    /// <summary>A subject or heading as it's stored: one line, trimmed.</summary>
    public static string Normalize(EmailPart part, string? text) =>
        part == EmailPart.Body ? (text ?? "").Replace("\r\n", "\n").Trim() : SingleLine(text ?? "");

    /// <summary>Why <paramref name="text"/> can't be this template's part, or null when it can.</summary>
    public static string? Problem(EmailTemplate t, EmailPart part, string? text)
    {
        var value = Normalize(part, text);
        var what = part.ToString().ToLowerInvariant();
        if (value.Length == 0) return $"Enter a {what}.";
        var max = part == EmailPart.Body ? MaxBody : MaxLine;
        if (value.Length > max) return $"The {what} is {value.Length:N0} characters; it can be {max:N0} at most.";
        foreach (Match m in TokenRegex().Matches(value))
        {
            var name = m.Groups[1].Value;
            if (t.Token(name) is not { } token) return $"{{{{{name}}}}} isn't something this email can fill in. It can use {string.Join(", ", t.Tokens.Select(k => $"{{{{{k.Name}}}}}"))}.";
            if (token.Html && part != EmailPart.Body) return $"{{{{{token.Name}}}}} can only go in the body.";
        }
        if (part == EmailPart.Body)
        {
            var named = TokenRegex().Matches(value).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (t.Tokens.FirstOrDefault(k => k.Required && !named.Contains(k.Name)) is { } missing)
                return $"The body needs {{{{{missing.Name}}}}}: it's what this email is for.";
        }
        return null;
    }

    /// <summary>Why a saved row can't be used, or null when it can (the bundle import's check).</summary>
    public static string? Problem(string key, string? value) =>
        Parse(key) is { } p ? Problem(p.Template, p.Part, value) : "not an email template";
}

/// <summary>A template filled in: the subject, the heading and the body's HTML (the shell goes round it), and its footer.</summary>
public sealed record RenderedTemplate(string Subject, string Heading, string BodyHtml, string Footer = EmailTemplateCatalog.DefaultFooter);

/// <summary>
/// Fills a template in. Plain values are HTML-encoded in the body (and left as text in the subject and heading, which
/// the shell encodes); HTML values are TIDE's own markup and go in as they are. The body's Markdown is rendered with raw
/// HTML escaped (<see cref="MarkdownService"/>), so a template can format text but can't add markup of its own. A name
/// the template doesn't have, or no value given, fills in as nothing.
/// </summary>
public static partial class EmailTemplateRenderer
{
    private static readonly MarkdownService Md = new();

    public static RenderedTemplate Render(EmailTemplate t, string subject, string heading, string body, IReadOnlyDictionary<string, string> values)
    {
        string Value(string name, out EmailToken? token)
        {
            token = t.Token(name);
            return token is not null && values.TryGetValue(token.Name, out var v) ? v : "";
        }

        string Line(string text) => SingleLine(EmailTemplateCatalog.TokenRegex().Replace(text, m =>
            Value(m.Groups[1].Value, out var token) is var v && token is { Html: false } ? v : ""));

        // An HTML value alone in its paragraph (a table) replaces the paragraph, so a table isn't put inside a <p>.
        var html = BlockToken().Replace(Md.ToHtml(body), m =>
            t.Token(m.Groups[1].Value) is { Html: true } ? Value(m.Groups[1].Value, out _) : m.Value);
        html = EmailTemplateCatalog.TokenRegex().Replace(Style(html), m =>
            Value(m.Groups[1].Value, out var token) is var v && token is { Html: true } ? v : Esc(v));
        return new RenderedTemplate(Line(subject), Line(heading), html, Line(t.Footer));
    }

    [GeneratedRegex(@"<p>\s*\{\{\s*([A-Za-z][A-Za-z0-9]*)\s*\}\}\s*</p>\n?", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex BlockToken();

    // The few tags Markdown writes, styled inline for mail clients as the reminders' own markup is.
    private static string Style(string html) => html
        .Replace("<p>", "<p style=\"margin:0 0 12px;\">", StringComparison.Ordinal)
        .Replace("<ul>", "<ul style=\"margin:0 0 12px;padding-left:22px;\">", StringComparison.Ordinal)
        .Replace("<ol>", "<ol style=\"margin:0 0 12px;padding-left:22px;\">", StringComparison.Ordinal)
        .Replace("<li>", "<li style=\"margin:0 0 4px;\">", StringComparison.Ordinal)
        .Replace("<h1>", "<h2 style=\"font-size:17px;margin:0 0 10px;\">", StringComparison.Ordinal).Replace("</h1>", "</h2>", StringComparison.Ordinal)
        .Replace("<h2>", "<h2 style=\"font-size:16px;margin:0 0 10px;\">", StringComparison.Ordinal)
        .Replace("<h3>", "<h3 style=\"font-size:15px;margin:0 0 8px;\">", StringComparison.Ordinal)
        .Replace("<code>", $"<code style=\"font-family:Consolas,'Courier New',monospace;font-size:13px;background:{Palette.Surface2};padding:1px 4px;\">", StringComparison.Ordinal)
        .Replace("<blockquote>", $"<blockquote style=\"margin:0 0 12px;padding-left:12px;border-left:3px solid {Palette.Border};color:{Palette.Muted};\">", StringComparison.Ordinal)
        .Replace(" target=\"_blank\" rel=\"noopener noreferrer\"", "", StringComparison.Ordinal)
        .Replace("<a href", $"<a style=\"color:{Palette.Link};\" href", StringComparison.Ordinal);
}

/// <summary>
/// The saved templates, kept in memory: read at start-up and again whenever one is saved, reset or imported. A row that
/// no longer passes its check is ignored, so the default holds. Singleton; <see cref="Defaults"/> is one with nothing
/// saved, for a service built without one (tests).
/// </summary>
public sealed class EmailTemplateStore
{
    private IReadOnlyDictionary<string, string> _values = new Dictionary<string, string>();

    public static EmailTemplateStore Defaults { get; } = new();

    /// <summary>The part as it's used: the saved text, or the default.</summary>
    public string Text(EmailTemplate t, EmailPart part) =>
        _values.TryGetValue(EmailTemplateCatalog.Key(t.Id, part), out var v) ? v : t.Default(part);

    public bool IsCustomized(EmailTemplate t) => Enum.GetValues<EmailPart>().Any(p => _values.ContainsKey(EmailTemplateCatalog.Key(t.Id, p)));

    /// <summary>The template as it's used, filled in with <paramref name="values"/>.</summary>
    public RenderedTemplate Render(EmailTemplate t, IReadOnlyDictionary<string, string> values) =>
        EmailTemplateRenderer.Render(t, Text(t, EmailPart.Subject), Text(t, EmailPart.Heading), Text(t, EmailPart.Body), values);

    public async Task RefreshAsync(IAppDbContextFactory dbs, CancellationToken ct = default)
    {
        await using var db = await dbs.CreateAsync(ct);
        var rows = await db.Settings.AsNoTracking().Where(s => s.Key.StartsWith(EmailTemplateCatalog.KeyPrefix)).ToListAsync(ct);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
            if (EmailTemplateCatalog.Parse(row.Key) is { } p && EmailTemplateCatalog.Problem(p.Template, p.Part, row.Value) is null)
                values[EmailTemplateCatalog.Key(p.Template.Id, p.Part)] = EmailTemplateCatalog.Normalize(p.Part, row.Value);
        _values = values;
    }
}
