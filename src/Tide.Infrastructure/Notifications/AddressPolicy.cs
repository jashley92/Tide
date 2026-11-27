using System.Net;
using System.Net.Sockets;

namespace Tide.Infrastructure.Notifications;

/// <summary>
/// Which addresses the outbound client may reach. Loopback, link-local (cloud metadata at 169.254.169.254 among them),
/// unspecified, multicast and broadcast addresses are always refused: no feed lives there, and reaching them is how a
/// feed's redirect would probe the server itself. Private ranges (RFC 1918, carrier-grade NAT, IPv6 unique-local and
/// site-local) are refused unless the caller allows them: <c>Intake:AllowPrivateNetworks</c> for an organization whose
/// feeds are internal, <c>Webhooks:AllowPrivateNetworks</c> (on by default) for webhook receivers, which often are.
/// IPv4-mapped IPv6 addresses are judged as the IPv4 address they carry. A cloud platform's metadata or host service that
/// sits outside the link-local range (Azure's 168.63.129.16, Alibaba's 100.100.100.200, Oracle's 192.0.0.192, the IPv6
/// forms on AWS and Google Cloud) is always refused too, private networks allowed or not: it hands out the server's own
/// credentials. <see cref="AddressGuard"/> applies it.
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
            // The platform's own endpoints on a cloud VM, outside 169.254/16: Azure's host service (168.63.129.16) and
            // Alibaba's metadata (100.100.100.200, inside carrier-grade NAT).
            if ((b[0] == 168 && b[1] == 63 && b[2] == 129 && b[3] == 16) || (b[0] == 100 && b[1] == 100 && b[2] == 100 && b[3] == 200))
                return "a cloud metadata address";
            // 192.0.0.0/24 (IETF protocol assignments; Oracle's metadata at 192.0.0.192) and 192.88.99.0/24 (the retired
            // 6to4 relays): nothing public answers there.
            if (b[0] == 192 && ((b[1] == 0 && b[2] == 0) || (b[1] == 88 && b[2] == 99))) return "a reserved address";
            // 198.18.0.0/15 is for testing between networks: never public, so it goes with the private ranges.
            var isPrivate = b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168)
                            || (b[0] == 100 && b[1] is >= 64 and <= 127) || (b[0] == 198 && b[1] is 18 or 19);
            return isPrivate && !allowPrivateNetworks ? "a private address" : null;
        }
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal) return "a link-local address";
            if (ip.Equals(IPAddress.IPv6Any) || ip.IsIPv6Multicast) return "a reserved address";
            // The metadata services' IPv6 addresses (AWS fd00:ec2::254, Google Cloud fd20:ce::254) are unique-local, so
            // allowing private networks would otherwise allow them.
            if (ip.Equals(AwsMetadataV6) || ip.Equals(GoogleMetadataV6)) return "a cloud metadata address";
            var isPrivate = ip.IsIPv6UniqueLocal || ip.IsIPv6SiteLocal;
            return isPrivate && !allowPrivateNetworks ? "a private address" : null;
        }
        return "an address that isn't IPv4 or IPv6";
    }

    // Built from their bytes, as the IPv4 checks above are, rather than parsed from text.
    private static readonly IPAddress AwsMetadataV6 = new([0xfd, 0x00, 0x0e, 0xc2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x02, 0x54]);      // fd00:ec2::254
    private static readonly IPAddress GoogleMetadataV6 = new([0xfd, 0x20, 0x00, 0xce, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x02, 0x54]);   // fd20:ce::254

    // The IPv4 address inside an IPv6 one, else null: 2002::/16 (6to4, bytes 2-5); 2001::/32 (Teredo: the client's
    // address in the last four bytes, each bit flipped); and in the last four bytes,
    // 64:ff9b::/96 (NAT64), 64:ff9b:1::/48 (local-use NAT64, RFC 8215), ::ffff:0:0/96 (SIIT), ::/96 (IPv4-compatible,
    // not :: or ::1) and an ISATAP interface id (bytes 8-11 00 00 5e fe or 02 00 5e fe, on any prefix).
    private static IPAddress? EmbeddedIPv4(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetworkV6) return null;
        var b = ip.GetAddressBytes();
        if (b[0] == 0x20 && b[1] == 0x02) return new IPAddress(b.AsSpan(2, 4));
        if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0 && b[3] == 0)
            return new IPAddress([(byte)~b[12], (byte)~b[13], (byte)~b[14], (byte)~b[15]]);
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

    /// <summary>What the address is ("a loopback address").</summary>
    public string Why { get; } = why;
}
