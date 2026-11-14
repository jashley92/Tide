using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Tide.Application;
using Tide.Application.Abstractions;
using Tide.Application.Distribution;
using Tide.Application.Exports;
using Tide.Application.Files;
using Tide.Application.Metrics;
using Tide.Application.Operations;
using Tide.Application.Org;
using Tide.Application.Security;
using Tide.Application.Settings;
using Tide.Application.Trends;
using Tide.Domain.Common;
using Tide.Domain.Enums;
using Tide.Infrastructure;
using Tide.Infrastructure.Persistence;
using Tide.Web.Api;
using static Tide.Web.Api.ExportEndpoints;
using Tide.Web.Components;
using Tide.Web.Security;
using Tide.Web.Services;

// One culture for every thread, so dates and numbers never follow the server's regional settings.
var culture = CultureInfo.GetCultureInfo("en-US");
CultureInfo.DefaultThreadCurrentCulture = culture;
CultureInfo.DefaultThreadCurrentUICulture = culture;

var builder = WebApplication.CreateBuilder(args);

// A failing readiness probe is reported once a minute by the /health endpoint itself (below), not as an Error per probe.
builder.Logging.AddFilter("Microsoft.Extensions.Diagnostics.HealthChecks", LogLevel.None);

// Don't advertise the server implementation.
builder.WebHost.ConfigureKestrel(options => options.AddServerHeader = false);

// --- Blazor Server ---
// Blazor's connections by transport (WebSockets, or long polling where IIS or a proxy can't carry them) since the app
// started, for Diagnostics: counted by TransportHubFilter, which also hands each circuit its own (PresenceCircuit.Transport).
var transportCounts = new TransportCounts();
builder.Services.AddSingleton(transportCounts);

// A session whose connection drops (a laptop asleep, a network blip) is kept for 20 minutes rather than the default 3,
// so coming back to an article being edited picks up where it was, unsaved changes and all.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents(o => o.DisconnectedCircuitRetentionPeriod = TimeSpan.FromMinutes(20))
    // A message from the browser over this limit drops the circuit, and what it carried is lost without a word. A text box
    // sends all its text in one message, so the limit is set by the longest box: every box has a maxlength, the longest
    // 200,000 characters (a paste to search for indicators, a query TIDE wrote), and a character is sent in at most 3 bytes
    // (UTF-8; a quote, backslash or line break escaped in JSON takes 2). 1 MB holds that with room; past a box's
    // maxlength the browser cuts a paste, and the box says so (LengthNote). Screenshots don't count: InputFile streams them.
    .AddHubOptions(o =>
    {
        o.MaximumReceiveMessageSize = 1024 * 1024;
        // Which transport (WebSockets, or a fallback) each connection and circuit is on, for Diagnostics.
        o.AddFilter(new TransportHubFilter(transportCounts));
    });

// --- Data Protection ---
// The key ring protects antiforgery tokens and circuit state. Under an IIS app pool it's ephemeral by default (no user
// profile), so every recycle signs everyone out. Persist it to a stable, ACL-restricted folder, encrypted at rest. A fixed
// application name keeps the ring stable if the content-root path changes. Development (no path) keeps the default.
var dp = builder.Services.AddDataProtection().SetApplicationName("TIDE");
builder.Services.AddSingleton<PlainDownloads>();
var dpKeyPath = builder.Configuration["DataProtection:KeyPath"];
if (!string.IsNullOrWhiteSpace(dpKeyPath))
{
    if (!Path.IsPathRooted(dpKeyPath))
        throw new InvalidOperationException($"DataProtection:KeyPath must be an absolute path; got '{dpKeyPath}'.");
    var keyDir = Path.GetFullPath(dpKeyPath);
    Directory.CreateDirectory(keyDir);
    dp.PersistKeysToFileSystem(new DirectoryInfo(keyDir));
    // Machine-scope DPAPI works for a gMSA/app-pool identity with no loaded user profile.
    if (OperatingSystem.IsWindows())
        dp.ProtectKeysWithDpapi(protectToLocalMachine: true);
}

builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddApplication(builder.Configuration);

