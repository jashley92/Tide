using Microsoft.AspNetCore.Components.Web;

namespace Tide.Web.Components.Shared;

/// <summary>
/// How a combobox's keys move its highlight, alike in every one (SearchCombobox, AttachPicker): the arrows go round the
/// options, and Home and End go to the first and last when Ctrl is held or nothing is typed (otherwise they move the caret).
/// With nothing highlighted (<c>active</c> -1), down goes to the first and up to the last.
/// </summary>
public static class ComboKeys
{
    /// <summary>The option highlighted after <paramref name="e"/>, of <paramref name="count"/>; null for a key that doesn't move it.</summary>
    public static int? Move(KeyboardEventArgs e, int active, int count, bool typed) => e.Key switch
    {
        "ArrowDown" => count == 0 ? 0 : (active + 1) % count,
        "ArrowUp" => count == 0 ? 0 : (Math.Max(active, 0) - 1 + count) % count,
        "Home" when e.CtrlKey || !typed => 0,
        "End" when e.CtrlKey || !typed => Math.Max(0, count - 1),
        _ => null,
    };
}
