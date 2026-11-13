using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Tide.Application.Content;
using Tide.Application.Exports;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.UnitTests;

/// <summary>
/// The STIX bundle and the CSV against golden files (tests/Tide.UnitTests/Golden). To accept a deliberate change, run the
/// tests with TIDE_UPDATE_GOLDEN=1 and review the diff.
/// </summary>
public class ExportTests
{
    private static readonly DateTimeOffset Day1 = new(2026, 10, 26, 13, 0, 0, TimeSpan.Zero);

    private static ExportSet Fixture()
    {
        var vpn = new Article
        {
            Id = new Guid("11111111-1111-4111-8111-111111111111"), Title = "Edge VPN zero-day", Bluf = "Exploited for initial access.",
            SourceName = "Vendor", SourceUrl = "https://vendor.example/advisory", Tlp = TlpLevel.Clear,
            Classification = Classification.Threat, Priority = Priority.P1, CreatedAtUtc = Day1,
            Indicators =
            [
                new() { Type = IndicatorType.IpAddress, Value = "203.0.113.45", Note = "C2" },
                new() { Type = IndicatorType.Domain, Value = "Update-Check.example.net" },
                new() { Type = IndicatorType.Host, Value = "WS-0142" },
            ],
            Cves = [new() { CveId = "CVE-2026-0001" }],
            Techniques = [new() { TechniqueId = "T1190" }],
        };
        var lure = new Article
        {
            Id = new Guid("22222222-2222-4222-8222-222222222222"), Title = "Same infrastructure, new lure", Bluf = "",
            Tlp = TlpLevel.Amber, Classification = Classification.Informational, CreatedAtUtc = Day1.AddDays(1), ModifiedAtUtc = Day1.AddDays(2),
            Indicators =
            [
                new() { Type = IndicatorType.IpAddress, Value = "203.0.113.45", Note = "Still up" },
                new() { Type = IndicatorType.FileHash, Value = "275A021BBFB6489E54D471899F7DB9D1663FC695EC2FE2A2C4538AABF651FD0F" },
                new() { Type = IndicatorType.Url, Value = "https://login.example.com/o'wa" },
            ],
        };
        return new ExportSet("brief-2026-10-27", [vpn, lure],
            new Dictionary<Guid, IReadOnlyList<DateOnly>> { [vpn.Id] = [new DateOnly(2026, 10, 26), new DateOnly(2026, 10, 27)] });
    }