// --- Authentication: Windows integrated in production, the dev handler locally ---
// Fail safe: the passwordless dev handler runs only in Development (or a non-Production environment that opts in);
// anywhere else a missing or mistyped Auth:Mode stops startup.
var useWindows = AuthModeGuard.UseWindows(builder.Environment, builder.Configuration);
if (useWindows)
{
    builder.Services.AddAuthentication(NegotiateDefaults.AuthenticationScheme).AddNegotiate();
    builder.Services.AddScoped<IClaimsTransformation, RoleClaimsTransformer>();
}
else
{
    builder.Services.Configure<DevAuthOptions>(builder.Configuration.GetSection("DevAuth"));
    builder.Services.AddAuthentication(DevAuthenticationHandler.SchemeName)
        .AddScheme<AuthenticationSchemeOptions, DevAuthenticationHandler>(DevAuthenticationHandler.SchemeName, _ => { });
}

// The API (EP-75): a bearer token signs in as its caller; anything else goes to the app's own sign-in.
builder.Services.AddAuthentication()
    .AddScheme<AuthenticationSchemeOptions, ApiTokenAuthenticationHandler>(ApiTokenAuthenticationHandler.SchemeName, _ => { })
    .AddPolicyScheme(ApiTokenAuthenticationHandler.ApiScheme, "TIDE API", o => o.ForwardDefaultSelector = ctx =>
        ApiTokenAuthenticationHandler.Bearer(ctx.Request) is not null ? ApiTokenAuthenticationHandler.SchemeName
        : useWindows ? NegotiateDefaults.AuthenticationScheme : DevAuthenticationHandler.SchemeName);
builder.Services.AddSingleton<Tide.Application.Abstractions.ISecretProtector, Tide.Web.Security.DataProtectionSecrets>();
builder.Services.AddScoped<Tide.Application.Api.IWebhookPoster, HttpWebhookPoster>();
builder.Services.AddHttpClient(HttpWebhookPoster.Client, c => c.Timeout = TimeSpan.FromSeconds(15))
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });

builder.Services.AddAuthorization(options =>
{
    foreach (var (policy, permission) in Policies.Required)
        options.AddPolicy(policy, p => p.RequireClaim(AppClaimTypes.Permission, permission.ToString()));
    // Everything requires a signed-in user unless it says otherwise.
    options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
});
builder.Services.AddCascadingAuthenticationState();
// Re-derive an open session's roles and permissions from the current mappings every couple of minutes, so removing an
// AD mapping reaches signed-in users without them reconnecting.
builder.Services.AddSingleton(sp => new SessionPermissionRefresher(() => sp.GetRequiredService<IRoleDirectory>(), useWindows));
builder.Services.AddScoped<AuthenticationStateProvider, PermissionRevalidatingAuthStateProvider>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, CurrentUser>();
builder.Services.AddScoped<ToastService>();
builder.Services.AddSingleton<Presence>();
builder.Services.AddSingleton<BoardDrags>();
builder.Services.AddSingleton<LiveMeetings>();
builder.Services.AddSingleton<PrerenderStore>();
builder.Services.AddScoped<PrerenderHandoff>();
// What the top bar's brief chip says, kept per circuit so a navigation reads nothing for it.
builder.Services.AddScoped<BriefChipSource>();
// Whether what's new, tips and the tour are offered unasked (off on the screenshot tools' demo).
builder.Services.AddSingleton<OnboardingGate>();
// Help's Take the tour, to the layout's OnboardingHost in the same circuit.
builder.Services.AddScoped<TourRequests>();
// Where a guide started from Help opens: a real article or action for those pages.
builder.Services.AddScoped<GuideStarts>();
// Which circuit shows each person their tour, so a second tab doesn't start a second one.
builder.Services.AddSingleton<TourClaims>();
// The Library's one search, over the tabs the person may open.
builder.Services.AddScoped<LibrarySearch>();
builder.Services.AddSingleton<Tide.Application.Briefs.IMeetingsInProgress>(sp => sp.GetRequiredService<LiveMeetings>());
// Clears a closed tab's presence: the circuit's handler, and the circuit id presence notes join with. It also keeps the
// transport the circuit is on, for Diagnostics.
builder.Services.AddScoped<PresenceCircuit>();
builder.Services.AddScoped<Microsoft.AspNetCore.Components.Server.Circuits.CircuitHandler>(sp => sp.GetRequiredService<PresenceCircuit>());
// The circuit's user is ambient during each of its activities, so a refusal can be logged with who asked.
builder.Services.AddScoped<Microsoft.AspNetCore.Components.Server.Circuits.CircuitHandler, AmbientUserCircuitHandler>();
// Whether the circuit's browser is still connected: a Pull all stops when it vanishes.
builder.Services.AddScoped<CircuitConnection>();
builder.Services.AddScoped<Microsoft.AspNetCore.Components.Server.Circuits.CircuitHandler>(sp => sp.GetRequiredService<CircuitConnection>());
// Development only, for the screenshot tool (tools/screenshots), when Demo:Seed is set: its fuller demo data (below), and
// the circuit work in hand for it to wait on (/demo/activity, below).
var demoMode = builder.Environment.IsDevelopment() && builder.Configuration.GetValue<bool>("Demo:Seed");
var demoActivity = demoMode ? new DemoActivity() : null;
if (demoActivity is not null)
    builder.Services.AddSingleton<Microsoft.AspNetCore.Components.Server.Circuits.CircuitHandler>(demoActivity);
