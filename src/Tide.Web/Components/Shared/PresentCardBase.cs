using Microsoft.AspNetCore.Components;

namespace Tide.Web.Components.Shared;

/// <summary>What Present tells a card it opens over a slide (<see cref="PresentCard"/>, and the cards drawn in one), as
/// it tells each of them alike.</summary>
public abstract class PresentCardBase : ComponentBase
{
    /// <summary>A word on whose it is: "Shown to everyone following", "Tester is showing this".</summary>
    [Parameter] public string? Kicker { get; set; }

    /// <summary>Whether this page may close it: not a follower held by the presenter's lock.</summary>
    [Parameter] public bool CanClose { get; set; } = true;

    /// <summary>Takes focus once drawn: this page opened it.</summary>
    [Parameter] public bool Autofocus { get; set; }

    [Parameter] public EventCallback OnClose { get; set; }

    /// <summary>Whether it links to the full page: not for a Visitor, who sees the card only in the meeting.</summary>
    [Parameter] public bool Links { get; set; } = true;
}
