using System.Reflection;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Abstractions;
using Tide.Application.Actions;
using Tide.Application.Articles;
using Tide.Application.Stack;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Web.Components.Pages;
using Tide.Web.Components.Shared;
using Tide.Web.Services;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>Owner decisions on comments and Our stack, against the services: a comment edit from an older copy is refused
/// and says who changed it, and a product's history names every article to the team edition's readers.</summary>
public class CommentsAndStackDecisionServiceTests : IDisposable
{
    private readonly TestDb _db = new();

    public CommentsAndStackDecisionServiceTests() => _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task EditComment_FromAnOlderVersion_IsRefused_AndSaysWhoChangedIt()
    {
        var svc = _db.Actions();
        var team = await _db.Teams().AddAsync("Cyber Defense", null, null);
        var action = (await svc.GetAsync(await svc.CreateAsync(new ActionForm { Title = "Block the domain", TeamId = team })))!.Action;
        await svc.AddCommentAsync(action.Id, "First go");
        var opened = (await svc.GetAsync(action.Number))!.Action.Comments.Single();

        _db.User.DisplayName = "Robin Admin";
        var robins = await svc.EditCommentAsync(opened.Id, "Robin's fix", opened.EditVersion);
        robins.Should().NotBe(opened.EditVersion, "an edit moves the version");
        (await svc.EditCommentAsync(opened.Id, "Robin's fix", robins)).Should().Be(robins, "saving the same text changes nothing");

        _db.User.DisplayName = "Test Analyst";
        await FluentActions.Invoking(() => svc.EditCommentAsync(opened.Id, "My second go", opened.EditVersion))
            .Should().ThrowAsync<StaleEditException>().WithMessage("Robin Admin changed this comment since you opened it.");
        var now = (await svc.GetAsync(action.Number))!.Action.Comments.Single();
        (now.Text, now.EditedByName, now.EditVersion).Should().Be(("Robin's fix", "Robin Admin", robins));

        await svc.EditCommentAsync(opened.Id, "My second go", now.EditVersion);
        (await svc.GetAsync(action.Number))!.Action.Comments.Single().Text.Should().Be("My second go", "redone on top of theirs, it saves");
    }

    [Fact]
    public async Task AProductsPage_NamesEveryArticleAndAction_ToTheTeamEditionsReaders_AndNoneToAReader()
    {
        var stack = _db.Stack();
        var vpn = await stack.SaveAsync(new StackForm { Name = "Contoso VPN Gateway" });
        var articles = _db.Articles();
        var open = await articles.CreateAsync("Contoso VPN Gateway patch out", "Vendor", null, TlpLevel.Amber);
        var red = await articles.CreateAsync("Contoso VPN Gateway breach at a partner", "Vendor", null, TlpLevel.Red);
        var team = await _db.Teams().AddAsync("Network", null, null);
        await _db.Actions().CreateAsync(new ActionForm { Title = "Patch the gateways", TeamId = team, ArticleId = open });
        await _db.Actions().CreateAsync(new ActionForm { Title = "Call the partner", TeamId = team, ArticleId = red });
        await _db.Actions().CreateAsync(new ActionForm { Title = "Check Contoso VPN Gateway logs", TeamId = team });

        // A Viewer reads a RED article everywhere in the app (a ceiling governs what leaves it): Our stack names it too.
        _db.User.Roles = new HashSet<AppRole> { AppRole.Viewer };
        var d = (await stack.GetAsync(vpn))!;
        d.Articles.Select(a => a.Id).Should().BeEquivalentTo([open, red]);
        d.Actions.Select(a => a.Title).Should().BeEquivalentTo(["Patch the gateways", "Call the partner", "Check Contoso VPN Gateway logs"],
            "an action is named wherever actions are shown, the one raised from the RED article too, as the Actions list names it");
        (await stack.ListAsync()).Single().Articles.Should().Be(2);
        (await stack.DetailsAsync([vpn]))[vpn].Articles.Should().HaveCount(2, "the article editor's card reads the same");

        // The present-mode card, on a screen Visitors may see, stays held to the team edition's ceiling.
        (await stack.CardAsync(vpn, TlpLevel.AmberStrict))!.Others.Select(a => a.Id).Should().Equal(open);

        _db.User.Roles = new HashSet<AppRole> { AppRole.Reader };
        await FluentActions.Invoking(() => stack.GetAsync(vpn)).Should().ThrowAsync<Tide.Application.Security.ForbiddenException>();
        _db.User.Roles = new HashSet<AppRole> { AppRole.Visitor };
        await FluentActions.Invoking(() => stack.GetAsync(vpn)).Should().ThrowAsync<Tide.Application.Security.ForbiddenException>();
    }

