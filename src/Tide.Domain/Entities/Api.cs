using Tide.Domain.Common;
using Tide.Domain.Enums;

namespace Tide.Domain.Entities;

/// <summary>
/// A token another system (a SOAR playbook, Power BI) calls TIDE's API with (EP-75). Only a hash of it is kept, and the
/// last four characters to tell tokens apart; the token itself is shown once, when it's issued. It can do only what its
/// scopes allow, sees nothing above <see cref="MaxTlp"/>, and stops working when it expires or is revoked.
/// </summary>
public class ApiToken : AuditableEntity
{
    public const int MaxName = 100;

    /// <summary>Who calls with it ("XSOAR playbooks"): shown in the audit log and on what it changes.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The SHA-256 of the token, in hex.</summary>
    public string Hash { get; set; } = string.Empty;

    /// <summary>The token's last four characters.</summary>
    public string Hint { get; set; } = string.Empty;

    /// <summary>The scopes it holds, space-separated ("actions:read actions:write").</summary>
    public string Scopes { get; set; } = string.Empty;

    /// <summary>The highest TLP it reads.</summary>
    public TlpLevel MaxTlp { get; set; } = TlpLevel.Green;

    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset? RevokedAtUtc { get; set; }
    public DateTimeOffset? LastUsedAtUtc { get; set; }

    /// <summary>Calls made with it.</summary>
    public long Calls { get; set; }
}

/// <summary>
/// An address TIDE posts to when something happens (an action raised or overdue, a brief sent), signed with HMAC-SHA256
/// so the receiver can tell it's from TIDE. The secret is kept encrypted (data protection) and shown once.
/// </summary>
public class Webhook : AuditableEntity
{
    public string Url { get; set; } = string.Empty;

    /// <summary>The events it's sent, space-separated ("action.raised brief.sent").</summary>
    public string Events { get; set; } = string.Empty;

    /// <summary>The signing secret, protected.</summary>
    public string Secret { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    /// <summary>Everything up to this moment has been sent (or tried): the next delivery starts after it.</summary>
    public DateTimeOffset SentUpToUtc { get; set; }

    /// <summary>The last day whose newly overdue actions were sent.</summary>
    public DateOnly? OverdueSentFor { get; set; }

    public DateTimeOffset? LastAtUtc { get; set; }
    public int? LastStatus { get; set; }
    public string? LastError { get; set; }
}