// With Demo:WorkingDay too (the layout checks), the app's clock is a working day's on a weekend (DemoWorkingDayClock).
if (demoMode && DemoWorkingDayClock.From(builder.Configuration) is { } workingDay)
    builder.Services.AddSingleton<TimeProvider>(workingDay);
// First, so it stops last: it writes the jobs' last runs and lets the job lease go once they've stopped.
builder.Services.AddHostedService<JobUpkeepHostedService>();
builder.Services.AddHostedService<ReminderHostedService>();
builder.Services.AddHostedService<IntakeHostedService>();
builder.Services.AddHostedService<MetricImportHostedService>();
builder.Services.AddHostedService<UpdateCheckHostedService>();
builder.Services.AddHostedService<WebhookHostedService>();
builder.Services.AddScoped<SystemSettings>();
builder.Services.AddScoped<SystemDiagnostics>();

// --- Health: liveness (the process answers), readiness (the database answers) and the background jobs ---
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database", tags: ["ready"])
    .AddCheck<JobsHealthCheck>("jobs", tags: ["jobs"]);

// --- Rate limits: per signed-in user (or address) on the image endpoint, which reads files from disk ---
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    // A Windows principal carries no NameIdentifier claim, so its account name keys the bucket, as CurrentUser.UserId does.
    static string Who(HttpContext ctx) =>
        ctx.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? ctx.User.Identity?.Name ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "anon";
    static RateLimitPartition<string> PerMinute(string key, int tokens) => RateLimitPartition.GetTokenBucketLimiter(key,
        _ => new TokenBucketRateLimiterOptions { TokenLimit = tokens, TokensPerPeriod = tokens, ReplenishmentPeriod = TimeSpan.FromMinutes(1), QueueLimit = 0 });
    o.AddPolicy("files", ctx => PerMinute(Who(ctx), 300));
    // Exports, CSVs, the email previews and the SharePoint zip read and render whole briefs: far more than a person
    // clicks, far less than a script could ask for.
    o.AddPolicy(ExportEndpoints.RateLimit, ctx => PerMinute(Who(ctx), 60));
    // The API, per caller (a token's id, or the person): a playbook's steady use, not a scrape.
    o.AddPolicy(ApiEndpoints.RateLimit, ctx => PerMinute("api:" + Who(ctx), 120));
    // The health probes answer without sign-in, so they're limited by address.
    o.AddPolicy("health", ctx => PerMinute(ctx.Connection.RemoteIpAddress?.ToString() ?? "anon", 120));
});

var app = builder.Build();

// Optional sub-path hosting. Installed as an IIS application within another site (/tide), IIS sets the path base itself
// and this stays blank. Behind another reverse proxy that forwards a sub-path, App:PathBase names it. Routing follows it,
// so routes match the path below the base.
if (PathBaseSetting.Normalize(app.Configuration["App:PathBase"]) is { } pathBase)
    app.UsePathBase(pathBase);
app.UseRouting();

