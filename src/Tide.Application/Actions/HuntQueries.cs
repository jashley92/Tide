using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Tide.Application.Abstractions;
using Tide.Application.Security;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.Application.Actions;

/// <summary>
/// A query language TIDE writes hunts in (ADR 0006). A dialect knows how to write a comment and a list of string
/// values, and has a default template per kind of value; administrators replace templates to match their own datasets
/// and fields, and add them for kinds with no default. A new dialect is one more implementation in
/// <see cref="QueryDialects.All"/>.
/// </summary>
public interface IQueryDialect
{
    /// <summary>Stored on each hunt ("xql").</summary>
    string Key { get; }
    string Name { get; }
    string Comment(string text);

    /// <summary>The values as this language writes a list of string literals (without the brackets the template adds).</summary>
    string List(IEnumerable<string> values);

    /// <summary>The built-in template for each kind of value it covers; <c>{{values}}</c> marks the list.</summary>
    IReadOnlyDictionary<HuntValueKind, string> Defaults { get; }
}

/// <summary>
/// How indicators are split for querying. A SIEM keeps IPv4 and IPv6 addresses, and each hash algorithm, in fields of
/// their own, so each is its own kind with its own template; every other indicator type is one kind.
/// </summary>
public static class HuntValues
{
    public static HuntValueKind KindOf(ArticleIndicator indicator) => indicator.Type switch
    {
        IndicatorType.IpAddress => IPAddress.TryParse(indicator.Value.Trim(), out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6
            ? HuntValueKind.Ipv6 : HuntValueKind.Ipv4,
        IndicatorType.FileHash => HashKind(indicator.Value.Trim()),
        IndicatorType.Domain => HuntValueKind.Domain,
        IndicatorType.Url => HuntValueKind.Url,
        IndicatorType.FileName => HuntValueKind.FileName,
        IndicatorType.EmailAddress => HuntValueKind.EmailAddress,
        IndicatorType.Host => HuntValueKind.Host,
        IndicatorType.Account => HuntValueKind.Account,
        IndicatorType.Process => HuntValueKind.Process,
        IndicatorType.RegistryKey => HuntValueKind.RegistryKey,
        IndicatorType.CommandLine => HuntValueKind.CommandLine,
        _ => HuntValueKind.Other,
    };

    private static HuntValueKind HashKind(string value) => !value.All(Uri.IsHexDigit) ? HuntValueKind.OtherHash : value.Length switch
    {
        32 => HuntValueKind.Md5,
        40 => HuntValueKind.Sha1,
        64 => HuntValueKind.Sha256,
        128 => HuntValueKind.Sha512,
        _ => HuntValueKind.OtherHash,
    };

    public static string Label(HuntValueKind k) => k switch
    {
        HuntValueKind.Ipv4 => "IPv4 addresses",
        HuntValueKind.Ipv6 => "IPv6 addresses",
        HuntValueKind.Domain => "Domains",
        HuntValueKind.Url => "URLs",
        HuntValueKind.Md5 => "MD5 hashes",
        HuntValueKind.Sha1 => "SHA-1 hashes",
        HuntValueKind.Sha256 => "SHA-256 hashes",
        HuntValueKind.Sha512 => "SHA-512 hashes",
        HuntValueKind.OtherHash => "Other hashes",
        HuntValueKind.FileName => "File names",
        HuntValueKind.EmailAddress => "Email addresses",
        HuntValueKind.Host => "Hosts",
        HuntValueKind.Account => "Accounts",
        HuntValueKind.Process => "Processes",
        HuntValueKind.RegistryKey => "Registry keys",
        HuntValueKind.CommandLine => "Command lines",
        _ => "Other indicators",
    };
}

/// <summary>The dialects that ship. Their defaults are a starting point: field and dataset names vary by environment.</summary>
public static class QueryDialects
{
    public const string Placeholder = "{{values}}";

    /// <summary>Optional in a template: how many days back to look (at least 30, more for an older story).</summary>
    public const string DaysPlaceholder = "{{days}}";

    public static readonly IReadOnlyList<IQueryDialect> All = [new Xql(), new Kql(), new Fql()];

