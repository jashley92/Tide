namespace Tide.TestSupport;

/// <summary>
/// Waits for something another thread does (a call's thread starting, a late answer landing), checking often, rather
/// than sleeping a fixed time and hoping it was long enough: a loaded machine can take far longer than a quiet one.
/// </summary>
public static class Poll
{
    /// <summary>Waits until <paramref name="done"/> holds, giving up after <paramref name="seconds"/> so the assertion
    /// after can say what's wrong.</summary>
    public static async Task<bool> Until(Func<bool> done, int seconds = 15)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!done())
        {
            if (DateTime.UtcNow > until) return false;
            await Task.Delay(10);
        }
        return true;
    }
}
