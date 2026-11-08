using AwesomeAssertions;
using Tide.Application.Intake;
using Tide.Domain.Enums;

namespace Tide.UnitTests;

public class StarterSourcesTests
{
    [Fact]
    public void EachHasAUniqueNameAndAddress_AndAPlainHttpsOne()
    {
        StarterSources.All.Select(s => s.Name).Should().OnlyHaveUniqueItems(s => s.ToLowerInvariant());
        StarterSources.All.Select(s => s.Url).Should().OnlyHaveUniqueItems();
        StarterSources.All.Should().OnlyContain(s => s.Name.Length <= 100 && Uri.IsWellFormedUriString(s.Url, UriKind.Absolute) && s.Url.StartsWith("https://"));
    }

    [Fact]
    public void TheRecommendedAreAShortList_InEachGroup()
    {
        var recommended = StarterSources.All.Where(s => s.Recommended).ToList();
        recommended.Count.Should().BeInRange(10, 15);
        recommended.Select(s => s.Group).Distinct().Should().BeEquivalentTo([StarterSources.Government, StarterSources.News, StarterSources.Research]);
        StarterSources.All.Should().ContainSingle(s => s.Kind == SourceKind.CisaKev).Which.Recommended.Should().BeTrue();
    }

    [Fact]
    public void PollIntervals_AreWithinWhatASourceMayHave()
    {
        StarterSources.All.Should().OnlyContain(s => s.PollMinutes >= 60 && s.PollMinutes <= 7 * 24 * 60);
    }
}
