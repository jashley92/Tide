using AwesomeAssertions;
using Tide.Application.Scorecard;
using Tide.Domain.Entities;

namespace Tide.UnitTests;

/// <summary>The scorecard tag picker's "Add a tag" search, matched in memory over the editor's categories.</summary>
public class ScorecardTagSearchTests
{
    private static ScorecardCategory Cat(string name, params string[] tags) =>
        new() { Name = name, Tags = tags.Select(t => new ScorecardTag { Name = t }).ToList() };

    private static readonly ScorecardCategory Delivery = Cat("Delivery", "Phishing", "Spear phishing", "Drive-by download");
    private static readonly ScorecardCategory Cloud = Cat("Cloud", "Misconfiguration", "Token theft");
    private static readonly ScorecardCategory Impact = Cat("Impact", "Ransomware", "Data theft", "Public cloud outage", "Old cloud thing");
    private static readonly ScorecardCategory Retired = Cat("Retired cloud bucket", "Anything");

    static ScorecardTagSearchTests()
    {
        Impact.Tags[3].IsRetired = true;
        Retired.IsRetired = true;
    }

    private static readonly ScorecardCategory[] All = [Delivery, Cloud, Impact, Retired];

    private static IEnumerable<string> Names(IEnumerable<ScorecardTagSearch.Hit> hits) => hits.Select(h => h.Tag.Name);

    [Theory]
    [InlineData("Phishing", "phish", 0)]
    [InlineData("Spear phishing", "PHISH", 1)]
    [InlineData("Drive-by download", "by", 1)]
    [InlineData("Ransomware", "ware", 2)]
    [InlineData("Ransomware", " ransom ", 0)]
    [InlineData("Mass mailing", "ss", 2)]
    [InlineData("Ransomware", "cloud", null)]
    [InlineData("Ransomware", "  ", null)]
    public void Rank_PutsTheStartFirst_ThenAWordsStart_ThenAnywhere(string name, string text, int? rank) =>
        ScorecardTagSearch.Rank(name, text).Should().Be(rank);

    [Fact]
    public void ACategoryMatchedByName_OffersAllItsTags_BeforeTagsThatOnlyContainTheWords()
    {
        var hits = ScorecardTagSearch.Find(All, "cloud");

        Names(hits).Should().Equal("Misconfiguration", "Token theft", "Public cloud outage");
        hits.Select(h => h.Category).Should().Equal(Cloud, Cloud, Impact);
        hits.Select(h => h.First).Should().Equal(true, false, true);
        hits.Should().NotContain(h => h.Tag.IsRetired || h.Category.IsRetired, "retired tags and categories aren't offered");
    }

    [Fact]
    public void Prefixes_ComeBeforeContains_InAGroup_AndAcrossGroups()
    {
        Names(ScorecardTagSearch.Find(All, "Phish")).Should().Equal("Phishing", "Spear phishing");
        Names(ScorecardTagSearch.Find(All, "theft")).Should().Equal("Token theft", "Data theft");
        Names(ScorecardTagSearch.Find(All, "t")).First().Should().Be("Token theft", "a name starting with it beats the rest");
    }

    [Fact]
    public void TheChosen_AreLeftOut_AndNothingTypedOffersNothing()
    {
        var chosen = new HashSet<Guid> { Delivery.Tags[0].Id };

        Names(ScorecardTagSearch.Find(All, "phish", chosen)).Should().Equal("Spear phishing");
        ScorecardTagSearch.Find(All, "phish", chosen)[0].First.Should().BeTrue("it heads its group now");
        ScorecardTagSearch.Find(All, null).Should().BeEmpty();
        ScorecardTagSearch.Find(All, "   ").Should().BeEmpty();
        ScorecardTagSearch.Find(All, "nothing like it").Should().BeEmpty();
    }

    [Fact]
    public void TheChosenTags_ComeByCategory_AndRetiredOnesAreLeftOver()
    {
        var retired = Guid.NewGuid();
        var chosen = new HashSet<Guid> { Impact.Tags[0].Id, Delivery.Tags[1].Id, Delivery.Tags[0].Id, retired };

        var groups = ScorecardTagSearch.Chosen(All, chosen);

        groups.Select(g => g.Category).Should().Equal(Delivery, Impact);
        groups[0].Tags.Select(t => t.Name).Should().Equal("Phishing", "Spear phishing");
        ScorecardTagSearch.Gone(All, chosen).Should().Equal(retired);
        ScorecardTagSearch.Summary(3).Should().Be("3 chosen");
    }
}
