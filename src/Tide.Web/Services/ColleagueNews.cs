using Tide.Application.Abstractions;

namespace Tide.Web.Services;

/// <summary>
/// One page's toasts about colleagues' saves, at most one in each <see cref="Window"/>: the first change heard is told at
/// once, and whatever arrives in the window after it is told together as the window ends, naming everyone
/// ("Sam and Robin updated the numbers."). Two people filling in a form together then see a toast now and then, not one
/// per save. Dispose with the page.
/// </summary>
public sealed class ColleagueNews : IDisposable
{
    /// <summary>The shortest gap between two of a page's toasts.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(10);

    private readonly ToastService _toasts;
    private readonly Func<string, string> _message;
    private readonly ITimer _timer;
    private readonly Lock _gate = new();
    private List<Change> _waiting = [];
    private bool _open, _disposed;

    /// <param name="toasts">The page's toasts.</param>
    /// <param name="message">The toast for who made the changes (<see cref="LiveChanges.Who"/>).</param>
    /// <param name="time">The clock the window runs on.</param>
    public ColleagueNews(ToastService toasts, Func<string, string> message, TimeProvider? time = null)
    {
        _toasts = toasts;
        _message = message;
        _timer = (time ?? TimeProvider.System).CreateTimer(_ => Close(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Colleagues' changes, heard: told now, or as the window ends if a toast went in it.</summary>
    public void Heard(IReadOnlyList<Change> changes)
    {
        string? tell = null;
        lock (_gate)
        {
            if (_disposed) return;
            if (_open) _waiting.AddRange(changes);
            else
            {
                tell = _message(LiveChanges.Who(changes));
                Open();
            }
        }
        if (tell is not null) _toasts.Tell(tell);
    }

    // Under the lock: a toast went, so the next waits for the window to end.
    private void Open()
    {
        _open = true;
        _timer.Change(Window, Timeout.InfiniteTimeSpan);
    }

    // The window ended: what was heard in it is told now, starting another; with nothing heard, the next is told at once.
    private void Close()
    {
        List<Change> heard;
        lock (_gate)
        {
            if (_disposed) return;
            _open = false;
            if (_waiting.Count == 0) return;
            heard = _waiting;
            _waiting = [];
            Open();
        }
        _toasts.Tell(_message(LiveChanges.Who(heard)));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _timer.Dispose();
    }
}
