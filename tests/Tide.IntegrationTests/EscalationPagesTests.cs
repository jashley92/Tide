using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Abstractions;
using Tide.Application.Actions;
using Tide.Application.Operations;
using Tide.Application.Settings;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Web.Components.Pages;
using Tide.Web.Components.Pages.Admin;
using Tide.Web.Components.Shared;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>The escalation ladder's settings: each checked as it's saved, laid over the server's at once, audited, and
/// carried in the configuration bundle.</summary>
public class EscalationSettingsTests
{
    [Fact]
    public async Task TheLadder_IsSavedChecked_WinsAtOnce_AndIsAudited()
    {
        using var rig = new SettingsRig(new() { ["Notifications:Escalation:Steps"] = "after 5: flag" });
        rig.Now<ReminderOptions>().Escalation.Steps.Should().Be("after 5: flag", "the server's value holds until one is saved");

        await rig.Get<OperationalSettingsService>().SaveAsync(new Dictionary<string, string?>
        {
            [OperationalSettings.Escalation] = "true",
            [OperationalSettings.EscalationScope] = "externalonly",
            [OperationalSettings.EscalationSteps] = "BEFORE 1: remind;after 4: contact, flag",
        });

        var o = rig.Now<ReminderOptions>().Escalation;
        (o.Enabled, o.Scope, o.Steps).Should().Be((true, EscalationScope.ExternalOnly, "before 1: remind; after 4: contact, flag"));
        rig.Now<ReminderOptions>().IsEnabled(ReminderKind.Escalation).Should().BeTrue();
        await using var ctx = rig.Db.Context();
        (await ctx.Settings.SingleAsync(s => s.Key == OperationalSettings.EscalationSteps)).Value.Should().Be("before 1: remind; after 4: contact, flag");
        (await ctx.AuditLog.AnyAsync(a => a.EntityType == nameof(AppSetting) && a.EntityKey == OperationalSettings.EscalationSteps)).Should().BeTrue();
    }

    [Theory]
    [InlineData(OperationalSettings.EscalationScope, "Everything")]
    [InlineData(OperationalSettings.EscalationScope, "1")]
    [InlineData(OperationalSettings.EscalationSteps, "after 61: flag")]
    [InlineData(OperationalSettings.EscalationSteps, "after 3: flag; after 3: flag")]
    [InlineData(OperationalSettings.Escalation, "maybe")]
    public async Task ABadValue_IsRefused_AndNothingIsSaved(string key, string value)
    {
        using var rig = new SettingsRig();
        var save = () => rig.Save(key, value);
        await save.Should().ThrowAsync<ArgumentException>();
        await using var ctx = rig.Db.Context();
        (await ctx.Settings.AnyAsync(s => s.Key == key)).Should().BeFalse();
    }

    [Fact]
    public async Task TheConfigurationBundle_CarriesTheLadder_AndAnImportTakesEffect_SkippingABadOne()
    {
        using var source = new SettingsRig();
        await source.Save(OperationalSettings.EscalationSteps, "due: remind; after 2: raise");
        await source.Save(OperationalSettings.EscalationScope, "PartsOnly");
        var bundle = await source.Get<ConfigBundleService>().ExportAsync();
        bundle.Settings.Should().Contain(new BundleSetting(OperationalSettings.EscalationSteps, "due: remind; after 2: raise"))
            .And.Contain(new BundleSetting(OperationalSettings.EscalationScope, "PartsOnly"));
        SettingsCatalog.Find(OperationalSettings.EscalationSteps)!.EditedIn.Should().Be("admin/teams");

        using var target = new SettingsRig();
        var done = await target.Get<ConfigBundleService>().PreviewAndImportAsync(bundle with
        {
            Settings = [.. bundle.Settings, new BundleSetting(OperationalSettings.Escalation, "sometimes")],
        });
        done.Changes.Single(c => c.Item == OperationalSettings.Escalation).Kind.Should().Be(BundleChangeKind.Skipped);
        done.Changes.Single(c => c.Item == OperationalSettings.EscalationSteps).Kind.Should().Be(BundleChangeKind.Add);
        target.Now<ReminderOptions>().Escalation.Should().Match<EscalationOptions>(o =>
            o.Steps == "due: remind; after 2: raise" && o.Scope == EscalationScope.PartsOnly && !o.Enabled);
    }
}

