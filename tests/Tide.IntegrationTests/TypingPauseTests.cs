using FluentAssertions;
using Tide.Web.Components.Shared;
using Tide.Web.Services;

namespace Tide.IntegrationTests;

/// <summary>The pause before a search acts on what's typed (the ATT&amp;CK picker's filter, the adversary and We run
/// pickers' searches): once per pause, the last keystroke wins, what applies at once drops the pending one, and a slower
/// answer to an earlier search is dropped.</summary>
public class TypingPauseTests
{
    [Fact]
    public void Every_picker_waits_200ms_by_default() =>
        new TypingPause().DelayMs.Should().Be(TypingPause.SearchMs).And.Be(200);

    // The pauses run on a ManualClock the test moves on, so a busy machine's slow timers can't put a keystroke after
    // the pause it was meant to fall within.
    private readonly ManualClock _clock = new();

    [Fact]
    public async Task A_pause_after_one_keystroke_applies_it_once()
    {
        var pause = new TypingPause(20, _clock);
        var settled = pause.Settled();
        _clock.Advance(TimeSpan.FromMilliseconds(19));
        settled.IsCompleted.Should().BeFalse("the pause hasn't passed");
        _clock.Advance(TimeSpan.FromMilliseconds(1));
        (await settled).Should().BeTrue();
    }

    [Fact]
    public async Task A_keystroke_within_the_pause_supersedes_the_one_before()
    {
        var pause = new TypingPause(150, _clock);
        var first = pause.Settled();
        _clock.Advance(TimeSpan.FromMilliseconds(30));
        var second = pause.Settled();
        var third = pause.Settled();
        _clock.Advance(TimeSpan.FromMilliseconds(150));
        (await Task.WhenAll(first, second, third)).Should().Equal(false, false, true);
    }

    [Fact]
    public async Task Applying_at_once_drops_the_pending_keystroke()
    {
        var pause = new TypingPause(100, _clock);
        var pending = pause.Settled();
        pause.Now(); // Clear, Enter or closing the picker
        _clock.Advance(TimeSpan.FromMilliseconds(100));
        (await pending).Should().BeFalse();
        // The next keystroke pauses again as usual.
        var next = pause.Settled();
        _clock.Advance(TimeSpan.FromMilliseconds(100));
        (await next).Should().BeTrue();
    }

    [Fact]
    public async Task A_search_started_later_supersedes_an_earlier_ones_answer()
    {
        var pause = new TypingPause(50, _clock);
        var earlier = pause.Now();
        pause.IsLatest(earlier).Should().BeTrue();
        var later = pause.Now();   // a second search, before the first answered
        pause.IsLatest(earlier).Should().BeFalse("its answer is dropped");
        pause.IsLatest(later).Should().BeTrue();
        var typing = pause.Settled();   // a keystroke supersedes it too
        pause.IsLatest(later).Should().BeFalse();
        _clock.Advance(TimeSpan.FromMilliseconds(50));
        (await typing).Should().BeTrue();
    }
}
