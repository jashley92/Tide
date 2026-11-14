using System.Net;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Abstractions;
using Tide.Application.Org;
using Tide.Application.Trends;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Web.Components.Layout;
using Tide.Web.Components.Pages;
using Tide.Web.Components.Shared;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>
/// Reports › Brief timeliness over HTTP and on the page: who opens it, and that no analyst reads another's days by any
/// route (the page, its address, the CSV, the API). Two past brief days are seeded: Pat's on time, Robin's late.
/// </summary>
public class BriefTimelinessPageTests(TideApp app) : IClassFixture<TideApp>, IAsyncLifetime
{
    private const string Pat = "dev:pat", Robin = "dev:robin";
    private static readonly SignIn PatAnalyst = new(Pat, "Pat", "Analyst");
    private static readonly SignIn Lee = new("dev:lee", "Lee", "Manager");
    private DateOnly _patDay, _robinDay;

    public async Task InitializeAsync()
    {
        var clock = app.Services.GetRequiredService<OrgClock>();
        _robinDay = clock.Days.Previous(clock.Today);
        _patDay = clock.Days.Previous(_robinDay);
        await using var scope = app.Services.CreateAsyncScope();
        await using var c = await scope.ServiceProvider.GetRequiredService<IAppDbContextFactory>().CreateAsync();
        if (c.Briefs.Any(b => b.BriefDate == _patDay)) return;
        foreach (var (day, who, name, late) in new[] { (_patDay, Pat, "Pat", false), (_robinDay, Robin, "Robin", true) })
        {
            var article = new Article { Title = "Timeliness " + name, Tlp = TlpLevel.Clear, AuthorId = who, AuthorName = name };
            var brief = new Brief { BriefDate = day, AnalystId = who, AnalystName = name, Status = BriefStatus.Sent };
            brief.Items.Add(new BriefItem { ArticleId = article.Id, Article = article });
            c.Articles.Add(article);
            c.Briefs.Add(brief);
            var at = OrgClock.At(clock.Zone, day, clock.SentBy).AddMinutes(late ? 25 : -20);
            foreach (var e in new[] { Edition.Team, Edition.Public })
                c.BriefSends.Add(new BriefSend { BriefId = brief.Id, BriefDate = day, Edition = e, Status = SendStatus.Sent, SentAtUtc = at });
        }
        await c.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private HttpClient Client() => app.CreateClient(new() { AllowAutoRedirect = false });

    [Theory]
    [InlineData("Analyst")]
    [InlineData("Manager")]
    [InlineData("Admin")]
    public async Task TheAnalystsTheManagersAndTheAdministrators_OpenThePage(string role)
    {
        (await Client().GetAsync($"/reports/timeliness?as=Pat&roles={role}")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("Viewer")]
    [InlineData("Reader")]
    [InlineData("ActionOwner")]
    [InlineData("Visitor")]
    public async Task EveryoneElse_IsTurnedAway_FromThePageAndItsCsv(string role)
    {
        var page = await Client().GetAsync($"/reports/timeliness?as=Val&roles={role}");
        page.StatusCode.Should().Be(HttpStatusCode.Redirect);
        page.Headers.Location!.OriginalString.Should().StartWith("/access-denied");
        (await Client().GetAsync($"/reports/timeliness/export.csv?as=Val&roles={role}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AnAnalyst_ReadsOnlyTheirOwnDays_InTheCsv_AndIsRefusedAnothers()
    {
        var mine = await Client().GetAsync("/reports/timeliness/export.csv?as=Pat&roles=Analyst");
        mine.StatusCode.Should().Be(HttpStatusCode.OK);
        var csv = await mine.Content.ReadAsStringAsync();
        csv.Should().Contain(Ui.IsoDay(_patDay)).And.NotContain("Robin").And.NotContain(Ui.IsoDay(_robinDay));

        var theirs = await Client().GetAsync($"/reports/timeliness/export.csv?as=Pat&roles=Analyst&who={Robin}");
        theirs.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await theirs.Content.ReadAsStringAsync()).Should().NotContain("Robin");
    }

    [Fact]
    public async Task AManager_ReadsEveryonesDays_InTheCsv()
    {
        var csv = await (await Client().GetAsync("/reports/timeliness/export.csv?as=Lee&roles=Manager")).Content.ReadAsStringAsync();
        csv.Should().Contain("Pat").And.Contain("Robin").And.Contain(",late,25,");
    }

    [Fact]
    public async Task AnAnalystsPage_ShowsOnlyTheirDays_AndAnotherAnalystsAddressShowsNothingOfThem()
    {
        await using (var page = await RenderAsync<BriefTimeliness>(app, "/reports/timeliness", @as: PatAnalyst))
        {
            var html = await page.Html();
            html.Should().Contain("Day by day").And.Contain("On time").And.NotContain("Robin").And.NotContain("Team summary")
                .And.NotContain("Notified of late");
        }
        await using (var page = await RenderAsync<BriefTimeliness>(app, $"/reports/timeliness?who={Robin}", @as: PatAnalyst))
        {
            var html = await page.Html();
            html.Should().Contain("You can see only your own brief timeliness.").And.NotContain("Robin").And.NotContain("Late by");
        }
    }

    [Fact]
    public async Task AManagersPage_HasTheTeamSummary_AndExcusesALateDay_WhichTheAnalystThenSees()
    {
        await using (var page = await RenderAsync<BriefTimeliness>(app, "/reports/timeliness?by=week", @as: Lee))
        {
            var html = await page.Html();
            html.Should().Contain("Team summary").And.Contain("Pat").And.Contain("Robin").And.Contain("Late by 25 min").And.Contain("Notified of late");
        }
        await using (var scope = SignedIn(app, Lee))
        {
            var svc = scope.ServiceProvider.GetRequiredService<BriefTimelinessService>();
            var report = await svc.BuildAsync(PeriodKind.Week, Robin);
            if (report.Days.Single(d => d.Date == _robinDay).Excuse is { } was) await svc.UndoExcuseAsync(_robinDay, was.EditVersion);
            await svc.ExcuseAsync(_robinDay, "Told me at 8:00 the relay was down");
        }
        await using (var page = await RenderAsync<BriefTimeliness>(app, "/reports/timeliness", @as: new SignIn(Robin, "Robin", "Analyst")))
        {
            var html = WebUtility.HtmlDecode(await page.Html());
            html.Should().Contain("Told me at 8:00 the relay was down").And.Contain("Late by 25 min · excused").And.NotContain("Undo the excuse");
        }
    }

    [Fact]
    public async Task TheApi_HasNoTimelinessRoute()
    {
        (await Client().GetAsync("/api/v1/timeliness?as=Pat&roles=Analyst")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ReportsTabs_OfferEachTabOnlyToWhoeverItsPageAdmits()
    {
        var viewer = new FakeUser(AppRole.Viewer);
        ReportsTabs.For(viewer).Select(t => t.Key).Should().Equal(ReportsTabs.Report, ReportsTabs.Teams);
        ReportsTabs.For(new FakeUser(AppRole.Analyst)).Select(t => t.Key).Should().Equal(ReportsTabs.Report, ReportsTabs.Teams, ReportsTabs.Timeliness);
        ReportsTabs.For(new FakeUser(AppRole.Manager)).Select(t => t.Key).Should().Equal(ReportsTabs.Report, ReportsTabs.Teams, ReportsTabs.Timeliness);
        ReportsTabs.For(new FakeUser(AppRole.Reader)).Should().BeEmpty();
        NavPages.For(new FakeUser(AppRole.Manager)).Should().Contain(p => p.Key == "reports");
        NavPages.For(viewer).Should().NotContain(p => p.Key == "reports", "a Viewer opens Reports but it isn't listed for them, as before");

        await using var page = await RenderAsync<Reports>(app, "/reports", @as: new SignIn("dev:val", "Val", "Viewer"));
        var html = await page.Html();
        html.Should().Contain("Team commitments").And.NotContain("Brief timeliness");
    }

    [Fact]
    public void EachTab_IsOfferedByItsPagesOwnPolicy()
    {
        var pages = new Dictionary<string, Type> { [ReportsTabs.Report] = typeof(Reports), [ReportsTabs.Teams] = typeof(TeamCommitments), [ReportsTabs.Timeliness] = typeof(BriefTimeliness) };
        foreach (var tab in ReportsTabs.All)
            System.Reflection.CustomAttributeExtensions.GetCustomAttribute<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>(pages[tab.Key])!.Policy.Should().Be(tab.Policy, tab.Key);
    }

    [Theory]
    [InlineData("/reports")]
    [InlineData("/reports/teams")]
    [InlineData("/reports/timeliness")]
    public async Task EachReportsPage_HasTheTabs_ItsOwnCurrent(string path)
    {
        var html = await (await Client().GetAsync(path)).Content.ReadAsStringAsync();
        html.Should().Contain("aria-label=\"Reports\"").And.Contain("href=\"reports/timeliness\"").And.Contain("href=\"reports/teams\"")
            .And.Contain($"href=\"{path.TrimStart('/')}\" aria-current=\"page\"");
    }
}