// Migrate, seed role mappings and the scorecard starter set (and sample data in Development). Then read the settings
// saved in Administration, which from here on win over the server's.
using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().InitializeAsync();
    await app.Services.GetRequiredService<SavedSettings>().RefreshAsync(scope.ServiceProvider.GetRequiredService<IAppDbContextFactory>());
    await app.Services.GetRequiredService<StoredSecrets>().RefreshAsync(scope.ServiceProvider.GetRequiredService<IAppDbContextFactory>());
    await app.Services.GetRequiredService<EmailTemplateStore>().RefreshAsync(scope.ServiceProvider.GetRequiredService<IAppDbContextFactory>());
    await app.Services.GetRequiredService<EmailCopyStore>().RefreshAsync(scope.ServiceProvider.GetRequiredService<IAppDbContextFactory>());
    // Each job's latest run from the job history, so Diagnostics and /health/jobs know when it last went well.
    try { await app.Services.GetRequiredService<JobRunLog>().RestoreAsync(scope.ServiceProvider.GetRequiredService<IAppDbContextFactory>()); }
    catch (Exception ex)
    {
        app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Tide.Jobs")
            .LogWarning(ex, "Couldn't read the job history at start-up; Diagnostics shows only this process's runs.");
    }
}

// Development only: the screenshot tool's fuller demo data (tools/screenshots), when Demo:Seed is set.
if (demoMode)
    await DemoSeeder.SeedAsync(app.Services, app.Configuration, app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Tide.Demo"));

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error", createScopeForErrors: true);
    app.UseHsts();
    app.UseHttpsRedirection();
}

// A signed-in user with no TIDE role gets a bare 403 from a page's [Authorize(Policy)] before any component renders, so
// turn a page GET's 403 into the friendly access-denied page, and a 404 into the not-found page. Only pages get this:
// a matched endpoint is a page when it's a Razor component, and an address nothing matched is one when the browser asked
// for HTML. Files, exports, emails, the circuit and health probes keep their real status codes.
var accessLog = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(AmbientUser.AccessLogCategory);
// A change is published on the publisher's thread, inside their circuit activity: each subscriber is called with the
// publisher's user cleared, so work the subscriber starts isn't logged as the publisher's; the host's logs stay, so it's
// still logged. (A static delegate to a static method: nothing host-owned is kept.)
ChangeFeed.AroundHandler = AmbientUser.WithoutUser;
app.UseStatusCodePages(context =>
{
    var http = context.HttpContext;
    var path = http.Request.Path;
    var isPage = HttpMethods.IsGet(http.Request.Method) && (http.GetEndpoint() is { } endpoint
        ? endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Components.Endpoints.ComponentTypeMetadata>() is not null
        : http.Request.Headers.Accept.ToString().Contains("text/html", StringComparison.OrdinalIgnoreCase) && !Path.HasExtension(path.Value));
    // Every refusal leaves a trace: who, and where. A 403 here comes from a policy on a page or an endpoint; a refusal
    // inside the Blazor circuit is logged where it's shown (UserFacingError).
    if (http.Response.StatusCode == StatusCodes.Status403Forbidden && !path.StartsWithSegments("/_blazor"))
        accessLog.LogWarning("Access denied for {User} at {Method} {Path}", http.User.Identity?.Name ?? "anonymous", http.Request.Method, path.Value);
    if (isPage && http.Response.StatusCode == StatusCodes.Status403Forbidden && !path.StartsWithSegments("/access-denied"))
        http.Response.Redirect(http.Request.PathBase + "/access-denied?from=" + Uri.EscapeDataString(path.Value ?? ""));
    else if (isPage && http.Response.StatusCode == StatusCodes.Status404NotFound && !path.StartsWithSegments("/not-found"))
        http.Response.Redirect(http.Request.PathBase + "/not-found");
    return Task.CompletedTask;
});

app.UseMiddleware<SecurityHeadersMiddleware>();
// A circuit is offered to this site's own pages only (the browser sends Windows credentials with any site's request).
app.UseMiddleware<CircuitOriginMiddleware>();
app.UseStaticFiles();
app.UseAuthentication();
// Every line logged during a request carries who made it and the request's trace id, so an unhandled error or a refusal
// in the access log can be tied to a person and a request.
var requestLog = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Tide.Request");
app.Use(async (http, next) =>
{
    using (requestLog.BeginScope(new Dictionary<string, object?> { ["User"] = http.User.Identity?.Name ?? "anonymous", ["RequestId"] = http.TraceIdentifier }))
        await next(http);
});
app.UseMiddleware<UserMirrorMiddleware>();
app.UseAuthorization();
app.UseAntiforgery();
app.UseRateLimiter();

// The security headers middleware sets the one CSP (frame-ancestors 'none'), so Blazor doesn't add its own.
app.MapRazorComponents<App>().AddInteractiveServerRenderMode(o => o.ContentSecurityFrameAncestorsPolicy = null);

