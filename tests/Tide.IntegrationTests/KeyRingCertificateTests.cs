using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Tide.Web.Security;

namespace Tide.IntegrationTests;

/// <summary>
/// The key ring's certificate option (<c>DataProtection:CertificateThumbprint</c>): the thumbprints read from settings,
/// and a start that says plainly why it can't use the certificate. Certificates here are made for the test and put in the
/// current user's store, then removed.
/// </summary>
public class KeyRingCertificateTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] pairs) =>
        new ConfigurationBuilder().AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value))).Build();

    [Fact]
    public void The_current_thumbprint_comes_first_then_the_previous_ones_cleaned_and_once_each()
    {
        KeyRingCertificate.Thumbprints(Config()).Should().BeEmpty("off unless set");
        KeyRingCertificate.Thumbprints(Config((KeyRingCertificate.ThumbprintKey, "‎AB CD:12"), (KeyRingCertificate.PreviousKey, "ff00; abcd12 ,,ee")))
            .Should().Equal("ABCD12", "ff00", "ee");
    }

    [Fact]
    public void A_certificate_that_is_missing_has_no_private_key_or_has_expired_is_refused_with_why()
    {
        using var current = Make(DateTimeOffset.UtcNow.AddYears(1));
        using var expired = Make(DateTimeOffset.UtcNow.AddDays(-1));
        using var publicOnly = X509CertificateLoader.LoadCertificate(Make(DateTimeOffset.UtcNow.AddYears(1)).Export(X509ContentType.Cert));
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        var mine = new[] { current, expired, publicOnly };
        foreach (var c in mine) store.Add(c);
        try
        {
            X509Store Open() => new(StoreName.My, StoreLocation.CurrentUser);
            KeyRingCertificate.Find([current.Thumbprint, expired.Thumbprint], Open()).Select(c => c.Thumbprint)
                .Should().Equal([current.Thumbprint, expired.Thumbprint], "an expired previous certificate still reads its old keys");
            FluentActions.Invoking(() => KeyRingCertificate.Find(["00112233445566778899AABBCCDDEEFF00112233"], Open()))
                .Should().Throw<InvalidOperationException>().WithMessage("*isn't in the machine's Personal store*");
            FluentActions.Invoking(() => KeyRingCertificate.Find([publicOnly.Thumbprint], Open()))
                .Should().Throw<InvalidOperationException>().WithMessage("*has no private key*");
            FluentActions.Invoking(() => KeyRingCertificate.Find([expired.Thumbprint], Open()))
                .Should().Throw<InvalidOperationException>().WithMessage("*expired on*PreviousThumbprints*");
        }
        finally
        {
            foreach (var c in mine) store.Remove(c);
        }
    }

    private static X509Certificate2 Make(DateTimeOffset notAfter)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN=TIDE key ring test {Guid.NewGuid():N}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(notAfter.AddYears(-2), notAfter);
        // Exportable, so the store keeps the private key with it.
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
    }
}
