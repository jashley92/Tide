using FluentAssertions;
using Tide.Application.Indicators;
using Tide.Domain.Enums;
using Tide.Domain.Observables;

namespace Tide.UnitTests;

public class IocExtractorTests
{
    [Fact]
    public void Extract_FindsDefangedAndLiveIndicators_Refanged()
    {
        const string text = """
            The loader beacons to hxxps://cdn-update[.]example[.]ru/a.php and 203.0.113[.]45 (port 443).
            Dropper SHA-256: 9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08.
            Phishing came from billing@invoices-example[.]com.
            """;

        var found = IocExtractor.Extract(text);

        found.Indicators.Should().ContainEquivalentOf(new IocObservable.ParsedIndicator("https://cdn-update.example.ru/a.php", IndicatorType.Url));
        found.Indicators.Should().ContainEquivalentOf(new IocObservable.ParsedIndicator("203.0.113.45", IndicatorType.IpAddress));
        found.Indicators.Should().ContainEquivalentOf(new IocObservable.ParsedIndicator(
            "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08", IndicatorType.FileHash));
        found.Indicators.Should().ContainEquivalentOf(new IocObservable.ParsedIndicator("billing@invoices-example.com", IndicatorType.EmailAddress));
    }

    [Fact]
    public void Extract_FindsCves_Uppercased_Once()
    {
        var found = IocExtractor.Extract("Patch cve-2026-1234 now. CVE-2026-1234 is in KEV; CVE-2025-99999 too.");
        found.Cves.Should().Equal("CVE-2026-1234", "CVE-2025-99999");
    }

    [Theory]
    [InlineData("Built on ASP.NET with Node.js on the U.S. east coast, e.g. in Q3.")]
    [InlineData("See section 4.2 and version 1.2 of the report.")]
    public void Extract_LeavesProseAlone(string text)
    {
        IocExtractor.Extract(text).Indicators.Should().BeEmpty();
    }

    [Fact]
    public void Extract_KeepsADefangedDomainWithAnUncommonTld_ButNotALiveOne()
    {
        var found = IocExtractor.Extract("C2: evil-domain[.]lol and plain words.lol");
        found.Indicators.Select(i => i.Value).Should().Equal("evil-domain.lol");
    }

    [Fact]
    public void Extract_Dedupes()
    {
        IocExtractor.Extract("1.2.3.4, 1.2.3[.]4; 1[.]2[.]3[.]4").Indicators.Should().HaveCount(1);
    }

    [Theory]
    [InlineData("CVE-2026-0001", "CVE-2026-0001")]
    [InlineData(" cve-2026-12345 ", "CVE-2026-12345")]
    [InlineData("CVE-26-1", null)]
    [InlineData("see CVE-2026-0001", null)]
    public void NormalizeCve(string input, string? expected) => IocExtractor.NormalizeCve(input).Should().Be(expected);

    [Theory]
    [InlineData("https://evil.example.com/x", "hxxps://evil[.]example[.]com/x")]
    [InlineData("a@b.com", "a[at]b[.]com")]
    [InlineData("10.0.0.1", "10.0.0[.]1")]
    public void Defang_RoundTrips(string live, string defanged)
    {
        IocObservable.Defang(live).Should().Be(defanged);
        IocObservable.Refang(defanged).Should().Be(live);
    }

    [Theory]
    [InlineData("0x52908400098527886E0F7030069857D2E4169EE7", IndicatorType.CryptoWallet)]
    [InlineData("bc1qar0srrr7xfkvy5l643lydnw9re59gtzzwf5mdq", IndicatorType.CryptoWallet)]
    [InlineData("52908400098527886e0f7030069857d2e4169ee7", IndicatorType.FileHash)]
    [InlineData("0xdeadbeef", IndicatorType.Other)]
    public void DetectType_KnowsWalletAddressesOfAClearShape(string value, IndicatorType expected) =>
        IocObservable.DetectType(value).Should().Be(expected);

    [Fact]
    public void Defang_ByType_LeavesFileNamesHashesAndHostsAsTyped()
    {
        IocObservable.Defang(IndicatorType.FileName, "cook.pth").Should().Be("cook.pth");
        IocObservable.Defang(IndicatorType.Host, "ws-01.corp.example").Should().Be("ws-01.corp.example");
        IocObservable.Defang(IndicatorType.Domain, "cook.example").Should().Be("cook[.]example");
        IocObservable.Defang(IndicatorType.IpAddress, "203.0.113.45").Should().Be("203.0.113[.]45");
    }
}
