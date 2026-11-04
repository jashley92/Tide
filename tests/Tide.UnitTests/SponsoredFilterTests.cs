using System.Text;
using FluentAssertions;
using Tide.Application.Intake;
using Tide.Application.Settings;

namespace Tide.UnitTests;

public class FeedAuthorTests
{
    private static Stream Text(string s) => new MemoryStream(Encoding.UTF8.GetBytes(s));

    [Fact]
    public void Rss2_ReadsDcCreator_AndAuthor_AsPlainText_AndTheSource()
    {
        var items = FeedParser.ParseFeed(Text("""
            <rss version="2.0" xmlns:dc="http://purl.org/dc/elements/1.1/"><channel>
              <item><title>From Beginner to Bug Hunter</title><link>https://e.com/1</link><dc:creator><![CDATA[ Sponsored by <b>TryHackMe</b> ]]></dc:creator></item>
              <item><title>Patch now</title><link>https://e.com/2</link><author>jane@example.com (Jane Doe)</author><source url="https://p.example/rss">Partner wire</source></item>
              <item><title>No byline</title><link>https://e.com/3</link></item>
            </channel></rss>
            """));
        items.Select(i => i.Author).Should().Equal("Sponsored by TryHackMe", "jane@example.com (Jane Doe)", null);
        items[1].Origin.Should().Be("Partner wire");
        items[0].Origin.Should().BeNull();
    }

    [Fact]
    public void Atom_ReadsEachAuthorsName_AndRss10_ItsCreator_KeptShort()
    {
        var atom = FeedParser.ParseFeed(Text($"""
            <feed xmlns="http://www.w3.org/2005/Atom"><entry><title>One</title><link href="https://e.com/a"/>
              <author><name>Ann</name><email>ann@e.com</email></author><author><name>Bob</name></author><author><name>{new string('x', 400)}</name></author>
            </entry></feed>
            """)).Single();
        atom.Author.Should().StartWith("Ann, Bob, xxx").And.HaveLength(FeedParser.MaxAuthor);

        var rdf = FeedParser.ParseFeed(Text("""
            <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#" xmlns="http://purl.org/rss/1.0/" xmlns:dc="http://purl.org/dc/elements/1.1/">
              <item><title>Two</title><link>https://e.com/b</link><dc:creator>Paid post</dc:creator></item>
            </rdf:RDF>
            """)).Single();
        rdf.Author.Should().Be("Paid post");
    }
}

public class SponsoredFilterTests
{
    private static readonly SponsoredFilter Defaults = new(SponsoredFilter.Parse(SponsoredFilter.DefaultMarkers), SponsoredFilter.Parse(SponsoredFilter.DefaultExceptions));

    private static ParsedItem Item(string title, string? author = null, string? summary = null, params string[] tags) =>
        new(title, "https://e.com/x", null, summary, []) { Author = author, Categories = tags };

    [Fact]
    public void TheDefaults_CatchTheBleepingComputerItem_ByItsCreator()
    {
        var item = Item("From Beginner to Bug Hunter: Learning Cybersecurity in the AI Era.", "Sponsored by TryHackMe",
            "AI-generated vulnerability reports are overwhelming ... [...]", "Security");
        Defaults.Match(item).Should().Be(new SponsoredMatch("Sponsored by", "Author"));
    }

    [Theory]
    [InlineData("[Sponsored] Five ways to harden your SOC")]
    [InlineData("Sponsored: Why identity is the new perimeter")]
    [InlineData("SPONSORED CONTENT - The state of ransomware 2026")]
    [InlineData("Paid Post: Securing the supply chain")]
    [InlineData("Partner content: zero trust in practice")]
    [InlineData("Advertorial — Cloud security made simple")]
    public void TheDefaults_CatchSponsoredTitles(string title) => Defaults.Match(Item(title)).Should().NotBeNull().And.Match<SponsoredMatch>(m => m.Field == "Title");

