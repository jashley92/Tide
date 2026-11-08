using System.Net;
using System.Reflection;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Abstractions;
using Tide.Application.Actions;
using Tide.Application.Articles;
using Tide.Application.Security;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Infrastructure.Persistence;
using Tide.Web.Components.Pages;
using Tide.Web.Components.Shared;
using Tide.Web.Services;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>
/// EP-89: the playbooks are the team's library, kept as the saved queries are. Who adds, edits, reorders, retires and
/// deletes them; the starters, TIDE's; the stale edit; and the audit trail and change feed.
/// </summary>
public class PlaybookLibraryTests : IDisposable
{
    private readonly TestDb _db = new();

    public PlaybookLibraryTests() => _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst };

    public void Dispose() => _db.Dispose();

    private Task<Guid> Save(string name, ActionKind? kind = ActionKind.IocSweep, params string[] steps) =>
        _db.Playbooks().SaveAsync(new PlaybookForm { Name = name, Kind = kind, Steps = steps.Length > 0 ? [.. steps] : ["Pull the indicators", "Sweep the proxy logs"] });

    private void As(string id, string name, params AppRole[] roles) =>
        (_db.User.UserId, _db.User.DisplayName, _db.User.Roles) = (id, name, roles.ToHashSet());

    // A team, added by an administrator, for an action to be raised on.
    private async Task<Guid> Team()
    {
        var (id, name, roles) = (_db.User.UserId, _db.User.DisplayName, _db.User.Roles);
        As("S-1-5-21-SETUP", "Setup", AppRole.Analyst, AppRole.Admin);
        var team = await _db.Teams().AddAsync("Cyber Defense " + Guid.NewGuid().ToString("N")[..4], null, null);
        (_db.User.UserId, _db.User.DisplayName, _db.User.Roles) = (id, name, roles);
        return team;
    }

    [Fact]
    public async Task WhoeverWorksActions_AddsEditsReordersAndRetires_AndAViewerOnlyReadsThem()
    {
        var sweep = await Save(" Ransomware sweep ");
        var p = (await _db.Playbooks().ListAsync()).Single();
        (p.Name, p.CreatedByName, p.CreatedBy, p.ModifiedAtUtc).Should().Be(("Ransomware sweep", "Test Analyst", "S-1-5-21-TEST-1", (DateTimeOffset?)null));
        _db.Playbooks().CanWrite.Should().BeTrue();

        As("S-1-5-21-OWNER", "Olu Owner", AppRole.ActionOwner);
        var closeOut = await Save("Close-out", null, "Tell the requester");
        await _db.Playbooks().MoveAsync(closeOut, -1);
        (await _db.Playbooks().ListAsync()).Select(x => x.Id).Should().Equal(closeOut, sweep);
        var form = PlaybookForm.From((await _db.Playbooks().ListAsync()).Single(x => x.Id == sweep));
        form.Steps.Add("Record hits");
        await _db.Playbooks().SaveAsync(form);
        await _db.Playbooks().SetRetiredAsync(sweep, true);
        await _db.Playbooks().SetRetiredAsync(sweep, true);   // already retired: nothing to do
        var retired = (await _db.Playbooks().ListAsync(includeRetired: true)).Single(x => x.Id == sweep);
        (retired.IsRetired, retired.ModifiedByName, retired.Steps.Count).Should().Be((true, "Olu Owner", 3));
        await _db.Playbooks().SetRetiredAsync(sweep, false);

        As("S-1-5-21-VIEWER", "Val Viewer", AppRole.Viewer);
        (await _db.Playbooks().ListAsync()).Should().HaveCount(2, "everyone who reads the team edition reads them");
        _db.Playbooks().CanWrite.Should().BeFalse();
        _db.Playbooks().CanDelete(retired).Should().BeFalse();
        await FluentActions.Invoking(() => Save("Viewer's")).Should().ThrowAsync<ForbiddenException>().WithMessage("Only people who work actions can change the playbooks.");
        await FluentActions.Invoking(() => _db.Playbooks().MoveAsync(sweep, -1)).Should().ThrowAsync<ForbiddenException>();
        await FluentActions.Invoking(() => _db.Playbooks().SetRetiredAsync(sweep, true)).Should().ThrowAsync<ForbiddenException>();
        await FluentActions.Invoking(() => _db.Playbooks().DeleteAsync(sweep)).Should().ThrowAsync<ForbiddenException>();
        await FluentActions.Invoking(() => _db.Playbooks().SetRetiredAsync(Guid.NewGuid(), true)).Should().ThrowAsync<ForbiddenException>("refused before it's looked for");

        As("S-1-5-21-READER", "Rhea Reader", AppRole.Reader);
        await FluentActions.Invoking(() => _db.Playbooks().ListAsync()).Should().ThrowAsync<ForbiddenException>();

        As("S-1-5-21-TEST-1", "Test Analyst", AppRole.Analyst);
        await FluentActions.Invoking(() => _db.Playbooks().SetRetiredAsync(Guid.NewGuid(), true)).Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task OnlyWhoeverMadeIt_OrAnAdministrator_DeletesOne()
    {
        As("S-1-5-21-OWNER", "Olu Owner", AppRole.ActionOwner);
        var owners = await Save("Owner's sweep");
        var another = await Save("Owner's other sweep");

        As("S-1-5-21-TEST-2", "Robin", AppRole.Analyst);
        var theirs = (await _db.Playbooks().ListAsync()).Single(p => p.Id == owners);
        _db.Playbooks().CanDelete(theirs).Should().BeFalse("another analyst retires it instead");
        await FluentActions.Invoking(() => _db.Playbooks().DeleteAsync(owners)).Should().ThrowAsync<ForbiddenException>().WithMessage("*Retire it instead*");
        await _db.Playbooks().DeleteAsync(Guid.NewGuid());   // one already gone: whoever could delete their own is let by

        As("S-1-5-21-OWNER", "Olu Owner", AppRole.ActionOwner);
        _db.Playbooks().CanDelete(theirs).Should().BeTrue();
        await _db.Playbooks().DeleteAsync(owners);

        As("S-1-5-21-ADMIN", "Ada Admin", AppRole.Admin);
        await _db.Playbooks().DeleteAsync(another);
        (await _db.Playbooks().ListAsync(includeRetired: true)).Should().BeEmpty();
        await using var ctx = _db.Context();
        (await ctx.AuditLog.CountAsync(e => e.EntityType == nameof(Playbook) && e.Action == "Deleted")).Should().Be(2);
    }

    [Fact]
    public async Task AStarter_MadeByTide_IsRetiredByAnAnalyst_ButDeletedOnlyByAnAdministrator()
    {
        // As the app's start does: no one signed in, so the rows are the system's.
        (_db.User.UserId, _db.User.IsAuthenticated) = ("system", false);
        await using (var ctx = _db.Context()) await DatabaseInitializer.SeedStarterLibrariesAsync(ctx);
        (_db.User.UserId, _db.User.IsAuthenticated) = ("S-1-5-21-TEST-1", true);

        var starters = await _db.Playbooks().ListAsync();
        starters.Should().HaveCount(StarterPlaybooks.All.Count).And.OnlyContain(p => p.CreatedByName == "TIDE" && p.CreatedBy == "system");
        var sweep = starters.Single(p => p.Kind == ActionKind.IocSweep);
        _db.Playbooks().CanDelete(sweep).Should().BeFalse();
        await FluentActions.Invoking(() => _db.Playbooks().DeleteAsync(sweep.Id)).Should().ThrowAsync<ForbiddenException>();
        await _db.Playbooks().SetRetiredAsync(sweep.Id, true);
        (await _db.Playbooks().ForKindAsync(ActionKind.IocSweep)).Should().NotContain(p => p.Id == sweep.Id);

        As("S-1-5-21-ADMIN", "Ada Admin", AppRole.Admin);
        _db.Playbooks().CanDelete(sweep).Should().BeTrue();
        await _db.Playbooks().DeleteAsync(sweep.Id);
        (await _db.Playbooks().ListAsync(includeRetired: true)).Should().HaveCount(StarterPlaybooks.All.Count - 1);
    }

    [Fact]
    public async Task AnEdit_BegunBeforeAColleaguesEdit_IsRefused_AndEachChangeIsAudited()
    {
        var id = await Save("Encoded PowerShell", ActionKind.ThreatHunt, "Sweep EDR", "Record hits");
        var p = (await _db.Playbooks().ListAsync()).Single();
        var mine = PlaybookForm.From(p);
        var theirs = PlaybookForm.From(p);

        As("S-1-5-21-TEST-2", "Robin", AppRole.Analyst);
        theirs.Steps.Insert(0, "Pull the indicators");
        await _db.Playbooks().SaveAsync(theirs);

        As("S-1-5-21-TEST-1", "Test Analyst", AppRole.Analyst);
        mine.Name = "Encoded PowerShell (mine)";
        await FluentActions.Invoking(() => _db.Playbooks().SaveAsync(mine)).Should().ThrowAsync<StaleEditException>()
            .WithMessage("Someone else changed this playbook since you opened it. Reload to see their changes.");

        var now = (await _db.Playbooks().ListAsync()).Single();
        (now.Name, now.ModifiedByName).Should().Be(("Encoded PowerShell", "Robin"));
        now.Steps.Select(s => s.Text).Should().Equal("Pull the indicators", "Sweep EDR", "Record hits");
        now.EditVersion.Should().NotBe(p.EditVersion);

        // Saved again unchanged: nothing moves. A new name alone leaves the steps as they were.
        await _db.Playbooks().SaveAsync(PlaybookForm.From(now));
        (await _db.Playbooks().ListAsync()).Single().EditVersion.Should().Be(now.EditVersion);
        var renamed = PlaybookForm.From(now);
        renamed.Name = "Encoded PowerShell from Office";
        renamed.Description = "  Macro droppers.  ";
        await _db.Playbooks().SaveAsync(renamed);
        var after = (await _db.Playbooks().ListAsync()).Single();
        (after.Name, after.Description).Should().Be(("Encoded PowerShell from Office", "Macro droppers."));
        after.Steps.Select(s => s.Id).Should().Equal(now.Steps.Select(s => s.Id), "the steps weren't written again");
        await FluentActions.Invoking(() => _db.Playbooks().SaveAsync(new PlaybookForm { Id = Guid.NewGuid(), Name = "Gone", Steps = ["x"] }))
            .Should().ThrowAsync<KeyNotFoundException>().WithMessage("That playbook no longer exists.");

        await using var ctx = _db.Context();
        (await ctx.AuditLog.Where(e => e.EntityType == nameof(Playbook) && e.EntityKey == id.ToString()).Select(e => e.Action).ToListAsync())
            .Should().BeEquivalentTo(["Added", "Modified", "Modified"], "each write is in the audit trail, as other team settings are");
        (await ctx.AuditLog.CountAsync(e => e.EntityType == nameof(PlaybookStep) && e.Action == "Deleted")).Should().Be(2, "the steps were replaced once, by Robin's edit");
        ChangeInterceptor.TopicsOf(now).Should().Equal(ChangeFeed.Playbooks);
        ChangeInterceptor.TopicsOf(now.Steps[0]).Should().Equal(ChangeFeed.Playbooks);
    }

    [Fact]
    public async Task Moving_StepsOverRetiredOnes_AndStopsAtEitherEnd()
    {
        var a = await Save("A sweep");
        var b = await Save("B sweep");
        var c = await Save("C sweep");
        await _db.Playbooks().SetRetiredAsync(b, true);
        var before = (await _db.Playbooks().ListAsync()).Single(p => p.Id == c).EditVersion;

        await _db.Playbooks().MoveAsync(c, -1);
        (await _db.Playbooks().ListAsync()).Select(p => p.Id).Should().Equal([c, a], "one click passes the retired one between them");
        await _db.Playbooks().MoveAsync(c, -1);   // already first: nothing moves
        await _db.Playbooks().MoveAsync(a, 1);    // already last of those offered
        await _db.Playbooks().MoveAsync(b, -1);   // a retired one isn't moved
        await _db.Playbooks().MoveAsync(Guid.NewGuid(), 1);
        (await _db.Playbooks().ListAsync()).Select(p => p.Id).Should().Equal(c, a);
        await _db.Playbooks().SetRetiredAsync(b, false);
        var all = await _db.Playbooks().ListAsync();
        all.Select(p => p.Id).Should().Equal([c, b, a], "the two swapped places, and the retired one kept its own");
        all.Single(p => p.Id == c).ModifiedByName.Should().Be("Test Analyst", "a move is a change like any other");
        all.Single(p => p.Id == c).EditVersion.Should().NotBe(before, "an edit begun before the move is refused, as after any change");
    }

    [Fact]
    public async Task TheList_CountsTheActionsStartedFromEach_AndMatchesWordsInTheSteps()
    {
        var team = await Team();
        var sweep = await Save("Ransomware sweep", ActionKind.IocSweep, "Pull the indicators", "Sweep DNS logs");
        var any = await Save("Close-out", null, "Tell the requester");
        await _db.Actions().CreateAsync(new ActionForm { Title = "One", Kind = ActionKind.IocSweep, TeamId = team, PlaybookId = sweep });
        await _db.Actions().CreateAsync(new ActionForm { Title = "Two", Kind = ActionKind.IocSweep, TeamId = team, PlaybookId = sweep });

        var rows = await _db.Playbooks().RowsAsync();
        rows.Single(r => r.Playbook.Id == sweep).Actions.Should().Be(2);
        rows.Single(r => r.Playbook.Id == any).Actions.Should().Be(0);

        var p = rows.Single(r => r.Playbook.Id == sweep).Playbook;
        PlaybookService.Matches(p, ["dns"]).Should().BeTrue("a step's words count");
        PlaybookService.Matches(p, ["ioc", "RANSOMWARE"]).Should().BeTrue("its kind's name and its own");
        PlaybookService.Matches(p, ["dns", "patch"]).Should().BeFalse("every word, not any");
        PlaybookService.Matches(rows.Single(r => r.Playbook.Id == any).Playbook, ["ioc"]).Should().BeFalse("one for any kind has no kind's name");
        PlaybookService.Suits(rows.Single(r => r.Playbook.Id == any).Playbook, ActionKind.Block).Should().BeTrue();
        PlaybookService.Suits(p, ActionKind.Block).Should().BeFalse();
    }
}

