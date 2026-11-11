using System.Net;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Abstractions;
using Tide.Application.Actions;
using Tide.Application.Settings;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Web.Components.Pages;
using Tide.Web.Components.Shared;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>
/// The action page's layout: the header (badges, who has it, one primary button chosen by the action's state and kind,
/// everything else under More), the status badge that opens the status row with its rules, the Next line, the one
/// "What's happened" feed with its comment box, the tool drawer and the tool it opens on (or the address's ?tool=), a
/// read-only viewer's page, and a parent's and a part's.
/// </summary>
public class ActionPageLayoutTests(TideApp app) : IClassFixture<TideApp>
{
    private static readonly SignIn Owner = new("dev:layout-owner", "Olu Owner", "ActionOwner");
    private static readonly SignIn Viewer = new("dev:layout-viewer", "Viewer", "Viewer");

    private async Task<T> As<T>(Func<IServiceProvider, Task<T>> what, SignIn? who = null)
    {
        await using var scope = SignedIn(app, who ?? SignIn.Analyst);
        return await what(scope.ServiceProvider);
    }

    private Task As(Func<ActionService, Task> what, SignIn? who = null) =>
        As(async sp => { await what(sp.GetRequiredService<ActionService>()); return true; }, who);

    private Task<Guid> Team(string name, bool external = false) => As(sp => external
        ? sp.GetRequiredService<TeamService>().AddExternalAsync(name, "noc@provider.example", confirmOutside: true)
        : sp.GetRequiredService<TeamService>().AddAsync(name, null, null));

    private Task<int> Raise(Guid team, string title, ActionKind kind = ActionKind.Block, DateOnly? due = null, Guid? parent = null,
        Priority priority = Priority.P3, string? description = null) =>
        As(sp => sp.GetRequiredService<ActionService>().CreateAsync(new ActionForm
        {
            Title = title, TeamId = team, Kind = kind, DueDate = due, ParentId = parent, Priority = priority, Description = description,
        }));

    private Task<ActionDetail> Detail(int number) => As(async sp => (await sp.GetRequiredService<ActionService>().GetAsync(number))!);

    private Task Move(int number, ActionStatus to, ActionOutcome outcome = ActionOutcome.None) =>
        As(async actions => await actions.SetStatusAsync((await actions.GetAsync(number))!.Action.Id, to, outcome, null));

    private Task<Rendered<ActionPage>> Open(int number, SignIn? who = null, string? tool = null, string? @do = null) =>
        RenderAsync<ActionPage>(app, $"/actions/{number}{(tool is null ? "" : "?tool=" + tool)}{(@do is null ? "" : "?do=" + @do)}",
            new() { ["Number"] = (int?)number }, who);

    // Does what a click on the page does: runs it as the page's event, so the page draws again after it.
    private static Task Clicked(ActionPage page, Func<Task> what) => EventCallback.Factory.Create(page, what).InvokeAsync();

    private static Task Clicked(ActionPage page, Action what) => EventCallback.Factory.Create(page, what).InvokeAsync();

    private DateOnly Today => app.Services.GetRequiredService<Tide.Application.Org.OrgClock>().Today;

