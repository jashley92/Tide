using System.Globalization;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Tide.Application.Api;
using Tide.Application.Security;
using Tide.Domain.Enums;

namespace Tide.Web.Security;

/// <summary>
/// Signs an API call in by its token (EP-75): <c>Authorization: Bearer tide_pat_…</c>. The caller gets the permissions
/// its scopes grant, a claim per scope (each endpoint checks its own), and its TLP ceiling. It never has a role, and the
/// AD-group transformer leaves it alone.
/// </summary>
public sealed class ApiTokenAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
    ApiTokenService tokens) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiToken";

    /// <summary>The API's scheme: a bearer token goes to <see cref="SchemeName"/>, anything else to the app's own sign-in.</summary>
    public const string ApiScheme = "ApiOrUser";

    public const string ScopeClaim = "tide:scope";
    public const string TlpClaim = "tide:tlp";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Bearer(Request) is not { } token) return AuthenticateResult.NoResult();
        if (await tokens.AuthenticateAsync(token, Context.RequestAborted) is not { } caller) return AuthenticateResult.Fail("The token isn't valid.");
        var identity = new ClaimsIdentity(SchemeName);
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, "api:" + caller.TokenId.ToString("N")));
        identity.AddClaim(new Claim(ClaimTypes.Name, caller.Name));
        foreach (var scope in caller.Scopes) identity.AddClaim(new Claim(ScopeClaim, scope));
        foreach (var permission in ApiScopes.Permissions(caller.Scopes)) identity.AddClaim(new Claim(AppClaimTypes.Permission, permission.ToString()));
        identity.AddClaim(new Claim(TlpClaim, ((int)caller.MaxTlp).ToString(CultureInfo.InvariantCulture)));
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer";
        return Task.CompletedTask;
    }

    public static string? Bearer(HttpRequest request) =>
        request.Headers.Authorization.ToString() is { } h && h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? h[7..].Trim() : null;

    /// <summary>Whether <paramref name="user"/> signed in with a token (and so is held to its scopes).</summary>
    public static bool IsToken(ClaimsPrincipal user) => user.Identities.Any(i => i.AuthenticationType == SchemeName && i.IsAuthenticated);

    /// <summary>A token caller's TLP ceiling; null for a person (who reads what their permissions allow).</summary>
    public static TlpLevel? Ceiling(ClaimsPrincipal user) =>
        IsToken(user) && int.TryParse(user.FindFirstValue(TlpClaim), NumberStyles.None, CultureInfo.InvariantCulture, out var t) && Enum.IsDefined((TlpLevel)t)
            ? (TlpLevel)t : null;
}
