using System.Security.Claims;
using Tide.Application.Abstractions;
using Tide.Application.Security;
using Tide.Domain.Enums;

namespace Tide.Web.Security;

/// <summary>
/// After authentication, records the signed-in user into the directory mirror (<c>AppUser</c>) so their
/// stable id resolves to a display name / email for the activity feed, audit view, and assignment picker.
/// The directory throttles writes, so this is cheap on every request.
/// </summary>
public sealed class UserMirrorMiddleware
{
    private readonly RequestDelegate _next;

    public UserMirrorMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, IUserDirectory directory, ICurrentUser currentUser, ILoggerFactory loggers)
    {
        // The request's user and this host's access and error logs are ambient for the rest of the pipeline (a page
        // prerendering, an endpoint), for a refusal or a failure logged where it's shown.
        using var ambient = AmbientUser.Begin(currentUser, loggers);
        // The process-long directory lookups log through the serving host (cheap: the factory caches its loggers).
        DirectoryLog.Use(loggers.CreateLogger(DirectoryLog.Category));
        var principal = context.User;
        if (principal.Identity?.IsAuthenticated == true)
        {
            var userId = CurrentUser.IdOf(principal);

            // Not from a request whose groups weren't all named in time. Its roles are short (fail closed for the
            // request), and stored they'd stand for the person's roles until the next touch, 30 minutes on: the calendar feed
            // and the recipient fallback read them. Skipped, the next whole answer writes the row.
            if (!string.IsNullOrWhiteSpace(userId) && !RoleClaimsTransformer.GroupsIncomplete(principal))
            {
                // Store the real display name (AD lookup for DOMAIN\sam), not the domain-qualified logon.
                // Bounded, and awaited so a slow domain controller doesn't hold the request's thread. The bare username
                // it falls back to is a stand-in: it doesn't replace a name already recorded, as a missing email doesn't.
                var (name, standIn) = await WindowsNames.Shared.ResolveForMirrorAsync(principal.Identity.Name);
                var upn = principal.FindFirstValue(ClaimTypes.Upn);
                var email = principal.FindFirstValue(ClaimTypes.Email);

                // Keep only TIDE role names; Windows group SIDs carried as role claims are dropped. De-duped since a
                // principal can carry the same role claim more than once.
                var roles = string.Join(',', principal.FindAll(ClaimTypes.Role)
                    .Select(c => c.Value)
                    .Where(v => RoleDefinitions.TryParseRole(v, out _))
                    .Distinct(StringComparer.OrdinalIgnoreCase));

                // S8949: flow the request's cancellation token — the mirror is a best-effort, throttled
                // convenience write, so abandon it if the client disconnects rather than doing dead work.
                await directory.TouchAsync(userId, name ?? userId, upn, email, roles, standIn, context.RequestAborted);
            }
        }

        await _next(context);
    }
}
