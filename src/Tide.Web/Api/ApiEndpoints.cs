using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Tide.Application.Actions;
using Tide.Application.Api;
using Tide.Application.Articles;
using Tide.Application.Org;
using Tide.Application.Security;
using Tide.Domain.Common;
using Tide.Web.Security;

namespace Tide.Web.Api;

/// <summary>
/// TIDE's API (EP-75), under <c>/api/v1</c>, described at <c>/api/v1/openapi.json</c>. A caller signs in with an API token
/// (<c>Authorization: Bearer tide_pat_…</c>, held to its scopes and TLP ceiling) or as a person (Windows sign-in, as a
/// gMSA for a server, held to their roles). Each endpoint goes through the service the pages use, so the same checks
/// apply; writes are audited under the caller's name. Errors are problem details: 400 for a bad request, 403 for a
/// missing scope or permission, 404 when there's nothing there, 409 when it changed meanwhile.
/// </summary>
public static class ApiEndpoints
{
    public const string RateLimit = "api";

    public static void MapTideApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1")
            .RequireAuthorization(new AuthorizationPolicyBuilder(ApiTokenAuthenticationHandler.ApiScheme).RequireAuthenticatedUser().Build())
            .RequireRateLimiting(RateLimit)
            .AddEndpointFilter(Errors)
            .DisableAntiforgery();

        api.MapGet("/briefs/{day}", async (string day, ApiService s, OrgClock clock, HttpContext http, CancellationToken ct) =>
                Day(day, clock) is not { } d ? Results.Problem(statusCode: 400, detail: "Give the day as yyyy-MM-dd, or \"today\".")
                : await s.BriefAsync(d, ApiTokenAuthenticationHandler.Ceiling(http.User), ct) is { } brief ? Results.Ok(brief) : NotFound("There's no brief for that day."))
            .Scope(ApiScopes.BriefsRead);

        api.MapGet("/actions", async (string? view, ApiService s, HttpContext http, CancellationToken ct) =>
                EnumNames.TryParse<ActionView>(view ?? "Open", out var v, ignoreCase: true) && v is ActionView.Open or ActionView.Overdue or ActionView.Closed
                    ? Results.Ok(await s.ActionsAsync(v, ApiTokenAuthenticationHandler.Ceiling(http.User), ct))
                    : Results.Problem(statusCode: 400, detail: "view is open, overdue or closed."))
            .Scope(ApiScopes.ActionsRead);

        api.MapGet("/actions/{number:int}", async (int number, ApiService s, HttpContext http, CancellationToken ct) =>
                await s.ActionAsync(number, ApiTokenAuthenticationHandler.Ceiling(http.User), ApiBase(http), ct) is { } a ? Results.Ok(a) : NotFound($"There's no action A-{number}."))
            .Scope(ApiScopes.ActionsRead);

        // A file on the action (or one of its comments), as it was added, or in a zip with the password "infected" (?zip=true;
        // always, whatever ?zip says, under Files:ZipDownloads=Always).
        api.MapGet("/actions/{number:int}/files/{fileId:guid}", async (int number, Guid fileId, bool? zip, ApiService s, HttpContext http, CancellationToken ct) =>
                await s.OpenFileAsync(number, fileId, ApiTokenAuthenticationHandler.Ceiling(http.User), ct) is { } file
                    ? zip == true || s.ZipMode == Tide.Application.Files.ZipDownloads.Always
                        ? FileDownloads.Zipped(http, file) : FileDownloads.Plain(http, file.Content, file.FileName)
                    : NotFound($"There's no such file on A-{number}."))
            .Scope(ApiScopes.ActionsRead).Recorded(AuditedWhen.Always);

        api.MapPatch("/actions/{number:int}", async (int number, ApiActionUpdate update, ApiService s, HttpContext http, CancellationToken ct) =>
                await s.UpdateActionAsync(number, update, ApiTokenAuthenticationHandler.Ceiling(http.User), ApiBase(http), ct) is { } a ? Results.Ok(a) : NotFound($"There's no action A-{number}."))
            .Scope(ApiScopes.ActionsWrite);

