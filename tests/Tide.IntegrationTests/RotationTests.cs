using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tide.Application.Actions;
using Tide.Application.Briefs;
using Tide.Application.Org;
using Tide.Application.Rotation;
using Tide.Application.Security;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.IntegrationTests;

public class RotationServiceTests : IDisposable
{
    private readonly TestDb _db = new(connectionPerContext: true);   // some tests race two approvals

    private const string Me = "S-1-5-21-TEST-1", Robin = "dev:robin", Sam = "dev:sam";

    // Next week and the week after: always in the future, whatever day the tests run.
    private readonly DateOnly _week1, _week2;

    public RotationServiceTests()
    {
        _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };
        _week1 = RotationService.WeekStart(_db.Clock.Today).AddDays(7);
        _week2 = _week1.AddDays(7);
    }

    public void Dispose() => _db.Dispose();

    private async Task Roster()
    {
        var r = _db.Rotation();
        await r.AddMemberAsync(Me, "Test Analyst");
        await r.AddMemberAsync(Robin, "Robin");
        await r.AddMemberAsync(Sam, "Sam");
        await r.SaveManagerEmailsAsync("manager@example.org");
        await using var ctx = _db.Context();
        ctx.Users.AddRange(
            new AppUser { Sid = Me, DisplayName = "Test Analyst", Email = "me@example.org", LastSeenUtc = DateTimeOffset.UtcNow },
            new AppUser { Sid = Robin, DisplayName = "Robin", Email = "robin@example.org", LastSeenUtc = DateTimeOffset.UtcNow });
        await ctx.SaveChangesAsync();
    }

    private void As(string id, params AppRole[] roles)
    {
        _db.User.UserId = id;
        _db.User.Roles = roles.Length == 0 ? new HashSet<AppRole> { AppRole.Analyst } : roles.ToHashSet();
    }

    [Fact]
    public async Task Generate_FillsEmptyWeeksInRosterOrder_CarryingOnAfterTheLastAssigned()
    {
        await Roster();
        var r = _db.Rotation();
        await r.SetWeekAsync(_week1, Robin);

        (await r.GenerateAsync(_week1, 4)).Should().Be(3, "the first week was already Robin's");

        var weeks = (await r.CalendarAsync(_week1, _week1.AddDays(27))).GroupBy(d => RotationService.WeekStart(d.Date)).Select(g => g.First().RegularName).ToList();
        weeks.Should().Equal("Robin", "Sam", "Test Analyst", "Robin");
    }

    [Fact]
    public async Task Cover_IsApprovedByThePartnerOnly_ThenTheDaysMove_AndTheManagerIsTold()
    {
        await Roster();
        await _db.Rotation().SetWeekAsync(_week1, Me);
        As(Me);

        var change = await _db.Rotation().RequestAsync(new RotationRequest { PartnerId = Robin, GiveFrom = _week1, GiveTo = _week1.AddDays(2), Note = "Conference" });

        _db.Email.Sent.Single().To.Should().Equal("robin@example.org");
        _db.Email.Sent.Single().Html.Should().Contain("Robin covers Test Analyst").And.Contain("Conference");
        var selfApprove = () => _db.Rotation().ApproveAsync(change.Id);
        await selfApprove.Should().ThrowAsync<ForbiddenException>("only the colleague taking the days on approves");

        As(Robin);
        await _db.Rotation().ApproveAsync(change.Id, "Happy to");

        var days = await _db.Rotation().CalendarAsync(_week1, _week1.AddDays(4));
        days.Select(d => d.AnalystName).Should().Equal("Robin", "Robin", "Robin", "Test Analyst", "Test Analyst");
        days[0].IsChanged.Should().BeTrue();
        days[0].RegularName.Should().Be("Test Analyst");
        _db.Email.Sent.Should().Contain(m => m.To.Contains("manager@example.org") && m.Subject.Contains("Robin covers Test Analyst"));
        _db.Email.Sent.Should().Contain(m => m.To.Contains("me@example.org") && m.Html.Contains("Happy to"));
    }

    [Fact]
    public async Task Swap_WholeWeeks_TradesEveryDay()
    {
        await Roster();
        var r = _db.Rotation();
        await r.SetWeekAsync(_week1, Me);
        await r.SetWeekAsync(_week2, Robin);
        As(Me);

        var change = await _db.Rotation().RequestAsync(new RotationRequest
        {
            Kind = RotationChangeKind.Swap, PartnerId = Robin, GiveFrom = _week1, GiveTo = _week1.AddDays(4), TakeFrom = _week2, TakeTo = _week2.AddDays(4),
        });
        As(Robin);
        await _db.Rotation().ApproveAsync(change.Id);

        var days = await _db.Rotation().CalendarAsync(_week1, _week2.AddDays(4));
        days.Take(5).Should().OnlyContain(d => d.AnalystId == Robin);
        days.Skip(5).Should().OnlyContain(d => d.AnalystId == Me);
    }

    [Fact]
    public async Task Request_MustBeForDaysYouHave_InTheFuture_AndWithinTheLimit()
    {
        await Roster();
        await _db.Rotation().SetWeekAsync(_week1, Robin);
        As(Me);
        var notMine = () => _db.Rotation().RequestAsync(new RotationRequest { PartnerId = Sam, GiveFrom = _week1, GiveTo = _week1 });
        await notMine.Should().ThrowAsync<InvalidOperationException>().WithMessage("Test Analyst isn't presenting*");

        var past = () => _db.Rotation().RequestAsync(new RotationRequest { PartnerId = Sam, GiveFrom = _db.Clock.Today.AddDays(-7), GiveTo = _db.Clock.Today.AddDays(-7) });
        await past.Should().ThrowAsync<ArgumentException>().WithMessage("*past*");

        var tooLong = () => _db.Rotation().RequestAsync(new RotationRequest { PartnerId = Sam, GiveFrom = _week1, GiveTo = _week1.AddDays(40) });
        await tooLong.Should().ThrowAsync<ArgumentException>().WithMessage("*at most*");

        var forSomeoneElse = () => _db.Rotation().RequestAsync(new RotationRequest { RequesterId = Robin, PartnerId = Sam, GiveFrom = _week1, GiveTo = _week1 });
        await forSomeoneElse.Should().ThrowAsync<ForbiddenException>("only an administrator arranges cover for someone else");
    }

    [Fact]
    public async Task AnAdministrator_ArrangesCover_StillApprovedByTheColleague_AndCanUndoIt()
    {
        await Roster();
        await _db.Rotation().SetWeekAsync(_week1, Robin);

        var change = await _db.Rotation().RequestAsync(new RotationRequest { RequesterId = Robin, PartnerId = Sam, GiveFrom = _week1, GiveTo = _week1 });
        change.CreatedByName.Should().Be("Test Analyst");
        change.RequesterName.Should().Be("Robin");

        As(Sam);
        await _db.Rotation().ApproveAsync(change.Id);
        (await _db.Rotation().PresenterAsync(_week1))!.Value.Name.Should().Be("Sam");

        As(Me, AppRole.Admin);
        await _db.Rotation().CancelAsync(change.Id);
        (await _db.Rotation().PresenterAsync(_week1))!.Value.Name.Should().Be("Robin", "undoing it gives the day back to the rota");
    }

    [Fact]
    public async Task Undo_AChangeLayeredOnAnEarlierOne_GivesTheDayBackToTheEarlierChange_NotTheRota()
    {
        await Roster();
        await _db.Rotation().SetWeekAsync(_week1, Sam);
        // Robin covers Sam's Monday, then gives that Monday on to me.
        var first = await _db.Rotation().RequestAsync(new RotationRequest { RequesterId = Sam, PartnerId = Robin, GiveFrom = _week1, GiveTo = _week1 });
        As(Robin);
        await _db.Rotation().ApproveAsync(first.Id);
        var second = await _db.Rotation().RequestAsync(new RotationRequest { PartnerId = Me, GiveFrom = _week1, GiveTo = _week1 });
        As(Me);
        await _db.Rotation().ApproveAsync(second.Id);
        (await _db.Rotation().PresenterAsync(_week1))!.Value.Name.Should().Be("Test Analyst");

        As(Me, AppRole.Admin);
        await _db.Rotation().CancelAsync(second.Id);

        (await _db.Rotation().PresenterAsync(_week1))!.Value.Name.Should().Be("Robin", "the first change still stands");
        await _db.Rotation().CancelAsync(first.Id);
        (await _db.Rotation().PresenterAsync(_week1))!.Value.Name.Should().Be("Sam", "with both undone, the rota");
    }

    [Fact]
    public async Task Undo_OfAChangePartlyPast_GivesBackOnlyTheDaysToCome()
    {
        await Roster();
        await _db.Rotation().SetWeekAsync(_week1, Me);
        As(Me);
        var change = await _db.Rotation().RequestAsync(new RotationRequest { PartnerId = Robin, GiveFrom = _week1, GiveTo = _week1.AddDays(4) });
        As(Robin);
        await _db.Rotation().ApproveAsync(change.Id);

        var wednesday = Later(days: 2);
        As(Me, AppRole.Admin);
        await _db.Rotation(wednesday).CancelAsync(change.Id);

        var days = await _db.Rotation(wednesday).CalendarAsync(_week1, _week1.AddDays(4));
        // Monday and Tuesday were Robin's to present; only the days to come go back.
        days.Select(d => d.AnalystId).Should().Equal(Robin, Robin, Me, Me, Me);
    }

    [Fact]
    public async Task Undo_OfAChangeWhoseDaysAllPassed_IsRefused_AsThePageDoesNotOfferIt()
    {
        await Roster();
        await _db.Rotation().SetWeekAsync(_week1, Me);
        As(Me);
        var change = await _db.Rotation().RequestAsync(new RotationRequest { PartnerId = Robin, GiveFrom = _week1, GiveTo = _week1.AddDays(1) });
        As(Robin);
        await _db.Rotation().ApproveAsync(change.Id);

        As(Me, AppRole.Admin);
        var tooLate = () => _db.Rotation(Later(days: 9)).CancelAsync(change.Id);
        await tooLate.Should().ThrowAsync<InvalidOperationException>().WithMessage("*nothing to undo*");
        (await _db.Rotation(Later(days: 9)).CalendarAsync(_week1, _week1)).Single().AnalystId.Should().Be(Robin);
    }

    [Fact]
    public async Task Approval_OfTwoRequestsForTheSameDay_AtTheSameMoment_LetsOnlyOneThrough()
    {
        await Roster();
        await _db.Rotation().SetWeekAsync(_week1, Me);
        As(Me);
        var toRobin = await _db.Rotation().RequestAsync(new RotationRequest { PartnerId = Robin, GiveFrom = _week1, GiveTo = _week1 });
        var toSam = await _db.Rotation().RequestAsync(new RotationRequest { PartnerId = Sam, GiveFrom = _week1, GiveTo = _week1 });
        RotationService As2(string id) => new(_db, new FakeUser(AppRole.Analyst) { UserId = id }, _db.Clock, _db.Email,
            Microsoft.Extensions.Options.Options.Create(new Tide.Application.Distribution.AppOptions()));

        var results = await Task.WhenAll(
            Try(() => As2(Robin).ApproveAsync(toRobin.Id)),
            Try(() => As2(Sam).ApproveAsync(toSam.Id)));

        results.Count(ok => ok).Should().Be(1, "once one is approved, the requester no longer holds the day");
        await using var ctx = _db.Context();
        (await ctx.RotationChanges.CountAsync(c => c.Status == RotationChangeStatus.Approved)).Should().Be(1);

        static async Task<bool> Try(Func<Task> approve)
        {
            try { await approve(); return true; }
            catch (InvalidOperationException) { return false; }
        }
    }

    [Fact]
    public async Task Approval_IsRefused_WhenTheDaysChangedHandsMeanwhile()
    {
        await Roster();
        await _db.Rotation().SetWeekAsync(_week1, Me);
        As(Me);
        var change = await _db.Rotation().RequestAsync(new RotationRequest { PartnerId = Robin, GiveFrom = _week1, GiveTo = _week1 });

        As(Me, AppRole.Admin);
        await _db.Rotation().SetWeekAsync(_week1, Sam);
        As(Robin);
        var approve = () => _db.Rotation().ApproveAsync(change.Id);
        await approve.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ApprovedLate_OnlyTheDaysStillToCome_ChangeHands()
    {
        await Roster();
        await _db.Rotation().SetWeekAsync(_week1, Me);
        As(Me);
        var change = await _db.Rotation().RequestAsync(new RotationRequest { PartnerId = Robin, GiveFrom = _week1, GiveTo = _week1.AddDays(4) });

        var wednesday = Later(days: 2);
        As(Robin);
        await _db.Rotation(wednesday).ApproveAsync(change.Id);

        var days = await _db.Rotation(wednesday).CalendarAsync(_week1, _week1.AddDays(4));
        days.Select(d => d.AnalystName).Should().Equal("Test Analyst", "Test Analyst", "Robin", "Robin", "Robin");
        await using var ctx = _db.Context();
        var saved = await ctx.RotationChanges.SingleAsync(c => c.Id == change.Id);
        (saved.GiveFrom, saved.GiveTo).Should().Be((_week1.AddDays(2), _week1.AddDays(4)), "the request is narrowed to what moved");
    }

    [Fact]
    public async Task ApprovedLate_WithEveryDayPast_IsRefused_AndCanBeDeclined()
    {
        await Roster();
        await _db.Rotation().SetWeekAsync(_week1, Me);
        As(Me);
        var change = await _db.Rotation().RequestAsync(new RotationRequest { PartnerId = Robin, GiveFrom = _week1, GiveTo = _week1.AddDays(1) });

        As(Robin);
        var approve = () => _db.Rotation(Later()).ApproveAsync(change.Id);
        await approve.Should().ThrowAsync<InvalidOperationException>().WithMessage("*passed*nothing to hand over*");
        await _db.Rotation(Later()).DeclineAsync(change.Id);
        (await _db.Rotation(Later()).CalendarAsync(_week1, _week1)).Single().AnalystName.Should().Be("Test Analyst");
    }

    [Fact]
    public async Task ASwapApprovedLate_TradesTheDaysToComeOnBothSides_ButNotOneSideAlone()
    {
        await Roster();
        var r = _db.Rotation();
        await r.SetWeekAsync(_week1, Me);
        await r.SetWeekAsync(_week2, Robin);
        As(Me);
        var swap = new RotationRequest { Kind = RotationChangeKind.Swap, PartnerId = Robin, GiveFrom = _week1.AddDays(3), GiveTo = _week1.AddDays(4), TakeFrom = _week2, TakeTo = _week2.AddDays(1) };
        var partly = await _db.Rotation().RequestAsync(swap);
        var gone = await _db.Rotation().RequestAsync(new RotationRequest
        {
            Kind = RotationChangeKind.Swap, PartnerId = Robin, GiveFrom = _week1, GiveTo = _week1, TakeFrom = _week2.AddDays(4), TakeTo = _week2.AddDays(4),
        });

        var friday = Later(days: 4);
        As(Robin);
        var oneSided = () => _db.Rotation(friday).ApproveAsync(gone.Id);
        await oneSided.Should().ThrowAsync<InvalidOperationException>().WithMessage("*can't be a swap*");

        await _db.Rotation(friday).ApproveAsync(partly.Id);
        var days = await _db.Rotation(friday).CalendarAsync(_week1.AddDays(3), _week2.AddDays(1));
        days.Select(d => d.AnalystId).Should().Equal(Me, Robin, Me, Me);
    }

    [Fact]
    public async Task Decline_AndWithdraw_LeaveTheRotaAlone()
    {
        await Roster();
        await _db.Rotation().SetWeekAsync(_week1, Me);
        As(Me);
        var a = await _db.Rotation().RequestAsync(new RotationRequest { PartnerId = Robin, GiveFrom = _week1, GiveTo = _week1 });
        var b = await _db.Rotation().RequestAsync(new RotationRequest { PartnerId = Robin, GiveFrom = _week1.AddDays(1), GiveTo = _week1.AddDays(1) });
        await _db.Rotation().CancelAsync(b.Id);
        As(Robin);
        await _db.Rotation().DeclineAsync(a.Id, "Away myself");

        (await _db.Rotation().CalendarAsync(_week1, _week1.AddDays(4))).Should().OnlyContain(d => d.AnalystId == Me);
        _db.Email.Sent.Should().Contain(m => m.To.Contains("me@example.org") && m.Subject.Contains("declined"));
        var statuses = (await _db.Rotation().ChangesAsync()).Select(c => c.Status);
        statuses.Should().BeEquivalentTo([RotationChangeStatus.Declined, RotationChangeStatus.Cancelled]);
    }

    [Fact]
    public async Task Upcoming_ShowsMyRuns_AndWhatWaitsForMe()
    {
        await Roster();
        await _db.Rotation().SetWeekAsync(_week1, Robin);
        await _db.Rotation().SetWeekAsync(_week2, Me);
        As(Robin);
        await _db.Rotation().RequestAsync(new RotationRequest { PartnerId = Me, GiveFrom = _week1, GiveTo = _week1 });

        As(Me);
        var up = await _db.Rotation().UpcomingAsync();
        up.MyRuns.Single().Should().Be(new RotationRun(Me, "Test Analyst", _week2, _week2.AddDays(4)));
        up.ToApprove.Single().RequesterName.Should().Be("Robin");
        (await _db.Rotation().MyCalendarAsync()).Should().Contain($"DTSTART;VALUE=DATE:{_week2:yyyyMMdd}");
    }

    [Fact]
    public async Task TheBrief_IsStartedForWhoeverIsPresenting()
    {
        await Roster();
        await _db.Rotation().SetWeekAsync(_week1, Robin);
        var brief = await _db.Briefs().GetOrStartAsync(_week1.AddDays(1));
        brief.AnalystName.Should().Be("Robin");
    }

    [Fact]
    public async Task ABriefStartedEarly_FollowsACoverApprovedLater_UnlessItsPresenterWasSetByHand()
    {
        await Roster();
        await _db.Rotation().SetWeekAsync(_week1, Sam);
        var tuesday = _week1.AddDays(1);
        var wednesday = _week1.AddDays(2);
        (await _db.Briefs().GetOrStartAsync(tuesday)).AnalystName.Should().Be("Sam", "carry-over starts tomorrow's brief early");
        var byHand = await _db.Briefs().GetOrStartAsync(wednesday);
        await _db.Briefs().SetDetailsAsync(byHand.Id, Me, "Test Analyst", null);

        var change = await _db.Rotation().RequestAsync(new RotationRequest { RequesterId = Sam, PartnerId = Robin, GiveFrom = tuesday, GiveTo = wednesday });
        As(Robin);
        await _db.Rotation().ApproveAsync(change.Id);

        var brief = (await _db.Briefs().GetAsync(tuesday))!;
        brief.AnalystId.Should().Be(Robin);
        brief.AnalystName.Should().Be("Robin", "the emails say who presents it now");
        (await _db.Briefs().GetAsync(wednesday))!.AnalystName.Should().Be("Test Analyst", "a presenter set by hand is kept");

        var local = tuesday.ToDateTime(new TimeOnly(9, 45));
        var morning = new OrgClock(Options.Create(new OrgOptions()), new TestClock(new DateTimeOffset(local, _db.Clock.Zone.GetUtcOffset(local))));
        await _db.Reminders(new ReminderOptions { BriefNotReady = { Enabled = true, Time = "09:30", Recipients = ["lead@example.org"] } }, morning).RunDueAsync();
        _db.Email.Sent[^1].To.Should().Equal(["robin@example.org"], "the \"not ready\" reminder goes to whoever presents it now");

        As(Me, AppRole.Admin);
        await _db.Rotation().CancelAsync(change.Id);
        (await _db.Briefs().GetAsync(tuesday))!.AnalystName.Should().Be("Sam", "undone, the day and its brief go back to the rota");
        await _db.Rotation().SetWeekAsync(_week1, Me);
        (await _db.Briefs().GetAsync(tuesday))!.AnalystName.Should().Be("Test Analyst", "and follow the rota when the week changes hands");
    }

    // A clock some days after week one: week one is in the past.
    private OrgClock Later(int days = 9, int hour = 15)
    {
        var clock = _db.Clock;
        var local = _week1.AddDays(days).ToDateTime(new TimeOnly(hour, 0));
        return new OrgClock(Options.Create(new OrgOptions()), new TestClock(new DateTimeOffset(local, clock.Zone.GetUtcOffset(local))));
    }

    [Fact]
    public async Task APastDaysBrief_IsPresentedByWhoeverWritesItUp_AndTheCoverCanBeRecorded()
    {
        await Roster();
        await _db.Rotation().SetWeekAsync(_week1, Robin);
        var later = Later();
        var friday = _week1.AddDays(4);
        As(Sam);
        _db.User.DisplayName = "Sam";

        var brief = await new BriefService(_db, _db.User, later, _db.Rotation(later)).GetOrStartAsync(friday);
        brief.AnalystName.Should().Be("Sam", "the rota says Robin, but Sam is writing up the day after the fact");

        var change = await _db.Rotation(later).RecordPastCoverAsync(friday);
        change!.Status.Should().Be(RotationChangeStatus.Approved);
        (change.RequesterName, change.PartnerName).Should().Be(("Robin", "Sam"));
        (await _db.Rotation(later).CalendarAsync(friday, friday)).Single().AnalystName.Should().Be("Sam");
        _db.Email.Sent.Should().Contain(e => e.To.Contains("manager@example.org")).And.Contain(e => e.To.Contains("robin@example.org"));
        (await _db.Rotation(later).RecordPastCoverAsync(friday)).Should().BeNull("it's already recorded");

        var future = () => _db.Rotation(later).RecordPastCoverAsync(later.Today.AddDays(1));
        await future.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task RecordingAPastCover_IsForThePeopleInvolved_OrAnAdministrator()
    {
        await Roster();
        await _db.Rotation().SetWeekAsync(_week1, Robin);
        var later = Later();
        As(Sam);
        await new BriefService(_db, _db.User, later, _db.Rotation(later)).GetOrStartAsync(_week1);

        As(Me);
        var act = () => _db.Rotation(later).RecordPastCoverAsync(_week1);
        await act.Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task EndingAPastDaysMeeting_RecordsBothWhenItWasHeld_AndWhenItWasRecorded()
    {
        var later = Later();
        var briefs = new BriefService(_db, _db.User, later, _db.Rotation(later));
        var tuesday = await briefs.GetOrStartAsync(_week1.AddDays(1));
        var wednesday = await briefs.GetOrStartAsync(_week1.AddDays(2));

        await briefs.MarkPresentedAsync(tuesday.Id);
        var chosen = later.MeetingStart(_week1.AddDays(2)).AddMinutes(45);
        await briefs.MarkPresentedAsync(wednesday.Id, chosen);

        var t = (await briefs.GetAsync(_week1.AddDays(1)))!;
        t.MeetingHeldAtUtc.Should().Be(later.MeetingStart(_week1.AddDays(1)), "a past day's meeting defaults to that day's meeting time");
        t.PresentedAtUtc.Should().Be(later.Now);
        (await briefs.GetAsync(_week1.AddDays(2)))!.MeetingHeldAtUtc.Should().Be(chosen);

        var thursday = await briefs.GetOrStartAsync(_week1.AddDays(3));
        var early = () => briefs.MarkPresentedAsync(thursday.Id, later.MeetingStart(_week1.AddDays(2)));
        await early.Should().ThrowAsync<ArgumentException>().WithMessage("*before the brief's day*");
        var ahead = () => briefs.MarkPresentedAsync(thursday.Id, later.Now.AddDays(1));
        await ahead.Should().ThrowAsync<ArgumentException>().WithMessage("*future*");
    }

    [Fact]
    public async Task PresentingReminder_GoesTheBusinessDayBefore_Once_AndNotToSomeoneAlreadyOn()
    {
        await Roster();
        await using (var ctx = _db.Context())
        {
            ctx.RotationWeeks.AddRange(
                new RotationWeek { WeekStart = new DateOnly(2026, 10, 26), AnalystId = Me, AnalystName = "Test Analyst" },
                new RotationWeek { WeekStart = new DateOnly(2026, 11, 2), AnalystId = Robin, AnalystName = "Robin" });
            await ctx.SaveChangesAsync();
        }
        OrgClock At(int month, int day) => new(Options.Create(new OrgOptions()), new TestClock(new DateTimeOffset(2026, month, day, 14, 0, 0, TimeSpan.Zero)));
        var options = new ReminderOptions { Presenting = { Enabled = true } };

        await _db.Reminders(options, At(10, 29)).RunDueAsync();
        _db.Email.Sent.Should().BeEmpty("Thursday's next business day is still Test Analyst's");

        await _db.Reminders(options, At(10, 30)).RunDueAsync();
        await _db.Reminders(options, At(10, 30)).RunDueAsync();
        var mail = _db.Email.Sent.Single();
        mail.To.Should().Equal("robin@example.org");
        mail.Subject.Should().Contain("presenting the brief from Mon Nov 2");
    }

    [Fact]
    public async Task BriefNotReady_GoesToWhoeverIsPresenting_WhenThereIsNoBriefYet()
    {
        await Roster();
        await using (var ctx = _db.Context())
        {
            ctx.RotationWeeks.Add(new RotationWeek { WeekStart = new DateOnly(2026, 10, 26), AnalystId = Robin, AnalystName = "Robin" });
            await ctx.SaveChangesAsync();
        }
        var monday = new OrgClock(Options.Create(new OrgOptions()), new TestClock(new DateTimeOffset(2026, 10, 26, 14, 0, 0, TimeSpan.Zero)));

        await _db.Reminders(new ReminderOptions { BriefNotReady = { Enabled = true, Time = "09:30", Recipients = ["lead@example.org"] } }, monday).RunDueAsync();

        _db.Email.Sent.Single().To.Should().Equal("robin@example.org");
    }
}
