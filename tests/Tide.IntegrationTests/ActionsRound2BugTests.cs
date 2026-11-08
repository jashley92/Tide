using System.Net;
using System.Text;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Actions;
using Tide.Application.Articles;
using Tide.Application.Detections;
using Tide.Application.Metrics;
using Tide.Application.Org;
using Tide.Application.Trends;
using Tide.Domain.Enums;
using Tide.Web.Components.Pages;

namespace Tide.IntegrationTests;

/// <summary>Bug round 2, actions: changing a closed action's outcome doesn't close it again over a colleague's reopen,
/// and an over-long detection CSV is refused rather than stored in part.</summary>
public class ActionsRound2BugTests : IDisposable
{
    private readonly TestDb _db = new();

    public ActionsRound2BugTests() => _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task ChangingTheOutcome_OfAnActionReopenedMeanwhile_IsRefused_AndItStaysOpen()
    {
        var team = await _db.Teams().AddAsync("Vulnerability Management", null, null);
        var svc = _db.Actions();
        var n = await svc.CreateAsync(new ActionForm { Title = "Patch the VPN", Kind = ActionKind.PatchRemediate, TeamId = team });
        var id = (await svc.GetAsync(n))!.Action.Id;
        await svc.MoveAsync(id, ActionStatus.Open, ActionStatus.Done, ActionOutcome.Remediated);

        // The outcome prompt opened on Done; a colleague reopened it before it was saved.
        await svc.MoveAsync(id, ActionStatus.Done, ActionStatus.InProgress);
        var save = () => svc.ChangeOutcomeAsync(id, ActionStatus.Done, ActionOutcome.NotAffected, "Not running that version");
        await save.Should().ThrowAsync<StaleEditException>().WithMessage("*In progress*");
        var action = (await svc.GetAsync(n))!.Action;
        action.Status.Should().Be(ActionStatus.InProgress, "saving the outcome used to close it again over the reopen");
        action.ClosedDate.Should().BeNull();

        // Closed again, its outcome changes in place.
        await svc.MoveAsync(id, ActionStatus.InProgress, ActionStatus.Done, ActionOutcome.Remediated);
        await svc.ChangeOutcomeAsync(id, ActionStatus.Done, ActionOutcome.NotAffected, "Not running that version");
        action = (await svc.GetAsync(n))!.Action;
        (action.Status, action.Outcome, action.OutcomeNote).Should().Be((ActionStatus.Done, ActionOutcome.NotAffected, "Not running that version"));
        await FluentActions.Invoking(() => svc.ChangeOutcomeAsync(id, ActionStatus.InProgress, ActionOutcome.None, null))
            .Should().ThrowAsync<ArgumentException>("an outcome is only a closed action's");
    }

    [Fact]
    public void ADetectionCsv_LongerThanTheRowLimit_IsRefused_NotReadInPart()
    {
        // Reading stopped at the limit, and the rules past it were stored as "not in this import", so they stopped counting.
        var csv = new StringBuilder("name,techniques\r\n");
        for (var i = 0; i <= MetricCsv.MaxRows; i++) csv.Append("Rule ").Append(i).Append(",T1059\r\n");
        FluentActions.Invoking(() => DetectionParsers.Csv("rules.csv", csv.ToString(), []))
            .Should().Throw<ArgumentException>().WithMessage("*more than 10,000 rows*");

        var fits = new StringBuilder("name,techniques\r\n");
        for (var i = 0; i < MetricCsv.MaxRows; i++) fits.Append("Rule ").Append(i).Append(",T1059\r\n");
        DetectionParsers.Csv("rules.csv", fits.ToString(), []).Should().HaveCount(MetricCsv.MaxRows);
    }
}

/// <summary>Bug round 2, actions, on the running app: Trends' kind switch and its drill-down's CSV.</summary>
public class ActionsRound2PageTests(TideApp app) : IClassFixture<TideApp>
{
    [Fact]
    public async Task Trends_SwitchingKind_FromAPeriodToDate_GoesToTheOneHoldingToday_NotOneNotStarted()
    {
        // This month to date, switched to Weekly, went to the week holding the month's last day: one that hasn't started.
        var clock = app.Services.GetRequiredService<OrgClock>();
        var month = Period.Of(PeriodKind.Month, clock.Today);
        await using var r = await ComponentHarness.RenderAsync<Trends>(app, $"/trends?p={month.Key}");
        (await r.Renderer.Dispatcher.InvokeAsync(() => r.Component.Switched(PeriodKind.Week)))
            .Should().Be(Period.Of(PeriodKind.Week, clock.Today));

        var past = new Period(PeriodKind.Month, 2026, 9);
        await using var september = await ComponentHarness.RenderAsync<Trends>(app, $"/trends?p={past.Key}");
        (await september.Renderer.Dispatcher.InvokeAsync(() => september.Component.Switched(PeriodKind.Week)))
            .Should().Be(Period.Of(PeriodKind.Week, past.End));
    }

    [Fact]
    public async Task Trends_ArticlesCsv_CountsSubTechniquesAsThePageDoes()
    {
        // The sample phishing article is tagged T1566.002 and briefed on the current brief day. With "count sub-techniques
        // separately" on, its line is technique:T1566.002, and the drill-down's Export CSV for it was a 404.
        var day = app.Services.GetRequiredService<OrgClock>().CurrentBriefDate;
        var month = Period.Of(PeriodKind.Month, day);
        var sub = await app.CreateClient().GetAsync($"/trends/articles.csv?p={month.Key}&line=technique:T1566.002&subs=true");
        sub.StatusCode.Should().Be(HttpStatusCode.OK);
        (await sub.Content.ReadAsStringAsync()).Should().Contain("Invoice-themed phishing wave");
        (await app.CreateClient().GetAsync($"/trends/articles.csv?p={month.Key}&line=technique:T1566")).StatusCode
            .Should().Be(HttpStatusCode.OK, "rolled up, as the page counts by default, the parent's line holds it");
    }
}
