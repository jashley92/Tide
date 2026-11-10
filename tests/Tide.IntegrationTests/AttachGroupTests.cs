using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Tide.Application.Intake;
using Tide.Application.Security;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.IntegrationTests;

/// <summary>
/// "Attach all" on a suggested group (<see cref="IntakeService.AttachAllAsync"/>): every report goes to the article in
/// one operation, through the same save as attaching one (its link and CVEs on the article, the article's edit version
/// bumped, the links audited), or none does, and the analyst is told which report stopped it and why.
/// </summary>
public class AttachGroupTests : IDisposable
{
    private readonly TestDb _db = new(connectionPerContext: true);
    private const string FeedUrl = "https://group.example/feed.xml";

    public AttachGroupTests() => _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };   // an administrator adds the source

    public void Dispose() => _db.Dispose();

    // Two new reports of one story, and a draft that covers it; the reports' ids, first then second.
    private async Task<(List<FeedItem> Items, Guid Article)> Seed()
    {
        var source = await _db.Intake().SaveSourceAsync(new SourceForm { Name = "Group news", Url = FeedUrl, PollMinutes = 30 });
        _db.Http.Set(FeedUrl, "<rss version=\"2.0\"><channel><title>News</title>" +
            "<item><title>Wallet drained (CVE-2026-7101)</title><link>https://group.example/one</link><pubDate>Mon, 26 Oct 2026 09:00:00 GMT</pubDate></item>" +
            "<item><title>Wallet theft, day two (CVE-2026-7102)</title><link>https://group.example/two</link><pubDate>Mon, 26 Oct 2026 10:00:00 GMT</pubDate></item>" +
            "</channel></rss>");
        await _db.Intake().PollNowAsync(source);
        var items = (await _db.Intake().InboxAsync(new InboxFilter())).Select(r => r.Item).OrderBy(i => i.Url).ToList();
        var article = await _db.Articles().CreateAsync("Wallet theft", "Vendor", "https://first.example/wallet");
        return (items, article);
    }

    private async Task<List<FeedItem>> Items(IEnumerable<Guid> ids)
    {
        await using var ctx = _db.Context();
        var wanted = ids.ToList();
        return await ctx.FeedItems.AsNoTracking().Where(i => wanted.Contains(i.Id)).OrderBy(i => i.Url).ToListAsync();
    }

    private async Task<int> AuditedLinks(Guid article)
    {
        await using var ctx = _db.Context();
        var links = (await ctx.ArticleLinks.Where(l => l.ArticleId == article).Select(l => l.Id).ToListAsync()).Select(id => id.ToString()).ToHashSet();
        var added = await ctx.AuditLog.Where(e => e.EntityType == nameof(ArticleLink) && e.Action == "Added").Select(e => e.EntityKey).ToListAsync();
        return added.Count(links.Contains);
    }

    [Fact]
    public async Task TwoReports_AttachInOneOperation_WithTheirLinksAndCves_TheEditVersionBumped_AndAudited()
    {
        var (items, article) = await Seed();
        var before = (await _db.Articles().GetAsync(article))!.EditVersion;

        (await _db.Intake().AttachAllAsync(items.Select(i => i.Id).ToList(), article)).Should().Be(2);

        (await Items(items.Select(i => i.Id))).Should().AllSatisfy(i =>
        {
            i.Status.Should().Be(FeedItemStatus.Promoted);
            i.ArticleId.Should().Be(article);
            i.StatusByName.Should().Be("Test Analyst");
        });
        (await _db.Articles().LinksAsync(article)).Select(l => l.Url).Should().BeEquivalentTo(["https://group.example/one", "https://group.example/two"]);
        var saved = (await _db.Articles().GetAsync(article))!;
        saved.Cves.Select(c => c.CveId).Should().BeEquivalentTo(["CVE-2026-7101", "CVE-2026-7102"]);
        saved.EditVersion.Should().NotBe(before, "an editor open on the article now holds a stale copy of its CVEs");
        (await AuditedLinks(article)).Should().Be(2, "each added link is in the audit log, as one attach's is");
    }

    [Fact]
    public async Task OneReportTakenMeanwhile_NoneAttach_AndTheAnalystIsToldWhichAndWhy()
    {
        var (items, article) = await Seed();
        var elsewhere = await _db.Articles().CreateAsync("Another article");
        // A colleague attached the second report to another article after this page read the group.
        await _db.Intake().AttachAsync(items[1].Id, elsewhere);
        var before = (await _db.Articles().GetAsync(article))!.EditVersion;

        var attach = () => _db.Intake().AttachAllAsync(items.Select(i => i.Id).ToList(), article);
        await attach.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Wallet theft, day two (CVE-2026-7102)*already made another article*none of the 2 was attached*");

        (await Items([items[0].Id])).Single().Status.Should().Be(FeedItemStatus.New, "the first report wasn't attached without the second");
        (await _db.Articles().LinksAsync(article)).Should().BeEmpty();
        var saved = (await _db.Articles().GetAsync(article))!;
        saved.Cves.Should().BeEmpty();
        saved.EditVersion.Should().Be(before, "nothing on the article changed");
    }

    [Fact]
    public async Task AReportClaimedBetweenTheCheckAndTheSave_NoneAttach()
    {
        var (items, article) = await Seed();
        // The second report is being promoted by a colleague: claimed, with no article yet (what the claim statement
        // refuses, as a stale read of the group would be).
        await using (var ctx = _db.Context())
        {
            var row = await ctx.FeedItems.SingleAsync(i => i.Id == items[1].Id);
            row.Status = FeedItemStatus.Promoted;
            row.StatusByName = "Robin";
            row.StatusAtUtc = DateTimeOffset.UtcNow;
            await ctx.SaveChangesAsync();
        }

        var attach = () => _db.Intake().AttachAllAsync(items.Select(i => i.Id).ToList(), article);
        await attach.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Wallet theft, day two (CVE-2026-7102)*being promoted*none of the 2 was attached*");
        (await Items([items[0].Id])).Single().Status.Should().Be(FeedItemStatus.New);
        (await _db.Articles().LinksAsync(article)).Should().BeEmpty();
    }

    [Fact]
    public async Task APresentedArticle_TakesNone()
    {
        var (items, article) = await Seed();
        await using (var ctx = _db.Context())
        {
            (await ctx.Articles.SingleAsync(a => a.Id == article)).Status = ArticleStatus.Presented;
            await ctx.SaveChangesAsync();
        }

        var attach = () => _db.Intake().AttachAllAsync(items.Select(i => i.Id).ToList(), article);
        await attach.Should().ThrowAsync<InvalidOperationException>().WithMessage("*already been presented*");
        (await Items(items.Select(i => i.Id))).Should().AllSatisfy(i => i.Status.Should().Be(FeedItemStatus.New));
        (await _db.Articles().LinksAsync(article)).Should().BeEmpty();
    }

    [Fact]
    public async Task SomeoneWhoCannotTriage_AttachesNone()
    {
        var (items, article) = await Seed();
        _db.User.Roles = new HashSet<AppRole> { AppRole.ActionOwner };

        var attach = () => _db.Intake().AttachAllAsync(items.Select(i => i.Id).ToList(), article);
        await attach.Should().ThrowAsync<ForbiddenException>();

        _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };
        (await Items(items.Select(i => i.Id))).Should().AllSatisfy(i => i.Status.Should().Be(FeedItemStatus.New));
        (await _db.Articles().LinksAsync(article)).Should().BeEmpty();
    }

    [Fact]
    public async Task AGoneReport_OrNoneOrTooMany_IsRefused()
    {
        var (items, article) = await Seed();

        var gone = () => _db.Intake().AttachAllAsync([items[0].Id, Guid.NewGuid()], article);
        await gone.Should().ThrowAsync<KeyNotFoundException>();
        var none = () => _db.Intake().AttachAllAsync([], article);
        await none.Should().ThrowAsync<ArgumentException>();
        var many = () => _db.Intake().AttachAllAsync(Enumerable.Range(0, TriageGroups.MaxSize + 1).Select(_ => Guid.NewGuid()).ToList(), article);
        await many.Should().ThrowAsync<ArgumentException>();
        (await Items([items[0].Id])).Single().Status.Should().Be(FeedItemStatus.New);
    }

    [Fact]
    public async Task AttachingTheGroupAgain_ChangesNothingMore()
    {
        var (items, article) = await Seed();
        var ids = items.Select(i => i.Id).ToList();
        await _db.Intake().AttachAllAsync(ids, article);
        await _db.Intake().AttachAllAsync(ids, article);
        (await _db.Articles().LinksAsync(article)).Should().HaveCount(2, "a link the article has isn't added twice");
    }
}
