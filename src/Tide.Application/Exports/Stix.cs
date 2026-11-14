using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Tide.Application.Actions;
using Tide.Application.Mitre;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.Application.Exports;

/// <summary>
/// An export as a STIX 2.1 bundle: an identity for the team, a report per article, and the indicators, vulnerabilities
/// (CVEs) and attack patterns (ATT&amp;CK techniques) the reports point at.
/// <para>
/// Every object carries the TLP 2.0 marking of the articles it came from (the highest, when an indicator is on several),
/// by the marking-definition ids the OASIS TLP 2.0 extension publishes, and the bundle carries those definitions. Ids
/// other than a report's are UUIDv5s of what the object is and its marking ("type|value|tlp"), so the same indicator,
/// CVE or technique at the same TLP has the same id in every export and a platform merges re-imports, while the same
/// one at another TLP is another object rather than a conflicting version of it.
/// </para>
/// </summary>
public static class Stix
{
    // The namespace STIX 2.1 gives for deterministic (UUIDv5) ids.
    private static readonly Guid Namespace = new("00abedb4-aa42-466c-9c01-fed23315a9b7");

    // A fixed creation time for the team's identity, so its id and timestamps never change between exports.
    private const string IdentityCreated = "2026-01-01T00:00:00.000Z";

    /// <summary>The TLP 2.0 marking-definition ids from the OASIS TLP 2.0 extension.</summary>
    public static string Marking(TlpLevel tlp) => tlp switch
    {
        TlpLevel.Clear => "marking-definition--94868c89-83c2-464b-929b-a1a8aa3c8487",
        TlpLevel.Green => "marking-definition--bab4a63c-aed9-4cf5-a766-dfca5abac2bb",
        TlpLevel.Amber => "marking-definition--55d920b0-5e8b-4f79-9ee9-91f868d9b421",
        TlpLevel.AmberStrict => "marking-definition--939a9414-2ddd-4d32-a0cd-375ea402b003",
        _ => "marking-definition--e828b379-4e03-4974-9ac4-e53a884c97c1",
    };

    // The OASIS TLP 2.0 extension's definition, and the date its marking definitions were made.
    private const string TlpExtension = "extension-definition--60a3c5c5-0d10-413e-aab3-9e08dde9e88d";
    private const string TlpCreated = "2022-10-01T00:00:00.000Z";

    // A TLP 2.0 marking definition, as the OASIS extension publishes it: named "TLP:AMBER+STRICT", its value "amber+strict".
    private static void MarkingDefinition(Utf8JsonWriter w, TlpLevel tlp)
    {
        var name = Tlp.Label(tlp);
        w.WriteStartObject();
        w.WriteString("type", "marking-definition");
        w.WriteString("spec_version", "2.1");
        w.WriteString("id", Marking(tlp));
        w.WriteString("created", TlpCreated);
        w.WriteString("name", name);
        w.WriteStartObject("extensions");
        w.WriteStartObject(TlpExtension);
        w.WriteString("extension_type", "property-extension");
        w.WriteString("tlp_2_0", name["TLP:".Length..].ToLowerInvariant());
        w.WriteEndObject();
        w.WriteEndObject();
        w.WriteEndObject();
    }

    /// <summary>An indicator type as a short key ("ip-address", "file-hash"), used in CSV too.</summary>
    public static string TypeKey(IndicatorType type) => type switch
    {
        IndicatorType.IpAddress => "ip-address",
        IndicatorType.FileHash => "file-hash",
        IndicatorType.FileName => "file-name",
        IndicatorType.EmailAddress => "email-address",
        IndicatorType.RegistryKey => "registry-key",
        IndicatorType.CommandLine => "command-line",
        IndicatorType.CryptoWallet => "crypto-wallet",
        _ => type.ToString().ToLowerInvariant(),
    };

    /// <summary>
    /// The STIX pattern for an indicator, or null for a kind STIX has no clean observable for (a host name, a process
    /// name, a hash of no known algorithm, "other"); those go out in the CSV only. Addresses and hashes are told apart
    /// the way hunt queries tell them apart (<see cref="HuntValues.KindOf"/>).
    /// </summary>
    public static string? Pattern(ArticleIndicator indicator)
    {
        var v = indicator.Value.Trim();
        if (v.Length == 0) return null;
        var (path, literal) = HuntValues.KindOf(indicator) switch
        {
            HuntValueKind.Ipv4 => ("ipv4-addr:value", v),
            HuntValueKind.Ipv6 => ("ipv6-addr:value", v),
            HuntValueKind.Domain => ("domain-name:value", v.ToLowerInvariant()),
            HuntValueKind.Url => ("url:value", v),
            HuntValueKind.EmailAddress => ("email-addr:value", v),
            HuntValueKind.Account => ("user-account:account_login", v),
            HuntValueKind.FileName => ("file:name", v),
            HuntValueKind.RegistryKey => ("windows-registry-key:key", v),
            HuntValueKind.CommandLine => ("process:command_line", v),
            HuntValueKind.Md5 => ("file:hashes.MD5", v.ToLowerInvariant()),
            HuntValueKind.Sha1 => ("file:hashes.'SHA-1'", v.ToLowerInvariant()),
            HuntValueKind.Sha256 => ("file:hashes.'SHA-256'", v.ToLowerInvariant()),
            HuntValueKind.Sha512 => ("file:hashes.'SHA-512'", v.ToLowerInvariant()),
            _ => (null, v),
        };
        return path is null ? null : $"[{path} = '{literal.Replace("\\", "\\\\").Replace("'", "\\'")}']";
    }