    [Fact]
    public void ATag_AndTheStartOfTheSummary_AreLookedAt_ButNotTheRestOfIt()
    {
        Defaults.Match(Item("Cloud posture", tags: ["Security", "Advertorial"])).Should().Be(new SponsoredMatch("Advertorial", "Category"));
        Defaults.Match(Item("Cloud posture", summary: "Sponsored: a look at posture management.")).Should().Be(new SponsoredMatch("Sponsored", "Summary"));
        Defaults.Match(Item("Cloud posture", summary: new string('a', 200) + " sponsored")).Should().BeNull("only the first 120 characters count");
    }

    [Theory]
    [InlineData("State-sponsored hackers breach telecom giants")]
    [InlineData("Chinese state-sponsored group exploits Ivanti zero-day")]
    [InlineData("Nation-state-sponsored APT targets European diplomats")]
    [InlineData("Nation-state sponsored actors abuse OAuth apps")]
    [InlineData("North Korean state sponsored hackers steal $1.5 billion")]
    [InlineData("Government-sponsored spyware found on activists' phones")]
    [InlineData("Iranian state–sponsored crew wipes Albanian systems")]
    public void TheExceptions_StopStateSponsoredHeadlines(string title)
    {
        Defaults.Match(Item(title, tags: ["Security"])).Should().BeNull();
        Defaults.Check(title).StoppedBy.Should().NotBeNull();
    }

    [Theory]
    [InlineData("Iran-sponsored hackers target Israeli water utilities", null)]
    [InlineData("China-sponsored APT41 exploits Ivanti flaw", null)]
    [InlineData("Iranian-Sponsored Group Pioneer Kitten Sells Access", null)]
    [InlineData("DPRK‑sponsored IT workers infiltrate firms", null)] // a non-breaking hyphen
    [InlineData("Hamas–sponsored crew phishes journalists", null)]
    [InlineData("Chinese sponsored group breaches telecoms", null)]
    [InlineData("Attacks sponsored by the North Korean regime steal $1.5B", null)]
    [InlineData("Cloud posture", "The campaign, sponsored by Beijing's MSS, targeted ministries across Asia.")]
    [InlineData("Wiper hits Albania", "Sponsored by Tehran, the group has wiped servers before.")]
    public void ANamedStateSponsor_IsNews_WhateverTheExceptionsSay(string title, string? summary)
    {
        Defaults.Match(Item(title, summary: summary, tags: ["Security"])).Should().BeNull();
        new SponsoredFilter(SponsoredFilter.Parse(SponsoredFilter.DefaultMarkers), []).Match(Item(title, summary: summary)).Should().BeNull("no exception is needed");
        Defaults.Check(summary ?? title).StoppedBy.Should().NotBeNull();
    }

    [Theory]
    [InlineData("Sponsored", "Sponsored")]
    [InlineData("Sponsored content", "Sponsored content")]
    [InlineData("Sponsored by Acme Security", "Sponsored by")]
    [InlineData("This post is sponsored by TryHackMe", "Sponsored by")]
    [InlineData("Sponsored by Acme: why state-sponsored threats need MDR", "Sponsored by")]
    [InlineData("Sponsored by the Acme team", "Sponsored by")]
    [InlineData("Iran-sponsored threats, explained | Sponsored by Acme", "Sponsored by")]
    [InlineData("Vendor-sponsored content: 5 ways to stop ransomware", "Sponsored content")] // a sponsor joined on that isn't a state
    [InlineData("Partner-sponsored post", "Sponsored post")]
    [InlineData("Co-sponsored by Acme and Beta", "Sponsored by")]
    [InlineData("Sponsored by Google Threat Intelligence", "Sponsored by")] // a state word in a company's name
    [InlineData("Sponsored by American Express", "Sponsored by")]
    [InlineData("Sponsored by the State of Cloud Security report", "Sponsored by")]
    [InlineData("Sponsored by British Airways", "Sponsored by")]
    [InlineData("Sponsored by China Mobile", "Sponsored by")]
    [InlineData("Sponsored by US Bank", "Sponsored by")]
    [InlineData("Threat Intelligence-sponsored webinar: defend your cloud", "Sponsored")]
    [InlineData("Sponsored by Acme and covers nation-state threats", "Sponsored by")] // a state noun far from "by"
    [InlineData("This post is sponsored by us", "Sponsored by")]
    public void Advertorials_AreStillCaught(string text, string marker)
    {
        Defaults.Check(text).Marker.Should().Be(marker);
        new SponsoredFilter(SponsoredFilter.Parse(SponsoredFilter.DefaultMarkers), []).Check(text).Marker.Should().Be(marker);
    }

