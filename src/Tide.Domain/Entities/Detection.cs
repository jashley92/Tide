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

    /// <summary>
    /// What separates an API rule's caller from its id in <see cref="ExternalId"/>: "&lt;caller&gt;&lt;mark&gt;&lt;its id&gt;". A
    /// control character, so no id read before callers were kept (EP-79's first pushes) is taken for one.
    /// </summary>
    public const char CallerMark = '\u001F';

    private DetectionSource _source;
    private string _externalId = string.Empty;

    public DetectionSource Source
    {
        get => _source;
        set { _source = value; Caller = CallerOf(value, _externalId); }
    }

    /// <summary>The rule's own id in its source (a Sentinel rule's name, a Sigma id, a CSV id), or its name when it has none.</summary>
    public string ExternalId
    {
        get => _externalId;
        set { _externalId = value; Caller = CallerOf(_source, value); }
    }

    /// <summary>
    /// The API caller whose push holds the rule, as <see cref="CallerOf"/> reads it from <see cref="Source"/> and
    /// <see cref="ExternalId"/>; null for an imported rule and an API rule pushed before callers were kept. Stored, so a
    /// push or a removal reads only its caller's rules; it follows the two it's read from whenever either is set.
    /// </summary>
    public string? Caller { get; private set; }

    /// <summary>The caller in an API rule's id (the part before <see cref="CallerMark"/>); null for any other rule, or with none.</summary>
    public static string? CallerOf(DetectionSource source, string externalId) =>
        source == DetectionSource.Api && externalId.IndexOf(CallerMark) is var i and > 0 ? externalId[..i] : null;

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
