using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Tide.Application.Security;
using Tide.Application.Trends;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.IntegrationTests;

/// <summary>
/// Brief timeliness: each business day's brief owner judged against the sent-by and posted-by times, from the send log
/// and the SharePoint records already stored (so the past is there from the first), with what each reader may see: an
/// analyst their own days, a manager or administrator everyone's, nobody else any.
/// </summary>
public class BriefTimelinessTests : IDisposable
{
    // Thursday 17 September 2026, 10:00 in New York (EDT, UTC-4): before today's 11:30 sent-by time.
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 14, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Mon = new(2026, 9, 14), Tue = new(2026, 9, 15), Wed = new(2026, 9, 16), Thu = new(2026, 9, 17);
    private const string Me = "S-1-5-21-TEST-1", Robin = "dev:robin", Sam = "dev:sam";

    private readonly TestClock _time = new(Now);
    private readonly TestDb _db;

    public BriefTimelinessTests() => _db = new TestDb(time: _time);

    public void Dispose() => _db.Dispose();

    // A local New York time on a day, as the instant it is.
    private static DateTimeOffset At(DateOnly day, int hour, int minute) => new(day.ToDateTime(new TimeOnly(hour, minute)), TimeSpan.FromHours(-4));

    private void As(string id, string name, params AppRole[] roles) =>
        (_db.User.UserId, _db.User.DisplayName, _db.User.Roles) = (id, name, roles.ToHashSet());

    // A brief for the day, presented by `analyst`, with one article at `tlp`, and its real and test sends.
    private async Task<Brief> Brief(DateOnly day, string analyst, string name, TlpLevel tlp = TlpLevel.Clear, bool sharePointRequired = false,
        DateTimeOffset? postedAt = null, params (Edition Edition, DateTimeOffset At, bool Test)[] sends)
    {
        await using var c = _db.Context();
        var article = new Article { Title = "Article for " + day, Tlp = tlp, AuthorId = Me, AuthorName = "Test Analyst" };
        var brief = new Brief
        {
            BriefDate = day, AnalystId = analyst, AnalystName = name, Status = sends.Any(s => !s.Test) ? BriefStatus.Sent : BriefStatus.Ready,
            SharePointRequired = sharePointRequired, SharePointPostedAtUtc = postedAt,
        };
        brief.Items.Add(new BriefItem { ArticleId = article.Id, Article = article });
        c.AddRange(article, brief);
        c.BriefSends.AddRange(sends.Select(s => new BriefSend
        {
            BriefId = brief.Id, BriefDate = day, Edition = s.Edition, IsTest = s.Test, Status = SendStatus.Sent, SentAtUtc = s.At,
        }));
        await c.SaveChangesAsync();
        return brief;
    }

    private static (Edition, DateTimeOffset, bool) Sent(Edition e, DateTimeOffset at) => (e, at, false);

    private async Task Week(string analyst, string name)
    {
        await using var c = _db.Context();
        c.RotationWeeks.Add(new RotationWeek { WeekStart = Mon, AnalystId = analyst, AnalystName = name, UpdatedAtUtc = Now, UpdatedBy = "test" });
        await c.SaveChangesAsync();
    }

    private async Task<TimelinessReport> Report(PeriodKind by = PeriodKind.Month, string? who = null) => await _db.Timeliness().BuildAsync(by, who);

