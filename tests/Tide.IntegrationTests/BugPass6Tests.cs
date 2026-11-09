using System.Text;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Tide.Application.Intake;
using Tide.Application.Operations;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.IntegrationTests;

/// <summary>Bug pass 6: a group's shortlist, and a bundle's holidays held to the page's rules.</summary>
public class BugPass6Tests : IDisposable
{
    private TestDb _db = new();

    public BugPass6Tests() => _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };

    public void Dispose() => _db.Dispose();

    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task AGroupsShortlist_TakesOnlyItemsStillNew_AndCountsThem(string provider)
    {
        var db = _db = _db.On(provider);
        var source = await db.Intake().SaveSourceAsync(new SourceForm { Name = "News", Url = "https://news.example.com/feed", PollMinutes = 30 });
        var items = Enumerable.Range(0, 3).Select(i => new FeedItem
        {
            SourceId = source, Title = $"Report {i}", Url = $"https://news.example.com/{i}", UrlHash = $"h{i}", FetchedAtUtc = DateTimeOffset.UtcNow,
        }).ToList();
        items[1].Status = FeedItemStatus.Dismissed;   // a colleague's decision since the group was drawn
        await using (var ctx = db.Context())
        {
            ctx.FeedItems.AddRange(items);
            await ctx.SaveChangesAsync();
        }

        (await db.Intake().ShortlistManyAsync(items.Select(i => i.Id).ToList())).Should().Be(2);

        await using var read = db.Context();
        var status = await read.FeedItems.ToDictionaryAsync(i => i.Id, i => i.Status);
        status[items[0].Id].Should().Be(FeedItemStatus.Shortlisted);
        status[items[1].Id].Should().Be(FeedItemStatus.Dismissed, "a colleague's dismissal isn't undone");
        status[items[2].Id].Should().Be(FeedItemStatus.Shortlisted);
    }

    [Fact]
    public async Task ABundlesHolidays_SkipABriefDay_AndAYearOutOfRange()
    {
        var day = new DateOnly(2026, 10, 7);
        await _db.Briefs().GetOrStartAsync(day);
        var bundle = new ConfigBundle(ConfigBundle.FormatName, ConfigBundle.CurrentVersion, DateTimeOffset.UtcNow, "x", "x", [], [], [], [], [],
            [new BundleHoliday(day, "Founders' Day"), new BundleHoliday(new DateOnly(2150, 1, 2), "Far off"), new BundleHoliday(new DateOnly(2026, 10, 8), "Kept")]);

        var preview = await new ConfigBundleService(_db, _db.User, new CountingRoleDirectory(), TimeProvider.System).PreviewAndImportAsync(bundle);

        var holidays = preview.Changes.Where(c => c.Area == "Holidays").ToList();
        holidays.Single(c => c.Item.StartsWith("2026-10-07", StringComparison.Ordinal)).Should().Match<BundleChange>(c => c.Kind == BundleChangeKind.Skipped && c.Detail!.Contains("brief"));
        holidays.Single(c => c.Item.StartsWith("2150", StringComparison.Ordinal)).Kind.Should().Be(BundleChangeKind.Skipped);
        holidays.Single(c => c.Item.StartsWith("2026-10-08", StringComparison.Ordinal)).Kind.Should().Be(BundleChangeKind.Add);
    }
}
