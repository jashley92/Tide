using System.Data.Common;
using System.Text.RegularExpressions;

namespace Tide.Application.Operations;

/// <summary>How a setting's value may be shown.</summary>
public enum SettingKind
{
    Plain,

    /// <summary>A password, key or token: shown only as set or not.</summary>
    Secret,

    /// <summary>A connection string: the server, database and how it signs in, never a password.</summary>
    ConnectionString,

    /// <summary>An address: shown without any user name and password in it, or a key in its query.</summary>
    Url,
}

/// <summary>
/// What of a setting's value Administration may show. Secrets are never shown, only whether they're set; a connection
/// string keeps only the parts that are known to be safe (an allow-list, so a part nobody thought of stays hidden); an
/// address loses its user-info and any query value whose name looks like a key. Error text from a background job is
/// scrubbed of anything that looks like a credential before it's shown.
/// </summary>
public static partial class SecretMask
{
    public const string Hidden = "••••";

    /// <summary>What a set secret shows as.</summary>
    public const string SetSecret = Hidden + " (set)";

    private static readonly HashSet<string> SafeConnectionParts = new(StringComparer.OrdinalIgnoreCase)
    {
        "server", "data source", "address", "addr", "network address", "database", "initial catalog", "trusted_connection",
        "integrated security", "encrypt", "trustservercertificate", "trust server certificate", "application name", "app",
        "multisubnetfailover", "multi subnet failover", "connect timeout", "connection timeout", "timeout", "mode", "cache",
        "pooling", "foreign keys", "failover partner", "applicationintent", "application intent",
    };

    [GeneratedRegex(@"(pass(word)?|pwd|secret|token|api[-_]?key|apikey|sig(nature)?|key$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SecretName();

    [GeneratedRegex(@"\b(password|pwd|passwd|secret|token|api[-_]?key|apikey|access[-_]?key|client[-_]?secret)\b(\s*[=:]\s*)(""[^""]*""|'[^']*'|[^;&\s,""']+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex CredentialInText();

    [GeneratedRegex(@"(?<=://)[^/@\s]+@", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex UserInfoInText();

    /// <summary>True when a key's own name says it holds a secret ("Email:SmtpPassword", "Cve:NvdApiKey"), whatever kind
    /// it was listed as: a guard against a secret listed as plain by mistake.</summary>
    public static bool LooksSecret(string key)
    {
        var name = key[(key.LastIndexOf(':') + 1)..];
        return SecretName().IsMatch(name) || name.EndsWith("Key", StringComparison.Ordinal);
    }

    /// <summary>The value as it may be shown; null when it isn't set.</summary>
    public static string? Display(string key, SettingKind kind, string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        if (kind == SettingKind.Secret || (kind == SettingKind.Plain && LooksSecret(key))) return SetSecret;
        return kind switch
        {
            SettingKind.ConnectionString => ConnectionString(value),
            SettingKind.Url => Url(value),
            _ => value,
        };
    }

    /// <summary>A connection string with only its known-safe parts readable, e.g.
    /// "Server=SQL01;Database=Tide;Trusted_Connection=True;Password=••••".</summary>
    public static string ConnectionString(string value)
    {
        var builder = new DbConnectionStringBuilder();
        try { builder.ConnectionString = value; }
        catch (ArgumentException) { return SetSecret + ", not readable"; }
        var parts = new List<string>();
        foreach (string key in builder.Keys)
            parts.Add($"{key}={(SafeConnectionParts.Contains(key) ? builder[key] : Hidden)}");
        return string.Join(";", parts);
    }

    /// <summary>An address without its user-info, and with any query value whose name looks like a key hidden.</summary>
    public static string Url(string value)
    {
        // "user:pw@host" parses as an address with the scheme "user", so only web and proxy addresses are read as ones.
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "socks4" or "socks5"))
            return value.Contains('@') ? Hidden : value;
        if (!UrlHasSecret(uri)) return value;
        var b = new UriBuilder(uri);
        if (!string.IsNullOrEmpty(uri.UserInfo)) { b.UserName = "user"; b.Password = ""; }
        if (uri.Query.Length > 1)
            b.Query = string.Join("&", uri.Query[1..].Split('&').Select(p => SecretPair(p) ? p[..p.IndexOf('=')] + "=" + Hidden : p));
        var shown = b.Uri.ToString();
        return string.IsNullOrEmpty(uri.UserInfo) ? shown : shown.Replace("user@", Hidden + "@", StringComparison.Ordinal);
    }

    /// <summary>True when the address carries user-info or a query value <see cref="Url"/> hides.</summary>
    public static bool UrlHasSecret(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && UrlHasSecret(uri);

    private static bool UrlHasSecret(Uri uri) =>
        !string.IsNullOrEmpty(uri.UserInfo) || (uri.Query.Length > 1 && uri.Query[1..].Split('&').Any(SecretPair));

    private static bool SecretPair(string pair)
    {
        var eq = pair.IndexOf('=');
        return eq > 0 && SecretName().IsMatch(Uri.UnescapeDataString(pair[..eq]));
    }

    /// <summary>Free text (an error message) with anything shaped like "password=…", "apikey: …" or an address's
    /// user-info hidden.</summary>
    public static string Scrub(string text) =>
        UserInfoInText().Replace(CredentialInText().Replace(text, m => m.Groups[1].Value + m.Groups[2].Value + Hidden), Hidden + "@");
}