    public static IQueryDialect? Find(string? key) => All.FirstOrDefault(d => string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>A value as a double-quoted string literal: backslashes and quotes escaped, line breaks removed.</summary>
    public static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", "") + "\"";

    private sealed class Xql : IQueryDialect
    {
        public string Key => "xql";
        public string Name => "Cortex XSIAM (XQL)";
        public string Comment(string text) => "// " + text;
        public string List(IEnumerable<string> values) => string.Join(", ", values.Select(Quote));

        public IReadOnlyDictionary<HuntValueKind, string> Defaults { get; } = new Dictionary<HuntValueKind, string>
        {
            [HuntValueKind.Ipv4] = "dataset = xdr_data\n| filter action_remote_ip in ({{values}}) or action_local_ip in ({{values}})\n| fields _time, agent_hostname, actor_process_image_name, action_local_ip, action_remote_ip, action_remote_port",
            [HuntValueKind.Ipv6] = "dataset = xdr_data\n| filter action_remote_ipv6 in ({{values}}) or action_local_ipv6 in ({{values}})\n| fields _time, agent_hostname, actor_process_image_name, action_local_ipv6, action_remote_ipv6, action_remote_port",
            [HuntValueKind.Domain] = "dataset = xdr_data\n| filter dns_query_name in ({{values}}) or action_external_hostname in ({{values}})\n| fields _time, agent_hostname, actor_process_image_name, dns_query_name, action_external_hostname",
            [HuntValueKind.Url] = "dataset = xdr_data\n| filter action_url in ({{values}})\n| fields _time, agent_hostname, actor_process_image_name, action_url",
            [HuntValueKind.Md5] = "dataset = xdr_data\n| filter action_file_md5 in ({{values}}) or actor_process_image_md5 in ({{values}})\n| fields _time, agent_hostname, action_file_name, action_file_path, action_file_md5",
            [HuntValueKind.Sha256] = "dataset = xdr_data\n| filter action_file_sha256 in ({{values}}) or actor_process_image_sha256 in ({{values}})\n| fields _time, agent_hostname, action_file_name, action_file_path, action_file_sha256",
            [HuntValueKind.FileName] = "dataset = xdr_data\n| filter action_file_name in ({{values}}) or actor_process_image_name in ({{values}})\n| fields _time, agent_hostname, action_file_name, action_file_path",
            [HuntValueKind.EmailAddress] = "dataset = xdr_data\n| filter action_email_sender in ({{values}}) or action_email_recipient in ({{values}})\n| fields _time, action_email_sender, action_email_recipient, action_email_subject",
            [HuntValueKind.Host] = "dataset = xdr_data\n| filter agent_hostname in ({{values}})\n| fields _time, agent_hostname, event_type, actor_process_image_name",
        };
    }

    private sealed class Kql : IQueryDialect
    {
        public string Key => "kql";
        public string Name => "Microsoft Sentinel / Defender (KQL)";
        public string Comment(string text) => "// " + text;
        public string List(IEnumerable<string> values) => string.Join(", ", values.Select(Quote));

        private const string Ip = "DeviceNetworkEvents\n| where Timestamp > ago({{days}}d)\n| where RemoteIP in ({{values}}) or LocalIP in ({{values}})\n| project Timestamp, DeviceName, InitiatingProcessFileName, LocalIP, RemoteIP, RemotePort";

        private static string Hash(string field) =>
            $"DeviceFileEvents\n| where Timestamp > ago({{{{days}}}}d)\n| where {field} in~ ({{{{values}}}})\n| project Timestamp, DeviceName, FileName, FolderPath, {field}";

        public IReadOnlyDictionary<HuntValueKind, string> Defaults { get; } = new Dictionary<HuntValueKind, string>
        {
            [HuntValueKind.Ipv4] = Ip,
            [HuntValueKind.Ipv6] = Ip,
            [HuntValueKind.Domain] = "DeviceNetworkEvents\n| where Timestamp > ago({{days}}d)\n| where RemoteUrl has_any ({{values}})\n| project Timestamp, DeviceName, InitiatingProcessFileName, RemoteUrl, RemoteIP",
            [HuntValueKind.Url] = "UrlClickEvents\n| where Timestamp > ago({{days}}d)\n| where Url in~ ({{values}})\n| project Timestamp, AccountUpn, Url, ActionType",
            [HuntValueKind.Md5] = Hash("MD5"),
            [HuntValueKind.Sha1] = Hash("SHA1"),
            [HuntValueKind.Sha256] = Hash("SHA256"),
            [HuntValueKind.FileName] = "DeviceFileEvents\n| where Timestamp > ago({{days}}d)\n| where FileName in~ ({{values}})\n| project Timestamp, DeviceName, FileName, FolderPath, SHA256",
            [HuntValueKind.EmailAddress] = "EmailEvents\n| where Timestamp > ago({{days}}d)\n| where SenderFromAddress in~ ({{values}}) or RecipientEmailAddress in~ ({{values}})\n| project Timestamp, SenderFromAddress, RecipientEmailAddress, Subject, DeliveryAction",
            [HuntValueKind.Host] = "DeviceInfo\n| where Timestamp > ago({{days}}d)\n| where DeviceName in~ ({{values}})\n| summarize arg_max(Timestamp, *) by DeviceName",
        };
    }

    private sealed class Fql : IQueryDialect
    {
        public string Key => "fql";
        public string Name => "CrowdStrike Falcon / Next-Gen SIEM";
        public string Comment(string text) => "// " + text;
        public string List(IEnumerable<string> values) => string.Join(", ", values.Select(Quote));

        private static string Hash(string field) =>
            $"#event_simpleName=ProcessRollup2\n| in(field={field}, values=[{{{{values}}}}], ignoreCase=true)\n| table([@timestamp, ComputerName, FileName, {field}])";

        public IReadOnlyDictionary<HuntValueKind, string> Defaults { get; } = new Dictionary<HuntValueKind, string>
        {
            [HuntValueKind.Ipv4] = "#event_simpleName=NetworkConnectIP4\n| in(field=RemoteAddressIP4, values=[{{values}}])\n| table([@timestamp, ComputerName, RemoteAddressIP4, RemotePort, ContextBaseFileName])",
            [HuntValueKind.Ipv6] = "#event_simpleName=NetworkConnectIP6\n| in(field=RemoteAddressIP6, values=[{{values}}], ignoreCase=true)\n| table([@timestamp, ComputerName, RemoteAddressIP6, RemotePort, ContextBaseFileName])",
            [HuntValueKind.Domain] = "#event_simpleName=DnsRequest\n| in(field=DomainName, values=[{{values}}], ignoreCase=true)\n| table([@timestamp, ComputerName, DomainName, ContextBaseFileName])",
            [HuntValueKind.Md5] = Hash("MD5HashData"),
            [HuntValueKind.Sha1] = Hash("SHA1HashData"),
            [HuntValueKind.Sha256] = Hash("SHA256HashData"),
            [HuntValueKind.FileName] = "#event_simpleName=ProcessRollup2\n| in(field=FileName, values=[{{values}}], ignoreCase=true)\n| table([@timestamp, ComputerName, FileName, CommandLine])",
            [HuntValueKind.Host] = "in(field=ComputerName, values=[{{values}}], ignoreCase=true)\n| groupBy([ComputerName, #event_simpleName])",
        };
    }
}

/// <summary>
/// A dialect's template for one kind of value: the built-in default, and the administrator's when saved (an empty
/// saved template turns that kind off).
/// </summary>
public sealed record QueryTemplateRow(IQueryDialect Dialect, HuntValueKind Kind, string? Default, string? Saved)
{
    /// <summary>The template generated hunts use; null or empty means the kind's values are left out.</summary>
    public string? Effective => Saved ?? Default;

