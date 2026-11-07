using System.Net;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Org;
using Tide.Infrastructure.Persistence;

namespace Tide.IntegrationTests;

/// <summary>The real app, in Development (dev sign-in, SQLite in a temporary folder), over HTTP.</summary>
public class TideApp : WebApplicationFactory<Program>
{
    /// <summary>The dev sign-in's roles, when not the default (Analyst and Admin).</summary>
    protected virtual string? Role => null;

    /// <summary>The sub-path the app is served under (App:PathBase), when not the root.</summary>
    protected virtual string? PathBase => null;

    /// <summary>The SQL Server database the app runs on instead of SQLite (see <see cref="SqlServerTideApp"/>).</summary>
    private protected virtual string? SqlServerDatabase => null;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tide-tests-" + Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(_dir);
        builder.UseEnvironment("Development");
        if (SqlServerDatabase is { } sql)
        {
            builder.UseSetting("Database:Provider", "SqlServer");
            builder.UseSetting("ConnectionStrings:Default", sql);
        }
        else builder.UseSetting("ConnectionStrings:Default", $"Data Source={Path.Combine(_dir, "tide.db")};Pooling=False");
        builder.UseSetting("Files:RootPath", Path.Combine(_dir, "files"));
        builder.UseSetting("Email:PickupDirectory", Path.Combine(_dir, "mail"));
        if (Role is { } role)
        {
            builder.UseSetting("DevAuth:Roles:0", role);
            builder.UseSetting("DevAuth:Roles:1", role);
        }
        if (PathBase is { } pathBase) builder.UseSetting("App:PathBase", pathBase);
        // A confirmation toast stays until something dismisses it, instead of going after a few real seconds: a test
        // that reads it after a step that took longer on a loaded machine still finds it (ToastServiceTests holds
        // the few seconds themselves).
        builder.ConfigureTestServices(s => s.AddScoped(_ => new Tide.Web.Services.ToastService(new ManualClock())));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
        if (SqlServerDatabase is not { } sql) return;
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(sql).Options);
        db.Database.EnsureDeleted();
    }
}

/// <summary>The real app on SQL Server, as production runs it: a database of its own on the server <c>TIDE_TEST_SQL</c>
/// names, which the app migrates at startup as it would production's, dropped afterwards. For a [SqlServerFact] test.</summary>
public class SqlServerTideApp : TideApp
{
    private protected override string? SqlServerDatabase { get; } = SqlServerFactAttribute.NewDatabase();
}

public class WebAppTests(TideApp app) : IClassFixture<TideApp>
{
    // The app's pages, each of which renders for the dev user (Analyst and Admin) on the sample data.
    private static readonly string[] PagePaths =
    [
        "/",
        "/articles",
        "/briefs",
        "/admin",
        "/admin/scorecard",
        "/admin/metrics",
        "/admin/metric-imports",
        "/admin/access",
        "/metrics",
        "/published",
        "/actions",
        "/actions/new",
        "/actions/1",
        "/workload",
        "/hunts/saved",
        "/outreach/templates",
        "/admin/teams",
        "/admin/organization",
        "/admin/queries",
        "/triage",
        "/stories",
        "/search",
        "/search?q=203.0.113.45",
        "/admin/sources",
        "/rotation",
        "/admin/rotation",
        "/playbooks",
        "/board-rules",
        "/admin/card-rules",
        "/actions?view=open&layout=board",
    ];

    public static TheoryData<string> Pages => new(PagePaths);

    // Pages the list above leaves out, read on SQL Server too: the rest of Administration, the reports, and a day's brief.
    private static readonly string[] MorePages =
    [
        "/admin/api",
        "/admin/detections",
        "/admin/email-templates",
        "/admin/source-health",
        "/admin/system",
        "/admin/system/audit",
        "/admin/system/bundle",
        "/admin/system/server",
        "/admin/system/sources",
        "/admin/system/updates",
        "/adversaries",
        "/coverage",
        "/reports",
        "/reports/teams",
        "/stack",
        "/trends",
        "/briefs/{day}",
        "/briefs/{day}/sharepoint",
        "/present/{day}",
        "/published/{day}",
    ];

    [Theory]
    [MemberData(nameof(Pages))]
    public async Task Pages_Render_ForTheDevUser(string path)
    {
        var res = await app.CreateClient().GetAsync(path);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        // An entity written in a component's attribute is encoded again, and shows as typed (RazorAttributeTests).
        (await res.Content.ReadAsStringAsync()).Should().Contain("TIDE").And.NotContain("&amp;quot;").And.NotContain("&amp;amp;");
    }

