using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Articles;
using Tide.Application.Briefs;
using Tide.Application.Org;
using Tide.Domain.Enums;
using Tide.Web.Components.Layout;
using Tide.Web.Components.Pages;
using Tide.Web.Services;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>A clock whose timers fire only when a test moves it on: the scroll throttle's wait, run by hand.</summary>
internal sealed class ManualClock(DateTimeOffset now) : TimeProvider
{
    private readonly List<ManualTimer> _timers = [];

    public DateTimeOffset Now { get; private set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var t = new ManualTimer(this, callback, state);
        t.Change(dueTime, period);
        lock (_timers) _timers.Add(t);
        return t;
    }

    /// <summary>Moves the clock on by <paramref name="by"/>, firing each timer that falls due on the way.</summary>
    public void Advance(TimeSpan by)
    {
        Now += by;
        List<ManualTimer> due;
        lock (_timers) due = _timers.Where(t => t.Due is { } d && d <= Now).ToList();
        foreach (var t in due) t.Fire();
    }

    private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
    {
        private TimeSpan _period;
        public DateTimeOffset? Due { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            _period = period;
            Due = dueTime == Timeout.InfiniteTimeSpan ? null : clock.Now + dueTime;
            return true;
        }

        public void Fire()
        {
            Due = _period == Timeout.InfiniteTimeSpan || _period == TimeSpan.Zero ? null : clock.Now + _period;
            callback(state);
        }

        public void Dispose() => Due = null;

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>The presenter's scroll in the meeting's state: kept, throttled to ten a second, reset on a new slide.</summary>
public sealed class LiveMeetingsScrollTests : IDisposable
{
    private static readonly DateOnly Day = new(2026, 9, 2);
    private readonly ManualClock _time = new(new(2026, 9, 2, 9, 0, 0, TimeSpan.Zero));
    private readonly LiveMeetings _meetings;
    private readonly List<MeetingScroll> _heard = [];
    private static readonly Guid PatsPage = Guid.NewGuid();

    public LiveMeetingsScrollTests()
    {
        _meetings = new LiveMeetings(_time);
        _meetings.Scrolled += _heard.Add;
    }

    public void Dispose() => _meetings.Dispose();

    private IMeetingSeat Start()
    {
        var pat = _meetings.Start(Day, Guid.NewGuid(), "pat", "Pat", "c-pat", PatsPage, 0);
        pat.At(2, key: "article:a");
        return pat;
    }

    private IMeetingSeat Join(string id) => _meetings.Join(Day, id, id, false, "c-" + id, Guid.NewGuid(), 0)!;

    [Fact]
    public void ThePresentersScroll_IsKept_AndPassedOn_WithTheirSlide()
    {
        var pat = Start();
        _meetings.Scroll(pat, 0.42).Should().BeTrue();

        _meetings.Get(Day)!.Scroll.Should().Be(0.42);
        _heard.Should().ContainSingle().Which.Should().Be(new MeetingScroll(Day, PatsPage, "article:a", 2, 0.42));
        _meetings.Scroll(pat, 0.42).Should().BeFalse("the same place again is no news");
        _heard.Should().HaveCount(1);
    }

    [Fact]
    public void AFollowersScroll_IsNotTheMeetings()
    {
        Start();
        var sam = Join("sam");
        _meetings.Scroll(sam, 0.5).Should().BeFalse();
        _meetings.Get(Day)!.Scroll.Should().Be(0);
        _heard.Should().BeEmpty();
    }

    [Theory]
    [InlineData(-0.3, 0)]
    [InlineData(1.7, 1)]
    [InlineData(double.NaN, 0)]
    [InlineData(0.123456, 0.1235)]
    public void TheFraction_IsKeptBetweenTopAndBottom(double given, double kept)
    {
        var pat = Start();
        _meetings.Scroll(pat, 0.9);
        _meetings.Scroll(pat, given);
        _meetings.Get(Day)!.Scroll.Should().Be(kept);
    }

    [Fact]
    public void ScrollsCloserThanATenthOfASecond_AreThrottled_AndTheLastAlwaysArrives()
    {
        var pat = Start();
        _meetings.Scroll(pat, 0.1);
        _time.Advance(TimeSpan.FromMilliseconds(20));
        _meetings.Scroll(pat, 0.2);
        _time.Advance(TimeSpan.FromMilliseconds(20));
        _meetings.Scroll(pat, 0.3);
        _heard.Select(h => h.Fraction).Should().Equal([0.1], "the two after it came too soon");
        _meetings.Get(Day)!.Scroll.Should().Be(0.3, "a page joining now still lands at the latest");

        _time.Advance(TimeSpan.FromMilliseconds(60));
        _heard.Select(h => h.Fraction).Should().Equal([0.1, 0.3], "once the tenth of a second is up, the latest is passed on, once");
        _time.Advance(TimeSpan.FromMilliseconds(500));
        _heard.Should().HaveCount(2);

        _meetings.Scroll(pat, 0.4);
        _heard.Select(h => h.Fraction).Should().Equal([0.1, 0.3, 0.4], "after a pause it goes at once");
    }

    [Fact]
    public void ThePresenterMovingOn_StartsTheNewSlideAtItsTop_AndDropsAScrollStillOwed()
    {
        var pat = Start();
        _meetings.Scroll(pat, 0.5);
        _time.Advance(TimeSpan.FromMilliseconds(10));
        _meetings.Scroll(pat, 0.7);
        pat.At(3, key: "article:b");

        _meetings.Get(Day)!.Scroll.Should().Be(0);
        _time.Advance(TimeSpan.FromSeconds(1));
        _heard.Select(h => h.Fraction).Should().Equal([0.5], "the owed scroll was the last slide's");
    }

    [Fact]
    public void ALateJoiner_SeesThePresentersPlace_AndAHandOff_StartsAtTheTop()
    {
        var pat = Start();
        _meetings.Scroll(pat, 0.6);
        var lee = _meetings.Join(Day, "lee", "Lee", true, "c-lee", Guid.NewGuid(), 0)!;
        _meetings.Get(Day)!.Scroll.Should().Be(0.6);

        _meetings.HandTo(pat, "lee").Should().BeTrue();
        _meetings.Get(Day)!.Scroll.Should().Be(0);
        _meetings.Scroll(lee, 0.2).Should().BeTrue("the new presenter's scroll is the meeting's");
        _meetings.Scroll(pat, 0.9).Should().BeFalse();
    }

    [Fact]
    public void AnEndedMeeting_SaysNoScrollItOwed()
    {
        var pat = Start();
        _meetings.Scroll(pat, 0.5);
        _time.Advance(TimeSpan.FromMilliseconds(10));
        _meetings.Scroll(pat, 0.8);
        _meetings.End(pat);
        _time.Advance(TimeSpan.FromSeconds(1));
        _heard.Should().HaveCount(1);
    }
}

/// <summary>A follower's following of the presenter's scroll, and the paging keys' decision.</summary>
public sealed class ScrollFollowTests
{
    [Fact]
    public void Following_OnTheSameSlide_GoesWhereThePresenterIs()
    {
        var f = new ScrollFollow();
        f.Heard("article:a", 0.4, "article:a", following: true, locked: false).Should().Be(0.4);
        f.Heard("article:a", 0.4, "article:b", following: true, locked: false).Should().BeNull("on another slide");
        f.Heard("article:a", 0.4, "article:a", following: false, locked: false).Should().BeNull("browsed away");
        f.Heard(null, 0.4, "article:a", following: true, locked: false).Should().BeNull();
        f.Heard("article:a", 3, "article:a", following: true, locked: false).Should().Be(1);
    }

    [Fact]
    public void ScrollingByHand_PausesForThatSlideOnly_UntilResumedOrTheSlideChanges()
    {
        var f = new ScrollFollow();
        f.ScrolledByHand("article:a").Should().BeTrue();
        f.ScrolledByHand("article:a").Should().BeFalse("already paused");
        f.ScrolledByHand(null).Should().BeFalse();
        f.Paused("article:a").Should().BeTrue();
        f.Heard("article:a", 0.5, "article:a", true, locked: false).Should().BeNull("paused");

        f.Resume();
        f.Paused("article:a").Should().BeFalse();
        f.Heard("article:a", 0.5, "article:a", true, false).Should().Be(0.5);

        f.ScrolledByHand("article:a");
        f.Paused("article:b").Should().BeFalse("the slide changed: the pause is over");
        f.Paused("article:a").Should().BeFalse("and doesn't come back with the slide");
    }

    [Fact]
    public void InALockedMeeting_ThePresentersNextScroll_EndsThePause()
    {
        var f = new ScrollFollow();
        f.ScrolledByHand("wrap");
        f.Heard("wrap", 0.3, "wrap", following: true, locked: true).Should().Be(0.3);
        f.Paused("wrap").Should().BeFalse();
    }

    [Fact]
    public void PagingKeys_ScrollTheBodyFirst_AndMoveOnlyAtItsEnd()
    {
        SlidePaging.For(atEdge: false).Should().Be(PageStep.Scroll);
        SlidePaging.For(atEdge: true).Should().Be(PageStep.Move);
        SlidePaging.Reply(PageStep.Scroll).Should().Be("scroll");
        SlidePaging.Reply(PageStep.Move).Should().Be("moved");
    }

    [Fact]
    public void TheShortcutsList_SaysSpaceScrollsFirst()
    {
        var present = Shortcuts.For("present/2026-09-01").Single(g => g.Title == "Present mode").Keys;
        present.Single(k => k.Keys.Contains("Space")).Should().Match<Shortcut>(k => k.Keys.Contains("Page Down") && k.What.Contains("Scroll"));
        present.Single(k => k.Keys.Contains("Page Up")).Keys.Should().Contain("Shift Space");
        present.Single(k => k.What == "Next slide").Keys.Should().Equal("→", "Enter");
    }
}

/// <summary>Present mode's scroll in a live meeting: the presenter's shared, a follower's own look, the markup.</summary>
public class PresentScrollTests(MeetingClockApp app) : IClassFixture<MeetingClockApp>
{
    private static readonly SignIn Owner = new("dev:scroll-owner", "Olive Owner", "ActionOwner");

    private DateOnly Today => app.Services.GetRequiredService<OrgClock>().CurrentBriefDate;
    private LiveMeetings Meetings => app.Services.GetRequiredService<LiveMeetings>();

    private async Task<DateOnly> Brief(int daysBack)
    {
        var day = Today;
        for (var i = 0; i < daysBack; i++) day = WorkingDays.Weekdays.Previous(day);
        await using var scope = SignedIn(app, SignIn.Analyst);
        var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
        var briefs = scope.ServiceProvider.GetRequiredService<BriefService>();
        var brief = await briefs.GetOrStartAsync(day);
        await briefs.AddArticleAsync(brief.Id, await articles.CreateAsync($"Scroll first {daysBack}", "Vendor", null, TlpLevel.Clear));
        await briefs.AddArticleAsync(brief.Id, await articles.CreateAsync($"Scroll second {daysBack}", "Vendor", null, TlpLevel.Clear));
        return day;
    }

    private static Task Do(Rendered<Present> r, Func<Present, Task> what) =>
        r.Click(async p =>
        {
            await what(p);
            await p.OnPresentKey("render");
        });

    private static Task Do(Rendered<Present> r, Action<Present> what) => Do(r, p =>
    {
        what(p);
        return Task.CompletedTask;
    });

    private Task<Rendered<Present>> Open(DateOnly day, SignIn? who = null) =>
        RenderAsync<Present>(app, $"/present/{day:yyyy-MM-dd}", new() { ["Day"] = $"{day:yyyy-MM-dd}" }, who);

    private void EndMeeting(DateOnly day) => Meetings.End(Meetings.Join(day, "dev:tester", "Tester", true, "", Guid.NewGuid(), 0)!);

    // The presenter's scroll as the follower's page hears it (the event comes on another page's thread).
    private MeetingScroll Said(DateOnly day, double f) =>
        new(day, Guid.NewGuid(), Meetings.Get(day)!.SlideKey, Meetings.Get(day)!.Slide, f);

    [Fact]
    public async Task TheSlide_ScrollsInItsBody_WithFadesAndAMorePill()
    {
        var day = await Brief(111);
        await using var pat = await Open(day);
        var html = await pat.Html();

        html.Should().Contain("<div class=\"tide-slide-body tide-scrollfade\" data-slide-body");
        var section = html.IndexOf("<section class=\"tide-slide\"", StringComparison.Ordinal);
        var body = html.IndexOf("tide-slide-body tide-scrollfade", section, StringComparison.Ordinal);
        var more = html.IndexOf("class=\"tide-slide-more\"", section, StringComparison.Ordinal);
        body.Should().BeGreaterThan(section);
        more.Should().BeGreaterThan(body, "the pill is in the slide, outside the part that scrolls")
            .And.BeLessThan(html.IndexOf("</section>", section, StringComparison.Ordinal));
        html.Should().Contain("data-slide-more aria-label=\"More below: scroll down\"");
        html.Should().NotContain("data-scroll-share", "nothing to share outside a meeting");
        html.Should().NotContain("tide-slide-back");

        var css = await app.CreateClient().GetStringAsync("/app.css");
        css.Should().Contain(".tide-scrollfade.is-more-above").And.Contain(".tide-scrollfade.is-more-below")
            .And.Contain(".tide-slide:has(> .tide-slide-body.is-more-below) .tide-slide-more")
            .And.Contain("container-type: size").And.Contain("var(--tide-fit, 1)").And.Contain("scrollbar-width: thin");
        var js = await app.CreateClient().GetStringAsync("/js/present.js");
        js.Should().Contain("const FLOOR = 0.8").And.Contain("'OnPageKey'").And.Contain("'OnSlideScrolled'");
    }

    [Fact]
    public async Task ThePresentersScroll_GoesToTheMeeting_AndAFollowerOnTheSlide_GoesWithIt()
    {
        var day = await Brief(112);
        await using var pat = await Open(day);
        await Do(pat, p => p.StartMeeting());
        await Do(pat, p => p.OnPresentKey("next"));
        (await pat.Html()).Should().Contain("data-scroll-share=\"present\"");

        await using var owner = await Open(day, Owner);
        await Do(owner, p => p.JoinIfLive());
        (await owner.Html()).Should().Contain("data-scroll-share=\"follow\"");

        await pat.Click(p =>
        {
            p.OnSlideScrolled(0.35);
            return Task.CompletedTask;
        });
        Meetings.Get(day)!.Scroll.Should().Be(0.35);

        await Do(owner, p => p.HeardScroll(Said(day, 0.35)));
        owner.Component.ScrolledTo.Should().Be(0.35);
        await owner.Click(p => Task.FromResult(p.CatchUpScroll().Should().Be(0.35)));
        EndMeeting(day);
    }

    [Fact]
    public async Task AFollowersScrollByHand_PausesFollowingTheScroll_UntilBackToThePresentersPlace()
    {
        var day = await Brief(113);
        await using var pat = await Open(day);
        await Do(pat, p => p.StartMeeting());
        await Do(pat, p => p.OnPresentKey("next"));
        await using var owner = await Open(day, Owner);
        await Do(owner, p => p.JoinIfLive());

        await Do(owner, p => p.OnSlideScrolled(0.8));
        owner.Component.ScrollPaused.Should().BeTrue();
        var html = await owner.Html();
        html.Should().Contain("data-scroll-paused=\"true\"").And.Contain("class=\"tide-slide-back\">Back to Tester&#x27;s place</button>");
        FollowPill(html).Should().Contain("is-following", "still following the presenter's slide");
        Meetings.Get(day)!.Scroll.Should().Be(0, "a follower's scroll isn't the meeting's");

        await pat.Click(p =>
        {
            p.OnSlideScrolled(0.5);
            return Task.CompletedTask;
        });
        await Do(owner, p => p.HeardScroll(Said(day, 0.5)));
        owner.Component.ScrolledTo.Should().BeNull("paused: the follower stays where they scrolled");
        await owner.Click(p => Task.FromResult(p.CatchUpScroll().Should().BeNull()));

        await Do(owner, p => p.BackToPlace());
        owner.Component.ScrolledTo.Should().Be(0.5, "back where the presenter is");
        owner.Component.ScrollPaused.Should().BeFalse();
        (await owner.Html()).Should().NotContain("tide-slide-back");
        EndMeeting(day);
    }

    [Fact]
    public async Task ThePresenterChangingSlide_ResumesFollowingTheScroll()
    {
        var day = await Brief(114);
        await using var pat = await Open(day);
        await Do(pat, p => p.StartMeeting());
        await Do(pat, p => p.OnPresentKey("next"));
        await using var owner = await Open(day, Owner);
        await Do(owner, p => p.JoinIfLive());
        await Do(owner, p => p.OnSlideScrolled(0.8));
        owner.Component.ScrollPaused.Should().BeTrue();

        await Do(pat, p => p.OnPresentKey("next"));
        await Do(owner, p => p.Follow());
        owner.Component.ScrollPaused.Should().BeFalse("a new slide: following its scroll again");
        await Do(owner, p => p.HeardScroll(Said(day, 0.25)));
        owner.Component.ScrolledTo.Should().Be(0.25);
        EndMeeting(day);
    }

    [Fact]
    public async Task InALockedMeeting_AFollowerMayScrollByHand_AndThePresentersNextScrollBringsThemBack()
    {
        var day = await Brief(115);
        await using var pat = await Open(day);
        await Do(pat, p => p.StartMeeting());
        await using var owner = await Open(day, Owner);
        await Do(owner, p => p.JoinIfLive());
        await Do(pat, p => p.ToggleLock());
        await Do(owner, p => p.Follow());

        await Do(owner, p => p.OnSlideScrolled(0.6));
        owner.Component.ScrollPaused.Should().BeTrue("looking around is allowed");
        await Do(owner, p => p.HeardScroll(Said(day, 0.1)));
        owner.Component.ScrolledTo.Should().Be(0.1);
        owner.Component.ScrollPaused.Should().BeFalse();
        EndMeeting(day);
    }

    [Fact]
    public async Task SpaceAtTheBottom_GoesToTheNextSlide_AndElsewhereScrolls()
    {
        var day = await Brief(116);
        await using var pat = await Open(day);
        string? reply = null;
        await Do(pat, async p => reply = await p.OnPageKey(forward: true, atEdge: false));
        reply.Should().Be("scroll");
        (await pat.Html()).Should().Contain("aria-label=\"Slide 1 of");

        await Do(pat, async p => reply = await p.OnPageKey(forward: true, atEdge: true));
        reply.Should().Be("moved");
        (await pat.Html()).Should().Contain("aria-label=\"Slide 2 of");

        await Do(pat, async p => reply = await p.OnPageKey(forward: false, atEdge: true));
        (await pat.Html()).Should().Contain("aria-label=\"Slide 1 of", "Page Up at the top goes back");
    }

    private static string FollowPill(string html)
    {
        var at = html.IndexOf("<div class=\"tide-follow ", StringComparison.Ordinal);
        return html[at..html.IndexOf("</div>", at, StringComparison.Ordinal)];
    }
}
