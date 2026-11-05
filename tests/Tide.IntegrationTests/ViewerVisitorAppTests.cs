using System.Net;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Actions;
using Tide.Application.Articles;
using Tide.Application.Briefs;
using Tide.Application.Files;
using Tide.Application.Org;
using Tide.Domain.Enums;
using Tide.Web.Components.Layout;
using Tide.Web.Components.Pages;
using Tide.Web.Components.Pages.Admin;
using Tide.Web.Security;
using Tide.Web.Services;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

public sealed class ViewerApp : TideApp
{
    protected override string? Role => "Viewer";
}

public sealed class VisitorApp : TideApp
{
    protected override string? Role => "Visitor";
}

/// <summary>The Viewer over HTTP and on the pages: every read page opens, nothing to write is drawn.</summary>
public class ViewerAppTests(ViewerApp app) : IClassFixture<ViewerApp>
{
    private static readonly SignIn Viewer = new("dev:viewer-val", "Val Viewer", "Viewer");

    [Theory]
    [InlineData("/briefs")]
    [InlineData("/articles")]
    [InlineData("/actions")]
    [InlineData("/workload")]
    [InlineData("/stack")]
    [InlineData("/adversaries")]
    [InlineData("/stories")]
    [InlineData("/search?q=vpn")]
    [InlineData("/metrics")]
    [InlineData("/reports")]
    [InlineData("/trends")]
    [InlineData("/rotation")]
    [InlineData("/briefs/2026-10-26/sharepoint")]
    [InlineData("/published")]
    public async Task AViewer_OpensEachReadPage(string path)
    {
        var client = app.CreateClient(new() { AllowAutoRedirect = false });
        (await client.GetAsync(path)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("/triage")]
    [InlineData("/coverage")]
    [InlineData("/admin")]
    [InlineData("/admin/access")]
    public async Task AViewer_IsTurnedAwayFromTheWritersAndAdministratorsPages(string path)
    {
        var client = app.CreateClient(new() { AllowAutoRedirect = false });
        var page = await client.GetAsync(path);
        page.StatusCode.Should().Be(HttpStatusCode.Redirect);
        page.Headers.Location!.OriginalString.Should().StartWith("/access-denied");
    }

    [Fact]
    public async Task AViewer_SeesAnActionReadOnly_NoCommentBoxNoUploadNoStatus()
    {
        int number;
        await using (var scope = SignedIn(app, SignIn.Analyst))
        {
            var sp = scope.ServiceProvider;
            var team = await sp.GetRequiredService<TeamService>().AddAsync("Viewer team " + Guid.NewGuid().ToString("N")[..6], null, null);
            number = await sp.GetRequiredService<ActionService>().CreateAsync(new ActionForm { Title = "Viewer's read-only action", Kind = ActionKind.IocSweep, TeamId = team });
            var id = (await sp.GetRequiredService<ActionService>().GetAsync(number))!.Action.Id;
            await sp.GetRequiredService<ActionService>().AddCommentAsync(id, "Checked by the analyst on duty");
        }
        await using var page = await RenderAsync<ActionPage>(app, "/actions/" + number, new() { ["Number"] = (int?)number }, Viewer);
        var html = await page.Html();
        html.Should().Contain("Viewer&#x27;s read-only action").And.Contain("Checked by the analyst on duty");
        html.Should().NotContain("aria-label=\"Add a comment\"").And.NotContain("type=\"file\"").And.NotContain(">Take it<")
            .And.NotContain("Split off a part");
    }

    [Fact]
    public void TheNavigation_ListsActionsAndWorkload_ButNotTriageOrAdministration()
    {
        var user = new FakeUser(AppRole.Viewer);
        var keys = NavPages.For(user).Select(p => p.Key).ToList();
        keys.Should().Contain(["today", "articles", "actions", "search", "archive", "stack", "workload", "published"]);
        keys.Should().NotContain(["triage", "coverage", "admin"]);
    }
}

/// <summary>
/// The Visitor over HTTP and in present mode: the published archive only, until they sit in a live meeting, and then that
/// meeting's slides, in the team edition, for as long as they're in it.
/// </summary>
public class VisitorAppTests(VisitorApp app) : IClassFixture<VisitorApp>
{
    private static readonly SignIn Visitor = new("dev:visitor-vic", "Vic Visitor", "Visitor");

