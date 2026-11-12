using AwesomeAssertions;
using Tide.Application.Intake;
using Tide.Application.Org;
using Tide.Domain.Enums;

namespace Tide.UnitTests;

/// <summary>When Today says the feeds haven't been pulled since the morning: the cutoff (a time of day, on working days
/// only, in the organization's zone), what's counted, and how it's said.</summary>
public class FeedFreshnessTests
{
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
    private static readonly TimeOnly Eight = new(8, 0);

    // Wednesday 4 November 2026, New York (EST, UTC-5).
    private static DateTimeOffset NewYorkAt(int day, int hour, int minute = 0) => new(2026, 11, day, hour, minute, 0, TimeSpan.FromHours(-5));

    // --- the cutoff ----------------------------------------------------------------------------------------------

    [Fact]
    public void Cutoff_OnAWorkingDayAfterIt_IsTodaysCutoff()
    {
        var cutoff = FeedFreshness.Cutoff(NewYorkAt(4, 9, 15).ToUniversalTime(), NewYork, WorkingDays.Weekdays, Eight);
        cutoff.Should().Be(NewYorkAt(4, 8, 0));
    }

    [Fact]
    public void Cutoff_AtItsMinute_IsInForce()
    {
        FeedFreshness.Cutoff(NewYorkAt(4, 8, 0), NewYork, WorkingDays.Weekdays, Eight).Should().Be(NewYorkAt(4, 8, 0));
    }

    [Fact]
    public void Cutoff_BeforeItOnAWorkingDay_IsNone_SoNothingIsSaid()
    {
        FeedFreshness.Cutoff(NewYorkAt(4, 7, 59), NewYork, WorkingDays.Weekdays, Eight).Should().BeNull();
        FeedFreshness.Cutoff(NewYorkAt(4, 0, 30), NewYork, WorkingDays.Weekdays, Eight).Should().BeNull();
    }

    [Theory]
    [InlineData(7)]   // Saturday
    [InlineData(8)]   // Sunday
    public void Cutoff_OnAWeekend_IsNone(int day)
    {
        FeedFreshness.Cutoff(NewYorkAt(day, 14, 0), NewYork, WorkingDays.Weekdays, Eight).Should().BeNull();
    }

    [Fact]
    public void Cutoff_OnAHoliday_IsNone_AndTheNextWorkingDayHasOne()
    {
        var days = new WorkingDays(new Dictionary<DateOnly, string> { [new DateOnly(2026, 11, 26)] = "Thanksgiving" });
        FeedFreshness.Cutoff(new DateTimeOffset(2026, 11, 26, 11, 0, 0, TimeSpan.FromHours(-5)), NewYork, days, Eight).Should().BeNull();
        FeedFreshness.Cutoff(new DateTimeOffset(2026, 11, 27, 11, 0, 0, TimeSpan.FromHours(-5)), NewYork, days, Eight)
            .Should().Be(new DateTimeOffset(2026, 11, 27, 8, 0, 0, TimeSpan.FromHours(-5)));
    }

