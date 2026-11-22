using System.Net;
using System.Security.Cryptography;
using System.Text;
using Tide.Domain.Enums;

namespace Tide.Domain.Observables;

/// <summary>
/// The stored, indexed keys a lookup finds an indicator by, so it seeks rather than reading every indicator: the value's
/// <see cref="ValueOf">key</see> (its exact match, in either case) and its host <see cref="HostRevOf">reversed by label</see>
/// (the same host or one under it). An indicator's value can be 2,000 characters, past what SQL Server can index, so
/// the value is kept as a hash.
/// </summary>
public static class IndicatorKey
{
    /// <summary>The longest <see cref="HostRevOf"/> kept: past any host name (253), so a longer one isn't a host.</summary>
    public const int MaxHostRev = 300;

    /// <summary>
    /// The key an exact lookup matches <paramref name="value"/> by: the SHA-256 (64 hex characters) of the value in lower
    /// case, as SQL Server's case-insensitive comparison and the old exact LIKE matched it.
    /// </summary>
    public static string ValueOf(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value.ToLowerInvariant())));

    /// <summary>
    /// The host an indicator of <paramref name="type"/> names, in lower case without a trailing dot: a domain or address
    /// itself, an email address's domain, a URL's host. Null for any other type, or when there's none.
    /// </summary>
    public static string? HostOf(IndicatorType type, string value)
    {
        var v = value.Trim();
        var host = type switch
        {
            IndicatorType.Domain or IndicatorType.IpAddress => v,
            IndicatorType.EmailAddress => v.LastIndexOf('@') is var at and >= 0 ? v[(at + 1)..] : null,
            IndicatorType.Url => Uri.TryCreate(v, UriKind.Absolute, out var uri) ? uri.Host.Trim('[', ']') : null,
            _ => null,
        };
        return string.IsNullOrWhiteSpace(host) ? null : host.TrimEnd('.').ToLowerInvariant();
    }

    /// <summary>
    /// <paramref name="host"/> with its labels in reverse ("mail.evil.com" is "com.evil.mail"), so every host under a
    /// name starts with that name and a dot: "under evil.com" is a range of the index, not a scan. An IP address is kept
    /// as it is (nothing is under one). Null for no host, or one too long to be a host name.
    /// </summary>
    public static string? HostRevOf(string? host) =>
        host is null || host.Length > MaxHostRev ? null
        : IPAddress.TryParse(host, out _) ? host
        : string.Join('.', host.Split('.').Reverse());

    /// <summary>The <see cref="HostRevOf"/> of the host an indicator of <paramref name="type"/> names.</summary>
    public static string? HostRevOf(IndicatorType type, string value) => HostRevOf(HostOf(type, value));
}
