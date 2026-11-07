using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tide.Application.Actions;
using Tide.Application.Org;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.TestSupport;

namespace Tide.IntegrationTests;

/// <summary>
/// Owner's decision: an external team's contact gets one reminder about an action a day. The escalation ladder's remind
/// step and the team's own cadence both fall two business days before the due date with the default settings; whichever
/// runs second is skipped, says so in the history, and is still marked done so it doesn't go the next day instead.
/// </summary>
public class OneReminderADayTests : IDisposable
{
    private readonly TestDb _db = new();

    public OneReminderADayTests() =>
        (_db.User.UserId, _db.User.DisplayName, _db.User.Roles) = ("S-1-5-21-TEST-1", "Test Analyst", new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin });

    public void Dispose() => _db.Dispose();

    // Nov 2026 at 14:00 UTC (past the reminder time); Thanksgiving and the day after off.
    private static OrgClock At(int day)
    {
        var clock = new OrgClock(Options.Create(new OrgOptions()), new TestClock(new DateTimeOffset(2026, 11, day, 14, 0, 0, TimeSpan.Zero)));
        clock.UseHolidays(new Dictionary<DateOnly, string> { [new DateOnly(2026, 11, 26)] = "Thanksgiving", [new DateOnly(2026, 11, 27)] = "Day after" });
        return clock;
    }

    // Both on, with their defaults: the ladder reminds two business days before, as the team's cadence does.
    private static ReminderOptions Both => new() { ExternalReminders = { Enabled = true }, Escalation = { Enabled = true, Steps = EscalationLadder.DefaultText } };

    private async Task<ActionItem> DueTuesday()
    {
        var team = await _db.Teams().AddExternalAsync(new ExternalTeamForm { Name = "Network Engineering", ContactName = "Alex", ContactEmail = "network@network.example" });
        var number = await _db.Actions().CreateAsync(new ActionForm { Title = "Block the domains", TeamId = team, Priority = Priority.P3 });
        await using var ctx = _db.Context();
        var row = await ctx.Actions.SingleAsync(x => x.Number == number);
        row.DueDate = new DateOnly(2026, 11, 24);
        await ctx.SaveChangesAsync();
        return row;
    }

    private async Task<List<string>> History(ActionItem a)
    {
        await using var ctx = _db.Context();
        return await ctx.ActionEvents.AsNoTracking().Where(e => e.ActionId == a.Id && e.ByName == ReminderService.EscalatedBy)
            .OrderBy(e => e.AtUtc).Select(e => e.Text).ToListAsync();
    }

    [Fact]
    public async Task TheTeamsReminderFirst_TheLaddersRemindIsSkipped_SaysSo_AndNeitherGoesTheNextDay()
    {
        var a = await DueTuesday();

        await _db.Reminders(Both, At(20)).RunDueAsync();   // Friday: two business days before Tuesday

        _db.Email.Sent.Should().ContainSingle("the contact is reminded once that day").Which.To.Should().Equal("network@network.example");
        (await History(a)).Should().Equal(
            "Escalated, 2 business days before its due date: reminder skipped: network@network.example was already reminded today.");

        await _db.Reminders(Both, At(23)).RunDueAsync();   // Monday
        _db.Email.Sent.Should().HaveCount(1, "the skipped step was marked done, so it doesn't go the next day instead");
    }

    [Fact]
    public async Task TheLaddersRemindFirst_TheTeamsReminderIsSkipped_SaysSo_AndNeitherGoesTheNextDay()
    {
        var a = await DueTuesday();

        await _db.Reminders(Both, At(20)).RunAsync(ReminderKind.Escalation);
        await _db.Reminders(Both, At(20)).RunAsync(ReminderKind.ExternalReminder);

        _db.Email.Sent.Should().ContainSingle("the contact is reminded once that day");
        (await History(a)).Should().Equal(
            "Escalated, 2 business days before its due date: reminded Network Engineering at network@network.example.",
            "Reminder skipped: network@network.example was already reminded today.");

        await _db.Reminders(Both, At(23)).RunDueAsync();
        _db.Email.Sent.Should().HaveCount(1, "the team's step was marked done though it was skipped");
    }

    [Fact]
    public async Task ADifferentDay_IsADifferentReminder()
    {
        var a = await DueTuesday();
        // The ladder reminds on the due day; the team's cadence two business days before.
        var options = new ReminderOptions { ExternalReminders = { Enabled = true }, Escalation = { Enabled = true, Steps = "due: remind" } };

        await _db.Reminders(options, At(20)).RunDueAsync();
        await _db.Reminders(options, At(24)).RunDueAsync();

        _db.Email.Sent.Should().HaveCount(2, "one a day is the rule, not one a due date");
        (await History(a)).Should().Equal("Escalated, on its due date: reminded Network Engineering at network@network.example.");
    }
}
