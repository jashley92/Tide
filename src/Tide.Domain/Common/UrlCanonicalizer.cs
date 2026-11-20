using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Tide.Domain.Common;

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

    /// <summary>The key an address is stored and looked up under (a feed item's unique one, an article's and a link's):
    /// SHA-256 of its canonical address, as lower-case hex.</summary>
    public static string Hash(string canonical) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));

    /// <summary>The <see cref="Hash"/> of <paramref name="url"/>'s canonical form, or null when it has none.</summary>
    public static string? HashOf(string? url) => Canonicalize(url) is { } canonical ? Hash(canonical) : null;
}
