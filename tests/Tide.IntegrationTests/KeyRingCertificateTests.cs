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
    public void A_certificate_that_is_missing_or_has_no_private_key_is_refused_with_why_and_an_expiry_is_only_warned_of()
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
                .Should().Equal([current.Thumbprint, expired.Thumbprint], "an expired certificate still reads its keys");
            FluentActions.Invoking(() => KeyRingCertificate.Find(["00112233445566778899AABBCCDDEEFF00112233"], Open()))
                .Should().Throw<InvalidOperationException>().WithMessage("*isn't in the machine's Personal store*");
            FluentActions.Invoking(() => KeyRingCertificate.Find([publicOnly.Thumbprint], Open()))
                .Should().Throw<InvalidOperationException>().WithMessage("*has no private key*");
            KeyRingCertificate.Find([expired.Thumbprint], Open()).Should().ContainSingle("an expiry doesn't stop the site: Diagnostics warns of it");
            KeyRingCertificate.ExpiryNote(expired, DateTimeOffset.UtcNow).Warning.Should().Contain("expired on").And.Contain("PreviousThumbprints");
            KeyRingCertificate.ExpiryNote(current, DateTimeOffset.UtcNow).Warning.Should().BeNull();
            KeyRingCertificate.ExpiryNote(current, DateTimeOffset.UtcNow.AddYears(1).AddDays(-10)).Warning.Should().Contain("renew it soon");
        }
        finally
        {
            foreach (var c in mine) store.Remove(c);
        }
    }

    [Fact]
    public void Diagnostics_says_how_keys_are_protected_and_warns_before_the_certificate_expires()
    {
        KeyRingCertificate.Describe(Config(), DateTimeOffset.UtcNow, windows: true).Should().Be(("Protected with machine DPAPI: readable on this server only.", (string?)null));
        KeyRingCertificate.Describe(Config(), DateTimeOffset.UtcNow, windows: false).Note.Should().BeEmpty();

        using var cert = Make(DateTimeOffset.UtcNow.AddDays(20));
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        store.Add(cert);
        try
        {
            X509Store Open() => new(StoreName.My, StoreLocation.CurrentUser);
            var set = Config((KeyRingCertificate.ThumbprintKey, cert.Thumbprint));
            var (note, warning) = KeyRingCertificate.Describe(set, DateTimeOffset.UtcNow, windows: true, Open());
            note.Should().Contain($"certificate {cert.Thumbprint}").And.Contain("expires on");
            warning.Should().Contain("renew it soon");
            KeyRingCertificate.Describe(set, DateTimeOffset.UtcNow.AddYears(-1), windows: true, Open()).Warning.Should().BeNull("a year before, it isn't near");

            var (gone, why) = KeyRingCertificate.Describe(Config((KeyRingCertificate.ThumbprintKey, "00112233445566778899AABBCCDDEEFF00112233")), DateTimeOffset.UtcNow, true, Open());
            gone.Should().Contain("can't be read now");
            why.Should().Contain("isn't in the machine's Personal store");
        }
        finally { store.Remove(cert); }
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
