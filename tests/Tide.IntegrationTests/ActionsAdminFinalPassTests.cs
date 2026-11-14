using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Tide.Application.Actions;
using Tide.Application.Articles;
using Tide.Application.Briefs;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Web.Components.Pages;

namespace Tide.IntegrationTests;

/// <summary>
/// Final bug pass, actions and administration: an action is given only to a team member who can work actions; a closed
/// action takes no new query or indicator; a sweep or hunt says "no hits" only with a query run on record, whichever way
/// it's closed; team names are their own; a create that lost a number to another server's is numbered again, and nothing
/// else is retried; a title change says what it was and became; a row carries its article's marking; the actions
/// address takes the names a link uses for a view; and Add article says what it offers.
/// </summary>
public class ActionsAdminFinalPassTests : IDisposable
{
    private readonly TestDb _db = new();

    public ActionsAdminFinalPassTests() => _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };

    public void Dispose() => _db.Dispose();

    private const string Reader = "dev:reader", Viewer = "dev:viewer", Owner = "dev:owner";

    // People as TIDE last saw them sign in, with their roles.
    private async Task SignedIn(params (string Sid, string Roles)[] people)
    {
        await using var ctx = _db.Context();
        foreach (var (sid, roles) in people)
            ctx.Users.Add(new AppUser { Sid = sid, DisplayName = sid, RolesCsv = roles, LastSeenUtc = DateTimeOffset.UtcNow });
        await ctx.SaveChangesAsync();
    }

    // A team with a Reader, a Viewer and an action owner on it.
    private async Task<Guid> MixedTeam()
    {
        await SignedIn((Reader, "Reader"), (Viewer, "Viewer"), (Owner, "ActionOwner"));
        var team = await _db.Teams().AddAsync("Cyber Defense ops", null, null);
        foreach (var (id, who) in new[] { (Reader, "Rae Reader"), (Viewer, "Vic Viewer"), (Owner, "Owen Owner") })
            await _db.Teams().AddMemberAsync(team, id, who);
        return team;
    }

    private async Task GiveDirectly(int number, string id, string name)
    {
        await using var ctx = _db.PlainContext();
        await ctx.Actions.Where(a => a.Number == number)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.AssigneeId, id).SetProperty(a => a.AssigneeName, name));
    }

    // --- who an action is given to ----------------------------------------------------------------------------------

    [Fact]
    public async Task AReaderOrViewerOnTheTeam_IsntGivenAnAction_OnCreate_Edit_OrAssign()
    {
        var team = await MixedTeam();
        var svc = _db.Actions();

        await svc.Invoking(s => s.CreateAsync(new ActionForm { Title = "Sweep", TeamId = team, AssigneeId = Reader }))
            .Should().ThrowAsync<ArgumentException>().WithMessage("Rae Reader can't work actions*");
        var number = await svc.CreateAsync(new ActionForm { Title = "Sweep", TeamId = team, AssigneeId = Owner });
        var a = (await svc.GetAsync(number))!.Action;
        a.AssigneeName.Should().Be("Owen Owner");

        var form = ActionForm.From(a);
        form.AssigneeId = Viewer;
        await svc.Invoking(s => s.SaveAsync(form)).Should().ThrowAsync<ArgumentException>().WithMessage("Vic Viewer can't work actions*");
        await svc.Invoking(s => s.AssignAsync(a.Id, Reader, null)).Should().ThrowAsync<ArgumentException>().WithMessage("Rae Reader can't work actions*");
        (await svc.GetAsync(number))!.Action.AssigneeId.Should().Be(Owner, "nothing was saved");
    }

    [Fact]
    public async Task AHolderFromBefore_WhoCantWorkActions_KeepsIt_UntilItChangesHands()
    {
        var team = await MixedTeam();
        var svc = _db.Actions();
        var number = await svc.CreateAsync(new ActionForm { Title = "Patch", TeamId = team });
        await GiveDirectly(number, Reader, "Rae Reader");

        var form = ActionForm.From((await svc.GetAsync(number))!.Action);
        form.TicketRef = "CHG-1";
        await svc.SaveAsync(form);
        (await svc.GetAsync(number))!.Action.AssigneeId.Should().Be(Reader, "existing holders aren't rewritten");
    }

    [Fact]
    public async Task AHandOver_LetsGoOfAHolder_WhoCantWorkActionsOnTheReceivingTeam()
    {
        var from = await MixedTeam();
        var to = await _db.Teams().AddAsync("Receiving team", null, null);
        await _db.Teams().AddMemberAsync(to, Viewer, "Vic Viewer");
        var svc = _db.Actions();
        var number = await svc.CreateAsync(new ActionForm { Title = "Block", TeamId = from });
        await GiveDirectly(number, Viewer, "Vic Viewer");

        await _db.Handoffs().HandOffAsync((await svc.GetAsync(number))!.Action.Id, to, "Theirs");

        var a = (await svc.GetAsync(number))!.Action;
        (a.TeamId, a.AssigneeId).Should().Be((to, (string?)null));
        a.Events.Should().Contain(e => e.Text.Contains("Vic Viewer is no longer its assignee", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheAssigneePickers_Teams_LeaveOutMembersWhoCantWorkActions_ButTheyStayMembers()
    {
        var team = await MixedTeam();
        await _db.Teams().AddMemberAsync(team, "dev:never-signed-in", "Nia New");

        var all = (await _db.Teams().ListAsync()).Single(t => t.Id == team).Members.Select(m => m.UserId);
        all.Should().BeEquivalentTo([Reader, Viewer, Owner, "dev:never-signed-in"]);
        var holders = (await _db.Teams().ListAsync(holdersOnly: true)).Single(t => t.Id == team).Members.Select(m => m.UserId);
        holders.Should().BeEquivalentTo([Owner, "dev:never-signed-in"], "a Reader and a Viewer read; they don't work actions");
    }

    // --- a closed action takes nothing new ------------------------------------------------------------------------

    [Fact]
    public async Task AClosedAction_TakesNoHandWrittenQuery_NoGeneratedOne_AndNoIndicators()
    {
        var team = await _db.Teams().AddAsync("Hunt team", null, null);
        var svc = _db.Actions();
        var number = await svc.CreateAsync(new ActionForm { Title = "Hunt", Kind = ActionKind.ThreatHunt, TeamId = team });
        var id = (await svc.GetAsync(number))!.Action.Id;
        await svc.AddIndicatorsAsync(id, [new IndicatorForm { Type = IndicatorType.IpAddress, Value = "203.0.113.9" }]);
        await svc.SetStatusAsync(id, ActionStatus.Done, ActionOutcome.HitsEscalated, "Found it");

        await svc.Invoking(s => s.AddHandWrittenHuntAsync(id, "kql", "DeviceProcessEvents")).Should().ThrowAsync<ActionClosedException>();
        await svc.Invoking(s => s.GenerateHuntAsync(id, "kql")).Should().ThrowAsync<ActionClosedException>();
        await svc.Invoking(s => s.AddIndicatorsAsync(id, [new IndicatorForm { Type = IndicatorType.IpAddress, Value = "203.0.113.10" }]))
            .Should().ThrowAsync<ActionClosedException>();

        var a = (await svc.GetAsync(number))!.Action;
        a.Hunts.Should().BeEmpty();
        a.Indicators.Should().ContainSingle();
    }

    // --- "no hits" only with a query run ----------------------------------------------------------------------------

    [Fact]
    public async Task AHunt_ClosesAsNoHits_OnlyWithAQueryRun_WhicheverWayItsClosed()
    {
        var team = await _db.Teams().AddAsync("Hunt team", null, null);
        var svc = _db.Actions();
        var number = await svc.CreateAsync(new ActionForm { Title = "Hunt", Kind = ActionKind.ThreatHunt, TeamId = team });
        var id = (await svc.GetAsync(number))!.Action.Id;

        await svc.Invoking(s => s.SetStatusAsync(id, ActionStatus.Done, ActionOutcome.NoHits, null))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("No query on this action has been kept and run*");
        await svc.Invoking(s => s.MoveAsync(id, ActionStatus.Open, ActionStatus.Done, ActionOutcome.NoHits))
            .Should().ThrowAsync<InvalidOperationException>();
        await svc.Invoking(s => s.RecordUpdateAsync(new TeamUpdate { ActionId = id, From = ActionStatus.Open, Status = ActionStatus.Done, Outcome = ActionOutcome.NoHits }))
            .Should().ThrowAsync<InvalidOperationException>();

        var hunt = await svc.AddHandWrittenHuntAsync(id, "kql", "DeviceProcessEvents | where FileName == \"sh\"");
        await svc.Invoking(s => s.SetStatusAsync(id, ActionStatus.Done, ActionOutcome.NoHits, null))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*hasn't been run*");
        (await svc.GetAsync(number))!.Action.Status.Should().Be(ActionStatus.Open, "nothing closed it");

        await svc.RecordHuntResultAsync(hunt.Id, HuntResult.NoHits, null, "30 days", null);
        await svc.SetStatusAsync(id, ActionStatus.Done, ActionOutcome.NoHits, "30 days of logs");
        (await svc.GetAsync(number))!.Action.Outcome.Should().Be(ActionOutcome.NoHits);
    }

    [Fact]
    public async Task AClosedHunt_IsntChangedToNoHits_WhenAQueryFoundHits()
    {
        var team = await _db.Teams().AddAsync("Hunt team", null, null);
        var svc = _db.Actions();
        var number = await svc.CreateAsync(new ActionForm { Title = "Hunt", Kind = ActionKind.IocSweep, TeamId = team });
        var id = (await svc.GetAsync(number))!.Action.Id;
        var hunt = await svc.AddHandWrittenHuntAsync(id, "kql", "DeviceNetworkEvents");
        await svc.RecordHuntResultAsync(hunt.Id, HuntResult.Hits, 3, null, null);
        await svc.SetStatusAsync(id, ActionStatus.Done, ActionOutcome.HitsEscalated, null);

        await svc.Invoking(s => s.ChangeOutcomeAsync(id, ActionStatus.Done, ActionOutcome.NoHits, null))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*found hits*");
    }

    [Fact]
    public async Task AnExternalTeamsSweep_ClosesAsNoHits_OnItsWord()
    {
        var team = await _db.Teams().AddExternalAsync("Managed SOC", "soc@example.org");
        var svc = _db.Actions();
        var number = await svc.CreateAsync(new ActionForm { Title = "Sweep at the SOC", Kind = ActionKind.IocSweep, TeamId = team });
        var id = (await svc.GetAsync(number))!.Action.Id;

        await svc.SetStatusAsync(id, ActionStatus.Done, ActionOutcome.NoHits, "Their SIEM, 30 days");
        (await svc.GetAsync(number))!.Action.Outcome.Should().Be(ActionOutcome.NoHits, "its queries run where that team works");
    }

    // --- team names -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ATeamName_AnotherTeamHas_IsRefused_WhateverItsCaseOrSpacing()
    {
        var teams = _db.Teams();
        var tiny = await teams.AddAsync("Tiny team", null, null);
        var other = await teams.AddAsync("Other team", null, null);

        await teams.Invoking(t => t.AddAsync("Tiny team", null, null)).Should().ThrowAsync<ArgumentException>()
            .WithMessage("There's already a team called “Tiny team”. Choose another name.");
        await teams.Invoking(t => t.AddAsync("  tiny   TEAM ", null, null)).Should().ThrowAsync<ArgumentException>();
        await teams.Invoking(t => t.AddExternalAsync("TINY TEAM", null)).Should().ThrowAsync<ArgumentException>();
        await teams.Invoking(t => t.UpdateAsync(other, "Tiny Team", null, null)).Should().ThrowAsync<ArgumentException>();

        await teams.UpdateAsync(tiny, "Tiny Team", null, null);   // its own name, in another case
        (await teams.ListAsync()).Select(t => t.Name).Should().Contain("Tiny Team").And.Contain("Other team");
    }

    // --- numbering ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ACreateThatLostItsNumber_IsNumberedAgain_AndAnyOtherFailureIsntRetried()
    {
        var team = await _db.Teams().AddAsync("Numbered team", null, null);
        var svc = _db.Actions();
        var failed = 0;
        _db.BeforeSave = c =>
        {
            if (!c.ChangeTracker.Entries<ActionItem>().Any(e => e.State == EntityState.Added) || failed++ > 0) return;
            throw new DbUpdateException("save", new SqliteException("SQLite Error 19: 'UNIQUE constraint failed: Actions.Number'.", 19));
        };
        var number = await svc.CreateAsync(new ActionForm { Title = "Raced", TeamId = team });
        (await svc.GetAsync(number)).Should().NotBeNull();
        failed.Should().Be(2, "it was saved on the second try");

        var tries = 0;
        _db.BeforeSave = c =>
        {
            if (!c.ChangeTracker.Entries<ActionItem>().Any(e => e.State == EntityState.Added)) return;
            tries++;
            throw new DbUpdateException("save", new SqliteException("SQLite Error 19: 'FOREIGN KEY constraint failed'.", 19));
        };
        await svc.Invoking(s => s.CreateAsync(new ActionForm { Title = "Broken", TeamId = team })).Should().ThrowAsync<DbUpdateException>();
        tries.Should().Be(1, "only a taken number is tried again");
    }

    [Theory]
    [InlineData("SQLite Error 19: 'UNIQUE constraint failed: Actions.Number'.", true)]
    [InlineData("Cannot insert duplicate key row in object 'dbo.Actions' with unique index 'IX_Actions_Number'. The duplicate key value is (7).", true)]
    [InlineData("SQLite Error 19: 'UNIQUE constraint failed: Users.Sid'.", false)]
    [InlineData("SQLite Error 19: 'FOREIGN KEY constraint failed'.", false)]
    public void OnlyTheActionNumbersIndex_ReadsAsANumberTaken(string message, bool taken) =>
        ActionService.NumberTaken(new DbUpdateException("save", new InvalidOperationException(message))).Should().Be(taken);

    [Fact]
    public async Task ManyCreatesAtOnce_EachTakeTheirOwnNumber()
    {
        using var db = new TestDb(connectionPerContext: true);
        db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };
        var team = await db.Teams().AddAsync("Busy team", null, null);
        var numbers = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => db.Actions().CreateAsync(new ActionForm { Title = $"At once {i}", TeamId = team })));
        numbers.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task CreatesAtOnce_ToATeamAtItsLimit_AreRefused_AllButTheOneThatFits()
    {
        using var db = new TestDb(connectionPerContext: true);
        db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };
        var team = await db.Teams().AddAsync("Limited team", null, null);
        await db.Teams().SetLimitAsync(team, 1, ActiveLimitMode.Refuse);

        var tries = await Task.WhenAll(Enumerable.Range(0, 3).Select(async i =>
        {
            try { await db.Actions().CreateAsync(new ActionForm { Title = $"At once {i}", TeamId = team }); return true; }
            catch (ActiveLimitException) { return false; }
        }));
        tries.Count(made => made).Should().Be(1, "each create's limit check sees the one saved before it");
    }

    // --- what the history and the rows say -------------------------------------------------------------------------

    [Fact]
    public async Task ATitleChange_SaysWhatItWas_AndWhatItIsNow()
    {
        var team = await _db.Teams().AddAsync("Titled team", null, null);
        var svc = _db.Actions();
        var number = await svc.CreateAsync(new ActionForm { Title = "Patch the edge", TeamId = team });
        var form = ActionForm.From((await svc.GetAsync(number))!.Action);
        form.Title = "Patch the edge VPN";
        await svc.SaveAsync(form);

        (await svc.GetAsync(number))!.Action.Events.Should().Contain(e => e.Text == "Changed title: “Patch the edge” → “Patch the edge VPN”.");
    }

    [Fact]
    public async Task ARow_CarriesTheMarkingOfTheArticleItWasRaisedFrom()
    {
        var team = await _db.Teams().AddAsync("Marked team", null, null);
        var red = await _db.Articles().CreateAsync("Partner breach", "Partner", null, TlpLevel.Red);
        var svc = _db.Actions();
        var fromRed = await svc.CreateAsync(new ActionForm { Title = "From RED", TeamId = team, ArticleId = red });
        var none = await svc.CreateAsync(new ActionForm { Title = "From nothing", TeamId = team });

        var rows = await svc.ListAsync(new ActionFilter(ActionView.Open));
        rows.Single(r => r.Number == fromRed).ArticleTlp.Should().Be(TlpLevel.Red);
        rows.Single(r => r.Number == none).ArticleTlp.Should().BeNull();
    }

    [Fact]
    public async Task SearchsGuide_LooksUpTheArticlesFirstCve_InOrder()
    {
        var article = await _db.Articles().CreateAsync("Two CVEs", "Vendor", null, TlpLevel.Clear);
        await using (var ctx = _db.Context())
        {
            ctx.ArticleCves.AddRange(new ArticleCve { ArticleId = article, CveId = "CVE-2026-9999" }, new ArticleCve { ArticleId = article, CveId = "CVE-2026-1111" });
            await ctx.SaveChangesAsync();
        }
        // TestDb refuses a First without an order, as EF only warns of one.
        (await _db.Articles().FirstLookupAsync(TlpLevel.Amber, 5)).Should().Be("CVE-2026-1111");
    }

    // --- the actions address and the brief's Add article ---------------------------------------------------------------

    [Theory]
    [InlineData("open", ActionView.Open)]
    [InlineData("all", ActionView.Open)]
    [InlineData("All-Open", ActionView.Open)]
    [InlineData("myteams", ActionView.MyTeams)]
    [InlineData("my-teams", ActionView.MyTeams)]
    [InlineData("recently-closed", ActionView.Closed)]
    [InlineData("OVERDUE", ActionView.Overdue)]
    [InlineData("everything", null)]
    [InlineData("2", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void TheActionsAddress_TakesTheNamesALinkUsesForAView(string? view, ActionView? shown) =>
        ActionsBoard.ViewFrom(view).Should().Be(shown);

    private static BriefCandidate Candidate(ArticleStatus status) =>
        new(Guid.NewGuid(), "A", Classification.Threat, Priority.P2, TlpLevel.Clear, status, "Ana", DateTimeOffset.UtcNow, null);

    [Fact]
    public void AddArticle_SaysItsReadyOnesAndDraftsApart_AndLeavesOutPresentedOnes()
    {
        BriefPage.AvailableLabel([]).Should().BeEmpty();
        BriefPage.AvailableLabel([Candidate(ArticleStatus.Presented)]).Should().BeEmpty();
        BriefPage.AvailableLabel([Candidate(ArticleStatus.Ready)]).Should().Be(" (1 ready)");
        BriefPage.AvailableLabel([Candidate(ArticleStatus.Draft), Candidate(ArticleStatus.Draft)]).Should().Be(" (2 drafts)");
        BriefPage.AvailableLabel([Candidate(ArticleStatus.Ready), Candidate(ArticleStatus.Draft), Candidate(ArticleStatus.Presented)])
            .Should().Be(" (1 ready, 1 draft)");
    }
}
