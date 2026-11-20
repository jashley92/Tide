using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Tide.Application.Abstractions;
using Tide.Application.Briefs;
using Tide.Domain.Entities;
using Tide.Web.Services;

namespace Tide.IntegrationTests;

/// <summary>A meeting nobody ended (everyone left, or the app restarted): the attendance record still says when each
/// person left, written by the app itself with no one signed in.</summary>
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
        // No one signed in: the closer is the app's, as the sweep and start-up are.
        (_db.User.IsAuthenticated, _db.User.UserId, _db.User.Roles) = (false, "system", new HashSet<Tide.Domain.Enums.AppRole>());
        _services = new ServiceCollection().AddSingleton<IAppDbContextFactory>(_db).AddScoped<AttendanceCloser>().BuildServiceProvider();
    }

    public void Dispose()
    {
        _services.Dispose();
        _meetings.Dispose();
        _db.Dispose();
    }

    private Task<Guid> BriefWith(params MeetingAttendee[] people) => BriefWith(Day, people);

    private async Task<Guid> BriefWith(DateOnly day, params MeetingAttendee[] people)
    {
        await using var ctx = _db.Context();
        var brief = new Brief { BriefDate = day, AnalystId = "pat", AnalystName = "Pat" };
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
        new(_meetings, _services.GetRequiredService<IServiceScopeFactory>(), _time, NullLogger<MeetingAttendanceHostedService>.Instance);

    [Fact]
    public async Task ASweptMeeting_GivesEveryoneStillThereALeftTime_WhenLastSeen_AndKeepsTheTimeOfWhoeverLeft()
    {
        var samLeft = Nine.AddMinutes(5);
        var brief = await BriefWith(
            new MeetingAttendee { UserId = "pat", Name = "Pat", JoinedAtUtc = Nine },
            new MeetingAttendee { UserId = "sam", Name = "Sam", JoinedAtUtc = Nine, LeftAtUtc = samLeft },
            new MeetingAttendee { UserId = "lee", Name = "Lee", JoinedAtUtc = Nine },
            new MeetingAttendee { Name = "Dana from Legal" });
        var pat = _meetings.Start(Day, brief, "pat", "Pat", "c-pat", Guid.NewGuid(), 0);
        var sam = _meetings.Join(Day, "sam", "Sam", false, "c-sam", Guid.NewGuid(), 0)!;
        DroppedMeeting? dropped = null;
        _meetings.Dropped += d => dropped = d;

        _time.Now = Nine.AddMinutes(10);
        sam.Dispose();
        _time.Now = Nine.AddMinutes(20);
        _meetings.SetConnected("c-pat", false);   // the presenter's lid closed: last seen now
        _time.Now += LiveMeetings.Abandoned;
        _meetings.Sweep();

        _meetings.Get(Day).Should().BeNull();
        dropped.Should().NotBeNull().And.Match<DroppedMeeting>(d => d.BriefId == brief && d.AtUtc == _time.Now);
        await new AttendanceCloser(_db).CloseAsync(brief, dropped!.LastSeenUtc, dropped.AtUtc);

        (await LeftTimes(brief)).Should().BeEquivalentTo(new Dictionary<string, DateTimeOffset?>
        {
            ["Pat"] = Nine.AddMinutes(20),   // when their connection went
            ["Sam"] = samLeft,               // left already: kept
            ["Lee"] = _time.Now,             // not seen in this meeting: the sweep's time
            ["Dana from Legal"] = null,      // added by name: no times
        });
        await using var ctx = _db.Context();
        (await ctx.AuditLog.Where(e => e.EntityType == nameof(MeetingAttendee) && e.Action == "Modified").Select(e => e.UserName).ToListAsync())
            .Should().HaveCount(2).And.OnlyContain(n => n == "system");
        pat.Dispose();
    }

    [Fact]
    public async Task TheSweep_ClosesTheRecord_ThroughTheHostedService_AndAgainIsNoChange()
    {
        var brief = await BriefWith(new MeetingAttendee { UserId = "pat", Name = "Pat", JoinedAtUtc = Nine });
        _meetings.Start(Day, brief, "pat", "Pat", "c-pat", Guid.NewGuid(), 0);
        using var hosted = Hosted();
        await hosted.StartAsync(CancellationToken.None);   // the meeting is live: start-up leaves it
        _time.Now = Nine.AddMinutes(30);
        _meetings.RemoveCircuit("c-pat");
        _time.Now += LiveMeetings.Abandoned;

        _meetings.Sweep();

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while ((await LeftTimes(brief))["Pat"] is null && DateTime.UtcNow < deadline) await Task.Delay(20);
        (await LeftTimes(brief))["Pat"].Should().Be(Nine.AddMinutes(30));

        (await new AttendanceCloser(_db).CloseAsync(brief, new Dictionary<string, DateTimeOffset>(), _time.Now.AddHours(1)))
            .Should().Be(0, "closed already: the time stays");
        await hosted.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AtStartUp_WhoeverARestartLeftInAMeeting_IsClosed_ButNotSomeoneInAMeetingLiveAgain()
    {
        var lastRun = await BriefWith(new MeetingAttendee { UserId = "pat", Name = "Pat", JoinedAtUtc = Nine.AddDays(-1) },
            new MeetingAttendee { UserId = "sam", Name = "Sam", JoinedAtUtc = Nine.AddDays(-1), LeftAtUtc = Nine.AddDays(-1).AddMinutes(5) });
        var today = await BriefWith(Day.AddDays(1), new MeetingAttendee { UserId = "lee", Name = "Lee", JoinedAtUtc = Nine });
        _meetings.Start(Day.AddDays(1), today, "lee", "Lee", "c-lee", Guid.NewGuid(), 0);

        using var hosted = Hosted();
        await hosted.StartAsync(CancellationToken.None);

        (await LeftTimes(lastRun)).Should().BeEquivalentTo(new Dictionary<string, DateTimeOffset?>
        {
            ["Pat"] = _time.Now, ["Sam"] = Nine.AddDays(-1).AddMinutes(5),
        });
        (await LeftTimes(today))["Lee"].Should().BeNull("their meeting is live");
        await hosted.StopAsync(CancellationToken.None);
    }
}
