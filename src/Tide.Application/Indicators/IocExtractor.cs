using System.Text.RegularExpressions;
using Tide.Domain.Enums;
using Tide.Domain.Observables;

namespace Tide.Application.Indicators;

/// <summary>
/// Pulls candidate indicators and CVE ids out of free text (an article pasted in, a vendor report's IOC appendix). The
/// result is a review list: the analyst ticks what belongs on the article, so this leans towards finding things, and
/// keeps only the shapes that are rarely prose: URLs, IP addresses, hashes, email addresses and domains.
/// <para>
/// Defanged text (<c>hxxp://evil[.]com</c>) is refanged first. A domain is kept when it was defanged in the text (the
/// author marked it as an indicator), or when it ends in a common top-level domain and has a lower-case letter, so
/// "ASP.NET" and "U.S." stay prose.
/// </para>
/// </summary>
public static partial class IocExtractor
{
    public sealed record Found(IReadOnlyList<IocObservable.ParsedIndicator> Indicators, IReadOnlyList<string> Cves);

    /// <summary>Most indicators a single extraction returns; a dump bigger than this is a feed, not an article.</summary>
    public const int MaxIndicators = 500;

    private static readonly IndicatorType[] Kept =
        [IndicatorType.Url, IndicatorType.IpAddress, IndicatorType.FileHash, IndicatorType.EmailAddress, IndicatorType.Domain];

    private static readonly HashSet<string> CommonTlds = new(StringComparer.OrdinalIgnoreCase)
    {
        "com", "net", "org", "info", "biz", "io", "co", "me", "us", "uk", "de", "ru", "cn", "su", "ir", "kp", "top", "xyz",
        "online", "site", "club", "shop", "live", "app", "dev", "cloud", "tk", "ml", "ga", "cf", "gq", "pw", "cc", "ws",
        "in", "br", "fr", "nl", "it", "es", "pl", "ua", "jp", "kr", "tw", "hk", "sg", "au", "ca", "eu", "tv", "link",
    };

    // Characters that end a token. Brackets are included because defang notation is refanged before splitting.
    private static readonly char[] Separators =
        [' ', '\t', '\r', '\n', ',', ';', '"', '\'', '<', '>', '(', ')', '[', ']', '{', '}', '|', '`', ' ', '“', '”', '‘', '’'];

    public static Found Extract(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new Found([], []);

        var cves = CveRx().Matches(text).Select(m => m.Value.ToUpperInvariant()).Distinct().ToList();

        // Each raw token, refanged on its own, so we know which ones the author defanged.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var found = new List<IocObservable.ParsedIndicator>();
        foreach (var raw in SplitDefangAware(text))
        {
            var refanged = IocObservable.Refang(raw);
            var defanged = !string.Equals(refanged, raw.Trim(), StringComparison.Ordinal);
            var live = refanged.TrimEnd('.', ':', '!', '?');
            if (live.Length < 4) continue;
            if (Detect(live) is not var (type, token)) continue;
            if (type == IndicatorType.Domain && !LooksLikeIndicatorDomain(token, defanged)) continue;
            var value = IocObservable.Normalize(type, token);
            if (!seen.Add($"{(int)type}|{value}")) continue;
            found.Add(new IocObservable.ParsedIndicator(value, type));
            if (found.Count >= MaxIndicators) break;
        }
        return new Found(found, cves);
    }

    // The token as an indicator, or its host when the token is a host with a port or a path and no scheme, as reports
    // write them ("45.77.12[.]3:8443", "update-check[.]net:443", "evil[.]com/payload.exe"): the host is what's hunted.
    private static (IndicatorType Type, string Token)? Detect(string live)
    {
        var type = IocObservable.DetectType(live);
        if (Kept.Contains(type)) return (type, live);
        if (live.Contains("://", StringComparison.Ordinal)) return null;
        // A network range (10.0.0.0/8, fe80::1/64) isn't an address to hunt: a bare address and a prefix length it can
        // have. "evil.com/404" and "1.2.3.4:8443/1" are a host and a path, whose host is kept below.
        if (live.IndexOf('/') is > 0 and var slash && System.Net.IPAddress.TryParse(live[..slash], out var network)
            && int.TryParse(live[(slash + 1)..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var bits)
            && bits <= (network.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128 : 32)) return null;
        var host = live.Split('/')[0];
        var colon = host.LastIndexOf(':');
        if (colon > 0 && host.IndexOf(':') == colon && host[(colon + 1)..] is { Length: > 0 and <= 5 } port && port.All(char.IsAsciiDigit))
            host = host[..colon];
        if (host == live || host.Length < 4) return null;
        var hostType = IocObservable.DetectType(host);
        return hostType is IndicatorType.IpAddress or IndicatorType.Domain && Kept.Contains(hostType) ? (hostType, host) : null;
    }

    private static bool LooksLikeIndicatorDomain(string domain, bool defanged)
    {
        if (defanged) return true;
        var tld = domain[(domain.LastIndexOf('.') + 1)..];
        return CommonTlds.Contains(tld) && domain.Any(char.IsLower);
    }

    /// <summary>
    /// Splits text into tokens without breaking defang notation apart: "evil[.]com" and "hxxp://x(.)io/a" stay one
    /// token each, because a bracket that holds only a dot, colon, slash, "at" or "dot" is part of the token.
    /// </summary>
    internal static IEnumerable<string> SplitDefangAware(string text)
    {
        var start = 0;
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c is '[' or '(' or '{' && DefangBracketLength(text, i) is var len and > 0)
            {
                i += len;
                continue;
            }
            if (Array.IndexOf(Separators, c) >= 0)
            {
                if (i > start) yield return text[start..i];
                start = i + 1;
            }
            i++;
        }
        if (start < text.Length) yield return text[start..];
    }

    // "[.]", "(dot)", "{:}", "[at]", "[/]" (with optional inner spaces): the length of the bracket, or 0 when it isn't one.
    private static int DefangBracketLength(string text, int open)
    {
        var close = open + 1;
        var limit = Math.Min(text.Length, open + 7);
        while (close < limit && text[close] is not (']' or ')' or '}')) close++;
        if (close >= limit) return 0;
        var inner = text.AsSpan(open + 1, close - open - 1).Trim();
        return inner is "." or ":" or "/" or "@" or "://" || inner.Equals("dot", StringComparison.OrdinalIgnoreCase) || inner.Equals("at", StringComparison.OrdinalIgnoreCase)
            ? close - open + 1
            : 0;
    }

    [GeneratedRegex(@"\bCVE-\d{4}-\d{4,7}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CveRx();

    /// <summary>A CVE id in its canonical form ("CVE-2026-1234"), or null when <paramref name="text"/> isn't one.</summary>
    public static string? NormalizeCve(string? text)
    {
        var t = (text ?? "").Trim();
        var m = CveRx().Match(t);
        return m.Success && m.Length == t.Length ? t.ToUpperInvariant() : null;
    }
}
