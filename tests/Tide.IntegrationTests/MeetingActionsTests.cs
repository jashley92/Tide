using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Actions;
using Tide.Application.Articles;
using Tide.Application.Briefs;
using Tide.Application.Org;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Infrastructure.Persistence;
using Tide.Web.Components.Pages;
using Tide.Web.Components.Shared;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>
/// The meeting's actions: each recommended line raised in one click from the article's slide, the wrap-up's gap, the
/// brief header's one primary button, how late an overdue action is, and closing a clean sweep from its hunt panel.
/// </summary>
public class MeetingActionsTests(TideApp app) : IClassFixture<TideApp>
{
    private DateOnly Today => app.Services.GetRequiredService<OrgClock>().CurrentBriefDate;

    private static DateOnly BusinessDaysBefore(DateOnly day, int n)
    {
        for (var i = 0; i < n; i++) day = WorkingDays.Weekdays.Previous(day);
        return day;
    }

    // A past day's brief with one P1 threat article recommending two things, at the given status.
    private async Task<(DateOnly Day, Guid Article)> BriefWithRecommendations(int daysBack, BriefStatus status = BriefStatus.Draft)
    {
        var day = BusinessDaysBefore(Today, daysBack);
        await using var scope = SignedIn(app, SignIn.Analyst);
        var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
        var briefs = scope.ServiceProvider.GetRequiredService<BriefService>();
        var id = await articles.CreateAsync($"GitLab takeover {daysBack}", "Vendor", null, TlpLevel.Clear);
        var form = ArticleForm.From((await articles.GetAsync(id))!);
        form.Bluf = "Exploited in the wild.";
        form.Classification = Classification.Threat;
        form.Priority = Priority.P1;
        form.RecommendedActions = "- Patch GitLab to 17.3.2\n- Notify the service desk";
        await articles.SaveAsync(form);
        var brief = await briefs.GetOrStartAsync(day);
        await briefs.AddArticleAsync(brief.Id, id);
        if (status != BriefStatus.Draft)
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var b = await db.Briefs.SingleAsync(x => x.Id == brief.Id);
            b.Status = status;
            await db.SaveChangesAsync();
        }
        return (day, id);
    }

    [Fact]
    public async Task TheArticleSlide_ListsEachRecommendedLine_WithAGuessedKind_AndARaisedOneShowsItsAction()
    {
        var (day, article) = await BriefWithRecommendations(12);
        await using var r = await PresentHarness.Open(app, day);
        await r.Click(p => p.OnPresentKey("last"));
        var wrap = await r.Html();
        wrap.Should().Contain("Recommended, with no action raised yet").And.Contain("<li>Patch GitLab to 17.3.2</li>").And.Contain("<li>Notify the service desk</li>");

        await r.Click(p => p.OnPresentKey("prev"));
        var slide = await r.Html();
        // Each line's guessed kind, on its chip in the rail.
        string Chip(string line) => slide[slide.IndexOf($"aria-label=\"Change the kind and team for: {line}\"", StringComparison.Ordinal)..][..300];
        Chip("Patch GitLab to 17.3.2").Should().Contain($"<span>{ActionService.KindName(ActionKind.PatchRemediate)} &#xB7;");
        Chip("Notify the service desk").Should().Contain($"<span>{ActionService.KindName(ActionKind.Awareness)} &#xB7;");
        slide.Should().Contain("class=\"tide-reco\"").And.Contain("Raise</button>")
            .And.Contain("Another action</button>", "the free-text add stays for anything else");

        int number;
        await using (var scope = SignedIn(app, SignIn.Analyst))
        {
            var actions = scope.ServiceProvider.GetRequiredService<ActionService>();
            var teams = scope.ServiceProvider.GetRequiredService<TeamService>();
            var team = await teams.AddAsync("Recommended lines team", null, null);
            await teams.AddMemberAsync(team, SignIn.Analyst.UserId, SignIn.Analyst.Name);
            number = (await actions.QuickAddAsync(article, "Patch GitLab to 17.3.2", ActionKind.PatchRemediate, team, null, day, recommendation: "Patch GitLab to 17.3.2")).Number;
            var made = (await actions.GetAsync(number))!.Action;
            made.Priority.Should().Be(Priority.P1, "the article's priority");
            (made.AssigneeId, made.AssigneeName).Should().Be(("dev:tester", "Tester"), "whoever raises it in the meeting, on its team, holds it");
        }
        await using var again = await PresentHarness.Open(app, day);
        await again.Click(p => p.OnPresentKey("last"));
        await again.Click(p => p.OnPresentKey("prev"));
        var raised = await again.Html();
        raised.Should().Contain($"<button type=\"button\" class=\"tide-action-num tide-action-open\" title=\"Open A-{number} here\" data-card-opener=\"action:{number}\">A-{number}</button> &#xB7; Open &#xB7; Tester")
            .And.NotContain("Change the kind and team for: Patch GitLab to 17.3.2");
        await again.Click(p => p.OnPresentKey("last"));
        (await again.Html()).Should().NotContain("<li>Patch GitLab to 17.3.2</li>", "a raised line isn't a gap any more")
            .And.Contain("<li>Notify the service desk</li>");
    }

    [Fact]
    public async Task TheSlidesTakeIt_IsOnlyForAMemberOfTheActionsTeam()
    {
        var (day, article) = await BriefWithRecommendations(14);
        Guid team;
        int number;
        await using (var scope = SignedIn(app, SignIn.Analyst))
        {
            team = await scope.ServiceProvider.GetRequiredService<TeamService>().AddAsync("Take on the slide team", null, null);
            number = (await scope.ServiceProvider.GetRequiredService<ActionService>()
                .QuickAddAsync(article, "Patch GitLab to 17.3.2", ActionKind.PatchRemediate, team, null, day, recommendation: "Patch GitLab to 17.3.2")).Number;
        }
        var take = $"aria-label=\"Take A-{number}\"";

        await using (var r = await PresentHarness.Open(app, day))
        {
            await r.Click(p => p.OnPresentKey("last"));
            await r.Click(p => p.OnPresentKey("prev"));
            var slide = await r.Html();
            slide.Should().Contain($"A-{number}</button>", "the raised action is on its line").And.NotContain(take, "the presenter isn't on its team");
        }

        await using (var scope = SignedIn(app, SignIn.Analyst))
            await scope.ServiceProvider.GetRequiredService<TeamService>().AddMemberAsync(team, SignIn.Analyst.UserId, SignIn.Analyst.Name);
        await using (var r = await PresentHarness.Open(app, day))
        {
            await r.Click(p => p.OnPresentKey("last"));
            await r.Click(p => p.OnPresentKey("prev"));
            (await r.Html()).Should().Contain(take, "on the team now, the presenter may take it");
        }
    }

    [Fact]
    public async Task TheSlidesRowsOfControls_EachHaveTheirLayout()
    {
        // A stylesheet edit once dropped the quick add's grid, and its five controls stacked one per line: each row of
        // controls on an article slide keeps its own layout rule.
        var css = await app.CreateClient().GetStringAsync("/app.css");
        css.Should().Contain(".tide-quick-add { display: grid;", "the quick add: what needs doing, then kind, team, due date and Add")
            .And.Contain(".tide-reco li { display: grid;", "a recommended line with what to raise it as")
            .And.Contain(".tide-slide-actlist { display: grid;", "the article's actions, one line each");
    }

    [Fact]
    public async Task TheMeeting_OffersInternalTeamsOnly_AndRefusesAnExternalOne()
    {
        var (day, article) = await BriefWithRecommendations(14);
        var name = "Meeting external provider " + Guid.NewGuid().ToString("N")[..6];
        Guid external;
        await using (var scope = SignedIn(app, SignIn.Analyst))
            external = await scope.ServiceProvider.GetRequiredService<TeamService>().AddExternalAsync(new ExternalTeamForm { Name = name });

        await using var r = await PresentHarness.Open(app, day);
        await r.Click(p => p.OnPresentKey("last"));
        await r.Click(p => p.OnPresentKey("prev"));
        var slide = await r.Html();
        slide.Should().Contain("class=\"tide-reco\"").And.NotContain(name, "an external team's work is split off an action, with a parent to follow it up");

        await using (var scope = SignedIn(app, SignIn.Analyst))
        {
            var refused = () => scope.ServiceProvider.GetRequiredService<ActionService>().QuickAddAsync(article, "Patch their edge", ActionKind.PatchRemediate, external, null, day);
            (await refused.Should().ThrowAsync<ArgumentException>()).WithMessage("*split off an action as a part*");
        }
    }

    // The team the quick add's select has chosen, as picking it would.
    private static void QuickAddTeam(Present page, Guid team) =>
        typeof(Present).GetField("_qaTeam", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(page, team);

    [Fact]
    public async Task AnActionAddedInTheMeeting_ToATeamThatWarnsAtItsLimit_AsksWhy_AndGoesAhead()
    {
        var (day, article) = await BriefWithRecommendations(17);
        Guid team;
        await using (var scope = SignedIn(app, SignIn.Analyst))
        {
            var teams = scope.ServiceProvider.GetRequiredService<TeamService>();
            team = await teams.AddAsync("Meeting limit team " + Guid.NewGuid().ToString("N")[..6], null, null);
            await teams.SetLimitAsync(team, 1, ActiveLimitMode.Warn);
            await scope.ServiceProvider.GetRequiredService<ActionService>().CreateAsync(new ActionForm { Title = "At the limit", TeamId = team });
        }
        await using var r = await PresentHarness.Open(app, day);
        r.Component.TypeQuickAddTitle(article, "Sweep the proxy logs");
        QuickAddTeam(r.Component, team);

        // The add waits on the dialog: start it as a click would, and keep hold of it to wait for it once it's answered.
        Task adding = Task.CompletedTask;
        await r.Renderer.Dispatcher.InvokeAsync(() => { adding = r.Pressed(p => p.QuickAdd(article)); });
        System.Net.WebUtility.HtmlDecode(await r.HtmlOnce("Why go over the limit?")).Should().Contain("Over Meeting limit team", "the meeting asks why, as the action page does");
        await r.Renderer.Dispatcher.InvokeAsync(() =>
        {
            r.Component.OverLimit.Reason = "Incident cover";
            return r.Component.OverLimit.Confirm();
        });
        await adding.WaitAsync(TimeSpan.FromSeconds(30));
        ActionRow? made;
        await using (var scope = SignedIn(app, SignIn.Analyst))
            made = (await scope.ServiceProvider.GetRequiredService<ActionService>().ListAsync(new ActionFilter(ActionView.Open, team)))
                .FirstOrDefault(x => x.Title == "Sweep the proxy logs");
        made.Should().NotBeNull("it goes ahead with the reason");
        await using (var scope = SignedIn(app, SignIn.Analyst))
            (await scope.ServiceProvider.GetRequiredService<ActionService>().GetAsync(made!.Number))!.Action.Events
                .Should().Contain(e => e.Text.Contains("(2 of 1 active): Incident cover"));
    }

    [Fact]
    public async Task AnActionAddedInTheMeeting_OverALimit_CancelledAtTheQuestion_IsntAdded_AndFocusGoesBackToAdd()
    {
        var (day, article) = await BriefWithRecommendations(18);
        Guid team;
        await using (var scope = SignedIn(app, SignIn.Analyst))
        {
            var teams = scope.ServiceProvider.GetRequiredService<TeamService>();
            team = await teams.AddAsync("Meeting cancel team " + Guid.NewGuid().ToString("N")[..6], null, null);
            await teams.SetLimitAsync(team, 1, ActiveLimitMode.Warn);
            await scope.ServiceProvider.GetRequiredService<ActionService>().CreateAsync(new ActionForm { Title = "At the limit", TeamId = team });
        }
        await using var r = await PresentHarness.Open(app, day);
        r.Component.TypeQuickAddTitle(article, "Not after all");
        QuickAddTeam(r.Component, team);

        Task adding = Task.CompletedTask;
        await r.Renderer.Dispatcher.InvokeAsync(() => { adding = r.Pressed(p => p.QuickAdd(article)); });
        await r.HtmlOnce("Why go over the limit?");
        await r.Renderer.Dispatcher.InvokeAsync(r.Component.OverLimit.Cancel);
        await adding.WaitAsync(TimeSpan.FromSeconds(30));

        r.Component.FocusingQuickAdd.Should().BeTrue("Add was disabled while it asked, so focus had left it");
        r.Component.QuickAddTitle(article).Should().Be("Not after all", "what was typed stays, to change the team or add it later");
        await using (var scope = SignedIn(app, SignIn.Analyst))
            (await scope.ServiceProvider.GetRequiredService<ActionService>().ListAsync(new ActionFilter(ActionView.Open, team)))
                .Should().NotContain(x => x.Title == "Not after all");
    }

    [Fact]
    public async Task AnotherDayInTheAddress_StartsFromItsOwnFirstSlide_AndOneThatIsntADayShowsNoBrief()
    {
        var (first, _) = await BriefWithRecommendations(21);
        var (second, _) = await BriefWithRecommendations(22);
        await using var r = await PresentHarness.Open(app, first);
        await r.Click(p => p.OnPresentKey("last"));
        (await r.Html()).Should().Contain("Recommended, with no action raised yet");

        Task Address(string day) => r.Click(p => p.SetParametersAsync(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Day"] = day })));
        await Address($"{second:yyyy-MM-dd}");
        var html = await r.Html();
        html.Should().Contain($"Presenting the brief for {Ui.ShortDay(second)}").And.Contain("aria-label=\"Slide 1 of")
            .And.NotContain("Recommended, with no action raised yet", "the last day's slide number doesn't carry over");

        await Address("not-a-day");
        (await r.Html()).Should().Contain("isn't a day").And.NotContain("GitLab takeover 22", "the last day's brief isn't shown under the new address");
    }

    [Fact]
    public async Task TheTopBarsEnd_BringsTheWrapUpForward_AndOnTheWrapUpEndsTheMeeting()
    {
        // A past day's brief with an article nobody ticked off: the wrap-up has to be seen before the meeting ends.
        var (day, _) = await BriefWithRecommendations(24);
        await using var r = await PresentHarness.Open(app, day);
        var endFromTop = typeof(Present).GetMethod("EndFromTop", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var redraw = typeof(ComponentBase).GetMethod("StateHasChanged", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        // As the button's click would: run it, then draw the page again.
        async Task EndFromTop(Present p)
        {
            await (Task)endFromTop.Invoke(p, null)!;
            redraw.Invoke(p, null);
        }
        async Task<BriefStatus> Status()
        {
            await using var scope = SignedIn(app, SignIn.Analyst);
            return (await scope.ServiceProvider.GetRequiredService<AppDbContext>().Briefs.SingleAsync(b => b.BriefDate == day)).Status;
        }

        await r.Click(EndFromTop);
        (await r.Html()).Should().Contain(": Wrap-up</div>", "the wrap-up slide is showing");
        (await Status()).Should().Be(BriefStatus.Draft, "the first click only shows the wrap-up");

        await r.Click(EndFromTop);
        (await Status()).Should().Be(BriefStatus.Presented, "on the wrap-up, the top bar's End ends the meeting instead of doing nothing");
    }

    [Fact]
    public async Task PresentMode_HasTheErrorBar_LikeEveryOtherPage()
    {
        var html = await app.CreateClient().GetStringAsync($"/present/{BusinessDaysBefore(Today, 23):yyyy-MM-dd}");

        html.Should().Contain("id=\"blazor-error-ui\"", "without it a circuit that ends leaves the slides frozen with no word why");
    }

    private static string Header(string html)
    {
        var start = html.IndexOf("<div class=\"tide-page-head tide-brief-head\">", StringComparison.Ordinal);
        var end = html.IndexOf("aria-label=\"Brief progress\"", StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0);
        return html[start..(end < 0 ? html.Length : end)];
    }

    private static int Count(string text, string what) => (text.Length - text.Replace(what, "", StringComparison.Ordinal).Length) / what.Length;

    [Theory]
    [InlineData(13, BriefStatus.Draft, "Mark ready</button>")]
    [InlineData(14, BriefStatus.Ready, "Present</a>")]
    [InlineData(15, BriefStatus.Presented, "Send</button>")]
    public async Task TheBriefHeader_HasOnePrimaryButton_ItsNextStep(int daysBack, BriefStatus status, string primary)
    {
        var (day, _) = await BriefWithRecommendations(daysBack, status);
        var head = Header(await app.CreateClient().GetStringAsync($"/briefs/{day:yyyy-MM-dd}"));

        Count(head, "btn btn-primary").Should().Be(1);
        var at = head.IndexOf("btn btn-primary", StringComparison.Ordinal);
        var end = head.IndexOf(primary, at, StringComparison.Ordinal);
        end.Should().BePositive();
        head[at..end].Should().NotContain("</a>").And.NotContain("</button>", "the primary class is on that button");
        if (status != BriefStatus.Ready) head.Should().Contain("btn btn-outline-secondary\" href=\"present/", "Present is still there, as a secondary button");
    }

    [Fact]
    public async Task AnEmptyDraft_HasNoMarkReady()
    {
        var day = BusinessDaysBefore(Today, 16);
        await using (var scope = SignedIn(app, SignIn.Analyst))
            await scope.ServiceProvider.GetRequiredService<BriefService>().GetOrStartAsync(day);
        var head = Header(await app.CreateClient().GetStringAsync($"/briefs/{day:yyyy-MM-dd}"));
        head.Should().NotContain("Mark ready").And.NotContain("btn btn-primary");
    }

    [Fact]
    public async Task AnOverdueBadge_SaysHowLate()
    {
        await using var scope = SignedIn(app, SignIn.Analyst);
        var renderer = new Microsoft.AspNetCore.Components.Web.HtmlRenderer(scope.ServiceProvider,
            scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<SlaBadge>(
            Microsoft.AspNetCore.Components.ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                ["State"] = SlaState.Overdue, ["Due"] = (DateOnly?)new DateOnly(2026, 10, 27), ["Today"] = (DateOnly?)new DateOnly(2026, 10, 30),
            }))).ToHtmlString());
        html.Should().Contain("Overdue &#xB7; 3 days</span>");
        await renderer.DisposeAsync();
    }

    [Fact]
    public async Task TheHuntPanel_NotesAQueryEditedAfterItsResult()
    {
        int number;
        await using (var scope = SignedIn(app, SignIn.Analyst))
        {
            var actions = scope.ServiceProvider.GetRequiredService<ActionService>();
            var team = (await scope.ServiceProvider.GetRequiredService<TeamService>().ListAsync())[0].Id;
            number = await actions.CreateAsync(new ActionForm { Title = "Hunt for shells (edited)", Kind = ActionKind.ThreatHunt, TeamId = team });
            var hunt = await actions.AddHandWrittenHuntAsync((await actions.GetAsync(number))!.Action.Id, "kql", "DeviceProcessEvents");
            await actions.RecordHuntResultAsync(hunt.Id, HuntResult.Hits, 2, null, null);
            await actions.SaveHuntTextAsync(hunt.Id, "DeviceProcessEvents | take 50");
        }
        await using var r = await RenderAsync<ActionPage>(app, "/actions/" + number, new() { ["Number"] = (int?)number });
        (await r.HtmlOnce("Edited after its result")).Should().Contain("<div class=\"small tide-muted\">Edited after its result was recorded (");
    }

    [Fact]
    public async Task ASweepsHuntPanel_OffersNoHitsClose_OnlyOnceTheQueryIsCopied()
    {
        int number;
        Guid huntId;
        await using (var scope = SignedIn(app, SignIn.Analyst))
        {
            var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
            var actions = scope.ServiceProvider.GetRequiredService<ActionService>();
            var id = await articles.CreateAsync("Edge VPN zero-day (sweep)", "Vendor", null, TlpLevel.Clear);
            var form = ArticleForm.From((await articles.GetAsync(id))!);
            form.Bluf = "Exploited.";
            form.Indicators = [new() { Type = IndicatorType.IpAddress, Value = "203.0.113.45" }];
            await articles.SaveAsync(form);
            var team = (await scope.ServiceProvider.GetRequiredService<TeamService>().ListAsync())[0].Id;
            number = await actions.CreateAsync(new ActionForm { Title = "Sweep for the VPN IPs", Kind = ActionKind.IocSweep, TeamId = team, ArticleId = id });
            huntId = (await actions.GenerateHuntAsync((await actions.GetAsync(number))!.Action.Id, "kql")).Id;
        }
        await using var r = await RenderAsync<ActionPage>(app, "/actions/" + number, new() { ["Number"] = (int?)number });
        var html = await r.HtmlOnce("No hits, close");
        html.Should().Contain("class=\"tide-hunt-close\"").And.Contain("value=\"30\"", "the query's own window")
            .And.Contain("Copy the query and run it first.").And.NotContain("left out");
        var button = html[html.LastIndexOf("<button", html.IndexOf("No hits, close", StringComparison.Ordinal), StringComparison.Ordinal)..];
        button.Should().StartWith("<button type=\"button\" class=\"btn btn-outline-secondary btn-sm\" disabled");

        await using (var scope = SignedIn(app, SignIn.Analyst))
            await scope.ServiceProvider.GetRequiredService<ActionService>().CloseWithNoHitsAsync(huntId, 30, false);
        await using var done = await RenderAsync<ActionPage>(app, "/actions/" + number, new() { ["Number"] = (int?)number });
        (await done.HtmlOnce("Searched 30 days")).Should().NotContain("No hits, close", "a closed action has nothing left to close");
    }
}
