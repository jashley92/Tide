using Tide.Web.Components.Shared;

namespace Tide.Web.Components.Pages.Admin;

/// <summary>The one question the admin lists (sources, teams, metrics, the scorecard) ask before retiring something.</summary>
internal static class Retiring
{
    /// <summary>
    /// Whether to go ahead. Retiring asks first, as an item nothing has used yet is deleted rather than retired;
    /// bringing one back doesn't.
    /// </summary>
    public static async Task<bool> ConfirmAsync(ConfirmDialog confirm, string name, bool isRetired) =>
        isRetired || await confirm.Ask($"Retire {name}?",
            "It's hidden from new work and kept for history. If nothing has used it yet, it's deleted instead.", "Retire");
}