    public bool IsOff => Saved is { Length: 0 };
}

/// <summary>
/// Builds hunt queries from indicators: split by kind of value (<see cref="HuntValues"/>), one block per kind the dialect
/// has a template for, the values written as the dialect writes a list. Kinds without a template, or turned off, are
/// named in a comment, never dropped silently.
/// </summary>
public sealed partial class HuntQueryBuilder(IAppDbContextFactory dbs, ICurrentUser user)
{
    /// <summary>The most values put in one block; more make a query a SIEM refuses or that runs for minutes.</summary>
    public const int MaxValuesPerBlock = 500;

    public static string DialectName(string key) => QueryDialects.Find(key)?.Name ?? key;

    public async Task<string> BuildAsync(string dialect, IEnumerable<ArticleIndicator> indicators, int days = DefaultDays, CancellationToken ct = default)
    {
        var d = QueryDialects.Find(dialect) ?? throw new ArgumentException("Choose a query language.");
        await using var db = await dbs.CreateAsync(ct);
        var saved = await db.QueryTemplates.AsNoTracking().Where(t => t.Dialect == d.Key).ToDictionaryAsync(t => t.Kind, t => t.Template, ct);
        return Build(d, indicators, saved, days);
    }

    /// <summary>How far back a hunt looks by default, in days.</summary>
    public const int DefaultDays = 30;