    [Fact]
    public async Task PastBriefs_AreJudgedFromTheSendLogAlreadyStored()
    {
        As(Me, "Test Analyst", AppRole.Analyst);
        await Brief(Mon, Me, "Test Analyst", sends: [Sent(Edition.Team, At(Mon, 10, 50)), Sent(Edition.Public, At(Mon, 11, 0))]);
        // The second edition went 15 minutes after the deadline: the brief went when both had.
        await Brief(Tue, Me, "Test Analyst", sends: [Sent(Edition.Team, At(Tue, 11, 0)), Sent(Edition.Public, At(Tue, 11, 45))]);
        // A test copy before the deadline doesn't count; the real one after it does.
        await Brief(Wed, Me, "Test Analyst", sends: [(Edition.Team, At(Wed, 9, 0), true), (Edition.Public, At(Wed, 9, 0), true),
            Sent(Edition.Team, At(Wed, 12, 0)), Sent(Edition.Public, At(Wed, 12, 0))]);

        var days = (await Report()).Days.ToDictionary(d => d.Date);

        days[Mon].Verdict.Should().Be(DayVerdict.OnTime);
        days[Mon].SentAtUtc.Should().Be(At(Mon, 11, 0));
        days[Tue].Verdict.Should().Be(DayVerdict.Late);
        days[Tue].LateBy.Should().Be(TimeSpan.FromMinutes(15));
        days[Wed].Verdict.Should().Be(DayVerdict.Late);
        days[Wed].LateBy.Should().Be(TimeSpan.FromMinutes(30));
        days.Should().NotContainKey(Thu, "nobody has today's brief, and the rotation names nobody");
    }

    [Fact]
    public async Task OneEditionGone_IsSentOnlyWhenTheOtherHadNothingInIt()
    {
        As(Me, "Test Analyst", AppRole.Analyst);
        // An AMBER article: the company-wide edition (GREEN) has nothing in it, so the team edition alone is the brief sent.
        await Brief(Mon, Me, "Test Analyst", TlpLevel.Amber, sends: [Sent(Edition.Team, At(Mon, 11, 0))]);
        // A CLEAR article went to the team only: the company-wide edition never went.
        await Brief(Tue, Me, "Test Analyst", TlpLevel.Clear, sends: [Sent(Edition.Team, At(Tue, 11, 0))]);

        var days = (await Report()).Days.ToDictionary(d => d.Date);

        days[Mon].Verdict.Should().Be(DayVerdict.OnTime);
        days[Tue].Verdict.Should().Be(DayVerdict.Missed);
        BriefTimelinessService.Verdict(days[Tue]).Should().Be("not sent");
    }

    [Fact]
    public async Task WhereSharePointIsRequired_ThePostedTimeCountsToo()
    {
        _db.DistributionOptions.RequireSharePoint = true;
        As(Me, "Test Analyst", AppRole.Analyst);
        var both = new[] { Sent(Edition.Team, At(Mon, 11, 0)), Sent(Edition.Public, At(Mon, 11, 0)) };
        await Brief(Mon, Me, "Test Analyst", sharePointRequired: true, postedAt: At(Mon, 11, 55), sends: both);
        await Brief(Tue, Me, "Test Analyst", sharePointRequired: true, postedAt: At(Tue, 12, 40),
            sends: [Sent(Edition.Team, At(Tue, 11, 0)), Sent(Edition.Public, At(Tue, 11, 0))]);
        await Brief(Wed, Me, "Test Analyst", sharePointRequired: true,
            sends: [Sent(Edition.Team, At(Wed, 11, 0)), Sent(Edition.Public, At(Wed, 11, 0))]);

        var days = (await Report()).Days.ToDictionary(d => d.Date);

        days[Mon].Verdict.Should().Be(DayVerdict.OnTime);
        days[Tue].Verdict.Should().Be(DayVerdict.Late);
        days[Tue].LateBy.Should().Be(TimeSpan.FromMinutes(40));
        days[Wed].Verdict.Should().Be(DayVerdict.Missed);
        BriefTimelinessService.Verdict(days[Wed]).Should().Be("not posted");
    }

    [Fact]
    public async Task WithoutSharePointRequired_OnlySendingCounts()
    {
        As(Me, "Test Analyst", AppRole.Analyst);
        // Required when it went, but not on this install now: only the sending is judged.
        await Brief(Mon, Me, "Test Analyst", sharePointRequired: true,
            sends: [Sent(Edition.Team, At(Mon, 11, 0)), Sent(Edition.Public, At(Mon, 11, 0))]);

        var day = (await Report()).Days.Single();

        day.Verdict.Should().Be(DayVerdict.OnTime);
        day.PostBy.Should().BeNull();
    }