    [Fact]
    public void Cutoff_IsInTheOrganizationsZone()
    {
        // 12:30 UTC: 7:30 in New York (before its cutoff), 12:30 in London (after its).
        var now = new DateTimeOffset(2026, 11, 4, 12, 30, 0, TimeSpan.Zero);
        FeedFreshness.Cutoff(now, NewYork, WorkingDays.Weekdays, Eight).Should().BeNull();
        FeedFreshness.Cutoff(now, London, WorkingDays.Weekdays, Eight).Should().Be(new DateTimeOffset(2026, 11, 4, 8, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Cutoff_TheDayIsTheZonesDay_NotUtcs()
    {
        // Friday 21:00 in New York is Saturday 02:00 UTC: still Friday's cutoff, not a weekend's none.
        var friday = new DateTimeOffset(2026, 11, 6, 21, 0, 0, TimeSpan.FromHours(-5));
        FeedFreshness.Cutoff(friday.ToUniversalTime(), NewYork, WorkingDays.Weekdays, Eight).Should().Be(NewYorkAt(6, 8, 0));
    }

    [Fact]
    public void Cutoff_FollowsDaylightSaving()
    {
        // Monday 2 November 2026 is the first working day after the clocks went back (EST, UTC-5); Friday 30 October was EDT.
        FeedFreshness.Cutoff(new DateTimeOffset(2026, 10, 30, 13, 0, 0, TimeSpan.Zero), NewYork, WorkingDays.Weekdays, Eight)
            .Should().Be(new DateTimeOffset(2026, 10, 30, 8, 0, 0, TimeSpan.FromHours(-4)));
        FeedFreshness.Cutoff(new DateTimeOffset(2026, 11, 2, 14, 0, 0, TimeSpan.Zero), NewYork, WorkingDays.Weekdays, Eight)
            .Should().Be(new DateTimeOffset(2026, 11, 2, 8, 0, 0, TimeSpan.FromHours(-5)));
    }

    [Fact]
    public void Cutoff_AtATimeTheClocksSkip_IsWhenTheyChange()
    {
        // A zone whose clocks go forward at 02:00 on Wednesday 10 March 2027 (real zones change on a weekend): a cutoff
        // of 02:30 that day, a time it skips, is when the clocks change.
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new DateTime(2027, 1, 1), new DateTime(2027, 12, 31), TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 10),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 2, 0, 0), 11, 3));
        var zone = TimeZoneInfo.CreateCustomTimeZone("Test/Wednesday", TimeSpan.FromHours(-5), "Test", "Test", "Test summer", [rule]);
        new DateOnly(2027, 3, 10).DayOfWeek.Should().Be(DayOfWeek.Wednesday);

        FeedFreshness.Cutoff(new DateTimeOffset(2027, 3, 10, 12, 0, 0, TimeSpan.Zero), zone, WorkingDays.Weekdays, new TimeOnly(2, 30))
            .Should().Be(new DateTimeOffset(2027, 3, 10, 3, 0, 0, TimeSpan.FromHours(-4)));
    }

    // --- what's counted --------------------------------------------------------------------------------------------

    private static readonly DateTimeOffset Cut = NewYorkAt(4, 8, 0);

    private static PolledSource Src(string name, DateTimeOffset? last, string? error = null) =>
        new(Guid.NewGuid(), name, SourceKind.Feed, last, null, error is null && last is not null ? 1 : null, error, last + TimeSpan.FromMinutes(15));

    [Fact]
    public void Of_NoSources_SaysNothing() => FeedFreshness.Of([], Cut).Should().BeNull();

    [Fact]
    public void Of_EveryFeedPulledSinceTheCutoff_SaysNothing()
    {
        FeedFreshness.Of([Src("A", Cut), Src("B", Cut.AddHours(1))], Cut).Should().BeNull("a pull at the cutoff's minute counts");
    }

    [Fact]
    public void Of_CountsThoseNotPulledSinceTheCutoff_AndTheFailingApart()
    {
        var f = FeedFreshness.Of([
            Src("Fresh", Cut.AddMinutes(30)),
            Src("Yesterday", Cut.AddHours(-13).AddMinutes(-20)),
            Src("Never", null),
            Src("Failing", Cut.AddMinutes(10), "The server answered 503."),
            Src("Failing yesterday", Cut.AddHours(-20), "Timed out."),
        ], Cut);

        f.Should().NotBeNull();
        f!.Sources.Should().Be(5);
        f.NotPulled.Should().Be(2, "the failing are counted apart, not as not pulled");
        f.Failing.Should().Be(2);
        f.LastPullUtc.Should().Be(Cut.AddMinutes(30));
    }

    [Fact]
    public void Of_OnlyAFailingFeed_StillSaysSo()
    {
        var f = FeedFreshness.Of([Src("Fresh", Cut.AddMinutes(30)), Src("Failing", Cut.AddMinutes(10), "Not found.")], Cut);
        f.Should().Match<FeedFreshness>(x => x.NotPulled == 0 && x.Failing == 1);
    }

    // --- how it's said ---------------------------------------------------------------------------------------------

    [Fact]
    public void Text_SaysHowManyOfHowMany_SinceWhen_AndTheLastPull()
    {
        var f = new FeedFreshness(9, 4, 0, Cut, NewYorkAt(3, 18, 40));
        f.Text(NewYork, NewYorkAt(4, 9, 0)).Should().Be("4 of 9 feeds not pulled since 8:00 am · last pull 6:40 pm yesterday");
    }

    [Fact]
    public void Text_WithFailing_CountsThemApart()
    {
        new FeedFreshness(9, 3, 1, Cut, NewYorkAt(3, 18, 40)).Text(NewYork, NewYorkAt(4, 9, 0))
            .Should().Be("3 of 9 feeds not pulled since 8:00 am · 1 failing · last pull 6:40 pm yesterday");
        new FeedFreshness(9, 0, 2, Cut, NewYorkAt(4, 8, 5)).Text(NewYork, NewYorkAt(4, 9, 0))
            .Should().Be("2 of 9 feeds failing · last pull 8:05 am");
    }

    [Fact]
    public void Text_OneFeed_AndNeverPulled()
    {
        new FeedFreshness(1, 1, 0, Cut, null).Text(NewYork, NewYorkAt(4, 9, 0)).Should().Be("1 of 1 feed not pulled since 8:00 am · never pulled");
    }

    [Theory]
    [InlineData(4, 7, 55, "7:55 am")]
    [InlineData(3, 18, 40, "6:40 pm yesterday")]
    [InlineData(30, 18, 40, "Fri 6:40 pm")]
    [InlineData(20, 9, 5, "Oct 20, 9:05 am")]
    public void Text_SaysTheLastPullAsAPersonWould(int day, int hour, int minute, string expected)
    {
        var month = day > 4 ? 10 : 11;
        var offset = month == 10 ? TimeSpan.FromHours(-4) : TimeSpan.FromHours(-5);
        var last = new DateTimeOffset(2026, month, day, hour, minute, 0, offset);
        new FeedFreshness(2, 1, 0, Cut, last).Text(NewYork, NewYorkAt(4, 9, 0)).Should().EndWith("last pull " + expected);
    }
}

