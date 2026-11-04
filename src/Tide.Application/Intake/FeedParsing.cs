using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Tide.Application.Intake;

/// <summary>An item as a feed gives it, before it's stored.</summary>
public sealed record ParsedItem(string Title, string Url, DateTimeOffset? PublishedAtUtc, string? Summary, IReadOnlyList<string> Cves)
{
    /// <summary>The feed's own tags on the item (RSS &lt;category&gt;, Atom category, RSS 1.0 dc:subject), as plain text.</summary>
    public IReadOnlyList<string> Categories { get; init; } = [];

    /// <summary>Who the feed says wrote the item (RSS &lt;author&gt; or dc:creator, Atom author name), as plain text, or null.
    /// A sponsored item often says so here ("Sponsored by …").</summary>
    public string? Author { get; init; }

    /// <summary>The feed an RSS item says it came from (&lt;source&gt;), as plain text, or null.</summary>
    public string? Origin { get; init; }
}

/// <summary>
/// One address per story: the scheme and host lower-cased, default ports, fragments and tracking parameters (utm_*,
/// fbclid, gclid and the like) removed, the remaining query sorted, and a trailing slash dropped. Two feeds linking the
/// same article with different tracking tags give the same address.
/// </summary>
public static class UrlCanonicalizer
{
    private static readonly HashSet<string> Tracking = new(StringComparer.OrdinalIgnoreCase)
    {
        "fbclid", "gclid", "dclid", "msclkid", "mc_cid", "mc_eid", "igshid", "yclid", "_hsenc", "_hsmi", "mkt_tok", "oly_anon_id",
        "oly_enc_id", "rb_clickid", "s_cid", "vero_conv", "vero_id", "wickedid", "ref", "ref_src", "cmpid", "sr_share", "spm",
        "ito", "ncid", "guccounter", "guce_referrer", "guce_referrer_sig", "at_medium", "at_campaign",
    };

    /// <summary>The canonical form of <paramref name="url"/>, or null when it isn't an absolute http(s) address.</summary>
    public static string? Canonicalize(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https"))
            return null;
        var query = u.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(p =>
            {
                var name = Uri.UnescapeDataString(p.Split('=', 2)[0]);
                return !name.StartsWith("utm_", StringComparison.OrdinalIgnoreCase) && !Tracking.Contains(name);
            })
            .Order(StringComparer.Ordinal)
            .ToList();
        var path = u.AbsolutePath.Length > 1 ? u.AbsolutePath.TrimEnd('/') : u.AbsolutePath;
        var port = u.IsDefaultPort ? "" : ":" + u.Port.ToString(CultureInfo.InvariantCulture);
        var text = $"{u.Scheme}://{u.Host.ToLowerInvariant()}{port}{(path == "/" ? "" : path)}{(query.Count > 0 ? "?" + string.Join('&', query) : "")}";
        return text.Length > 2000 ? null : text;
    }

    /// <summary>The unique key an item is stored under: SHA-256 of its canonical address, as lower-case hex.</summary>
    public static string Hash(string canonical) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
}

/// <summary>Spotting the same story under two headlines: titles reduced to their significant words, then compared.</summary>
public static partial class TitleMatch
{
    /// <summary>How alike two titles' words must be (Jaccard similarity) to call them the same story.</summary>
    public const double Threshold = 0.75;

