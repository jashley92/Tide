using Tide.Domain.Common;
using Tide.Domain.Enums;

namespace Tide.Domain.Entities;

/// <summary>
/// The lead's assessment of one section of a period's report ("headlines" of "2026-10"): what the numbers mean, in their
/// words. One per section per period, kept so next quarter's report can look back at this one's judgement. It has a TLP of
/// its own, since a judgement may name the team's own gaps. Clearing the text deletes the row; the audit trail keeps it.
/// </summary>
public class ReportAssessment : Entity
{
    public const int MaxText = 4000;

    /// <summary>The longest period key ("2026-W44").</summary>
    public const int MaxPeriodKey = 16;

    public const int MaxSection = 32;

    /// <summary>The period, by its key: "2026-10" (a month), "2026-Q4" (a quarter), "2026-W44" (an ISO week).</summary>
    public string PeriodKey { get; set; } = string.Empty;

    /// <summary>The section it assesses: "headlines", "outcomes", "metrics", "topics" or "teams".</summary>
    public string Section { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;

    public TlpLevel Tlp { get; set; } = TlpLevel.Amber;

    /// <summary>Who saved it last, and when: the "Edited by Pat, Nov 3" line under it.</summary>
    public string UpdatedById { get; set; } = string.Empty;
    public string UpdatedByName { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAtUtc { get; set; }

    /// <summary>Moves on every save, so a save from a page that read an older assessment is refused.</summary>
    public Guid EditVersion { get; set; } = Guid.NewGuid();
}
