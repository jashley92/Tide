using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Briefs;
using Tide.Web.Services;
using static Tide.IntegrationTests.ComponentHarness;
using static Tide.IntegrationTests.PresentHarness;

namespace Tide.IntegrationTests;

/// <summary>
/// Bug round 5, present mode: following the presenter's scroll across a hand-off, the tag panel and a late join.
/// </summary>
public class PresentBugRound5Tests(MeetingClockApp app) : IClassFixture<MeetingClockApp>
{
    private static readonly SignIn Robin = new("dev:round5-robin", "Robin Analyst", "Analyst");
    private static readonly SignIn Owner = new("dev:round5-owner", "Olive Owner", "ActionOwner");

    private LiveMeetings Meetings => app.Services.GetRequiredService<LiveMeetings>();

    // The presenter's scroll as another page hears it.
    private MeetingScroll Said(DateOnly day, double f) =>
        new(day, Guid.NewGuid(), Meetings.Get(day)!.SlideKey, Meetings.Get(day)!.Slide, f);

    private async Task<Guid> FirstArticleId(DateOnly day)
    {
        await using var scope = SignedIn(app, SignIn.Analyst);
        var brief = await scope.ServiceProvider.GetRequiredService<BriefService>().GetAsync(day);
        return brief!.Items.OrderBy(i => i.SortOrder).First().ArticleId;
    }

    [Fact]
    public async Task APauseFromBeforePresenting_IsOver_WhenTheyFollowAgain()
    {
        var day = await TwoArticleBrief(app, 151, "Round5");
        await using var pat = await Open(app, day);
        await Do(pat, p => p.StartMeeting());
        await Do(pat, p => p.OnPresentKey("next"));
        await using var robin = await Open(app, day, Robin);
        await Do(robin, p => p.JoinIfLive());
        await Do(robin, p => p.OnSlideScrolled(0.2));
        robin.Component.ScrollPaused.Should().BeTrue();

        // Handed the meeting, Robin presents on the same slide, then hands it back without scrolling as a follower.
        await Do(pat, p => p.HandOff(new ChangeEventArgs { Value = Robin.UserId }));
        await Do(robin, p => p.Follow());
        await Do(robin, p => p.HandOff(new ChangeEventArgs { Value = SignIn.Analyst.UserId }));

        robin.Component.ScrollPaused.Should().BeFalse("they haven't scrolled by hand since they followed again");
        (await robin.Html()).Should().NotContain("tide-slide-back");
        await Do(robin, p => p.HeardScroll(Said(day, 0.7)));
        robin.Component.ScrolledTo.Should().Be(0.7, "following the presenter's scroll");
        EndMeeting(app, day);
    }

    [Fact]
    public async Task APresenterWhoHandsOff_AndIsHandedItBack_StartsUnpaused()
    {
        var day = await TwoArticleBrief(app, 152, "Round5");
        await using var pat = await Open(app, day);
        await Do(pat, p => p.StartMeeting());
        await Do(pat, p => p.OnPresentKey("next"));
        await using var robin = await Open(app, day, Robin);
        await Do(robin, p => p.JoinIfLive());

        // Pat, following Robin, scrolls by hand; Robin hands the meeting back and Pat hands it on again.
        await Do(pat, p => p.HandOff(new ChangeEventArgs { Value = Robin.UserId }));
        await Do(robin, p => p.Follow());
        await Do(pat, p => p.OnSlideScrolled(0.3));
        pat.Component.ScrollPaused.Should().BeTrue();
        await Do(robin, p => p.HandOff(new ChangeEventArgs { Value = SignIn.Analyst.UserId }));
        await Do(pat, p => p.Follow());
        await Do(pat, p => p.HandOff(new ChangeEventArgs { Value = Robin.UserId }));
        await Do(robin, p => p.Follow());

        pat.Component.ScrollPaused.Should().BeFalse();
        EndMeeting(app, day);
    }

