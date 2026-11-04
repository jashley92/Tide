using Tide.Domain.Common;

namespace Tide.Domain.Entities;

/// <summary>Where a detection rule was read from. Stored; append, never renumber.</summary>
public enum DetectionSource
{
    /// <summary>A Microsoft Sentinel analytics-rules export (ARM template JSON).</summary>
    Sentinel = 0,

    /// <summary>Sigma rules (YAML), one file each or a zip of them.</summary>
    Sigma = 1,

    /// <summary>A CSV of name, techniques, platform and enabled.</summary>
    Csv = 2,

    /// <summary>Pushed by another tool through the API (EP-75).</summary>
    Api = 3,
}

/// <summary>
/// One of the SOC's detection rules, as last imported (EP-79): its name, the ATT&amp;CK techniques it's tagged with, and
/// whether it's enabled. TIDE only references rules, to say which briefed techniques have one; it never edits or runs
/// them. Each import of a source refreshes its rules; a rule missing from the latest import keeps its last state, marked
/// by <see cref="LastSeenAtUtc"/>. Internal: rule names never reach an edition.
/// </summary>
public class DetectionRule : Entity
{
    public const int MaxName = 300;
    public const int MaxExternalId = 300;
    public const int MaxTechniques = 1000;
    public const int MaxPlatform = 100;

    public DetectionSource Source { get; set; }

    /// <summary>The rule's own id in its source (a Sentinel rule's name, a Sigma id, a CSV id), or its name when it has none.</summary>
    public string ExternalId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>The ATT&amp;CK technique ids it's tagged with ("T1078", "T1566.004"), comma-separated, upper case.</summary>
    public string Techniques { get; set; } = string.Empty;

    public string? Platform { get; set; }
    public bool Enabled { get; set; } = true;

    /// <summary>When an import of its source last contained it.</summary>
    public DateTimeOffset LastSeenAtUtc { get; set; }

    public IEnumerable<string> TechniqueList => Techniques.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>A briefed technique with no enabled rule, raised as an action for detection engineering (EP-79's Coverage).</summary>
public class DetectionGap
{
    public string TechniqueId { get; set; } = string.Empty;
    public Guid ActionId { get; set; }
    public DateTimeOffset RaisedAtUtc { get; set; }
}
