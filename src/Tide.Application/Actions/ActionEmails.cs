using System.Globalization;
using System.Text;
using Tide.Application.Org;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using static Tide.Application.Distribution.EmailComposer;

namespace Tide.Application.Actions;

/// <summary>The parts every email about actions shares: their TLP marking, the day line, and the table of actions
/// (the reminders, the digests, a hand-off notice, and the email templates' preview).</summary>
internal static class ActionEmails
{
    // An action carries the TLP of the article it was raised from (one raised on its own carries none). A reminder or
    // digest is marked with the highest it lists, in its subject and at its top, as a brief's email is, since it may go
    // to a team mailbox wider than TIDE's users.
    internal static TlpLevel? MarkingOf(IEnumerable<ActionItem> actions) => actions.Select(a => a.Article?.Tlp).Max();

    internal static string Marked(string subject, IEnumerable<ActionItem> actions) =>
        MarkingOf(actions) is { } tlp ? $"{subject} [{Tlp.Label(tlp)}]" : subject;

    internal static string MarkingLine(IEnumerable<ActionItem> actions) => MarkingOf(actions) is { } tlp
        ? $"<p style=\"margin:0 0 12px;font-size:13px;color:{Palette.Muted};\">{TlpLabel(tlp)} {Esc(Tlp.Meaning(tlp))}</p>"
        : "";

    internal static string DayText(DateOnly d) => d.ToString("dddd, MMMM d", CultureInfo.InvariantCulture);

    /// <summary>The reminders' table of actions (the email templates' preview uses it too, with sample actions).</summary>
    internal static string ActionTable(IEnumerable<ActionItem> actions, DateOnly today, string? appBaseUrl, WorkingDays? workingDays = null)
    {
        var baseUrl = appBaseUrl?.TrimEnd('/');
        var sb = new StringBuilder("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" style=\"font-size:14px;\">");
        foreach (var a in actions)
        {
            var label = $"A-{a.Number}";
            var number = string.IsNullOrWhiteSpace(baseUrl) ? Esc(label) : $"<a style=\"color:{Palette.Link};\" href=\"{Esc(baseUrl + "/actions/" + a.Number)}\">{Esc(label)}</a>";
            var state = ActionSla.StateOf(a, today, workingDays);
            var due = a.ClosedDate is { } c ? $"closed {c.ToString("MMM d", CultureInfo.InvariantCulture)} ({ActionService.OutcomeName(a.Outcome)})"
                : a.DueDate is { } d ? "due " + d.ToString("ddd MMM d", CultureInfo.InvariantCulture) : "no due date";
            var color = state == SlaState.Overdue || state == SlaState.Missed ? Palette.Threat : state == SlaState.DueSoon ? Palette.Warn : Palette.Muted;
            sb.Append(CultureInfo.InvariantCulture, $"""
                <tr>
                  <td style="padding:6px 8px 6px 0;border-bottom:1px solid {Palette.Border};white-space:nowrap;vertical-align:top;">{number}</td>
                  <td style="padding:6px 8px;border-bottom:1px solid {Palette.Border};vertical-align:top;">{(a.Article is { } from ? TlpLabel(from.Tlp, 10) + " " : "")}{Esc(a.Title)}<div style="font-size:12px;color:{Palette.Muted};">{Esc(ActionService.KindName(a.Kind))} &middot; {a.Priority}{(HolderLabel.Of(a, "") is { Kind: not HolderKind.Nobody } who ? " &middot; " + Esc(who.Text) : "")}</div></td>
                  <td style="padding:6px 0 6px 8px;border-bottom:1px solid {Palette.Border};white-space:nowrap;vertical-align:top;color:{color};font-size:13px;">{Esc(due)}</td>
                </tr>
                """);
        }
        return sb.Append("</table>").ToString();
    }
}
