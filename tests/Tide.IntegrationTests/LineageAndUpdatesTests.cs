using System.Net;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Actions;
using Tide.Application.Articles;
using Tide.Application.Org;
using Tide.Application.Security;
using Tide.Domain.Enums;
using Tide.Web.Components.Pages;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>
/// EP-88 on the action's page: a split action's lineage (article, parent, parts, states and links, and what a screen
/// reader hears), its parts' remediation timeline, and Record an update (one history line, the reply kept as a comment,
/// the status and due date moved by the rules, a team's limit, and who may).
/// </summary>
public class LineageAndUpdatesTests(TideApp app) : IClassFixture<TideApp>
{
    private static readonly SignIn Owner = new("dev:ep88-owner", "Owner", "ActionOwner");

    private async Task<T> As<T>(Func<IServiceProvider, Task<T>> what, SignIn? who = null)
    {
        await using var scope = SignedIn(app, who ?? SignIn.Analyst);
        return await what(scope.ServiceProvider);
    }

    private Task<Guid> Team(string name, int? limit = null, ActiveLimitMode mode = ActiveLimitMode.Show, bool external = false) => As(async sp =>
    {
        var teams = sp.GetRequiredService<TeamService>();
        var id = external ? await teams.AddExternalAsync(name, "noc@provider.example") : await teams.AddAsync(name, null, null);
        if (limit is not null) await teams.SetLimitAsync(id, limit, mode);
        return id;
    });

    private Task<int> Raise(Guid team, string title, Guid? parent = null, DateOnly? due = null, Guid? article = null) =>
        As(sp => sp.GetRequiredService<ActionService>().CreateAsync(new ActionForm { Title = title, TeamId = team, ParentId = parent, DueDate = due, ArticleId = article }));

    private Task<ActionDetail> Detail(int number) => As(async sp => (await sp.GetRequiredService<ActionService>().GetAsync(number))!);

    private Task Move(int number, ActionStatus to, ActionOutcome outcome = ActionOutcome.None) => As(async sp =>
    {
        var svc = sp.GetRequiredService<ActionService>();
        await svc.SetStatusAsync((await svc.GetAsync(number))!.Action.Id, to, outcome, null);
        return true;
    });

    private Task<Rendered<ActionPage>> Open(int number, SignIn? who = null) =>
        RenderAsync<ActionPage>(app, "/actions/" + number, new() { ["Number"] = (int?)number }, who);

    private DateOnly Today => app.Services.GetRequiredService<OrgClock>().Today;

    private Task<Guid> Article(string title, params string[] cves) => As(async sp =>
    {
        var svc = sp.GetRequiredService<ArticleService>();
        var id = await svc.CreateAsync(title, "Vendor", "https://vendor.example.com/" + Guid.NewGuid().ToString("N"), TlpLevel.Clear);
        var form = ArticleForm.From((await svc.GetAsync(id))!);
        form.Cves = [.. cves];
        await svc.SaveAsync(form);
        return id;
    });

    // A parent from an article, split to an internal team (overdue), an external one (tracking) and a third (done).
    private async Task<(int Parent, int Overdue, int Tracking, int Done, Guid Article)> Split(string tag)
    {
        var article = await Article($"{tag} edge VPN zero-day", "CVE-2026-1111", "CVE-2026-2222");
        var owner = await Team($"{tag} Security Operations");
        var network = await Team($"{tag} Network Engineering");
        var provider = await Team($"{tag} Hosting Provider", external: true);
        var desk = await Team($"{tag} Service Desk");
        var parent = await Raise(owner, $"{tag} patch the VPN", due: Today.AddDays(30), article: article);
        var id = (await Detail(parent)).Action.Id;
        var overdue = await Raise(network, $"{tag} part one", id, Today.AddDays(-3));
        var tracking = await Raise(provider, $"{tag} part two", id, Today.AddDays(20));
        var done = await Raise(desk, $"{tag} part three", id, Today.AddDays(10));
        await Move(tracking, ActionStatus.Tracking);
        await Move(done, ActionStatus.Done, ActionOutcome.NoHits);
        return (parent, overdue, tracking, done, article);
    }

    private static string Flow(string html) => Regex.Match(html, "<section class=\"tide-card tide-lineage\".*?</section>", RegexOptions.Singleline).Value;

    private static string Timeline(string html) => Regex.Match(html, "<section class=\"tide-card tide-remtl\".*?</section>", RegexOptions.Singleline).Value;

