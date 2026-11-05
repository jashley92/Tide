using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Tide.Web.Components.Shared;

/// <summary>Moving focus to an element, where a focus that can't happen isn't an error.</summary>
public static class Focus
{
    /// <summary>
    /// Focuses <paramref name="element"/>, doing nothing when the tab has gone or nothing is rendered interactively (a
    /// prerender or a test render); with <paramref name="orGone"/>, also when the element has left the page meanwhile.
    /// </summary>
    public static async Task TryFocusAsync(this ElementReference element, bool orGone = false)
    {
        try { await element.FocusAsync(); }
        catch (JSDisconnectedException) { /* the tab has gone */ }
        catch (InvalidOperationException) { /* not rendered interactively */ }
        catch (JSException) when (orGone) { /* not drawn after all */ }
    }
}
