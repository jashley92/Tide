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

    /// <summary>Put the new draft at the end of today's brief as well (<see cref="TriagePromoter"/>), starting the brief
    /// if there's none yet. Ignored by <see cref="IntakeService"/>'s own promotes.</summary>
    public bool AddToBrief { get; set; }

    /// <summary>A KEV round-up (<see cref="KevRoundup"/>): up to <see cref="KevRoundup.MaxItems"/> items, each naming a
    /// CVE on CISA's KEV list, promoted as one, the draft with a bullet per KEV-listed CVE and the round-up's recommended
    /// action.</summary>
    public bool AsKevRoundup { get; set; }
}
