using System.Text.RegularExpressions;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Infrastructure.Persistence;
using Tide.Web.Components.Pages;

namespace Tide.IntegrationTests;

/// <summary>
/// Triage's selected row, as the markup says it: the row the address names (<c>?row=</c>), the one row that is
/// <c>selected</c>, <c>aria-current</c> and the list's tab stop, J and K moving it and stopping at the ends of the only
/// page, and the reading pane following it. (J and K across pages are in <c>PagingAndFiltersPageTests</c>.)
/// </summary>
public class TriageSelectionTests(TideApp app) : IClassFixture<TideApp>
{
    // Dismissed items of a source of their own, newest first as numbered, so a test's rows are only its own.
    private async Task<(Guid Source, List<string> Titles)> Seed(int count, bool withText = false)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var key = Guid.NewGuid().ToString("N");
        var source = new Source { Name = "Selection feed " + key, Url = $"https://selection.example/{key}/feed" };
        var now = DateTimeOffset.UtcNow;
        var items = Enumerable.Range(0, count).Select(n => new FeedItem
        {
            Source = source, Status = FeedItemStatus.Dismissed, Title = $"Selection {key} row {n}", Url = $"https://selection.example/{key}/{n}",
            UrlHash = $"selection-{key}-{n}", PublishedAtUtc = now.AddMinutes(-n), FetchedAtUtc = now.AddMinutes(-n),
        }).ToList();
        db.Add(source);
        db.AddRange(items);
        await db.SaveChangesAsync();
        if (withText)
        {
            db.AddRange(items.Select((i, n) => new FeedItemText { FeedItemId = i.Id, Text = $"Kept text of row {n}.", How = "the page's article markup", FetchedAtUtc = now }));
            await db.SaveChangesAsync();
        }
        return (source.Id, items.Select(i => i.Title).ToList());
    }

    // Each row's opening <li> tag, in the order shown.
    private static List<string> RowTags(string html) =>
        Regex.Matches(html, "<li class=\"tide-triage-row[^>]*>").Select(m => m.Value).ToList();

    // Exactly the row at `at` is selected: its class, aria-current and the one tab stop; every other row has none of them.
    private static void OnlySelected(string html, int at, IReadOnlyList<string> titles)
    {
        var rows = RowTags(html);
        rows.Should().HaveCount(titles.Count);
        for (var i = 0; i < rows.Count; i++)
        {
            rows[i].Should().Contain($"aria-label=\"{titles[i]}\"");
            if (i == at)
                rows[i].Should().StartWith("<li class=\"tide-triage-row selected").And.Contain("aria-current=\"true\"").And.Contain("tabindex=\"0\"");
            else
                rows[i].Should().NotContain("selected").And.NotContain("aria-current").And.Contain("tabindex=\"-1\"");
        }
    }

    [Theory]
    [InlineData("2", 2)]
    [InlineData("0", 0)]
    [InlineData("4", 4)]
    [InlineData("999", 4)]    // past the end: the last row
    [InlineData("-3", 0)]     // before the start, not a number, cut short, or markup: the first row, and never drawn
    [InlineData("abc", 0)]
    [InlineData("2.5", 0)]
    [InlineData("%22%3E%3Cb%3Ex", 0)]
    public async Task TheRowInTheAddress_IsTheOneSelected(string row, int expected)
    {
        var (source, titles) = await Seed(5);
        await using var r = await ComponentHarness.RenderAsync<Triage>(app, $"/triage?tab=dismissed&source={source}&row={row}");
        var html = await r.HtmlOnce(titles[4]);

        OnlySelected(html, expected, titles);
        html.Should().NotContain("<b>x");
    }

    [Fact]
    public async Task WithNoRowInTheAddress_TheFirstRowIsSelected()
    {
        var (source, titles) = await Seed(3);
        await using var r = await ComponentHarness.RenderAsync<Triage>(app, $"/triage?tab=dismissed&source={source}");

        OnlySelected(await r.HtmlOnce(titles[2]), 0, titles);
    }

    [Fact]
    public async Task JAndK_MoveTheSelection_AndStopAtTheEndsOfTheOnlyPage()
    {
        var (source, titles) = await Seed(3);
        await using var r = await ComponentHarness.RenderAsync<Triage>(app, $"/triage?tab=dismissed&source={source}");
        await r.HtmlOnce(titles[2]);

        await r.Click(t => t.OnTriageKey("prev"));
        OnlySelected(await r.Html(), 0, titles);   // K on the first row of the first page stays

        await r.Click(t => t.OnTriageKey("next"));
        OnlySelected(await r.Html(), 1, titles);
        await r.Click(t => t.OnTriageKey("next"));
        OnlySelected(await r.Html(), 2, titles);
        await r.Click(t => t.OnTriageKey("next"));
        OnlySelected(await r.Html(), 2, titles);   // J on the last row of the last page stays

        await r.Click(t => t.OnTriageKey("prev"));
        OnlySelected(await r.Html(), 1, titles);
    }

    [Fact]
    public async Task TheReadingPane_FollowsTheSelection()
    {
        var (source, titles) = await Seed(3, withText: true);
        await using var r = await ComponentHarness.RenderAsync<Triage>(app, $"/triage?tab=dismissed&source={source}&row=1");
        await r.HtmlOnce(titles[2]);

        await r.Click(t => t.OnTriageKey("read"));
        Pane(await r.Html()).Should().Contain($"<h2 class=\"tide-reading-title\">{titles[1]}</h2>").And.Contain("Kept text of row 1.");

        await r.Click(t => t.OnTriageKey("next"));
        var html = await r.Html();
        OnlySelected(html, 2, titles);
        Pane(html).Should().Contain($"<h2 class=\"tide-reading-title\">{titles[2]}</h2>").And.Contain("Kept text of row 2.");

        await r.Click(t => t.OnTriageKey("prev"));
        await r.Click(t => t.OnTriageKey("prev"));
        Pane(await r.Html()).Should().Contain($"<h2 class=\"tide-reading-title\">{titles[0]}</h2>").And.Contain("Kept text of row 0.");
    }

    private static string Pane(string html)
    {
        var at = html.IndexOf("<aside class=\"tide-card tide-reading\"", StringComparison.Ordinal);
        at.Should().BePositive("the reading pane is open");
        return html[at..html.IndexOf("</aside>", at, StringComparison.Ordinal)];
    }

    [Fact]
    public void TheAddress_NamesTheRow_UnlessItIsTheFirst()
    {
        Triage.Address(FeedItemStatus.New, null, null, sinceLast: false, null, 4, page: 2).Should().Be("triage?tab=new&page=2&row=4");
        Triage.Address(FeedItemStatus.New, null, null, sinceLast: false, null, 0, page: 2).Should().Be("triage?tab=new&page=2");
    }
}