    [Fact]
    public async Task HandedTheMeeting_ThePageTellsTheRoomWhereItIs()
    {
        var day = await TwoArticleBrief(app, 153, "Round5");
        await using var pat = await Open(app, day);
        await Do(pat, p => p.StartMeeting());
        await Do(pat, p => p.OnPresentKey("next"));
        await using var robin = await Open(app, day, Robin);
        await Do(robin, p => p.JoinIfLive());
        robin.Component.SharedPlace.Should().BeFalse();

        await Do(pat, p => p.HandOff(new ChangeEventArgs { Value = Robin.UserId }));
        await Do(robin, p => p.Follow());
        robin.Component.SharedPlace.Should().BeTrue("the meeting's place restarts at the top, but Robin's page is where it was");
        pat.Component.SharedPlace.Should().BeFalse();

        // Where the page then says it is goes to the room, and to anyone joining late.
        await Do(robin, p => p.OnSlideScrolled(0.45));
        Meetings.Get(day)!.Scroll.Should().Be(0.45);
        EndMeeting(app, day);
    }

    [Fact]
    public async Task TakingOver_ThePageTellsTheRoomWhereItIs()
    {
        var day = await TwoArticleBrief(app, 154, "Round5");
        var pat = await Open(app, day);
        await Do(pat, p => p.StartMeeting());
        await using var robin = await Open(app, day, Robin);
        await Do(robin, p => p.JoinIfLive());
        await pat.DisposeAsync();
        app.Clock.Now += LiveMeetings.Grace + TimeSpan.FromSeconds(1);
        await Do(robin, p => p.Follow());

        await Do(robin, p => p.TakeOver());
        Meetings.Get(day)!.PresenterId.Should().Be(Robin.UserId);
        robin.Component.SharedPlace.Should().BeTrue();
        EndMeeting(app, day);
    }

    [Fact]
    public async Task ClosingTheTagPanel_GoesToWhereThePresenterScrolledMeanwhile()
    {
        var day = await TwoArticleBrief(app, 155, "Round5");
        var first = await FirstArticleId(day);
        await using var pat = await Open(app, day);
        await Do(pat, p => p.StartMeeting());
        await Do(pat, p => p.OnPresentKey("next"));
        await using var robin = await Open(app, day, Robin);
        await Do(robin, p => p.JoinIfLive());
        await Do(robin, p => p.CatchUpScrollAsync());   // as the page does once it's drawn the slide
        robin.Component.ScrolledTo.Should().BeNull("the presenter is at the top");

        await Do(robin, p => p.EditTags(first));
        await Do(robin, p => p.CatchUpScrollAsync());
        await Do(pat, p => p.OnSlideScrolled(0.6));
        await Do(robin, p => p.HeardScroll(Said(day, 0.6)));
        robin.Component.ScrolledTo.Should().BeNull("the slide stays put under the panel");

        await Do(robin, p => p.CloseTags(false));
        await Do(robin, p => p.CatchUpScrollAsync());
        robin.Component.ScrolledTo.Should().Be(0.6, "the panel closed: back with the presenter, who scrolled meanwhile");
        robin.Component.ScrollPaused.Should().BeFalse();
        EndMeeting(app, day);
    }

    [Fact]
    public async Task ALateJoiner_OnTheSlideThePageOpenedOn_LandsAtThePresentersPlace()
    {
        var day = await TwoArticleBrief(app, 156, "Round5");
        await using var pat = await Open(app, day);
        await Do(pat, p => p.StartMeeting());   // on the title slide, which a long agenda makes scroll
        await Do(pat, p => p.OnSlideScrolled(0.4));

        await using var owner = await Open(app, day, Owner);
        await Do(owner, p => p.CatchUpScrollAsync());   // the first render, before the page is live and seated
        await Do(owner, p => p.GoLive());
        await Do(owner, p => p.CatchUpScrollAsync());
        owner.Component.ScrolledTo.Should().Be(0.4, "seated on the presenter's slide, the one it opened on");
        EndMeeting(app, day);
    }

    [Fact]
    public async Task SpaceAndPageDown_PageAPanelOpenOverTheSlide_NotTheSlideBehindIt()
    {
        var js = await app.CreateClient().GetStringAsync("/js/present.js");
        js.Should().Contain("const el = pagedBody(), forward")
            .And.Contain(".tide-present-action > .tide-present-action-body")
            .And.Contain("let intentAt = -Infinity", "a scroll in the page's first moments isn't taken for the person's own")
            .And.Contain("share: function ()");
    }
}