/// <summary>The Playbooks page, the old admin address, Board rules, and the pickers that use playbooks.</summary>
public class PlaybookPageTests(TideApp app) : IClassFixture<TideApp>
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly SignIn Viewer = new("dev:viewer-val", "Val Viewer", "Viewer");
    private static readonly SignIn Robin = new("dev:robin", "Robin", "Analyst");

    private static IReadOnlyList<ToastService.Toast> Toasts<T>(Rendered<T> r) => r.Scope.ServiceProvider.GetRequiredService<ToastService>().Toasts;

    private async Task<Guid> Saved(string name, ActionKind? kind, SignIn? who = null, params string[] steps)
    {
        await using var scope = SignedIn(app, who ?? Robin);
        return await scope.ServiceProvider.GetRequiredService<PlaybookService>().SaveAsync(
            new PlaybookForm { Name = name, Kind = kind, Steps = steps.Length > 0 ? [.. steps] : ["Pull the indicators"] });
    }

    private static Task Set<T>(Rendered<T> r, string field, object? value, string? then = null) where T : Microsoft.AspNetCore.Components.IComponent => r.Click(c =>
    {
        typeof(T).GetField(field, Private)!.SetValue(c, value);
        if (then is not null) typeof(T).GetMethod(then, Private)!.Invoke(c, null);
        // Drawn again, as a change from the page's own box would be.
        typeof(Microsoft.AspNetCore.Components.ComponentBase).GetMethod("StateHasChanged", Private)!.Invoke(c, null);
        return Task.CompletedTask;
    });

    [Fact]
    public async Task ThePage_ShowsEachPlaybook_WithWhoSavedIt_AndAnAnalystKeepsThem()
    {
        var tag = Guid.NewGuid().ToString("N")[..6];
        await Saved("Sweep " + tag, ActionKind.IocSweep, Robin, "Pull the indicators", "Sweep proxy logs");

        await using var r = await RenderAsync<PlaybooksPage>(app, "/playbooks", @as: Robin);
        await Set(r, "_words", tag, "Refilter");
        var html = WebUtility.HtmlDecode(await r.Html());
        html.Should().Contain("<h1>Playbooks</h1>").And.Contain(" New playbook</button>")
            .And.Contain("Sweep " + tag).And.Contain("Saved by Robin").And.Contain("<li>Sweep proxy logs</li>").And.Contain("Not used yet")
            .And.Contain("aria-label=\"Retire Sweep " + tag + "\"").And.Contain("aria-label=\"Delete Sweep " + tag + "\"", "Robin made it")
            .And.NotContain("aria-label=\"Move Sweep " + tag, "moving is on the whole list, not a filtered one");

        // A new one, from the page: its steps one a line, blank lines dropped.
        await r.Click(p => { p.New(); return Task.CompletedTask; });
        var form = await r.Html();
        form.Should().Contain("id=\"pb-f-steps\" class=\"form-control form-control-sm tide-autogrow tide-grow-3\"")
            .And.Contain("<div data-submit-scope>", "Ctrl+Enter in a box presses the form's Save")
            .And.Contain("<button class=\"btn btn-primary btn-sm\" data-submit>Save</button>");
        r.Component.Form!.Name = "Phishing triage " + tag;
        r.Component.Form.Kind = ActionKind.Block;
        await Set(r, "_steps", "Pull the sender\n\n  Block the domain  \n");
        await r.Click(p => p.Save());
        Toasts(r).Select(t => t.Message).Should().Contain("Phishing triage " + tag + " saved for the team.");
        r.Component.Form.Should().BeNull();
        var made = r.Component.Rows!.Single(x => x.Playbook.Name == "Phishing triage " + tag).Playbook;
        made.Steps.Select(s => s.Text).Should().Equal("Pull the sender", "Block the domain");

        // An edit begun before a colleague's is refused, and says why.
        await r.Click(p => { p.Edit(made); return Task.CompletedTask; });
        await using (var scope = SignedIn(app, SignIn.Analyst))
        {
            var svc = scope.ServiceProvider.GetRequiredService<PlaybookService>();
            var theirs = PlaybookForm.From((await svc.ListAsync()).Single(p => p.Id == made.Id));
            theirs.Steps.Add("Tell the SOC");
            await svc.SaveAsync(theirs);
        }
        r.Component.Form!.Name = "Phishing " + tag;
        await r.Click(p => p.Save());
        Toasts(r).Select(t => t.Message).Should().Contain("Someone else changed this playbook since you opened it. Reload to see their changes.");
        r.Component.Form.Should().NotBeNull("what's typed is kept");

        await r.Click(p => p.Retire(made, true));
        Toasts(r).Select(t => t.Message).Should().Contain($"Phishing triage {tag} retired. It's no longer offered on actions.");
        r.Component.Rows.Should().NotContain(x => x.Playbook.Id == made.Id, "retired ones are hidden until Show retired");
    }

    [Fact]
    public async Task AViewer_ReadsThePlaybooks_WithNothingToChangeThem()
    {
        var tag = Guid.NewGuid().ToString("N")[..6];
        await Saved("Viewer's read " + tag, null);
        await using var r = await RenderAsync<PlaybooksPage>(app, "/playbooks", @as: Viewer);
        var html = WebUtility.HtmlDecode(await r.Html());
        html.Should().Contain("Viewer's read " + tag).And.Contain("Any kind")
            .And.NotContain("New playbook</button>").And.NotContain("aria-label=\"Edit ").And.NotContain("aria-label=\"Retire ").And.NotContain("aria-label=\"Delete ");
    }

    [Fact]
    public async Task TheFilters_AKindAndTheWords_AndPagesOf20_AndMovingOnTheWholeList()
    {
        var tag = Guid.NewGuid().ToString("N")[..6];
        for (var i = 0; i < 22; i++) await Saved($"Paged {tag} {i:00}", i < 15 ? ActionKind.Block : ActionKind.PatchRemediate);
        await Saved($"Paged {tag} any", null);

        await using var r = await RenderAsync<PlaybooksPage>(app, "/playbooks?page=2");
        r.Component.Page.Should().Be(2, "the page is the address's");
        await Set(r, "_words", tag, "Refilter");
        r.Component.Page.Should().Be(1, "a new filter starts from the first page");
        r.Component.Shown.Should().HaveCount(23);
        r.Component.OnPage.Should().HaveCount(20);
        var html = WebUtility.HtmlDecode(await r.Html());
        html.Should().Contain("1–20 of 23 playbooks").And.Contain("aria-label=\"Pages of playbooks\"")
            .And.Contain(">Block</button>", "a pill per kind of action");

        await Set(r, "_kind", ActionKind.Block, "Refilter");
        r.Component.Shown.Should().HaveCount(16, "its own 15, and the one for any kind").And.OnlyContain(x => x.Playbook.Kind == null || x.Playbook.Kind == ActionKind.Block);
        await Set(r, "_kind", null, "ClearFilters");
        r.Component.Shown.Count.Should().BeGreaterThan(23, "the starters and the rest are back");

        // On the whole list, each offered one can move; the first can't go up.
        var first = r.Component.OnPage[0].Playbook;
        html = WebUtility.HtmlDecode(await r.Html());
        html.Should().MatchRegex($"aria-label=\"Move {System.Text.RegularExpressions.Regex.Escape(first.Name)} up\" disabled");
        var second = r.Component.OnPage[1].Playbook;
        await r.Click(p => p.Move(second, -1));
        r.Component.OnPage[0].Playbook.Id.Should().Be(second.Id);
        await r.Click(p => p.Move(second, 1));   // and back, for the other tests
        await r.Click(p => p.OnChangedElsewhere([]));
        Toasts(r).Select(t => t.Message).Should().Contain(m => m.EndsWith("updated the playbooks.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheOldAdminAddress_GoesToThePlaybooksPage()
    {
        var res = await app.CreateClient(new() { AllowAutoRedirect = false }).GetAsync("/admin/playbooks");
        res.StatusCode.Should().Be(HttpStatusCode.Redirect);
        new Uri(new Uri("http://localhost/"), res.Headers.Location!).AbsolutePath.Should().Be("/playbooks");
        (await app.CreateClient().GetStringAsync("/admin")).Should().NotContain("href=\"admin/playbooks\"", "Administration has no Playbooks tab now");
    }

    [Fact]
    public async Task BoardRules_ShowAViewerEachRule_ItsConditionsColourAndFilter_ReadOnly()
    {
        var tag = Guid.NewGuid().ToString("N")[..6];
        Guid team;
        await using (var scope = SignedIn(app, SignIn.Analyst))
        {
            team = (await scope.ServiceProvider.GetRequiredService<TeamService>().ListAsync())[0].Id;
            var rules = scope.ServiceProvider.GetRequiredService<CardRuleService>();
            await rules.SaveAsync(new ActionCardRule { Name = "Late " + tag, Colour = CardColour.Red, PriorityAtLeast = Priority.P2, Sla = CardRuleSla.Overdue });
            await rules.SaveAsync(new ActionCardRule { Name = "Ours " + tag, Colour = CardColour.Teal, TeamId = team, ShowAsFilter = false });
            await rules.SaveAsync(new ActionCardRule { Name = "Off " + tag, Unassigned = true, IsActive = false });
        }

        await using (var r = await RenderAsync<BoardRulesPage>(app, "/board-rules", @as: Viewer))
        {
            var html = WebUtility.HtmlDecode(await r.Html());
            html.Should().Contain("<h1>Board rules</h1>")
                .And.Contain("<span class=\"tide-rule-label tide-rule-red\">Late " + tag + "</span>")
                .And.Contain("P2 or higher · overdue").And.Contain(">Red</td>").And.Contain(">Teal</td>")
                .And.NotContain("Off " + tag, "a rule turned off marks nothing")
                .And.NotContain("Edit card rules", "a viewer reads them; administrators edit them");
            var ours = r.Component.Rules!.Single(x => x.Name == "Ours " + tag);
            ours.ShowAsFilter.Should().BeFalse();
            html.Should().MatchRegex($"Ours {tag}</span></td>\\s*<td class=\"small\">[^<]+</td>\\s*<td class=\"small\">Teal</td>\\s*<td class=\"small\">No</td>");
        }

        await using var admin = await RenderAsync<BoardRulesPage>(app, "/board-rules");
        (await admin.Html()).Should().Contain("href=\"admin/card-rules\"");
        (await app.CreateClient().GetStringAsync("/actions")).Should().Contain("href=\"board-rules\"", "the Actions page links to them");
    }

    [Fact]
    public async Task TheNewActionPicker_AndTheChecklist_OfferAnAnalystsPlaybook_AndNotARetiredOne()
    {
        var tag = Guid.NewGuid().ToString("N")[..6];
        var mine = await Saved("Analyst's sweep " + tag, ActionKind.IocSweep, Robin, "Pull the indicators", "Sweep DNS logs");
        var old = await Saved("Retired sweep " + tag, ActionKind.IocSweep);
        await using (var scope = SignedIn(app, Robin)) await scope.ServiceProvider.GetRequiredService<PlaybookService>().SetRetiredAsync(old, true);

        IReadOnlyList<Team> teams;
        int number;
        await using (var scope = SignedIn(app, SignIn.Analyst))
        {
            teams = await scope.ServiceProvider.GetRequiredService<TeamService>().ListAsync();
            number = await scope.ServiceProvider.GetRequiredService<ActionService>().CreateAsync(new ActionForm { Title = "Sweep " + tag, Kind = ActionKind.IocSweep, TeamId = teams[0].Id });
        }

        var form = new ActionForm { Title = "New", Kind = ActionKind.IocSweep, TeamId = teams[0].Id, PlaybookId = mine };
        await using (var editor = await RenderAsync<ActionEditor>(app, "/actions/new", new() { ["Form"] = form, ["Teams"] = teams, ["IsNew"] = true }))
        {
            var html = WebUtility.HtmlDecode(await editor.Html());
            html.Should().Contain("id=\"a-playbook\"").And.Contain("Analyst's sweep " + tag + " (2 steps)").And.Contain("<li>Sweep DNS logs</li>")
                .And.NotContain("Retired sweep " + tag);
        }

        ActionItem action;
        await using (var scope = SignedIn(app, SignIn.Analyst))
            action = (await scope.ServiceProvider.GetRequiredService<ActionService>().GetAsync(number))!.Action;
        await using var checklist = await RenderAsync<ActionChecklist>(app, "/actions/" + number,
            new() { ["ActionId"] = action.Id, ["Kind"] = ActionKind.IocSweep, ["CanWork"] = true }, Robin);
        var list = WebUtility.HtmlDecode(await checklist.Html());
        list.Should().Contain("Apply a playbook…").And.Contain("Analyst's sweep " + tag).And.NotContain("Retired sweep " + tag);
        await Set(checklist, "_playbookId", (Guid?)mine);
        await checklist.Click(c => (Task)typeof(ActionChecklist).GetMethod("Apply", Private)!.Invoke(c, null)!);
        Toasts(checklist).Select(t => t.Message).Should().Contain("2 steps added.");
        await using (var scope = SignedIn(app, SignIn.Analyst))
            (await scope.ServiceProvider.GetRequiredService<ActionService>().ChecklistAsync(action.Id)).Select(i => i.Text).Should().Equal("Pull the indicators", "Sweep DNS logs");
    }
}
