using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Actions;
using Tide.Application.Articles;
using Tide.Application.Briefs;
using Tide.Application.Metrics;
using Tide.Application.Org;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Web.Components.Pages;
using Tide.Web.Services;
using static Tide.IntegrationTests.ComponentHarness;
using static Tide.IntegrationTests.PresentHarness;

namespace Tide.IntegrationTests;

/// <summary>
/// The presenter's controls are the presenter's, checked where each is handled, not only by what the page draws: someone
/// following, an analyst who could present included, changes nothing with a key or with any of the rail's steps called
/// directly (as a forged event would), on the presenter's slide or one they browsed to.
/// </summary>
public class PresentFollowerGateTests(TideApp app) : IClassFixture<TideApp>
{
    private static readonly SignIn Andy = new("dev:gate-andy", "Andy Analyst", "Analyst");

    private DateOnly Today { get; } = app.Services.GetRequiredService<OrgClock>().CurrentBriefDate;
    private LiveMeetings Meetings => app.Services.GetRequiredService<LiveMeetings>();

    private const string Line = "Hunt for the indicators in proxy logs";

    // A past day's brief with one article that has a recommended line and an action raised from it by hand.
    private async Task<(DateOnly Day, Guid Brief, Guid Article, EditionAction Action)> Brief(int daysBack)
    {
        var day = Today;
        for (var i = 0; i < daysBack; i++) day = WorkingDays.Weekdays.Previous(day);
        await using var scope = SignedIn(app, SignIn.Analyst);
        var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
        var briefs = scope.ServiceProvider.GetRequiredService<BriefService>();
        var actions = scope.ServiceProvider.GetRequiredService<ActionService>();
        var brief = await briefs.GetOrStartAsync(day);
        var id = await articles.CreateAsync($"Gate article {daysBack}", "Vendor", null, TlpLevel.Clear);
        var form = ArticleForm.From((await articles.GetAsync(id))!);
        form.Bluf = "Exploited.";
        form.RecommendedActions = Line;
        await articles.SaveAsync(form);
        await briefs.AddArticleAsync(brief.Id, id);
        var team = (await scope.ServiceProvider.GetRequiredService<TeamService>().ListAsync())[0].Id;
        var number = (await actions.QuickAddAsync(id, $"Gate action {daysBack}", ActionKind.IocSweep, team, null, day)).Number;
        var a = (await actions.GetAsync(number))!.Action;
        await actions.AssignAsync(a.Id, null, null);
        a = (await actions.GetAsync(number))!.Action;
        return (day, brief.Id, id, new EditionAction(a.Number, a.Title, a.Team!.Name, a.DueDate, a.Status, a.Id, a.TeamId, a.AssigneeId, a.AssigneeName));
    }

    private static async Task FirstArticle(Rendered<Present> r)
    {
        await r.Click(p => p.OnPresentKey("last"));
        await r.Click(p => p.OnPresentKey("prev"));
    }

    private async Task<BriefItem> Item(Guid article, DateOnly day)
    {
        await using var scope = SignedIn(app, SignIn.Analyst);
        return (await scope.ServiceProvider.GetRequiredService<BriefService>().GetAsync(day))!.Items.Single(i => i.ArticleId == article);
    }

    private async Task<ActionItem> Read(int number)
    {
        await using var scope = SignedIn(app, SignIn.Analyst);
        return (await scope.ServiceProvider.GetRequiredService<ActionService>().GetAsync(number))!.Action;
    }

    [Fact]
    public async Task AnAnalystFollowing_PressingD_MarksNothingDiscussed_OnThePresentersSlide_OrOneTheyBrowsedTo()
    {
        var (day, _, article, _) = await Brief(141);
        await using var pat = await Open(app, day);
        await Do(pat, p => p.StartMeeting());
        await using var andy = await Open(app, day, Andy);
        await Do(andy, p => p.JoinIfLive());
        await FirstArticle(pat);
        await Do(andy, p => p.Follow());

        await andy.Click(p => p.OnPresentKey("discussed"));
        (await Item(article, day)).Discussed.Should().BeFalse("following on the presenter's slide: not theirs to tick");

        await andy.Click(p => p.OnPresentKey("prev"));
        await andy.Click(p => p.OnPresentKey("next"));
        await andy.Click(p => p.OnPresentKey("discussed"));
        (await Item(article, day)).Discussed.Should().BeFalse("browsed to it themselves: still not theirs");

        // The presenter's D does it.
        await pat.Click(p => p.OnPresentKey("discussed"));
        (await Item(article, day)).Discussed.Should().BeTrue();
        EndMeeting(app, day);
    }