    private LiveMeetings Meetings => app.Services.GetRequiredService<LiveMeetings>();

    [Theory]
    [InlineData("/")]
    [InlineData("/briefs")]
    [InlineData("/articles")]
    [InlineData("/actions")]
    [InlineData("/actions/1")]
    [InlineData("/workload")]
    [InlineData("/stack")]
    [InlineData("/adversaries")]
    [InlineData("/stories")]
    [InlineData("/search?q=vpn")]
    [InlineData("/metrics")]
    [InlineData("/reports")]
    [InlineData("/trends")]
    [InlineData("/triage")]
    [InlineData("/rotation")]
    [InlineData("/briefs/2026-10-26/sharepoint")]
    [InlineData("/admin")]
    public async Task AVisitor_IsRefusedEachTeamPage(string path)
    {
        var client = app.CreateClient(new() { AllowAutoRedirect = false });
        var page = await client.GetAsync(path);
        page.StatusCode.Should().Be(HttpStatusCode.Redirect, path);
        page.Headers.Location!.OriginalString.Should().MatchRegex("/(access-denied|published)");
    }

    [Theory]
    [InlineData("/metrics/export.csv")]
    [InlineData("/trends/export.csv")]
    public async Task AVisitor_IsRefusedTheTeamsEndpoints(string path)
    {
        var client = app.CreateClient(new() { AllowAutoRedirect = false });
        (await client.GetAsync(path)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var day = app.Services.GetRequiredService<OrgClock>().CurrentBriefDate;
        (await client.GetAsync($"/exports/brief/{day:yyyy-MM-dd}?format=csv")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.GetAsync($"/briefs/{day:yyyy-MM-dd}/email/team")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AVisitor_OpensThePublishedArchive_AndPresentModeSaysNoMeetingIsOn()
    {
        var client = app.CreateClient(new() { AllowAutoRedirect = false });
        (await client.GetAsync("/published")).StatusCode.Should().Be(HttpStatusCode.OK);
        var day = Day(95);
        (await client.GetAsync($"/present/{day:yyyy-MM-dd}")).StatusCode.Should().Be(HttpStatusCode.OK, "a Visitor may open it, to join");

        await using var page = await Open(day);
        await page.Click(async p => { await p.GoLive(); await p.OnPresentKey("render"); });
        (await page.Html()).Should().Contain("no meeting on now").And.NotContain(Title(95));
        Meetings.Get(day).Should().BeNull();
    }

    [Fact]
    public void TheNavigation_ListsThePublishedArchiveOnly()
    {
        NavPages.For(new FakeUser(AppRole.Visitor)).Select(p => p.Key).Should().Equal("published");
    }

    [Fact]
    public async Task AVisitor_InALiveMeeting_SeesTheRoomsTeamEdition_WithNoLinksOut_AndLosesItWhenItEnds()
    {
        var (day, brief, number, _) = await Brief(96);
        await using var pat = await Open(day, SignIn.Analyst);
        await pat.Click(async p => { await p.GoLive(); await p.OnPresentKey("render"); });
        await pat.Click(p => p.StartMeeting());
        await pat.Click(p => p.OnPresentKey("last"));
        await pat.Click(p => p.OnPresentKey("prev"));   // the article's slide

        await using var vic = await Open(day);
        (await vic.Html()).Should().NotContain(Title(96), "nothing is read until the page is live and seated");
        await vic.Click(async p => { await p.GoLive(); await p.OnPresentKey("render"); });
        var live = Meetings.Get(day)!;
        live.People.Should().Contain(p => p.UserId == Visitor.UserId && !p.CanPresent);
        var html = await vic.Html();
        html.Should().Contain(Title(96)).And.Contain("Visitor&#x27;s action").And.Contain("Raise hand");
        html.Should().NotContain("Meeting notes").And.NotContain("tide-action-open").And.NotContain("Take over")
            .And.NotContain("Start the meeting").And.NotContain("actions?view=overdue").And.NotContain($"href=\"briefs/{day:yyyy-MM-dd}\"");

        // The presenter opens the action for the room: the Visitor sees it, without its page link or files.
        await pat.Click(p => p.ShowAction(number));
        await vic.Click(async p => { await p.Follow(); await p.OnPresentKey("render"); });
        html = await vic.Html();
        html.Should().Contain("present-action-title").And.NotContain("Open the page");

        // Recorded as attending, as anyone who joins is.
        await using (var scope = SignedIn(app, SignIn.Analyst))
            (await scope.ServiceProvider.GetRequiredService<MeetingService>().GetAsync(brief)).Attendees.Should().Contain(a => a.UserId == Visitor.UserId);

        await vic.Click(p => { p.ToggleHand(); return Task.CompletedTask; });
        Meetings.Get(day)!.Hands.Should().ContainSingle(h => h.UserId == Visitor.UserId);

        var presenter = Meetings.Join(day, SignIn.Analyst.UserId, SignIn.Analyst.Name, true, "", Guid.NewGuid(), 0)!;
        Meetings.End(presenter);
        await vic.Click(async p => { await p.Follow(); await p.OnPresentKey("render"); });
        html = await vic.Html();
        html.Should().Contain("no meeting on now").And.NotContain(Title(96));
        await using (var scope = SignedIn(app, Visitor))
            await scope.ServiceProvider.GetRequiredService<BriefService>().Invoking(b => b.GetAsync(day)).Should().ThrowAsync<Tide.Application.Security.ForbiddenException>();
    }

    [Fact]
    public async Task AVisitorsSlideImage_LoadsOnlyWhileSeated_AndNeverAnotherBriefs()
    {
        var (day, brief, _, shot) = await Brief(97);
        var (_, _, _, otherShot) = await Brief(98);
        var client = app.CreateClient(new() { AllowAutoRedirect = false });
        var me = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<DevAuthOptions>>().Value.UserId;
        (await client.GetAsync($"/files/{shot}/inline")).StatusCode.Should().Be(HttpStatusCode.NotFound, "no meeting on");

        using var presenter = Meetings.Start(day, brief, "dev:someone", "Someone", "c-someone", Guid.NewGuid(), 0);
        (await client.GetAsync($"/files/{shot}/inline")).StatusCode.Should().Be(HttpStatusCode.NotFound, "a meeting on, but they're not in it");
        var seat = Meetings.Join(day, me, "Dev Visitor", false, "c-visitor", Guid.NewGuid(), 0)!;
        var image = await client.GetAsync($"/files/{shot}/inline");
        image.StatusCode.Should().Be(HttpStatusCode.OK);
        image.Headers.CacheControl!.NoStore.Should().BeTrue("not kept past the meeting");
        (await client.GetAsync($"/files/{otherShot}/inline")).StatusCode.Should().Be(HttpStatusCode.NotFound, "another brief's");
        (await client.GetAsync($"/files/{shot}/download")).StatusCode.Should().Be(HttpStatusCode.NotFound, "no downloads");

        Meetings.End(presenter);
        (await client.GetAsync($"/files/{shot}/inline")).StatusCode.Should().Be(HttpStatusCode.NotFound, "the meeting ended");
        seat.Dispose();
    }

    private static string Title(int daysBack) => $"Visitor slide {daysBack}";

    private static DateOnly Day(int daysBack)
    {
        var day = DateOnly.FromDateTime(DateTime.Today);
        for (var i = 0; i < daysBack; i++) day = WorkingDays.Weekdays.Previous(day);
        return day;
    }

    private Task<Rendered<Present>> Open(DateOnly day, SignIn? who = null) =>
        RenderAsync<Present>(app, $"/present/{day:yyyy-MM-dd}", new() { ["Day"] = $"{day:yyyy-MM-dd}" }, who ?? Visitor);

    // A past day's brief with one article, a screenshot on it and an action raised from it.
    private async Task<(DateOnly Day, Guid Brief, int Action, Guid Shot)> Brief(int daysBack)
    {
        var day = Day(daysBack);
        await using var scope = SignedIn(app, SignIn.Analyst);
        var sp = scope.ServiceProvider;
        var briefs = sp.GetRequiredService<BriefService>();
        var brief = await briefs.GetOrStartAsync(day);
        var article = await sp.GetRequiredService<ArticleService>().CreateAsync(Title(daysBack), "Vendor", null, TlpLevel.Clear);
        await briefs.AddArticleAsync(brief.Id, article);
        var shot = (await sp.GetRequiredService<ScreenshotService>().AddAsync(article, [new ScreenshotUpload("console.png", Samples.Png)])).Single().FileId;
        var team = await sp.GetRequiredService<TeamService>().AddAsync("Visitor team " + daysBack, null, null);
        var number = await sp.GetRequiredService<ActionService>().CreateAsync(new ActionForm { Title = "Visitor's action " + daysBack, Kind = ActionKind.Block, TeamId = team, ArticleId = article });
        return (day, brief.Id, number, shot);
    }
}

/// <summary>Role names and descriptions wherever roles are listed, and the dev sign-in's per-browser roles.</summary>
public class RoleListingTests(TideApp app) : IClassFixture<TideApp>
{
    [Fact]
    public async Task TheRoleMappingsPage_ListsAndDescribesBothNewRoles_AndShowsWhatEachRoleCanDo()
    {
        await using var page = await RenderAsync<AccessAdmin>(app, "/admin/access");
        var html = await page.Html();
        html.Should().Contain(">Viewer<").And.Contain(">Visitor<").And.Contain("What each role can do")
            .And.Contain("A colleague from elsewhere")
            .And.Contain("only its live meeting, while in it").And.Contain("own team&#x27;s").And.Contain("<span class=\"visually-hidden\">No</span>");
    }

    [Fact]
    public void EveryRole_HasALabelAndADescription()
    {
        foreach (var role in Enum.GetValues<AppRole>())
        {
            Tide.Application.Security.RoleDefinitions.Describe(role).Should().NotBe(role.ToString());
            Tide.Application.Security.RoleDefinitions.SystemRoles.Should().ContainKey(role);
        }
        Tide.Application.Security.RoleDefinitions.Label(AppRole.Viewer).Should().Be("Viewer");
        Tide.Application.Security.RoleDefinitions.Label(AppRole.Visitor).Should().Be("Visitor");
        ((int)AppRole.Viewer, (int)AppRole.Visitor, (int)AppRole.Admin).Should().Be((4, 5, 3), "appended, nothing renumbered");
    }

    [Fact]
    public async Task TheDevSignIn_TakesRolesForThisBrowser_OnlyTidesOwn()
    {
        DevAuthenticationHandler.ParseRoles("viewer, Visitor,Nope,,Admin1").Should().Equal("Viewer", "Visitor");
        DevAuthenticationHandler.ParseRoles(null).Should().BeEmpty();

        var client = app.CreateClient(new() { AllowAutoRedirect = false });
        var asked = await client.GetAsync("/briefs?roles=Visitor");
        asked.StatusCode.Should().Be(HttpStatusCode.Redirect, "a Visitor at once");
        asked.Headers.GetValues("Set-Cookie").Should().ContainSingle(c => c.StartsWith("dev_roles=Visitor"));
        var kept = new HttpRequestMessage(HttpMethod.Get, "/briefs");
        kept.Headers.Add("Cookie", "dev_roles=Visitor");
        (await client.SendAsync(kept)).StatusCode.Should().Be(HttpStatusCode.Redirect, "the cookie keeps them a Visitor");
        var forged = new HttpRequestMessage(HttpMethod.Get, "/briefs");
        forged.Headers.Add("Cookie", "dev_roles=Superuser");
        (await client.SendAsync(forged)).StatusCode.Should().Be(HttpStatusCode.OK, "a name that isn't a role is ignored: DevAuth:Roles");
        var back = await client.GetAsync("/briefs?roles=default");
        back.StatusCode.Should().Be(HttpStatusCode.OK, "back to DevAuth:Roles");
        back.Headers.GetValues("Set-Cookie").Should().ContainSingle(c => c.StartsWith("dev_roles=;"));
    }
}