/// <summary>The ladder in the real app: its section on Teams &amp; SLA, the escalation contact on both team forms, and the
/// flag on Today, an action's page and the lists.</summary>
public class EscalationPagesTests(TideApp app) : IClassFixture<TideApp>
{
    private static readonly SignIn Owner = new("dev:esc-owner", "Escalation Owner", "ActionOwner");

    private async Task<string?> Saved(string key)
    {
        await using var scope = app.Services.CreateAsyncScope();
        await using var db = await scope.ServiceProvider.GetRequiredService<IAppDbContextFactory>().CreateAsync();
        return db.Settings.Where(s => s.Key == key).Select(s => s.Value).FirstOrDefault();
    }

    private async Task<Team> TeamNamed(string name)
    {
        await using var scope = SignedIn(app, SignIn.Analyst);
        return (await scope.ServiceProvider.GetRequiredService<TeamService>().ListAsync()).Single(t => t.Name == name);
    }

    [Fact]
    public async Task TeamsAndSla_EditsTheLadder_AndRefusesABadOne()
    {
        await using var r = await RenderAsync<TeamsAdmin>(app, "/admin/teams");
        var html = await r.Html();
        html.Should().Contain("id=\"esc-title\"").And.Contain("id=\"esc-on\"").And.Contain("id=\"esc-scope-PartsOnly\"")
            .And.Contain("id=\"esc-days-1\"").And.Contain("id=\"esc-when-1\"").And.Contain("id=\"esc-1-remind\"").And.Contain("id=\"esc-4-flag\"")
            .And.Contain("Add step").And.Contain("Use the defaults").And.Contain("Save escalation").And.Contain("aria-label=\"Run the escalation ladder now\"");
        html.Should().NotContain("id=\"rem-on-Escalation\"", "the ladder has its own section, not a reminder switch");

        // Saved off (the shared app's sweep mustn't climb other tests' actions), with its scope and steps.
        var form = r.Component.Escalation;
        form.On.Should().BeFalse("off until it's turned on");
        form.Scope = EscalationScope.ExternalOnly;
        form.Remove(form.Steps[3]);
        await r.Click(c => c.SaveEscalation());
        (await Saved(OperationalSettings.EscalationScope)).Should().Be("ExternalOnly");
        (await Saved(OperationalSettings.EscalationSteps)).Should().Be("before 2: remind; after 3: contact; after 7: raise, flag");
        (await Saved(OperationalSettings.Escalation)).Should().Be("false");

        r.Component.Escalation.Steps.Add(new StepRow { When = StepWhen.After, Days = 3, Does = EscalationDoes.CopyContact });
        await r.Click(c => c.SaveEscalation());
        (await Saved(OperationalSettings.EscalationSteps)).Should().Be("before 2: remind; after 3: contact; after 7: raise, flag", "a duplicate is refused whole");
        r.Scope.ServiceProvider.GetRequiredService<Tide.Web.Services.ToastService>().Toasts
            .Should().Contain(t => t.IsError && t.Message.Contains("Steps 2 and 4 are the same"));
    }

    [Fact]
    public async Task TeamsAndSla_SetsAnInternalTeamsEscalationContact()
    {
        await using (var scope = SignedIn(app, SignIn.Analyst))
            await scope.ServiceProvider.GetRequiredService<TeamService>().AddAsync("Escalation Page Team", null, null);
        var team = await TeamNamed("Escalation Page Team");

        await using var r = await RenderAsync<TeamsAdmin>(app, "/admin/teams");
        await r.Click(c =>
        {
            c.Edit(c.Shown.Single(t => t.Id == team.Id));
            return Task.CompletedTask;
        });
        (r.Component.EditForm.EscalationName, r.Component.EditForm.EscalationEmail) = ("Pat Lee", "pat.lee@example.org");
        await r.Click(c => c.Save(team.Id));

        (await TeamNamed("Escalation Page Team")).Should().Match<Team>(t => t.EscalationName == "Pat Lee" && t.EscalationEmail == "pat.lee@example.org");
        await using var again = await RenderAsync<TeamsAdmin>(app, "/admin/teams");
        (await again.Html()).Should().Contain("Escalation contact: Pat Lee <span class=\"tide-data\">pat.lee@example.org</span>");
    }