    // Every page on production's database: SQL Server answers a query asynchronously where SQLite returns before the first
    // await, so a page that reads state set after its first await renders on SQLite and fails there (Teams & SLA did). The
    // response is still a 200 then: the layout's error boundary shows its message in place of the page.
    [SqlServerFact]
    public async Task EveryPage_RendersOnSqlServer()
    {
        await using var server = new SqlServerTideApp();
        var client = server.CreateClient();
        var day = Audit.WebSeed.Day(server);
        foreach (var path in PagePaths.Concat(MorePages).Select(p => p.Replace("{day}", day)))
        {
            var res = await client.GetAsync(path);
            res.StatusCode.Should().Be(HttpStatusCode.OK, path);
            (await res.Content.ReadAsStringAsync()).Should().Contain("TIDE", path).And.NotContain("Something went wrong on this page", path);
        }
    }

    [Fact]
    public async Task Responses_CarryTheSecurityHeaders()
    {
        var res = await app.CreateClient().GetAsync("/");
        res.Headers.GetValues("X-Frame-Options").Should().Equal("DENY");
        var csp = res.Headers.TryGetValues("Content-Security-Policy", out var h) ? h : res.Content.Headers.GetValues("Content-Security-Policy");
        csp.Single().Should().Contain("script-src 'self'");
    }

    [Fact]
    public void TheStartedApp_ReadsAFeedInACodePage()
    {
        // The app registers the code-page encodings as it starts; the test registers nothing itself.
        _ = app.Services;
        var rss = "<?xml version=\"1.0\" encoding=\"iso-2022-jp\"?><rss version=\"2.0\"><channel>"
            + "<item><title>脆弱性情報</title><link>https://example.jp/1</link></item></channel></rss>";

        Tide.Application.Intake.FeedParser.ParseFeed(new MemoryStream(System.Text.Encoding.GetEncoding("iso-2022-jp").GetBytes(rss)))
            .Single().Title.Should().Be("脆弱性情報");
    }

