using System.Net;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Articles;
using Tide.Application.Briefs;
using Tide.Application.Reviews;
using Tide.Domain.Enums;
using Tide.Web.Components.Pages;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>The SharePoint copy's images zip is held for review as the copy is, and the page says so rather than offering a
/// download that fails without a reason.</summary>
public class SharePointPageTests(TideApp app) : IClassFixture<TideApp>
{
    [Fact]
    public async Task TheCopy_OffersMarkPosted_AndOnceMarkedSaysWhoAndWhen()
    {
        DateOnly day;
        await using (var scope = SignedIn(app, SignIn.Analyst))
        {
            var sp = scope.ServiceProvider;
            day = await Audit.WebSeed.SendableDayAsync(sp);
            var articles = sp.GetRequiredService<ArticleService>();
            var id = await articles.CreateAsync("Posted story", "Vendor", null, TlpLevel.Clear);
            var briefs = sp.GetRequiredService<BriefService>();
            await briefs.AddArticleAsync((await briefs.GetOrStartAsync(day)).Id, id);
        }
        var iso = day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            await using var r = await RenderAsync<Tide.Web.Components.Shared.SharePointPosted>(app, $"/briefs/{iso}/sharepoint", new() { ["Date"] = day });
            (await r.Html()).Should().Contain("Not posted to SharePoint yet.", "not required by default").And.Contain("Mark posted to SharePoint");

            await r.Click(p => p.Mark(true));

            await using var page = await RenderAsync<SharePointPage>(app, $"/briefs/{iso}/sharepoint", new() { ["Day"] = iso });
            (await page.Html()).Should().Contain("Posted to SharePoint by").And.Contain("Undo");
        }
        finally
        {
            await using var scope = SignedIn(app, SignIn.Analyst);
            await scope.ServiceProvider.GetRequiredService<Tide.Application.Distribution.DistributionService>().MarkSharePointPostedAsync(day, posted: false);
        }
    }

    [Fact]
    public async Task TheImagesZip_HeldSinceThePageWasRead_SaysWhyOnThePage_AndTheAddressAnswersWithText()
    {
        DateOnly day;
        Guid threat;
        await using (var scope = SignedIn(app, SignIn.Analyst))
        {
            var sp = scope.ServiceProvider;
            day = await Audit.WebSeed.SendableDayAsync(sp);
            var articles = sp.GetRequiredService<ArticleService>();
            threat = await articles.CreateAsync("Zip threat story", "Vendor", null, TlpLevel.Clear);
            var form = ArticleForm.From((await articles.GetAsync(threat))!);
            form.Classification = Classification.Threat;
            form.Priority = Priority.P2;
            await articles.SaveAsync(form);
            var briefs = sp.GetRequiredService<BriefService>();
            await briefs.AddArticleAsync((await briefs.GetOrStartAsync(day)).Id, threat);
        }
        var iso = day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        await using var r = await RenderAsync<SharePointPage>(app, $"/briefs/{iso}/sharepoint", new() { ["Day"] = iso });
        (await r.Html()).Should().Contain("id=\"sp-copy\"", "review isn't required yet").And.Contain("Zip threat story");

        // Review is now required, and the threat story isn't reviewed: the zip is held, as the copy is.
        await using (var scope = SignedIn(app, SignIn.Analyst))
            await scope.ServiceProvider.GetRequiredService<ReviewService>().SaveRuleAsync(ReviewRule.Threats);
        try
        {
            await r.Click(p => p.Images());

            var html = await r.Html();
            html.Should().Contain("Review is required before this edition is copied for SharePoint").And.Contain("Zip threat story");
            html.Should().NotContain("Images (.zip").And.NotContain("id=\"sp-copy\"", "nothing held is offered");

            var res = await app.CreateClient().GetAsync($"/briefs/{iso}/sharepoint-images.zip");
            res.StatusCode.Should().Be(HttpStatusCode.Conflict);
            res.Content.Headers.ContentType!.MediaType.Should().Be("text/plain");
            (await res.Content.ReadAsStringAsync()).Should().StartWith("Review is required before this edition is copied for SharePoint");
        }
        finally
        {
            await using var scope = SignedIn(app, SignIn.Analyst);
            await scope.ServiceProvider.GetRequiredService<ReviewService>().SaveRuleAsync(ReviewRule.Off);
        }
    }
}
