using Tide.Domain.Entities;

namespace Tide.Application.Actions;

/// <summary>What a <see cref="HolderLabel"/> names.</summary>
public enum HolderKind
{
    /// <summary>Someone in TIDE holds it.</summary>
    Person = 0,

    /// <summary>Nobody holds it, on an internal team: the surface's own word ("Unassigned", "nobody yet").</summary>
    Nobody = 1,

    /// <summary>An external team's, nobody in TIDE holding it: its ticket names it.</summary>
    Ticket = 2,

    /// <summary>An external team's with no ticket yet.</summary>
    NoTicket = 3,
}

/// <summary>
/// Who has an action, in words: the one rule every list, card, slide and email that names a holder follows. Whoever in
/// TIDE holds it is named first, as the action page does. An external team has no members, so nobody here holding its
/// action is expected: its ticket names it instead ("INC-4411"), with <see cref="Url"/> the ticket's link when that is an
/// http or https address; with no ticket, "With Hosting Provider · no ticket yet", so the gap shows. On an internal team
/// nobody holding it reads as the caller says. Only the words change: counting and filtering still go by the assignee.
/// </summary>
public sealed record HolderLabel(string Text, HolderKind Kind, string? Url = null)
{
    /// <summary>A ticket with a link and no reference: named by this.</summary>
    public const string UnnamedTicket = "Ticket";

    /// <summary>The words where the team is named beside them: "no ticket yet" rather than repeating the team.</summary>
    public string Short => Kind == HolderKind.NoTicket ? "no ticket yet" : Text;

    /// <summary>An external team's that nobody in TIDE holds: named by its ticket, or by having none yet.</summary>
    public bool Outside => Kind is HolderKind.Ticket or HolderKind.NoTicket;

    /// <param name="nobody">The words for an internal team's action nobody holds.</param>
    public static HolderLabel Of(string? assigneeId, string? assigneeName, bool external, string teamName, string? ticketRef,
        string? ticketUrl, string nobody)
    {
        if (assigneeId is not null) return new(assigneeName ?? assigneeId, HolderKind.Person);
        if (!external) return new(nobody, HolderKind.Nobody);
        // The link as the ticket field keeps it (http or https only); anything else isn't linked.
        var url = Articles.ArticleService.SafeUrl(ticketUrl);
        return string.IsNullOrWhiteSpace(ticketRef)
            ? url is null ? new($"With {teamName} · no ticket yet", HolderKind.NoTicket) : new(UnnamedTicket, HolderKind.Ticket, url)
            : new(ticketRef.Trim(), HolderKind.Ticket, url);
    }

    /// <summary>An action as its page has it, its team loaded (without it, it's taken as internal). Held when it has an
    /// assignee's id, or only a name (an email template's sample actions).</summary>
    public static HolderLabel Of(ActionItem a, string nobody) =>
        Of(a.AssigneeId ?? a.AssigneeName, a.AssigneeName, a.Team?.IsExternal == true, a.Team?.Name ?? "", a.TicketRef, a.TicketUrl, nobody);

    public override string ToString() => Text;
}
