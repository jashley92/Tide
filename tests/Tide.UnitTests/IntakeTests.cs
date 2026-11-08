using System.Text;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application;
using Tide.Application.Intake;

namespace Tide.UnitTests;

public class UrlCanonicalizerTests
{
    [Theory]
    [InlineData("HTTPS://Example.COM/News/Story/?utm_source=x&utm_medium=rss&id=7#comments", "https://example.com/News/Story?id=7")]
    [InlineData("https://example.com:443/a?b=2&a=1", "https://example.com/a?a=1&b=2")]
    [InlineData("http://example.com/", "http://example.com")]
    [InlineData("https://example.com/a?fbclid=abc&gclid=def", "https://example.com/a")]
    [InlineData("https://example.com:8443/x", "https://example.com:8443/x")]
    public void Canonicalize_StripsTrackingAndNormalizes(string input, string expected) =>
        UrlCanonicalizer.Canonicalize(input).Should().Be(expected);

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://example.com/x")]
    [InlineData("/relative/path")]
    [InlineData("")]
    public void Canonicalize_RefusesWhatIsntAWebAddress(string input) => UrlCanonicalizer.Canonicalize(input).Should().BeNull();
}

public class TitleMatchTests
{
    [Fact]
    public void Similarity_SameStoryReworded_IsHigh_DifferentStory_IsLow()
    {
        var a = TitleMatch.Key("Ransomware gang exploits edge VPN zero-day against manufacturers");
        var b = TitleMatch.Key("Manufacturers hit as ransomware gang exploits edge VPN zero-day");
        var c = TitleMatch.Key("Browser vendor ships fix for actively exploited bug");
        TitleMatch.Similarity(a, b).Should().BeGreaterThanOrEqualTo(TitleMatch.Threshold);
        TitleMatch.Similarity(a, c).Should().BeLessThan(TitleMatch.Threshold);
    }

    [Fact]
    public void Key_DropsStopWordsAndPunctuation() =>
        TitleMatch.Key("The CVE-2026-1234 bug: a new flaw, in the VPN!").Should().Be("bug cve-2026-1234 flaw vpn");
}

public class FeedParserTests
{
    private static Stream Text(string s) => new MemoryStream(Encoding.UTF8.GetBytes(s));

    [Fact]
    public void Rss2_ReadsItems_StripsHtml_FindsCves_ParsesNamedZones()
    {
        const string rss = """
            <?xml version="1.0"?>
            <rss version="2.0"><channel><title>News</title>
              <item><title>Vendor fixes CVE-2026-1111 &amp; more</title><link>https://example.com/a?utm_source=rss</link>
                <pubDate>Mon, 26 Oct 2026 09:30:00 EDT</pubDate>
                <description>&lt;p&gt;An &lt;b&gt;actively&lt;/b&gt; exploited bug (cve-2026-2222).&lt;script&gt;alert(1)&lt;/script&gt;&lt;/p&gt;</description></item>
              <item><title></title><link>https://example.com/no-title</link></item>
            </channel></rss>
            """;
        var items = FeedParser.ParseFeed(Text(rss));
        items.Should().ContainSingle("an item without a title is skipped");
        var i = items[0];
        i.Title.Should().Be("Vendor fixes CVE-2026-1111 & more");
        i.Summary.Should().Be("An actively exploited bug (cve-2026-2222).");
        i.Cves.Should().Equal("CVE-2026-1111", "CVE-2026-2222");
        i.PublishedAtUtc.Should().Be(new DateTimeOffset(2026, 10, 26, 13, 30, 0, TimeSpan.Zero));
    }

    private static DateTimeOffset? PubDate(string date) => FeedParser.ParseFeed(Text(
        $"<rss version=\"2.0\"><channel><item><title>T</title><link>https://example.com/a</link><pubDate>{date}</pubDate></item></channel></rss>"))
        .Single().PublishedAtUtc;

