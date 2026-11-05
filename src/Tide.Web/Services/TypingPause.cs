namespace Tide.Web.Services;

/// <summary>
/// A pause before acting on what's typed: a word typed is one search, not one a letter. Each keystroke awaits
/// <see cref="Settled"/>; only the last one before typing stops for <see cref="DelayMs"/> gets true. <see cref="Now"/>
/// drops a pending one, for what applies at once (Clear, Enter, closing, a search run straight away), and hands back a
/// mark that <see cref="IsLatest"/> checks, so a slower answer to an earlier search is dropped when a later one started.
/// Every picker that searches as you type uses one: the ATT&amp;CK picker, the adversary picker and "We run".
/// </summary>
public sealed class TypingPause(int delayMs = TypingPause.SearchMs)
{
    /// <summary>How long a picker waits after a keystroke before it searches.</summary>
    public const int SearchMs = 200;

    private int _latest;

    /// <summary>How long typing must stop before the work runs.</summary>
    public int DelayMs => delayMs;

    /// <summary>Waits out the pause after a keystroke: true when nothing came after it in the meantime.</summary>
    public async Task<bool> Settled()
    {
        var mine = ++_latest;
        await Task.Delay(delayMs);
        return mine == _latest;
    }

    /// <summary>Supersedes any pending keystroke (its <see cref="Settled"/> answers false), and marks the work starting
    /// now: <see cref="IsLatest"/> says whether anything has superseded it since.</summary>
    public int Now() => ++_latest;

    /// <summary>Whether nothing has come since <paramref name="mark"/> (from <see cref="Now"/>): no keystroke, and no other work.</summary>
    public bool IsLatest(int mark) => mark == _latest;
}
