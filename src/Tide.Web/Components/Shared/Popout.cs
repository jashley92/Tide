using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Tide.Web.Components.Shared;

/// <summary>
/// A popout that closes when it's done with (wwwroot/js/popout.js): while it's open, Escape anywhere on the page, a press
/// outside it, or focus moving outside it calls <paramref name="dismissed"/> to close it. After Escape focus is back on
/// its button, and the page under it didn't hear that Escape. The component calls <see cref="FollowAsync"/> after each
/// render, and disposes this with itself.
/// </summary>
public sealed class Popout(IJSRuntime js, Func<Task> dismissed) : IAsyncDisposable
{
    private DotNetObjectReference<Popout>? _self;
    private int _watch;

    /// <summary>Watches <paramref name="holder"/> (the popout and its <paramref name="opener"/>) while
    /// <paramref name="open"/>, placing it inside the page as it's drawn, and stops when it closes. Called before the
    /// popout's first control takes focus, so it's in place by then.</summary>
    public async Task FollowAsync(bool open, ElementReference holder, ElementReference opener)
    {
        if (!open)
        {
            if (_watch != 0) await StopAsync();
            return;
        }
        try
        {
            if (_watch != 0) await js.InvokeVoidAsync("tidePopout.fit", _watch);
            else
            {
                _self ??= DotNetObjectReference.Create(this);
                _watch = await js.InvokeAsync<int>("tidePopout.watch", holder, opener, _self);
            }
        }
        catch (JSDisconnectedException) { /* the tab has gone */ }
        catch (InvalidOperationException) { /* not rendered interactively */ }
    }

    /// <summary>Escape, or a press or focus outside: the script has stopped watching.</summary>
    [JSInvokable]
    public Task Dismiss()
    {
        _watch = 0;
        return dismissed();
    }

    private ValueTask StopAsync()
    {
        var watch = _watch;
        _watch = 0;
        return js.StopAsync("tidePopout.unwatch", watch);
    }

    public async ValueTask DisposeAsync()
    {
        if (_watch != 0) await StopAsync();
        _self?.Dispose();
    }
}