    [Fact]
    public async Task ExternalTeams_KeepTheirEscalationContact_ForWhoeverWorksActions()
    {
        await using var r = await RenderAsync<ExternalTeams>(app, "/teams/external", null, new SignIn("dev:esc-ext", "Ext Owner", "ActionOwner"));
        (await r.Html()).Should().Contain("id=\"new-esc-name\"").And.Contain("id=\"new-esc-email\"");
        r.Component.NewForm.Name = "Escalation Hosting";
        r.Component.NewForm.ContactEmail = "noc@hosting.example";
        r.Component.NewForm.EscalationName = "Morgan";
        r.Component.NewForm.EscalationEmail = "duty@hosting.example";
        await r.Click(c => c.Add());

        var team = await TeamNamed("Escalation Hosting");
        (team.IsExternal, team.EscalationName, team.EscalationEmail).Should().Be((true, "Morgan", "duty@hosting.example"));
        await using var again = await RenderAsync<ExternalTeams>(app, "/teams/external");
        (await again.Html()).Should().Contain("<dt>Escalation contact</dt>").And.Contain("duty@hosting.example");
    }

    [Fact]
    public async Task AFlaggedAction_IsOnToday_ItsPage_AndTheLists_MarkedEscalated()
    {
        int number;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        await using (var scope = SignedIn(app, SignIn.Analyst))
        {
            var teams = scope.ServiceProvider.GetRequiredService<TeamService>();
            var actions = scope.ServiceProvider.GetRequiredService<ActionService>();
            today = actions.Today;
            var team = await teams.AddAsync("Escalation Today Team", null, null);
            await teams.AddMemberAsync(team, Owner.UserId, Owner.Name);
            number = await actions.CreateAsync(new ActionForm { Title = "Escalated: patch the VPN", TeamId = team, DueDate = today.AddDays(-8) });
            await using var db = await scope.ServiceProvider.GetRequiredService<IAppDbContextFactory>().CreateAsync();
            var flagged = await db.Actions.SingleAsync(a => a.Number == number);
            (flagged.EscalatedForDue, flagged.EscalatedAtUtc) = (flagged.DueDate, DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        }

        await using (var home = await RenderAsync<Home>(app, "/", null, Owner))
        {
            var html = System.Net.WebUtility.HtmlDecode(await home.Html());
            var at = html.IndexOf("id=\"escalated-h\"", StringComparison.Ordinal);
            at.Should().BePositive("Today lists it for its team");
            at.Should().BeLessThan(html.IndexOf("id=\"team-queue-h\"", StringComparison.Ordinal), "before the queue");
            html[at..].Should().Contain("Escalated: patch the VPN").And.Contain("Escalation Today Team");
        }

        await using (var page = await RenderAsync<ActionPage>(app, "/actions/" + number, new() { ["Number"] = (int?)number }))
            (await page.Html()).Should().Contain("bi-arrow-up-circle-fill").And.Contain(">Escalated</span>");

        ActionRow row;
        await using (var scope = SignedIn(app, SignIn.Analyst))
            row = (await scope.ServiceProvider.GetRequiredService<ActionService>().ListAsync(new ActionFilter(ActionView.Overdue)))
                .Single(x => x.Number == number);
        row.Escalated.Should().BeTrue();
        await using (var table = await RenderAsync<ActionTable>(app, "/actions", new() { ["Rows"] = (IReadOnlyList<ActionRow>)[row], ["Today"] = today }))
            (await table.Html()).Should().Contain(">Escalated</span>");
        await using (var board = await RenderAsync<ActionKanban>(app, "/actions?layout=board", new()
               {
                   ["Board"] = new ActionBoard([row], true, new HashSet<Guid>()), ["Today"] = today,
               }))
            (await board.Html()).Should().Contain(">Escalated</span>");
        await using (var closed = await RenderAsync<ActionTable>(app, "/actions", new()
               {
                   ["Rows"] = (IReadOnlyList<ActionRow>)[row with { Status = ActionStatus.Done }], ["Today"] = today,
               }))
            (await closed.Html()).Should().NotContain(">Escalated</span>", "a closed action isn't escalated, whatever its flag");
    }
}