    [Theory]
    [InlineData("Sponsored by the Chinese government, the group hit telecoms", "Sponsored by the Chinese")]
    [InlineData("Operations sponsored by Russia, officials say", "sponsored by Russia,")]
    [InlineData("Attacks sponsored by Iran's intelligence ministry", "sponsored by Iran's")]
    [InlineData("Campaign sponsored by Saudi Arabia targets dissidents", "sponsored by Saudi Arabia")]
    [InlineData("Kremlin-sponsored trolls amplify the leak", "Kremlin-sponsored")]
    [InlineData("Espionage campaign sponsored by a foreign government hits telecoms", "sponsored by a foreign government")]
    [InlineData("Wiper attack sponsored by a nation-state, researchers say", "sponsored by a nation")]
    [InlineData("Hackers sponsored by the US government breached the agency", "sponsored by the US")]
    [InlineData("Operation sponsored by the state targets dissidents", "sponsored by the state")]
    [InlineData("Hackers Sponsored By A Foreign Government Hit Telecoms", "Sponsored By A Foreign Government")]
    [InlineData("Spyware sponsored by the United States, leaked files show", "sponsored by the United States,")]
    [InlineData("Campaign sponsored by its military intelligence", "sponsored by its military")]
    [InlineData("Attacks sponsored by Russia's GRU hit grids", "sponsored by Russia's")]
    [InlineData("Phishing sponsored by Iran's Ministry of Intelligence", "sponsored by Iran's")]
    [InlineData("Heists sponsored by North Korea's Lazarus Group drain exchanges", "sponsored by North Korea's")]
    [InlineData("Intrusions sponsored by China's Volt Typhoon reach utilities", "sponsored by China's")]
    [InlineData("Sponsored by India's Infosys", "Sponsored by India's")] // a possessive country always counts: a missed advert, not dropped news
    [InlineData("U.S.-sponsored malware found in the wild", "U.S. sponsored")]
    [InlineData("Nation-State-sponsored crew hits Asia", "State-sponsored")]
    public void AStateNamedAfterBy_OrJoinedOn_IsStillNews(string text, string stoppedBy) =>
        new SponsoredFilter(SponsoredFilter.Parse(SponsoredFilter.DefaultMarkers), []).Check(text).Should().Be(new SponsoredCheck(null, stoppedBy));

    [Fact]
    public void TheStateThatStoppedAMarker_IsShown()
    {
        Defaults.Check("Iran-sponsored hackers").Should().Be(new SponsoredCheck(null, "Iran-sponsored"));
        Defaults.Check("Attacks sponsored by the North Korean regime").Should().Be(new SponsoredCheck(null, "sponsored by the North Korean"));
    }

    [Theory]
    [InlineData("Malicious advertisement campaign spreads infostealer")]
    [InlineData("Attackers promoted fake AI tools on social media")]
    [InlineData("CISA webinar on Secure by Design")]
    public void TheDefaults_NeverSkipRealThreatHeadlines_ThatUseAdWords(string title)
    {
        Defaults.Match(Item(title, tags: ["Security"])).Should().BeNull();
        SponsoredFilter.For(new IntakeOptions())!.Match(Item(title)).Should().BeNull("the server's defaults are the same list");
        new SponsoredFilter(["Advertisement", "Promoted", "Webinar"], []).Match(Item(title)).Should().NotBeNull("an administrator can still add them");
    }

    [Theory]
    [InlineData("Unsponsored research finds flaws in routers")]
    [InlineData("Sponsorship scam targets streamers")]
    [InlineData("Microsoft patches 70 flaws")]
    public void WholeWordsOnly(string title) => Defaults.Check(title).Should().Be(new SponsoredCheck(null, null));

