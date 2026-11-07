namespace Tide.Web.Components.Shared;

/// <summary>The questions the board and an action's page ask before a move, worded once.</summary>
public static class ActionPrompts
{
    /// <summary>"Close anyway (2 open)": the button that closes a parent whose parts are still open.</summary>
    public static string CloseAnyway(int open) => $"Close anyway ({open} open)";

    /// <summary>
    /// Asks before a parent is closed while <paramref name="open"/> of its parts are still open: they stay open, each its
    /// team's. True to close it anyway.
    /// </summary>
    public static Task<bool> ConfirmCloseParent(ConfirmDialog dialog, int number, int open) =>
        dialog.Ask($"Close {Ui.ActionLabel(number)} with parts still open?",
            $"{open} of its parts {(open == 1 ? "is" : "are")} still open. {(open == 1 ? "It stays" : "They stay")} open, with {(open == 1 ? "its" : "their")} teams.",
            CloseAnyway(open), danger: false);
}
