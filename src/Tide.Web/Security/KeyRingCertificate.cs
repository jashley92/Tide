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
    /// its private key. Throws with a sentence an administrator can act on when one isn't there or has no private key:
    /// without it the ring can't be read. An expired one still works (Data Protection doesn't check the date), so its
    /// expiry is Diagnostics' to warn of (<see cref="ExpiryNote"/>), not a reason to stop the site at the next recycle.
    /// </summary>
    public static IReadOnlyList<X509Certificate2> Find(IReadOnlyList<string> thumbprints, X509Store? store = null)
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
            found.Add(cert);
        }
        return found;
    }

    /// <summary>When the certificate expires, and a warning when that's within <paramref name="soon"/> days or past.</summary>
    public static (DateTime NotAfter, string? Warning) ExpiryNote(X509Certificate2 cert, DateTimeOffset now, int soon = 30)
    {
        var left = cert.NotAfter.ToUniversalTime() - now.UtcDateTime;
        return (cert.NotAfter, left.TotalDays switch
        {
            < 0 => $"Its certificate expired on {cert.NotAfter:yyyy-MM-dd}; keys still read, but renew it: set the new thumbprint and move this one to DataProtection:PreviousThumbprints.",
            var d when d < soon => $"Its certificate expires on {cert.NotAfter:yyyy-MM-dd}: renew it soon.",
            _ => null,
        });
    }

    /// <summary>
    /// For Diagnostics: how new keys are protected (a certificate, readable on any server that holds it, with when it
    /// expires; or machine DPAPI on Windows, this server only), and a warning when the certificate expires within 30 days
    /// or has. A certificate that can't be found is said plainly (start-up would have stopped, so it's gone since).
    /// </summary>
    public static (string Note, string? Warning) Describe(IConfiguration config, DateTimeOffset now, bool windows, X509Store? store = null)
    {
        if (Thumbprints(config) is not [var current, ..])
            return (windows ? "Protected with machine DPAPI: readable on this server only." : "", null);
        try
        {
            var (at, warning) = ExpiryNote(Find([current], store)[0], now);
            return ($"Protected with certificate {current}, readable on any server that has it; it expires on {at:yyyy-MM-dd}.", warning);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Security.Cryptography.CryptographicException)
        {
            return ($"Protected with certificate {current}, which can't be read now.", ex.Message);
        }
    }

    private static string Normalize(string? thumbprint) =>
        new((thumbprint ?? "").Where(char.IsAsciiHexDigit).ToArray());
}
