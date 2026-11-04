using FluentAssertions;
using Microsoft.Extensions.Options;
using Tide.Application.History;
using Tide.Application.Operations;
using Tide.Application.Org;
using Tide.Domain.Entities;

namespace Tide.UnitTests;

/// <summary>The whole-system audit log's pieces that need no database: its lines and masking, its filter in the address,
/// and its CSV.</summary>
public class AuditLogTests
{
    private static AuditLogEntry Row(string action, string type, string key, string? json) => new()
    {
        AtUtc = new DateTimeOffset(2026, 10, 30, 14, 5, 0, TimeSpan.Zero), UserId = "dev:robin", UserName = "Robin", Action = action,
        EntityType = type, EntityKey = key, ChangesJson = json,
    };

    [Fact]
    public void AuditChanges_LeaveOutTheStamps_AndNameTimesWithoutUtc()
    {
        var changes = HistoryFormatter.AuditChanges(Row("Modified", "Source", "s",
            """{"UpdatedAtUtc":["a","b"],"UpdatedBy":["x","y"],"LastPolledAtUtc":["2026-10-30T10:00:00+00:00","2026-10-30T11:00:00+00:00"],"Name":["Old","New"]}"""));

        changes.Should().Equal(
            new HistoryChange("Last polled at", "2026-10-30 10:00 UTC", "2026-10-30 11:00 UTC"),
            new HistoryChange("Name", "Old", "New"));
    }

