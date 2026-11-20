using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Tide.Application.Abstractions;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Web.Services;

namespace Tide.IntegrationTests;

/// <summary>A meeting everyone left without ending it: the attendance record still says when each person left, as far
/// as the meeting saw, written by the app itself with no one signed in.</summary>
public sealed class SweptMeetingAttendanceTests : IDisposable
{
    private static readonly DateOnly Day = new(2026, 9, 1);
    private static readonly DateTimeOffset Nine = new(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly TestDb _db = new();
    private readonly TestClock _time = new(Nine);
    private readonly LiveMeetings _meetings;
    private readonly ServiceProvider _services;

    public SweptMeetingAttendanceTests()
    {
        _meetings = new LiveMeetings(_time);
        // No one signed in: the sweep is the app's.
        (_db.User.IsAuthenticated, _db.User.UserId, _db.User.Roles) = (false, "system", new HashSet<AppRole>());
        _services = new ServiceCollection().AddSingleton<IAppDbContextFactory>(_db).BuildServiceProvider();
    }

    public void Dispose()
    {
        _services.Dispose();
        _meetings.Dispose();
        _db.Dispose();
    }

    private async Task<Guid> BriefWith(params MeetingAttendee[] people)
    {
        await using var ctx = _db.Context();
        var brief = new Brief { BriefDate = Day, AnalystId = "pat", AnalystName = "Pat" };
        ctx.Briefs.Add(brief);
        foreach (var p in people) p.BriefId = brief.Id;
        ctx.MeetingAttendees.AddRange(people);
        await ctx.SaveChangesAsync();
        return brief.Id;
    }

    private async Task<Dictionary<string, DateTimeOffset?>> LeftTimes(Guid brief)
    {
        await using var ctx = _db.Context();
        return await ctx.MeetingAttendees.Where(a => a.BriefId == brief).ToDictionaryAsync(a => a.Name, a => a.LeftAtUtc);
    }

    private MeetingAttendanceHostedService Hosted() =>
        new(_meetings, _services.GetRequiredService<IServiceScopeFactory>(), NullLogger<MeetingAttendanceHostedService>.Instance);

    // Swept, through the hosted service as the app runs it; waits for its write (it's off the sweep's thread).
    private async Task SweepAndWait(Guid brief, string name)
    {
        using var hosted = Hosted();
        await hosted.StartAsync(CancellationToken.None);
        _meetings.Sweep();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while ((await LeftTimes(brief))[name] is null && DateTime.UtcNow < deadline) await Task.Delay(20);
        await hosted.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ASweptMeeting_GivesWhoeverItSawLeaveTheirTime_KeepsAnEarlierOne_AndAsksNoOneSignedIn()
    {
        var samLeft = Nine.AddMinutes(5);
        var brief = await BriefWith(
            new MeetingAttendee { UserId = "pat", Name = "Pat", JoinedAtUtc = Nine },
            new MeetingAttendee { UserId = "sam", Name = "Sam", JoinedAtUtc = Nine, LeftAtUtc = samLeft },
            new MeetingAttendee { Name = "Dana from Legal" });
        _meetings.Start(Day, brief, "pat", "Pat", "c-pat", Guid.NewGuid(), 0);
        var sam = _meetings.Join(Day, "sam", "Sam", false, "c-sam", Guid.NewGuid(), 0)!;
        _time.Now = Nine.AddMinutes(10);
        sam.Dispose();
        _time.Now = Nine.AddMinutes(20);
        _meetings.SetConnected("c-pat", false);   // the presenter's lid closed: last seen now
        _time.Now += LiveMeetings.Abandoned;

        await SweepAndWait(brief, "Pat");

        _meetings.Get(Day).Should().BeNull();
        (await LeftTimes(brief)).Should().BeEquivalentTo(new Dictionary<string, DateTimeOffset?>
        {
            ["Pat"] = Nine.AddMinutes(20),   // when their connection went, not the sweep's time
            ["Sam"] = samLeft,               // left already: kept
            ["Dana from Legal"] = null,      // added by name: no times
        });
        await using var ctx = _db.Context();
        (await ctx.AuditLog.Where(e => e.EntityType == nameof(MeetingAttendee) && e.Action == "Modified").Select(e => e.UserName).ToListAsync())
            .Should().ContainSingle().Which.Should().Be("system");
    }

    [Fact]
    public async Task SomeoneTheSweptMeetingNeverSaw_StaysOpen_RatherThanGivenTheSweepsTime()
    {
        var brief = await BriefWith(
            new MeetingAttendee { UserId = "pat", Name = "Pat", JoinedAtUtc = Nine },
            new MeetingAttendee { UserId = "lee", Name = "Lee", JoinedAtUtc = Nine.AddDays(-1) });   // from a meeting a restart lost
        _meetings.Start(Day, brief, "pat", "Pat", "c-pat", Guid.NewGuid(), 0);
        _time.Now = Nine.AddMinutes(30);
        _meetings.RemoveCircuit("c-pat");
        _time.Now += LiveMeetings.Abandoned;

        await SweepAndWait(brief, "Pat");

        var left = await LeftTimes(brief);
        left["Pat"].Should().Be(Nine.AddMinutes(30));
        left["Lee"].Should().BeNull("the meeting has no time for them");
    }

    [Fact]
    public async Task ALaterEnd_OverwritesATimeTheSweepRecorded()
    {
        var brief = await BriefWith(new MeetingAttendee { UserId = "pat", Name = "Pat", JoinedAtUtc = Nine });
        _meetings.Start(Day, brief, "pat", "Pat", "c-pat", Guid.NewGuid(), 0);
        _time.Now = Nine.AddMinutes(10);
        _meetings.RemoveCircuit("c-pat");
        _time.Now += LiveMeetings.Abandoned;
        await SweepAndWait(brief, "Pat");
        (await LeftTimes(brief))["Pat"].Should().Be(Nine.AddMinutes(10));

        // Started again and ended by its presenter: End's times are the record's.
        (_db.User.IsAuthenticated, _db.User.UserId, _db.User.Roles) = (true, "pat", new HashSet<AppRole> { AppRole.Analyst });
        var ended = Nine.AddHours(1);
        await _db.Meetings(_time).LeftAsync(brief, new Dictionary<string, DateTimeOffset> { ["pat"] = ended });

        (await LeftTimes(brief))["Pat"].Should().Be(ended);
    }
}