    private static void Golden(string name, string actual)
    {
        if (Environment.GetEnvironmentVariable("TIDE_UPDATE_GOLDEN") == "1")
        {
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Golden", name), actual);
            return;
        }
        actual.Replace("\r\n", "\n").Should().Be(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Golden", name)).Replace("\r\n", "\n"));
    }

    [Fact]
    public void Stix_MatchesTheGoldenBundle() => Golden("export.stix.json", Encoding.UTF8.GetString(Stix.Write(Fixture(), "Cyber Threat Intelligence")));

    [Fact]
    public void TrendArticlesCsv_LeavesTheArticleLinkOut_WhenTheServersAddressIsntSet()
    {
        var id = Guid.NewGuid();
        var article = new Tide.Application.Trends.TrendArticle(id, "Loader", new DateOnly(2026, 10, 26), true, TlpLevel.Green, Classification.Threat,
            Priority.P2, "Bottom line", null, null, [], [], 0);
        // Blank, a link would be "/articles/…": the site's root, not TIDE under a sub-path, and no link in a spreadsheet.
        var blank = Tide.Application.Trends.TrendsService.ArticlesCsv([article], "").Split('\n')[1].Split(',');
        blank[3].Should().BeEmpty();
        var set = Tide.Application.Trends.TrendsService.ArticlesCsv([article], "https://tide.example.org/tide/").Split('\n')[1].Split(',');
        set[3].Should().Be($"https://tide.example.org/tide/articles/{id}");
    }

    [Fact]
    public void Csv_MatchesTheGoldenFile() => Golden("export.csv", IndicatorCsv.Write(Fixture(), "https://tide.example.org/"));

    [Fact]
    public void Stix_SharesAnIndicatorAcrossReports_AtTheHighestTlp_AndLeavesOutUnmappedTypes()
    {
        using var doc = JsonDocument.Parse(Stix.Write(Fixture(), "CTI"));
        var objects = doc.RootElement.GetProperty("objects").EnumerateArray().ToList();
        objects.All(o => o.GetProperty("type").GetString() is "identity" or "marking-definition" || o.TryGetProperty("object_marking_refs", out _)).Should().BeTrue("every object but the identity and the markings is marked");
        var ip = objects.Single(o => o.GetProperty("type").GetString() == "indicator" && o.GetProperty("name").GetString() == "203.0.113.45");
        ip.GetProperty("object_marking_refs")[0].GetString().Should().Be(Stix.Marking(TlpLevel.Amber));
        ip.GetProperty("description").GetString().Should().Be("C2\nStill up");
        objects.Where(o => o.GetProperty("type").GetString() == "report")
            .Should().OnlyContain(r => r.GetProperty("object_refs").EnumerateArray().Any(x => x.GetString() == ip.GetProperty("id").GetString()));
        objects.Any(o => o.TryGetProperty("name", out var n) && n.GetString() == "WS-0142").Should().BeFalse("a host name has no STIX observable");
    }

    [Fact]
    public void Stix_CarriesEachTlpMarkingItRefersTo_AsTheTlp20ExtensionDefinesIt()
    {
        // The TLP 2.0 marking definitions aren't among STIX 2.1's own: a bundle that refers to them carries them.
        using var doc = JsonDocument.Parse(Stix.Write(Fixture(), "CTI"));
        var objects = doc.RootElement.GetProperty("objects").EnumerateArray().ToList();
        var used = objects.Where(o => o.TryGetProperty("object_marking_refs", out _))
            .SelectMany(o => o.GetProperty("object_marking_refs").EnumerateArray().Select(r => r.GetString())).Distinct().Order().ToList();
        var defined = objects.Where(o => o.GetProperty("type").GetString() == "marking-definition").ToList();

        defined.Select(d => d.GetProperty("id").GetString()).Order().Should().Equal(used, "each marking used, once, and no other");
        var amber = defined.Single(d => d.GetProperty("id").GetString() == Stix.Marking(TlpLevel.Amber));
        amber.GetProperty("name").GetString().Should().Be("TLP:AMBER");
        amber.GetProperty("spec_version").GetString().Should().Be("2.1");
        amber.GetProperty("created").GetString().Should().Be("2022-10-01T00:00:00.000Z");
        var ext = amber.GetProperty("extensions").GetProperty("extension-definition--60a3c5c5-0d10-413e-aab3-9e08dde9e88d");
        ext.GetProperty("extension_type").GetString().Should().Be("property-extension");
        ext.GetProperty("tlp_2_0").GetString().Should().Be("amber");
    }

    // An export of one article at tlp, pointing at one IP, one CVE and one technique.
    private static ExportSet OneArticle(TlpLevel tlp, string stem, Guid id) => new(stem,
    [
        new Article
        {
            Id = id, Title = "One", Tlp = tlp, Classification = Classification.Threat, CreatedAtUtc = Day1,
            Indicators = [new() { Type = IndicatorType.IpAddress, Value = "198.51.100.7" }],
            Cves = [new() { CveId = "CVE-2026-0002" }],
            Techniques = [new() { TechniqueId = "T1566" }],
        },
    ], new Dictionary<Guid, IReadOnlyList<DateOnly>>());

    private static Dictionary<string, string> SharedIds(ExportSet set)
    {
        using var doc = JsonDocument.Parse(Stix.Write(set, "CTI"));
        return doc.RootElement.GetProperty("objects").EnumerateArray()
            .Where(o => o.GetProperty("type").GetString() is "indicator" or "vulnerability" or "attack-pattern")
            .ToDictionary(o => o.GetProperty("type").GetString()!, o => o.GetProperty("id").GetString()!);
    }

    [Fact]
    public void Stix_SharedObjectIds_AreTheirTypeValueAndTlp()
    {
        // uuid5 of "type|value|tlp": the value is the pattern for an indicator, the CVE id, the technique id.
        var ids = SharedIds(OneArticle(TlpLevel.AmberStrict, "a", Guid.NewGuid()));
        ids["indicator"].Should().Be($"indicator--{Stix.Uuid5("indicator|[ipv4-addr:value = '198.51.100.7']|amber-strict")}");
        ids["vulnerability"].Should().Be($"vulnerability--{Stix.Uuid5("vulnerability|CVE-2026-0002|amber-strict")}");
        ids["attack-pattern"].Should().Be($"attack-pattern--{Stix.Uuid5("attack-pattern|T1566|amber-strict")}");
    }

    [Fact]
    public void Stix_SharedObjectIds_DifferByTlp_SoTwoMarkingsAreNeverOneObject()
    {
        // One export has the IP at CLEAR, another at AMBER: a platform importing both must see two objects, not two
        // conflicting versions of one.
        var clear = SharedIds(OneArticle(TlpLevel.Clear, "a", Guid.NewGuid()));
        var amber = SharedIds(OneArticle(TlpLevel.Amber, "a", Guid.NewGuid()));
        foreach (var type in clear.Keys) clear[type].Should().NotBe(amber[type], $"the {type} is marked differently");
    }

    [Fact]
    public void Stix_SharedObjectIds_StayTheSame_AcrossExportsAtTheSameTlp()
    {
        // A different export (another file, another article) at the same TLP: re-imports still merge.
        SharedIds(OneArticle(TlpLevel.Green, "brief-1", Guid.NewGuid()))
            .Should().Equal(SharedIds(OneArticle(TlpLevel.Green, "story-2", Guid.NewGuid())));
    }

    [Theory]
    [InlineData(IndicatorType.IpAddress, "203.0.113.45", "[ipv4-addr:value = '203.0.113.45']")]
    [InlineData(IndicatorType.IpAddress, "2001:db8::7", "[ipv6-addr:value = '2001:db8::7']")]
    [InlineData(IndicatorType.Domain, "Evil.Example", "[domain-name:value = 'evil.example']")]
    [InlineData(IndicatorType.Url, @"https://x.example/a'b\c", @"[url:value = 'https://x.example/a\'b\\c']")]
    [InlineData(IndicatorType.FileHash, "44D88612FEA8A8F36DE82E1278ABB02F", "[file:hashes.MD5 = '44d88612fea8a8f36de82e1278abb02f']")]
    [InlineData(IndicatorType.FileHash, "da39a3ee5e6b4b0d3255bfef95601890afd80709", "[file:hashes.'SHA-1' = 'da39a3ee5e6b4b0d3255bfef95601890afd80709']")]
    [InlineData(IndicatorType.EmailAddress, "billing@example.org", "[email-addr:value = 'billing@example.org']")]
    [InlineData(IndicatorType.Account, @"CORP\svc_backup", @"[user-account:account_login = 'CORP\\svc_backup']")]
    [InlineData(IndicatorType.RegistryKey, @"HKCU\Software\Run", @"[windows-registry-key:key = 'HKCU\\Software\\Run']")]
    [InlineData(IndicatorType.FileName, "invoice.exe", "[file:name = 'invoice.exe']")]
    [InlineData(IndicatorType.CommandLine, "cmd /c whoami", "[process:command_line = 'cmd /c whoami']")]
    [InlineData(IndicatorType.FileHash, "abc123", null)]
    [InlineData(IndicatorType.FileHash, "zz4d88612fea8a8f36de82e1278abb02", null)]
    [InlineData(IndicatorType.Host, "WS-0142", null)]
    [InlineData(IndicatorType.Other, "anything", null)]
    public void Pattern_MapsEachTypeAndEscapesTheLiteral(IndicatorType type, string value, string? pattern) =>
        Stix.Pattern(new ArticleIndicator { Type = type, Value = value }).Should().Be(pattern);

    [Fact]
    public void Uuid5_MatchesTheRfcExample_AndIsStable()
    {
        // RFC 9562 appendix A.4: "www.example.com" in the DNS namespace.
        Stix.Uuid5(new Guid("6ba7b810-9dad-11d1-80b4-00c04fd430c8"), "www.example.com").Should().Be(new Guid("2ed6657d-e927-568b-95e1-2665a8aea6a2"));
        Stix.Uuid5("indicator|x").Should().Be(Stix.Uuid5("indicator|x"));
    }

    [Theory]
    [InlineData("amber", TlpLevel.Amber)]
    [InlineData("amber-strict", TlpLevel.AmberStrict)]
    [InlineData("TLP:AMBER+STRICT", TlpLevel.AmberStrict)]
    [InlineData("white", TlpLevel.Clear)]
    [InlineData("red", TlpLevel.Red)]
    [InlineData("purple", null)]
    public void TlpParse_ReadsTheAddressForms(string text, TlpLevel? level)
    {
        Tlp.Parse(text).Should().Be(level);
        if (level is { } l) Tlp.Parse(Tlp.Key(l)).Should().Be(l, "a key reads back as its level");
    }

    [Fact]
    public void Csv_NeutralisesFormulas() => Csv.Cell("=HYPERLINK(\"x\")").Should().Be("\"'=HYPERLINK(\"\"x\"\")\"");
}