    [Fact]
    public async Task Health_AnswersWithoutSignIn()
    {
        (await app.CreateClient().GetAsync("/health")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await app.CreateClient().GetAsync("/health/live")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task TheDevSignInCookie_CoversTheWholeSite()
    {
        var res = await app.CreateClient().GetAsync("/health?as=Sam");
        res.Headers.GetValues("Set-Cookie").Should().ContainSingle(c => c.StartsWith("dev_as=Sam") && c.Contains("path=/;"));
    }

    [Fact]
    public async Task AnAmberArticle_OffersItsBulletsNothingBelowAmber()
    {
        Guid id;
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Tide.Infrastructure.Persistence.AppDbContext>();
            var article = new Tide.Domain.Entities.Article { Title = "Floor check", Tlp = Tide.Domain.Enums.TlpLevel.Amber, AuthorId = "x", AuthorName = "X" };
            // Stored below the article, as an old save could leave it: it shows at the article's TLP.
            article.Bullets.Add(new Tide.Domain.Entities.ArticleBullet { Text = "A bullet", Tlp = Tide.Domain.Enums.TlpLevel.Green });
            db.Articles.Add(article);
            await db.SaveChangesAsync();
            id = article.Id;
        }

        var html = await app.CreateClient().GetStringAsync($"/articles/{id}");

        var at = html.IndexOf("aria-label=\"This bullet&#x27;s TLP\"", StringComparison.Ordinal);
        at.Should().BePositive();
        var picker = html[at..html.IndexOf("</select>", at, StringComparison.Ordinal)];
        picker.Should().Contain("<option value=\"Amber\" selected>").And.Contain("TLP:RED")
            .And.NotContain("TLP:CLEAR").And.NotContain("TLP:GREEN");
    }

    [Fact]
    public async Task AnUnknownScreenshot_IsNotFound()
    {
        (await app.CreateClient().GetAsync($"/files/{Guid.NewGuid()}/inline")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await app.CreateClient().GetAsync($"/files/{Guid.NewGuid()}/download")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AnEvidenceFile_OnAComment_IsADownload_NeverInline()
    {
        Guid fileId;
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Tide.Infrastructure.Persistence.AppDbContext>();
            var store = scope.ServiceProvider.GetRequiredService<Tide.Application.Abstractions.IFileStore>();
            var file = new Tide.Domain.Entities.StoredFile { FileName = "extract.log", ContentType = "application/octet-stream", SizeBytes = 5 };
            using (var bytes = new MemoryStream("hello"u8.ToArray())) await store.WriteAsync(file.Id, bytes);
            db.Files.Add(file);
            var comment = new Tide.Domain.Entities.ActionComment { ActionId = db.Actions.First().Id, Text = "Log", AuthorId = "x", AuthorName = "X", AtUtc = DateTimeOffset.UtcNow };
            comment.Files.Add(new Tide.Domain.Entities.ActionCommentFile { FileId = file.Id, FileName = "extract.log", SizeBytes = 5 });
            db.ActionComments.Add(comment);
            await db.SaveChangesAsync();
            fileId = file.Id;
        }
        var client = app.CreateClient();
        var res = await client.GetAsync($"/files/{fileId}/download");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        res.Content.Headers.ContentType!.MediaType.Should().Be("application/octet-stream");
        res.Content.Headers.ContentDisposition!.DispositionType.Should().Be("attachment");
        res.Headers.GetValues("X-Content-Type-Options").Should().Equal("nosniff");
        (await res.Content.ReadAsStringAsync()).Should().Be("hello");
        (await client.GetAsync($"/files/{fileId}/inline")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task EmailPreview_IsTheBriefAsEmailed_WithAScriptlessSandboxedPolicy()
    {
        var day = app.Services.GetRequiredService<OrgClock>().CurrentBriefDate;
        var res = await app.CreateClient().GetAsync($"/briefs/{day:yyyy-MM-dd}/email/team");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var csp = res.Headers.TryGetValues("Content-Security-Policy", out var h) ? h.Single() : res.Content.Headers.GetValues("Content-Security-Policy").Single();
        csp.Should().Contain("sandbox").And.Contain("default-src 'none'").And.NotContain("script-src 'self'");
        var html = await res.Content.ReadAsStringAsync();
        html.Should().Contain("daily brief").And.Contain("TLP:");

        (await app.CreateClient().GetAsync($"/briefs/{day:yyyy-MM-dd}/email/nonsense")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task SharePointPage_AndMetricsCsv_Answer()
    {
        var day = app.Services.GetRequiredService<OrgClock>().CurrentBriefDate;
        (await app.CreateClient().GetAsync($"/briefs/{day:yyyy-MM-dd}/sharepoint")).StatusCode.Should().Be(HttpStatusCode.OK);
        var csv = await app.CreateClient().GetAsync("/metrics/export.csv");
        csv.StatusCode.Should().Be(HttpStatusCode.OK);
        (await csv.Content.ReadAsStringAsync()).Should().Contain("date,group,metric_key");
        var stix = await app.CreateClient().GetAsync($"/exports/brief/{day:yyyy-MM-dd}?format=stix&tlp=amber");
        stix.StatusCode.Should().Be(HttpStatusCode.OK);
        stix.Content.Headers.ContentType!.MediaType.Should().Be("application/stix+json");
        stix.Content.Headers.ContentDisposition!.FileNameStar.Should().Be($"tide-brief-{day:yyyy-MM-dd}-amber.stix.json");
        (await stix.Content.ReadAsStringAsync()).Should().Contain("\"type\": \"bundle\"");
        var indicators = await app.CreateClient().GetAsync($"/exports/brief/{day:yyyy-MM-dd}?format=csv");
        (await indicators.Content.ReadAsStringAsync()).Should().Contain("type,value,defanged");
        (await app.CreateClient().GetAsync($"/exports/brief/{day:yyyy-MM-dd}?tlp=purple")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await app.CreateClient().GetAsync($"/exports/nothing/{day:yyyy-MM-dd}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var trends = await app.CreateClient().GetAsync("/trends/export.csv?p=2026-Q3");
        trends.Content.Headers.ContentDisposition!.FileNameStar.Should().Be("tide-trends-2026-Q3.csv");
        (await trends.Content.ReadAsStringAsync()).Should().Contain("section,group,line,key,2025-Q3,2025-Q4,2026-Q1,2026-Q2,2026-Q3,change");
        var counted = await app.CreateClient().GetAsync("/trends/articles.csv?p=2026-09&line=articles:threat");
        counted.Content.Headers.ContentDisposition!.FileNameStar.Should().Be("tide-articles-2026-09-threats.csv");
        (await counted.Content.ReadAsStringAsync()).Should().Contain("counted_on,briefed,title");
        (await app.CreateClient().GetAsync("/trends/articles.csv?line=actions:raised")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var ics = await app.CreateClient().GetAsync("/rotation/my.ics");
        ics.Content.Headers.ContentType!.MediaType.Should().Be("text/calendar");
        (await ics.Content.ReadAsStringAsync()).Should().Contain("BEGIN:VEVENT", "the dev sign-in has weeks on the sample rota");
    }
}

/// <summary>The app with the dev sign-in holding only the Reader role.</summary>
public sealed class ReaderApp : TideApp
{
    protected override string? Role => "Reader";
}

/// <summary>The app with the dev sign-in holding only the ActionOwner role: the team edition and actions, but no triage or
/// administration.</summary>
public sealed class ActionOwnerApp : TideApp
{
    protected override string? Role => "ActionOwner";
}

public class ReaderTests(ReaderApp app) : IClassFixture<ReaderApp>
{
    [Fact]
    public async Task AReader_GetsThePublishedArchive_NotTheTeamsPagesOrScreenshots()
    {
        var client = app.CreateClient(new() { AllowAutoRedirect = false });
        (await client.GetAsync("/published")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/metrics/export.csv")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.GetAsync("/exports/brief/2026-10-26?format=csv")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.GetAsync("/trends/export.csv")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var page = await client.GetAsync("/actions");
        page.StatusCode.Should().Be(HttpStatusCode.Redirect, "a page they can't open goes to the friendly access-denied page");
        page.Headers.Location!.OriginalString.Should().StartWith("/access-denied?from=");
        var missing = new HttpRequestMessage(HttpMethod.Get, "/no-such-page");
        missing.Headers.Accept.ParseAdd("text/html");
        (await client.SendAsync(missing)).Headers.Location!.OriginalString.Should().Be("/not-found");
        (await client.GetAsync("/search?q=test")).StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.Redirect);
        var day = app.Services.GetRequiredService<OrgClock>().CurrentBriefDate;
        (await client.GetAsync($"/briefs/{day:yyyy-MM-dd}/email/public")).StatusCode.Should().Be(HttpStatusCode.Forbidden, "an endpoint that isn't a page keeps its status code");

        // The sample brief's screenshots (none are sent) are never served to a Reader.
        Guid fileId;
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Tide.Infrastructure.Persistence.AppDbContext>();
            var file = new Tide.Domain.Entities.StoredFile { FileName = "a.png", ContentType = "image/png" };
            db.Files.Add(file);
            var article = db.Articles.First();
            db.ArticleScreenshots.Add(new Tide.Domain.Entities.ArticleScreenshot { ArticleId = article.Id, FileId = file.Id });
            await db.SaveChangesAsync();
            fileId = file.Id;
        }
        (await client.GetAsync($"/files/{fileId}/inline")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}

/// <summary>The app served under a sub-path, as when installed as an application within another IIS site.</summary>
public sealed class SubPathApp : TideApp
{
    protected override string? PathBase => "/tide";
}

public class SubPathHostingTests(SubPathApp app) : IClassFixture<SubPathApp>
{
    [Fact]
    public async Task Pages_StaticFiles_AndHealth_AnswerUnderThePathBase_WithTheBaseHrefSet()
    {
        var client = app.CreateClient(new() { AllowAutoRedirect = false });
        var page = await client.GetAsync("/tide/briefs");
        page.StatusCode.Should().Be(HttpStatusCode.OK);
        (await page.Content.ReadAsStringAsync()).Should().Contain("<base href=\"/tide/\" />", "relative links, scripts and the circuit resolve under it");
        (await client.GetAsync("/tide/app.css")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/tide/health")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task TheNotFoundRedirect_StaysUnderThePathBase()
    {
        var client = app.CreateClient(new() { AllowAutoRedirect = false });
        var missing = new HttpRequestMessage(HttpMethod.Get, "/tide/no-such-page");
        missing.Headers.Accept.ParseAdd("text/html");
        (await client.SendAsync(missing)).Headers.Location!.OriginalString.Should().Be("/tide/not-found");
    }

    [Fact]
    public async Task TheDevSignInCookie_IsScopedToThePathBase()
    {
        var res = await app.CreateClient().GetAsync("/tide/health?as=Sam");
        res.Headers.GetValues("Set-Cookie").Should().ContainSingle(c => c.StartsWith("dev_as=Sam") && c.Contains("path=/tide"));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("/", null)]
    [InlineData("tide", "/tide")]
    [InlineData(" /tide/ ", "/tide")]
    [InlineData("/apps/cti-brief", "/apps/cti-brief")]
    public void PathBase_IsNormalized(string? value, string? expected) =>
        Tide.Web.Services.PathBaseSetting.Normalize(value).Should().Be(expected);

    [Theory]
    [InlineData("/ti de")]
    [InlineData("/../tide")]
    [InlineData("https://host/tide")]
    [InlineData("/tide?x=1")]
    public void APathBaseThatIsNotAPath_StopsStartup(string value) =>
        FluentActions.Invoking(() => Tide.Web.Services.PathBaseSetting.Normalize(value)).Should().Throw<InvalidOperationException>();
}
