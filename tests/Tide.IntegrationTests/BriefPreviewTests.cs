using System.Net;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Org;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Infrastructure.Persistence;
using Tide.Web.Components.Pages;

namespace Tide.IntegrationTests;

/// <summary>
/// The brief's preview of both editions (/briefs/{day}?tab=preview), which took the place of the Team edition and
/// Company-wide edition tabs, and the old links: ?tab=team and ?tab=company open it, ?tab=distribute opens the Send
/// dialog. The sample data has today's brief with three articles, one of them TLP:AMBER.
/// </summary>
public class BriefPreviewTests(TideApp app) : IClassFixture<TideApp>
{
    private DateOnly Today => app.Services.GetRequiredService<OrgClock>().CurrentBriefDate;

    internal static int At(string html, string text)
    {
        var at = html.IndexOf(text, StringComparison.Ordinal);
        at.Should().BeGreaterThanOrEqualTo(0, $"the page should contain {text}");
        return at;
    }

    /// <summary>The team and company-wide sections of the preview.</summary>
    internal static (string Team, string Company) Sections(string html) =>
        (html[At(html, "aria-labelledby=\"proof-team-h\"")..At(html, "aria-labelledby=\"proof-company-h\"")],
         html[At(html, "aria-labelledby=\"proof-company-h\"")..]);

    private static int Count(string html, string what) => html.Split(what).Length - 1;

    [Fact]
    public async Task ThePreview_ShowsBothEditionsAsReadersGetThem_AndTheCompanyWideLeavesOutTheAmberArticle()
    {
        var html = await app.CreateClient().GetStringAsync($"/briefs/{Today:yyyy-MM-dd}?tab=preview");

        // Two tabs, the preview the current one; no edition or Distribute tabs.
        html.Should().Contain("role=\"tablist\" aria-label=\"Brief\"").And.Contain("role=\"tabpanel\" id=\"brief-panel\" aria-labelledby=\"brief-panel-tab-1\"");
        html.Should().Contain("id=\"brief-panel-tab-1\" class=\"tide-tabbar-item active\" aria-selected=\"true\"")
            .And.Contain("<span>Preview both editions</span>").And.Contain("<span>Running order</span>");
        Count(html, "role=\"tab\"").Should().Be(2);
        html.Should().NotContain("<span>Distribute</span>").And.NotContain("<span>Team edition</span>").And.NotContain("aria-label=\"Brief progress\"");

        // Each edition headed by its name, marking and who it's for, then drawn as readers get it.
        var (team, company) = Sections(html);
        team.Should().Contain("id=\"proof-team-h\">Security team edition").And.Contain("What the security teams see").And.Contain("tide-edition-banner");
        company.Should().Contain("id=\"proof-company-h\">Company-wide edition").And.Contain("What the company sees").And.Contain("tide-edition-banner");
        Count(team, "<article class=\"tide-edition-article\">").Should().Be(3);
        Count(company, "<article class=\"tide-edition-article\">").Should().Be(2, "the TLP:AMBER article is above the company-wide ceiling");
        company.Should().Contain("1 restricted item was briefed to the security team and left out of this edition.");

        // The switch for a narrow screen: the team edition shown first.
        html.Should().Contain("class=\"tide-seg tide-both-switch\" role=\"group\" aria-label=\"Edition to show\"")
            .And.Contain("class=\"tide-both-ed is-shown\" aria-labelledby=\"proof-team-h\"")
            .And.Contain("class=\"tide-both-ed \" aria-labelledby=\"proof-company-h\"");
    }

    [Theory]
    [InlineData("team", "proof-team-h")]
    [InlineData("company", "proof-company-h")]
    [InlineData("Company", "proof-company-h")]
    public async Task TheOldEditionTabs_OpenThePreview_WithThatEditionShownOnANarrowScreen(string tab, string shown)
    {
        var res = await app.CreateClient(new() { AllowAutoRedirect = false }).GetAsync($"/briefs/{Today:yyyy-MM-dd}?tab={tab}");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await res.Content.ReadAsStringAsync();
        html.Should().Contain($"class=\"tide-both-ed is-shown\" aria-labelledby=\"{shown}\"").And.Contain("aria-labelledby=\"brief-panel-tab-1\"");
        Count(html, "tide-both-ed is-shown").Should().Be(1);
    }

