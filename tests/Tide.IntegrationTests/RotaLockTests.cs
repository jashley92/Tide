using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Abstractions;
using Tide.Application.Rotation;
using Tide.Application.Security;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Web.Components.Pages.Admin;

namespace Tide.IntegrationTests;

/// <summary>
/// The owner's decision on the weekly rota: giving a week to someone else keeps the presenter of its days already
/// presented (presented, sent, or held and put back to ready); "Correct past days" changes them too, audited as a correction.
/// </summary>
public class RotaLockTests : IDisposable
{
    private readonly TestDb _db = new();
    private const string Robin = "dev:robin", Sam = "dev:sam";
    private readonly DateOnly _week;

    public RotaLockTests()
    {
        _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };
        // The rule follows the briefs, not the date, so a week ahead keeps the test the same on any day.
        _week = RotationService.WeekStart(_db.Clock.Today).AddDays(14);
    }

    public void Dispose() => _db.Dispose();

    // Sam's week: Monday presented, Tuesday sent, Wednesday held and put back to ready (an attendee), Thursday a draft.
    private async Task<List<DateOnly>> SamsWeekPartlyPresented()
    {
        var r = _db.Rotation();
        await r.AddMemberAsync(Robin, "Robin");
        await r.AddMemberAsync(Sam, "Sam");
        await r.SetWeekAsync(_week, Sam);
        var days = Enumerable.Range(0, 5).Select(i => _week.AddDays(i)).ToList();
        var ids = new List<Guid>();
        foreach (var d in days.Take(4)) ids.Add((await _db.Briefs().GetOrStartAsync(d)).Id);
        await using var ctx = _db.Context();
        (await ctx.Briefs.SingleAsync(b => b.Id == ids[0])).Status = BriefStatus.Presented;
        (await ctx.Briefs.SingleAsync(b => b.Id == ids[1])).Status = BriefStatus.Sent;
        ctx.MeetingAttendees.Add(new MeetingAttendee { BriefId = ids[2], Name = "In the room" });
        await ctx.SaveChangesAsync();
        return days;
    }

    [Fact]
    public async Task GivingTheWeekToSomeoneElse_KeepsThePresenterOfDaysAlreadyPresented_AndMovesTheRest()
    {
        var days = await SamsWeekPartlyPresented();

        var set = await _db.Rotation().SetWeekAsync(_week, Robin);

        set.Kept.Should().Equal((days[0], "Sam"), (days[1], "Sam"), (days[2], "Sam"));
        set.Corrected.Should().BeEmpty();
        var cal = await _db.Rotation().CalendarAsync(days[0], days[4]);
        cal.Select(d => d.AnalystName).Should().Equal("Sam", "Sam", "Sam", "Robin", "Robin");
        cal.Should().OnlyContain(d => d.RegularName == "Robin");
        cal.Take(3).Should().OnlyContain(d => !d.IsChanged, "a kept day isn't a swap or cover");
        (await _db.Briefs().GetAsync(days[3]))!.AnalystName.Should().Be("Robin", "a draft's brief follows the rota");

        // Back to Sam: the kept days are the week's again, and nothing is left kept.
        (await _db.Rotation().SetWeekAsync(_week, Sam)).Kept.Should().BeEmpty();
        await using var ctx = _db.Context();
        (await ctx.RotationOverrides.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ClearingTheWeek_StillKeepsWhoPresentedItsPresentedDays()
    {
        var days = await SamsWeekPartlyPresented();

        (await _db.Rotation().SetWeekAsync(_week, null)).Kept.Should().HaveCount(3);

        (await _db.Rotation().CalendarAsync(days[0], days[4])).Select(d => d.AnalystName).Should().Equal("Sam", "Sam", "Sam", null, null);
    }

    [Fact]
    public async Task CorrectPastDays_ChangesThePresentedDaysToo_AndTheAuditLogSaysItWasACorrection()
    {
        var days = await SamsWeekPartlyPresented();
        await _db.Rotation().SetWeekAsync(_week, Robin);

        var set = await _db.Rotation().SetWeekAsync(_week, Robin, correctPastDays: true);

        set.Corrected.Should().Equal(days[0], days[1], days[2]);
        set.Kept.Should().BeEmpty();
        (await _db.Rotation().CalendarAsync(days[0], days[4])).Should().OnlyContain(d => d.AnalystName == "Robin");
        await using var ctx = _db.Context();
        var entry = await ctx.AuditLog.SingleAsync(e => e.EntityType == nameof(RotationWeek) && e.ChangesJson!.Contains("Correction"));
        entry.ChangesJson.Should().Contain("Presented days corrected").And.Contain(days[0].ToString("ddd MMM d", System.Globalization.CultureInfo.InvariantCulture));
        entry.UserName.Should().Be(_db.User.DisplayName);
    }

    [Fact]
    public async Task ADayAnApprovedChangeMoved_StaysWithTheChange_EvenAsACorrection()
    {
        var days = await SamsWeekPartlyPresented();
        await using (var ctx = _db.Context())
        {
            var change = new RotationChange
            {
                Kind = RotationChangeKind.Cover, RequesterId = Sam, RequesterName = "Sam", PartnerId = "dev:kim", PartnerName = "Kim",
                GiveFrom = days[0], GiveTo = days[0], Status = RotationChangeStatus.Approved, CreatedById = Sam, CreatedByName = "Sam", CreatedAtUtc = DateTimeOffset.UtcNow,
            };
            ctx.RotationChanges.Add(change);
            ctx.RotationOverrides.Add(new RotationOverride { Date = days[0], AnalystId = "dev:kim", AnalystName = "Kim", ChangeId = change.Id });
            await ctx.SaveChangesAsync();
        }

        var set = await _db.Rotation().SetWeekAsync(_week, Robin, correctPastDays: true);

        set.Corrected.Should().Equal(days[1], days[2]);
        (await _db.Rotation().CalendarAsync(days[0], days[0]))[0].AnalystName.Should().Be("Kim");
    }
}

/// <summary>The admin rotation page saying which days a week change kept and why, and the one-step correction.</summary>
public class RotaLockPageTests(TideApp app) : IClassFixture<TideApp>
{
    [Fact]
    public async Task TheRotaPage_SaysWhichPresentedDaysWereKept_AndCorrectPastDaysIsOneDeliberateStep()
    {
        var robin = "dev:lock-robin";
        var sam = "dev:lock-sam";
        DateOnly week;
        await using (var admin = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst))
        {
            var rotation = admin.ServiceProvider.GetRequiredService<RotationService>();
            await rotation.AddMemberAsync(robin, "Robin Lock");
            await rotation.AddMemberAsync(sam, "Sam Lock");
            week = RotationService.WeekStart(rotation.Today).AddDays(91);
            await rotation.SetWeekAsync(week, sam);
            var brief = await admin.ServiceProvider.GetRequiredService<Tide.Application.Briefs.BriefService>().GetOrStartAsync(week);
            await using var ctx = await admin.ServiceProvider.GetRequiredService<IAppDbContextFactory>().CreateAsync();
            (await ctx.Briefs.SingleAsync(b => b.Id == brief.Id)).Status = BriefStatus.Presented;
            await ctx.SaveChangesAsync();
        }
        await using var page = await ComponentHarness.RenderAsync<RotationAdmin>(app, "/admin/rotation");
        (await page.Html()).Should().Contain("<label class=\"form-check-label\" for=\"correct-past\">Correct past days</label>");

        await page.Click(p => p.SetWeek(week, robin));

        var monday = week.ToString("ddd MMM d", System.Globalization.CultureInfo.InvariantCulture);
        page.Component._weekNote.Should().Be($"Kept Sam Lock on {monday}: that day's brief was already presented. Tick Correct past days to change it too.");

        await page.Click(p => { p._correctPast = true; return p.SetWeek(week, robin); });

        page.Component._weekNote.Should().Be($"Corrected {monday}, already presented, to the new analyst. The audit log records the correction.");
        page.Component._correctPast.Should().BeFalse("a correction is one deliberate step, not a mode left on");
    }
}
