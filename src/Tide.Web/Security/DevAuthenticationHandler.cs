using System.Security.Claims;
using System.Text.Encodings.Web;
using Tide.Application.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Tide.Web.Security;

public sealed class DevAuthOptions
{
    public string UserId { get; set; } = "S-1-5-21-DEV-1001";
    public string DisplayName { get; set; } = "Dev Analyst";
    public string Upn { get; set; } = "dev.analyst@example.com";
    public string Email { get; set; } = "dev.analyst@example.com";

    /// <summary>
    /// Roles granted to the dev user (role names), from <c>DevAuth:Roles</c>; appsettings.Development.json grants
    /// Analyst and Admin. No default here: the configuration binder appends to an array's initial value rather than
    /// replacing it, so a code default would make it impossible to sign in with fewer roles (to try a Reader, say).
    /// </summary>
    public string[] Roles { get; set; } = [];
}

/// <summary>
/// Development-only authentication: signs every request in as a fixed user so the app is usable
/// without a domain controller. NEVER enabled in production (guarded by Auth:Mode = "Windows").
/// </summary>
public sealed class DevAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "Dev";

    /// <summary>The longest <c>?as=</c> name taken.</summary>
    public const int MaxAsNameLength = 64;
    private readonly DevAuthOptions _dev;

    public DevAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IOptions<DevAuthOptions> dev)
        : base(options, logger, encoder)
    {
        _dev = dev.Value;
    }

    /// <summary>
    /// Dev-only: <c>?roles=Viewer</c> (or a comma list) on a page load signs this browser in with those roles instead of
    /// <c>DevAuth:Roles</c>, kept in a cookie like <c>?as=</c>, so one dev server can show an Analyst presenting in one
    /// browser and a Visitor following in another. Only TIDE's role names are taken; <c>?roles=default</c> goes back.
    /// </summary>
    private string[]? RolesAsked()
    {
        const string cookieName = "dev_roles";
        var asked = Context.Request.Query["roles"].ToString();
        if (asked.Length > 0)
        {
            var named = ParseRoles(asked);
            var cookie = new CookieOptions
            {
                HttpOnly = true, Secure = true, SameSite = SameSiteMode.Lax, IsEssential = true,
                Path = Context.Request.PathBase.HasValue ? Context.Request.PathBase.Value : "/",
            };
            if (named.Length == 0) Context.Response.Cookies.Delete(cookieName, cookie);
            else Context.Response.Cookies.Append(cookieName, string.Join(',', named), cookie);
            return named.Length == 0 ? null : named;
        }
        var kept = ParseRoles(Context.Request.Cookies[cookieName]);
        return kept.Length == 0 ? null : kept;
    }

    /// <summary>The role names in a comma list that are TIDE's, by their own names; anything else is dropped.</summary>
    internal static string[] ParseRoles(string? list) =>
        (list ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(n => RoleDefinitions.TryParseRole(n, out var r) ? r.ToString() : null).OfType<string>().Distinct().ToArray();

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // Dev-only convenience: `?as=<name>` on the initial page load signs in as a distinct throwaway identity (same
        // roles), so several people can be tried on one box. Only ever reached under Auth:Mode!=Windows; Production
        // forbids this handler entirely (AuthModeGuard), so it can never leak to prod.
        var userId = _dev.UserId;
        var displayName = _dev.DisplayName;
        var upn = _dev.Upn;
        var email = _dev.Email;

        // `?as=<name>` on the initial page GET pins the identity for this browser via a cookie, so the
        // Blazor circuit's websocket (which carries no query string) resolves to the same person.
        const string cookieName = "dev_as";
        var asName = Context.Request.Query["as"].ToString();
        // A name, not a payload: letters, digits, spaces and a few punctuation marks, and no longer than a display name.
        asName = new string(asName.Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '.' or '\'').ToArray());
        if (asName.Length > MaxAsNameLength) asName = asName[..MaxAsNameLength];
        if (!string.IsNullOrWhiteSpace(asName))
            // Secure=true is safe even though dev runs over http://localhost: browsers treat localhost as
            // a secure context and still send the cookie. This handler is dev-only (Production forbids it),
            // so it never sees a non-localhost HTTP origin.
            Context.Response.Cookies.Append(cookieName, asName.Trim(),
                new CookieOptions
                {
                    HttpOnly = true, Secure = true, SameSite = SameSiteMode.Lax, IsEssential = true,
                    Path = Context.Request.PathBase.HasValue ? Context.Request.PathBase.Value : "/",
                });
        else
            asName = Context.Request.Cookies[cookieName] ?? "";

        if (!string.IsNullOrWhiteSpace(asName))
        {
            displayName = asName.Trim();
            userId = "dev:" + displayName.ToLowerInvariant();
            upn = email = displayName.Replace(" ", ".").ToLowerInvariant() + "@example.com";
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId),
            new(ClaimTypes.Name, displayName),
            new(ClaimTypes.Upn, upn),
            new(ClaimTypes.Email, email),
        };
        var roles = RolesAsked() ?? _dev.Roles;
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));

        foreach (var perm in RoleDefinitions.PermissionsForRoleNames(roles))
            claims.Add(new Claim(AppClaimTypes.Permission, perm.ToString()));

        var identity = new ClaimsIdentity(claims, SchemeName, ClaimTypes.Name, ClaimTypes.Role);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