    /// <summary>The bundle for <paramref name="set"/>, produced by <paramref name="producer"/> (the team's name), as UTF-8 JSON.</summary>
    public static byte[] Write(ExportSet set, string producer)
    {
        var identity = Id("identity", producer);
        var indicators = new Dictionary<string, Shared>(StringComparer.Ordinal);
        var vulnerabilities = new Dictionary<string, Shared>(StringComparer.Ordinal);
        var techniques = new Dictionary<string, Shared>(StringComparer.Ordinal);
        var reports = new List<(Article Article, List<Shared> Refs)>();

        foreach (var a in set.Articles)
        {
            var refs = new List<Shared>();
            foreach (var i in ExportSet.Indicators(a))
                if (Pattern(i) is { } pattern) refs.Add(Note(indicators, "indicator", pattern, a, i.Value, pattern, i.Note));
            foreach (var c in ExportSet.Cves(a)) refs.Add(Note(vulnerabilities, "vulnerability", c, a, c));
            foreach (var t in ExportSet.Techniques(a)) refs.Add(Note(techniques, "attack-pattern", t, a, t));
            if (refs.Count > 0) reports.Add((a, refs.Distinct().ToList()));
        }

        using var ms = new MemoryStream();
        // A file to import, never embedded in a page: quotes and apostrophes in patterns are written as themselves, not
        // escaped for HTML, and lines end in \n on every server so the same export is the same bytes.
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true, NewLine = "\n", Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteString("type", "bundle");
            w.WriteString("id", Id("bundle", set.FileStem + "|" + string.Join(',', reports.Select(r => r.Article.Id))));
            w.WriteStartArray("objects");

            w.WriteStartObject();
            Common(w, "identity", identity, IdentityCreated, IdentityCreated);
            w.WriteString("name", producer);
            w.WriteString("identity_class", "organization");
            w.WriteEndObject();

            // The TLP 2.0 marking definitions aren't among STIX 2.1's own, so each one the bundle refers to is in it.
            var used = reports.Select(r => r.Article.Tlp).Concat(indicators.Values.Concat(vulnerabilities.Values).Concat(techniques.Values).Select(s => s.Tlp));
            foreach (var tlp in used.Distinct().Order()) MarkingDefinition(w, tlp);

            foreach (var (a, refs) in reports)
            {
                w.WriteStartObject();
                Common(w, "report", "report--" + a.Id, Time(a.CreatedAtUtc), Time(a.ModifiedAtUtc ?? a.CreatedAtUtc));
                w.WriteString("created_by_ref", identity);
                w.WriteString("name", a.Title);
                if (!string.IsNullOrWhiteSpace(a.Bluf)) w.WriteString("description", a.Bluf);
                w.WriteString("published", Time(a.PublishedAtUtc ?? a.CreatedAtUtc));
                Strings(w, "report_types", ["threat-report"]);
                Strings(w, "labels", a.Priority is { } p ? [a.Classification.ToString().ToLowerInvariant(), p.ToString().ToLowerInvariant()] : [a.Classification.ToString().ToLowerInvariant()]);
                Strings(w, "object_refs", refs.Select(r => r.Id));
                if (!string.IsNullOrWhiteSpace(a.SourceUrl))
                    ExternalReference(w, string.IsNullOrWhiteSpace(a.SourceName) ? "source" : a.SourceName, url: a.SourceUrl);
                Strings(w, "object_marking_refs", [Marking(a.Tlp)]);
                w.WriteEndObject();
            }

            foreach (var s in indicators.Values)
                SharedObject(w, "indicator", s, identity, s.Value, () =>
                {
                    if (s.Notes.Count > 0) w.WriteString("description", string.Join("\n", s.Notes));
                    w.WriteString("pattern", s.Pattern);
                    w.WriteString("pattern_type", "stix");
                    w.WriteString("pattern_version", "2.1");
                    w.WriteString("valid_from", Time(s.First));
                });
            foreach (var s in vulnerabilities.Values)
                SharedObject(w, "vulnerability", s, identity, s.Value, () => ExternalReference(w, "cve", externalId: s.Value));
            foreach (var s in techniques.Values)
                SharedObject(w, "attack-pattern", s, identity, AttackCatalog.Find(s.Value)?.Name ?? s.Value,
                    () => ExternalReference(w, "mitre-attack", externalId: s.Value, url: AttackMatrix.TechniqueUrl(s.Value)));

            w.WriteEndArray();
            w.WriteEndObject();
        }
        return ms.ToArray();
    }

    /// <summary>A UUIDv5 (RFC 9562) of <paramref name="name"/> in the STIX namespace.</summary>
    public static Guid Uuid5(string name) => Uuid5(Namespace, name);

    /// <summary>A UUIDv5 (RFC 9562) of <paramref name="name"/> in <paramref name="ns"/>.</summary>
    public static Guid Uuid5(Guid ns, string name)
    {
        var input = new byte[16 + Encoding.UTF8.GetByteCount(name)];
        ns.TryWriteBytes(input, bigEndian: true, out _);
        Encoding.UTF8.GetBytes(name, input.AsSpan(16));
        Span<byte> hash = stackalloc byte[20];
#pragma warning disable CA5350, S4790 // SHA-1 is what UUIDv5 is defined over; it names an object, it doesn't protect anything.
        SHA1.HashData(input, hash);
#pragma warning restore CA5350, S4790
        hash[6] = (byte)((hash[6] & 0x0F) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash[..16], bigEndian: true);
    }

    // "indicator--<uuid5 of 'indicator|name'>".
    private static string Id(string type, string name) => $"{type}--{Uuid5(type + "|" + name)}";

    private static string Time(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static void Common(Utf8JsonWriter w, string type, string id, string created, string modified)
    {
        w.WriteString("type", type);
        w.WriteString("spec_version", "2.1");
        w.WriteString("id", id);
        w.WriteString("created", created);
        w.WriteString("modified", modified);
    }

    private static void Strings(Utf8JsonWriter w, string name, IEnumerable<string> values)
    {
        w.WriteStartArray(name);
        foreach (var v in values) w.WriteStringValue(v);
        w.WriteEndArray();
    }

    private static void ExternalReference(Utf8JsonWriter w, string source, string? externalId = null, string? url = null)
    {
        w.WriteStartArray("external_references");
        w.WriteStartObject();
        w.WriteString("source_name", source);
        if (externalId is not null) w.WriteString("external_id", externalId);
        if (url is not null) w.WriteString("url", url);
        w.WriteEndObject();
        w.WriteEndArray();
    }

    // An indicator, vulnerability or attack pattern: the common properties, its own (body), and its marking.
    private static void SharedObject(Utf8JsonWriter w, string type, Shared s, string identity, string name, Action body)
    {
        w.WriteStartObject();
        Common(w, type, s.Id, Time(s.First), Time(s.Last));
        w.WriteString("created_by_ref", identity);
        w.WriteString("name", name);
        body();
        Strings(w, "object_marking_refs", [Marking(s.Tlp)]);
        w.WriteEndObject();
    }

    // An indicator, CVE or technique that one or more articles point at: its marking is the highest of theirs, and its
    // first and last times span them. Its id is known once every article has been noted, since the marking is in it.
    private sealed class Shared(string type, string key, string value, string? pattern)
    {
        public string Id => Stix.Id(type, key + "|" + Domain.Enums.Tlp.Key(Tlp));
        public string Value { get; } = value;
        public string? Pattern { get; } = pattern;
        public TlpLevel Tlp { get; set; }
        public DateTimeOffset First { get; set; } = DateTimeOffset.MaxValue;
        public DateTimeOffset Last { get; set; } = DateTimeOffset.MinValue;
        public List<string> Notes { get; } = [];
    }

    // Records that article a points at the object of this type and key (the pattern, the CVE id, the technique id),
    // creating it the first time, and returns it.
    private static Shared Note(Dictionary<string, Shared> map, string type, string key, Article a, string value, string? pattern = null, string? note = null)
    {
        if (!map.TryGetValue(key, out var s)) map[key] = s = new Shared(type, key, value, pattern);
        if (a.Tlp > s.Tlp) s.Tlp = a.Tlp;
        if (a.CreatedAtUtc < s.First) s.First = a.CreatedAtUtc;
        var changed = a.ModifiedAtUtc ?? a.CreatedAtUtc;
        if (changed > s.Last) s.Last = changed;
        var n = note?.Trim();
        if (!string.IsNullOrEmpty(n) && !s.Notes.Contains(n)) s.Notes.Add(n);
        return s;
    }
}