// The API (EP-75), under /api/v1.
app.MapTideApi();

// An adversary profile's techniques as an ATT&CK Navigator layer (EP-73).
app.MapGet("/adversaries/{id:guid}/navigator.json", async (Guid id, Tide.Application.Adversaries.AdversaryService adversaries, CancellationToken ct) =>
    await adversaries.NavigatorLayerAsync(id, ct) is { } layer
        ? Results.File(Encoding.UTF8.GetBytes(layer), "application/json", $"tide-adversary-{id:N}-layer.json")
        : Results.NotFound())
    .AsExport();

// Screenshots, inline. Only real raster images are served (the bytes are sniffed), never as a download, and only to
// someone allowed (ScreenshotService.OpenAsync decides: a Reader gets only images in a sent company-wide edition).
app.MapGet("/files/{id:guid}/inline", async (Guid id, ScreenshotService shots, HttpContext http, CancellationToken ct) =>
{
    if (await shots.OpenAsync(id, http.Request.Headers.IfNoneMatch.ToString(), ct) is not { } file) return Results.NotFound();
    // The browser keeps a copy for a few minutes, then asks again with the entity tag (the file's hash): a 304 while the
    // bytes match and the viewer may still see it, a 404 once either has changed. Revocation reaches a browser in minutes.
    // A Visitor's slide image is theirs only while they're in the meeting: never kept, so each look asks again.
    http.Response.Headers.CacheControl = shots.NoStore ? "no-store" : "private, max-age=300, must-revalidate";
    http.Response.Headers.XContentTypeOptions = "nosniff";
    http.Response.Headers.ETag = file.ETag;
    if (file.NotModified) return Results.StatusCode(StatusCodes.Status304NotModified);
    return Results.Stream(file.Content!, file.ContentType);
}).RequireAuthorization().RequireRateLimiting("files");

// Files on actions and their comments (and a hunt's evidence, an article's screenshot), as downloads under the name they
// were added with: any kind of file (a log, a pcap, an email, a sample), so never shown inline and never sniffed into a
// type. ActionFileService.OpenAsync decides who may have an action's or a comment's file, per file; ScreenshotService the rest.
// An action's or a comment's file whose main download is the zip (Files:ZipDownloads) isn't sent plain from a link written
// by hand (one in a comment's Markdown, say): it's sent to the zip, unless ?plain= carries the pass the action's Files
// list mints for its "as is" link (PlainDownloads: this file, this person, ten minutes). Under Always, it's never sent
// plain. Every download is recorded, in the access log and the audit log.
app.MapGet("/files/{id:guid}/download", async (Guid id, string? plain, Tide.Application.Actions.ActionFileService files, ScreenshotService shots,
    PlainDownloads passes, ICurrentUser user, HttpContext http, CancellationToken ct) =>
{
    if (await files.OpenAsync(id, ct: ct) is { } onAction)
    {
        var asIs = passes.Allows(plain, id, user.UserId);
        if (!FileRisk.ZipFirst(files.ZipMode, onAction.RiskReason)) return FileDownloads.Plain(http, onAction.Content, onAction.FileName);
        if (asIs && files.ZipMode != ZipDownloads.Always) return FileDownloads.Plain(http, onAction.Content, onAction.FileName);
        await onAction.DisposeAsync();
        return asIs
            ? Results.Text("Every file on an action downloads in a zip here (Files:ZipDownloads is Always). Download the zip: it opens with the password \"infected\".",
                "text/plain", Encoding.UTF8, StatusCodes.Status403Forbidden)
            : Results.Redirect($"{http.Request.PathBase}/files/{id}/zip");
    }
    if (await shots.OpenAttachmentAsync(id, ct) is not { } file) return Results.NotFound();
    return FileDownloads.Plain(http, file.Content, file.FileName);
}).RequireAuthorization().RequireRateLimiting("files").Recorded(AuditedWhen.Always);

// The same file inside a zip with the password "infected" (classic ZipCrypto): the main download of a file that can run
// (Files:ZipDownloads), offered for an action's or a comment's file only.
app.MapGet("/files/{id:guid}/zip", async (Guid id, Tide.Application.Actions.ActionFileService files, HttpContext http, CancellationToken ct) =>
    await files.OpenAsync(id, ct: ct) is { } file ? FileDownloads.Zipped(http, file) : Results.NotFound())
    .RequireAuthorization().RequireRateLimiting("files").Recorded(AuditedWhen.Always);

