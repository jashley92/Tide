using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Tide.Application.Abstractions;
using Tide.Application.Actions;
using Tide.Application.Security;
using Tide.Application.Trends;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Infrastructure.Persistence;

namespace Tide.IntegrationTests;

/// <summary>
/// Deleting an action raised by mistake or twice: who may, what's refused, how it disappears from every read (the query
/// filter on the action and on what's only its), that its number isn't reused, the administrator's list and restore,
/// the audit trail, and the actions feed.
/// </summary>
public class DeleteActionTests : IDisposable
{
    private readonly TestDb _db = new();

    public DeleteActionTests() => _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };

    public void Dispose() => _db.Dispose();

    private ActionService Svc => _db.Actions();

    private async Task<(Guid Id, int Number)> Raise(Guid team, string title, DateOnly? due = null, Guid? parent = null, Guid? article = null)
    {
        var n = await Svc.CreateAsync(new ActionForm { Title = title, TeamId = team, DueDate = due ?? _db.Clock.Today, ParentId = parent, ArticleId = article });
        return ((await Svc.GetAsync(n))!.Action.Id, n);
    }

    private void As(params AppRole[] roles) => _db.User.Roles = roles.ToHashSet();

    [Fact]
    public async Task OnlyAnAnalystOrAdministratorWhoMayWorkIt_Deletes_WithAReason()
    {
        var team = await _db.Teams().AddAsync("Network Engineering", null, null);
        await _db.Teams().AddMemberAsync(team, "S-OWNER", "Owner");
        var (id, number) = await Raise(team, "Block the C2 domains");

        // A member of the owning team works it, but deleting is an analyst's or an administrator's.
        As(AppRole.ActionOwner);
        (_db.User.UserId, _db.User.DisplayName) = ("S-OWNER", "Owner");
        await Svc.Invoking(s => s.DeleteAsync(id, "A duplicate")).Should().ThrowAsync<ForbiddenException>();
        Svc.MayDelete.Should().BeFalse();
        As(AppRole.Reader);
        await Svc.Invoking(s => s.DeleteAsync(id, "A duplicate")).Should().ThrowAsync<ForbiddenException>();

        As(AppRole.Analyst);
        Svc.MayDelete.Should().BeTrue();
        await Svc.Invoking(s => s.DeleteAsync(id, "  ")).Should().ThrowAsync<ArgumentException>().WithMessage("Say why*");
        await Svc.Invoking(s => s.DeleteAsync(id, new string('x', ActionItem.MaxDeleteReason + 1))).Should().ThrowAsync<ArgumentException>();
        await Svc.DeleteAsync(id, "A duplicate of A-1");

        await using var c = _db.Context();
        var gone = await c.Actions.IgnoreQueryFilters([ActionItem.DeletedFilter]).SingleAsync(a => a.Id == id);
        (gone.DeleteReason, gone.DeletedById, gone.DeletedByName, gone.DeletedAtUtc is not null).Should().Be(("A duplicate of A-1", _db.User.UserId, _db.User.DisplayName, true));
        (await Svc.GetAsync(number)).Should().BeNull("a deleted action is gone from its page");
        await Svc.Invoking(s => s.DeleteAsync(id, "again")).Should().ThrowAsync<KeyNotFoundException>("it's deleted already");
    }

    [Fact]
    public async Task AParentWithOpenParts_IsRefused_UntilTheyreDeletedOrClosed()
    {
        var team = await _db.Teams().AddAsync("Security Operations", null, null);
        var other = await _db.Teams().AddAsync("Service Desk", null, null);
        var (parent, _) = await Raise(team, "Patch the VPN");
        var (open, _) = await Raise(other, "Patch the VPN: desk", parent: parent);
        var (closed, _) = await Raise(team, "Patch the VPN: ours", parent: parent);
        await Svc.SetStatusAsync(closed, ActionStatus.Cancelled, ActionOutcome.None, null);

        await Svc.Invoking(s => s.DeleteAsync(parent, "A mistake")).Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*1 open part: delete or close its parts first.");

        await Svc.DeleteAsync(open, "Raised twice");
        await Svc.DeleteAsync(parent, "A mistake");
        (await Svc.RowAsync(parent)).Should().BeNull();

        // Its closed part stays, its parent gone from under it: no parent number, no lineage, and nothing fails.
        var part = (await Svc.RowAsync(closed))!;
        (part.ParentId, part.ParentNumber).Should().Be((parent, (int?)null));
        var detail = (await Svc.GetAsync(part.Number))!;
        (detail.Parent, detail.Lineage).Should().Be((null, null));
    }

    [Fact]
    public async Task ADeletedAction_IsGoneFromTheListsBoardCountsQueuesSearchApiRemindersAndCommitments()
    {
        var team = await _db.Teams().AddAsync("Cyber Defense", null, "defense@example.org");
        var today = _db.Clock.Today;
        var article = await _db.Articles().CreateAsync("Edge VPN zero-day", "Vendor", null, TlpLevel.Clear);
        var (kept, keptNumber) = await Raise(team, "Kept: sweep for the loader", today, article: article);
        var (gone, goneNumber) = await Raise(team, "Gone: sweep for the loader", today, article: article);
        var (late, _) = await Raise(team, "Gone too: overdue", today.AddDays(-3));
        await Svc.AddCommentAsync(gone, "zebra-quartz in the proxy logs");
        await Svc.DeleteAsync(gone, "A duplicate of the kept one");
        await Svc.DeleteAsync(late, "Raised by mistake");

        (await Svc.ListAsync(new ActionFilter())).Select(r => r.Id).Should().Equal(kept);
        var page = await Svc.PageAsync(new ActionFilter());
        (page.Total, page.Rows.Single().Id).Should().Be((1, kept));
        (await Svc.BoardAsync(new ActionFilter())).Cards.Select(c => c.Id).Should().Equal(kept);
        (await Svc.ForArticleAsync(article)).Select(r => r.Id).Should().Equal(kept);
        (await Svc.LikelyDuplicatesAsync(article, ActionKind.IocSweep, team)).Select(r => r.Id).Should().Equal(kept);
        (await Svc.ListByIdAsync([kept, gone])).Select(r => r.Id).Should().Equal(kept);
        var load = (await Svc.WorkloadAsync()).Single();
        (load.Open, load.Overdue).Should().Be((1, 0), "the deleted overdue one isn't counted");
        (await Svc.OutstandingAsync(today, TlpLevel.Red)).Overdue.Should().Be(0);

        var card = await _db.TeamScorecard().BuildAsync();
        card.Teams.Single().Should().BeEquivalentTo(new { Parts = 1, OpenNow = 1, OverdueNow = 0, OriginallyDue = 1 });
        TeamScorecardService.ToCsv(card).Should().Contain("Cyber Defense,no,1,0,0,,,1,0,");

        var found = await _db.Search().SearchAsync("sweep for the loader");
        found.Actions.Items.Select(a => a.Number).Should().Equal(keptNumber);
        (await _db.Search().SearchAsync("zebra-quartz")).Actions.Items.Should().BeEmpty("its comments are gone with it");

        var api = _db.Api();
        (await api.ActionsAsync(ActionView.Open, null)).Select(a => a.Number).Should().Equal(keptNumber);
        (await api.ActionAsync(goneNumber, null)).Should().BeNull();

        (await _db.Reminders().RunAsync(ReminderKind.DueSoon)).Should().Be(1);
        _db.Email.Sent.Single().Html.Should().Contain("Kept: sweep").And.NotContain("Gone");
        (await _db.Reminders().RunAsync(ReminderKind.Overdue)).Should().Be(0, "the only overdue one is deleted");

        // What's only its is gone too, read on its own; an administrator's IgnoreQueryFilters still finds it.
        await using var c = _db.Context();
        (await c.ActionComments.AnyAsync(x => x.ActionId == gone)).Should().BeFalse();
        (await c.ActionEvents.AnyAsync(x => x.ActionId == gone)).Should().BeFalse();
        (await c.ActionComments.IgnoreQueryFilters([ActionItem.DeletedFilter]).AnyAsync(x => x.ActionId == gone)).Should().BeTrue("it's kept, to be restored");
        (await c.ActionEvents.AnyAsync(x => x.ActionId == kept)).Should().BeTrue();
        (await c.Actions.Include(a => a.Comments).Include(a => a.Events).SingleAsync(a => a.Id == kept)).Events.Should().NotBeEmpty();
    }

    [Fact]
    public async Task ItsNumber_IsNeverReused_AndWhatItRefersTo_IsStillHeld()
    {
        var team = await _db.Teams().AddAsync("Retiring desk", null, null);
        var article = await _db.Articles().CreateAsync("A draft with a deleted action", "Vendor", null, TlpLevel.Clear);
        var (id, number) = await Raise(team, "The last one", article: article);
        await Svc.DeleteAsync(id, "A mistake");

        var next = await Svc.CreateAsync(new ActionForm { Title = "The next one", TeamId = (await _db.Teams().AddAsync("Another", null, null)) });
        next.Should().Be(number + 1, "the deleted one's number stays its own");

        // The article and team are still held by the deleted action (it can come back), so they're kept, not deleted.
        await _db.Articles().Invoking(a => a.DeleteAsync(article)).Should().ThrowAsync<InvalidOperationException>().WithMessage("Actions were raised*");
        await _db.Teams().SetRetiredAsync(team, true);
        await using var c = _db.Context();
        (await c.Teams.SingleAsync(t => t.Id == team)).IsRetired.Should().BeTrue("retired, not deleted from under its action");
    }

    [Fact]
    public async Task AnAdministrator_ListsTheDeleted_AndRestoresOne_AsItWas()
    {
        var team = await _db.Teams().AddAsync("Cyber Defense", null, null);
        var (parent, parentNumber) = await Raise(team, "Parent");
        var (part, partNumber) = await Raise(team, "Part", parent: parent);
        await Svc.SetStatusAsync(part, ActionStatus.Done, ActionOutcome.NoHits, null);
        await Svc.DeleteAsync(part, "Raised twice");
        await Svc.DeleteAsync(parent, "A mistake");

        As(AppRole.Analyst);
        await Svc.Invoking(s => s.DeletedAsync()).Should().ThrowAsync<ForbiddenException>();
        await Svc.Invoking(s => s.RestoreAsync(parent)).Should().ThrowAsync<ForbiddenException>();

        As(AppRole.Admin);
        var deleted = await Svc.DeletedAsync();
        deleted.Select(d => (d.Number, d.Reason, d.ParentDeleted)).Should().Equal((parentNumber, "A mistake", false), (partNumber, "Raised twice", true));
        deleted[1].Should().BeEquivalentTo(new { Title = "Part", TeamName = "Cyber Defense", Status = ActionStatus.Done, DeletedByName = _db.User.DisplayName });

        await Svc.Invoking(s => s.RestoreAsync(part)).Should().ThrowAsync<InvalidOperationException>().WithMessage($"*restore A-{parentNumber} first.");
        (await Svc.RestoreAsync(parent)).Should().Be(parentNumber);
        await Svc.RestoreAsync(part);

        (await Svc.DeletedAsync()).Should().BeEmpty();
        var back = (await Svc.GetAsync(partNumber))!;
        (back.Action.Status, back.Action.Outcome, back.Action.DeletedAtUtc is not null, back.Action.DeleteReason).Should().Be((ActionStatus.Done, ActionOutcome.NoHits, false, (string?)null));
        back.Parent!.Number.Should().Be(parentNumber);
        back.Action.Events.Select(e => e.Text).Should().Contain(t => t.StartsWith("Deleted it: Raised twice"))
            .And.Contain(t => t.StartsWith("Restored it (deleted ") && t.EndsWith(": Raised twice)."), "its history keeps both");
    }

    [Fact]
    public async Task DeletingAndRestoring_AreAudited_AndHeardOnTheActionsFeed()
    {
        var team = await _db.Teams().AddAsync("Cyber Defense", null, null);
        var (id, number) = await Raise(team, "Audited");
        var heard = new List<string>();
        _db.Feed.Changed += c => { lock (heard) heard.Add(c.Topic); };

        await Svc.DeleteAsync(id, "A duplicate");
        heard.Should().Contain(ChangeFeed.Actions).And.Contain(ChangeFeed.Action(id));
        heard.Clear();
        await Svc.RestoreAsync(id);
        heard.Should().Contain(ChangeFeed.Actions);

        await using var c = _db.Context();
        var entries = await c.AuditLog.Where(e => e.EntityType == nameof(ActionItem) && e.EntityKey == id.ToString() && e.Action == "Modified")
            .OrderBy(e => e.AtUtc).ToListAsync();
        entries.Should().Contain(e => e.ChangesJson!.Contains("\"DeleteReason\":[null,\"A duplicate\"]"))
            .And.Contain(e => e.ChangesJson!.Contains("\"DeleteReason\":[\"A duplicate\",null]"));
        entries.Should().OnlyContain(e => e.UserName == _db.User.DisplayName);

        // The audit log still names a deleted action, linking to the deleted list.
        await Svc.DeleteAsync(id, "Gone again");
        var rows = await new Tide.Application.History.AuditLogService(_db, _db.User, _db.Clock).PageAsync(new Tide.Application.History.AuditFilter());
        rows.Rows.Should().Contain(r => r.Record == $"A-{number} Audited (deleted)" && r.Href == ActionService.DeletedActionsHref);
    }

    [Fact]
    public void DeleteIsLastUnderMore_ForAnAnalystOrAdministratorWhoMayWorkIt()
    {
        var facts = new ActionFacts(ActionStatus.InProgress, ActionKind.Block, CanWork: true, Assigned: true, External: false, Mine: true, CanEditAll: true);
        ActionGuide.More(facts).Should().NotContain(ActionFlow.Delete);
        ActionGuide.More(facts with { CanDelete = true })[^1].Should().Be(ActionFlow.Delete);
        (ActionGuide.Label(ActionFlow.Delete), ActionGuide.Icon(ActionFlow.Delete)).Should().Be(("Delete…", "bi-trash"));
    }

    [Fact]
    public void TheModel_HasNoRequiredRelationshipEndingAtAFilteredRow()
    {
        // EF warns (10622) when a required relationship's principal is filtered and its dependent isn't: the dependent
        // could be read without its principal. Turned into an error here, a model with one would fail to build.
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).EnableServiceProviderCaching(false)
            .ConfigureWarnings(w => w.Throw(CoreEventId.PossibleIncorrectRequiredNavigationWithQueryFilterInteractionWarning)).Options;
        using var ctx = new AppDbContext(options);
        var filtered = ctx.Model.GetEntityTypes().Where(t => t.GetDeclaredQueryFilters().Count > 0).Select(t => t.ClrType.Name).ToList();
        filtered.Should().BeEquivalentTo(nameof(ActionItem), nameof(ActionComment), nameof(ActionCommentFile), nameof(ActionEvent), nameof(ActionFile),
            nameof(ActionChecklistItem), nameof(HuntQuery), nameof(ActionIndicator), nameof(OutreachMessage), nameof(DetectionGap));
    }
}
