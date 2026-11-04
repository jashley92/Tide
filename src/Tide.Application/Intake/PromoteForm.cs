using Tide.Domain.Enums;

namespace Tide.Application.Intake;

/// <summary>
/// What Triage's promote form asks before the draft is made (<see cref="IntakeService.PromoteAsync(Guid, PromoteForm, CancellationToken)"/>).
/// A part left empty takes the item's own: its title, and the source's TLP. An empty form is a plain promote: an
/// informational draft with the item's title and the source's TLP.
/// </summary>
public sealed class PromoteForm
{
    /// <summary>The article's title; null for the item's.</summary>
    public string? Title { get; set; }

    public Classification Classification { get; set; } = Classification.Informational;

    /// <summary>Kept for a threat only, as the editor keeps it.</summary>
    public Priority? Priority { get; set; }

    /// <summary>The article's TLP; null for the source's default.</summary>
    public TlpLevel? Tlp { get; set; }
}
