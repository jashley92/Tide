using System.Net;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Actions;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Web.Components.Pages;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>
/// Owner's decision: an action's "Don't remind" isn't carried to the team it's handed to unseen. The hand-off asks
/// ("Keep reminders off", ticked), only while they're off; unticked, the hand-off turns them back on and its history
/// line says so. The service takes the choice explicitly; the editor's team change keeps them as they are by default.
/// </summary>
public class HandoffKeepsRemindersOffTests : IDisposable
{
    private readonly TestDb _db = new();

    public HandoffKeepsRemindersOffTests() =>
        (_db.User.UserId, _db.User.DisplayName, _db.User.Roles) = ("S-1-5-21-TEST-1", "Test Analyst", new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin });

    public void Dispose() => _db.Dispose();

    // An action on one external team, its reminders turned off, and another external team to hand it to.
    private async Task<(ActionItem Action, Guid To)> Quiet()
    {
        var from = await _db.Teams().AddExternalAsync("Network Engineering", "network@network.example");
        var to = await _db.Teams().AddExternalAsync("Hosting Provider", "noc@hosting.example");
        var number = await _db.Actions().CreateAsync(new ActionForm { Title = "Block the domains", TeamId = from });
        var a = (await _db.Actions().GetAsync(number))!.Action;
        await _db.Actions().SetNoRemindersAsync(a.Id, off: true);
        return ((await _db.Actions().GetAsync(number))!.Action, to);
    }

    private async Task<(ActionItem Action, List<string> History)> Read(ActionItem a)
    {
        var d = (await _db.Actions().GetAsync(a.Number))!;
        return (d.Action, [.. d.Action.Events.OrderBy(e => e.AtUtc).Select(e => e.Text)]);
    }

    [Fact]
    public async Task HandingOff_WithKeepRemindersOffUnticked_TurnsThemBackOn_AndTheHistorySaysSo()
    {
        var (a, to) = await Quiet();

        await _db.Handoffs().HandOffAsync(a.Id, to, "They host the resolvers", turnRemindersOn: true);

        var (after, history) = await Read(a);
        after.NoReminders.Should().BeFalse("the new team is reminded about it");
        history.Last().Should().Be("Handed from Network Engineering to Hosting Provider: They host the resolvers " + HandoffService.RemindersBackOn);
    }

    [Fact]
    public async Task HandingOff_WithKeepRemindersOffTicked_KeepsThemOff_AndWhileTheyreOn_TheChoiceChangesNothing()
    {
        var (a, to) = await Quiet();
        await _db.Handoffs().HandOffAsync(a.Id, to, "They host the resolvers");
        var (kept, history) = await Read(a);
        kept.NoReminders.Should().BeTrue();
        history.Last().Should().Be("Handed from Network Engineering to Hosting Provider: They host the resolvers");

        var other = await _db.Teams().AddExternalAsync("Registrar", "abuse@registrar.example");
        await _db.Actions().SetNoRemindersAsync(a.Id, off: false);
        await _db.Handoffs().HandOffAsync(a.Id, other, "They hold the domain");
        (await Read(a)).Action.NoReminders.Should().BeFalse("keeping them off never turns them off");
    }

    [Fact]
    public async Task ATeamChangedInTheEditor_KeepsThemAsTheyAreByDefault_OrTurnsThemBackOn()
    {
        var (a, to) = await Quiet();
        var form = ActionForm.From(a);
        form.TurnRemindersOn.Should().BeFalse("they stay as they are, by default");
        form.TeamId = to;
        await _db.Actions().SaveAsync(form);
        (await Read(a)).Action.NoReminders.Should().BeTrue();

        var back = ActionForm.From((await Read(a)).Action);
        back.TeamId = (await _db.Teams().AddExternalAsync("Registrar", "abuse@registrar.example"));
        back.TurnRemindersOn = true;
        await _db.Actions().SaveAsync(back);
        var (after, history) = await Read(a);
        after.NoReminders.Should().BeFalse();
        history.Should().Contain(h => h.StartsWith("Handed from Hosting Provider to Registrar") && h.Contains(HandoffService.RemindersBackOn));
    }
}

/// <summary>The hand-off dialog's "Keep reminders off": shown, ticked, only while the action's reminders are off.</summary>
public class HandoffKeepsRemindersOffPageTests(TideApp app) : IClassFixture<TideApp>
{
    private async Task<T> As<T>(Func<IServiceProvider, Task<T>> what)
    {
        await using var scope = SignedIn(app, SignIn.Analyst);
        return await what(scope.ServiceProvider);
    }

    private Task<Rendered<ActionPage>> Open(int number) => RenderAsync<ActionPage>(app, "/actions/" + number, new() { ["Number"] = (int?)number });

    private Task<ActionItem> Action(int number) => As(async sp => (await sp.GetRequiredService<ActionService>().GetAsync(number))!.Action);

    [Fact]
    public async Task TheDialog_AsksOnlyWhileTheyreOff_TickedByDefault_AndUntickingTurnsThemBackOn()
    {
        var (from, to, number) = await As(async sp =>
        {
            var teams = sp.GetRequiredService<TeamService>();
            var f = await teams.AddExternalAsync("DecA remind-off sender", "a@decA.example");
            var t = await teams.AddExternalAsync("DecA remind-off receiver", "b@decA.example");
            var n = await sp.GetRequiredService<ActionService>().CreateAsync(new ActionForm { Title = "DecA quiet action", TeamId = f });
            return (f, t, n);
        });

        await using (var page = await Open(number))
        {
            await page.Press(p => p.StartHandoff());
            (await page.HtmlOnce("Hand it to")).Should().NotContain("Keep reminders off", "its reminders are on: nothing to ask");
        }

        await As(async sp =>
        {
            var svc = sp.GetRequiredService<ActionService>();
            await svc.SetNoRemindersAsync((await svc.GetAsync(number))!.Action.Id, off: true);
            return 0;
        });
        await using var quiet = await Open(number);
        await quiet.Press(p => p.StartHandoff());
        WebUtility.HtmlDecode(await quiet.HtmlOnce("Hand it to")).Should().Contain("Keep reminders off");
        quiet.Component.KeepRemindersOff.Should().BeTrue("ticked by default");
        await quiet.Click(async p =>
        {
            (p.HandTo, p.HandWhy, p.KeepRemindersOff) = (to, "Theirs now", false);
            (await p.HandOff()).Should().BeTrue(p.HandError);
        });

        var a = await Action(number);
        a.TeamId.Should().Be(to);
        a.NoReminders.Should().BeFalse();
        from.Should().NotBe(to);
    }
}
