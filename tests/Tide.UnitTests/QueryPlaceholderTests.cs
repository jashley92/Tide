using AwesomeAssertions;
using Tide.Application.Actions;
using Tide.Domain.Enums;

namespace Tide.UnitTests;

/// <summary>What a saved query's Insert menu and "{{" list offer (QueryPlaceholder.All): what a saved query is filled from.</summary>
public class QueryPlaceholderTests
{
    [Fact]
    public void TheMenu_OffersTheDaysTheValues_AndEachKind_AllOfThemAcceptedOnSave()
    {
        var written = QueryPlaceholder.All.Select(p => p.Written).ToList();

        written.Take(2).Should().Equal(QueryDialects.DaysPlaceholder, QueryDialects.Placeholder);
        written.Skip(2).Should().Equal(Enum.GetValues<HuntValueKind>().Select(HuntQueryBuilder.ValuesPlaceholder), "one for each kind, in order");
        written.Should().OnlyHaveUniqueItems();
        HuntQueryBuilder.ValuesPlaceholders(string.Join(" ", written)).Should().HaveCount(written.Count - 1,
            "every values placeholder offered is one a saved query is accepted with (the days aren't values)");
        QueryPlaceholder.All.Should().OnlyContain(p => !string.IsNullOrWhiteSpace(p.Description));
        QueryPlaceholder.All.Single(p => p.Name == "values:sha256").Description.Should().StartWith("SHA-256 hashes only");
    }
}
