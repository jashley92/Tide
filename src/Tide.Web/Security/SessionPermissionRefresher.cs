using System.Security.Claims;
using Tide.Application.Abstractions;
using Tide.Application.Security;
using Tide.Domain.Enums;

namespace Tide.Web.Security;

/// <summary>
/// Re-derives a signed-in principal's TIDE roles and permissions from the <em>current</em> role directory.
/// A Blazor circuit keeps the principal it started with, so without this an admin removing an AD-group mapping only
/// reached open sessions when they reconnected. Windows sign-in:
/// roles come from the user's AD groups (the group SIDs/names on the Windows identity) through the current mappings.
/// Dev sign-in: the configured roles stay, their permissions are re-read. AD group membership itself only changes
/// with a new Windows logon — that's the OS token, not something an app can refresh.
/// </summary>
public sealed class SessionPermissionRefresher
{
    private readonly Func<IRoleDirectory> _directoryFactory;
    private readonly bool _windowsMode;
    private readonly SidNames? _sids;

    public SessionPermissionRefresher(IRoleDirectory directory, bool windowsMode)
        : this(() => directory, windowsMode) { }

    /// <param name="directory">
    /// Resolved on first use: the directory loads itself through a DbContext whose audit interceptor needs the current
    /// user, whose auth-state provider needs this — taking it eagerly deadlocks the container at startup.
    /// </param>
    /// <param name="sids">The group-name lookup (tests stand in for the directory here); the sign-in's by default.</param>
    public SessionPermissionRefresher(Func<IRoleDirectory> directory, bool windowsMode, SidNames? sids = null)
    {
        _directoryFactory = directory;
        _windowsMode = windowsMode;
        _sids = sids;
    }

    private IRoleDirectory Directory => _directoryFactory();

    /// <summary>
    /// The principal with its roles and permissions brought up to date, or <c>null</c> when nothing changed (so the
    /// caller doesn't churn the auth state). Identity claims (name, SID, UPN, groups, the email) are kept as they were.
    /// </summary>
    public ClaimsPrincipal? Refresh(ClaimsPrincipal principal) =>
        principal.Identity?.IsAuthenticated != true ? null
            : Apply(principal, _windowsMode ? RoleClaimsTransformer.GroupCandidates(principal, _sids) : null);

    /// <summary>As <see cref="Refresh"/>, without holding the caller's thread while group names are looked up.</summary>
    public async Task<ClaimsPrincipal?> RefreshAsync(ClaimsPrincipal principal) =>
        principal.Identity?.IsAuthenticated != true ? null
            : Apply(principal, _windowsMode ? await RoleClaimsTransformer.GroupCandidatesAsync(principal, _sids) : null);

    private ClaimsPrincipal? Apply(ClaimsPrincipal principal, RoleClaimsTransformer.GroupNames? groups)
    {
        var heldRoles = AppRoleClaims(principal).Select(c => c.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var heldPerms = principal.FindAll(AppClaimTypes.Permission).Select(c => c.Value).ToHashSet(StringComparer.Ordinal);

        IReadOnlySet<string> roles = groups switch
        {
            null => heldRoles,
            { Complete: true } => Directory.RolesForGroups(groups.Names),
            _ => KeptOnPartialAnswer(heldRoles, groups.Names),
        };
        var perms = Directory.PermissionsForRoles(roles);
        var permNames = perms.Select(p => p.ToString()).ToHashSet(StringComparer.Ordinal);

        if (heldRoles.SetEquals(roles) && heldPerms.SetEquals(permNames)) return null;

        // Keep each authenticated identity minus the app-role and permission claims, then add the current ones.
        var identities = principal.Identities
            .Where(i => i.IsAuthenticated)
            .Select(i => new ClaimsIdentity(
                i.Claims.Where(c => c.Type != AppClaimTypes.Permission && !IsAppRoleClaim(c)).Select(c => c.Clone()),
                i.AuthenticationType, i.NameClaimType, i.RoleClaimType))
            .ToList();
        var granted = new ClaimsIdentity { Label = RoleClaimsTransformer.GrantsLabel };
        // The email address the sign-in added from AD rides on the grants identity; it's kept, not re-derived.
        foreach (var e in principal.Identities.Where(i => !i.IsAuthenticated && i.Label == RoleClaimsTransformer.GrantsLabel)
                     .SelectMany(i => i.FindAll(ClaimTypes.Email)))
            granted.AddClaim(e.Clone());
        foreach (var r in roles) granted.AddClaim(new Claim(ClaimTypes.Role, r));
        foreach (var p in permNames) granted.AddClaim(new Claim(AppClaimTypes.Permission, p));
        identities.Add(granted);
        return new ClaimsPrincipal(identities);
    }

    // A group whose name didn't come back in time would drop its name-mapped roles from an open session until the next
    // re-check, so a partial answer takes away only what it can rule out (skipping the refresh instead would leave an
    // admin's revocation unapplied for a whole AD outage). A held role stays while the groups that were named still map
    // to it, or any mapping by name does (an unnamed group may be that one); it's never added on a partial answer.
    private HashSet<string> KeptOnPartialAnswer(HashSet<string> held, IReadOnlySet<string> names)
    {
        var granted = Directory.RolesForGroups(names);
        var byName = Directory.RolesMappedByName;
        return held.Where(r => granted.Contains(r) || byName.Contains(r)).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private IEnumerable<Claim> AppRoleClaims(ClaimsPrincipal p) => p.FindAll(ClaimTypes.Role).Where(IsAppRoleClaim);

    // A TIDE role, as opposed to a Windows group SID carried as a role claim.
    private bool IsAppRoleClaim(Claim c) =>
        c.Type == ClaimTypes.Role && (RoleDefinitions.TryParseRole(c.Value, out _) || Directory.IsRole(c.Value));
}