    [Fact]
    public async Task TodaysEditionCheck_LinksToThePreview_AndNotToDistribute()
    {
        var html = await app.CreateClient().GetStringAsync("/");
        var check = html[At(html, "id=\"edition-check-h\"")..At(html, "id=\"meeting-h\"")];
        check.Should().Contain($"href=\"briefs/{Today:yyyy-MM-dd}?tab=preview\">Preview both editions</a>")
            .And.Contain($"briefs/{Today:yyyy-MM-dd}?tab=team").And.Contain($"briefs/{Today:yyyy-MM-dd}?tab=company");
        html.Should().NotContain("?tab=distribute").And.NotContain("role=\"tablist\"");
    }

    [Fact]
    public async Task TodaysAddress_GoesToToday_KeepingTheSendDialog_ButThePreviewStaysOnIt()
    {
        var client = app.CreateClient(new() { AllowAutoRedirect = false });
        string Where(HttpResponseMessage r) => new Uri(new Uri("http://localhost/"), r.Headers.Location!).PathAndQuery;

        var plain = await client.GetAsync($"/briefs/{Today:yyyy-MM-dd}");
        plain.StatusCode.Should().Be(HttpStatusCode.Redirect);
        Where(plain).Should().Be("/");

        var send = await client.GetAsync($"/briefs/{Today:yyyy-MM-dd}?tab=distribute");
        send.StatusCode.Should().Be(HttpStatusCode.Redirect);
        Where(send).Should().Be("/?tab=distribute");

        var preview = await client.GetAsync($"/briefs/{Today:yyyy-MM-dd}?tab=preview");
        preview.StatusCode.Should().Be(HttpStatusCode.OK, "the preview has no place on Today, so it stays on the brief's address");
    }

    [Theory]
    [InlineData(null, false, Edition.Team, false)]
    [InlineData("preview", true, Edition.Team, false)]
    [InlineData("PREVIEW", true, Edition.Team, false)]
    [InlineData("team", true, Edition.Team, false)]
    [InlineData("company", true, Edition.Public, false)]
    [InlineData("distribute", false, Edition.Team, true)]
    [InlineData("nonsense", false, Edition.Team, false)]
    public void ATabQuery_OpensThePreviewOrTheSendDialog(string? query, bool preview, Edition shown, bool send) =>
        BriefPage.TabFor(query).Should().Be(new BriefPage.BriefTab(preview, shown, send));

    [Fact]
    public async Task TheOldDistributeLink_OpensTheSendDialog_OnToday()
    {
        await using var r = await ComponentHarness.RenderAsync<Home>(app, "/?tab=distribute");
        var html = await r.HtmlOnce("id=\"send-public-h\"");
        var dialog = html[At(html, "tide-modal-scrim")..];
        dialog.Should().Contain(">Send the brief</h2>").And.Contain("Send a test copy to me").And.Contain("id=\"send-team-h\"").And.Contain("id=\"send-public-h\"");
    }
}

/// <summary>Sending from a day that isn't today, in its own app: a draft for the next business day.</summary>
public class SendFromAnotherDayTests(TideApp app) : IClassFixture<TideApp>
{
    private async Task<DateOnly> ADraftForTheNextDay()
    {
        var day = WorkingDays.Weekdays.Next(app.Services.GetRequiredService<OrgClock>().CurrentBriefDate);
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (await db.Briefs.AnyAsync(b => b.BriefDate == day)) return day;
        var brief = new Brief { BriefDate = day, AnalystId = "system", AnalystName = "Sample data", Status = BriefStatus.Draft };
        brief.Items.Add(new BriefItem { ArticleId = (await db.Articles.FirstAsync()).Id, SortOrder = 0 });
        db.Briefs.Add(brief);
        await db.SaveChangesAsync();
        return day;
    }