    /// <summary>
    /// How far back to hunt for indicators first reported at <paramref name="earliest"/>: from a week before then, but never
    /// less than <see cref="DefaultDays"/> or more than 180 days.
    /// </summary>
    public static int WindowDays(DateTimeOffset? earliest, DateTimeOffset now) =>
        earliest is { } e ? Math.Clamp((int)(now - e).TotalDays + 7, DefaultDays, 180) : DefaultDays;

    /// <summary>The query for <paramref name="indicators"/> in <paramref name="dialect"/>, <paramref name="saved"/> templates first.</summary>
    public static string Build(IQueryDialect dialect, IEnumerable<ArticleIndicator> indicators, IReadOnlyDictionary<HuntValueKind, string>? saved = null,
        int days = DefaultDays)
    {
        var byKind = ByKind(indicators);
        var sb = new StringBuilder();
        // The window is claimed only when every block sets it ({{days}} in its template): a dialect whose queries can't
        // (Falcon's runs over the range picked in the console) says to pick it, so a clean result isn't recorded as having
        // searched days it didn't (WindowOf reads the claim back).
        var windowed = true;
        var blocks = 0;
        foreach (var (kind, values) in byKind)
        {
            var off = saved is not null && saved.TryGetValue(kind, out var s) && s.Length == 0;
            var template = off ? null : saved is not null && saved.TryGetValue(kind, out var mine) ? mine : dialect.Defaults.GetValueOrDefault(kind);
            sb.Append('\n');
            if (template is null)
            {
                var why = off ? "turned off in the query templates" : $"no {dialect.Name} template";
                sb.Append(LeftOutLine(dialect, kind, why, values.Count)).Append('\n');
                continue;
            }
            foreach (var chunk in values.Chunk(MaxValuesPerBlock))
            {
                sb.Append(dialect.Comment($"{HuntValues.Label(kind)} ({chunk.Length})")).Append('\n');
                windowed &= template.Contains(QueryDialects.DaysPlaceholder, StringComparison.Ordinal);
                blocks++;
                // A kind's template takes only {{values}}; {{values:kind}} is a saved query's, and is left as it is here.
                var list = dialect.List(Searched(kind, chunk));
                sb.Append(Fill(template, days, scope => scope is null ? list : null)).Append('\n');
            }
        }
        // No block at all (every value's kind left out) searches nothing, so it claims no window either.
        var window = blocks == 0 ? "with nothing it can search: every value was left out"
            : windowed ? Window(days) : $"with no time range of its own: set it to the last {days} days when you run it";
        var head = Head(dialect, $"{GeneratedBy}{Count(byKind.Sum(t => t.Values.Count))}, {window}. Check the dataset and field names before running.");
        return (head + "\n" + sb.ToString()).TrimEnd() + "\n";
    }

    private const string LookingBack = "looking back ";
    private const string NotIncluded = " value(s) not included.";
    private const string GeneratedBy = "Generated by TIDE from ";
    private const string FromSavedQuery = "From the saved query ";

    // --- the pieces a generated query and a saved one are both written with ---------------------------------------

