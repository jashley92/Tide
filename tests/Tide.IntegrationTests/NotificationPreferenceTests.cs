using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tide.Application.Actions;
using Tide.Application.Org;
using Tide.Application.Preferences;
using Tide.Application.Rotation;
using Tide.Application.Security;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using static Tide.Application.Preferences.UserPreferences;

namespace Tide.IntegrationTests;

/// <summary>
/// People's own notification choices (My notifications): each opt-out leaves only that person out, a team mailbox and
/// the brief-not-ready fallback are never left out, the personal digest goes daily or weekly with their own actions,
/// and only an administrator sees who chose what.
/// </summary>
public class NotificationPreferenceTests : IDisposable
{
    private readonly TestDb _db = new();

    private const string Robin = "dev:robin", Sam = "dev:sam", Kim = "dev:kim";

    // Every kind on, as an administrator would have it for people to opt out of.
    private static ReminderOptions AllOn() => new()
    {
        DueSoon = { Enabled = true }, Overdue = { Enabled = true }, Presenting = { Enabled = true },
        Digest = { Enabled = true, Day = DayOfWeek.Monday, Time = "08:00" },
        BriefNotReady = { Enabled = true, Time = "09:30", Recipients = ["lead@example.org"] },
        OutreachFollowUp = { Enabled = true },
        ReminderTime = "08:00",
    };

    // 09:45 local (America/New_York) on the day given, in October 2026: the 26th is a Monday.
    private static OrgClock At(int day, int hourUtc = 13, int minuteUtc = 45) =>
        new(Options.Create(new OrgOptions()), new TestClock(new DateTimeOffset(2026, 10, day, hourUtc, minuteUtc, 0, TimeSpan.Zero)));

    public NotificationPreferenceTests() => As("S-ADMIN", AppRole.Analyst, AppRole.Admin);

    public void Dispose() => _db.Dispose();

    private void As(string id, params AppRole[] roles)
    {
        _db.User.UserId = id;
        _db.User.DisplayName = id;
        _db.User.Email = null;
        _db.User.Roles = roles.Length == 0 ? new HashSet<AppRole> { AppRole.Analyst } : roles.ToHashSet();
    }

    private async Task Person(string sid, string email, string roles = "Analyst")
    {
        await using var ctx = _db.Context();
        ctx.Users.Add(new AppUser { Sid = sid, DisplayName = sid[4..], Email = email, RolesCsv = roles, LastSeenUtc = DateTimeOffset.UtcNow });
        await ctx.SaveChangesAsync();
    }

    // The person chooses, in My notifications, with every kind on.
    private async Task Choose(string sid, string name, string value = Off, AppRole role = AppRole.Analyst)
    {
        var (id, roles) = (_db.User.UserId, _db.User.Roles);
        As(sid, role);
        await _db.NotificationPrefs(AllOn()).SetMineAsync(name, value);
        (_db.User.UserId, _db.User.Roles) = (id, roles);
    }

    private async Task<Guid> TeamWith(string? mailbox, params string[] members)
    {
        var team = await _db.Teams().AddAsync("Team " + Guid.NewGuid().ToString("N")[..6], null, mailbox);
        foreach (var m in members) await _db.Teams().AddMemberAsync(team, m, m[4..]);
        return team;
    }

    private IEnumerable<string> To => _db.Email.Sent.SelectMany(m => m.To);

    // --- each opt-out --------------------------------------------------------------------------------------------

    [Fact]
    public async Task DueSoonOptOut_LeavesThatMemberOut_AndTheOthersStillGetIt()
    {
        await Person(Robin, "robin@example.org");
        await Person(Sam, "sam@example.org");
        var team = await TeamWith(null, Robin, Sam);
        await _db.Actions().CreateAsync(new ActionForm { Title = "Patch it", TeamId = team, DueDate = _db.Clock.Today });
        await Choose(Robin, NotifyDueSoon);

        (await _db.Reminders().RunAsync(ReminderKind.DueSoon)).Should().Be(1);
        To.Should().Equal("sam@example.org");
    }

