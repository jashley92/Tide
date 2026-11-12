using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Tide.Application.Metrics;
using Tide.Application.Security;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.IntegrationTests;

public class MetricsServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private static readonly DateOnly Thu = new(2026, 10, 22), Fri = new(2026, 10, 23), Mon = new(2026, 10, 26);

    public MetricsServiceTests() => _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };

    public void Dispose() => _db.Dispose();

    private async Task<Guid> Metric(string name = "Older than 24 hours", MetricDirection direction = MetricDirection.LowerIsBetter,
        decimal? warn = 5, decimal? critical = 10)
    {
        var svc = _db.Metrics();
        var group = await svc.AddGroupAsync("Alert queue");
        return await svc.SaveMetricAsync(new MetricForm { GroupId = group, Name = name, Direction = direction, WarnThreshold = warn, CriticalThreshold = critical });
    }

    [Fact]
    public async Task OnceTheBriefIsSent_TheDaysNumbersAreFixed_ExceptForAnAdministrator()
    {
        var id = await Metric();
        await using (var ctx = _db.Context())
        {
            ctx.Briefs.Add(new Brief { BriefDate = Mon, AnalystId = "x", AnalystName = "x", Status = BriefStatus.Sent });
            await ctx.SaveChangesAsync();
        }
        _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst };

        (await _db.Metrics().IsLockedAsync(Mon)).Should().BeTrue();
        (await _db.Metrics().IsLockedAsync(Fri)).Should().BeFalse();
        var act = () => _db.Metrics().SaveValueAsync(id, Mon, 3, null);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*sent*");
        await _db.Metrics().SaveValueAsync(id, Fri, 3, null);

        _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };
        await _db.Metrics().SaveValueAsync(id, Mon, 3, "corrected");
    }

    [Fact]
    public async Task SaveNumber_SetsTheValue_AndLeavesTheNoteAsItIs()
    {
        var id = await Metric();
        var svc = _db.Metrics();
        await svc.SaveValueAsync(id, Mon, 7, "relay outage, partial count");

        await svc.SaveNumberAsync(id, Mon, 9);
        await svc.SaveNumberAsync(id, Fri, 4);

        var monday = (await svc.GetDayAsync(Mon)).Groups.Single().Tiles.Single();
        monday.Value.Should().Be(9);
        monday.Note.Should().Be("relay outage, partial count");
        (await svc.GetDayAsync(Fri)).Groups.Single().Tiles.Single().Note.Should().BeNull("a first number has no note");
    }

    [Fact]
    public async Task GetDay_ComparesMondayWithFriday_ColoursByDirection_AndAppliesThresholds()
    {
        var id = await Metric();
        var svc = _db.Metrics();
        await svc.SaveValueAsync(id, Thu, 2, null);
        await svc.SaveValueAsync(id, Fri, 4, null);
        await svc.SaveValueAsync(id, Mon, 7, "relay outage, partial count");

        var tile = (await svc.GetDayAsync(Mon)).Groups.Single().Tiles.Single();

        tile.Value.Should().Be(7);
        tile.Note.Should().Be("relay outage, partial count");
        tile.Previous!.Date.Should().Be(Fri);
        tile.Previous.Delta.Should().Be(3);
        tile.Previous.Trend.Should().Be(MetricTrend.Worse, "more old alerts is worse");
        tile.Level.Should().Be(MetricLevel.Warn);
        tile.Spark.Should().HaveCount(MetricsService.SparkDays);
        tile.Spark.TakeLast(3).Should().Equal(2m, 4m, 7m);
    }

    [Fact]
    public async Task GetDay_AHolidayWithNoValueIsSkipped_AndAMissingValueIsADashNotZero()
    {
        var id = await Metric();
        await _db.Metrics().SaveValueAsync(id, Thu, 6, null);

        var day = await _db.Metrics().GetDayAsync(Mon);
        var tile = day.Groups.Single().Tiles.Single();
        tile.Value.Should().BeNull();
        tile.Display.Should().Be("—");
        tile.Previous.Should().BeNull("there's no value today to compare");
        tile.Reference.Should().Be((Thu, 6m), "the entry form shows the value it'll be compared with before today's is typed");
        day.Missing.Should().ContainSingle();

        await _db.Metrics().SaveValueAsync(id, Mon, 6, null);
        var cmp = (await _db.Metrics().GetDayAsync(Mon)).Groups.Single().Tiles.Single().Previous!;
        cmp.Date.Should().Be(Thu, "Friday had no value");
        MetricMath.ReferenceLabel(cmp.Date, Mon).Should().Be("Oct 22", "a comparison that skipped a day is named by its date");
        cmp.Trend.Should().Be(MetricTrend.Unchanged);
    }

    [Fact]
    public async Task GetDay_TrendDays_KeepAHolidayWithAValueInTheGroup_AndDropOneWithout()
    {
        var id = await Metric();
        var other = await _db.Metrics().AddGroupAsync("Cases");
        var otherId = await _db.Metrics().SaveMetricAsync(new MetricForm { GroupId = other, Name = "Open", Direction = MetricDirection.Neutral });
        _db.Clock.UseHolidays(new Dictionary<DateOnly, string> { [Thu] = "Founders' Day", [Fri] = "Founders' Day, observed" });
        var svc = new MetricsService(_db, _db.User, _db.Clock);
        await svc.SaveValueAsync(id, Thu, 4, "counted anyway");
        await svc.SaveValueAsync(otherId, Mon, 1, null);

        var day = await svc.GetDayAsync(Mon);

        var queue = day.Groups.Single(g => g.Group.Name == "Alert queue");
        queue.TrendDays.Should().Contain(Thu).And.NotContain(Fri).And.EndWith(Mon);
        queue.Tiles.Single().Trend[queue.TrendDays.ToList().IndexOf(Thu)].Should().Be(4);
        day.Groups.Single(g => g.Group.Name == "Cases").TrendDays.Should().NotContain(Thu, "the value is in another group");
    }

    [Fact]
    public async Task GetDay_CarriesTheWorkingDays_SoTheDayAfterAHolidayStillSaysFri()
    {
        var id = await Metric();
        var tue = new DateOnly(2026, 10, 27);
        _db.Clock.UseHolidays(new Dictionary<DateOnly, string> { [Mon] = "Founders' Day" });
        var svc = new MetricsService(_db, _db.User, _db.Clock);
        await svc.SaveValueAsync(id, Fri, 3, null);
        await svc.SaveValueAsync(id, tue, 4, null);

        var day = await svc.GetDayAsync(tue);

        var from = day.Groups.Single().Tiles.Single().Previous!.Date;
        from.Should().Be(Fri);
        MetricMath.ReferenceLabel(from, tue, day.Days).Should().Be("Fri", "the holiday between isn't a business day");
    }

    [Fact]
    public async Task ValuesAndThresholds_TooLargeForTheColumn_AreRefused_EvenWhenRoundingBringsThemThere()
    {
        var id = await Metric();
        var group = (await _db.Metrics().ListAsync()).Single().Id;

        var value = () => _db.Metrics().SaveValueAsync(id, Fri, 99_999_999_999_999.99996m, null);
        var warn = () => _db.Metrics().SaveMetricAsync(new MetricForm { Id = id, GroupId = group, Name = "Big", WarnThreshold = 1_000_000_000_000_000_000m });
        await value.Should().ThrowAsync<ArgumentException>().WithMessage("*too large*");
        await warn.Should().ThrowAsync<ArgumentException>().WithMessage("*too large*");

        await _db.Metrics().SaveMetricAsync(new MetricForm { Id = id, GroupId = group, Name = "Fine", WarnThreshold = 1.23456m });
        await using var ctx = _db.Context();
        (await ctx.MetricDefinitions.SingleAsync()).WarnThreshold.Should().Be(1.2346m, "kept to the four places the column holds");
    }

    [Fact]
    public async Task RenameAndRetire_KeepHistory_RetiredDropsOffTheDay()
    {
        var id = await Metric();
        var svc = _db.Metrics();
        await svc.SaveValueAsync(id, Fri, 3, null);
        string key;
        await using (var ctx = _db.Context()) key = (await ctx.MetricDefinitions.SingleAsync()).Key;

        await svc.SaveMetricAsync(new MetricForm { Id = id, GroupId = (await svc.ListAsync()).Single().Id, Name = "Aged > 24h", Direction = MetricDirection.LowerIsBetter });
        await svc.SetMetricRetiredAsync(id, true);

        (await svc.GetDayAsync(Mon)).Any.Should().BeFalse();
        var history = await svc.HistoryAsync(Thu, Mon);
        history.Metrics.Single().Should().Match<Domain.Entities.MetricDefinition>(m => m.Name == "Aged > 24h" && m.Key == key && m.IsRetired);
        history.Values.Single().Value.Should().Be(3);
        MetricsService.ToCsv(history).Should().Contain("2026-10-23,Alert queue," + key + ",Aged > 24h,3,Count,LowerIsBetter,yes");
    }

    [Fact]
    public async Task Retire_DeletesAMetricThatNeverHadAValue()
    {
        var id = await Metric();
        await _db.Metrics().SetMetricRetiredAsync(id, true);
        await using var ctx = _db.Context();
        (await ctx.MetricDefinitions.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task SaveValue_NullClears_AndOnlyBusinessDays()
    {
        var id = await Metric();
        await _db.Metrics().SaveValueAsync(id, Fri, 3, null);
        await _db.Metrics().SaveValueAsync(id, Fri, null, null);
        await using (var ctx = _db.Context()) (await ctx.MetricValues.AnyAsync()).Should().BeFalse();

        var weekend = () => _db.Metrics().SaveValueAsync(id, new DateOnly(2026, 10, 24), 1, null);
        await weekend.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task SaveMetric_RefusesThresholdsTheWrongWayRound()
    {
        var act = () => Metric(direction: MetricDirection.HigherIsBetter, warn: 90, critical: 95);
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*at or below*");
    }

    [Fact]
    public async Task Permissions_AnalystsEnter_AdminsDefine_ActionOwnersSee_ReadersDont()
    {
        var id = await Metric();

        _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst };
        await _db.Metrics().SaveValueAsync(id, Fri, 1, null);
        var define = () => _db.Metrics().AddGroupAsync("x");
        await define.Should().ThrowAsync<ForbiddenException>();

        _db.User.Roles = new HashSet<AppRole> { AppRole.ActionOwner };
        (await _db.Metrics().GetDayAsync(Fri)).Any.Should().BeTrue();
        var enter = () => _db.Metrics().SaveValueAsync(id, Fri, 2, null);
        await enter.Should().ThrowAsync<ForbiddenException>();

        _db.User.Roles = new HashSet<AppRole> { AppRole.Reader };
        var see = () => _db.Metrics().GetDayAsync(Fri);
        await see.Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task ExampleSet_IsNeverSeeded_ImportsOnce_AndIsGeneric()
    {
        (await _db.Metrics().ListAsync()).Should().BeEmpty("nothing is seeded silently");

        (await _db.Metrics().ImportExampleSetAsync()).Should().Be(3);
        (await _db.Metrics().ImportExampleSetAsync()).Should().Be(0, "a second import adds nothing");

        var groups = await _db.Metrics().ListAsync();
        groups.Select(g => g.Name).Should().Equal("User-reported email", "Alert queue", "Vulnerability SLA");
        groups.SelectMany(g => g.Metrics).Should().HaveCount(6);
    }
}