    /// <summary>The indicators by kind of value, in kind order, each kind's values trimmed and once (ignoring case).</summary>
    private static List<(HuntValueKind Kind, List<string> Values)> ByKind(IEnumerable<ArticleIndicator> indicators) => indicators
        .Where(i => !string.IsNullOrWhiteSpace(i.Value))
        .GroupBy(HuntValues.KindOf)
        .OrderBy(g => g.Key)
        .Select(g => (Kind: g.Key, Values: g.Select(i => i.Value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList()))
        .ToList();

    private static readonly System.Globalization.IdnMapping Idn = new();

    /// <summary>
    /// The values a block searches for <paramref name="values"/> of <paramref name="kind"/>: each as written, and an
    /// international domain name ("bücher.example") in its punycode form too ("xn--bcher-kva.example"), which is how DNS,
    /// proxy and endpoint logs record it, so the hunt doesn't come back clean for a name the logs hold. Counted as the one
    /// indicator it is.
    /// </summary>
    private static string[] Searched(HuntValueKind kind, IEnumerable<string> values) => kind != HuntValueKind.Domain
        ? values.ToArray()
        : values.SelectMany(v => Ascii(v) is { } ascii && !string.Equals(ascii, v, StringComparison.OrdinalIgnoreCase) ? [v, ascii] : new[] { v })
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private static string? Ascii(string domain)
    {
        try { return Idn.GetAscii(domain.TrimEnd('.')).ToLowerInvariant(); }
        catch (ArgumentException) { return null; }   // not a name IDNA can write (a label too long, say): searched as written
    }

    /// <summary>
    /// A template filled for one block: <c>{{days}}</c> first (so a value that happens to contain "{{days}}" is left as it
    /// is), then each values placeholder with <paramref name="list"/>'s written list for its scope: null for
    /// <c>{{values}}</c>, else the kind <c>{{values:kind}}</c> names. A scope <paramref name="list"/> gives nothing for,
    /// or a kind TIDE doesn't know, is left as it is. The caller writes each list once, before this.
    /// </summary>
    private static string Fill(string template, int days, Func<HuntValueKind?, string?> list) =>
        ValuesPattern().Replace(
            template.Replace(QueryDialects.DaysPlaceholder, days.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal),
            m => (m.Groups[1].Success ? KindNamed(m.Groups[1].Value) is { } kind ? list(kind) : null : list(null)) ?? m.Value);

    // The first line TIDE writes on a query it fills (WithoutHeader strips it, WindowOf reads the window back), and a line
    // for each kind of value it left out (LeftOut reads the counts back).
    private static string Head(IQueryDialect dialect, string text) => dialect.Comment(text);

    private static string Window(int days) => $"{LookingBack}{days} days";

    private static string Count(int indicators) => $"{indicators} indicator(s)";

    private static string LeftOutLine(IQueryDialect dialect, HuntValueKind kind, string why, int values) =>
        dialect.Comment($"{HuntValues.Label(kind)}: {why}; {values}{NotIncluded}");

    /// <summary>
    /// A saved query as it's added to an action. <c>{{days}}</c> is filled with <paramref name="days"/>; <c>{{values}}</c>
    /// with every one of <paramref name="indicators"/>, and <c>{{values:sha256}}</c> (any <see cref="HuntValueKind"/>, by
    /// name) with those of that kind, each list written as the dialect writes one. The first line names the query and, as a
    /// generated query's does, the window (a query without <c>{{days}}</c> sets its own range, so none is claimed), so the
    /// close step reads it back (<see cref="WindowOf"/>). A query that uses only kinds' placeholders names, as a generated
    /// one does, the indicators of other kinds it left out (<see cref="LeftOut"/>). Refused when a placeholder would get no
    /// values: an empty list is a syntax error in every dialect, and a hunt that searches nothing isn't one.
    /// </summary>
    /// <remarks>
    /// Past <see cref="MaxValuesPerBlock"/> values for a placeholder, the query is repeated, as a generated one repeats a
    /// kind's block: each copy (headed "Part 2 of 3") takes the next block. Cutting the list and naming the rest as left out
    /// would also keep a query a SIEM accepts, but would leave the analyst to write the rest of the hunt by hand; repeating
    /// it searches every value. With more than one placeholder over the limit, the copies are every combination of their
    /// blocks, so a query that needs an address AND a domain still sees each pair (in practice two or four copies: an action
    /// carries at most 500 indicators, and its article 500).
    /// </remarks>
    public static string FromSaved(IQueryDialect dialect, string name, string text, int days, IEnumerable<ArticleIndicator>? indicators = null)
    {
        var byKind = ByKind(indicators ?? []).ToDictionary(g => g.Kind, g => g.Values);
        var all = byKind.Values.SelectMany(v => v).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var kindOf = new Dictionary<string, HuntValueKind>(StringComparer.OrdinalIgnoreCase);
        foreach (var (kind, values) in byKind) foreach (var v in values) kindOf.TryAdd(v, kind);
        // Each scope the query fills, once: null for {{values}}, else a kind.
        var scopes = ValuesPlaceholders(text).Distinct().ToList();
        foreach (var scope in scopes)
        {
            if (scope is null && all.Count == 0)
                throw new InvalidOperationException($"This saved query puts the action's indicators where {QueryDialects.Placeholder} is, and the action has none. Add them on this card first.");
            if (scope is { } k && !byKind.ContainsKey(k))
                throw new InvalidOperationException($"This saved query needs {HuntValues.Label(k).ToLowerInvariant()} ({ValuesPlaceholder(k)}), and the action has none.");
        }
        // A scope's indicators, each with the forms it's searched as (Searched), so a block holds at most so many indicators.
        List<string[]> ValuesOf(HuntValueKind? scope) => scope is { } k
            ? byKind[k].Chunk(MaxValuesPerBlock).Select(c => Searched(k, c)).ToList()
            : all.Chunk(MaxValuesPerBlock).Select(c => c.SelectMany(v => Searched(kindOf[v], [v])).ToArray()).ToList();

        // Each scope's lists, block by block, each written once.
        var blocks = scopes.Select(s => ValuesOf(s).Select(dialect.List).ToList()).ToList();
        var parts = blocks.Aggregate(1, (n, b) => n * b.Count);
        var windowed = text.Contains(QueryDialects.DaysPlaceholder, StringComparison.Ordinal);
        var used = scopes.Count == 0 ? 0 : scopes.Contains(null) ? all.Count : scopes.Sum(k => byKind[k!.Value].Count);
        var sb = new StringBuilder(Head(dialect, $"{FromSavedQuery}\"{name}\"{(used > 0 ? $" with {Count(used)}" : "")}{(windowed ? $", {Window(days)}" : "")}.")).Append('\n');
        // Only a query written for some kinds leaves the others out; one with no placeholder isn't an indicator hunt.
        if (scopes.Count > 0 && !scopes.Contains(null))
            foreach (var (kind, values) in byKind.Where(kv => !scopes.Contains(kv.Key)))
                sb.Append(LeftOutLine(dialect, kind, "not in this saved query", values.Count)).Append('\n');
        for (var part = 0; part < parts; part++)
        {
            // Which block of each scope this copy takes: the part number counted in mixed radix, one digit per scope.
            var at = new int[scopes.Count];
            var rest = part;
            for (var s = scopes.Count - 1; s >= 0; s--)
            {
                at[s] = rest % blocks[s].Count;
                rest /= blocks[s].Count;
            }
            if (parts > 1) sb.Append(part == 0 ? "" : "\n").Append(dialect.Comment($"Part {part + 1} of {parts}")).Append('\n');
            sb.Append(Fill(text, days, scope =>
            {
                var s = scopes.IndexOf(scope);
                return blocks[s][at[s]];
            }).Trim()).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Whether <paramref name="text"/> has a values placeholder (<c>{{values}}</c> or a kind's) for an action's indicators to fill.</summary>
    public static bool UsesValues(string text) => ValuesPattern().IsMatch(text);

    /// <summary>The placeholder for <paramref name="kind"/>'s values in a saved query ("{{values:sha256}}").</summary>
    public static string ValuesPlaceholder(HuntValueKind kind) => "{{values:" + kind.ToString().ToLowerInvariant() + "}}";

    /// <summary>
    /// The values placeholders in a saved query, in order: null for <c>{{values}}</c>, else the kind named. Throws for a
    /// kind TIDE doesn't know, so a typo is caught when the query is saved rather than when it's used.
    /// </summary>
    public static IReadOnlyList<HuntValueKind?> ValuesPlaceholders(string text) =>
        ValuesPattern().Matches(text).Select(m => m.Groups[1].Success
            ? KindNamed(m.Groups[1].Value) ?? throw new ArgumentException(
                $"{{{{values:{m.Groups[1].Value}}}}} isn't a kind of indicator TIDE knows. Use {QueryDialects.Placeholder}, or one of: {string.Join(", ", Enum.GetValues<HuntValueKind>().Select(ValuesPlaceholder))}.")
            : (HuntValueKind?)null).ToList();

    internal static HuntValueKind? KindNamed(string name) =>
        Domain.Common.EnumNames.TryParseExact(name, out HuntValueKind k, ignoreCase: true) ? k : null;

    [System.Text.RegularExpressions.GeneratedRegex(@"\{\{values(?::([A-Za-z0-9]+))?\}\}", System.Text.RegularExpressions.RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial System.Text.RegularExpressions.Regex ValuesPattern();

    /// <summary>
    /// The query without the first line TIDE wrote on it (generated, or added from a saved query), for saving it to the
    /// team's library: adding it again writes a new one.
    /// </summary>
    public static string WithoutHeader(string dialect, string text)
    {
        var d = QueryDialects.Find(dialect);
        var clean = (text ?? "").Replace("\r\n", "\n").Trim();
        if (d is null) return clean;
        var newline = clean.IndexOf('\n');
        var first = newline < 0 ? clean : clean[..newline];
        return first.StartsWith(d.Comment(GeneratedBy), StringComparison.Ordinal) || first.StartsWith(d.Comment(FromSavedQuery), StringComparison.Ordinal)
            ? (newline < 0 ? "" : clean[(newline + 1)..].Trim())
            : clean;
    }

    /// <summary>How many days back a generated query looks, from its first line, or null (hand-written, or older).</summary>
    public static int? WindowOf(string? generated)
    {
        var text = generated ?? "";
        var newline = text.IndexOf('\n');
        var first = newline < 0 ? text : text[..newline];
        var at = first.IndexOf(LookingBack, StringComparison.Ordinal);
        if (at < 0) return null;
        var digits = new string(first[(at + LookingBack.Length)..].TakeWhile(char.IsAsciiDigit).ToArray());
        return int.TryParse(digits, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var days) ? days : null;
    }

    /// <summary>How many of the indicators a generated query left out (no template for their kind, or turned off).</summary>
    public static int LeftOut(string? generated)
    {
        var total = 0;
        foreach (var line in (generated ?? "").Split('\n'))
        {
            var end = line.IndexOf(NotIncluded, StringComparison.Ordinal);
            if (end <= 0) continue;
            var start = line.LastIndexOf(' ', end - 1) + 1;
            if (int.TryParse(line[start..end], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n)) total += n;
        }
        return total;
    }

    // --- administering templates ---------------------------------------------------------------------------------

    /// <summary>Every dialect's template for every kind of value: the default, the saved one, or neither.</summary>
    public async Task<IReadOnlyList<QueryTemplateRow>> TemplatesAsync(CancellationToken ct = default)
    {
        RequireAdmin();
        await using var db = await dbs.CreateAsync(ct);
        var saved = await db.QueryTemplates.AsNoTracking().ToListAsync(ct);
        return QueryDialects.All.SelectMany(d => Enum.GetValues<HuntValueKind>().Select(kind => new QueryTemplateRow(d, kind,
            d.Defaults.GetValueOrDefault(kind), saved.FirstOrDefault(t => t.Dialect == d.Key && t.Kind == kind)?.Template))).ToList();
    }

    /// <summary>
    /// Saves an administrator's template. It must say where the values go; an empty one turns the kind off (its values
    /// are named in a comment and left out).
    /// </summary>
    public async Task SaveTemplateAsync(string dialect, HuntValueKind kind, string? template, CancellationToken ct = default)
    {
        RequireAdmin();
        var d = QueryDialects.Find(dialect) ?? throw new ArgumentException("That isn't a query language TIDE knows.");
        if (!Enum.IsDefined(kind)) throw new ArgumentException("That isn't a kind of indicator TIDE knows.");
        var text = (template ?? "").Replace("\r\n", "\n").Trim();
        if (text.Length > 0 && !text.Contains(QueryDialects.Placeholder, StringComparison.Ordinal))
            throw new ArgumentException($"A template must contain {QueryDialects.Placeholder} where the values go.");
        if (text.Length > 4000) throw new ArgumentException("A template can be at most 4,000 characters.");
        await using var db = await dbs.CreateAsync(ct);
        var row = await db.QueryTemplates.FirstOrDefaultAsync(t => t.Dialect == d.Key && t.Kind == kind, ct);
        if (row is null) db.QueryTemplates.Add(row = new QueryTemplate { Dialect = d.Key, Kind = kind });
        row.Template = text;
        row.UpdatedAtUtc = DateTimeOffset.UtcNow;
        row.UpdatedBy = user.UserId;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Goes back to the built-in template (or, for a kind with none, to leaving it out).</summary>
    public async Task ResetTemplateAsync(string dialect, HuntValueKind kind, CancellationToken ct = default)
    {
        RequireAdmin();
        await using var db = await dbs.CreateAsync(ct);
        if (await db.QueryTemplates.FirstOrDefaultAsync(t => t.Dialect == dialect && t.Kind == kind, ct) is { } row)
        {
            db.QueryTemplates.Remove(row);
            await db.SaveChangesAsync(ct);
        }
    }

    private void RequireAdmin()
    {
        if (!user.Has(Permission.Administer)) throw new ForbiddenException("Only an administrator can change the query templates.");
    }
}