        api.MapGet("/stack", async (ApiService s, HttpContext http, CancellationToken ct) =>
                Results.Ok(await s.StackAsync(ApiTokenAuthenticationHandler.Ceiling(http.User), ct)))
            .Scope(ApiScopes.StackRead);

        api.MapGet("/metrics", async (string? from, string? to, ApiService s, OrgClock clock, CancellationToken ct) =>
                (from is null ? clock.Today.AddDays(-30) : Day(from, clock)) is { } f && (to is null ? clock.Today : Day(to, clock)) is { } t
                    ? Results.Ok(await s.MetricsAsync(f, t, ct))
                    : Results.Problem(statusCode: 400, detail: "Give from and to as yyyy-MM-dd."))
            .Scope(ApiScopes.MetricsRead);

        api.MapPost("/metrics", async (ApiMetricEntry entry, ApiService s, CancellationToken ct) =>
            {
                await s.SetMetricAsync(entry, ct);
                return Results.NoContent();
            })
            .Scope(ApiScopes.MetricsWrite);

        // The new item's place in triage. From the app's root (with its path base): a relative "triage?at=" resolves
        // against this endpoint's address, to /api/v1/triage?at=, which isn't a page.
        api.MapPost("/triage", async (ApiProposal proposal, ApiService s, HttpContext http, CancellationToken ct) =>
                await s.ProposeAsync(proposal, ct) is { } id ? Results.Created($"{http.Request.PathBase}/triage?at={id}", new { id }) : Results.Ok(new { id = (Guid?)null, already = true }))
            .Scope(ApiScopes.TriagePropose);

        api.MapPost("/detections", async (ApiDetections push, Tide.Application.Detections.DetectionService s, CancellationToken ct) =>
                Results.Ok(await s.PushAsync((push.Rules ?? []).Where(r => r is not null).Select(r => new Tide.Application.Detections.ParsedRule(r.Id ?? "", r.Name ?? "", r.Techniques ?? [], r.Platform, r.Enabled ?? true)).ToList(), ct)))
            .Scope(ApiScopes.DetectionsWrite);

        api.MapGet("/openapi.json", (HttpContext http) => Results.Text(OpenApi.For(http.Request.PathBase), "application/json"));
    }

    // A token caller must hold the endpoint's scope; a person is held to their permissions by the services.
    private static RouteHandlerBuilder Scope(this RouteHandlerBuilder b, string scope) => b.AddEndpointFilter(async (ctx, next) =>
    {
        var user = ctx.HttpContext.User;
        if (ApiTokenAuthenticationHandler.IsToken(user) && !user.HasClaim(ApiTokenAuthenticationHandler.ScopeClaim, scope))
            return Results.Problem(statusCode: 403, detail: $"This token doesn't have the {scope} scope.");
        return await next(ctx);
    }).WithMetadata(new ApiScopeAttribute(scope));

    // The services' refusals as problem details; nothing is cached.
    private static async ValueTask<object?> Errors(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        ctx.HttpContext.Response.Headers.CacheControl = "private, no-store";
        try { return await next(ctx); }
        catch (ForbiddenException ex) { return Results.Problem(statusCode: 403, detail: ex.Message); }
        catch (KeyNotFoundException ex) { return NotFound(ex.Message); }
        catch (StaleEditException ex) { return Results.Problem(statusCode: 409, detail: ex.Message); }
        catch (ArgumentException ex) { return Results.Problem(statusCode: 400, detail: ex.Message); }
        catch (InvalidOperationException ex) { return Results.Problem(statusCode: 400, detail: ex.Message); }
    }

    // The API's own address under the app's path base (/tide/api/v1 under an IIS sub-path), for the links it gives.
    private static string ApiBase(HttpContext http) => http.Request.PathBase.Add("/api/v1").Value!;

    private static IResult NotFound(string detail) => Results.Problem(statusCode: 404, detail: detail);

    private static DateOnly? Day(string text, OrgClock clock) =>
        string.Equals(text, "today", StringComparison.OrdinalIgnoreCase) ? clock.CurrentBriefDate
        : DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
}

/// <summary>The scope an endpoint needs, for the OpenAPI description.</summary>
public sealed record ApiScopeAttribute(string Scope);
