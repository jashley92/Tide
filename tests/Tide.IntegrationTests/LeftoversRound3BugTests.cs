using Microsoft.Extensions.Options;
using Tide.Application.Org;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Abstractions;
using Tide.Application.Actions;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Infrastructure.Security;
using Tide.TestSupport;
using Tide.Web.Components.Pages;
using Tide.Web.Security;
using Tide.Web.Services;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>Bug round 3, actions and leftovers: a comment typed while the last one is sent stays in the box; a comment
/// edit a colleague deleted or changed says so; and the user mirror keeps a person's real name when AD is slow.</summary>
public class LeftoversRound3BugTests(TideApp app) : IClassFixture<TideApp>
{
    private static readonly SignIn Colleague = new("dev:leftovers-robin", "Robin", "Analyst", "Admin");

    private async Task<T> As<T>(Func<ActionService, TeamService, Task<T>> what, SignIn? who = null)
    {
        await using var scope = SignedIn(app, who ?? SignIn.Analyst);
        return await what(scope.ServiceProvider.GetRequiredService<ActionService>(), scope.ServiceProvider.GetRequiredService<TeamService>());
    }

    private async Task<ActionItem> NewAction(string title)
    {
        var teamId = await As(async (_, teams) => (await teams.ListAsync()).First(t => t.Name == "Cyber Defense").Id);
        return await As(async (actions, _) =>
            (await actions.GetAsync(await actions.CreateAsync(new ActionForm { Title = title, Kind = ActionKind.Block, TeamId = teamId })))!.Action);
    }

    private Task As(Func<ActionService, TeamService, Task> what, SignIn? who = null) =>
        As(async (actions, teams) => { await what(actions, teams); return true; }, who);

    private Task<ActionItem> Reread(ActionItem a) => As(async (actions, _) => (await actions.GetAsync(a.Number))!.Action);

    private static IReadOnlyList<Change> ByRobin(ActionItem a) => [new Change(ChangeFeed.Action(a.Id), Colleague.UserId, Colleague.Name, Guid.NewGuid())];

    private static IReadOnlyList<ToastService.Toast> Toasts<T>(Rendered<T> r) => r.Scope.ServiceProvider.GetRequiredService<ToastService>().Toasts;

    // --- the new-comment box --------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Seen on two hosts", "Seen on two hosts", null)]
    [InlineData("Seen on two hosts\n\nAnd a third", "Seen on two hosts", "And a third")]
    [InlineData("Seen on two hosts   ", "Seen on two hosts", null)]
    [InlineData("Seen on three hosts", "Seen on two hosts", "Seen on three hosts")]
    [InlineData("Typed after a files-only post", "", "Typed after a files-only post")]
    [InlineData(null, "Seen on two hosts", null)]
    public void APost_ClearsOnlyWhatWasPosted_AndKeepsWhatWasTypedSince(string? box, string posted, string? left) =>
        ActionPage.Unposted(box, posted).Should().Be(left);

    [Fact]
    public async Task PostingAComment_EmptiesTheBox_AndShowsTheComment()
    {
        var a = await NewAction("Block the leftovers C2");
        await using var r = await RenderAsync<ActionPage>(app, "/actions/" + a.Number, new() { ["Number"] = (int?)a.Number });

        await r.Click(p => { p.CommentDraft = "Blocked at the proxy."; return p.Comment(); });

        r.Component.CommentDraft.Should().BeNull();
        (await Reread(a)).Comments.Should().ContainSingle().Which.Text.Should().Be("Blocked at the proxy.");
    }

    // --- a comment's edit -----------------------------------------------------------------------------------------

    [Fact]
    public async Task SavingACommentEdit_ClosesIt()
    {
        var a = await NewAction("Block the leftovers domain");
        await As((actions, _) => actions.AddCommentAsync(a.Id, "First go"));
        var comment = (await Reread(a)).Comments.Single();
        await using var r = await RenderAsync<ActionPage>(app, "/actions/" + a.Number, new() { ["Number"] = (int?)a.Number });

        await r.Click(async p => { await p.EditComment(comment); p.CommentEditDraft = "Second go"; await p.SaveComment(comment); });

        r.Component.EditingComment.Should().BeNull();
        (await Reread(a)).Comments.Single().Text.Should().Be("Second go");
    }

