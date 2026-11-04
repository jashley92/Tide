using System.Net;
using System.Net.Sockets;

namespace Tide.Infrastructure.Notifications;

/// <summary>
/// Which addresses the outbound client may reach. Loopback, link-local (cloud metadata at 169.254.169.254 among them),
/// unspecified, multicast and broadcast addresses are always refused: no feed lives there, and reaching them is how a
/// feed's redirect would probe the server itself. Private ranges (RFC 1918, carrier-grade NAT, IPv6 unique-local and
/// site-local) are refused unless <c>Intake:AllowPrivateNetworks</c> is set, for an organization whose feeds are
/// internal. IPv4-mapped IPv6 addresses are judged as the IPv4 address they carry.
/// </summary>
public static class AddressPolicy
{
    /// <summary>Why <paramref name="address"/> is refused ("a loopback address"), or null when it may be reached.</summary>
    public static string? Refusal(IPAddress address, bool allowPrivateNetworks)
    {
        var ip = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        // An IPv6 address that carries an IPv4 one (NAT64, 6to4, SIIT, ISATAP, IPv4-compatible) can be translated to it on the way, so
        // it's judged as that address too.
        if (EmbeddedIPv4(ip) is { } inner && Refusal(inner, allowPrivateNetworks) is { } why) return why;
        if (IPAddress.IsLoopback(ip)) return "a loopback address";
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            if (b[0] == 169 && b[1] == 254) return "a link-local address";
            if (b[0] == 0 || b[0] >= 224) return "a reserved address";   // 0.0.0.0/8, multicast, 240/4 and broadcast
            var isPrivate = b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168)
                            || (b[0] == 100 && b[1] is >= 64 and <= 127);
            return isPrivate && !allowPrivateNetworks ? "a private address" : null;
        }
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal) return "a link-local address";
            if (ip.Equals(IPAddress.IPv6Any) || ip.IsIPv6Multicast) return "a reserved address";
            var isPrivate = ip.IsIPv6UniqueLocal || ip.IsIPv6SiteLocal;
            return isPrivate && !allowPrivateNetworks ? "a private address" : null;
        }
        return "an address that isn't IPv4 or IPv6";
    }

    // The IPv4 address inside an IPv6 one, else null: 2002::/16 (6to4, bytes 2-5); and in the last four bytes,
    // 64:ff9b::/96 (NAT64), 64:ff9b:1::/48 (local-use NAT64, RFC 8215), ::ffff:0:0/96 (SIIT), ::/96 (IPv4-compatible,
    // not :: or ::1) and an ISATAP interface id (bytes 8-11 00 00 5e fe or 02 00 5e fe, on any prefix).
    private static IPAddress? EmbeddedIPv4(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetworkV6) return null;
        var b = ip.GetAddressBytes();
        if (b[0] == 0x20 && b[1] == 0x02) return new IPAddress(b.AsSpan(2, 4));
        var wellKnownNat64 = b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b && b.AsSpan(4, 8).IndexOfAnyExcept((byte)0) < 0;
        var localNat64 = b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b && b[4] == 0x00 && b[5] == 0x01;
        var siit = b.AsSpan(0, 8).IndexOfAnyExcept((byte)0) < 0 && b[8] == 0xff && b[9] == 0xff && b[10] == 0 && b[11] == 0;
        var compatible = b.AsSpan(0, 12).IndexOfAnyExcept((byte)0) < 0 && b.AsSpan(12, 4).IndexOfAnyExcept((byte)0) >= 0
                         && !IPAddress.IsLoopback(ip);
        var isatap = b[8] is 0x00 or 0x02 && b[9] == 0x00 && b[10] == 0x5e && b[11] == 0xfe;
        return wellKnownNat64 || localNat64 || siit || compatible || isatap ? new IPAddress(b.AsSpan(12, 4)) : null;
    }
}

/// <summary>A connection or redirect refused by <see cref="AddressPolicy"/>.</summary>
public sealed class RefusedAddressException(string host, string why)
    : IOException($"{host} is {why}, which TIDE doesn't fetch from.")
{
    public string Host { get; } = host;
}