    [Fact]
    public async Task AnotherDaysRunningOrder_HasTheStepStripsSendButton_WhichOpensTheDialog()
    {
        var day = await ADraftForTheNextDay();
        var html = await app.CreateClient().GetStringAsync($"/briefs/{day:yyyy-MM-dd}");
        html.Should().Contain("aria-label=\"Brief progress\"").And.Contain("title=\"Send a test copy, then each edition\"").And.Contain("aria-label=\"Brief\"");

        await using var r = await ComponentHarness.RenderAsync<BriefPage>(app, $"/briefs/{day:yyyy-MM-dd}", new() { ["Day"] = $"{day:yyyy-MM-dd}" });
        (await r.Html()).Should().NotContain("tide-modal");
        await r.Press(p => p.AskSend());
        var dialog = await r.HtmlOnce("id=\"send-public-h\"");
        dialog.Should().Contain(">Send the brief</h2>").And.Contain("Send a test copy to me").And.Contain("id=\"send-team-h\"").And.Contain("Send log");
    }

    [Fact]
    public async Task TheOldDistributeLink_OpensTheSendDialog_OnAnotherDay()
    {
        var day = await ADraftForTheNextDay();
        await using var r = await ComponentHarness.RenderAsync<BriefPage>(app, $"/briefs/{day:yyyy-MM-dd}?tab=distribute", new() { ["Day"] = $"{day:yyyy-MM-dd}" });
        var html = await r.HtmlOnce("id=\"send-public-h\"");
        html.Should().Contain("tide-modal-scrim").And.Contain(">Send the brief</h2>").And.Contain("aria-label=\"Brief progress\"", "behind it, the running order");
    }
}

public class ActionOwnerBriefPreviewTests(ActionOwnerApp app) : IClassFixture<ActionOwnerApp>
{
    private DateOnly Today => app.Services.GetRequiredService<OrgClock>().CurrentBriefDate;

    [Fact]
    public async Task AnActionOwner_ReadsThePreview_WithNothingToChange()
    {
        var res = await app.CreateClient(new() { AllowAutoRedirect = false }).GetAsync($"/briefs/{Today:yyyy-MM-dd}?tab=preview");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await res.Content.ReadAsStringAsync();
        html.Should().Contain("role=\"tablist\" aria-label=\"Brief\"").And.Contain("<span>Preview both editions</span>");
        var (team, company) = BriefPreviewTests.Sections(html);
        team.Should().Contain("tide-edition-banner");
        company.Should().Contain("tide-edition-banner");
        html.Should().NotContain("Mark ready").And.NotContain("Back to draft").And.NotContain("Present</a>");
    }

    [Fact]
    public async Task TheOldDistributeLink_ShowsAnActionOwnerTheSendLogOnly()
    {
        await using var r = await ComponentHarness.RenderAsync<Home>(app, "/?tab=distribute", null,
            new ComponentHarness.SignIn("dev:owner", "Owner", "ActionOwner"));
        var html = await r.HtmlOnce("tide-modal-scrim");
        var dialog = html[BriefPreviewTests.At(html, "tide-modal-scrim")..];
        dialog.Should().Contain(">Send log</h2>").And.Contain(">Send log</h3>").And.NotContain("Send a test copy to me").And.NotContain("bi bi-send-check");
    }
}

public class ReaderBriefPreviewTests(ReaderApp app) : IClassFixture<ReaderApp>
{
    [Fact]
    public async Task AReader_CantOpenThePreview()
    {
        var day = app.Services.GetRequiredService<OrgClock>().CurrentBriefDate;
        var res = await app.CreateClient(new() { AllowAutoRedirect = false }).GetAsync($"/briefs/{day:yyyy-MM-dd}?tab=preview");
        res.StatusCode.Should().Be(HttpStatusCode.Redirect);
        res.Headers.Location!.OriginalString.Should().StartWith("/access-denied?from=");
    }
}