// The email exactly as it would be sent now (its images from the inline endpoint), for the preview frame on the brief
// page. Its own CSP: no script at all, sandboxed, and framable by this app only.
app.MapGet("/briefs/{day}/email/{edition}", async (string day, string edition, DistributionService distribution, HttpContext http, CancellationToken ct) =>
{
    if (!DateOnly.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
        || !EnumNames.TryParse<Edition>(edition, out var ed, ignoreCase: true)) return Results.NotFound();
    try
    {
        var html = await distribution.PreviewEmailAsync(date, ed, id => $"{http.Request.PathBase}/files/{id}/inline", ct);
        return EmailPage(http, html);
    }
    catch (KeyNotFoundException) { return Results.NotFound(); }
}).AsExport();

// A frozen send's email, as it went (its images the frozen copies).
app.MapGet("/sends/{id:guid}/email", async (Guid id, DistributionService distribution, HttpContext http, CancellationToken ct) =>
{
    if (await distribution.GetSendAsync(id, ct) is not { EmailHtml: { } html } send) return Results.NotFound();
    return EmailPage(http, DistributionService.WithImageUrls(html, send.Images, f => $"{http.Request.PathBase}/files/{f}/inline"));
}).AsExport();

// A frozen correction's email, as it went (it carries no images).
app.MapGet("/corrections/{id:guid}/email", async (Guid id, CorrectionService corrections, HttpContext http, CancellationToken ct) =>
    await corrections.GetAsync(id, ct) is { } correction ? EmailPage(http, correction.EmailHtml) : Results.NotFound())
    .AsExport();