    [Fact]
    public async Task AnAnalystFollowing_ChangesNothing_WithAnyOfTheRailsSteps()
    {
        var (day, brief, article, action) = await Brief(142);
        await using var pat = await Open(app, day);
        await Do(pat, p => p.StartMeeting());
        await using var olive = await Open(app, day, new SignIn("dev:gate-olive", "Olive Hand", "ActionOwner"));
        await Do(olive, p => p.JoinIfLive());
        await Do(olive, p => p.ToggleHand());
        await using var andy = await Open(app, day, Andy);
        await Do(andy, p => p.JoinIfLive());
        await FirstArticle(pat);
        await Do(andy, p => p.Follow());
        var before = Meetings.Get(day)!;
        var item = await Item(article, day);

        await Do(andy, p => p.ToggleDiscussed(item));
        await Do(andy, p => p.ClearHand(null));
        await Do(andy, p => p.Summon());
        await Do(andy, p => p.ToggleLock());
        await Do(andy, p => p.HandOff(Andy.UserId));
        await Do(andy, p => p.Raise(article, Line));
        await Do(andy, p => p.TypeQuickAddTitle(article, "Forged quick add"));
        await Do(andy, p => p.QuickAdd(article));
        await Do(andy, p => p.Take(action));
        await Do(andy, p => p.Assign(action, Andy.UserId));
        await Do(andy, p => p.Carry(article, true));
        await Do(andy, p => p.SaveOpeningNote("Forged opening note"));
        await Do(andy, p => p.KeepOpeningNote());
        await Do(andy, p => p.ToggleRail());

        var after = Meetings.Get(day)!;
        after.PresenterName.Should().Be("Tester", "no hand-off");
        after.Hands.Should().HaveCount(before.Hands.Count, "no hand lowered");
        after.Locked.Should().BeFalse();
        after.Summons.Should().Be(before.Summons, "nobody brought here");
        (await Item(article, day)).Discussed.Should().BeFalse();
        var a = await Read(action.Number);
        a.AssigneeId.Should().BeNull("not taken or assigned");
        await using (var scope = SignedIn(app, SignIn.Analyst))
        {
            var rows = await scope.ServiceProvider.GetRequiredService<ActionService>().ListAsync(new ActionFilter());
            rows.Should().NotContain(r => r.Title == "Forged quick add" || r.Title == Line, "nothing raised");
            (await scope.ServiceProvider.GetRequiredService<BriefService>().GetAsync(day))!.TeamNote.Should().NotBe("Forged opening note");
        }
        andy.Component.Carried.Should().BeEmpty();
        (await andy.Html()).Should().NotContain("id=\"present-rail\"");
        EndMeeting(app, day);
    }

    [Fact]
    public async Task AnAnalystFollowing_EntersNoMissingNumber_AndChangesOnlyTheAttendanceTheyAdded()
    {
        var (day, brief, _, _) = await Brief(143);
        Guid metric;
        await using (var scope = SignedIn(app, SignIn.Analyst))
        {
            var metrics = scope.ServiceProvider.GetRequiredService<MetricsService>();
            var g = await metrics.AddGroupAsync("Gate numbers");
            metric = await metrics.SaveMetricAsync(new MetricForm { GroupId = g, Name = "Gate phish", Direction = MetricDirection.LowerIsBetter });
        }
        await using var pat = await Open(app, day);
        await Do(pat, p => p.StartMeeting());
        await using var andy = await Open(app, day, Andy);
        await Do(andy, p => p.JoinIfLive());

        await Do(andy, p => p.EnterMissingNumber(metric, "7"));
        await Do(pat, p => p.AddAttendeeName = "Sam from the SOC");
        await Do(pat, p => p.AddAttendee());
        // As anyone following may: added as theirs, and only that is theirs to take off.
        await Do(andy, p => p.AddAttendeeName = "Kim from IT");
        await Do(andy, p => p.AddAttendee());
        await using (var scope = SignedIn(app, SignIn.Analyst))
        {
            (await scope.ServiceProvider.GetRequiredService<MetricsService>().GetDayAsync(day)).Groups
                .SelectMany(g => g.Tiles).Single(t => t.Metric.Id == metric).Value.Should().BeNull("no number entered");
            var attendees = (await scope.ServiceProvider.GetRequiredService<MeetingService>().GetAsync(brief)).Attendees;
            attendees.Single(x => x.Name == "Kim from IT").AddedById.Should().Be(Andy.UserId);
            await Do(andy, p => p.RemoveAttendee(attendees.Single(x => x.Name == "Sam from the SOC")));
        }
        await using (var scope = SignedIn(app, SignIn.Analyst))
            (await scope.ServiceProvider.GetRequiredService<MeetingService>().GetAsync(brief)).Attendees.Should().Contain(x => x.Name == "Sam from the SOC", "the presenter's");
        EndMeeting(app, day);
    }
}