    [Fact]
    public async Task ACommentDeletedByAColleague_WhileItsEdited_ClosesTheEdit_AndSaysSo()
    {
        var a = await NewAction("Block the leftovers hash");
        await As((actions, _) => actions.AddCommentAsync(a.Id, "To be deleted"));
        var comment = (await Reread(a)).Comments.Single();
        await using var r = await RenderAsync<ActionPage>(app, "/actions/" + a.Number, new() { ["Number"] = (int?)a.Number });
        await r.Click(async p => { await p.EditComment(comment); p.CommentEditDraft = "An edit in progress"; });

        await As((actions, _) => actions.DeleteCommentAsync(comment.Id), Colleague);
        await r.Click(p => p.OnChangedElsewhere(ByRobin(a)));

        r.Component.EditingComment.Should().BeNull("the comment is gone, so the page no longer says it's being edited");
        r.Component.CommentEditDraft.Should().BeNull();
        Toasts(r).Select(t => t.Message).Should().Contain("Robin deleted the comment you were editing.");
    }

    [Fact]
    public async Task ACommentChangedByAColleague_WhileItsEdited_KeepsTheEdit_AndSaysSoOnce()
    {
        var a = await NewAction("Block the leftovers sender");
        await As((actions, _) => actions.AddCommentAsync(a.Id, "Their first version"));
        var comment = (await Reread(a)).Comments.Single();
        await using var r = await RenderAsync<ActionPage>(app, "/actions/" + a.Number, new() { ["Number"] = (int?)a.Number });
        await r.Click(async p => { await p.EditComment(comment); p.CommentEditDraft = "My edit"; });

        await As((actions, _) => actions.EditCommentAsync(comment.Id, "An administrator's fix"), Colleague);
        await r.Click(p => p.OnChangedElsewhere(ByRobin(a)));

        r.Component.EditingComment.Should().Be(comment.Id);
        r.Component.CommentEditDraft.Should().Be("My edit", "what's being typed stays");
        Toasts(r).Select(t => t.Message).Should().Contain("Robin changed the comment you're editing. Their text is shown above yours.");

        // Another change to the action, not the comment: the usual note.
        await As((actions, _) => actions.AddCommentAsync(a.Id, "Something else"), Colleague);
        await r.Click(p => p.OnChangedElsewhere(ByRobin(a)));
        Toasts(r).Select(t => t.Message).Should().Contain("Robin updated this action.");
    }

    // --- the user mirror ------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheMirrorLookup_SaysWhenTheNameIsOnlyTheBareUsername()
    {
        (await new WindowsNames(_ => throw new InvalidOperationException("the DC is down"), TimeSpan.FromSeconds(5))
            .ResolveForMirrorAsync("CONTOSO\\jdoe")).Should().Be(("jdoe", true));
        (await new WindowsNames(_ => new DirectoryPerson(null, "jdoe@contoso.com"), TimeSpan.FromSeconds(5))
            .ResolveForMirrorAsync("CONTOSO\\jdoe")).Should().Be(("jdoe", true), "AD found the person but no name for them");
        (await new WindowsNames(_ => new DirectoryPerson("Jordan Rivera", null), TimeSpan.FromSeconds(5))
            .ResolveForMirrorAsync("CONTOSO\\jdoe")).Should().Be(("Jordan Rivera", false));

        var notAsked = new WindowsNames(_ => throw new InvalidOperationException("not asked"), TimeSpan.FromSeconds(5));
        (await notAsked.ResolveForMirrorAsync("Dev Analyst")).Should().Be(("Dev Analyst", false), "a name that isn't DOMAIN\\sam is the name");
        (await notAsked.ResolveForMirrorAsync(null)).Should().Be(((string?)null, true));
    }

    [Fact]
    public async Task TheMirror_KeepsAKnownName_WhenTheLookupTimesOut_AndUsesTheUsernameOnAFirstVisit()
    {
        using var db = new TestDb();
        var services = new ServiceCollection();
        services.AddScoped(_ => db.Context());
        var mirror = new UserDirectory(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), TimeProvider.System, new OrgClock(Options.Create(new OrgOptions())));
        const string sid = "S-1-5-21-1-2-3-501";

        // A first visit with AD slow: the username is all there is.
        await mirror.TouchAsync(sid, "jdoe", null, null, "", nameIsStandIn: true);
        await using (var ctx = db.Context())
            (await ctx.Users.SingleAsync()).DisplayName.Should().Be("jdoe");

        // AD answers: the real name.
        await mirror.TouchAsync(sid, "Jordan Rivera", null, "jordan.rivera@contoso.com", "");
        // A later lookup times out: the bare username doesn't replace it.
        await mirror.TouchAsync(sid, "jdoe", null, null, "Analyst", nameIsStandIn: true);
        await using (var ctx = db.Context())
        {
            var row = await ctx.Users.SingleAsync();
            row.DisplayName.Should().Be("Jordan Rivera");
            row.Email.Should().Be("jordan.rivera@contoso.com");
            row.RolesCsv.Should().Be("Analyst", "the rest of the visit is still recorded");
        }
    }
}