    [Fact]
    public async Task TheOwner_IsTheBriefsPresenter_ElseTheRotations_AndTodayIsntJudgedBeforeItsDeadline()
    {
        As("admin", "Admin", AppRole.Admin);
        await Week(Robin, "Robin");
        // Monday's brief was handed to Sam; Tuesday's has no brief (missed, Robin's); Wednesday went on time.
        await Brief(Mon, Sam, "Sam", sends: [Sent(Edition.Team, At(Mon, 11, 0)), Sent(Edition.Public, At(Mon, 11, 0))]);
        await Brief(Wed, Robin, "Robin", sends: [Sent(Edition.Team, At(Wed, 11, 0)), Sent(Edition.Public, At(Wed, 11, 0))]);

        var days = (await Report()).Days.ToDictionary(d => d.Date);

        days[Mon].AnalystId.Should().Be(Sam);
        days[Tue].AnalystId.Should().Be(Robin);
        days[Tue].Verdict.Should().Be(DayVerdict.Missed);
        days[Thu].AnalystId.Should().Be(Robin);
        days[Thu].Verdict.Should().Be(DayVerdict.Pending, "it's 10:00, before the 11:30 deadline");
        days[Thu].Counts.Should().BeFalse();
    }

    [Fact]
    public async Task ASwapOrCover_MovesTheExpectation()
    {
        As("admin", "Admin", AppRole.Admin);
        await Week(Robin, "Robin");
        await Brief(Mon, Robin, "Robin", sends: [Sent(Edition.Team, At(Mon, 11, 0)), Sent(Edition.Public, At(Mon, 11, 0))]);
        await using (var c = _db.Context())
        {
            c.RotationOverrides.Add(new RotationOverride { Date = Tue, AnalystId = Sam, AnalystName = "Sam" });
            await c.SaveChangesAsync();
        }

        var days = (await Report()).Days.ToDictionary(d => d.Date);

        days[Tue].AnalystId.Should().Be(Sam, "Sam covered Tuesday, and no brief was made");
    }

    [Fact]
    public async Task HolidaysAndDaysBeforeTheFirstBrief_AreNotCounted()
    {
        As("admin", "Admin", AppRole.Admin);
        await Week(Robin, "Robin");
        _db.Clock.UseHolidays(new Dictionary<DateOnly, string> { [Wed] = "Founders' day" });
        await Brief(Tue, Robin, "Robin", sends: [Sent(Edition.Team, At(Tue, 11, 0)), Sent(Edition.Public, At(Tue, 11, 0))]);

        var dates = (await Report()).Days.Select(d => d.Date).ToList();

        dates.Should().Equal(Thu, Tue);
    }

    [Fact]
    public async Task AnAnalyst_SeesOnlyTheirOwnDays_AndIsRefusedAnyoneElses()
    {
        await Week(Robin, "Robin");
        await Brief(Mon, Me, "Test Analyst", sends: [Sent(Edition.Team, At(Mon, 11, 0)), Sent(Edition.Public, At(Mon, 11, 0))]);
        await Brief(Tue, Robin, "Robin", sends: [Sent(Edition.Team, At(Tue, 12, 0)), Sent(Edition.Public, At(Tue, 12, 0))]);

        As(Me, "Test Analyst", AppRole.Analyst);
        var mine = await Report();
        mine.Everyone.Should().BeFalse();
        mine.Days.Should().OnlyContain(d => d.AnalystId == Me).And.ContainSingle();
        mine.People.Should().ContainSingle().Which.AnalystId.Should().Be(Me);
        mine.Team.Should().BeNull();
        mine.Analysts.Should().Equal((Me, "Test Analyst"));
        (await Report(who: Me)).Days.Should().ContainSingle();

        var asked = () => Report(who: Robin);
        await asked.Should().ThrowAsync<ForbiddenException>();
        var csv = BriefTimelinessService.ToCsv(mine, _db.Clock.Zone);
        csv.Should().NotContain("Robin");
    }