    [Fact]
    public void AnEditForm_IsUntouched_OnlyWhileItHoldsTheProductAsRead()
    {
        var p = new StackProduct { Name = "GitLab", Versions = "17.11", TeamId = Guid.NewGuid() };
        var f = StackForm.From(p);
        StackPage.Untouched(f, p).Should().BeTrue();
        f.Notes = "";
        StackPage.Untouched(f, p).Should().BeTrue("an emptied box that was empty is no change");
        f.Versions = "17.12";
        StackPage.Untouched(f, p).Should().BeFalse();
    }
}

/// <summary>The same decisions on the pages: a refused comment save, the discard question, focus after an edit closes,
/// and Our stack following a colleague's change.</summary>
public class CommentsAndStackDecisionPageTests(TideApp app) : IClassFixture<TideApp>
{
    private static readonly SignIn Colleague = new("dev:decisions-robin", "Robin", "Analyst", "Admin");

    private async Task<T> As<T>(Func<ActionService, TeamService, Task<T>> what, SignIn? who = null)
    {
        await using var scope = SignedIn(app, who ?? SignIn.Analyst);
        return await what(scope.ServiceProvider.GetRequiredService<ActionService>(), scope.ServiceProvider.GetRequiredService<TeamService>());
    }

    private async Task<ActionItem> NewAction(string title, params string[] comments)
    {
        var teamId = await As(async (_, teams) => (await teams.ListAsync()).First(t => t.Name == "Cyber Defense").Id);
        var number = await As((actions, _) => actions.CreateAsync(new ActionForm { Title = title, Kind = ActionKind.Block, TeamId = teamId }));
        var id = await As(async (actions, _) => (await actions.GetAsync(number))!.Action.Id);
        foreach (var c in comments) await As(async (actions, _) => { await actions.AddCommentAsync(id, c); return true; });
        return await Reread(number);
    }

    private Task<ActionItem> Reread(int number) => As(async (actions, _) => (await actions.GetAsync(number))!.Action);

    private static IReadOnlyList<ToastService.Toast> Toasts<T>(Rendered<T> r) => r.Scope.ServiceProvider.GetRequiredService<ToastService>().Toasts;

