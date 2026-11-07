using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Articles;
using Tide.Application.Briefs;
using Tide.Application.Org;
using Tide.Domain.Enums;
using Tide.Web.Components.Pages;
using Tide.Web.Services;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>
/// Bug round 4, the page-level flows: the brief editor's Undo after taking an article off, once the page has moved on;
/// a search of only one-letter words.
/// </summary>
public class PagesRound4BugTests(TideApp app) : IClassFixture<TideApp>
{
    private DateOnly Today => app.Services.GetRequiredService<OrgClock>().CurrentBriefDate;

    private static DateOnly Before(DateOnly day, int n)
    {
        for (var i = 0; i < n; i++) day = WorkingDays.Weekdays.Previous(day);
        return day;
    }

    [Fact]
    public async Task UndoingATakeOff_AfterSteppingToTheNextDay_PutsTheArticleBackOnTheBriefItCameOff()
    {
        var day = Before(Today, 172);
        var next = WorkingDays.Weekdays.Next(day);
        Guid first, second;
        await using (var scope = SignedIn(app, SignIn.Analyst))
        {
            var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
            var briefs = scope.ServiceProvider.GetRequiredService<BriefService>();
            var brief = await briefs.GetOrStartAsync(day);
            first = await articles.CreateAsync("Undo round 4 first", "Vendor", null, TlpLevel.Clear);
            second = await articles.CreateAsync("Undo round 4 second", "Vendor", null, TlpLevel.Clear);
            await briefs.AddArticleAsync(brief.Id, first);
            await briefs.AddArticleAsync(brief.Id, second);
            await briefs.GetOrStartAsync(next);
        }

        await using var r = await RenderAsync<BriefPage>(app, $"/briefs/{day:yyyy-MM-dd}", new() { ["Day"] = $"{day:yyyy-MM-dd}" });
        var item = (await Briefs(r).GetAsync(day))!.Items.Single(i => i.ArticleId == first);
        await r.Click(p => p.Remove(item));
        var toasts = r.Scope.ServiceProvider.GetRequiredService<ToastService>();
        var undo = toasts.Toasts.Single(t => t.Undo is not null && t.Message.Contains("Undo round 4 first", StringComparison.Ordinal));

        // The "next day" link: the same page, now on the next day's brief, with the toast still showing.
        await r.Click(p => p.SetParametersAsync(
            ParameterView.FromDictionary(new Dictionary<string, object?> { ["Day"] = $"{next:yyyy-MM-dd}" })));
        await r.Click(_ => toasts.UndoAsync(undo.Id));

        (await Briefs(r).GetAsync(day))!.Items.OrderBy(i => i.SortOrder).Select(i => i.ArticleId)
            .Should().Equal([first, second], "it goes back where it was, on the brief it came off");
        (await Briefs(r).GetAsync(next))!.Items.Should().BeEmpty("not onto the brief the page moved on to");
    }

    [Fact]
    public async Task ASearchOfOnlyOneLetterWords_SaysWhy_RatherThanShowingNothing()
    {
        await using var r = await RenderAsync<Search>(app, "/search?q=a%20b");
        var html = await r.Html();
        html.Should().Contain("Nothing to search for").And.Contain("Search words need two characters or more.");

        await using var words = await RenderAsync<Search>(app, "/search?q=zzqq-no-such-words");
        (await words.Html()).Should().NotContain("Nothing to search for").And.Contain("Nothing matches");
    }

    private static BriefService Briefs<T>(Rendered<T> r)
    {
        r.Scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Http.IHttpContextAccessor>().HttpContext = r.Signedin;
        return r.Scope.ServiceProvider.GetRequiredService<BriefService>();
    }
}