    [Theory]
    [InlineData(AppRole.Viewer)]
    [InlineData(AppRole.Reader)]
    [InlineData(AppRole.ActionOwner)]
    [InlineData(AppRole.Visitor)]
    public async Task EveryoneElse_SeesNone(AppRole role)
    {
        As("someone", "Someone", role);
        var read = () => Report();
        await read.Should().ThrowAsync<ForbiddenException>();
        _db.Timeliness().CanRead.Should().BeFalse();
    }

    [Theory]
    [InlineData(AppRole.Manager)]
    [InlineData(AppRole.Admin)]
    public async Task AManagerOrAdministrator_SeesEveryone_AndTheTeamsFigures(AppRole role)
    {
        await Week(Robin, "Robin");
        await Brief(Mon, Me, "Test Analyst", sends: [Sent(Edition.Team, At(Mon, 11, 0)), Sent(Edition.Public, At(Mon, 11, 0))]);
        await Brief(Tue, Robin, "Robin", sends: [Sent(Edition.Team, At(Tue, 12, 0)), Sent(Edition.Public, At(Tue, 12, 0))]);
        As("lead", "Lee", role);

        var all = await Report(PeriodKind.Week);

        all.Everyone.Should().BeTrue();
        all.People.Select(p => p.AnalystId).Should().BeEquivalentTo([Me, Robin]);
        all.Team.Should().Be(new Tally(1, 1, 1, 0, 1), "Monday on time, Tuesday late, Wednesday missed (Robin's, no brief), today to come");
        all.Team!.Rate.Should().Be(33.3);
        all.Periods.Should().HaveCount(BriefTimelinessService.Weeks);
        (await Report(who: Robin)).Days.Should().OnlyContain(d => d.AnalystId == Robin);
    }

    [Fact]
    public async Task AManager_ExcusesALateDay_WhichNoLongerCounts_AndTheAnalystSeesWhy()
    {
        await Brief(Tue, Me, "Test Analyst", sends: [Sent(Edition.Team, At(Tue, 12, 0)), Sent(Edition.Public, At(Tue, 12, 0))]);
        As("lead", "Lee", AppRole.Manager);

        var excuse = await _db.Timeliness().ExcuseAsync(Tue, "  Told me at 9:00: the relay was down.  ");

        excuse.Reason.Should().Be("Told me at 9:00: the relay was down.");
        excuse.AnalystId.Should().Be(Me);
        As(Me, "Test Analyst", AppRole.Analyst);
        var day = (await Report()).Days.Single(d => d.Date == Tue);
        day.Excused.Should().BeTrue();
        day.Counts.Should().BeFalse();
        day.Excuse!.ExcusedByName.Should().Be("Lee");
        (await Report()).People.Single().Total.Should().Be(new Tally(0, 0, 0, 1, 0));
        await using var c = _db.Context();
        (await c.AuditLog.AnyAsync(e => e.EntityType == nameof(BriefExcuse) && e.Action == "Added" && e.UserName == "Lee")).Should().BeTrue();
    }

