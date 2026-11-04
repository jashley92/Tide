namespace Tide.Domain.Enums;

/// <summary>
/// The MITRE ATT&amp;CK Enterprise tactics (the "why" of a technique). Used to group an article's tagged techniques.
/// <see cref="Unspecified"/> is the default when a technique is tagged without a tactic.
/// </summary>
/// <remarks>
/// Aligned to ATT&amp;CK v19.1. Enum <em>values</em> are persisted (stored as <c>int</c>), so members keep their numbers:
/// new tactics are appended, never renumbered. v19 renamed <c>TA0005 "Defense Evasion" → "Stealth"</c> (same id, so the
/// value <c>7</c> is relabelled) and split out the new <c>TA0112 "Defense Impairment"</c>. Display order follows the
/// ATT&amp;CK matrix (<c>AttackMatrix.TacticRank</c>), not the declaration order.
/// </remarks>
public enum MitreTactic
{
    Unspecified = 0,
    Reconnaissance = 1,
    ResourceDevelopment = 2,
    InitialAccess = 3,
    Execution = 4,
    Persistence = 5,
    PrivilegeEscalation = 6,
    Stealth = 7, // TA0005, formerly "Defense Evasion" (renamed in ATT&CK v19).
    CredentialAccess = 8,
    Discovery = 9,
    LateralMovement = 10,
    Collection = 11,
    CommandAndControl = 12,
    Exfiltration = 13,
    Impact = 14,
    DefenseImpairment = 15 // TA0112, added in ATT&CK v19 (matrix position is after Stealth).
}
