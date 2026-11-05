using System.Security.Claims;
using Tide.Application.Abstractions;
using Tide.Application.Security;
using Tide.Domain.Enums;
using Microsoft.AspNetCore.Components.Authorization;

namespace Tide.Web.Security;

/// <summary>
/// Resolves the current user for both plain HTTP requests and interactive Blazor circuits.
/// Prefers the request principal; falls back to the circuit's authentication state.
/// </summary>
public sealed class CurrentUser : ICurrentUser
{
    private readonly IHttpContextAccessor _http;
    private readonly AuthenticationStateProvider _authState;
    private readonly IUserDirectory? _users;
    private ClaimsPrincipal? _cached;

    /// <param name="users">The user mirror, for the real name of someone AD didn't name in time (null: none).</param>
    public CurrentUser(IHttpContextAccessor http, AuthenticationStateProvider authState, IUserDirectory? users = null)
    {
        _http = http;
        _authState = authState;
        _users = users;
        // The circuit's principal can be refreshed in place (roles/permissions re-derived); drop the cached one.
        _authState.AuthenticationStateChanged += _ => _cached = null;
    }

    private ClaimsPrincipal Principal
    {
        get
        {
            // Inside a circuit the HttpContext is the connection's (the /_blazor WebSocket), whose user is fixed at
            // connect time; the circuit's auth state is the one the permission re-check refreshes, so it wins there.
            var http = _http.HttpContext;
            var inCircuit = http?.Request.Path.StartsWithSegments("/_blazor") == true;
            var fromHttp = inCircuit ? null : http?.User;
            if (fromHttp?.Identity?.IsAuthenticated == true) return fromHttp;
            if (_cached is not null) return _cached;

            try
            {
                // Works inside a Razor component circuit.
                _cached = _authState.GetAuthenticationStateAsync().GetAwaiter().GetResult().User;
            }
            catch
            {
                // Outside any auth scope (e.g. startup seeding): treat as anonymous → "system".
                _cached = new ClaimsPrincipal(new ClaimsIdentity());
            }
            return _cached;
        }
    }

    public bool IsAuthenticated => Principal.Identity?.IsAuthenticated == true;

    public string UserId => IdOf(Principal) ?? "system";

    /// <summary>The stable id a principal is known by: its SID (or the dev sign-in's id), else its UPN, else its name.</summary>
    public static string? IdOf(ClaimsPrincipal principal) =>
        principal.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? principal.FindFirstValue(ClaimTypes.Upn)
        ?? principal.Identity?.Name;

    // Windows auth only exposes DOMAIN\sam as the name; resolve the real AD display name (cached),
    // degrading to the bare username so the UI never shows the domain. Dev auth already sets a real name, and so does an
    // API token: its caller's name as issued, never looked up in AD (a "SOAR\Prod" token was named "Prod", or whoever
    // AD said, so its detection push and its revocation no longer matched, and each call waited on the lookup).
    // When AD didn't answer in time the bare username ("jdoe") is only a stand-in, and it was stored as the author of a
    // comment, an assignee's name, and so on: the name the user mirror holds for them is used instead (no AD call; the
    // mirror's names are kept in memory). Someone with none recorded yet still gets the username.
    public string DisplayName
    {
        get
        {
            var principal = Principal;
            if (ApiTokenAuthenticationHandler.IsToken(principal)) return principal.Identity?.Name ?? UserId;
            var (name, standIn) = WindowsNames.FriendlyOrStandIn(principal.Identity?.Name);
            if (standIn && _users is not null && principal.Identity?.IsAuthenticated == true && _users.RecordedName(UserId) is { } recorded)
                return recorded;
            return name ?? UserId;
        }
    }

    public string? UserPrincipalName => Principal.FindFirstValue(ClaimTypes.Upn);

    public string? Email => Principal.FindFirstValue(ClaimTypes.Email);

    public IReadOnlySet<AppRole> Roles =>
        Principal.FindAll(ClaimTypes.Role)
            .Select(c => RoleDefinitions.TryParseRole(c.Value, out var r) ? r : (AppRole?)null)
            .OfType<AppRole>()
            .ToHashSet();

    public IReadOnlySet<Permission> Permissions =>
        Principal.FindAll(AppClaimTypes.Permission)
            .Select(c => Enum.TryParse<Permission>(c.Value, out var p) ? p : (Permission?)null)
            .OfType<Permission>()
            .ToHashSet();

    public bool Has(Permission permission) =>
        Principal.HasClaim(AppClaimTypes.Permission, permission.ToString());
}