    [Fact]
    public async Task AParent_ShowsItsArticle_ItselfAndEachPart_ColouredByState_EachLinked()
    {
        var (parent, overdue, tracking, done, article) = await Split("EP88a");
        await using var page = await Open(parent);
        var flow = WebUtility.HtmlDecode(Flow(await page.Html()));

        flow.Should().Contain($"href=\"articles/{article}\"").And.Contain("EP88a edge VPN zero-day").And.Contain("CVE-2026-1111, CVE-2026-2222");
        flow.Should().Contain($"href=\"actions/{parent}\" aria-current=\"page\"", "the parent is the page you're on")
            .And.Contain("EP88a Security Operations").And.Contain("1 of 3 done · 1 overdue");
        Regex.Matches(flow, "data-part=\"(\\d+)\" data-state=\"(\\w+)\"").Select(m => (int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), m.Groups[2].Value))
            .Should().Equal((overdue, "Overdue"), (tracking, "Tracking"), (done, "Done"));
        flow.Should().Contain($"href=\"actions/{overdue}\"").And.Contain($"href=\"actions/{tracking}\"").And.Contain($"href=\"actions/{done}\"");
        flow.Should().Contain("3 days late").And.Contain("Closed " + Today.ToString("MMM d", System.Globalization.CultureInfo.InvariantCulture));
        Regex.Match(flow, $"data-part=\"{tracking}\".*?</li>", RegexOptions.Singleline).Value.Should().Contain("tide-badge-purple").And.Contain(">external<");
        flow.Should().Contain("tide-badge-danger").And.Contain("tide-badge-info");
        Regex.Matches(flow, "<li><span class=\"tide-badge (tide-badge-\\w+)\"").Select(m => m.Groups[1].Value)
            .Should().Equal(["tide-badge-info", "tide-badge-ok", "tide-badge-warn", "tide-badge-danger", "tide-badge-purple"], "the legend lists every state in the SLA badge's tones");
        // On the parent the lineage is the Parts card: its controls are here, and the parts aren't listed a second time.
        flow.Should().Contain($"actions?view=open&layout=board&parent={parent}\">Open its 3 actions on the board").And.Contain("Split off a part for a team");
        WebUtility.HtmlDecode(await page.Html()).Should().NotContain("<h2>Parts");
    }

    [Fact]
    public async Task TheFlow_IsANestedListAScreenReaderHears_InTheSameOrder_WithEachStateInWords()
    {
        var (parent, _, _, _, _) = await Split("EP88b");
        await using var page = await Open(parent);
        var flow = WebUtility.HtmlDecode(Flow(await page.Html()));
        flow.Should().Contain("<ol class=\"tide-lin-flow\"").And.Contain("<ul class=\"tide-lin-parts\">");
        var said = Regex.Replace(Regex.Replace(flow, "<[^>]*aria-hidden=\"true\"[^>]*>[^<]*</[^>]+>", ""), "<[^>]+>", " ");
        said = Regex.Replace(said, "\\s+", " ");
        said.Should().ContainAll("Raised from the article:", "Then the action:", "Split into 3 parts:", "Overdue", "Tracking", "Done or closed", "external");
        said.IndexOf("Raised from the article", StringComparison.Ordinal).Should().BeLessThan(said.IndexOf("Then the action", StringComparison.Ordinal));
        said.IndexOf("Then the action", StringComparison.Ordinal).Should().BeLessThan(said.IndexOf("Split into", StringComparison.Ordinal));
    }

    [Fact]
    public async Task APart_ShowsItsParentAndSiblings_WithItselfMarked_AndNoTimeline()
    {
        var (parent, overdue, tracking, done, _) = await Split("EP88c");
        await using var page = await Open(tracking);
        var html = WebUtility.HtmlDecode(await page.Html());
        var flow = Flow(html);
        flow.Should().Contain($"href=\"actions/{parent}\"").And.NotContain($"href=\"actions/{parent}\" aria-current");
        flow.Should().Contain($"href=\"actions/{tracking}\" aria-current=\"page\"").And.Contain("(this action)");
        flow.Should().Contain($"data-part=\"{overdue}\"").And.Contain($"data-part=\"{done}\"");
        Timeline(html).Should().BeEmpty("the timeline is the parent's");
    }

    [Fact]
    public async Task AnArticlelessParent_StartsAtItself_AndAnActionWithNoParts_HasNoLineage()
    {
        var team = await Team("EP88d team");
        var parent = await Raise(team, "EP88d no article", due: Today.AddDays(5));
        var lone = await Raise(team, "EP88d alone");
        await Raise(team, "EP88d its part", (await Detail(parent)).Action.Id, Today.AddDays(4));

        await using (var page = await Open(parent))
        {
            var flow = WebUtility.HtmlDecode(Flow(await page.Html()));
            flow.Should().NotContain("data-node=\"article\"").And.Contain("The action:").And.Contain("Split into 1 part:");
        }
        await using var alone = await Open(lone);
        Flow(await alone.Html()).Should().BeEmpty();
    }

    [Fact]
    public async Task TheTimeline_HasARowPerPart_TodaysMarker_TheOverrun_AndADoneEarlyClose()
    {
        var (parent, overdue, tracking, done, _) = await Split("EP88e");
        var d = await Detail(parent);
        var timeline = RemediationTimeline.Build(d.Lineage!.Parts, Today)!;
        await using var page = await RenderAsync<ActionPage>(app, $"/actions/{parent}?tool=timeline", new() { ["Number"] = (int?)parent });
        var html = WebUtility.HtmlDecode(Timeline(await page.Html()));

        html.Should().Contain($"data-today-at=\"{timeline.Today.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}\"");
        html.Should().Contain($"class=\"tide-remtl-grid\" style=\"--at: {timeline.Today.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}%\"", "each track draws today's line from it");
        Regex.Matches(html, "<li class=\"tide-remtl-row (tide-badge-\\w+)\" data-part=\"(\\d+)\"").Select(m => (int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture), m.Groups[1].Value))
            .Should().Equal((overdue, "tide-badge-danger"), (tracking, "tide-badge-purple"), (done, "tide-badge-info"));
        var short_ = (DateOnly x) => x.ToString("MMM d", System.Globalization.CultureInfo.InvariantCulture);
        Regex.Match(html, $"data-part=\"{overdue}\".*?</li>", RegexOptions.Singleline).Value.Should().Contain("tide-remtl-over").And.Contain("3 days late");
        Regex.Match(html, $"data-part=\"{done}\".*?</li>", RegexOptions.Singleline).Value.Should().Contain("tide-remtl-closed")
            .And.Contain($"closed {short_(Today)}, 10 days early");
        html.Should().Contain($"From {short_(timeline.From)} to {short_(timeline.To)}").And.Contain(">Today<");
    }

    [Fact]
    public async Task TheTimeline_IsHidden_WhenNoPartHasADueDate()
    {
        var team = await Team("EP88f team");
        var parent = await Raise(team, "EP88f parent");
        var id = (await Detail(parent)).Action.Id;
        var part = await Raise(team, "EP88f part", id);
        // A part with no SLA target has no due date; clear it in case the default targets gave it one.
        await As(async sp =>
        {
            await using var db = await sp.GetRequiredService<Tide.Application.Abstractions.IAppDbContextFactory>().CreateAsync();
            var a = db.Actions.Single(x => x.Number == part);
            a.DueDate = null;
            await db.SaveChangesAsync();
            return true;
        });
        await using var page = await Open(parent);
        var html = await page.Html();
        Flow(html).Should().NotBeEmpty();
        Timeline(html).Should().BeEmpty();
    }

    [Fact]
    public async Task RecordingAnUpdate_WritesOneLine_KeepsTheReply_AndMovesStatusAndDate()
    {
        var team = await Team("EP88g Network Engineering", external: true);
        var number = await Raise(team, "EP88g firewall change", due: Today.AddDays(2));
        var before = await Detail(number);
        var newDue = Today.AddDays(9);
        await As(async sp =>
        {
            await sp.GetRequiredService<ActionService>().RecordUpdateAsync(new TeamUpdate
            {
                ActionId = before.Action.Id, From = ActionStatus.Open, Status = ActionStatus.Tracking,
                Reply = "Hi,\r\nWe'll do it in Tuesday's window.\r\n\r\nThanks", DueDate = newDue, DueReason = "their change window", Note = "Scheduled for Tuesday",
            });
            return true;
        });
        var d = await Detail(number);
        (d.Action.Status, d.Action.DueDate).Should().Be((ActionStatus.Tracking, newDue));
        var line = d.Action.Events.Should().ContainSingle(e => e.Text.StartsWith("Update from", StringComparison.Ordinal)).Subject.Text;
        line.Should().Be($"Update from EP88g Network Engineering: Scheduled for Tuesday. Due: {ActionService.Day(before.Action.DueDate!.Value)} → {ActionService.Day(newDue)} (their change window). Their reply is kept as a comment. Status: Open → Tracking.");
        d.Action.Events.Should().HaveCount(before.Action.Events.Count + 1, "it's one entry");
        d.Action.Comments.Should().ContainSingle().Which.Text.Should()
            .Be("**Reply from EP88g Network Engineering**, recorded by email:\n\n> Hi,  \n> We'll do it in Tuesday's window.\n>\n> Thanks");

        await using var page = await Open(number);
        WebUtility.HtmlDecode(await page.Html()).Should().Contain("Record an update").And.Contain("<blockquote>");
    }

    [Fact]
    public async Task RecordingAnUpdate_FollowsTheRules_TheLimit_TheOutcome_AStaleCopy_AndNothingToSay()
    {
        var team = await Team("EP88h refusing team", 1, ActiveLimitMode.Refuse);
        var number = await Raise(team, "EP88h tracked", due: Today.AddDays(5));
        var id = (await Detail(number)).Action.Id;
        await Move(number, ActionStatus.Tracking);
        await Raise(team, "EP88h at the limit");
        Task Record(TeamUpdate u) => As(async sp =>
        {
            await sp.GetRequiredService<ActionService>().RecordUpdateAsync(u);
            return true;
        });

        var back = () => Record(new TeamUpdate { ActionId = id, From = ActionStatus.Tracking, Status = ActionStatus.InProgress, Note = "They handed it back" });
        (await back.Should().ThrowAsync<ActiveLimitException>()).Which.Message.Should().Contain("at its limit");
        var noOutcome = () => Record(new TeamUpdate { ActionId = id, From = ActionStatus.Tracking, Status = ActionStatus.Done });
        await noOutcome.Should().ThrowAsync<ArgumentException>().WithMessage("*outcome*");
        var stale = () => Record(new TeamUpdate { ActionId = id, From = ActionStatus.Open, Status = ActionStatus.Blocked });
        await stale.Should().ThrowAsync<StaleEditException>();
        var nothing = () => Record(new TeamUpdate { ActionId = id, From = ActionStatus.Tracking, Status = ActionStatus.Tracking });
        await nothing.Should().ThrowAsync<ArgumentException>().WithMessage("Paste their reply*");
        var noReason = () => Record(new TeamUpdate { ActionId = id, From = ActionStatus.Tracking, Status = ActionStatus.Tracking, DueDate = Today.AddDays(8) });
        await noReason.Should().ThrowAsync<ArgumentException>().WithMessage("Say why*");
        var d = await Detail(number);
        (d.Action.Status, d.Action.DueDate, d.Action.Comments.Count).Should().Be((ActionStatus.Tracking, Today.AddDays(5), 0), "nothing refused was saved");

        await Record(new TeamUpdate { ActionId = id, From = ActionStatus.Tracking, Status = ActionStatus.Done, Outcome = ActionOutcome.NoHits, Note = "Rule in place" });
        d = await Detail(number);
        (d.Action.Status, d.Action.Outcome, d.Action.OutcomeNote, d.Action.ClosedDate).Should().Be((ActionStatus.Done, ActionOutcome.NoHits, "Rule in place", Today));
        d.Action.Events[0].Text.Should().Be("Update from EP88h refusing team. Status: Tracking → Done (No hits): Rule in place");
    }

    [Fact]
    public async Task OnlyWhoeverWorksTheAction_MayRecordAnUpdate_AndTheButtonIsTheirs()
    {
        var team = await Team("EP88i team");
        var number = await Raise(team, "EP88i not theirs");
        var id = (await Detail(number)).Action.Id;
        var refused = () => As(async sp =>
        {
            await sp.GetRequiredService<ActionService>().RecordUpdateAsync(new TeamUpdate { ActionId = id, From = ActionStatus.Open, Status = ActionStatus.Open, Note = "Hi" });
            return true;
        }, Owner);
        await refused.Should().ThrowAsync<ForbiddenException>();
        await using var page = await Open(number, Owner);
        (await page.Html()).Should().NotContain("Record an update");
    }

    [Fact]
    public async Task TheDialog_RecordsTheUpdate_AndSaysWhyWhenRefused()
    {
        var team = await Team("EP88j refusing team", 1, ActiveLimitMode.Refuse);
        var number = await Raise(team, "EP88j tracked");
        await Move(number, ActionStatus.Tracking);
        await Raise(team, "EP88j at the limit");
        await using var page = await Open(number);
        await page.Press(p => p.StartUpdate());
        WebUtility.HtmlDecode(await page.HtmlOnce("Their reply")).Should().Contain("Record an update from EP88j refusing team").And.Contain("Tracking (now)");
        var dialog = page.Component.Updater!;
        await page.Click(async _ =>
        {
            (dialog.Form!.Status, dialog.Form.Note) = (ActionStatus.InProgress, "Back with us");
            (await dialog.Save()).Should().BeFalse();
        });
        dialog.Error.Should().Contain("at its limit");
        await page.Click(async _ =>
        {
            (dialog.Form!.Status, dialog.Form.Reply) = (ActionStatus.Tracking, "Still on it, next week.");
            (await dialog.Save()).Should().BeTrue(dialog.Error);
        });
        var d = await Detail(number);
        d.Action.Comments.Should().ContainSingle().Which.Text.Should().EndWith("> Still on it, next week.");
        d.Action.Events[0].Text.Should().Be("Update from EP88j refusing team: Back with us. Their reply is kept as a comment.");
    }
}
