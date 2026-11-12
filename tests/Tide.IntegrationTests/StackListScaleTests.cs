using System.Diagnostics;
using System.Globalization;
using AwesomeAssertions;
using Tide.Application.Stack;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Xunit.Abstractions;

namespace Tide.IntegrationTests;

/// <summary>
/// The Our stack list on a large install: 2,000 articles (a tenth on the last week's briefs, each with a CVE) and 500
/// new triage items, read for 20 products and for 200. The list is read in the same number of SQL commands either way,
/// so a longer list costs no more round trips; the wall time of the larger read is written to the test's output.
/// </summary>
public class StackListScaleTests(ITestOutputHelper output)
{
    private const int Articles = 2000, Items = 500;

    [Fact]
    public async Task TheList_ReadsInTheSameNumberOfCommands_HoweverManyProducts()
    {
        var (few, _) = await MeasureAsync(20);
        var (many, ms) = await MeasureAsync(200);

        output.WriteLine($"OverviewAsync, 200 products, {Articles} articles, {Items} new items: {many} commands, {ms} ms (best of 3)");
        many.Should().Be(few, "the list is read once for every product, not once a product");
        many.Should().BeLessThanOrEqualTo(10);
    }

    /// <summary>The context rail reads only the selected product (its history and what's in triage naming it): the same
    /// number of SQL commands however many products are on the list.</summary>
    [Fact]
    public async Task TheRail_ReadsOneProduct_InTheSameNumberOfCommands_HoweverManyProducts()
    {
        var (few, _) = await MeasureRailAsync(20);
        var (many, ms) = await MeasureRailAsync(200);

        output.WriteLine($"The rail's read, 200 products, {Articles} articles, {Items} new items: {many} commands, {ms} ms (best of 3)");
        many.Should().Be(few, "the rail reads the selected product, not the list");
        many.Should().BeLessThanOrEqualTo(10);
    }

    private static async Task<(int Commands, long Ms)> MeasureRailAsync(int products)
    {
        using var db = new TestDb();
        db.User.Roles = new HashSet<AppRole> { AppRole.Analyst };
        await SeedAsync(db, products);
        var stack = db.Stack();
        var id = (await stack.ListAsync()).First(r => r.Product.Name == Name(0)).Product.Id;
        (await stack.InTriageAsync(id)).Should().NotBeEmpty("the seed's items name the product");   // warm, as above
        await stack.GetAsync(id);

        db.Commands.Reset();
        await stack.GetAsync(id);
        await stack.InTriageAsync(id);
        var commands = db.Commands.Count;

        var best = long.MaxValue;
        for (var i = 0; i < 3; i++)
        {
            var sw = Stopwatch.StartNew();
            await stack.GetAsync(id);
            await stack.InTriageAsync(id);
            best = Math.Min(best, sw.ElapsedMilliseconds);
        }
        return (commands, best);
    }

    private static async Task<(int Commands, long Ms)> MeasureAsync(int products)
    {
        using var db = new TestDb();
        db.User.Roles = new HashSet<AppRole> { AppRole.Analyst };
        await SeedAsync(db, products);
        var stack = db.Stack();
        (await stack.OverviewAsync()).Rows.Should().HaveCount(products);   // warm: the model and the queries are compiled

        db.Commands.Reset();
        var rows = (await stack.OverviewAsync()).Rows;
        var commands = db.Commands.Count;
        rows.Sum(r => r.Articles).Should().BeGreaterThan(0, "the seed's articles name its products");

        var best = long.MaxValue;
        for (var i = 0; i < 3; i++)
        {
            var sw = Stopwatch.StartNew();
            await stack.OverviewAsync();
            best = Math.Min(best, sw.ElapsedMilliseconds);
        }
        return (commands, best);
    }

    private static string Name(int n) => string.Create(CultureInfo.InvariantCulture, $"Contoso Unit{n:000} Gateway");

    private static async Task SeedAsync(TestDb db, int products)
    {
        await using var ctx = db.PlainContext();
        var today = db.Clock.Today;
        var now = DateTimeOffset.UtcNow;
        var team = new Team { Name = "Platform" };
        ctx.Teams.Add(team);
        var made = Enumerable.Range(0, products).Select(n => new StackProduct
        {
            Name = Name(n), OtherNames = n % 3 == 0 ? string.Create(CultureInfo.InvariantCulture, $"Unit{n:000} GW; CU{n:000}") : null,
            Team = n % 4 == 0 ? null : team, CheckedOn = n % 5 == 0 ? null : today.AddDays(-(n % 200)),
        }).ToList();
        ctx.StackProducts.AddRange(made);

        // Every other article names a product (of 200, so the same articles whatever the list's length); one in ten is on
        // one of the last five days' briefs and carries a CVE; one in fifty is tagged.
        var briefs = Enumerable.Range(0, 5).Select(d => new Brief { BriefDate = today.AddDays(-d), AnalystId = "a", AnalystName = "A" }).ToList();
        ctx.Briefs.AddRange(briefs);
        const string filler = "Researchers describe the flaw, the versions affected and the fix the vendor shipped this week, with notes on exploitation seen so far.";
        for (var i = 0; i < Articles; i++)
        {
            var a = new Article
            {
                Title = i % 2 == 0 ? $"Advisory {i}: {Name(i % 200)} flaw under attack" : $"Story {i} about something else entirely",
                Bluf = filler, Tlp = i % 40 == 0 ? TlpLevel.Red : TlpLevel.Clear,
            };
            ctx.Articles.Add(a);
            if (i % 10 == 0)
            {
                ctx.BriefItems.Add(new BriefItem { BriefId = briefs[i / 10 % briefs.Count].Id, ArticleId = a.Id, SortOrder = i });
                var cve = string.Create(CultureInfo.InvariantCulture, $"CVE-2026-{10000 + i}");
                ctx.ArticleCves.Add(new ArticleCve { ArticleId = a.Id, CveId = cve });
                ctx.CveRecords.Add(new CveRecord { CveId = cve, CvssScore = 5 + i % 5, EpssScore = i % 100 / 100m, IsKev = i % 30 == 0 });
            }
            if (i % 50 == 0 && made.Count > 0) ctx.ArticleProducts.Add(new ArticleProduct { ArticleId = a.Id, ProductId = made[i % made.Count].Id });
        }

        var source = new Source { Name = "News", Url = "https://news.example.com/feed.xml" };
        ctx.Sources.Add(source);
        for (var i = 0; i < Items; i++)
        {
            ctx.FeedItems.Add(new FeedItem
            {
                SourceId = source.Id, Title = i % 3 == 0 ? $"Patch out for {Name(i % 200)}" : $"Unrelated item {i}", Excerpt = filler,
                Url = $"https://news.example.com/{i}", UrlHash = i.ToString(CultureInfo.InvariantCulture), PublishedAtUtc = now.AddHours(-i), FetchedAtUtc = now.AddHours(-i),
            });
        }
        await ctx.SaveChangesAsync();
    }
}