    [Theory]
    [InlineData("Mon, 07 Nov 2026 10:00:00 GMT")]
    [InlineData("Mon, 07 Nov 2026 10:00:00 +0000")]
    [InlineData("Monday, 07 Nov 2026 05:00:00 EST")]
    public void Rss2_ADayNameThatIsntTheDatesWeekday_StillReadsTheDate(string date) =>
        PubDate(date).Should().Be(new DateTimeOffset(2026, 11, 7, 10, 0, 0, TimeSpan.Zero), "7 November 2026 is a Saturday, whatever the feed calls it");

    [Theory]
    [InlineData("Thu, 01 Jan 1970 00:00:00 GMT")]
    [InlineData("0001-01-01T00:00:00Z")]
    public void Rss2_AnEpochOrYearOneDate_IsNoDate(string date) =>
        PubDate(date).Should().BeNull("a feed writes it for an item it has no date for");

    [Fact]
    public void Atom_TakesTheAlternateLink()
    {
        const string atom = """
            <feed xmlns="http://www.w3.org/2005/Atom"><title>t</title>
              <entry><title type="html">Advisory</title><link rel="self" href="https://example.com/self"/><link rel="alternate" href="https://example.com/post"/>
                <updated>2026-10-25T12:00:00Z</updated><summary>Short.</summary></entry>
            </feed>
            """;
        var i = FeedParser.ParseFeed(Text(atom)).Single();
        i.Url.Should().Be("https://example.com/post");
        i.PublishedAtUtc.Should().Be(new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Rss10_IsRead()
    {
        const string rdf = """
            <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#" xmlns="http://purl.org/rss/1.0/" xmlns:dc="http://purl.org/dc/elements/1.1/">
              <item rdf:about="https://example.com/x"><title>Old style</title><link>https://example.com/x</link><dc:date>2026-10-20T08:00:00Z</dc:date></item>
            </rdf:RDF>
            """;
        FeedParser.ParseFeed(Text(rdf)).Single().Title.Should().Be("Old style");
    }

    [Fact]
    public void ADtdOrExternalEntity_IsRefused()
    {
        const string xxe = """
            <?xml version="1.0"?>
            <!DOCTYPE rss [<!ENTITY xxe SYSTEM "file:///c:/windows/win.ini">]>
            <rss version="2.0"><channel><item><title>&xxe;</title><link>https://example.com/</link></item></channel></rss>
            """;
        var act = () => FeedParser.ParseFeed(Text(xxe));
        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void AControlCharacterInOneItem_DoesntRefuseTheFeed()
    {
        // Pasted from Word (a vertical tab) and a terminal (escape), as a byte and as a reference; XML forbids both.
        var rss = "<?xml version=\"1.0\" encoding=\"utf-8\"?><rss version=\"2.0\"><channel>"
            + "<item><title>Pasted\u000Bfrom Word</title><link>https://example.com/1</link><description>Colour\u001B[0m codes \u0000here</description></item>"
            + "<item><title>Escaped &#x1B;[31mred&#x1B;[0m and &#11;tab</title><link>https://example.com/2</link></item>"
            + "<item><title>Café — an ordinary one</title><link>https://example.com/3</link></item>"
            + "</channel></rss>";

        var items = FeedParser.ParseFeed(Text(rss));

        items.Select(i => i.Title).Should().Equal("Pasted from Word", "Escaped [31mred [0m and tab", "Café — an ordinary one");
        items[0].Summary.Should().Be("Colour [0m codes here");
        FeedParser.ParseFeed(new MemoryStream(Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(rss.Replace("utf-8", "utf-16")
            .Replace("\u000B", " ").Replace("\u001B", " ").Replace("\u0000", " "))).ToArray())).Should().HaveCount(3, "a UTF-16 feed is read as it is");
    }

    [Fact]
    public void AnIso2022Feed_KeepsTheEscapesItsCharactersAreMadeOf()
    {
        // ISO-2022-JP switches into and out of Japanese with ESC sequences; made spaces, every Japanese run would be garbage.
        // The app's own setup registers the code-page encodings it needs (nothing else in the tests does).
        new ServiceCollection().AddApplication(new ConfigurationBuilder().Build());
        var rss = "<?xml version=\"1.0\" encoding=\"iso-2022-jp\"?><rss version=\"2.0\"><channel>"
            + "<item><title>脆弱性情報 JVN#12345678</title><link>https://example.jp/1</link></item></channel></rss>";
        var bytes = Encoding.GetEncoding("iso-2022-jp").GetBytes(rss);
        bytes.Should().Contain(0x1B);

        FeedParser.ParseFeed(new MemoryStream(bytes)).Single().Title.Should().Be("脆弱性情報 JVN#12345678");
    }

    [Theory]
    [InlineData("shift_jis", "脆弱性情報")]
    [InlineData("gb2312", "漏洞通告")]
    [InlineData("euc-kr", "보안 권고")]
    [InlineData("windows-1251", "Уязвимость")]
    public void AFeedInACodePage_IsRead_OnceTheAppIsSetUp(string encoding, string title)
    {
        new ServiceCollection().AddApplication(new ConfigurationBuilder().Build());
        var rss = $"<?xml version=\"1.0\" encoding=\"{encoding}\"?><rss version=\"2.0\"><channel>"
            + $"<item><title>{title}</title><link>https://example.com/1</link></item></channel></rss>";

        FeedParser.ParseFeed(new MemoryStream(Encoding.GetEncoding(encoding).GetBytes(rss))).Single().Title.Should().Be(title);
    }

    [Fact]
    public void NotAFeed_IsRefused()
    {
        var act = () => FeedParser.ParseFeed(Text("<html><body>hi</body></html>"));
        act.Should().Throw<FormatException>().WithMessage("*RSS or Atom*");
    }

    [Fact]
    public void Kev_IsReadAndTurnedIntoItems()
    {
        const string kev = """
            {"title":"CISA Catalog","vulnerabilities":[
              {"cveID":"CVE-2026-0001","vendorProject":"Example","product":"Edge VPN","vulnerabilityName":"Auth bypass","dateAdded":"2026-10-24",
               "shortDescription":"Lets an attacker in.","dueDate":"2026-11-14","knownRansomwareCampaignUse":"Known"},
              {"cveID":"not-a-cve"}]}
            """;
        var entries = FeedParser.ParseKev(Text(kev));
        var e = entries.Single();
        e.Ransomware.Should().BeTrue();
        e.Due.Should().Be(new DateOnly(2026, 11, 14));
        var item = FeedParser.KevItems(entries).Single();
        item.Title.Should().Be("CVE-2026-0001: Example Edge VPN: Auth bypass");
        item.Url.Should().Be("https://nvd.nist.gov/vuln/detail/CVE-2026-0001");
        item.Summary.Should().Contain("federal due date 2026-11-14").And.Contain("Known ransomware use");
    }

    [Fact]
    public void Kev_AndEpss_SkipEntriesThatArentObjects_InsteadOfThrowing()
    {
        FeedParser.ParseKev(Text("""{"vulnerabilities":[1,"x",null,{"cveID":"CVE-2026-0002"}]}"""))
            .Select(e => e.CveId).Should().Equal("CVE-2026-0002");
        CveParsers.ParseEpss(Text("""{"data":[7,{"cve":"CVE-2026-0002","epss":"0.5"}]}"""))
            .Select(e => e.CveId).Should().Equal("CVE-2026-0002");
    }

    [Fact]
    public void Epss_AndNvd_AreRead()
    {
        var epss = CveParsers.ParseEpss(Text("""{"status":"OK","data":[{"cve":"CVE-2026-0001","epss":"0.94210","percentile":"0.99900"}]}"""));
        epss.Single().Score.Should().Be(0.94210m);

        const string nvd = """
            {"vulnerabilities":[{"cve":{"id":"CVE-2026-0001","published":"2026-10-20T10:15:00.000",
              "descriptions":[{"lang":"es","value":"no"},{"lang":"en","value":"An auth bypass."}],
              "metrics":{"cvssMetricV31":[{"type":"Secondary","cvssData":{"baseScore":7.5,"baseSeverity":"HIGH","vectorString":"v2nd"}},
                                          {"type":"Primary","cvssData":{"baseScore":9.8,"baseSeverity":"CRITICAL","vectorString":"CVSS:3.1/AV:N"}}],
                         "cvssMetricV2":[{"cvssData":{"baseScore":5.0,"vectorString":"old"},"baseSeverity":"MEDIUM"}]}}}]}
            """;
        var n = CveParsers.ParseNvd(Text(nvd))!;
        n.Description.Should().Be("An auth bypass.");
        n.Score.Should().Be(9.8m, "the primary CVSS 3.1 score wins over v2 and a secondary one");
        n.Severity.Should().Be("CRITICAL");
        CveParsers.ParseNvd(Text("""{"vulnerabilities":[]}""")).Should().BeNull();
    }
}

public class CategoryFilterTests
{
    private static Stream Text(string s) => new MemoryStream(Encoding.UTF8.GetBytes(s));

    [Fact]
    public void TheParser_KeepsEachFormatsTags_AsPlainText_OnceEach()
    {
        var rss = """
            <rss version="2.0" xmlns:dc="http://purl.org/dc/elements/1.1/"><channel><item><title>A</title><link>https://e.com/a</link>
            <category><![CDATA[Security]]></category><category>security</category><category domain="x">Microsoft &amp; Windows</category>
            <dc:subject>Ransomware</dc:subject></item></channel></rss>
            """;
        FeedParser.ParseFeed(Text(rss)).Single().Categories.Should().Equal("Security", "Microsoft & Windows", "Ransomware");

        var atom = """
            <feed xmlns="http://www.w3.org/2005/Atom"><entry><title>B</title><link href="https://e.com/b"/>
            <category term="vuln" label="Vulnerabilities"/><category term="cloud"/></entry></feed>
            """;
        FeedParser.ParseFeed(Text(atom)).Single().Categories.Should().Equal("Vulnerabilities", "cloud");

        FeedParser.ParseFeed(Text("<rss><channel><item><title>C</title><link>https://e.com/c</link></item></channel></rss>"))
            .Single().Categories.Should().BeEmpty();
    }

    private static ParsedItem Item(string title, params string[] tags) => new(title, "https://e.com", null, null, []) { Categories = tags };

    private static FeedFilter Filter(string onlyTags = "", string skipTags = "", string onlyWords = "", string skipWords = "") =>
        new(CategoryFilter.Parse(onlyTags), CategoryFilter.Parse(skipTags), CategoryFilter.Parse(onlyWords), CategoryFilter.Parse(skipWords));

    [Theory]
    [InlineData("Security", "", true)]
    [InlineData("Technology", "", false)]
    [InlineData("Security, Deals", "", true)]
    [InlineData("", "Deals", false)]
    [InlineData("", "", true)]
    [InlineData("Security", "deals", false)]
    [InlineData("Securi", "", false)]
    public void ByTags_AnItemIsKept_WhenItHasAnOnlyTag_AndNoSkipTag(string only, string skip, bool kept) =>
        Filter(only, skip).Keeps(Item("Anything", "  SECURITY ", "Deals")).Should().Be(kept);

    [Fact]
    public void ATagPaddedInsideCData_AsBleepingComputerWritesIt_ReadsAndMatchesWithoutTheSpaces()
    {
        var rss = """
            <rss version="2.0"><channel><item><title>Hackers exploit a zero-day</title><link>https://e.com/z</link>
            <category><![CDATA[ Security ]]></category><category><![CDATA[
              Microsoft
            ]]></category><category><![CDATA[ Patch&nbsp;Tuesday ]]></category></item></channel></rss>
            """;
        var item = FeedParser.ParseFeed(Text(rss)).Single();

        item.Categories.Should().Equal("Security", "Microsoft", "Patch Tuesday");
        Filter(onlyTags: "Security").Keeps(item).Should().BeTrue();
        Filter(onlyTags: " security ").Keeps(item).Should().BeTrue();
        CategoryFilter.Seen([item]).Should().Be("Microsoft\nPatch Tuesday\nSecurity", "the form offers the tag without its padding");
    }

    [Fact]
    public void WithAnOnlyList_AnUntaggedItemIsLeftOut_WithoutOne_ItsKept()
    {
        Filter("Security").Keeps(Item("x")).Should().BeFalse();
        Filter(skipTags: "Deals").Keeps(Item("x")).Should().BeTrue();
    }

    [Theory]
    [InlineData("New Ransomware gang hits hospitals", "ransomware", true)]
    [InlineData("Anti-ransomware tool released", "ransomware", true)]
    [InlineData("Ransomwares compared", "ransomware", false)]
    [InlineData("Chrome Zero-Day exploited", "zero-day", true)]
    [InlineData("Microsoft's  Patch   Tuesday fixes 60 flaws", "patch tuesday", true)]
    [InlineData("Fix for CVE-2026-1234 out", "CVE", true)]
    [InlineData("Fix for CVE-2026-1234 out", "2026-1234", true)]
    [InlineData("Dealing with phishing", "deal", false)]
    [InlineData("C++ library flaw", "c++", true)]
    public void TitleWords_MatchWholeWords_IgnoringCaseAndSpacing(string title, string phrase, bool match) =>
        FeedFilter.Mentions(title, phrase).Should().Be(match);

    [Fact]
    public void OnlyTagsOrOnlyWords_EitherLetsAnItemIn_AndAnySkipLeavesItOut()
    {
        var filter = Filter(onlyTags: "Security", onlyWords: "ransomware, zero-day", skipTags: "Deals", skipWords: "webinar");

        filter.Keeps(Item("Patch now", "Security")).Should().BeTrue("it has an only-tag");
        filter.Keeps(Item("Ransomware gang returns", "Technology")).Should().BeTrue("its title has an only-word");
        filter.Keeps(Item("Ransomware gang returns")).Should().BeTrue("an untagged item can still pass on its title");
        filter.Keeps(Item("New phone launched", "Technology")).Should().BeFalse();
        filter.Keeps(Item("Zero-day VPN deal", "Security", "Deals")).Should().BeFalse("a skip-tag wins");
        filter.Keeps(Item("Ransomware webinar next week", "Security")).Should().BeFalse("a skip-word wins");
        Filter(skipWords: "webinar").Keeps(Item("Anything at all")).Should().BeTrue("with no only side, everything not skipped passes");
    }

    [Fact]
    public void TypedTags_AreSplitTrimmedAndStoredOncePerLine()
    {
        CategoryFilter.Store(" Security,security ; Microsoft\nCloud ,, ").Should().Be("Security\nMicrosoft\nCloud");
        CategoryFilter.Store(" , ").Should().BeNull();
        CategoryFilter.Show("Security\nMicrosoft").Should().Be("Security, Microsoft");
    }

    [Fact]
    public void SeenTags_AreMostUsedFirst()
    {
        CategoryFilter.Seen([Item("t", "Technology", "Security"), Item("t", "security"), Item("t", "Deals")]).Should().Be("Security\nDeals\nTechnology");
        CategoryFilter.Seen([Item("t")]).Should().BeNull();
    }
}

/// <summary>WordPress's "The post … appeared first on …" footer leaves a summary before the excerpt is cut, and nothing else does.</summary>
public class FeedFooterTests
{
    private static Stream Text(string s) => new MemoryStream(Encoding.UTF8.GetBytes(s));

    // As SecurityWeek's feed gives it: the summary, a "[…]" trailer, then the footer, its title and site as links.
    private const string SecurityWeek = """
        <rss version="2.0"><channel><title>SecurityWeek</title>
          <item><title>Chrome 130 Update Patches High-Severity Vulnerabilities</title><link>https://www.securityweek.com/chrome-130-update/</link>
            <description>&lt;p&gt;Google has released a Chrome 130 update that resolves three high-severity vulnerabilities reported by external researchers.&lt;/p&gt;
        &lt;p&gt;The post &lt;a href="https://www.securityweek.com/chrome-130-update/"&gt;Chrome 130 Update Patches High-Severity Vulnerabilities&lt;/a&gt; appeared first on &lt;a href="https://www.securityweek.com"&gt;SecurityWeek&lt;/a&gt;.&lt;/p&gt;</description></item>
          <item><title>Ransomware Gang Claims Hospital Attack</title><link>https://www.securityweek.com/ransomware-hospital/</link>
            <description>&lt;p&gt;The group posted samples of stolen data on its leak site [&amp;#8230;]&lt;/p&gt;
        &lt;p&gt;The post &lt;a href="https://www.securityweek.com/ransomware-hospital/"&gt;Ransomware Gang Claims Hospital Attack&lt;/a&gt; appeared first on &lt;a href="https://www.securityweek.com"&gt;SecurityWeek&lt;/a&gt;.&lt;/p&gt;</description></item>
        </channel></rss>
        """;

    [Fact]
    public void SecurityWeek_LosesTheFooter_AndKeepsATrailer()
    {
        var items = FeedParser.ParseFeed(Text(SecurityWeek));
        items[0].Summary.Should().Be("Google has released a Chrome 130 update that resolves three high-severity vulnerabilities reported by external researchers.");
        items[1].Summary.Should().Be("The group posted samples of stolen data on its leak site […]", "a \"[…]\" trailer is fine to keep");
    }

    [Fact]
    public void TheHackerNews_HasNoFooter_AndKeepsItsSummary()
    {
        const string thn = """
            <rss version="2.0"><channel><title>The Hacker News</title>
              <item><title>New Botnet Hijacks Routers via CVE-2026-1111</title><link>https://thehackernews.com/2026/10/new-botnet.html</link>
                <description><![CDATA[Cybersecurity researchers have flagged a new botnet that exploits CVE-2026-1111 in end-of-life routers. The post-exploitation payload appeared first on a Telegram channel.]]></description></item>
            </channel></rss>
            """;
        FeedParser.ParseFeed(Text(thn)).Single().Summary.Should()
            .Be("Cybersecurity researchers have flagged a new botnet that exploits CVE-2026-1111 in end-of-life routers. The post-exploitation payload appeared first on a Telegram channel.",
                "\"The post-exploitation\" isn't \"The post \", so a real last sentence stays");
    }

    [Theory]
    [InlineData("The post Some Title appeared first on The Hacker News.", "")]
    [InlineData("Story. The post Some Title appeared first on example.com.", "Story.")]
    [InlineData("Story [\u2026] The post Some Title appeared first on SecurityWeek", "Story [\u2026]")]
    [InlineData("The post office said it appeared first on Monday, then spread. More text follows here.", "The post office said it appeared first on Monday, then spread. More text follows here.")]
    [InlineData("Story. The post Some Title appeared first on SecurityWeek. Read more on our site.", "Story. The post Some Title appeared first on SecurityWeek. Read more on our site.")]
    [InlineData("A story where the post Some Title appeared first on SecurityWeek.", "A story where the post Some Title appeared first on SecurityWeek.")]
    [InlineData("", "")]
    public void OnlyAFinalFooterGoes(string text, string expected) => FeedParser.WithoutFooter(text).Should().Be(expected);

    [Fact]
    public void TheFooterGoes_BeforeTheExcerptIsCut()
    {
        var story = string.Join(' ', Enumerable.Repeat("word", 500));   // cut to just under the excerpt limit: over it with the footer
        story = story[..(Tide.Domain.Entities.FeedItem.MaxExcerpt - 20)].TrimEnd() + ".";
        var rss = $"""
            <rss version="2.0"><channel><item><title>T</title><link>https://e.com/t</link>
              <description>{story} The post T appeared first on SecurityWeek.</description></item></channel></rss>
            """;
        FeedParser.ParseFeed(Text(rss)).Single().Summary.Should().Be(story, "the story fits once the footer's gone, so it isn't cut");
    }
}

/// <summary>The promote form's suggested priority (CveService.SuggestedPriority): P1 for KEV or a critical CVE said to be
/// exploited, P2 for a high EPSS or a critical CVSS, nothing otherwise; always with its reason.</summary>
public class SuggestedPriorityTests
{
    private static readonly DateOnly Today = new(2026, 10, 30);

    private static Tide.Domain.Entities.CveRecord Cve(decimal? cvss = null, decimal? epss = null, bool kev = false, DateOnly? added = null) =>
        new() { CveId = "CVE-2026-" + Random.Shared.Next(1000, 9999), CvssScore = cvss, EpssScore = epss, IsKev = kev, KevAddedDate = added };

    private static PrioritySuggestion? Suggest(string title, params Tide.Domain.Entities.CveRecord[] records) =>
        CveService.SuggestedPriority(records, title, Today);

    [Fact]
    public void OnKev_IsP1_WithItsCvss() =>
        Suggest("Vendor patches VPN bug", Cve(10m, kev: true, added: Today.AddDays(-3)), Cve(5m))
            .Should().Be(new PrioritySuggestion(Tide.Domain.Enums.Priority.P1, "on KEV, CVSS 10"));

    [Fact]
    public void AnOldKevListing_SaysWhen() =>
        Suggest("Roundup: patch these", Cve(7.5m, kev: true, added: new DateOnly(2023, 3, 14)))
            .Should().Be(new PrioritySuggestion(Tide.Domain.Enums.Priority.P1, "on KEV since Mar 2023"));

    [Fact]
    public void ACriticalCve_SaidToBeExploited_IsP1() =>
        Suggest("Hackers Exploit Critical Fortinet Flaw", Cve(9.8m, epss: 0.02m))
            .Should().Be(new PrioritySuggestion(Tide.Domain.Enums.Priority.P1, "CVSS 9.8, \"exploit\" in the title"));

    [Theory]
    [InlineData("Critical flaw not yet exploited, vendor says")]
    [InlineData("No evidence of exploitation for the critical bug")]
    [InlineData("Vendor fixes a critical bug")]
    public void ACriticalCve_NotSaidToBeExploited_IsP2(string title) =>
        Suggest(title, Cve(9.1m)).Should().Be(new PrioritySuggestion(Tide.Domain.Enums.Priority.P2, "CVSS 9.1"));

    [Fact]
    public void AHighEpss_IsP2() =>
        Suggest("Exploit code published for a router bug", Cve(7.2m, epss: 0.34m))
            .Should().Be(new PrioritySuggestion(Tide.Domain.Enums.Priority.P2, "EPSS 34%"), "exploit in the title alone isn't P1 below CVSS 9");

    [Fact]
    public void AHighEpss_AndACriticalCvss_GiveBoth() =>
        Suggest("Patch now", Cve(9m, epss: 0.1m)).Should().Be(new PrioritySuggestion(Tide.Domain.Enums.Priority.P2, "EPSS 10%, CVSS 9"));

    [Fact]
    public void NothingNotable_OrNothingKnown_SuggestsNothing()
    {
        Suggest("Vendor fixes a bug", Cve(6.5m, epss: 0.01m)).Should().BeNull();
        Suggest("Vendor fixes a bug", Cve()).Should().BeNull();
        Suggest("Exploited zero-day").Should().BeNull("no CVE data is loaded");
    }
}
