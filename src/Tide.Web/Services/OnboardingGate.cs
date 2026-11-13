using System.Security.Claims;

namespace Tide.Web.Services;

/// <summary>
/// Whether TIDE offers what's new, a page's tip and the first-sign-in tour by itself. It does (<c>Onboarding:Enabled</c>,
/// on by default) except on the demo the screenshot tools run (<c>Demo:Seed</c>, Development only), whose pages would
/// otherwise open under a dialog. There a browser asks for them with the dev sign-in's <c>?onboarding=on</c>. Help's own
/// buttons work either way: they're asked for.
/// </summary>
public sealed class OnboardingGate(IConfiguration config, IWebHostEnvironment env)
{
    /// <summary>The dev sign-in's claim for a browser that asked for onboarding on the demo (<c>?onboarding=on</c>).</summary>
    public const string DemoClaim = "tide:onboarding";

    private bool Demo => env.IsDevelopment() && config.GetValue<bool>("Demo:Seed");

    /// <summary>Whether <paramref name="user"/> is offered them unasked.</summary>
    public bool Automatic(ClaimsPrincipal user) =>
        config.GetValue("Onboarding:Enabled", !Demo) || Asked(user, "on") || Returning(user);

    /// <summary>Whether a browser on the demo asked to be treated as someone who used TIDE before
    /// (<c>?onboarding=returning</c>): What's new and tips, never the first-sign-in tour, whoever it signs in as.</summary>
    public bool Returning(ClaimsPrincipal user) => Asked(user, "returning");

    private bool Asked(ClaimsPrincipal user, string how) => env.IsDevelopment() && user.HasClaim(DemoClaim, how);

    /// <summary>
    /// Whether a page at <paramref name="relativePath"/> (as <c>NavigationManager.ToBaseRelativePath</c> gives it) may
    /// show them: never in present mode, whose screen the room shares, as presenter or follower.
    /// </summary>
    public static bool AllowedAt(string relativePath)
    {
        var path = relativePath.Split('?', '#')[0];
        return !(path.Equals("present", StringComparison.OrdinalIgnoreCase) || path.StartsWith("present/", StringComparison.OrdinalIgnoreCase));
    }
}
