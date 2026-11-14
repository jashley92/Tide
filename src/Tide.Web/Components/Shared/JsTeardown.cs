using Microsoft.JSInterop;

namespace Tide.Web.Components.Shared;

/// <summary>Telling the page's script a component has gone, where a call that can't happen isn't an error.</summary>
public static class JsTeardown
{
    /// <summary>
    /// Calls <paramref name="identifier"/> from a component's disposal, doing nothing when the tab has gone, the circuit
    /// cancelled the call while it was shutting down, or nothing was rendered interactively (a prerender or a test render).
    /// A disposal that throws isn't caught by the page's error boundary: it ends the whole circuit ("Something went
    /// wrong. Reload the page"), so a teardown that's no longer needed must never throw.
    /// </summary>
    public static async ValueTask StopAsync(this IJSRuntime js, string identifier, params object?[]? args)
    {
        try { await js.InvokeVoidAsync(identifier, args); }
        catch (JSDisconnectedException) { /* the tab has gone */ }
        catch (OperationCanceledException) { /* the circuit cancelled it on its way down */ }
        catch (InvalidOperationException) { /* not rendered interactively */ }
    }

    /// <summary>
    /// A call nothing waits on (a nudge to the page's script): nothing when it can't happen, as <see cref="StopAsync"/>
    /// (the browser not answering in time included: its connection dropping), nor when the script refuses it.
    /// </summary>
    public static async ValueTask BestEffortAsync(this IJSRuntime js, string identifier, params object?[]? args)
    {
        try { await js.StopAsync(identifier, args); }
        catch (JSException) { /* the script refused it: nothing depends on it */ }
    }
}
