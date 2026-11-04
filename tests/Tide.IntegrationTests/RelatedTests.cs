using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Tide.Application.Actions;
using Tide.Application.Articles;
using Tide.Application.Search;
using Tide.Application.Security;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.IntegrationTests;

/// <summary>The editor's Related tab (RelatedService): other articles sharing a CVE, a link or the story, and their open actions.</summary>
public class RelatedServiceTests : IDisposable
{
    private readonly TestDb _db = new();

    public RelatedServiceTests() => _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };

    public void Dispose() => _db.Dispose();

    private async Task<Guid> Article(string title, string? url = null, IEnumerable<string>? cves = null, Guid? story = null, int daysAgo = 0)
    {
        var svc = _db.Articles();
        var id = await svc.CreateAsync(title, "Vendor", url, TlpLevel.Clear);
        if (cves is not null)
        {
            var form = ArticleForm.From((await svc.GetAsync(id))!);
            form.Cves = cves.ToList();
            await svc.SaveAsync(form);
        }
        if (story is not null) await _db.Stories().LinkAsync(id, story);
        await using var ctx = _db.Context();
        var a = await ctx.Articles.SingleAsync(x => x.Id == id);
        a.CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-daysAgo);
        await ctx.SaveChangesAsync();
        return id;
    }

    [Fact]
    public async Task FindsTheSameCve_TheSameLink_AndTheSameStory_NewestFirst()
    {
        var story = await _db.Stories().CreateAsync("Edge VPN campaign", null);
        var me = await Article("GlobalProtect zero-day", "https://vendor.example/advisory?utm_source=rss", ["CVE-2026-0001"], story);
        var byCve = await Article("Earlier GlobalProtect note", cves: ["CVE-2026-0001", "CVE-2026-0002"], daysAgo: 4);
        var byLink = await Article("Vendor advisory", "https://vendor.example/advisory", daysAgo: 3);
        var byOtherReport = await Article("Roundup", daysAgo: 2);
        await using (var ctx = _db.Context())
        {
            ctx.ArticleLinks.Add(new ArticleLink { ArticleId = byOtherReport, Url = "https://vendor.example/advisory", Title = "Advisory", AddedByName = "X" });
            await ctx.SaveChangesAsync();
        }
        var byStory = await Article("Same campaign, new lure", story: story, daysAgo: 1);
        await Article("Unrelated", "https://other.example/", ["CVE-2026-9999"]);

        var work = await _db.Related().ForArticleAsync(me);

        work.Articles.Select(a => a.Id).Should().Equal(byStory, byOtherReport, byLink, byCve);
        work.Articles.Single(a => a.Id == byCve).Why.Should().Equal("CVE-2026-0001");
        work.Articles.Single(a => a.Id == byLink).Why.Should().Equal(new[] { RelatedService.SameLink }, "the link is compared canonical, tracking dropped");
        work.Articles.Single(a => a.Id == byOtherReport).Why.Should().Equal(new[] { RelatedService.SameLink }, "another report counts as its link");
        var inStory = work.Articles.Single(a => a.Id == byStory);
        (inStory.Why.Single(), inStory.StoryId, inStory.StoryName).Should().Be((RelatedService.SameStory, story, "Edge VPN campaign"));
        (work.Total, work.More, work.SearchQuery).Should().Be((4, false, "CVE-2026-0001"));
    }

    [Fact]
    public async Task ShowsTheNewestFive_AndSaysWhatToSearchForTheRest()
    {
        var me = await Article("Exchange zero-day", cves: ["CVE-2026-0101"]);
        var others = new List<Guid>();
        for (var i = 1; i <= 7; i++) others.Add(await Article($"Exchange note {i}", cves: ["CVE-2026-0101"], daysAgo: i));

        var work = await _db.Related().ForArticleAsync(me);

        work.Articles.Should().HaveCount(RelatedService.MaxArticles);
        work.Articles.Select(a => a.Id).Should().Equal(others.Take(5));
        (work.Total, work.More, work.SearchQuery).Should().Be((7, true, "CVE-2026-0101"));
    }

    [Fact]
    public async Task CountsOnlyOpenActions_WithTheirTeamAssigneeAndLateness_AndTheBriefDate()
    {
        var me = await Article("Citrix follow-up", cves: ["CVE-2026-0202"]);
        var earlier = await Article("Citrix bleed", cves: ["CVE-2026-0202"], daysAgo: 10);
        var team = await _db.Teams().AddAsync("Vulnerability Management", null, null);
        var open = await _db.Actions().CreateAsync(new ActionForm { Title = "Patch the gateways", Kind = ActionKind.PatchRemediate, TeamId = team, ArticleId = earlier });
        var started = await _db.Actions().CreateAsync(new ActionForm { Title = "Sweep the logs", Kind = ActionKind.IocSweep, TeamId = team, ArticleId = earlier });
        var done = await _db.Actions().CreateAsync(new ActionForm { Title = "Block the IPs", Kind = ActionKind.Block, TeamId = team, ArticleId = earlier });
        var cancelled = await _db.Actions().CreateAsync(new ActionForm { Title = "Tell the vendor", Kind = ActionKind.IocSweep, TeamId = team, ArticleId = earlier });
        var today = _db.Clock.Today;
        await using (var ctx = _db.Context())
        {
            var rows = await ctx.Actions.ToListAsync();
            rows.Single(a => a.Number == open).DueDate = today.AddDays(-8);
            rows.Single(a => a.Number == open).AssigneeName = "Sam";
            rows.Single(a => a.Number == started).Status = ActionStatus.InProgress;
            rows.Single(a => a.Number == started).DueDate = today.AddDays(3);
            rows.Single(a => a.Number == done).Status = ActionStatus.Done;
            rows.Single(a => a.Number == cancelled).Status = ActionStatus.Cancelled;
            var brief = new Brief { BriefDate = new DateOnly(2026, 10, 22), AnalystName = "Robin" };
            brief.Items.Add(new BriefItem { ArticleId = earlier });
            ctx.Briefs.Add(brief);
            await ctx.SaveChangesAsync();
        }

        var related = (await _db.Related().ForArticleAsync(me)).Articles.Single();

        related.LastBriefed.Should().Be(new DateOnly(2026, 10, 22));
        related.OpenActions.Select(a => a.Number).Should().Equal(started, open);
        var late = related.OpenActions.Single(a => a.Number == open);
        (late.TeamName, late.AssigneeName, late.DaysLate).Should().Be(("Vulnerability Management", "Sam", 8));
        related.OpenActions.Single(a => a.Number == started).DaysLate.Should().Be(0);
        RelatedService.MentionText(late).Should().Be($"A-{open} (open)", "never how late: that would go stale in a sent brief");
    }

    [Fact]
    public async Task NothingRelated_IsEmpty_AndReadersAreRefused()
    {
        var me = await Article("Alone", "https://alone.example/");
        var work = await _db.Related().ForArticleAsync(me);
        (work.Articles.Count, work.Total, work.SearchQuery).Should().Be((0, 0, null));
        (await _db.Related().ForArticleAsync(Guid.NewGuid())).Should().Be(RelatedWork.None);

        _db.User.Roles = new HashSet<AppRole> { AppRole.Reader };
        var read = () => _db.Related().ForArticleAsync(me);
        await read.Should().ThrowAsync<ForbiddenException>();
    }
}