    private static ConfirmDialog Confirm(Rendered<ActionPage> r) =>
        (ConfirmDialog)typeof(ActionPage).GetField("_confirm", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(r.Component)!;

    private Task<Rendered<ActionPage>> Open(ActionItem a) =>
        RenderAsync<ActionPage>(app, "/actions/" + a.Number, new() { ["Number"] = (int?)a.Number });

    [Fact]
    public async Task ASaveOverAColleaguesChange_IsRefused_ShowsTheirs_AndKeepsWhatWasTyped()
    {
        var a = await NewAction("Block the decisions C2", "First go");
        var comment = a.Comments.Single();
        await using var r = await Open(a);
        await r.Click(async p => { await p.EditComment(comment); p.CommentEditDraft = "My second go"; });

        await As((actions, _) => actions.EditCommentAsync(comment.Id, "Robin's fix"), Colleague);
        await r.Click(p => p.SaveComment(comment));

        r.Component.EditingComment.Should().Be(comment.Id, "the edit stays open");
        r.Component.CommentEditDraft.Should().Be("My second go", "what was typed stays in the box");
        r.Component.CommentTheirs.Should().Be("Robin's fix");
        Toasts(r).Select(t => t.Message).Should().Contain("Robin changed this comment since you opened it.");
        await r.Renderer.Dispatcher.InvokeAsync(() => typeof(Microsoft.AspNetCore.Components.ComponentBase)
            .GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(r.Component, null));
        (await r.Html()).Should().Contain("Robin changed it to this. Your text is still in the box; saving puts it in place.");
        (await Reread(a.Number)).Comments.Single().Text.Should().Be("Robin's fix");

        await r.Click(p => p.SaveComment(comment));
        (await Reread(a.Number)).Comments.Single().Text.Should().Be("My second go", "a second save is on top of theirs");
        r.Component.EditingComment.Should().BeNull();
        r.Component.FocusEditOf.Should().Be(comment.Id, "focus goes back to the comment's Edit button");
    }

    [Fact]
    public async Task AColleaguesChangeHeard_WhileEditing_ShowsTheirs_AndTheSaveGoesOnTopOfIt()
    {
        var a = await NewAction("Block the decisions sender", "Their first version");
        var comment = a.Comments.Single();
        await using var r = await Open(a);
        await r.Click(async p => { await p.EditComment(comment); p.CommentEditDraft = "My edit"; });

        await As((actions, _) => actions.EditCommentAsync(comment.Id, "An administrator's fix"), Colleague);
        await r.Click(p => p.OnChangedElsewhere([new Change(ChangeFeed.Action(a.Id), Colleague.UserId, Colleague.Name, Guid.NewGuid())]));

        Toasts(r).Select(t => t.Message).Should().Contain("Robin changed the comment you're editing. Their text is shown above yours.");
        r.Component.CommentTheirs.Should().Be("An administrator's fix");
        await r.Click(p => p.SaveComment(comment));
        (await Reread(a.Number)).Comments.Single().Text.Should().Be("My edit", "theirs was shown, so the save goes ahead");
    }

    [Fact]
    public async Task EditingAnotherComment_AsksBeforeDroppingUnsavedChanges_AndOnlyThen()
    {
        var a = await NewAction("Block the decisions hash", "One", "Two");
        var (one, two) = (a.Comments[0], a.Comments[1]);
        await using var r = await Open(a);

        // Nothing typed: no question.
        await r.Click(p => p.EditComment(one));
        await r.Click(p => p.EditComment(two));
        r.Component.EditingComment.Should().Be(two.Id);

        await r.Click(async p => { await p.EditComment(one); p.CommentEditDraft = "One, changed"; });
        await r.Press(p => p.EditComment(two));
        var html = await r.HtmlOnce("Discard your changes to the other comment?");
        html.Should().Contain(">Keep editing</button>").And.Contain("Discard");

        await r.Renderer.Dispatcher.InvokeAsync(Confirm(r).Cancel);
        await Task.Delay(50);
        r.Component.EditingComment.Should().Be(one.Id, "Keep editing leaves the first edit open");
        r.Component.CommentEditDraft.Should().Be("One, changed");

        await r.Press(p => p.EditComment(two));
        await r.HtmlOnce("Discard your changes to the other comment?");
        await r.Renderer.Dispatcher.InvokeAsync(Confirm(r).Confirm);
        for (var i = 0; i < 50 && r.Component.EditingComment != two.Id; i++) await Task.Delay(20);
        r.Component.EditingComment.Should().Be(two.Id);
        r.Component.CommentEditDraft.Should().Be("Two");
    }

    [Fact]
    public async Task CancellingACommentEdit_SendsFocusToItsEditButton()
    {
        var a = await NewAction("Block the decisions URL", "Note");
        var comment = a.Comments.Single();
        await using var r = await Open(a);
        await r.Click(async p => { await p.EditComment(comment); p.CancelComment(comment); });

        r.Component.EditingComment.Should().BeNull();
        r.Component.FocusEditOf.Should().Be(comment.Id);
    }

    [Fact]
    public async Task OurStack_FollowsAColleaguesChange_AndAnEditFormKeepsWhatsTyped()
    {
        Guid id;
        await using (var scope = SignedIn(app, SignIn.Analyst))
            id = await scope.ServiceProvider.GetRequiredService<StackService>().SaveAsync(new StackForm { Name = "Live Decision Router", Versions = "1.0" });

        await using var list = await RenderAsync<StackPage>(app, "/stack");
        await using var page = await RenderAsync<StackPage>(app, $"/stack/{id}", new() { ["Id"] = id });
        await using var typed = await RenderAsync<StackPage>(app, $"/stack/{id}", new() { ["Id"] = id });
        await typed.Click(p => { p.Form!.Notes = "Typed, not saved"; return Task.CompletedTask; });

        await using (var robin = SignedIn(app, Colleague))
        {
            var svc = robin.ServiceProvider.GetRequiredService<StackService>();
            await svc.SaveAsync(new StackForm { Id = id, Name = "Live Decision Router", Versions = "1.1" });
            await svc.SaveAsync(new StackForm { Name = "Live Decision Firewall" });
        }

        // Each page hears both saves through its live feed, once its pause has passed. The waits read the page on its
        // own thread, so they never catch a reload halfway (the product re-read, the form not yet).
        await list.Until(p => p.List?.Any(r => r.Product.Name == "Live Decision Firewall") == true);
        (await list.Html()).Should().Contain("Live Decision Firewall");
        Toasts(list).Select(t => t.Message).Should().Contain("Robin updated Our stack.");

        await page.Until(p => p.Detail?.Product.Versions == "1.1");
        page.Component.Detail!.Product.Versions.Should().Be("1.1");
        page.Component.Form!.Versions.Should().Be("1.1", "an untouched form takes their change");
        Toasts(page).Select(t => t.Message).Should().Contain("Robin updated this product.");

        await typed.Until(p => p.Detail?.Product.Versions == "1.1");
        typed.Component.Form!.Notes.Should().Be("Typed, not saved", "what's typed stays");

        // Another product's change re-reads quietly: this one didn't change.
        var shown = Toasts(page).Select(t => t.Id).ToHashSet();
        await page.Click(p => p.OnChangedElsewhere([new Change(ChangeFeed.Stack, Colleague.UserId, Colleague.Name, Guid.NewGuid())]));
        Toasts(page).Where(t => !shown.Contains(t.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task AProductsPage_NamesARedArticle_ToAViewer()
    {
        Guid id;
        await using (var scope = SignedIn(app, SignIn.Analyst))
        {
            var stack = scope.ServiceProvider.GetRequiredService<StackService>();
            id = await stack.SaveAsync(new StackForm { Name = "Ceiling Decision Appliance" });
            var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
            await articles.CreateAsync("Ceiling Decision Appliance flaw", "Vendor", null, TlpLevel.Clear);
            await articles.CreateAsync("Ceiling Decision Appliance breach at a partner", "Vendor", null, TlpLevel.Red);
        }
        await using var page = await RenderAsync<StackPage>(app, $"/stack/{id}", new() { ["Id"] = id }, new SignIn("dev:viewer", "Robin", "Viewer"));
        var html = await page.Html();
        html.Should().Contain("Ceiling Decision Appliance flaw").And.Contain("Ceiling Decision Appliance breach at a partner")
            .And.NotContain(" above TLP:").And.Contain(">Articles <span class=\"tide-count\">(2)</span>");
    }

    [Fact]
    public async Task OurStack_AProductAdded_ClosesTheAddForm_SoBackDoesntOpenItAgain()
    {
        // The list and a product's page are one component: Add goes to the product, and Back returns to the same one.
        await using var page = await RenderAsync<StackPage>(app, "/stack");
        var adding = typeof(StackPage).GetField("_adding", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await page.Click(p =>
        {
            p.ToggleAdd();
            typeof(StackPage).GetField("_newName", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(p, $"Back Button Gateway {Guid.NewGuid():N}"[..30]);
            return Task.CompletedTask;
        });
        ((bool)adding.GetValue(page.Component)!).Should().BeTrue();

        await page.Click(p => p.Add());   // here the move to the product's page throws, as navigating outside a circuit does

        ((bool)adding.GetValue(page.Component)!).Should().BeFalse("the product was added: the form's work is done");
    }
}