    [Fact]
    public async Task OverdueOptOut_StopsTheAssigneesReminder_WithoutHandingItToTheTeam_AndLeavesTheirDueSoon()
    {
        await Person(Robin, "robin@example.org");
        await Person(Sam, "sam@example.org");
        var team = await TeamWith(null, Robin, Sam);
        var today = _db.Clock.Today;
        await _db.Actions().CreateAsync(new ActionForm { Title = "Late one", TeamId = team, DueDate = today.AddDays(-3), AssigneeId = Robin, AssigneeName = "robin" });
        await _db.Actions().CreateAsync(new ActionForm { Title = "Soon one", TeamId = team, DueDate = today, AssigneeId = Robin, AssigneeName = "robin" });
        await Choose(Robin, NotifyOverdue);
        var log = new ReminderLog();

        (await _db.Reminders(log: log).RunAsync(ReminderKind.Overdue)).Should().Be(0, "the action is Robin's, so the team isn't told instead");
        log.Last(ReminderKind.Overdue)!.Note.Should().Be("all recipients opted out");

        (await _db.Reminders(log: log).RunAsync(ReminderKind.DueSoon)).Should().Be(1, "only overdue was turned off");
        _db.Email.Sent.Single().To.Should().Equal("robin@example.org");
        log.Last(ReminderKind.DueSoon)!.Note.Should().BeNull();
    }

    [Fact]
    public async Task EveryoneOptedOut_SendsNothing_AndTheLogSaysSo_ThenSomeLeftOutAreCounted()
    {
        await Person(Robin, "robin@example.org");
        await Person(Sam, "sam@example.org");
        await Person(Kim, "kim@example.org");
        var team = await TeamWith(null, Robin, Sam);
        await _db.Actions().CreateAsync(new ActionForm { Title = "Patch it", TeamId = team, DueDate = _db.Clock.Today });
        await Choose(Robin, NotifyDueSoon);
        await Choose(Sam, NotifyDueSoon);
        var log = new ReminderLog();

        (await _db.Reminders(log: log).RunAsync(ReminderKind.DueSoon)).Should().Be(0);
        _db.Email.Sent.Should().BeEmpty();
        log.Last(ReminderKind.DueSoon)!.Note.Should().Be("all recipients opted out");

        await _db.Teams().AddMemberAsync(team, Kim, "kim");
        (await _db.Reminders(log: log).RunAsync(ReminderKind.DueSoon)).Should().Be(1);
        To.Should().Equal("kim@example.org");
        log.Last(ReminderKind.DueSoon)!.Note.Should().Be("2 opted out");
    }