/// <summary>Pull all: its progress line, its summary, and the guard that lets one run at a time.</summary>
public class PullAllTextTests
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    [Fact]
    public void Step_SaysWhichOfHowMany() =>
        new PullAllStep(3, 9, Guid.NewGuid(), "Sample news").Text.Should().Be("Pulling 3 of 9: Sample news…");

    [Fact]
    public void Summary_SaysWhatWasPulled_Skipped_AndFailed()
    {
        new PullAllResult(7, 12, ["A", "B"], [], ["Wire"], 0).Summary(Window)
            .Should().Be("Pulled 7 · 12 new items · 2 skipped (pulled in the last 15 min) · 1 failed: Wire");
    }

    [Fact]
    public void Summary_OneNewItem_NoneNew_AndBeingPulled()
    {
        new PullAllResult(1, 1, [], [], [], 0).Summary(Window).Should().Be("Pulled 1 · 1 new item");
        new PullAllResult(3, 0, [], ["C"], [], 0).Summary(Window).Should().Be("Pulled 3 · no new items · 1 skipped (being pulled)");
        new PullAllResult(0, 0, ["A"], [], ["X", "Y"], 0).Summary(TimeSpan.FromHours(1))
            .Should().Be("Pulled 0 · no new items · 1 skipped (pulled in the last 1 h) · 2 failed: X, Y");
    }

    [Fact]
    public void Summary_NothingToPull_SaysSo()
    {
        new PullAllResult(0, 0, ["A", "B"], [], [], 0).Summary(Window).Should().Be("Nothing to pull · 2 skipped (pulled in the last 15 min)");
    }

    [Fact]
    public void Summary_Stopped_SaysHowManyWerentReached()
    {
        new PullAllResult(2, 4, [], [], [], 3).Summary(Window).Should().Be("Pulled 2 · 4 new items · stopped: 3 not pulled");
    }

    [Fact]
    public void Guard_LetsOnePullAllRunAtATime()
    {
        var guard = new SourcePollGuard();
        guard.AllRunning.Should().BeFalse();
        guard.TryStartAll().Should().BeTrue();
        guard.AllRunning.Should().BeTrue();
        guard.TryStartAll().Should().BeFalse("a second press, by anyone, waits for the first run");
        guard.AllFinished();
        guard.AllRunning.Should().BeFalse();
        guard.TryStartAll().Should().BeTrue();
    }
}
