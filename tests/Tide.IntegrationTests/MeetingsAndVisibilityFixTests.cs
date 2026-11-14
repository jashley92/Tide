using System.Net;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Tide.Application.Abstractions;
using Tide.Application.Actions;
using Tide.Application.Briefs;
using Tide.Application.Distribution;
using Tide.Application.Org;
using Tide.Application.Security;
using Tide.Application.Settings;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Web.Components.Shared;
using Tide.Web.Services;

namespace Tide.IntegrationTests;

/// <summary>
/// A meeting is never started before its brief's day (it couldn't be ended until then), and one that is (the time zone
/// moved under it) is cleared; the meeting's start and end keep to one rule.
/// </summary>
public sealed class MeetingDayTests : IDisposable
{
    // 2026-11-13, 11:00 in New York (the default zone).
    private readonly TestClock _time = new(new DateTimeOffset(2026, 11, 13, 16, 0, 0, TimeSpan.Zero));
    private readonly LiveMeetings _meetings;
    private readonly OrgClock _clock;

    public MeetingDayTests()
    {
        _clock = new OrgClock(Options.Create(new OrgOptions()), _time);
        _meetings = new LiveMeetings(_time, _clock);
    }

    public void Dispose()
    {
        _meetings.Dispose();
        _clock.Dispose();
    }

    private static readonly DateOnly Today = new(2026, 11, 13), Monday = new(2026, 11, 16);

    private IMeetingSeat Start(DateOnly day) => _meetings.Start(day, Guid.NewGuid(), "pat", "Pat", "c-pat", Guid.NewGuid(), 0);

    [Fact]
    public void AFutureDaysMeeting_IsRefusedAtTheStart_WithTheEndsOwnWords()
    {
        FluentActions.Invoking(() => Start(Monday)).Should().Throw<ArgumentException>().WithMessage(MeetingDay.TooEarly);
        _meetings.Get(Monday).Should().BeNull("nothing went live, so nothing lingers on every page");
        _meetings.Live().Should().BeEmpty();

        Start(Today);
        _meetings.Get(Today).Should().NotBeNull("today's meeting starts as before");
        Start(Today.AddDays(-1)).Should().NotBeNull("a past day's meeting can be held late");
    }

    [Fact]
    public void AMeetingWhoseDayHasntCome_IsCleared_ForItCouldntBeEnded()
    {
        Start(Today);
        // The organization's day moves back under it (its time zone changed): ending it would be refused until then.
        _time.Now = _time.Now.AddDays(-1);
        var heard = new List<MeetingChange>();
        _meetings.Changed += heard.Add;

        _meetings.Sweep();

        _meetings.Get(Today).Should().BeNull();
        heard.Should().ContainSingle().Which.Day.Should().Be(Today, "every page showing it hears it went");
    }

    [Fact]
    public void TheMeetingDayRule_IsTheOrganizationsDay()
    {
        // 23:30 on Thursday in New York is Friday in UTC: still Thursday for the rule.
        var lateThursday = new DateTimeOffset(2026, 11, 13, 4, 30, 0, TimeSpan.Zero);
        MeetingDay.Before(_clock, Today, lateThursday).Should().BeTrue();
        MeetingDay.Before(_clock, Today, lateThursday.AddHours(1)).Should().BeFalse();
    }
}

/// <summary>/actions/new is for those who may create an action; everyone else gets the access-denied page.</summary>
public sealed class NewActionAccessTests
{
    [Theory]
    [InlineData("Viewer")]
    [InlineData("ActionOwner")]
    public async Task SomeoneWhoCantCreateAnAction_GetsTheAccessDeniedPage(string role)
    {
        using TideApp app = role == "Viewer" ? new ViewerApp() : new ActionOwnerApp();
        var page = await app.CreateClient(new() { AllowAutoRedirect = false }).GetAsync("/actions/new?article=" + Guid.NewGuid());
        page.StatusCode.Should().Be(HttpStatusCode.Redirect);
        page.Headers.Location!.OriginalString.Should().StartWith("/access-denied?from=");
    }

    [Fact]
    public async Task AnAnalyst_GetsTheNewActionForm()
    {
        using var app = new TideApp();
        var html = await app.CreateClient().GetStringAsync("/actions/new");
        html.Should().Contain("<h1>New action</h1>").And.NotContain("Only analysts create actions.");
    }
}

