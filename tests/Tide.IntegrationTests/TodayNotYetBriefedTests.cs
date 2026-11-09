using System.Net;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Abstractions;
using Tide.Application.Articles;
using Tide.Application.Org;
using Tide.Domain.Enums;
using Tide.Web.Components.Pages;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>
/// Today's "Not on a brief yet", for whoever writes the brief: ready articles (anyone's) and their own drafts that have
/// never been on a brief, ready first, each with Add. Its own app, so adding to today's brief reaches no other test.
/// </summary>
public class TodayNotYetBriefedTests(TideApp app) : IClassFixture<TideApp>
{
    private static readonly SignIn Analyst = SignIn.Analyst;

    private DateOnly Today => app.Services.GetRequiredService<OrgClock>().CurrentBriefDate;

    private async Task<Guid> Article(string title, SignIn who, ArticleStatus status = ArticleStatus.Draft, TlpLevel tlp = TlpLevel.Clear)
    {
        Guid id;
        await using (var scope = SignedIn(app, who))
            id = await scope.ServiceProvider.GetRequiredService<ArticleService>().CreateAsync(title, "Vendor", null, tlp);
        if (status != ArticleStatus.Draft)
        {
            await using var scope = app.Services.CreateAsyncScope();
            await using var db = await scope.ServiceProvider.GetRequiredService<IAppDbContextFactory>().CreateAsync();
            (await db.Articles.SingleAsync(a => a.Id == id)).Status = status;
            await db.SaveChangesAsync();
        }
        return id;
    }

    [Fact]
    public async Task ReadyArticlesThenMyDrafts_NotOnAnyBrief_AreListed_AndAddPutsOneOnTodaysBrief()
    {
        var ready = await Article("NYB ready vendor advisory", Analyst, ArticleStatus.Ready);
        var mine = await Article("NYB my draft", Analyst);
        await Article("NYB someone else's draft", new SignIn("dev:nyb-other", "Other Analyst", "Analyst"));

        await using var r = await RenderAsync<BriefPage>(app, "/", new() { ["Day"] = $"{Today:yyyy-MM-dd}", ["AsToday"] = true });
        var html = WebUtility.HtmlDecode(await r.Html());
        html.Should().Contain("Not on a brief yet").And.NotContain("Recently written");
        var listed = r.Component.NotYetBriefed.Select(c => c.Title).ToList();
        listed.Should().Contain(["NYB ready vendor advisory", "NYB my draft"]).And.NotContain("NYB someone else's draft");
        listed.IndexOf("NYB ready vendor advisory").Should().BeLessThan(listed.IndexOf("NYB my draft"), "ready ones first");
        r.Component.NotYetBriefed.Should().OnlyContain(c => c.LastBriefed == null && c.Available);
        html.Should().Contain("aria-label=\"Add NYB ready vendor advisory to today's brief\"");

        await r.Click(c => c.AddFromCard(c.NotYetBriefed.Single(x => x.Id == ready)));
        r.Component.NotYetBriefed.Select(c => c.Id).Should().NotContain(ready, "it's on today's brief now").And.Contain(mine);
        await using var scope = app.Services.CreateAsyncScope();
        await using var db = await scope.ServiceProvider.GetRequiredService<IAppDbContextFactory>().CreateAsync();
        (await db.BriefItems.AnyAsync(i => i.ArticleId == ready && db.Briefs.Any(b => b.Id == i.BriefId && b.BriefDate == Today))).Should().BeTrue();
    }

    [Fact]
    public async Task AReader_GetsOnlyWhatsBeenPresented_AtOrBelowTheTeamEditionsTlp()
    {
        await Article("NYB presented to all", Analyst, ArticleStatus.Presented);
        await Article("NYB presented, red", Analyst, ArticleStatus.Presented, TlpLevel.Red);
        await Article("NYB a draft readers can't see", Analyst);

        await using var r = await RenderAsync<BriefPage>(app, "/", new() { ["Day"] = $"{Today:yyyy-MM-dd}", ["AsToday"] = true }, new SignIn("dev:nyb-reader", "Reader", "ActionOwner"));
        var html = WebUtility.HtmlDecode(await r.Html());
        html.Should().NotContain("Not on a brief yet", "they don't write the brief").And.Contain("Recently presented")
            .And.Contain("NYB presented to all")
            .And.NotContain("NYB presented, red", "above the team edition's ceiling")
            .And.NotContain("NYB a draft readers can't see", "a draft's title isn't theirs to see");
    }
}
