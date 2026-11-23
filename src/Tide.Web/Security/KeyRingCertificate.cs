using System.Security.Cryptography.X509Certificates;

namespace Tide.Web.Security;

/// <summary>
/// The certificate that protects the Data Protection key ring when <c>DataProtection:CertificateThumbprint</c> is set: an
/// option to machine DPAPI, for a key ring a second (standby) server can read, or that survives a restore to a new
/// server. The certificate, with its private key, must be in the machine's Personal store (LocalMachine\My) on every
/// server, readable by the app pool's identity. <c>DataProtection:PreviousThumbprints</c> (comma-separated) names older
/// certificates whose keys may still be read after a renewal.
/// </summary>
public static class KeyRingCertificate
{
    public const string ThumbprintKey = "DataProtection:CertificateThumbprint";
    public const string PreviousKey = "DataProtection:PreviousThumbprints";

    /// <summary>The thumbprints configured: the current one first, then the previous ones; empty when it isn't set.</summary>
    public static IReadOnlyList<string> Thumbprints(IConfiguration config) =>
        [.. new[] { config[ThumbprintKey] }.Concat((config[PreviousKey] ?? "").Split(',', ';'))
            .Select(Normalize).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// The certificates for <paramref name="thumbprints"/> from LocalMachine\My (or <paramref name="store"/>), each with
    /// its private key. Throws with a sentence an administrator can act on when one isn't there, has no private key, or
    /// has expired (the current one only: an expired previous certificate still reads its old keys).
    /// </summary>
    public static IReadOnlyList<X509Certificate2> Find(IReadOnlyList<string> thumbprints, X509Store? store = null, DateTimeOffset? now = null)
    {
        using var opened = store ?? new X509Store(StoreName.My, StoreLocation.LocalMachine);
        opened.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        var found = new List<X509Certificate2>();
        for (var i = 0; i < thumbprints.Count; i++)
        {
            var match = opened.Certificates.Find(X509FindType.FindByThumbprint, thumbprints[i], validOnly: false);
            if (match.Count == 0)
                throw new InvalidOperationException($"The Data Protection certificate {thumbprints[i]} isn't in the machine's Personal store (LocalMachine\\My). " +
                    "Import it, with its private key, on this server, or remove the thumbprint from the settings.");
            var cert = match[0];
            if (!cert.HasPrivateKey)
                throw new InvalidOperationException($"The Data Protection certificate {thumbprints[i]} has no private key on this server: import it from its .pfx.");
            if (i == 0 && cert.NotAfter.ToUniversalTime() < (now ?? DateTimeOffset.UtcNow).UtcDateTime)
                throw new InvalidOperationException($"The Data Protection certificate {thumbprints[i]} expired on {cert.NotAfter:yyyy-MM-dd}. " +
                    "Issue a new one, set its thumbprint as DataProtection:CertificateThumbprint and move this one to DataProtection:PreviousThumbprints.");
            found.Add(cert);
        }
        return found;
    }

    private static string Normalize(string? thumbprint) =>
        new((thumbprint ?? "").Where(char.IsAsciiHexDigit).ToArray());
}
