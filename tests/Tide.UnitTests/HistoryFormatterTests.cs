using FluentAssertions;
using Tide.Application.History;
using Tide.Domain.Entities;

namespace Tide.UnitTests;

/// <summary>The audit log's before-and-after JSON, as lines for people in the History view.</summary>
public class HistoryFormatterTests
{
    private static AuditLogEntry Row(string action, string type, string key, string? json) => new()
    {
        AtUtc = DateTimeOffset.UtcNow, UserName = "Robin", Action = action, EntityType = type, EntityKey = key, ChangesJson = json,
    };

    [Fact]
    public void AChange_ListsEachField_AsOldToNew_InTheEditorsOrder_AndSkipsTheNoise()
    {
        var entry = HistoryFormatter.Entry(Row("Modified", "Article", "a",
            """{"WhyItMattersTlp":["Clear","Amber"],"EditVersion":["x","y"],"ModifiedAtUtc":[null,"2026-10-30T10:00:00.0000000+00:00"],"Title":["Old","New"],"Priority":[null,"P1"]}"""));

        entry!.What.Should().Be("Changed");
        entry.Changes.Select(c => $"{c.Field}: {c.Before ?? "(none)"} → {c.After}").Should().Equal(
            "Title: Old → New", "Priority: (none) → P1", "Why it matters marking: TLP:CLEAR → TLP:AMBER");
    }

    [Fact]
    public void AChangeOfNoiseOnly_IsLeftOut() =>
        HistoryFormatter.Entry(Row("Modified", "Article", "a", """{"EditVersion":["x","y"]}""")).Should().BeNull();

    [Fact]
    public void ACreate_AndADelete_SayWhatHappened()
    {
        var created = HistoryFormatter.Entry(Row("Added", "Brief", "b", """{"Status":[null,"Draft"],"CreatedBy":[null,"S-1"]}"""))!;
        created.What.Should().Be("Created");
        created.Changes.Should().ContainSingle().Which.Should().Be(new HistoryChange("Status", null, "Draft"));

        var deleted = HistoryFormatter.Entry(Row("Deleted", "Article", "a", """{"Title":["Gone",null]}"""))!;
        deleted.What.Should().Be("Deleted");
        deleted.Changes.Should().BeEmpty();
    }

    [Fact]
    public void ALinkRow_NamesWhatWasAddedOrRemoved()
    {
        var technique = HistoryFormatter.Entry(Row("Added", "ArticleTechnique", "a/T1566.001", """{"ArticleId":[null,"a"]}"""))!;
        technique.What.Should().Be("Added a technique");
        technique.Changes.Single().Should().Be(new HistoryChange("Technique", null, "T1566.001"));

        var tagId = Guid.NewGuid();
        var tag = HistoryFormatter.Entry(Row("Deleted", "ArticleTag", $"a/{tagId}", null), id => id == tagId ? "Phishing" : null)!;
        tag.What.Should().Be("Removed a scorecard tag");
        tag.Changes.Single().After.Should().Be("Phishing");

        HistoryFormatter.Entry(Row("Added", "ArticleTag", $"a/{Guid.NewGuid()}", null))!.Changes.Single().After.Should().Be("a tag since removed");
    }

    [Theory]
    [InlineData("Tlp", "AmberStrict", "TLP:AMBER+STRICT")]
    [InlineData("MeetingHeldAtUtc", "2026-10-30T14:05:00.0000000+00:00", "2026-10-30 14:05 UTC")]
    [InlineData("StoryId", "9d7c3f53-0000-0000-0000-000000000000", "set")]
    [InlineData("Bluf", "  ", null)]
    [InlineData("Bluf", "Exploited.", "Exploited.")]
    public void Values_AreWrittenAsTheAppWritesThem(string field, string? raw, string? expected) =>
        HistoryFormatter.Value(field, raw).Should().Be(expected);

    [Fact]
    public void Labels_AreFriendly_AndAnUnknownFieldIsSplitAtItsCapitals()
    {
        HistoryFormatter.Label("Bluf").Should().Be("Bottom line");
        HistoryFormatter.Label("SomeNewField").Should().Be("Some new field");
        HistoryFormatter.Part("plain").Should().Be("plain");
    }

    [Fact]
    public void LongText_IsCutUntilAskedFor()
    {
        var change = new HistoryChange("Why it matters", null, new string('x', HistoryChange.ShortLength + 10));
        change.IsLong.Should().BeTrue();
        HistoryChange.Short(change.After).Should().HaveLength(HistoryChange.ShortLength + 1).And.EndWith("…");
        new HistoryChange("Title", "a", "b").IsLong.Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not json")]
    [InlineData("""{"Title":["only one"]}""")]
    [InlineData("null")]
    public void BadOrOddJson_GivesNoLines(string? json) => HistoryFormatter.Changes(json).Should().BeEmpty();
}
