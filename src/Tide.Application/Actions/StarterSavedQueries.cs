using Tide.Domain.Entities;

namespace Tide.Application.Actions;

/// <summary>One saved query of the starter set, as it's added.</summary>
public sealed record StarterSavedQuery(string Name, string Dialect, string Description, string Text);

/// <summary>
/// The sweeps a new install's Saved queries page starts with: an address, a domain, a SHA-256 hash and a URL sweep in
/// each language TIDE writes, and one across every indicator where the language puts them in one query. Each is built on
/// its dialect's own generation template (<see cref="QueryDialects"/>), so the tables and fields are those TIDE already
/// generates, and takes one kind's values (<c>{{values:ipv4}}</c>) so the action's other kinds are named as left out.
/// Added once, to an empty library (DatabaseInitializer); a team that deletes or retires them doesn't get them back.
/// </summary>
public static class StarterSavedQueries
{
    /// <summary>Who the page says saved them.</summary>
    public const string SavedBy = "TIDE";

    public static readonly IReadOnlyList<StarterSavedQuery> All =
    [
        // KQL: Defender's advanced hunting tables, as the dialect's templates use them.
        new("IP sweep (KQL)", "kql", "IPv4 addresses in Defender network events, as either end of a connection.",
            "DeviceNetworkEvents\n| where Timestamp > ago({{days}}d)\n| where RemoteIP in ({{values:ipv4}}) or LocalIP in ({{values:ipv4}})\n| project Timestamp, DeviceName, InitiatingProcessFileName, LocalIP, RemoteIP, RemotePort"),
        new("Domain sweep (KQL)", "kql", "Domains in the addresses Defender saw devices connect to.",
            "DeviceNetworkEvents\n| where Timestamp > ago({{days}}d)\n| where RemoteUrl has_any ({{values:domain}})\n| project Timestamp, DeviceName, InitiatingProcessFileName, RemoteUrl, RemoteIP"),
        new("File hash sweep (KQL)", "kql", "SHA-256 hashes of files Defender saw created or changed.",
            "DeviceFileEvents\n| where Timestamp > ago({{days}}d)\n| where SHA256 in~ ({{values:sha256}})\n| project Timestamp, DeviceName, FileName, FolderPath, SHA256"),
        new("URL sweep (KQL)", "kql", "URLs clicked in email, Teams and Office (Defender for Office 365).",
            "UrlClickEvents\n| where Timestamp > ago({{days}}d)\n| where Url in~ ({{values:url}})\n| project Timestamp, AccountUpn, Url, ActionType"),
        new("Every indicator sweep (KQL)", "kql", "Every indicator at once, in network and file events: addresses, domains, hashes and file names.",
            "let iocs = dynamic([{{values}}]);\nunion\n    (DeviceNetworkEvents | where Timestamp > ago({{days}}d) | where RemoteIP in (iocs) or RemoteUrl has_any (iocs)),\n    (DeviceFileEvents | where Timestamp > ago({{days}}d) | where SHA256 in~ (iocs) or SHA1 in~ (iocs) or MD5 in~ (iocs) or FileName in~ (iocs))\n| project Timestamp, DeviceName, ActionType, RemoteIP, RemoteUrl, FileName, SHA256"),

        // XQL: the xdr_data dataset and fields the dialect's templates use; the config stage sets the window.
        new("IP sweep (XQL)", "xql", "IPv4 addresses in endpoint network events, as either end of a connection.",
            "config timeframe = {{days}}d\n| dataset = xdr_data\n| filter action_remote_ip in ({{values:ipv4}}) or action_local_ip in ({{values:ipv4}})\n| fields _time, agent_hostname, actor_process_image_name, action_local_ip, action_remote_ip, action_remote_port"),
        new("Domain sweep (XQL)", "xql", "Domains looked up in DNS or connected to by name.",
            "config timeframe = {{days}}d\n| dataset = xdr_data\n| filter dns_query_name in ({{values:domain}}) or action_external_hostname in ({{values:domain}})\n| fields _time, agent_hostname, actor_process_image_name, dns_query_name, action_external_hostname"),
        new("File hash sweep (XQL)", "xql", "SHA-256 hashes of files written and processes run.",
            "config timeframe = {{days}}d\n| dataset = xdr_data\n| filter action_file_sha256 in ({{values:sha256}}) or actor_process_image_sha256 in ({{values:sha256}})\n| fields _time, agent_hostname, action_file_name, action_file_path, action_file_sha256"),
        new("URL sweep (XQL)", "xql", "URLs endpoints requested.",
            "config timeframe = {{days}}d\n| dataset = xdr_data\n| filter action_url in ({{values:url}})\n| fields _time, agent_hostname, actor_process_image_name, action_url"),
        new("Every indicator sweep (XQL)", "xql", "Every indicator at once: addresses, domains, URLs and SHA-256 hashes.",
            "config timeframe = {{days}}d\n| dataset = xdr_data\n| filter action_remote_ip in ({{values}}) or dns_query_name in ({{values}}) or action_external_hostname in ({{values}}) or action_url in ({{values}}) or action_file_sha256 in ({{values}}) or actor_process_image_sha256 in ({{values}})\n| fields _time, agent_hostname, actor_process_image_name, action_remote_ip, dns_query_name, action_url, action_file_name, action_file_sha256"),

        // Falcon: the event types the dialect's templates use. Its queries run over the range picked in the console, so
        // they carry no window; Falcon telemetry has no URL field to sweep, and its filters take one field each.
        new("IP sweep (FQL)", "fql", "IPv4 addresses Falcon saw hosts connect to. Set the time range in the console.",
            "#event_simpleName=NetworkConnectIP4\n| in(field=RemoteAddressIP4, values=[{{values:ipv4}}])\n| table([@timestamp, ComputerName, RemoteAddressIP4, RemotePort, ContextBaseFileName])"),
        new("Domain sweep (FQL)", "fql", "Domains hosts looked up in DNS. Set the time range in the console.",
            "#event_simpleName=DnsRequest\n| in(field=DomainName, values=[{{values:domain}}], ignoreCase=true)\n| table([@timestamp, ComputerName, DomainName, ContextBaseFileName])"),
        new("File hash sweep (FQL)", "fql", "SHA-256 hashes of processes that ran. Set the time range in the console.",
            "#event_simpleName=ProcessRollup2\n| in(field=SHA256HashData, values=[{{values:sha256}}], ignoreCase=true)\n| table([@timestamp, ComputerName, FileName, SHA256HashData])"),
    ];

    /// <summary>A starter as a row, saved by TIDE.</summary>
    public static SavedQuery ToEntity(StarterSavedQuery s) => new()
    {
        Name = s.Name, Dialect = s.Dialect, Description = s.Description, Text = s.Text, CreatedByName = SavedBy,
    };
}
