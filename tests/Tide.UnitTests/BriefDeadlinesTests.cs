using AwesomeAssertions;
using Tide.Application.Trends;
using Tide.Domain.Enums;

namespace Tide.UnitTests;

/// <summary>The rules a brief day's deadlines are judged by: when a brief counts as sent, and how the day went.</summary>
public class BriefDeadlinesTests
{
    private static readonly DateTimeOffset SendBy = new(2026, 9, 14, 15, 30, 0, TimeSpan.Zero), PostBy = SendBy.AddMinutes(30);

    [Fact]
    public void SentAt_IsWhenBothEditionsHadGone_AtTheirFirstSends()
    {
        var at = BriefDeadlines.SentAt([(Edition.Team, SendBy.AddMinutes(-20)), (Edition.Public, SendBy.AddMinutes(-5)), (Edition.Public, SendBy.AddMinutes(40))], []);
        at.Should().Be(SendBy.AddMinutes(-5));
    }

    [Fact]
    public void SentAt_IsNull_WhileAnEditionWithAnythingInItHasntGone()
    {
        BriefDeadlines.SentAt([(Edition.Team, SendBy)], []).Should().BeNull();
        BriefDeadlines.SentAt([], []).Should().BeNull();
    }

    [Fact]
    public void SentAt_DoesntWaitForAnEditionWithNothingInIt()
    {
        BriefDeadlines.SentAt([(Edition.Team, SendBy)], [Edition.Public]).Should().Be(SendBy);
    }

    [Fact]
    public void SentAt_ForABriefDistributedWithEmailOff_IsWhenItWasMarked()
    {
        BriefDeadlines.SentAt([], [], SendBy.AddMinutes(-1)).Should().Be(SendBy.AddMinutes(-1));
        BriefDeadlines.SentAt([(Edition.Team, SendBy)], [], SendBy.AddMinutes(-1)).Should().BeNull("one edition was emailed after all, the other wasn't");
    }

    [Fact]
    public void Judge_OnTime_AtTheDeadlineItself()
    {
        BriefDeadlines.Judge(new DayFacts(SendBy, null, false), SendBy, null, SendBy.AddHours(5)).Should().Be((DayVerdict.OnTime, (TimeSpan?)null));
    }

    [Fact]
    public void Judge_Late_ByTheLaterOfSentAndPosted()
    {
        BriefDeadlines.Judge(new DayFacts(SendBy.AddMinutes(10), PostBy.AddMinutes(25), true), SendBy, PostBy, PostBy.AddHours(5))
            .Should().Be((DayVerdict.Late, (TimeSpan?)TimeSpan.FromMinutes(25)));
    }

    [Fact]
    public void Judge_Missed_OnceADeadlinePassesWithItUndone()
    {
        BriefDeadlines.Judge(new DayFacts(null, null, false), SendBy, null, SendBy.AddMinutes(1)).Verdict.Should().Be(DayVerdict.Missed);
        BriefDeadlines.Judge(new DayFacts(SendBy, null, true), SendBy, PostBy, PostBy.AddMinutes(1)).Verdict.Should().Be(DayVerdict.Missed);
    }

    [Fact]
    public void Judge_Pending_BeforeTheDeadline_UnlessAlreadyLate()
    {
        BriefDeadlines.Judge(new DayFacts(null, null, false), SendBy, null, SendBy.AddMinutes(-1)).Verdict.Should().Be(DayVerdict.Pending);
        BriefDeadlines.Judge(new DayFacts(SendBy, null, true), SendBy, PostBy, SendBy.AddMinutes(10)).Verdict.Should().Be(DayVerdict.Pending);
        BriefDeadlines.Judge(new DayFacts(SendBy.AddMinutes(5), null, true), SendBy, PostBy, SendBy.AddMinutes(10)).Verdict.Should().Be(DayVerdict.Late);
    }

    [Fact]
    public void Judge_WithoutARequiredPosting_IgnoresIt()
    {
        BriefDeadlines.Judge(new DayFacts(SendBy, null, false), SendBy, null, PostBy.AddHours(1)).Verdict.Should().Be(DayVerdict.OnTime);
    }

    [Fact]
    public void Tally_RateLeavesOutExcusedAndPendingDays()
    {
        new Tally(2, 1, 1, 3, 1).Rate.Should().Be(50);
        Tally.None.Rate.Should().BeNull();
    }
}