    [Fact]
    public void AMarkerOutsideAnException_StillMatches()
    {
        Defaults.Check("State-sponsored threats: a sponsored post by Acme").Marker.Should().Be("Sponsored post");
        Defaults.Check("Sponsored by Acme: state-sponsored threats").Marker.Should().Be("Sponsored by");
    }

    [Fact]
    public void ACustomMarker_CatchesAnotherFeedsConvention_WithNoCode()
    {
        var custom = new SponsoredFilter(["[Partner]", "Advertorial", "Brand studio"], []);
        custom.Match(Item("[Partner] How MDR cuts dwell time")).Should().Be(new SponsoredMatch("[Partner]", "Title"));
        custom.Match(Item("Cloud posture", tags: ["advertorial"])).Should().Be(new SponsoredMatch("Advertorial", "Category"));
        custom.Match(Item("Cloud posture", "Brand Studio")).Should().Be(new SponsoredMatch("Brand studio", "Author"));
        custom.Match(Item("Partner arrested in fraud case")).Should().BeNull();
        custom.Match(Item("Sponsored by TryHackMe")).Should().BeNull("only the admin's markers count");
    }

    [Fact]
    public void TheSwitch_Off_GivesNoFilter_AndUnsetListsUseTheDefaults()
    {
        SponsoredFilter.For(new IntakeOptions { SkipSponsored = false }).Should().BeNull();
        SponsoredFilter.For(new IntakeOptions())!.Check("Sponsored by X").Marker.Should().Be("Sponsored by");
        SponsoredFilter.For(new IntakeOptions { SponsoredMarkers = "Brand studio", SponsoredExceptions = "" })!.Check("Sponsored by X").Marker.Should().BeNull();
    }

    [Fact]
    public void TheLists_AreValidated_AsTheyreSaved()
    {
        OperationalSettings.Normalize(OperationalSettings.SponsoredMarkers, " Sponsored \n\nPaid post; paid POST\r\n[Partner]").Should().Be("Sponsored; Paid post; [Partner]");
        OperationalSettings.Normalize(OperationalSettings.SponsoredExceptions, "").Should().BeEmpty("no exceptions is allowed");
        OperationalSettings.Normalize(OperationalSettings.SkipSponsored, "TRUE").Should().Be("true");

        OperationalSettings.Problem(OperationalSettings.SponsoredMarkers, " \n ").Should().Contain("at least one");
        OperationalSettings.Problem(OperationalSettings.SponsoredMarkers, new string('a', 61)).Should().Contain("longer than 60");
        OperationalSettings.Problem(OperationalSettings.SponsoredMarkers, "Sponsored; []").Should().Contain("'[]' needs a letter");
        OperationalSettings.Problem(OperationalSettings.SponsoredExceptions, string.Join(';', Enumerable.Range(0, 51).Select(i => "p" + i))).Should().Contain("at most 50");
        OperationalSettings.Problem(OperationalSettings.SkipSponsored, "maybe").Should().NotBeNull();
    }

    [Fact]
    public void ThePhrasesFromTheServer_AreUsedOnlyWhenUsable()
    {
        var f = new SponsoredFilter(["", "  ", "--", new string('x', 61), "Promoted"], ["", "state-sponsored"]);
        f.Check("--").Marker.Should().BeNull();
        f.Check("Promoted listing").Marker.Should().Be("Promoted");
        f.Check(null).Should().Be(new SponsoredCheck(null, null));
    }

    [Fact]
    public void TheLog_KeepsEachSourcesLastPoll_AndAFewOfItsItems()
    {
        var log = new SponsoredLog();
        var id = Guid.NewGuid();
        log.For(id).Count.Should().Be(0);
        log.Record(id, [.. Enumerable.Range(0, 7).Select(i => new SponsoredSkip("t" + i, "Sponsored", "Title"))]);
        log.For(id).Should().Match<(int Count, IReadOnlyList<SponsoredSkip> Sample)>(l => l.Count == 7 && l.Sample.Count == SponsoredLog.Sample);
        log.Record(id, []);
        log.For(id).Count.Should().Be(0);
    }
}
