namespace Tide.Web.Services;

/// <summary>
/// A pause before acting on what's typed: a word typed is one search, not one a letter. Each keystroke awaits
/// <see cref="Settled"/>; only the last one before typing stops for <see cref="DelayMs"/> gets true. <see cref="Now"/>
/// drops a pending one, for what applies at once (Clear, Enter, closing).
/// </summary>
public sealed class TypingPause(int delayMs)
{
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

    /// <summary>Supersedes any pending keystroke: its <see cref="Settled"/> answers false.</summary>
    public void Now() => _latest++;
}