    private static readonly HashSet<string> Stop = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "and", "or", "of", "to", "in", "on", "for", "with", "by", "at", "from", "as", "is", "are", "was", "were",
        "be", "its", "it", "this", "that", "new", "via", "after", "over", "into", "about", "how", "why", "what",
    };

    [GeneratedRegex(@"[^\p{L}\p{N}-]+", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex NonWord();

    /// <summary>The title's significant words, lower-cased, sorted, space-separated.</summary>
    public static string Key(string title) => string.Join(' ', NonWord().Split(title.ToLowerInvariant())
        .Where(w => w.Length > 1 && !Stop.Contains(w)).Distinct().Order(StringComparer.Ordinal));

    public static double Similarity(string keyA, string keyB)
    {
        var a = keyA.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var b = keyB.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        if (a.Count < 3 || b.Count < 3) return a.SetEquals(b) && a.Count > 0 ? 1 : 0;
        return (double)a.Intersect(b).Count() / a.Union(b).Count();
    }
}

/// <summary>
/// Reads RSS 2.0, RSS 1.0 (RDF) and Atom, and the CISA KEV catalog's JSON. The XML reader skips a DTD unread and resolves
/// no external entities, so a hostile feed can't read files or expand itself into gigabytes: an entity a DTD would have
/// declared is undeclared, and refuses the feed. Summaries come back as plain
/// text: feed HTML is never rendered anywhere in TIDE.
/// </summary>
public static partial class FeedParser
{
    /// <summary>The most items taken from one fetch.</summary>
    public const int MaxItems = 200;

    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    private static readonly XNamespace Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private static readonly XNamespace Rss1 = "http://purl.org/rss/1.0/";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace Content = "http://purl.org/rss/1.0/modules/content/";

    [GeneratedRegex(@"<[^>]*>", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Tag();

    [GeneratedRegex(@"<script\b[^>]*>.*?</script\s*>|<style\b[^>]*>.*?</style\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ScriptOrStyle();

    // Runs of space, and the control characters a feed can carry as references ("&#x1B;").
    [GeneratedRegex(@"[\s\p{Cc}]+", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Space();

    [GeneratedRegex(@"\bCVE-\d{4}-\d{4,7}\b", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Cve();

    // "The post <title> appeared first on <site>." as the whole text or its last sentence: after the start or a sentence's
    // end, a title of up to 300 characters, a site name (a dot only inside one, as in "example.com"), and nothing after it.
    [GeneratedRegex(@"(?:^|(?<=[.!?…\]]))\s*The post \S.{0,299}? appeared first on [^\s.!?](?:[^.!?]|\.(?=\S)){0,79}\.?\s*$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Footer();

    /// <summary>
    /// The deepest element nesting a feed may have. Reading an element's text (<see cref="XElement.Value"/>) walks its
    /// children recursively, so a document nested tens of thousands deep would overflow the stack, which no catch can
    /// stop; a real feed is a few levels deep, an entry's XHTML content a few dozen.
    /// </summary>
    public const int MaxDepth = 256;

    /// <summary>Parses a feed document. Throws <see cref="FormatException"/> when it isn't RSS or Atom, or is nested
    /// deeper than <see cref="MaxDepth"/>.</summary>
    public static IReadOnlyList<ParsedItem> ParseFeed(Stream content)
    {
        XDocument doc;
        try
        {
            // A control character XML doesn't allow, pasted into one post from Word or a terminal, would refuse the whole
            // feed on every poll: as a byte it's made a space before reading, and as a reference ("&#x1B;") the reader lets
            // it through (CheckCharacters off) and PlainText drops it. A DOCTYPE is skipped rather than refused, since some
            // publishers' feeds start with one (MSRC's, Google's); nothing it declares is used.
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, MaxCharactersInDocument = 20_000_000, IgnoreComments = true,
                CheckCharacters = false,
            };
            // The depth is checked on a first pass with the reader alone (which reads without recursing) before anything
            // builds a tree, so an over-deep document is refused rather than loaded.
            content = WithoutControlBytes(content);
            var start = content.Position;
            using (var probe = XmlReader.Create(content, settings))
                while (probe.Read())
                    if (probe.Depth > MaxDepth) throw new FormatException($"The feed is nested more than {MaxDepth} elements deep.");
            content.Position = start;
            using var reader = XmlReader.Create(content, settings);
            doc = XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            throw new FormatException("The feed isn't well-formed XML (" + ex.Message + ").", ex);
        }
        var root = doc.Root ?? throw new FormatException("The feed is empty.");
        IEnumerable<ParsedItem?> items = root.Name.LocalName switch
        {
            "rss" => root.Element("channel")?.Elements("item").Select(Rss2) ?? [],
            "feed" when root.Name.Namespace == Atom => root.Elements(Atom + "entry").Select(AtomEntry),
            "RDF" when root.Name.Namespace == Rdf => root.Elements(Rss1 + "item").Select(Rss10),
            _ => throw new FormatException("That isn't an RSS or Atom feed."),
        };
        return items.OfType<ParsedItem>().Take(MaxItems).ToList();
    }

    // The feed as a copy the reader can go through twice, each control byte XML forbids (all below 0x20 but tab, line
    // feed and carriage return) made a space. On the bytes, that's safe for UTF-8 and the single-byte encodings, where
    // such a byte is never part of another character; a UTF-16 or UTF-32 feed (a mark, or a zero byte in its first two)
    // is copied as it is, and so is one whose declaration names a stateful ISO-2022 encoding (Japanese, Korean or Chinese),
    // which switches character sets with ESC, SO and SI.
    private static MemoryStream WithoutControlBytes(Stream content)
    {
        var copy = new MemoryStream();
        content.CopyTo(copy);
        var bytes = copy.GetBuffer().AsSpan(0, (int)copy.Length);
        var wide = bytes.Length >= 2 && (bytes[0] == 0 || bytes[1] == 0 || (bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF));
        var head = Encoding.Latin1.GetString(bytes[..Math.Min(bytes.Length, 100)]);
        var stateful = head.StartsWith("<?xml", StringComparison.Ordinal) && head.IndexOf("?>", StringComparison.Ordinal) is var shut and > 0
            && head[..shut].Contains("iso-2022", StringComparison.OrdinalIgnoreCase);
        if (!wide && !stateful)
            foreach (ref var b in bytes)
                if (b < 0x20 && b is not ((byte)'\t' or (byte)'\n' or (byte)'\r')) b = (byte)' ';
        copy.Position = 0;
        return copy;
    }

    private static ParsedItem? Rss2(XElement e)
    {
        var link = (string?)e.Element("link") ?? ((string?)e.Element("guid") is { } g && g.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? g : null);
        var summary = (string?)e.Element(Content + "encoded") is { Length: > 0 } full && ((string?)e.Element("description") ?? "").Length < 40
            ? full : (string?)e.Element("description");
        return Item((string?)e.Element("title"), link, Date((string?)e.Element("pubDate") ?? (string?)e.Element(Dc + "date")), summary,
            e.Elements("category").Select(c => (string?)c).Concat(e.Elements(Dc + "subject").Select(c => (string?)c)),
            e.Elements(Dc + "creator").Concat(e.Elements("author")).Select(a => (string?)a), (string?)e.Element("source"));
    }

    private static ParsedItem? Rss10(XElement e) =>
        Item((string?)e.Element(Rss1 + "title"), (string?)e.Element(Rss1 + "link"), Date((string?)e.Element(Dc + "date")), (string?)e.Element(Rss1 + "description"),
            e.Elements(Dc + "subject").Select(c => (string?)c), e.Elements(Dc + "creator").Select(a => (string?)a));

    private static ParsedItem? AtomEntry(XElement e)
    {
        var links = e.Elements(Atom + "link").ToList();
        var link = links.FirstOrDefault(l => ((string?)l.Attribute("rel") ?? "alternate") == "alternate") ?? links.FirstOrDefault();
        return Item((string?)e.Element(Atom + "title"), (string?)link?.Attribute("href"),
            Date((string?)e.Element(Atom + "published") ?? (string?)e.Element(Atom + "updated")),
            (string?)e.Element(Atom + "summary") ?? (string?)e.Element(Atom + "content"),
            e.Elements(Atom + "category").Select(c => (string?)c.Attribute("label") ?? (string?)c.Attribute("term")),
            e.Elements(Atom + "author").Select(a => (string?)a.Element(Atom + "name")));
    }

    /// <summary>The longest author kept, in characters.</summary>
    public const int MaxAuthor = 200;

    private static ParsedItem? Item(string? title, string? link, DateTimeOffset? published, string? summary, IEnumerable<string?> categories,
        IEnumerable<string?>? authors = null, string? origin = null)
    {
        var t = PlainText(title, 500);
        if (t.Length == 0 || string.IsNullOrWhiteSpace(link)) return null;
        var s = Cut(WithoutFooter(PlainText(summary, int.MaxValue)), Domain.Entities.FeedItem.MaxExcerpt);
        return new ParsedItem(t, link.Trim(), published, s.Length == 0 ? null : s, CvesIn(t + " " + s))
        {
            Categories = categories.Select(c => PlainText(c, 100)).Where(c => c.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(30).ToList(),
            Author = PlainText(string.Join(", ", (authors ?? []).Select(a => PlainText(a, MaxAuthor)).Where(a => a.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(10)), MaxAuthor) is { Length: > 0 } author ? author : null,
            Origin = PlainText(origin, MaxAuthor) is { Length: > 0 } from ? from : null,
        };
    }

    /// <summary>The CVE ids in <paramref name="text"/>, upper-cased, in order, once each.</summary>
    public static IReadOnlyList<string> CvesIn(string? text) =>
        Cve().Matches(text ?? "").Select(m => m.Value.ToUpperInvariant()).Distinct().Take(50).ToList();

    /// <summary>HTML (or plain text) as one line of plain text: scripts and tags removed, entities decoded, cut short.</summary>
    public static string PlainText(string? html, int max)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";
        var text = WebUtility.HtmlDecode(Tag().Replace(ScriptOrStyle().Replace(html, " "), " "));
        return Cut(Space().Replace(text, " ").Trim(), max);
    }

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..(max - 1)].TrimEnd() + "…";

    /// <summary>
    /// A summary without the footer WordPress feeds end with ("The post Some title appeared first on SecurityWeek."), so
    /// the excerpt is the story. Only a last sentence of exactly that shape goes; a "[…]" before it stays.
    /// </summary>
    public static string WithoutFooter(string text)
    {
        var m = Footer().Match(text);
        return m.Success ? text[..m.Index].TrimEnd() : text;
    }

    private static DateTimeOffset? Date(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim();
        if (DateTimeOffset.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d)) return d.ToUniversalTime();
        // RFC 822 with a named zone ("GMT", "EST") that .NET doesn't parse.
        var zones = new Dictionary<string, string> { ["GMT"] = "+0000", ["UT"] = "+0000", ["UTC"] = "+0000", ["EST"] = "-0500", ["EDT"] = "-0400",
            ["CST"] = "-0600", ["CDT"] = "-0500", ["MST"] = "-0700", ["MDT"] = "-0600", ["PST"] = "-0800", ["PDT"] = "-0700" };
        foreach (var (name, offset) in zones)
            if (t.EndsWith(" " + name, StringComparison.OrdinalIgnoreCase)
                && DateTimeOffset.TryParse(t[..^name.Length] + offset, CultureInfo.InvariantCulture, DateTimeStyles.None, out var z))
                return z.ToUniversalTime();
        return null;
    }

    // --- CISA KEV ----------------------------------------------------------------------------------------------

    /// <summary>One entry of the KEV catalog.</summary>
    public sealed record KevEntry(string CveId, string Vendor, string Product, string Name, string? Description, DateOnly? Added, DateOnly? Due,
        bool Ransomware);

    /// <summary>Reads the KEV catalog's JSON (<c>vulnerabilities[]</c>). Throws <see cref="FormatException"/> when it isn't one.</summary>
    public static IReadOnlyList<KevEntry> ParseKev(Stream content)
    {
        try
        {
            using var doc = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = 16 });
            if (!doc.RootElement.TryGetProperty("vulnerabilities", out var list) || list.ValueKind != JsonValueKind.Array)
                throw new FormatException("That isn't the KEV catalog: there's no vulnerabilities list.");
            var entries = new List<KevEntry>();
            foreach (var v in list.EnumerateArray())
            {
                var id = Str(v, "cveID")?.Trim().ToUpperInvariant();
                if (id is null || !Cve().IsMatch(id)) continue;
                entries.Add(new KevEntry(id, Str(v, "vendorProject") ?? "", Str(v, "product") ?? "", Str(v, "vulnerabilityName") ?? id,
                    Str(v, "shortDescription"), Day(Str(v, "dateAdded")), Day(Str(v, "dueDate")),
                    string.Equals(Str(v, "knownRansomwareCampaignUse"), "Known", StringComparison.OrdinalIgnoreCase)));
            }
            return entries;
        }
        catch (JsonException ex)
        {
            throw new FormatException("The KEV catalog isn't valid JSON.", ex);
        }
    }

    /// <summary>KEV entries as triage items, newest first: one per CVE, linked to its NVD page.</summary>
    public static IReadOnlyList<ParsedItem> KevItems(IEnumerable<KevEntry> entries) => entries
        .OrderByDescending(e => e.Added).Take(MaxItems)
        .Select(e => new ParsedItem(
            PlainText($"{e.CveId}: {e.Vendor} {e.Product}: {e.Name}", 500),
            "https://nvd.nist.gov/vuln/detail/" + e.CveId,
            e.Added is { } a ? new DateTimeOffset(a.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) : null,
            PlainText($"Added to CISA KEV{(e.Due is { } due ? $"; federal due date {due:yyyy-MM-dd}" : "")}.{(e.Ransomware ? " Known ransomware use." : "")} {e.Description}",
                Domain.Entities.FeedItem.MaxExcerpt),
            [e.CveId]))
        .ToList();

    // An entry that isn't an object (a stray number in the list) reads as having nothing, not as a crash.
    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static DateOnly? Day(string? s) =>
        DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
}