    // Flags an action escalated for the due date it has, as a step of the ladder does.
    private Task Escalate(int number) => As(async sp =>
    {
        await using var db = await sp.GetRequiredService<IAppDbContextFactory>().CreateAsync();
        var a = await db.Actions.SingleAsync(x => x.Number == number);
        (a.EscalatedForDue, a.EscalatedAtUtc) = (a.DueDate, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();
        return true;
    });

    private static string Section(string html, string start, string end)
    {
        var at = html.IndexOf(start, StringComparison.Ordinal);
        at.Should().BeGreaterThanOrEqualTo(0, $"the page has {start}");
        return html[at..html.IndexOf(end, at, StringComparison.Ordinal)];
    }

    private static string Header(string html) => Section(html, "<div class=\"tide-page-head tide-action-head\">", "class=\"tide-action-main\"");

    [Fact]
    public async Task TheHeader_HasTheTitle_OneLineOfBadges_WhoHasIt_AndOnePrimaryButton()
    {
        var team = await Team("Layout header team");
        var number = await Raise(team, "Layout patch the gateways", ActionKind.PatchRemediate, Today.AddDays(-2), priority: Priority.P2);
        await Escalate(number);

        await using var page = await Open(number);
        var head = WebUtility.HtmlDecode(Header(await page.Html()));
        head.Should().Contain($"<h1><span class=\"tide-action-num\">A-{number}</span> Layout patch the gateways</h1>");
        var badges = Section(head, "<div class=\"tide-action-badges\">", "<div class=\"tide-sub tide-action-who\">");
        badges.Should().Contain("class=\"tide-status-badge\" aria-expanded=\"false\" aria-controls=\"status-choices\"")
            .And.Contain("aria-label=\"Status: Open. Change it\"").And.Contain("title=\"Priority P2\"")
            .And.Contain("Due " + Ui.ShortDay(Today.AddDays(-2))).And.Contain("Overdue · 2 days").And.Contain(">Escalated</span>");
        badges.IndexOf("tide-status-badge", StringComparison.Ordinal).Should().BeLessThan(badges.IndexOf("Priority P2", StringComparison.Ordinal),
            "status, then priority, then the due date, then escalated");
        badges.IndexOf("Overdue", StringComparison.Ordinal).Should().BeLessThan(badges.IndexOf(">Escalated<", StringComparison.Ordinal));
        head.Should().Contain("<span>Layout header team</span> · <span>Unassigned</span>");

        Regex.Matches(head, "btn btn-primary").Should().HaveCount(1, "one primary button");
        head.Should().Contain("data-primary=\"TakeIt\"").And.Contain("Take it</button>");
        page.Component.Primary.Should().Be(ActionFlow.TakeIt, "open and nobody has it");
    }

    [Fact]
    public async Task AHandedOverAction_SaysFromWhom_OnTheLineUnderItsBadges()
    {
        var from = await Team("Layout handing team");
        var to = await Team("Layout receiving team");
        var number = await Raise(from, "Layout handed over");
        var id = (await Detail(number)).Action.Id;
        await As(sp => sp.GetRequiredService<HandoffService>().HandOffAsync(id, to, "The fix is theirs"));

        await using var page = await Open(number);
        var who = Section(WebUtility.HtmlDecode(await page.Html()), "<div class=\"tide-sub tide-action-who\">", "</div>");
        who.Should().Contain("<span>Layout receiving team</span>").And.Contain("handed from Layout handing team by Tester").And.Contain("The fix is theirs");
    }

    [Fact]
    public async Task TheMoreMenu_HoldsEveryOtherAction_AsAnAccessibleMenu_AndEachRunsItsFlow()
    {
        var team = await Team("Layout more team");
        var number = await Raise(team, "Layout block the domain");
        await using var page = await Open(number);
        page.Component.MoreItems.Select(i => i.Label).Should().Equal("Record an update", "Hand off…", "Split to teams…", "Split off a part", "Edit", "Change status…", "Delete…");

        var head = Header(await page.Html());
        head.Should().Contain("class=\"btn btn-outline-secondary tide-menubutton-btn\" aria-haspopup=\"menu\" aria-expanded=\"false\"").And.NotContain("role=\"menu\"");
        await page.Click(p => { p.More!.Toggle(); return Task.CompletedTask; });
        head = WebUtility.HtmlDecode(Header(await page.Html()));
        head.Should().Contain("aria-expanded=\"true\" aria-controls=\"menu-").And.Contain("role=\"menu\" aria-label=\"More\"");
        foreach (var label in new[] { "Record an update", "Hand off…", "Split to teams…", "Edit", "Change status…" })
            head.Should().Contain($"<button type=\"button\" role=\"menuitem\" tabindex=\"-1\" class=\"tide-filter-item\">").And.Contain($"<span>{label}</span>");
        head.Should().Contain($"role=\"menuitem\" tabindex=\"-1\" class=\"tide-filter-item\" href=\"actions/new?parent={(await Detail(number)).Action.Id}\"");

        await page.Click(p => p.More!.Choose(p.MoreItems.Single(i => i.Label == "Edit")));
        var html = await page.Html();
        html.Should().Contain("<h2>Edit</h2>", "Edit opens the edit form").And.NotContain("role=\"menu\"", "choosing closes the menu");

        await page.Press(p => p.More!.Choose(p.MoreItems.Single(i => i.Label == "Hand off…")));
        (await page.HtmlOnce("Hand it to")).Should().Contain($"Hand A-{number} off");
    }

    [Fact]
    public async Task Delete_UnderMore_AsksWhy_SaysWhatItsFor_AndRefusesAParentWithOpenParts()
    {
        var team = await Team("Layout delete team");
        var parent = await Raise(team, "Layout delete parent");
        await Raise(team, "Layout delete part", parent: (await Detail(parent)).Action.Id);
        var dup = await Raise(team, "Layout delete duplicate");

        // A team member who isn't an analyst works it, but isn't offered Delete.
        await As(async sp => { await sp.GetRequiredService<TeamService>().AddMemberAsync(team, "dev:layout-owner", "Olu Owner"); return true; });
        await using (var theirs = await Open(dup, Owner))
            theirs.Component.MoreItems.Select(i => i.Label).Should().Contain("Edit").And.NotContain("Delete…");

        await using (var page = await Open(parent))
        {
            await page.Press(p => p.More!.Choose(p.MoreItems.Single(i => i.Label == "Delete…")));
            var dialog = WebUtility.HtmlDecode(await page.HtmlOnce("Why"));
            dialog.Should().Contain($"Delete A-{parent}?").And.Contain("raised by mistake, or a duplicate")
                .And.Contain("<strong>Cancelled</strong> or <strong>Not applicable</strong> instead");
            await page.Click(async p =>
            {
                p.DeleteWhy = "A mistake";
                (await p.Delete()).Should().BeFalse("it has an open part");
                p.DeleteError.Should().Contain("delete or close its parts first");
            });
        }

        await using (var page = await Open(dup))
        {
            await page.Press(p => p.StartDelete());
            await page.Click(async p =>
            {
                p.DeleteWhy = "A duplicate of the parent";
                // Deleted, it goes back to the actions (the test's renderer throws where a circuit would navigate).
                var leaving = await p.Invoking(x => x.Delete()).Should().ThrowAsync<NavigationException>();
                leaving.Which.Location.Should().EndWith("actions");
                p.DeleteError.Should().BeNull();
            });
        }
        (await As(sp => sp.GetRequiredService<ActionService>().GetAsync(dup))).Should().BeNull();
        await using (var gone = await Open(dup))
            (await gone.Html()).Should().Contain("Action not found");

        // The administrator's list has it, with who, when and why, and restores it.
        await using var admin = await RenderAsync<Tide.Web.Components.Pages.Admin.DeletedActionsAdmin>(app, "/admin/deleted-actions");
        var row = admin.Component.Rows!.Single(r => r.Number == dup);
        (row.Reason, row.DeletedByName).Should().Be(("A duplicate of the parent", "Tester"));
        WebUtility.HtmlDecode(await admin.Html()).Should().Contain("<h1 class=\"tide-admin-title\">Deleted actions</h1>").And.Contain("A duplicate of the parent")
            .And.Contain($"aria-label=\"Restore A-{dup}\"");
        await As(a => a.RestoreAsync(row.Id));
        (await Detail(dup)).Action.Title.Should().Be("Layout delete duplicate");
    }

    [Fact]
    public async Task TakeIt_IsUnderMore_WhenSomeoneElseHasIt_AndThePrimaryIsTheStatusMoveThatFits()
    {
        var team = await Team("Layout take team");
        await As(async sp => { await sp.GetRequiredService<TeamService>().AddMemberAsync(team, Owner.UserId, Owner.Name); return true; });
        var number = await Raise(team, "Layout someone else's");
        await As(async actions => await actions.TakeAsync((await actions.GetAsync(number))!.Action.Id), Owner);
        await Move(number, ActionStatus.Open);   // taking it started it: back to Open, held by Olu

        await using var page = await Open(number);
        page.Component.Primary.Should().Be(ActionFlow.StartWork);
        (await page.Html()).Should().Contain("data-primary=\"StartWork\"").And.Contain("Start work</button>");
        page.Component.MoreItems.Select(i => i.Label).Should().StartWith(["Take it", "Record an update"]);

        await page.Click(p => p.RunPrimary());
        (await Detail(number)).Action.Status.Should().Be(ActionStatus.InProgress, "Start work moves it to In progress");

        await using var again = await Open(number);
        again.Component.Primary.Should().Be(ActionFlow.CloseWithOutcome);
        await again.Click(p => p.RunPrimary());
        (await again.Html()).Should().Contain("class=\"tide-status-prompt\"").And.Contain($"Move A-{number} to Done")
            .And.Contain("aria-expanded=\"true\" aria-controls=\"status-choices\"", "closing asks for the outcome in the status row, opened");
        (await Detail(number)).Action.Status.Should().Be(ActionStatus.InProgress, "nothing moves until the outcome is chosen");
    }

    [Fact]
    public async Task TheStatusBadge_OpensTheStatusRow_AMoveFoldsItAway_AndItsRulesHold()
    {
        var team = await Team("Layout status team");
        var number = await Raise(team, "Layout status parent");
        var id = (await Detail(number)).Action.Id;
        await Raise(team, "Layout status part", parent: id);

        await using var page = await Open(number);
        (await page.Html()).Should().NotContain("tide-status-seg", "the row is folded into the badge");
        var control = page.Component.StatusControl!;
        await page.Click(_ => control.ToggleChoices());
        var html = await page.Html();
        html.Should().Contain("aria-expanded=\"true\" aria-controls=\"status-choices\"").And.Contain("id=\"status-choices\"")
            .And.Contain("aria-pressed=\"true\" data-status=\"Open\"").And.Contain("data-status=\"Tracking\"");

        await page.Click(_ => control.Pick(ActionStatus.InProgress));
        (await Detail(number)).Action.Status.Should().Be(ActionStatus.InProgress);
        control.ChoicesShown.Should().BeFalse("a one-click move folds the row back into the badge");

        await page.Press(_ => control.Pick(ActionStatus.Done));
        (await page.HtmlOnce("with parts still open?")).Should().Contain(ActionPrompts.CloseAnyway(1), "a parent with open parts asks first");

        // More's Change status shows the same row.
        await using var other = await Open(await Raise(team, "Layout status other"));
        await other.Click(p => p.More!.Choose(p.MoreItems.Single(i => i.Label == "Change status…")));
        (await other.Html()).Should().Contain("tide-status-seg").And.Contain("id=\"status-choices\"");
    }

    [Fact]
    public async Task TheNextLine_NamesALatePart_WithLinksToChaseItAndRecordTheirReply()
    {
        var owner = await Team("Layout next owner");
        var branch = await Team("Layout Branch IT");
        var parent = await Raise(owner, "Layout next parent", due: Today.AddDays(10));
        var id = (await Detail(parent)).Action.Id;
        var late = await Raise(branch, "Layout next part", due: Today.AddDays(-3), parent: id);

        await using var page = await Open(parent);
        var html = WebUtility.HtmlDecode(await page.Html());
        var next = Section(html, "<div class=\"tide-next\"", "</div>");
        next.Should().Contain("data-next=\"PartLate\"").And.Contain($"Layout Branch IT is 3 days late on A-{late}.")
            .And.Contain($"<a href=\"actions/{late}\">Chase them</a>").And.Contain($"<a href=\"actions/{late}?do=recordupdate\">Record their reply</a>");
        html.IndexOf("class=\"tide-next\"", StringComparison.Ordinal).Should().BeLessThan(html.IndexOf("id=\"feed-h\"", StringComparison.Ordinal));
        page.Component.Primary.Should().Be(ActionFlow.RecordUpdate, "a parent with open parts records their updates");

        // The part's own page: late itself, and ?do=recordupdate runs Record an update once it's drawn.
        await using var part = await Open(late, @do: "recordupdate");
        part.Component.Next!.Kind.Should().Be(NextKind.Overdue);
        WebUtility.HtmlDecode(await part.Html()).Should().Contain($"It's 3 days late: it was due {Tide.Application.Content.Words.MonthDay(Today.AddDays(-3))}.");
        part.Component.Doing.Should().Be(ActionFlow.RecordUpdate);
        await part.Press(p => p.Run(ActionFlow.RecordUpdate));
        (await part.HtmlOnce("Their reply")).Should().Contain("Record an update from Layout Branch IT", "the flow ?do= names, through the one dispatcher");

        // A flow the page doesn't offer whoever is looking doesn't run, from the address or anywhere else.
        await using var viewer = await Open(late, Viewer, @do: "handoff");
        viewer.Component.Doing.Should().Be(ActionFlow.HandOff);
        await viewer.Press(p => p.Run(ActionFlow.HandOff));
        (await viewer.Html()).Should().NotContain("Hand it to");
    }

    [Fact]
    public async Task TheNextLine_SaysEscalatedBlockedOrOverdue_RunsItsLinks_AndIsntThereWhenNothingNeedsDoing()
    {
        var team = await Team("Layout next team");
        var escalated = await Raise(team, "Layout escalated", due: Today.AddDays(-1));
        await Escalate(escalated);
        var blocked = await Raise(team, "Layout blocked", due: Today.AddDays(5));
        await Move(blocked, ActionStatus.Blocked);
        var onTrack = await Raise(team, "Layout on track", due: Today.AddDays(5));

        await using (var page = await Open(escalated))
        {
            page.Component.Next!.Kind.Should().Be(NextKind.Escalated);
            (await page.Html()).Should().Contain("data-next=\"Escalated\"").And.Contain("Record an update</button>").And.Contain("Hand off</button>");
            await page.Press(p => p.Run(ActionFlow.HandOff));
            (await page.HtmlOnce("Hand it to")).Should().Contain($"Hand A-{escalated} off");
        }
        await using (var page = await Open(blocked))
        {
            page.Component.Next!.Kind.Should().Be(NextKind.Blocked);
            page.Component.Primary.Should().Be(ActionFlow.TakeIt);
            (await page.Html()).Should().Contain("It&#x27;s blocked.").And.Contain("Say what it&#x27;s waiting on</button>");
            await page.Click(p => Clicked(p, () => p.Run(ActionFlow.Edit)));
            (await page.Html()).Should().Contain("<h2>Edit</h2>");
        }
        await using (var page = await Open(onTrack))
        {
            page.Component.Next.Should().BeNull();
            (await page.Html()).Should().NotContain("class=\"tide-next\"");
        }
        await using (var page = await Open(escalated, Viewer))
            (await page.Html()).Should().NotContain("class=\"tide-next\"", "a viewer who can't work it isn't told what to do");
    }

    [Fact]
    public async Task AnOutreachAction_WritesItsEmailFirst_ThenRecordsTheReply_AndSaysWhenItsFollowUpHasPassed()
    {
        await As(async sp =>
        {
            await sp.GetRequiredService<OperationalSettingsService>().SaveAsync(new Dictionary<string, string?>
            {
                [OperationalSettings.OutreachFollowUp] = "true", [OperationalSettings.OutreachFollowUpDays] = "2",
            });
            return true;
        });
        var team = await Team("Layout outreach team");
        var number = await Raise(team, "Layout ask the vendor", ActionKind.Outreach, Today.AddDays(20));
        var id = (await Detail(number)).Action.Id;

        await using (var page = await Open(number))
        {
            page.Component.Primary.Should().Be(ActionFlow.WriteEmail);
            (await page.Html()).Should().Contain("data-tool=\"outreach\"", "an Outreach action's drawer opens on Outreach").And.Contain("Write the email</button>");
            await page.Click(p => p.RunPrimary());
            (await page.HtmlOnce("id=\"outreach-writer\"")).Should().Contain("id=\"outreach-writer\"", "the primary opens the writer");
        }

        await As(async sp =>
        {
            await sp.GetRequiredService<OutreachService>().MarkSentAsync(id, new OutreachSend
            {
                To = "psirt@vendor.example", Subject = "Are we affected?", Body = "Hello", Tlp = TlpLevel.Clear,
            });
            return true;
        });
        await using (var page = await Open(number))
        {
            page.Component.Primary.Should().Be(ActionFlow.RecordReply);
            page.Component.Next.Should().BeNull("its follow-up isn't due yet");
            await page.Press(p => p.RunPrimary());
            (await page.HtmlOnce("Record the reply from psirt@vendor.example")).Should().Contain("Close the action with this reply");
        }

        await As(async sp =>
        {
            await using var db = await sp.GetRequiredService<IAppDbContextFactory>().CreateAsync();
            var m = await db.OutreachMessages.SingleAsync(x => x.ActionId == id);
            m.SentAtUtc = DateTimeOffset.UtcNow.AddDays(-14);
            await db.SaveChangesAsync();
            return true;
        });
        await using (var page = await Open(number))
        {
            page.Component.Next!.Kind.Should().Be(NextKind.NoReply);
            (await page.Html()).Should().Contain("data-next=\"NoReply\"").And.Contain("No reply from psirt@vendor.example since ")
                .And.Contain("Record the reply</button>").And.Contain("Write a follow-up</button>");
        }
    }

    [Fact]
    public async Task TheFeed_MergesCommentsAndHistory_OldestFirst_FoldsTheEarlier_AndHasTheCommentBoxAtItsFoot()
    {
        var team = await Team("Layout feed team");
        var number = await Raise(team, "Layout feed action");
        var id = (await Detail(number)).Action.Id;
        await As(actions => actions.AddCommentAsync(id, "Layout first comment"));
        await Move(number, ActionStatus.InProgress);
        await As(actions => actions.AddCommentAsync(id, "Layout second comment"));

        await using (var page = await Open(number))
        {
            var html = WebUtility.HtmlDecode(await page.Html());
            var feed = Section(html, "<section class=\"tide-card tide-feed\"", "</section>");
            feed.Should().Contain("What's happened").And.NotContain("Show ", "nothing to fold yet");
            var first = feed.IndexOf("Layout first comment", StringComparison.Ordinal);
            var moved = feed.IndexOf("Status: Open → In progress.", StringComparison.Ordinal);
            var second = feed.IndexOf("Layout second comment", StringComparison.Ordinal);
            first.Should().BePositive();
            moved.Should().BeGreaterThan(first, "oldest first: the history line comes between the comments");
            second.Should().BeGreaterThan(moved);
            feed.Should().Contain("<span class=\"tide-avatar tide-feed-mark\" aria-hidden=\"true\" title=\"Tester\">T</span>", "a comment carries its author's initials")
                .And.Contain("<span class=\"tide-avatar tide-feed-mark tide-feed-sys\" aria-hidden=\"true\" title=\"From its history\">T</span>", "a history line TIDE's mark")
                .And.Contain("class=\"tide-feed-item tide-feed-line\"").And.Contain("class=\"tide-feed-item tide-comment\"");

            var box = feed[feed.IndexOf("<div id=\"comment-zone\"", StringComparison.Ordinal)..];
            box.Should().Contain("data-submit-scope").And.Contain("placeholder=\"Add a comment (Markdown). Ctrl+Enter to post\"")
                .And.Contain("data-submit").And.Contain(">Comment</button>").And.Contain("Record a team's update</button>")
                .And.Contain("Ctrl+Enter posts.");
            feed.IndexOf("id=\"comment-zone\"", StringComparison.Ordinal).Should().BeGreaterThan(second, "the box is at the feed's foot");

            var comment = (await Detail(number)).Action.Comments.First();
            await page.Click(p => p.EditComment(comment));
            await page.Click(p => Clicked(p, p.ShowAll));
            var editing = Section(await page.Html(), "<div data-submit-scope>", "Cancel</button>");
            editing.Should().Contain("id=\"comment-edit\"").And.Contain("class=\"btn btn-primary btn-sm\" data-submit");
        }

        for (var i = 0; i < 12; i++) await As(actions => actions.AddCommentAsync(id, $"Layout more {i}"));
        await using (var page = await Open(number))
        {
            var html = await page.Html();
            var all = (await Detail(number)).Action;
            var entries = all.Comments.Count + all.Events.Count;
            html.Should().Contain($"Show {entries - ActionFeed.Latest} earlier</button>").And.NotContain("Layout first comment").And.Contain("Layout more 11");
            await page.Click(p => Clicked(p, p.ShowAll));
            (await page.Html()).Should().Contain("Layout first comment").And.NotContain("earlier</button>");
        }
    }

    [Fact]
    public async Task TheDrawer_OpensOnTheKindsOwnTool_OrTheOneTheAddressAsksFor()
    {
        var team = await Team("Layout drawer team");
        var block = await Raise(team, "Layout drawer block");
        var sweep = await Raise(team, "Layout drawer sweep", ActionKind.IocSweep);
        var outreach = await Raise(team, "Layout drawer outreach", ActionKind.Outreach);

        await using (var page = await Open(block))
        {
            var html = await page.Html();
            html.Should().Contain("role=\"tablist\" aria-label=\"Tools\"").And.Contain("role=\"tabpanel\" id=\"tool-panel\" aria-labelledby=\"tool-panel-tab-checklist\"")
                .And.Contain("data-tool=\"checklist\"").And.NotContain("tool-panel-tab-hunt").And.NotContain("tool-panel-tab-outreach");
            page.Component.Drawer!.Tools.Should().Equal(ActionTool.Checklist);
        }
        await using (var page = await Open(sweep))
        {
            var html = await page.Html();
            html.Should().Contain("data-tool=\"hunt\"").And.Contain("id=\"tool-panel-tab-hunt\" class=\"tide-tabbar-item active\" aria-selected=\"true\"")
                .And.Contain("id=\"tool-panel-tab-checklist\" class=\"tide-tabbar-item \" aria-selected=\"false\"");
            html.Should().Contain("aria-label=\"Query language\"", "the hunt panel is drawn")
                .And.NotContain("Add checklist item", "the checklist isn't, until its tab shows");
        }
        await using (var page = await Open(outreach))
            (await page.Html()).Should().Contain("data-tool=\"outreach\"");

        await using (var asked = await Open(sweep, tool: "checklist"))
        {
            var html = await asked.Html();
            html.Should().Contain("data-tool=\"checklist\"", "?tool= opens the tool asked for")
                .And.NotContain("aria-label=\"Query language\"", "a tool's panel is drawn the first time its tab shows");
        }

        // A tab asks for its tool to go in the address, which shows it; its panel is drawn now, and kept.
        string? keyAsked = null;
        var detail = await Detail(sweep);
        await using (var drawer = await RenderAsync<ActionToolDrawer>(app, $"/actions/{sweep}", new()
        {
            ["Detail"] = detail, ["Asked"] = "checklist", ["Today"] = Today,
            ["ToolChanged"] = EventCallback.Factory.Create<string>(this, key => keyAsked = key),
        }))
        {
            await drawer.Click(d => EventCallback.Factory.Create(d, () => d.Select(ActionTool.Hunt)).InvokeAsync());
            keyAsked.Should().Be("hunt");
            drawer.Component.Tool.Should().Be(ActionTool.Checklist, "the address hasn't changed yet");
            (await drawer.Html()).Should().Contain("<h2>Hunt queries", "the hunt panel is drawn, hidden, ready to show")
                .And.NotContain("aria-label=\"Query language\"", "its language and window are on the tab row only while its tab shows");
        }
        await using (var missing = await Open(block, tool: "outreach"))
            (await missing.Html()).Should().Contain("data-tool=\"checklist\"", "a tool it hasn't got falls back to the kind's own");
    }

    [Fact]
    public async Task TheTools_AreInThePageAboveWhatsHappened_FirstForASweepOrAHunt_AndAfterTheDetailsForEveryOtherKind()
    {
        var team = await Team("Layout tools place team");
        const string drawer = "class=\"tide-card tide-drawer\"", main = "<div class=\"tide-action-main\">", rail = "<aside id=\"tide-rail\"";
        foreach (var kind in Enum.GetValues<ActionKind>())
        {
            var wide = kind is ActionKind.IocSweep or ActionKind.ThreatHunt;
            await using var page = await Open(await Raise(team, $"Layout tools place {kind}", kind, description: "Layout tools place details"));
            var html = await page.Html();
            int At(string text) => html.IndexOf(text, StringComparison.Ordinal);
            At(drawer).Should().BeGreaterThan(At(main)).And.BeLessThan(At(rail), $"a {kind}'s tools are in the page, not the rail")
                .And.BeLessThan(At("id=\"feed-h\""), "above what's happened");
            html.Should().NotContain("tide-action-side", "the page has one column; what was beside it is in the rail");
            if (wide)
            {
                At(drawer).Should().BeLessThan(At("Layout tools place details"), "above its details, so Copy is on the first screen");
                html.Should().Contain("data-tool=\"hunt\"", "opened on its hunt queries");
            }
            else
            {
                At(drawer).Should().BeGreaterThan(At("Layout tools place details"), $"a {kind}'s tools follow its details");
            }
        }
    }

    [Fact]
    public async Task AParentAndAPart_ShowTheirLineage_AndOnlyTheParentHasTheTimeline()
    {
        var owner = await Team("Layout lineage owner");
        var provider = await Team("Layout lineage provider", external: true);
        var parent = await Raise(owner, "Layout lineage parent", due: Today.AddDays(10));
        var id = (await Detail(parent)).Action.Id;
        var part = await Raise(provider, "Layout lineage part", due: Today.AddDays(5), parent: id);

        await using (var page = await Open(parent))
        {
            var html = await page.Html();
            html.Should().Contain("tide-lineage").And.Contain("id=\"tool-panel-tab-timeline\"").And.NotContain("tide-remtl", "drawn once its tab shows");
            html.IndexOf("tide-lineage", StringComparison.Ordinal).Should().BeLessThan(html.IndexOf("id=\"feed-h\"", StringComparison.Ordinal),
                "the lineage comes before what's happened");
            page.Component.MoreItems.Should().Contain(i => i.Label == "Split to teams…");
        }
        await using (var timeline = await Open(parent, tool: "timeline"))
            (await timeline.Html()).Should().Contain("data-tool=\"timeline\"").And.Contain("tide-remtl");
        await using (var page = await Open(part))
        {
            var html = await page.Html();
            html.Should().Contain($"Part of A-{parent}").And.Contain("tide-lineage").And.NotContain("tool-panel-tab-timeline");
            page.Component.Primary.Should().Be(ActionFlow.RecordUpdate, "an external team's part records their updates");
            page.Component.MoreItems.Select(i => i.Label).Should().NotContain(["Split to teams…", "Split off a part", "Take it"],
                "a part isn't split again, and nobody takes an external team's work");
        }
    }

    [Fact]
    public async Task AReadOnlyViewer_SeesTheActionAndItsFeed_ButNoPrimaryMoreStatusButtonOrComposer()
    {
        var team = await Team("Layout viewer team");
        var number = await Raise(team, "Layout viewer action", due: Today.AddDays(-2));
        var id = (await Detail(number)).Action.Id;
        await As(actions => actions.AddCommentAsync(id, "Layout checked on duty"));

        await using var page = await Open(number, Viewer);
        var html = await page.Html();
        html.Should().Contain("Layout viewer action").And.Contain("Layout checked on duty").And.Contain("id=\"feed-h\"");
        page.Component.Primary.Should().Be(ActionFlow.None);
        page.Component.MoreItems.Should().BeEmpty();
        Header(html).Should().NotContain("btn-primary").And.NotContain("tide-menubutton").And.NotContain("tide-status-badge")
            .And.Contain(">Open</span>", "the status is a plain badge");
        html.Should().NotContain("Attach screenshots or evidence").And.NotContain("Record a team's update").And.NotContain("class=\"tide-next\"")
            .And.NotContain("Add checklist item");
    }

    [Fact]
    public async Task AFilesOnACommentLink_UnfoldsTheFeed_SoTheCommentIsThere()
    {
        var team = await Team("Layout jump team");
        var number = await Raise(team, "Layout jump action");
        var id = (await Detail(number)).Action.Id;
        await As(actions => actions.AddCommentAsync(id, "Layout the oldest comment"));
        for (var i = 0; i < 11; i++) await As(actions => actions.AddCommentAsync(id, $"Layout later {i}"));
        var oldest = (await Detail(number)).Action.Comments.First();

        await using var page = await Open(number);
        (await page.Html()).Should().NotContain("Layout the oldest comment");
        await page.Click(p => Clicked(p, () => p.ShowComment(oldest.Id)));
        (await page.Html()).Should().Contain($"id=\"comment-{oldest.Id}\" tabindex=\"-1\"").And.Contain("Layout the oldest comment");
        page.Component.FeedAll.Should().BeTrue();
    }
}