// The company-wide edition's images for the SharePoint copy, named as its figure captions name them.
app.MapGet("/briefs/{day}/sharepoint-images.zip", async (string day, DistributionService distribution, CancellationToken ct) =>
{
    if (!DateOnly.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return Results.NotFound();
    try { return Results.File(await distribution.SharePointImagesZipAsync(date, ct), "application/zip", $"brief-{day}-images.zip"); }
    catch (KeyNotFoundException) { return Results.NotFound(); }
    // Held for review, as the copy is: a short text, so a browser that opens this address shows why.
    catch (InvalidOperationException ex) { return Results.Text(ex.Message, "text/plain", Encoding.UTF8, StatusCodes.Status409Conflict); }
}).AsExport();

// The current user's brief days, as a calendar file to import into Outlook.
app.MapGet("/rotation/my.ics", async (Tide.Application.Rotation.RotationService rotation, CancellationToken ct) =>
    Results.File(Encoding.UTF8.GetBytes(await rotation.MyCalendarAsync(ct)), "text/calendar; charset=utf-8", "tide-brief-days.ics"))
    .AsExport();

// The opening metrics over a range, as CSV.
app.MapGet("/metrics/export.csv", async (string? from, string? to, MetricsService metrics, OrgClock clock, CancellationToken ct) =>
{
    var end = DateOnly.TryParseExact(to, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : clock.CurrentBriefDate;
    var start = DateOnly.TryParseExact(from, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var f) ? f : end.AddDays(-30);
    try
    {
        var history = await metrics.HistoryAsync(start, end, ct);
        return CsvFile(MetricsService.ToCsv(history), $"tide-metrics-{history.From:yyyy-MM-dd}-to-{history.To:yyyy-MM-dd}.csv");
    }
    catch (ArgumentException ex) { return Results.BadRequest(ex.Message); }
}).AsExport();

// The trend table (every line, a column per period, ?n= of them) ending with ?p= ("2026-09", "2026-Q3"), as CSV.
app.MapGet("/trends/export.csv", async (string? p, bool? subs, int? n, TrendsService trends, CancellationToken ct) =>
{
    var end = Period.Parse(p) ?? trends.DefaultEnd(PeriodKind.Month);
    var report = await trends.ReportAsync(end, rollUpSubtechniques: subs != true, span: n, ct: ct);
    return CsvFile(TrendsService.ToCsv(report), $"tide-trends-{end.Key}.csv");
}).AsExport();

// The report for period ?p= (Reports), one row per number, as CSV.
app.MapGet("/reports/export.csv", async (string? p, TrendsService trends, ReportService reports, CancellationToken ct) =>
{
    var period = Period.Parse(p) ?? trends.DefaultEnd(PeriodKind.Month);
    return CsvFile(ReportService.ToCsv(await reports.BuildAsync(period, ct)), $"tide-report-{period.Key}.csv");
}).AsExport();

// The team commitments scorecard (Reports > Team commitments) over the last ?days= (30, 90, 180 or 365; 90 otherwise),
// ?parts=true for the parts of split actions only, ?external=true for external teams only, as CSV.
app.MapGet("/reports/teams/export.csv", async (int? days, bool? parts, bool? external, TeamScorecardService scorecard, CancellationToken ct) =>
{
    var card = await scorecard.BuildAsync(days, parts == true, external == true, ct);
    var stem = (card.PartsOnly ? "-parts" : "") + (card.ExternalOnly ? "-external" : "");
    return CsvFile(TeamScorecardService.ToCsv(card), $"tide-team-commitments-{card.To:yyyy-MM-dd}-{card.Days}d{stem}.csv");
}).AsExport(Policies.ReadActions);

// The articles one trend line (?line=, default all) counted in period ?p=, for writing the report, as CSV. ?subs=true
// counts sub-techniques separately, as Trends does with it, so a technique line is the one the page showed.
app.MapGet("/trends/articles.csv", async (string? p, string? line, bool? subs, TrendsService trends, IOptions<AppOptions> appOptions, CancellationToken ct) =>
{
    var period = Period.Parse(p) ?? trends.DefaultEnd(PeriodKind.Month);
    var report = await trends.ReportAsync(period, rollUpSubtechniques: subs != true, ct: ct);
    var row = report.Find(line ?? "articles:all");
    if (row is null || report.IsActionRow(row)) return Results.NotFound();
    var articles = await trends.ArticlesAsync(row.Ids[^1], report.CountedOn, ct);
    var stem = row.Key == "articles:all" ? "" : "-" + ExportService.Slug(row.Name);
    return CsvFile(TrendsService.ArticlesCsv(articles, appOptions.Value.BaseUrl), $"tide-articles-{period.Key}{stem}.csv");
}).AsExport();

// Indicators from an article, a brief, a story or a lookup, as STIX 2.1 (?format=stix, or none) or CSV (?format=csv), with
// the articles above the TLP ceiling (?tlp=) left out. The ceiling is always named: a request without one is refused, never
// given the most sensitive one, and so is a format it doesn't write. One with a ceiling above TLP:GREEN is in the audit log
// as well as the access log.
app.MapGet("/exports/{scope}/{key}", async (string scope, string key, string? format, string? tlp, ExportService exports,
    OrgClock clock, IOptions<AppOptions> appOptions, HttpContext http, CancellationToken ct) =>
{
    if (!EnumNames.TryParse<ExportScope>(scope, out var s, ignoreCase: true)) return Results.NotFound();
    if (tlp is null) return Results.BadRequest("Choose the highest TLP to include (?tlp=).");
    if (Tlp.Parse(tlp) is not { } ceiling) return Results.BadRequest("Unknown TLP.");
    var csv = string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase);
    if (!csv && format is not null && !string.Equals(format, "stix", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest("Unknown format: stix or csv.");
    NoteCeiling(http, ceiling);
    var set = await exports.GatherAsync(s, key, ceiling, ct);
    if (set is null) return Results.NotFound();
    var name = $"tide-{set.FileStem}-{Tlp.Key(ceiling)}";
    return csv
        ? CsvFile(IndicatorCsv.Write(set, appOptions.Value.BaseUrl), name + ".csv")
        : Results.File(Stix.Write(set, clock.TeamName), "application/stix+json;version=2.1", name + ".stix.json");
}).AsExport(audited: AuditedWhen.AboveGreen);

// Administration > System > Configuration bundle: the install answers file this server would be installed with, and the
// settings administrators made in the app. Neither carries a secret (see InstallAnswers and ConfigBundle). Each download is
// in the audit log.
app.MapGet("/admin/system/tide.config.psd1", async (SystemSettings settings, HttpContext http, CancellationToken ct) =>
{
    var text = InstallAnswers.Write(await settings.InstallFactsAsync(http.Request.PathBase.Value, ct));
    http.Response.Headers.CacheControl = "no-store";
    return Results.File(Utf8WithBom(text), "text/plain; charset=utf-8", InstallAnswers.FileName);
}).RequireAuthorization(Policies.Administer).Recorded(AuditedWhen.Always);

app.MapGet("/admin/system/config-bundle.json", async (ConfigBundleService bundles, OrgClock clock, HttpContext http, CancellationToken ct) =>
{
    var bundle = await bundles.ExportAsync(ct);
    http.Response.Headers.CacheControl = "no-store";
    return Results.File(Encoding.UTF8.GetBytes(bundle.ToJson()), "application/json", $"tide-config-bundle-{clock.Today:yyyy-MM-dd}.json");
}).RequireAuthorization(Policies.Administer).Recorded(AuditedWhen.Always);

// Administration > System > Audit log: the rows the page's filters (the same query names) match, newest first, as CSV,
// at most AuditLogService.MaxExport of them. Secrets are masked as on the page; the service checks the user is an
// administrator too. Each download is itself in the audit log.
app.MapGet("/admin/system/audit.csv", async (HttpContext http, Tide.Application.History.AuditLogService audit, OrgClock clock, CancellationToken ct) =>
{
    var filter = Tide.Application.History.AuditFilter.FromQuery(name => http.Request.Query[name].FirstOrDefault());
    var rows = await audit.ExportAsync(filter, ct);
    return CsvFile(Tide.Application.History.AuditCsv.Write(rows, clock), $"tide-audit-log-{clock.Today:yyyy-MM-dd}.csv");
}).AsExport(Policies.Administer, AuditedWhen.Always);

// Health probes for IIS, a load balancer or uptime monitoring: status only, no detail, no sign-in.
// The framework logs an Error for each failing probe (one every few seconds from a load balancer during an outage); its
// category is quiet (see the logging filter above) and one Warning a minute says what isn't healthy instead.
var healthLog = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Tide.Health");
Func<HttpContext, HealthReport, Task> StatusWord(string what)
{
    var lastWarningTicks = 0L;
    return (http, report) =>
    {
        var now = DateTimeOffset.UtcNow.UtcTicks;
        var last = Interlocked.Read(ref lastWarningTicks);
        if (report.Status != HealthStatus.Healthy && now - last > TimeSpan.TicksPerMinute
            && Interlocked.CompareExchange(ref lastWarningTicks, now, last) == last)
            healthLog.LogWarning("{What} is {Status}: {Checks}", what, report.Status,
                string.Join("; ", report.Entries.Where(e => e.Value.Status != HealthStatus.Healthy)
                    .Select(e => e.Key + " " + e.Value.Description + (e.Value.Exception is { } ex ? " (" + SecretMask.Scrub(ex.GetType().Name + ": " + ex.Message) + ")" : ""))));
        http.Response.ContentType = "text/plain";
        return http.Response.WriteAsync(report.Status.ToString());
    };
}
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous().RequireRateLimiting("health");
app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready"), ResponseWriter = StatusWord("Readiness") })
    .AllowAnonymous().RequireRateLimiting("health");
// The background jobs (JobsHealthCheck): Healthy, Degraded (200) or Unhealthy (503), the word only; the detail is on
// Diagnostics.
app.MapHealthChecks("/health/jobs", new HealthCheckOptions { Predicate = c => c.Tags.Contains("jobs"), ResponseWriter = StatusWord("Background jobs") })
    .AllowAnonymous().RequireRateLimiting("health");
if (demoActivity is not null)
    app.MapGet("/demo/activity", () => demoActivity.Now()).AllowAnonymous();

await app.RunAsync();

// An email shown in the browser: no script may run (sandbox, script-src none), styles are inline, images come from this
// app. Framable by this app only (the preview pane on the brief page). An email's styles must be inline to survive mail
// clients, hence 'unsafe-inline' for style only: accepted trade-off (Sonar S7039), as in SecurityHeadersMiddleware.
static IResult EmailPage(HttpContext http, string html)
{
    var headers = http.Response.Headers;
#pragma warning disable S7039 // above: the analyzer reports it in the build despite NOSONAR
    headers.ContentSecurityPolicy = "default-src 'none'; img-src 'self' data:; style-src 'unsafe-inline'; frame-ancestors 'self'; base-uri 'none'; form-action 'none'; sandbox allow-same-origin allow-popups"; // NOSONAR S7039, above
#pragma warning restore S7039
    headers.XFrameOptions = "SAMEORIGIN";
    headers.CacheControl = "no-store";
    return Results.Content(html, "text/html; charset=utf-8");
}
