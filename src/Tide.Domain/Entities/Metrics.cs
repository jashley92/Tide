using Tide.Domain.Common;
using Tide.Domain.Enums;

namespace Tide.Domain.Entities;

/// <summary>
/// A group of opening metrics ("Alert queue"): one block of tiles on the metrics slide. Admin-defined, with a stable
/// <see cref="Key"/>; retired, never deleted, once a metric in it has values.
/// </summary>
public class MetricGroup : AuditableEntity
{
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public bool IsRetired { get; set; }

    public List<MetricDefinition> Metrics { get; set; } = [];
}

/// <summary>
/// One number read out at the start of the brief ("Older than 24 hours"). Nothing about it is built in: what it is, which
/// way is good and when it's worth a warning are all set by an administrator. Renaming keeps its history (values hang
/// off the id, and the <see cref="Key"/> never changes); retiring takes it off the slide and keeps the history.
/// </summary>
public class MetricDefinition : AuditableEntity
{
    public Guid GroupId { get; set; }
    public MetricGroup? Group { get; set; }

    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public MetricFormat Format { get; set; } = MetricFormat.Count;
    public int SortOrder { get; set; }
    public bool IsRetired { get; set; }

    public MetricDirection Direction { get; set; } = MetricDirection.Neutral;

    /// <summary>Optional thresholds that colour the value, applied in <see cref="Direction"/>: for LowerIsBetter (and
    /// Neutral) a value at or above one trips it; for HigherIsBetter, at or below.</summary>
    public decimal? WarnThreshold { get; set; }
    public decimal? CriticalThreshold { get; set; }

    /// <summary>Whether a day without a value is called out on the brief page (a non-blocking check).</summary>
    public bool FlagMissing { get; set; } = true;

    public MetricSource Source { get; set; } = MetricSource.Manual;
}

/// <summary>A metric's value on one brief date (the organization's local business day). One per metric per date.</summary>
public class MetricValue : AuditableEntity
{
    public Guid DefinitionId { get; set; }
    public DateOnly BriefDate { get; set; }
    public decimal Value { get; set; }
    public string? Note { get; set; }

    /// <summary>Who typed it (user id and name), or for an imported value "import" and "Import: file name".</summary>
    public string EnteredById { get; set; } = string.Empty;
    public string EnteredByName { get; set; } = string.Empty;

    /// <summary>Set when an analyst replaced an imported value with a different one. Later imports leave it alone.</summary>
    public bool IsOverride { get; set; }

    /// <summary>Typed, or read from a file. An import only ever replaces an imported value.</summary>
    public MetricSource Source { get; set; } = MetricSource.Manual;

    /// <summary>The <see cref="MetricImport"/> that last set an imported value; null for a typed one.</summary>
    public Guid? ImportId { get; set; }
}

/// <summary>
/// One metrics CSV read: where it came from, what it held, and what happened to each row. Kept as the provenance of the
/// values it set (<see cref="MetricValue.ImportId"/>) and shown on the imports page.
/// </summary>
public class MetricImport : AuditableEntity
{
    public string FileName { get; set; } = string.Empty;
    public MetricImportOrigin Origin { get; set; }
    public DateTimeOffset ReadAtUtc { get; set; }

    /// <summary>Who uploaded it; "Drop folder" for a picked-up file.</summary>
    public string ImportedByName { get; set; } = string.Empty;

    /// <summary>Data rows in the file (the header not counted).</summary>
    public int Rows { get; set; }

    /// <summary>Rows that set a new value or changed one.</summary>
    public int Applied { get; set; }

    /// <summary>Rows whose value was already there.</summary>
    public int Unchanged { get; set; }

    /// <summary>Rows not applied: a sent brief's day, an analyst's value, a retired or unknown metric, a bad cell.</summary>
    public int Skipped { get; set; }

    /// <summary>Whether the whole file was refused (no usable header, too large, not text); nothing was applied.</summary>
    public bool Rejected { get; set; }

    /// <summary>Why rows were skipped, one per line ("Row 4: ..."), the first 50.</summary>
    public string? Problems { get; set; }
}