    [Theory]
    [InlineData("Added", null, "hunter2", null, HistoryFormatter.SecretSet)]
    [InlineData("Modified", "hunter1", "hunter2", HistoryFormatter.SecretSet, HistoryFormatter.SecretChanged)]
    [InlineData("Modified", null, "hunter2", null, HistoryFormatter.SecretSet)]
    [InlineData("Modified", "hunter1", null, HistoryFormatter.SecretSet, null)]
    public void ASecretSetting_ShowsOnlyAsSetOrChanged(string action, string? before, string? after, string? shownBefore, string? shownAfter)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string?[]> { ["Value"] = [before, after] });

        foreach (var key in new[] { "Email:SmtpPassword", "Cve:NvdApiKey", "Custom:SigningToken" })
            HistoryFormatter.AuditChanges(Row(action, nameof(AppSetting), key, json)).Should().ContainSingle(key).Which.Should().Be(new HistoryChange("Value", shownBefore, shownAfter), key);
    }

    [Fact]
    public void APlainSetting_ShowsItsValue_AndAnAddressSettingWithoutItsKey()
    {
        HistoryFormatter.AuditChanges(Row("Modified", nameof(AppSetting), "Org:TeamName", """{"Value":["Red","Blue"]}"""))
            .Should().Equal(new HistoryChange("Value", "Red", "Blue"));
        HistoryFormatter.AuditChanges(Row("Added", nameof(AppSetting), "Cve:NvdUrl", """{"Value":[null,"https://nvd.example.org/?apikey=abc123"]}"""))
            .Single().After.Should().NotContain("abc123");
    }

    [Fact]
    public void ASource_ShowsItsAddressWithoutAKey_AndItsErrorScrubbed()
    {
        var changes = HistoryFormatter.AuditChanges(Row("Modified", nameof(Source), "s",
            """{"Url":["https://user:pw@feed.example.org/rss","https://feed.example.org/rss?token=t0ps3cret"],"LastError":[null,"401 for password=letmein"]}"""));

        string.Join("\n", changes.Select(c => $"{c.Before} {c.After}")).Should().NotContain("pw@").And.NotContain("t0ps3cret").And.NotContain("letmein");
    }

    [Fact]
    public void ALinkRow_IsWhatItNames()
    {
        HistoryFormatter.AuditChanges(Row("Deleted", "ArticleTechnique", "a/T1566", null)).Should().Equal(new HistoryChange("Technique", null, "T1566"));
    }

    [Fact]
    public void Kinds_HaveNamesForPeople_FallingBackToTheTypeSplitUp()
    {
        AuditKinds.Name(nameof(AdGroupRoleMapping)).Should().Be("Role mapping");
        AuditKinds.Name(nameof(AppSetting)).Should().Be("Setting");
        AuditKinds.Name(nameof(ActionItem)).Should().Be("Action");
        AuditKinds.Name("SomethingNew").Should().Be("Something new");
        AuditKinds.All.Select(k => k.Name).Should().BeInAscendingOrder(StringComparer.Ordinal);
    }

    [Fact]
    public void TheFilter_RoundTripsThroughTheAddress_AndDropsWhatItDoesntKnow()
    {
        var filter = new AuditFilter
        {
            Person = "dev:sam", Kind = "Article", Key = "k1", From = new DateOnly(2026, 10, 1), To = new DateOnly(2026, 10, 31),
            Action = AuditAction.Deleted, Search = "ransomware",
        };
        var query = filter.ToQuery();

        AuditFilter.FromQuery(n => query.GetValueOrDefault(n)?.ToString()).Should().Be(filter);
        AuditFilter.FromQuery(n => n switch { "key" => "k1", "action" => "4", "from" => "yesterday", _ => null })
            .Should().Be(new AuditFilter(), "a record needs its kind; the action is a name; a date is a date");
        new AuditFilter { Key = "k1" }.ToQuery()["key"].Should().BeNull();
        filter.Unindexed.Should().BeFalse("the person has an index, and narrows the action and the words to their rows");
        (filter with { Person = null }).Unindexed.Should().BeTrue();
        new AuditFilter { Kind = "Article", Key = "k", From = new DateOnly(2026, 1, 1) }.Unindexed.Should().BeFalse();
        filter.Cleared().Should().Be(new AuditFilter { Search = "ransomware" });
    }

    [Fact]
    public void TheChips_NameEachFilter_AndRemoveIt()
    {
        var filter = new AuditFilter { Person = "dev:sam", Kind = "AppSetting", Key = "Org:TeamName", Action = AuditAction.Changed, From = new DateOnly(2026, 10, 1) };

        var chips = filter.Chips(id => id == "dev:sam" ? "Sam" : null, "Organization: Team name");

        chips.Select(c => c.Label).Should().Equal("By: Sam", "Kind: Setting", "Only: Organization: Team name", "Action: changed", "From 1 Oct 2026");
        chips[1].Without.Should().Match<AuditFilter>(f => f.Kind == null && f.Key == null, "the record goes with its kind");
        chips[2].Without.Key.Should().BeNull();
        new AuditFilter { To = new DateOnly(2026, 10, 2) }.Chips(_ => null, null).Single().Label.Should().Be("Up to 2 Oct 2026");
    }

    [Theory]
    [InlineData("50%", "50!%")]
    [InlineData("a_b", "a!_b")]
    [InlineData("[x]!", "![x]!!")]
    public void TheWords_AreLiteralInTheLikePattern(string text, string pattern) => AuditLogService.Escape(text).Should().Be(pattern);

    [Fact]
    public void TheCsv_HasALinePerField_InTheOrgsTime_WithFormulasNeutralised()
    {
        var clock = new OrgClock(Options.Create(new OrgOptions { TimeZone = "America/New_York" }));
        var at = new DateTimeOffset(2026, 10, 30, 14, 5, 0, TimeSpan.Zero);
        AuditRow Audit(AuditAction action, IReadOnlyList<HistoryChange> changes, string record = "Edge VPN") =>
            new(Guid.NewGuid(), at, "dev:robin", "Robin", action, "Article", "k1", "Article", record, null, changes);

        var csv = AuditCsv.Write(
        [
            Audit(AuditAction.Changed, [new("Title", "Old, \"quoted\"", "=cmd|' /C calc'!A0"), new("Bottom line", null, "Line one\nline two")]),
            Audit(AuditAction.Deleted, [], record: "@SUM(1)"),
        ], clock);

        var lines = csv.Split("\r\n");
        lines[0].Should().Be(AuditCsv.Header);
        lines[1].Should().Be("2026-10-30 10:05:00,2026-10-30T14:05:00Z,Robin,dev:robin,changed,Article,Edge VPN,k1,Title,\"Old, \"\"quoted\"\"\",'=cmd|' /C calc'!A0");
        csv.Should().Contain(",Bottom line,,\"Line one\nline two\"");
        csv.Should().Contain(",deleted,Article,'@SUM(1),k1,,,", "a save with no fields still has its line");
    }
}