    [Fact]
    public async Task TheLastRun_KeepsWhatTheDaysRealScanSent_WhenLaterPollsOnlyFindOptedOutPeople()
    {
        await Person(Robin, "robin@example.org");
        await Person(Sam, "sam@example.org");
        var team = await TeamWith(null, Robin, Sam);
        await _db.Actions().CreateAsync(new ActionForm { Title = "Patch it", TeamId = team, DueDate = new DateOnly(2026, 10, 26) });
        await Choose(Sam, NotifyDueSoon);
        var log = new ReminderLog();
        var options = new ReminderOptions { DueSoon = { Enabled = true } };

        await _db.Reminders(options, At(26), log).RunDueAsync();
        log.Last(ReminderKind.DueSoon)!.Sent.Should().Be(1);

        // The next poll, five minutes on: nothing new, and Sam's action is still waiting on no one.
        await _db.Reminders(options, At(26, 13, 50), log).RunDueAsync();
        log.Last(ReminderKind.DueSoon)!.Sent.Should().Be(1, "a poll that sent nothing doesn't wipe the run that did");
        log.Last(ReminderKind.DueSoon)!.Note.Should().Be("1 opted out");

        // The next day's poll is a new run, whatever it found.
        await _db.Reminders(options, At(27), log).RunDueAsync();
        log.Last(ReminderKind.DueSoon)!.AtUtc.Should().BeAfter(new DateTimeOffset(2026, 10, 26, 14, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task TeamDigestOptOut_LeavesThatMemberOut_ButATeamMailboxStillGetsIt()
    {
        await Person(Robin, "robin@example.org");
        await Person(Sam, "sam@example.org");
        var members = await TeamWith(null, Robin, Sam);
        var mailbox = await TeamWith("defense@example.org", Robin);
        await _db.Actions().CreateAsync(new ActionForm { Title = "Members one", TeamId = members, DueDate = _db.Clock.Today.AddDays(9) });
        await _db.Actions().CreateAsync(new ActionForm { Title = "Mailbox one", TeamId = mailbox, DueDate = _db.Clock.Today.AddDays(9) });
        await Choose(Robin, NotifyTeamDigest);

        (await _db.Reminders().RunAsync(ReminderKind.Digest)).Should().Be(2);
        _db.Email.Sent.Should().Contain(m => m.To.SequenceEqual(new[] { "sam@example.org" }) && m.Html.Contains("Members one"));
        _db.Email.Sent.Should().Contain(m => m.To.SequenceEqual(new[] { "defense@example.org" }) && m.Html.Contains("Mailbox one"));
        To.Should().NotContain("robin@example.org");
    }

    [Fact]
    public async Task TeamMailboxReminders_AreUnaffected_ByItsMembersOptingOut()
    {
        await Person(Robin, "robin@example.org");
        var team = await TeamWith("defense@example.org", Robin);
        await _db.Actions().CreateAsync(new ActionForm { Title = "Late one", TeamId = team, DueDate = _db.Clock.Today.AddDays(-2) });
        await Choose(Robin, NotifyOverdue);

        (await _db.Reminders().RunAsync(ReminderKind.Overdue)).Should().Be(1);
        To.Should().Equal("defense@example.org");
    }

    // The analyst presents the week starting on the Monday given.
    private async Task Rota(string analystId, DateOnly weekStart)
    {
        await _db.Rotation().AddMemberAsync(analystId, analystId[4..]);
        await _db.Rotation().SetWeekAsync(weekStart, analystId);
    }

    [Fact]
    public async Task RotationRemindersOptOut_StopsTheYourePresentingReminder()
    {
        await Person(Robin, "robin@example.org");
        await Rota(Robin, new DateOnly(2026, 11, 2));
        await Choose(Robin, NotifyRotationReminders);
        var log = new ReminderLog();

        await _db.Reminders(new ReminderOptions { Presenting = { Enabled = true } }, At(30), log).RunDueAsync();

        _db.Email.Sent.Should().BeEmpty();
        log.Last(ReminderKind.Presenting)!.Note.Should().Be("all recipients opted out");
    }

    [Fact]
    public async Task BriefNotReady_ForAnAnalystWhoOptedOut_GoesToTheFallbackRecipients()
    {
        await Person(Robin, "robin@example.org");
        await Rota(Robin, new DateOnly(2026, 10, 26));
        var options = new ReminderOptions { BriefNotReady = { Enabled = true, Time = "09:30", Recipients = ["lead@example.org"] } };

        await _db.Reminders(options, At(26)).RunDueAsync();
        To.Should().Equal("robin@example.org");

        await Choose(Robin, NotifyRotationReminders);
        await _db.Reminders(options, At(27)).RunDueAsync();
        _db.Email.Sent.Last().To.Should().Equal(["lead@example.org"], "the fallback is the team lead's, not the analyst's choice");
    }

    [Fact]
    public async Task BriefNotReady_WithNoFallback_SaysTheAnalystOptedOut()
    {
        await Person(Robin, "robin@example.org");
        await Rota(Robin, new DateOnly(2026, 10, 26));
        await Choose(Robin, NotifyRotationReminders);
        var log = new ReminderLog();

        await _db.Reminders(new ReminderOptions { BriefNotReady = { Enabled = true, Time = "09:30" } }, At(26), log).RunDueAsync();

        _db.Email.Sent.Should().BeEmpty();
        log.Last(ReminderKind.BriefNotReady)!.Note.Should().Be("all recipients opted out");
    }

    [Fact]
    public async Task RotationRequestsOptOut_StopsTheRequestAndTheNotice_ButNotTheManagersCopy()
    {
        const string me = "S-1-5-21-TEST-1";
        var week = RotationService.WeekStart(_db.Clock.Today).AddDays(7);
        var r = _db.Rotation();
        await r.AddMemberAsync(me, "Test Analyst");
        await r.AddMemberAsync(Robin, "Robin");
        await r.SaveManagerEmailsAsync("manager@example.org");
        await r.SetWeekAsync(week, me);
        await Person(me, "me@example.org");
        await Person(Robin, "robin@example.org");
        await Choose(Robin, NotifyRotationRequests);
        await Choose(me, NotifyRotationRequests);

        As(me);
        var change = await _db.Rotation().RequestAsync(new RotationRequest { PartnerId = Robin, GiveFrom = week, GiveTo = week });
        _db.Email.Sent.Should().BeEmpty("Robin opted out of requests");

        As(Robin);
        await _db.Rotation().ApproveAsync(change.Id);
        _db.Email.Sent.Single().To.Should().Equal("manager@example.org");
    }

    [Fact]
    public async Task RotationRequests_StillReachThoseWhoDidntOptOut()
    {
        const string me = "S-1-5-21-TEST-1";
        var week = RotationService.WeekStart(_db.Clock.Today).AddDays(7);
        var r = _db.Rotation();
        await r.AddMemberAsync(me, "Test Analyst");
        await r.AddMemberAsync(Robin, "Robin");
        await r.SetWeekAsync(week, me);
        await Person(me, "me@example.org");
        await Person(Robin, "robin@example.org");
        await Choose(me, NotifyRotationRequests);

        As(me);
        var change = await _db.Rotation().RequestAsync(new RotationRequest { PartnerId = Robin, GiveFrom = week, GiveTo = week });
        As(Robin);
        await _db.Rotation().DeclineAsync(change.Id, "On leave");

        To.Should().Equal("robin@example.org");
    }

    // --- My notifications --------------------------------------------------------------------------------------

    [Fact]
    public async Task Mine_IsOnByDefault_ShowsWhatsOffForEveryone_AndRefusesChangingIt()
    {
        await Person(Robin, "robin@example.org");
        As(Robin);
        var options = AllOn();
        options.Overdue.Enabled = false;

        var mine = await _db.NotificationPrefs(options).GetMineAsync();

        mine.Address.Should().Be("robin@example.org", "the address saved from the directory, when this session has none");
        mine.Digest.Should().Be(Off);
        mine.DigestOffered.Should().BeTrue();
        mine.Switches.Select(s => s.Name).Should().Equal(NotificationPreferenceService.Switches);
        mine.Switches.Where(s => s.Name != NotifyOverdue).Should().OnlyContain(s => s.On && !s.OffForEveryone);
        var overdue = mine.Switches.Single(s => s.Name == NotifyOverdue);
        (overdue.On, overdue.OffForEveryone).Should().Be((false, true));
        mine.Switches.Single(s => s.Name == NotifyRotationReminders).Note.Should().Be(NotificationPreferenceService.DutyNote);

        var change = () => _db.NotificationPrefs(options).SetMineAsync(NotifyOverdue, On);
        await change.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Off for everyone (set by an administrator)*");
        options.PersonalDigest.Enabled = false;
        var digest = () => _db.NotificationPrefs(options).SetMineAsync(NotifyDigest, Daily);
        await digest.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Mine_ForAnActionOwner_LeavesOutTheRotation_AndAReaderHasNothingToChoose()
    {
        As(Kim, AppRole.ActionOwner);
        (await _db.NotificationPrefs(AllOn()).GetMineAsync()).Switches.Select(s => s.Name)
            .Should().Equal(NotifyDueSoon, NotifyOverdue, NotifyTeamDigest, NotifyOutreachFollowUp);
        var rotation = () => _db.NotificationPrefs(AllOn()).SetMineAsync(NotifyRotationRequests, Off);
        await rotation.Should().ThrowAsync<ForbiddenException>();

        As(Kim, AppRole.Reader);
        var read = () => _db.NotificationPrefs(AllOn()).GetMineAsync();
        await read.Should().ThrowAsync<ForbiddenException>();
        var unknown = () => { As(Kim); return _db.NotificationPrefs(AllOn()).SetMineAsync("actions.layout", "list"); };
        await unknown.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Choices_AreEachPersonsOwn()
    {
        await Choose(Robin, NotifyDueSoon);
        await Choose(Robin, NotifyDigest, Weekly);

        As(Sam);
        var sams = await _db.NotificationPrefs(AllOn()).GetMineAsync();
        sams.Switches.Should().OnlyContain(s => s.On);
        sams.Digest.Should().Be(Off);
        await _db.NotificationPrefs(AllOn()).SetMineAsync(NotifyDueSoon, On);

        As(Robin);
        var robins = await _db.NotificationPrefs(AllOn()).GetMineAsync();
        robins.Switches.Single(s => s.Name == NotifyDueSoon).On.Should().BeFalse("Sam's save changed only Sam's");
        robins.Digest.Should().Be(Weekly);
    }

    // --- personal digest -----------------------------------------------------------------------------------------

    [Fact]
    public async Task PersonalDigest_Daily_GoesAtTheReminderTimeOnBusinessDays_WithTheirOwnActions()
    {
        await Person(Robin, "robin@example.org");
        await Person(Sam, "sam@example.org");
        var mine = await TeamWith(null, Robin);
        var theirs = await TeamWith(null, Sam);
        var monday = new DateOnly(2026, 10, 26);
        var svc = _db.Actions();
        await svc.CreateAsync(new ActionForm { Title = "Robin late one", TeamId = mine, DueDate = monday.AddDays(-3), AssigneeId = Robin, AssigneeName = "robin" });
        await svc.CreateAsync(new ActionForm { Title = "Robin soon one", TeamId = mine, DueDate = monday.AddDays(1), AssigneeId = Robin, AssigneeName = "robin" });
        await svc.CreateAsync(new ActionForm { Title = "Team later one", TeamId = mine, DueDate = monday.AddDays(20) });
        await svc.CreateAsync(new ActionForm { Title = "Sam one", TeamId = theirs, DueDate = monday, AssigneeId = Sam, AssigneeName = "sam" });
        await Choose(Robin, NotifyDigest, Daily);
        var options = new ReminderOptions { ReminderTime = "09:00" };

        await _db.Reminders(options, At(26, 12, 30)).RunDueAsync();   // 08:30: before the reminder time
        await _db.Reminders(options, At(31)).RunDueAsync();           // Saturday
        _db.Email.Sent.Should().BeEmpty();

        await _db.Reminders(options, At(26)).RunDueAsync();
        await _db.Reminders(options, At(26)).RunDueAsync();
        var mail = _db.Email.Sent.Single();
        mail.To.Should().Equal("robin@example.org");
        mail.Subject.Should().Be("TIDE: your actions, Monday, October 26");
        mail.Html.Should().Contain("Robin late one").And.Contain("Robin soon one").And.Contain("Team later one")
            .And.Contain("Your other open actions").And.NotContain("Sam one");
        mail.Html.Should().Contain("daily digest because you chose it in My notifications");

        await svc.CreateAsync(new ActionForm { Title = "Robin new one", TeamId = theirs, DueDate = monday.AddDays(30), AssigneeId = Robin, AssigneeName = "robin" });
        await _db.Reminders(options, At(27)).RunDueAsync();
        var next = _db.Email.Sent[1].Html;
        next.Should().Contain("Robin new one").And.Contain("New to you since your last digest").And.Contain("Robin late one")
            .And.NotContain("Team later one", "it was in the last one, and isn't due yet");
    }

    [Fact]
    public async Task PersonalDigest_Weekly_GoesOnTheDigestDay_OncePerWeek()
    {
        await Person(Robin, "robin@example.org");
        var team = await TeamWith(null);
        await _db.Actions().CreateAsync(new ActionForm { Title = "Robin one", TeamId = team, DueDate = new DateOnly(2026, 10, 27), AssigneeId = Robin, AssigneeName = "robin" });
        await Choose(Robin, NotifyDigest, Weekly);
        var options = new ReminderOptions { Digest = { Day = DayOfWeek.Tuesday, Time = "09:00" } };

        await _db.Reminders(options, At(26)).RunDueAsync();
        _db.Email.Sent.Should().BeEmpty("Monday isn't the digest day");

        await _db.Reminders(options, At(27)).RunDueAsync();
        await _db.Reminders(options, At(29)).RunDueAsync();
        var mail = _db.Email.Sent.Single();
        mail.Subject.Should().Be("TIDE: your actions, the week of October 26");
        mail.Html.Should().Contain("Robin one").And.Contain("Due today or the next business day").And.Contain("weekly digest");
    }

    [Fact]
    public async Task PersonalDigest_NotOffered_GoesToNoOne_AndNotToSomeoneWhoLostSightOfActions()
    {
        await Person(Robin, "robin@example.org");
        await Person(Sam, "sam@example.org", roles: "Reader");
        var team = await TeamWith(null, Robin, Sam);
        await _db.Actions().CreateAsync(new ActionForm { Title = "Team one", TeamId = team, DueDate = new DateOnly(2026, 10, 26) });
        await Choose(Robin, NotifyDigest, Daily);
        await Choose(Sam, NotifyDigest, Daily);

        await _db.Reminders(new ReminderOptions { PersonalDigest = { Enabled = false } }, At(26)).RunDueAsync();
        _db.Email.Sent.Should().BeEmpty();

        (await _db.Reminders(clock: At(26)).RunAsync(ReminderKind.PersonalDigest)).Should().Be(1);
        To.Should().Equal("robin@example.org");
    }

    [Fact]
    public async Task PersonalDigest_WithNothingToSay_SendsNothing()
    {
        await Person(Robin, "robin@example.org");
        await Choose(Robin, NotifyDigest, Daily);

        (await _db.Reminders(clock: At(26)).RunAsync(ReminderKind.PersonalDigest)).Should().Be(0);
        _db.Email.Sent.Should().BeEmpty();
    }

    // --- an administrator's counts ----------------------------------------------------------------------------

    [Fact]
    public async Task Counts_AndTheListBehindThem_AreForAdministratorsOnly()
    {
        await Person(Robin, "robin@example.org");
        await Person(Sam, "sam@example.org");
        await Choose(Robin, NotifyDueSoon);
        await Choose(Sam, NotifyDueSoon);
        await Choose(Sam, NotifyDueSoon, On);
        await Choose(Robin, NotifyDigest, Weekly);
        await Choose(Kim, NotifyDigest, Daily);

        var admin = _db.NotificationPrefs(AllOn());
        var counts = await admin.CountsAsync();
        counts[NotifyDueSoon].Should().Be(1, "Sam turned it back on");
        counts[NotifyOverdue].Should().Be(0);
        counts[NotifyDigest].Should().Be(2);

        var people = await admin.PeopleAsync(NotifyDigest);
        people.Select(p => (p.Name, p.Email, p.Value)).Should().Equal((Kim, null, Daily), ("robin", "robin@example.org", Weekly));
        people.Should().OnlyContain(p => p.Since > DateTimeOffset.UtcNow.AddMinutes(-5));
        people[0].UserId.Should().Be(Kim, "someone not yet in the user mirror is listed by id");

        As(Robin);
        var count = () => _db.NotificationPrefs(AllOn()).CountsAsync();
        await count.Should().ThrowAsync<ForbiddenException>();
        var list = () => _db.NotificationPrefs(AllOn()).PeopleAsync(NotifyDueSoon);
        await list.Should().ThrowAsync<ForbiddenException>();
    }
}
