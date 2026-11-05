using FluentAssertions;
using Tide.Web.Components.Shared;
using Tide.Web.Services;

namespace Tide.IntegrationTests;

/// <summary>The pause before a search acts on what's typed (the ATT&amp;CK picker's filter): once per pause, the last keystroke
/// wins, and what applies at once drops the pending one.</summary>
public class TypingPauseTests
{
    [Fact]
    public void The_attack_picker_waits_as_long_as_the_adversary_picker() =>
        new TypingPause(ArticleAdversaryCard.TypingPauseMs).DelayMs.Should().Be(200);

    [Fact]
    public async Task A_pause_after_one_keystroke_applies_it_once()
    {
        var pause = new TypingPause(20);
        var applied = 0;
        if (await pause.Settled()) applied++;
        applied.Should().Be(1);
    }

    [Fact]
    public async Task A_keystroke_within_the_pause_supersedes_the_one_before()
    {
        var pause = new TypingPause(150);
        var first = pause.Settled();
        await Task.Delay(30);
        var second = pause.Settled();
        var third = pause.Settled();
        (await Task.WhenAll(first, second, third)).Should().Equal(false, false, true);
    }

    [Fact]
    public async Task Applying_at_once_drops_the_pending_keystroke()
    {
        var pause = new TypingPause(100);
        var pending = pause.Settled();
        pause.Now(); // Clear, Enter or closing the picker
        (await pending).Should().BeFalse();
        // The next keystroke pauses again as usual.
        (await pause.Settled()).Should().BeTrue();
    }
}
