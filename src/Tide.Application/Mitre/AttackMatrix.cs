using Tide.Domain.Enums;

namespace Tide.Application.Mitre;

/// <summary>Display facts about the ATT&amp;CK tactics: matrix order, official ids, short names and descriptions.</summary>
public static class AttackMatrix
{
    private static readonly MitreTactic[] MatrixOrder =
    [
        MitreTactic.Reconnaissance, MitreTactic.ResourceDevelopment, MitreTactic.InitialAccess, MitreTactic.Execution,
        MitreTactic.Persistence, MitreTactic.PrivilegeEscalation, MitreTactic.Stealth, MitreTactic.DefenseImpairment,
        MitreTactic.CredentialAccess, MitreTactic.Discovery, MitreTactic.LateralMovement, MitreTactic.Collection,
        MitreTactic.CommandAndControl, MitreTactic.Exfiltration, MitreTactic.Impact,
    ];

    /// <summary>The tactics in ATT&amp;CK matrix order (Unspecified left out).</summary>
    public static IReadOnlyList<MitreTactic> Tactics => MatrixOrder;

    /// <summary>A tactic's column in the matrix (Unspecified last).</summary>
    public static int TacticRank(MitreTactic t) => Array.IndexOf(MatrixOrder, t) is var i and >= 0 ? i : MatrixOrder.Length;

    /// <summary>The official tactic id (TA00xx).</summary>
    public static string TacticId(MitreTactic t) => t switch
    {
        MitreTactic.Reconnaissance => "TA0043",
        MitreTactic.ResourceDevelopment => "TA0042",
        MitreTactic.InitialAccess => "TA0001",
        MitreTactic.Execution => "TA0002",
        MitreTactic.Persistence => "TA0003",
        MitreTactic.PrivilegeEscalation => "TA0004",
        MitreTactic.Stealth => "TA0005",
        MitreTactic.DefenseImpairment => "TA0112",
        MitreTactic.CredentialAccess => "TA0006",
        MitreTactic.Discovery => "TA0007",
        MitreTactic.LateralMovement => "TA0008",
        MitreTactic.Collection => "TA0009",
        MitreTactic.CommandAndControl => "TA0011",
        MitreTactic.Exfiltration => "TA0010",
        MitreTactic.Impact => "TA0040",
        _ => "",
    };

    /// <summary>The tactic's name as ATT&amp;CK writes it ("Command and Control").</summary>
    public static string TacticName(MitreTactic t) => t switch
    {
        MitreTactic.ResourceDevelopment => "Resource Development",
        MitreTactic.InitialAccess => "Initial Access",
        MitreTactic.PrivilegeEscalation => "Privilege Escalation",
        MitreTactic.DefenseImpairment => "Defense Impairment",
        MitreTactic.CredentialAccess => "Credential Access",
        MitreTactic.LateralMovement => "Lateral Movement",
        MitreTactic.CommandAndControl => "Command and Control",
        MitreTactic.Unspecified => "Unspecified",
        _ => t.ToString(),
    };

    /// <summary>One line on what the tactic is.</summary>
    public static string TacticDescription(MitreTactic t) => t switch
    {
        MitreTactic.Reconnaissance => "Gathering information to plan the operation.",
        MitreTactic.ResourceDevelopment => "Establishing resources to support operations.",
        MitreTactic.InitialAccess => "Getting into the network.",
        MitreTactic.Execution => "Running malicious code.",
        MitreTactic.Persistence => "Maintaining a foothold across restarts.",
        MitreTactic.PrivilegeEscalation => "Gaining higher-level permissions.",
        MitreTactic.Stealth => "Avoiding detection.",
        MitreTactic.DefenseImpairment => "Disabling or degrading defenses.",
        MitreTactic.CredentialAccess => "Stealing account names and passwords.",
        MitreTactic.Discovery => "Learning the environment.",
        MitreTactic.LateralMovement => "Moving through the environment.",
        MitreTactic.Collection => "Gathering data of interest.",
        MitreTactic.CommandAndControl => "Communicating with compromised systems.",
        MitreTactic.Exfiltration => "Stealing data out of the network.",
        MitreTactic.Impact => "Manipulating, interrupting or destroying systems and data.",
        _ => "Tactic not specified.",
    };

    /// <summary>The technique's page on attack.mitre.org ("T1566.001" is /techniques/T1566/001/).</summary>
    public static string TechniqueUrl(string techniqueId) =>
        "https://attack.mitre.org/techniques/" + techniqueId.Trim().ToUpperInvariant().Replace('.', '/') + "/";
}