/// <summary>Smaller fixes: the list's group headings as they're heard, the board's All open, and a top bar that goes
/// quietly with its circuit.</summary>
public sealed partial class SmallFixesTests(TideApp app) : IClassFixture<TideApp>
{
    [Fact]
    public async Task AGroupsHeading_IsHeardWithAStopBetweenItsCountAndItsNote()
    {
        var today = app.Services.GetRequiredService<OrgClock>().Today;
        var n = 0;
        ActionRow Row(string title) => new(Guid.NewGuid(), ++n, title, ActionKind.Other, Priority.P3, Guid.Empty, "SOC", null, null,
            today.AddDays(-3), ActionStatus.Open, ActionOutcome.None, null, null, null, null, 0, DateTimeOffset.UtcNow);
        var rows = Enumerable.Range(0, 33).Select(i => Row($"Late {i}")).ToList();

        await using var r = await ComponentHarness.RenderAsync<ActionTable>(app, "/actions?view=open", new()
        {
            ["Rows"] = (IReadOnlyList<ActionRow>)rows, ["Today"] = today, ["GroupByUrgency"] = true,
            ["GroupsUnowned"] = (IReadOnlyDictionary<string, int>)new Dictionary<string, int> { ["Overdue"] = 3 },
        });
        var heading = HeadingText(await r.Html());
        heading.Should().Be("Overdue 33. 3 have no owner", "the count and the note are heard apart, not as \"33 3\"");
    }

    // The group heading's text as assistive tech reads it: its tags dropped, its spaces collapsed.
    private static string HeadingText(string html)
    {
        var th = GroupHeading().Match(html).Groups["inner"].Value;
        return Spaces().Replace(WebUtility.HtmlDecode(Tags().Replace(th, "")), " ").Trim();
    }

    [GeneratedRegex("<th colspan=\"\\d+\" scope=\"rowgroup\">(?<inner>[\\s\\S]*?)</th>")]
    private static partial Regex GroupHeading();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex("\\s+")]
    private static partial Regex Spaces();

    [Fact]
    public async Task TheBoardsAllOpen_ShowsOpenWorkingStatusesOnly_AsTheListsAllOpenDoes()
    {
        var token = $"allopen{Guid.NewGuid():N}"[..14];
        await using var scope = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst);
        var sp = scope.ServiceProvider;
        var actions = sp.GetRequiredService<ActionService>();
        var team = await sp.GetRequiredService<TeamService>().AddAsync($"{token} team", null, null);
        foreach (var status in new[] { ActionStatus.Open, ActionStatus.InProgress, ActionStatus.Blocked, ActionStatus.Tracking, ActionStatus.Done, ActionStatus.Cancelled })
        {
            var id = (await actions.GetAsync(await actions.CreateAsync(new ActionForm { Title = $"{token} {status}", TeamId = team, Kind = ActionKind.Block })))!.Action.Id;
            if (status != ActionStatus.Open)
                await actions.SetStatusAsync(id, status, status == ActionStatus.Done ? ActionOutcome.Blocked : ActionOutcome.None, "moved");
        }

        var filter = new ActionFilter(ActionView.Open) { Search = token };
        var board = await actions.BoardAsync(filter);
        board.Cards.Select(c => c.Status).Should().BeEquivalentTo([ActionStatus.Open, ActionStatus.InProgress, ActionStatus.Blocked, ActionStatus.Tracking],
            "All open is open working statuses, never a closed one");
        (await actions.ViewCountsAsync(filter, closedDays: ActionService.BoardClosedDays)).Open.Should().Be(board.Cards.Count, "the tab counts what the board shows");
    }

    [Fact]
    public async Task TheBriefChip_GoesQuietly_WhenItsCircuitHasGoneUnderARead()
    {
        await using var scope = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst);
        var sp = scope.ServiceProvider;
        var log = new ListLogger<BriefChipSource>();
        var source = new BriefChipSource(sp.GetRequiredService<BriefService>(), sp.GetRequiredService<DistributionService>(),
            sp.GetRequiredService<DistributionSettings>(), sp.GetRequiredService<OrgClock>(), sp.GetRequiredService<ICurrentUser>(),
            sp.GetRequiredService<ChangeFeed>(), log);

        // The circuit's services are disposed under the read (a quick navigation on SQL Server): nothing to say.
        source.AfterBriefRead = () => throw new ObjectDisposedException("IServiceProvider");
        (await source.GetAsync()).Should().BeNull();
        log.Warnings.Should().BeEmpty("a circuit that's gone isn't a fault");

        // A real failure is still said, once.
        source.AfterBriefRead = () => throw new InvalidOperationException("the database is down");
        (await source.GetAsync()).Should().BeNull();
        log.Warnings.Should().ContainSingle();

        // And whatever fails once the source has gone is nobody's to hear.
        source.Dispose();
        (await source.GetAsync()).Should().BeNull();
        log.Warnings.Should().ContainSingle();
    }
}