    [Fact]
    public async Task AnExcuse_CanBeUndone_AsRead_AndTheUndoIsAudited()
    {
        await Brief(Tue, Me, "Test Analyst", sends: [Sent(Edition.Team, At(Tue, 12, 0)), Sent(Edition.Public, At(Tue, 12, 0))]);
        As("lead", "Lee", AppRole.Manager);
        var excuse = await _db.Timeliness().ExcuseAsync(Tue, "Warned at 9:00");

        var stale = () => _db.Timeliness().UndoExcuseAsync(Tue, Guid.NewGuid());
        await stale.Should().ThrowAsync<InvalidOperationException>();
        var again = () => _db.Timeliness().ExcuseAsync(Tue, "Another reason");
        await again.Should().ThrowAsync<InvalidOperationException>().WithMessage("*already excused*");

        await _db.Timeliness().UndoExcuseAsync(Tue, excuse.EditVersion);

        (await Report()).Days.Single(d => d.Date == Tue).Verdict.Should().Be(DayVerdict.Late);
        (await Report()).Days.Single(d => d.Date == Tue).Excused.Should().BeFalse();
        await using var c = _db.Context();
        (await c.AuditLog.AnyAsync(e => e.EntityType == nameof(BriefExcuse) && e.Action == "Deleted")).Should().BeTrue();
        var gone = () => _db.Timeliness().UndoExcuseAsync(Tue, excuse.EditVersion);
        await gone.Should().ThrowAsync<InvalidOperationException>();
    }

    [Theory]
    [InlineData(AppRole.Analyst)]
    [InlineData(AppRole.Viewer)]
    public async Task OnlyAManagerOrAdministrator_Excuses(AppRole role)
    {
        await Brief(Tue, Me, "Test Analyst", sends: [Sent(Edition.Team, At(Tue, 12, 0)), Sent(Edition.Public, At(Tue, 12, 0))]);
        As(Me, "Test Analyst", role);

        var excuse = () => _db.Timeliness().ExcuseAsync(Tue, "My own excuse");
        await excuse.Should().ThrowAsync<ForbiddenException>();
        var undo = () => _db.Timeliness().UndoExcuseAsync(Tue, Guid.NewGuid());
        await undo.Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task AnExcuse_NeedsAReason_AndADayThatWasDue()
    {
        await Brief(Tue, Me, "Test Analyst", sends: [Sent(Edition.Team, At(Tue, 12, 0)), Sent(Edition.Public, At(Tue, 12, 0))]);
        As("lead", "Lee", AppRole.Admin);
        var svc = _db.Timeliness();

        await ((Func<Task>)(() => svc.ExcuseAsync(Tue, "  "))).Should().ThrowAsync<ArgumentException>();
        await ((Func<Task>)(() => svc.ExcuseAsync(Tue, new string('x', BriefExcuse.MaxReason + 1)))).Should().ThrowAsync<ArgumentException>();
        await ((Func<Task>)(() => svc.ExcuseAsync(Thu.AddDays(1), "Tomorrow"))).Should().ThrowAsync<ArgumentException>();
        await ((Func<Task>)(() => svc.ExcuseAsync(new DateOnly(2026, 9, 12), "A Saturday"))).Should().ThrowAsync<ArgumentException>();
        await ((Func<Task>)(() => svc.ExcuseAsync(Mon, "Nobody's day"))).Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ABriefSentWithNothingInTheLog_IsntJudged()
    {
        As(Me, "Test Analyst", AppRole.Analyst);
        var brief = await Brief(Mon, Me, "Test Analyst");
        await using (var c = _db.Context())
        {
            (await c.Briefs.SingleAsync(x => x.Id == brief.Id)).Status = BriefStatus.Sent;
            await c.SaveChangesAsync();
        }

        (await Report()).Days.Should().BeEmpty("there's no time to judge it by");
    }

    [Fact]
    public async Task ADistributedBrief_WithEmailOff_WentWhenItWasMarked()
    {
        As(Me, "Test Analyst", AppRole.Analyst);
        var brief = await Brief(Mon, Me, "Test Analyst");
        await using (var c = _db.Context())
        {
            var b = await c.Briefs.SingleAsync(x => x.Id == brief.Id);
            (b.Status, b.DistributedAtUtc) = (BriefStatus.Sent, At(Mon, 11, 10));
            await c.SaveChangesAsync();
        }

        (await Report()).Days.Single().Verdict.Should().Be(DayVerdict.OnTime);
    }
}
