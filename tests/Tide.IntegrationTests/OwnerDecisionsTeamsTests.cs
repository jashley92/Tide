using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Abstractions;
using Tide.Application.Actions;
using Tide.Application.Articles;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Web.Components.Pages;
using Tide.Web.Components.Pages.Admin;
using Tide.Web.Services;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>
/// The owner's decision on teams: a team carries an edit version, so a form opened before a colleague's change to the
/// team (a save, its limit, its escalation contact, retiring or bringing it back) can't save over it. Its members and
/// its place in the list don't make a form stale.
/// </summary>
public class OwnerDecisionsTeamsTests : IDisposable
{
    private readonly TestDb _db = new();

    public OwnerDecisionsTeamsTests() => _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };

    public void Dispose() => _db.Dispose();

    private async Task<Team> Get(Guid id) => (await _db.Teams().ListAsync(includeRetired: true)).Single(t => t.Id == id);

    [Fact]
    public async Task AnInternalTeamsForm_SavedAfterAnotherAdministratorsSave_IsRefused_AndTheirsKept()
    {
        var teams = _db.Teams();
        var id = await teams.AddAsync("Network", null, "network@example.org");
        var opened = (await Get(id)).EditVersion;
        await teams.UpdateAsync(id, "Network Engineering", null, "neteng@example.org", (await Get(id)).EditVersion);   // a colleague

        await FluentActions.Invoking(() => teams.UpdateAsync(id, "Network", "Routers and firewalls", "network@example.org", opened))
            .Should().ThrowAsync<StaleEditException>().WithMessage("Someone else changed Network Engineering since you opened it*");
        var now = await Get(id);
        (now.Name, now.Email, now.Description).Should().Be(("Network Engineering", "neteng@example.org", null));

        // Opened again, on top of theirs, it saves, and the save's later parts go on the version it left.
        var version = await teams.UpdateAsync(id, "Network Engineering", "Routers and firewalls", "neteng@example.org", now.EditVersion);
        version = await teams.SetLimitAsync(id, 5, ActiveLimitMode.Show, version);
        await teams.SetEscalationContactAsync(id, "Dana", "dana@example.org", version);
        (await Get(id)).Description.Should().Be("Routers and firewalls");
    }

    [Fact]
    public async Task AFormOpenedBefore_ALimitAnEscalationContactOrARetirement_CantSaveThemAway()
    {
        var teams = _db.Teams();
        var id = await teams.AddExternalAsync(new ExternalTeamForm { Name = "Facilities", ContactEmail = "facilities@example.org" });
        var limited = ExternalTeamForm.From(await Get(id));
        await teams.SetLimitAsync(id, 3, ActiveLimitMode.Refuse);
        await FluentActions.Invoking(() => teams.UpdateExternalAsync(id, limited)).Should().ThrowAsync<StaleEditException>();

        var contacted = ExternalTeamForm.From(await Get(id));
        await teams.SetEscalationContactAsync(id, "Lee", "lee@example.org");
        await FluentActions.Invoking(() => teams.UpdateExternalAsync(id, contacted)).Should().ThrowAsync<StaleEditException>();
        (await Get(id)).EscalationEmail.Should().Be("lee@example.org", "the form opened before can't clear it");

        // The internal form's other parts are checked too: a limit saved from a form opened before a retirement.
        var internalId = await teams.AddAsync("Applications", null, null);
        await _db.Actions().CreateAsync(new ActionForm { Title = "Patch", Kind = ActionKind.PatchRemediate, TeamId = internalId });   // so it's retired, not deleted
        var opened = (await Get(internalId)).EditVersion;
        await teams.SetRetiredAsync(internalId, true);
        await teams.SetRetiredAsync(internalId, false);
        await FluentActions.Invoking(() => teams.SetLimitAsync(internalId, 10, ActiveLimitMode.Show, opened)).Should().ThrowAsync<StaleEditException>();
        (await Get(internalId)).ActiveLimit.Should().BeNull();
    }

    [Fact]
    public async Task MembersAndTheListsOrder_DontMakeATeamsFormStale()
    {
        var teams = _db.Teams();
        var id = await teams.AddAsync("Identity", null, null);
        await teams.AddAsync("Fraud", null, null);
        var opened = (await Get(id)).EditVersion;

        await teams.AddMemberAsync(id, "dev:sam", "Sam");
        await teams.RemoveMemberAsync(id, "dev:sam");
        await teams.MoveAsync(id, 1);

        (await Get(id)).EditVersion.Should().Be(opened);
        await teams.UpdateAsync(id, "Identity and access", null, null, opened);
        (await Get(id)).Name.Should().Be("Identity and access");
    }
}

