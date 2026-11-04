using System.Net;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Articles;
using Tide.Application.Briefs;
using Tide.Application.Distribution;
using Tide.Application.Org;
using Tide.Application.Settings;
using Tide.Domain.Enums;
using Tide.Infrastructure.Persistence;
using Tide.Web.Services;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>
/// The published briefs page as a Reader gets it: each sent company-wide edition with its headlines, and a filter on
/// its words. Both come only from the frozen sends, so an AMBER article, or one never sent company-wide, never shows.
/// </summary>
public class ReaderPublishedTests(ReaderApp app) : IClassFixture<ReaderApp>
{
    private const string NeverPublished = "Lighthouse keepers rotate their shared passphrase";

    // Today's sample brief (two open articles and an AMBER one) sent in both editions; the day before it, a CLEAR
    // article sent to the security teams only. Done once for the app, as an analyst.
    private async Task<(string Sent, string Amber)> Sent()
    {
        await using var scope = SignedIn(app, SignIn.Analyst);
        var sp = scope.ServiceProvider;
        var today = await Tide.IntegrationTests.Audit.WebSeed.SendableDayAsync(sp);
        var before = WorkingDays.Weekdays.Previous(today);
        var db = sp.GetRequiredService<AppDbContext>();
        var items = (await BriefService.Load(db.Briefs.AsNoTracking()).SingleAsync(b => b.BriefDate == today)).Items.Select(i => i.Article!).ToList();
        var titles = (items.First(a => a.Tlp <= TlpLevel.Green).Title, items.First(a => a.Tlp >= TlpLevel.Amber).Title);
        if (await db.BriefSends.AnyAsync()) return titles;

        var settings = sp.GetRequiredService<DistributionSettings>();
        await settings.SaveRecipientsAsync(Edition.Team, "cti@example.org");
        await settings.SaveRecipientsAsync(Edition.Public, "everyone@example.org");

        var briefs = sp.GetRequiredService<BriefService>();
        var article = await sp.GetRequiredService<ArticleService>().CreateAsync(NeverPublished, "Vendor", null, TlpLevel.Clear);
        var old = await briefs.GetOrStartAsync(before);
        await briefs.AddArticleAsync(old.Id, article);

        var sends = sp.GetRequiredService<DistributionService>();
        await sends.SendAsync(await sends.PlanAsync(before, Edition.Team), withoutMeeting: true);
        await sends.SendAsync(await sends.PlanAsync(today, Edition.Team), withoutMeeting: true);
        await sends.SendAsync(await sends.PlanAsync(today, Edition.Public));
        return titles;
    }

    private async Task<string> Page(string path)
    {
        var res = await app.CreateClient(new() { AllowAutoRedirect = false }).GetAsync(path);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        return WebUtility.HtmlDecode(await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task TheList_ShowsEachSentEditionsHeadlines_ButNeverAnAmberOrUnpublishedArticle()
    {
        var (sent, amber) = await Sent();

        var html = await Page("/published");

        html.Should().Contain("role=\"search\"").And.Contain("aria-label=\"Filter the published briefs\"");
        html.Should().Contain("<ol class=\"tide-published-heads\">").And.Contain($"<li>{sent}</li>");
        html.Should().NotContain(amber, "the AMBER article was left out of the company-wide edition")
            .And.NotContain(NeverPublished, "that brief went to the security teams only");
    }

    [Fact]
    public async Task TheFilter_FindsASentHeadline_AndNothingFromAnAmberOrUnpublishedArticle()
    {
        var (sent, amber) = await Sent();

        var hit = await Page("/published?q=" + Uri.EscapeDataString(sent.ToUpperInvariant()));
        hit.Should().Contain("1 published brief matches.").And.Contain($"<li>{sent}</li>");

        foreach (var q in new[] { amber, NeverPublished, "credential harvesting" })
        {
            var miss = await Page("/published?q=" + Uri.EscapeDataString(q));
            miss.Should().Contain("No matching briefs", q).And.NotContain("tide-published-heads");
        }
    }

    [Fact]
    public async Task ShowOlderBriefs_WhoseReadFails_SaysSo_AndKeepsTheList()
    {
        var (sent, _) = await Sent();
        await using var r = await RenderAsync<Tide.Web.Components.Pages.Published>(app, "/published", @as: new SignIn("dev:reader", "Reader", "Reader"));
        (await r.Html()).Should().Contain($"<li>{sent}</li>");

        // The read is refused (as one that fails or is cancelled would throw): no longer signed in with the permission.
        r.Signedin.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "dev:reader")], "Test"));
        await r.Click(p => p.Older());

        r.Scope.ServiceProvider.GetRequiredService<ToastService>().Toasts.Should().ContainSingle(t => t.IsError, "a toast, not the error boundary");
        (await r.Html()).Should().Contain($"<li>{sent}</li>");
    }
}
