using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Abstractions;
using Tide.Application.Articles;
using Tide.Application.Briefs;
using Tide.Application.Org;
using Tide.Application.Security;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Infrastructure.Persistence;
using Tide.Web.Components.Pages;
using Tide.Web.Services;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>
/// The running order's one-click fix for a threat whose "Why it matters" would reach the company-wide edition
/// (<see cref="ArticleService.MarkWhyItMattersAmberAsync"/>): by people who write articles, from the copy the page showed,
/// on a brief that hasn't gone out, raising that one marking and never lowering it, recorded and heard like any save.
/// </summary>
public class WhyItMattersAmberTests(TideApp app) : IClassFixture<TideApp>
{
    private static readonly SignIn Robin = new("dev:amber-robin", "Robin Analyst", "Analyst");
    private static readonly SignIn Owner = new("dev:amber-owner", "Olive Owner", "ActionOwner");

    // Read once: a test that straddles midnight mustn't see two days.
    private DateOnly Today { get; } = app.Services.GetRequiredService<OrgClock>().CurrentBriefDate;

    private sealed record Setup(DateOnly Day, Guid Brief, Guid Article, Guid Version, string Title);

    // A past day's brief (its own day per test) with one threat on it, complete but for its "Why it matters" marking.
    private async Task<Setup> Brief(int daysBack, TlpLevel article = TlpLevel.Clear, TlpLevel why = TlpLevel.Clear, bool sent = false)
    {
        var day = Today;
        for (var i = 0; i < daysBack; i++) day = WorkingDays.Weekdays.Previous(day);
        var title = $"Amber fix {daysBack}";
        Guid briefId, id, version;
        await using (var scope = SignedIn(app, SignIn.Analyst))
        {
            var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
            var briefs = scope.ServiceProvider.GetRequiredService<BriefService>();
            id = await articles.CreateAsync(title, "Vendor", null, article);
            var form = ArticleForm.From((await articles.GetAsync(id))!);
            form.Classification = Classification.Threat;
            form.Priority = Priority.P2;
            form.Bluf = "Patch it.";
            form.Bullets.Add(new BulletForm { Text = "Exploited in the wild.", Tlp = article });
            form.RecommendedActions = "Patch.";
            form.WhyItMatters = "We run two of these.";
            form.WhyItMattersTlp = why;
            version = await articles.SaveAsync(form);
            briefId = (await briefs.GetOrStartAsync(day)).Id;
            await briefs.AddArticleAsync(briefId, id);
        }
        if (sent)
        {
            await using var scope = app.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.Briefs.SingleAsync(b => b.Id == briefId)).Status = BriefStatus.Sent;
            await db.SaveChangesAsync();
        }
        return new Setup(day, briefId, id, version, title);
    }

    private async Task<Article> Stored(Guid id)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Articles.AsNoTracking().SingleAsync(a => a.Id == id);
    }

    private async Task<bool> Mark(Setup s, SignIn who, Guid? version = null)
    {
        await using var scope = SignedIn(app, who);
        return await scope.ServiceProvider.GetRequiredService<ArticleService>().MarkWhyItMattersAmberAsync(s.Article, version ?? s.Version, s.Brief);
    }

    [Fact]
    public async Task AnAnalyst_MarksItAmber_AndOnlyThat_RecordedUnderTheirName_AndHeard()
    {
        var s = await Brief(201);
        var before = await Stored(s.Article);
        var heard = new List<Change>();
        void Listen(Change c) { lock (heard) heard.Add(c); }
        var feed = app.Services.GetRequiredService<ChangeFeed>();
        feed.Changed += Listen;
        try
        {
            (await Mark(s, Robin)).Should().BeTrue();
        }
        finally { feed.Changed -= Listen; }

        var after = await Stored(s.Article);
        after.WhyItMattersTlp.Should().Be(TlpLevel.Amber);
        after.EditVersion.Should().NotBe(s.Version, "it is an edit: a form loaded before it is now stale");
        (after.Tlp, after.Title, after.Bluf, after.WhyItMatters, after.Status, after.Classification, after.Priority)
            .Should().Be((before.Tlp, before.Title, before.Bluf, before.WhyItMatters, before.Status, before.Classification, before.Priority));
        lock (heard) heard.Should().Contain(c => c.Topic == ChangeFeed.Article(s.Article) && c.ActorId == Robin.UserId, "open pages showing the article update");

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var key = s.Article.ToString();
        var entry = (await db.AuditLog.AsNoTracking().Where(e => e.EntityType == nameof(Article) && e.EntityKey == key).ToListAsync())
            .OrderBy(e => e.AtUtc).Last();
        (entry.UserId, entry.UserName, entry.Action).Should().Be((Robin.UserId, Robin.Name, "Modified"));
        entry.ChangesJson.Should().Contain(nameof(Article.WhyItMattersTlp)).And.Contain("Clear").And.Contain("Amber")
            .And.NotContain(nameof(Article.Bluf), "nothing else changed");

        // Its history, as the article page shows it, names who did it.
        await using var asRobin = SignedIn(app, Robin);
        var history = await asRobin.ServiceProvider.GetRequiredService<Tide.Application.History.HistoryService>().ArticleAsync(s.Article);
        history.Entries.Should().Contain(h => h.UserName == Robin.Name && h.Changes.Any(c => c.Field == "Why it matters marking" && c.Before == "TLP:CLEAR" && c.After == "TLP:AMBER"));
    }

    [Fact]
    public async Task AStaleEditVersion_IsRefused_AndNothingChanges()
    {
        var s = await Brief(202);

        await FluentActions.Awaiting(() => Mark(s, Robin, Guid.NewGuid())).Should().ThrowAsync<StaleEditException>();

        (await Stored(s.Article)).WhyItMattersTlp.Should().Be(TlpLevel.Clear);
    }

    [Fact]
    public async Task SomeoneWhoCantWriteArticles_IsRefused()
    {
        var s = await Brief(203);

        await FluentActions.Awaiting(() => Mark(s, Owner)).Should().ThrowAsync<ForbiddenException>();

        (await Stored(s.Article)).WhyItMattersTlp.Should().Be(TlpLevel.Clear);
    }

    [Theory]
    [InlineData(204, TlpLevel.Clear, TlpLevel.Amber)]
    [InlineData(205, TlpLevel.Clear, TlpLevel.Red)]
    [InlineData(206, TlpLevel.AmberStrict, TlpLevel.Clear)]   // the article's own marking is what the paragraph counts as
    public async Task AlreadyAmberOrStricter_NothingHappens_AndNothingIsLowered(int daysBack, TlpLevel article, TlpLevel why)
    {
        var s = await Brief(daysBack, article, why);
        var before = await Stored(s.Article);

        (await Mark(s, Robin)).Should().BeFalse();

        var after = await Stored(s.Article);
        (after.WhyItMattersTlp, after.Tlp, after.EditVersion).Should().Be((before.WhyItMattersTlp, before.Tlp, before.EditVersion));
    }

    [Fact]
    public async Task OnASentBrief_ItIsRefused()
    {
        var s = await Brief(207, sent: true);

        (await FluentActions.Awaiting(() => Mark(s, Robin)).Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*sent*correction*");

        (await Stored(s.Article)).WhyItMattersTlp.Should().Be(TlpLevel.Clear);
    }

    [Fact]
    public async Task ForABriefTheArticleIsntOn_ItIsRefused()
    {
        var s = await Brief(208);

        await FluentActions.Awaiting(() => Mark(s with { Brief = Guid.NewGuid() }, Robin)).Should().ThrowAsync<InvalidOperationException>();

        (await Stored(s.Article)).WhyItMattersTlp.Should().Be(TlpLevel.Clear);
    }

    // The brief's page, as /briefs/{day} draws it.
    private Task<Rendered<BriefPage>> Open(Setup s, SignIn who) =>
        RenderAsync<BriefPage>(app, $"/briefs/{s.Day:yyyy-MM-dd}", new() { ["Day"] = $"{s.Day:yyyy-MM-dd}" }, who);

    [Fact]
    public async Task TheRunningOrder_OffersTheFix_ToAnAnalyst_AndOneClickClearsTheWarning()
    {
        var s = await Brief(209);
        await using var r = await Open(s, Robin);

        var html = await r.Html();
        html.Should().Contain("Needs: Why it matters TLP").And.Contain(">Set to AMBER</button>")
            .And.Contain($"aria-label=\"Set to AMBER: Why it matters on &#x201C;{s.Title}&#x201D;\"");
        html.Should().Contain("will reach the company-wide edition", "the edition check warns of it too");
        html.Should().NotContain(">Keep it<");

        await r.Click(p => p.Fix(s.Article, ArticleFix.WhyItMattersAmber));

        // The page drawn again, as the button's click draws it (a test's call isn't an event, so it draws a fresh one).
        await using var again = await Open(s, Robin);
        html = await again.Html();
        html.Should().NotContain("Needs: Why it matters TLP").And.NotContain("Set to AMBER").And.NotContain("will reach the company-wide edition");
        html.Should().Contain("1 part above TLP:GREEN left out", "the edition check now leaves the paragraph out");
        (await Stored(s.Article)).WhyItMattersTlp.Should().Be(TlpLevel.Amber);
        var toast = r.Scope.ServiceProvider.GetRequiredService<ToastService>().Toasts.Should().ContainSingle().Subject;
        toast.Message.Should().Be($"Why it matters on “{s.Title}” is now TLP:AMBER. The company-wide edition leaves it out.");
        toast.Undo.Should().BeNull("an article edit has no undo");
    }

    [Fact]
    public async Task TheRunningOrder_DoesntOfferTheFix_ToAnActionOwner_OrOnASentBrief()
    {
        var open = await Brief(210);
        await using (var owner = await Open(open, Owner))
        {
            (await owner.Html()).Should().Contain(open.Title).And.NotContain("Set to AMBER");
            await owner.Click(p => p.Fix(open.Article, ArticleFix.WhyItMattersAmber));
            (await Stored(open.Article)).WhyItMattersTlp.Should().Be(TlpLevel.Clear, "the page doesn't act for someone who can't edit");
        }

        var sent = await Brief(211, sent: true);
        await using var r = await Open(sent, Robin);
        (await r.Html()).Should().Contain("Needs: Why it matters TLP", "the warning still shows").And.NotContain("Set to AMBER");
    }

    [Fact]
    public async Task AColleaguesSaveMeanwhile_IsRefused_FromThePage()
    {
        var s = await Brief(212);
        await using var r = await Open(s, Robin);
        await using (var scope = SignedIn(app, SignIn.Analyst))
        {
            var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
            var form = ArticleForm.From((await articles.GetAsync(s.Article))!);
            form.Bluf = "Patch it today.";
            await articles.SaveAsync(form);
        }

        await r.Click(p => p.Fix(s.Article, ArticleFix.WhyItMattersAmber));

        (await Stored(s.Article)).WhyItMattersTlp.Should().Be(TlpLevel.Clear, "the page's copy was out of date");
        r.Scope.ServiceProvider.GetRequiredService<ToastService>().Toasts.Should().Contain(t => t.Message.Contains("Someone else saved this article"));
    }
}