/// <summary>Teams &amp; SLA and External teams with a colleague changing the team someone is editing.</summary>
public class OwnerDecisionsTeamsPageTests(TideApp app) : IClassFixture<TideApp>
{
    private static readonly SignIn Colleague = new("dev:odt-robin", "Robin", "Analyst", "Admin");

    private static IReadOnlyList<ToastService.Toast> Toasts<T>(Rendered<T> r) => r.Scope.ServiceProvider.GetRequiredService<ToastService>().Toasts;

    // Robin's team service, saving through the page's own origin, which its live feed skips: the page hears Robin's
    // change only when the test hands it over.
    private static TeamService Unheard<T>(AsyncServiceScope robin, Rendered<T> page) where T : Microsoft.AspNetCore.Components.IComponent =>
        ActivatorUtilities.CreateInstance<TeamService>(robin.ServiceProvider, page.Scope.ServiceProvider.GetRequiredService<IAppDbContextFactory>());

    private static string Named(string name) => $"{name} {Guid.NewGuid().ToString("N")[..6]}";

    [Fact]
    public async Task TeamsAdmin_AFormRefusedAsStale_StaysWithABanner_AndReloadShowsTheirs()
    {
        var name = Named("ODT Network");
        Guid id;
        await using (var scope = SignedIn(app, SignIn.Analyst))
            id = await scope.ServiceProvider.GetRequiredService<TeamService>().AddAsync(name, null, null);
        await using var page = await RenderAsync<TeamsAdmin>(app, "/admin/teams");
        await page.Click(p => { p.Edit(p.Shown.Single(t => t.Id == id)); p.EditName = name + " (typed)"; return Task.CompletedTask; });

        await using (var robin = SignedIn(app, Colleague))
            await Unheard(robin, page).UpdateAsync(id, name, "Robin's description", null);
        await page.Click(p => p.Save(id));

        Toasts(page).Should().Contain(t => t.IsError && t.Message.StartsWith($"Someone else changed {name} since you opened it"));
        page.Component.ChangedBy.Should().Be("Someone");
        page.Component.EditName.Should().Be(name + " (typed)", "the form stays to copy from");
        await using (var check = SignedIn(app, SignIn.Analyst))
            (await check.ServiceProvider.GetRequiredService<TeamService>().ListAsync()).Single(t => t.Id == id).Description.Should().Be("Robin's description");

        await page.Click(p => p.ReloadTheirs());
        page.Component.ChangedBy.Should().BeNull();
        (page.Component.EditName, page.Component.EditDescription).Should().Be((name, "Robin's description"));
    }

    [Fact]
    public async Task ExternalTeams_AColleaguesChangeToTheTeamBeingEdited_ShowsTheBanner_AndItsSaveIsRefused()
    {
        var name = Named("ODT Legal");
        Guid id;
        await using (var scope = SignedIn(app, SignIn.Analyst))
            id = await scope.ServiceProvider.GetRequiredService<TeamService>().AddExternalAsync(name, "legal@example.org");
        await using var page = await RenderAsync<ExternalTeams>(app, "/teams/external");
        await page.Click(p => { p.Edit(p.Shown.Single(t => t.Id == id)); p.EditForm.Engage = "Typed, not saved"; return Task.CompletedTask; });

        await using (var robin = SignedIn(app, Colleague))
        {
            var form = ExternalTeamForm.From((await robin.ServiceProvider.GetRequiredService<TeamService>().ListAsync()).Single(t => t.Id == id));
            form.ContactName = "Robin's contact";
            await Unheard(robin, page).UpdateExternalAsync(id, form);
        }
        await page.Click(p => p.OnChangedElsewhere([new Change(ChangeFeed.Teams, Colleague.UserId, Colleague.Name, Guid.NewGuid())]));
        page.Component.ChangedBy.Should().Be("Robin");
        page.Component.EditForm.Engage.Should().Be("Typed, not saved", "what's typed stays");

        await page.Click(p => p.Save(id));
        Toasts(page).Should().Contain(t => t.IsError && t.Message.StartsWith($"Someone else changed {name}"));
        page.Component.Editing.Should().Be(id, "the form stays open");

        await page.Click(p => p.ReloadTheirs());
        (page.Component.ChangedBy, page.Component.EditForm.ContactName, page.Component.EditForm.Engage).Should().Be((null, "Robin's contact", null));
    }
}
