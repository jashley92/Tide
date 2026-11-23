using AwesomeAssertions;
using Tide.Application.Search;
using Tide.Domain.Enums;
using Tide.Domain.Observables;

namespace Tide.UnitTests;

public class SearchTextTests
{
    [Fact]
    public void Terms_AreWordsOrQuotedPhrases_WithoutRepeatsOrSingleCharacters()
    {
        SearchText.Terms("  ransomware \"edge VPN\" a Ransomware  ").Should().Equal("ransomware", "edge VPN");
        SearchText.Terms(null).Should().BeEmpty();
        SearchText.Terms(string.Join(' ', Enumerable.Range(10, 20))).Should().HaveCount(SearchText.MaxTerms);
    }

    [Fact]
    public void Forms_LookForANetworkIndicatorLiveAndDefanged()
    {
        SearchText.Forms("evil[.]com").Should().Equal("evil[.]com", "evil.com");
        SearchText.Forms("hxxps://evil.com/x").Should().Equal("hxxps://evil.com/x", "https://evil.com/x", "hxxps://evil[.]com/x");
        SearchText.Forms("ransomware").Should().Equal("ransomware");
        SearchText.Forms("d41d8cd98f00b204e9800998ecf8427e").Should().ContainSingle("a hash has no defanged form");
        SearchText.Forms("203.0.113.45").Should().Equal("203.0.113.45", "203.0.113[.]45", "203[.]0[.]113[.]45");
    }

    [Fact]
    public void Literal_EscapesLikeWildcards()
    {
        SearchText.Literal(@"100%_[x]\").Should().Be(@"100\%\_\[x]\\");
        SearchText.Contains("a_b").Should().Be(@"%a\_b%");
    }

    [Fact]
    public void Snippet_CentresOnTheFirstMatch_WithoutMarkdown()
    {
        var text = string.Join(' ', Enumerable.Repeat("filler", 30)) + " the **ransomware** crew [moved](https://x.example) on " + string.Join(' ', Enumerable.Repeat("tail", 40));
        var s = SearchText.Snippet([null, "nothing here", text], ["ransomware"])!;
        s.Should().StartWith("…").And.EndWith("…").And.Contain("the ransomware crew moved on");
        s.Should().NotContain("**").And.NotContain("https://");
        SearchText.Snippet(["no match"], ["zzz"], "The bottom line.").Should().Be("The bottom line.");
        SearchText.Snippet(["no match"], ["zzz"]).Should().BeNull();
    }

    [Fact]
    public void Mark_SplitsTextIntoMatchedRuns_CaseInsensitive_LongestFirst()
    {
        SearchText.Mark("Edge VPN and edge devices", SearchText.AllForms(["edge", "edge vpn"]))
            .Should().Equal(new TextRun("Edge VPN", true), new TextRun(" and ", false), new TextRun("edge", true), new TextRun(" devices", false));
        SearchText.Mark("nothing", ["zzz"]).Should().Equal(new TextRun("nothing", false));
        SearchText.Mark(null, ["x"]).Should().BeEmpty();
    }

    [Theory]
    [InlineData(IndicatorType.Domain, "Bad.Example.net.", "bad.example.net")]
    [InlineData(IndicatorType.Url, "https://cdn.bad.example.net:8443/a?b", "cdn.bad.example.net")]
    [InlineData(IndicatorType.EmailAddress, "billing@bad.example.net", "bad.example.net")]
    [InlineData(IndicatorType.IpAddress, "203.0.113.45", "203.0.113.45")]
    [InlineData(IndicatorType.Url, "https://[2001:db8::1]/x", "2001:db8::1")]
    [InlineData(IndicatorType.FileHash, "d41d8cd98f00b204e9800998ecf8427e", null)]
    public void HostOf_NamesTheHost(IndicatorType type, string value, string? host) => IndicatorKey.HostOf(type, value).Should().Be(host);

    [Theory]
    [InlineData(IndicatorType.Domain, "Mail.Bad.Example.net.", "net.example.bad.mail")]
    [InlineData(IndicatorType.Url, "https://cdn.bad.example.net:8443/a?b", "net.example.bad.cdn")]
    [InlineData(IndicatorType.EmailAddress, "billing@bad.example.net", "net.example.bad")]
    [InlineData(IndicatorType.IpAddress, "203.0.113.45", "203.0.113.45")]
    [InlineData(IndicatorType.Url, "https://[2001:db8::1]/x", "2001:db8::1")]
    [InlineData(IndicatorType.FileHash, "d41d8cd98f00b204e9800998ecf8427e", null)]
    public void HostRevOf_ReversesANamesLabels_AndKeepsAnAddress(IndicatorType type, string value, string? rev) =>
        IndicatorKey.HostRevOf(type, value).Should().Be(rev);

    [Fact]
    public void HostRevOf_AHostNameStartsWithEveryNameItsUnder_AndNoLookalikeDoes()
    {
        var under = IndicatorKey.HostRevOf(IndicatorType.Domain, "evil.com") + ".";
        IndicatorKey.HostRevOf(IndicatorType.Domain, "mail.evil.com").Should().StartWith(under);
        IndicatorKey.HostRevOf(IndicatorType.Domain, "notevil.com").Should().NotStartWith(under).And.NotBe("com.evil");
        IndicatorKey.HostRevOf(IndicatorType.Domain, "evil.com.attacker.net").Should().NotStartWith(under);
        IndicatorKey.HostRevOf(IndicatorType.Domain, new string('a', 400) + ".com").Should().BeNull("no host name is that long");
    }

    [Fact]
    public void ValueOf_IsTheSameInEitherCase_AndFitsItsColumn()
    {
        IndicatorKey.ValueOf("HTTPS://Evil.Example.net/X").Should().Be(IndicatorKey.ValueOf("https://evil.example.net/x")).And.HaveLength(64);
        IndicatorKey.ValueOf(new string('a', 2000)).Should().HaveLength(64);
        IndicatorKey.ValueOf("evil.example.net").Should().NotBe(IndicatorKey.ValueOf("evil.example.net "));
    }

    [Fact]
    public void SameOrUnder_TakesSubdomainsButNotLookalikesOrOtherAddresses()
    {
        SearchText.SameOrUnder("cdn.bad.example.net", "bad.example.net").Should().BeTrue();
        SearchText.SameOrUnder("notbad.example.net", "bad.example.net").Should().BeFalse();
        SearchText.SameOrUnder("1.203.0.113.45", "203.0.113.45").Should().BeFalse();
    }

    [Fact]
    public void Terms_KeepALoneDigit_AsAnActionNumber_ButNotOtherSingleCharacters()
    {
        SearchText.Terms("7").Should().Equal("7");
        SearchText.Terms("a _").Should().BeEmpty();
    }

    [Theory]
    [InlineData("A-12", 12)]
    [InlineData("a-7", 7)]
    [InlineData("42", 42)]
    [InlineData("A-", null)]
    [InlineData("12a", null)]
    public void ActionNumber_ReadsALabelOrNumber(string term, int? number) => SearchText.ActionNumber(term).Should().Be(number);
}
