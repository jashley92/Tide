using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Abstractions;
using Tide.Application.Articles;
using Tide.Application.Briefs;
using Tide.Application.History;
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
/// "Keep it", beside "Set to AMBER" on the running order (<see cref="ArticleService.KeepWhyItMattersAsync"/>): someone
/// who writes articles says a threat's "Why it matters" is meant for the company-wide edition. It changes no marking and
/// no edition, only answers the warning, and only while the paragraph and its marking stay as they were kept. Checked,
/// recorded and heard as "Set to AMBER" is.
/// </summary>
public class KeepWhyItMattersTests(TideApp app) : IClassFixture<TideApp>
{
    private static readonly SignIn Robin = new("dev:keep-robin", "Robin Analyst", "Analyst");
    private static readonly SignIn Owner = new("dev:keep-owner", "Olive Owner", "ActionOwner");
    private const string Warned = "Needs: Why it matters TLP";

    // Read once: a test that straddles midnight mustn't see two days.
    private DateOnly Today { get; } = app.Services.GetRequiredService<OrgClock>().CurrentBriefDate;

    private sealed record Setup(DateOnly Day, Guid Brief, Guid Article, Guid Version, string Title);

    // A past day's brief (its own day per test) with one threat on it, complete but for its "Why it matters" marking.
    private async Task<Setup> Brief(int daysBack, bool sent = false)
    {
        var day = Today;
        for (var i = 0; i < daysBack; i++) day = WorkingDays.Weekdays.Previous(day);
        var title = $"Keep it {daysBack}";
        Guid briefId, id, version;
        await using (var scope = SignedIn(app, SignIn.Analyst))
        {
            var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
            var briefs = scope.ServiceProvider.GetRequiredService<BriefService>();
            id = await articles.CreateAsync(title, "Vendor", null, TlpLevel.Clear);
            var form = ArticleForm.From((await articles.GetAsync(id))!);
            form.Classification = Classification.Threat;
            form.Priority = Priority.P2;
            form.Bluf = "Patch it.";
            form.Bullets.Add(new BulletForm { Text = "Exploited in the wild." });
            form.RecommendedActions = "Patch.";
            form.WhyItMatters = "Everyone should patch their home routers too.";
            form.WhyItMattersTlp = TlpLevel.Clear;
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

    private async Task<TlpLevel?> Keep(Setup s, SignIn who, Guid? version = null)
    {
        await using var scope = SignedIn(app, who);
        return await scope.ServiceProvider.GetRequiredService<ArticleService>().KeepWhyItMattersAsync(s.Article, version ?? s.Version, s.Brief);
    }

    private async Task Edit(Guid id, Action<ArticleForm> change)
    {
        await using var scope = SignedIn(app, SignIn.Analyst);
        var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
        var form = ArticleForm.From((await articles.GetAsync(id))!);
        change(form);
        await articles.SaveAsync(form);
    }

    private static bool Warns(Article a) => ArticleChecks.WhyReachesCompany(a, TlpLevel.Green) is not null;

    [Fact]
    public async Task AnAnalyst_KeepsIt_ChangingNoMarking_RecordedUnderTheirName_AndHeard()
    {
        var s = await Brief(301);
        var before = await Stored(s.Article);
        Warns(before).Should().BeTrue();
        var heard = new List<Change>();
        void Listen(Change c) { lock (heard) heard.Add(c); }
        var feed = app.Services.GetRequiredService<ChangeFeed>();
        feed.Changed += Listen;
        try
        {
            (await Keep(s, Robin)).Should().Be(TlpLevel.Clear, "it is kept at the marking it has");
        }
        finally { feed.Changed -= Listen; }

        var after = await Stored(s.Article);
        Warns(after).Should().BeFalse("kept: the warning is answered");
        (after.WhyItMattersKeptTlp, after.WhyItMattersKeptById, after.WhyItMattersKeptByName).Should().Be((TlpLevel.Clear, Robin.UserId, Robin.Name));
        after.WhyItMattersKeptAtUtc.Should().NotBeNull();
        after.WhyItMattersKeptHash.Should().Be(Article.WhyItMattersFingerprint(before.WhyItMatters, TlpLevel.Clear));
        (after.Tlp, after.WhyItMattersTlp, after.WhyItMatters, after.Title, after.Bluf, after.Status, after.Classification, after.Priority)
            .Should().Be((before.Tlp, before.WhyItMattersTlp, before.WhyItMatters, before.Title, before.Bluf, before.Status, before.Classification, before.Priority),
                "keeping it changes no marking and nothing written");
        after.EditVersion.Should().Be(s.Version, "it changes nothing written, so an editor open on the article can still save");
        lock (heard) heard.Should().Contain(c => c.Topic == ChangeFeed.Article(s.Article) && c.ActorId == Robin.UserId, "open pages showing the article update");

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var key = s.Article.ToString();
        var entry = (await db.AuditLog.AsNoTracking().Where(e => e.EntityType == nameof(Article) && e.EntityKey == key).ToListAsync())
            .OrderBy(e => e.AtUtc).Last();
        (entry.UserId, entry.UserName, entry.Action).Should().Be((Robin.UserId, Robin.Name, "Modified"));
        entry.ChangesJson.Should().Contain(nameof(Article.WhyItMattersKeptTlp)).And.Contain(nameof(Article.WhyItMattersKeptHash))
            .And.NotContain($"\"{nameof(Article.WhyItMattersTlp)}\"", "no marking changed").And.NotContain($"\"{nameof(Article.Tlp)}\"");

        // Its history, as the article page shows it, says what was kept and who kept it.
        await using var asRobin = SignedIn(app, Robin);
        var history = await asRobin.ServiceProvider.GetRequiredService<HistoryService>().ArticleAsync(s.Article);
        history.Entries[0].Should().Match<HistoryEntry>(h => h.UserName == Robin.Name && h.What == "Kept “Why it matters” at TLP:CLEAR");
    }

    [Fact]
    public async Task KeepingItAgain_AsItIs_SavesNothingMore()
    {
        var s = await Brief(302);
        await Keep(s, Robin);
        var first = await Stored(s.Article);

        (await Keep(s, Robin)).Should().Be(TlpLevel.Clear);

        (await Stored(s.Article)).WhyItMattersKeptAtUtc.Should().Be(first.WhyItMattersKeptAtUtc, "it was already kept as it is");
    }

    [Fact]
    public async Task ItLapses_WhenTheTextChanges_AndCanBeKeptAgain_RecordingTheMarkingEachTime()
    {
        var s = await Brief(303);
        await Keep(s, Robin);

        await Edit(s.Article, f => f.WhyItMatters += " And the office ones.");

        var edited = await Stored(s.Article);
        Warns(edited).Should().BeTrue("what was kept isn't what goes out now");
        edited.WhyItMattersKeptHash.Should().BeNull("a keep that no longer holds is cleared by the save that ended it");

        (await Keep(s with { Version = edited.EditVersion }, Robin)).Should().Be(TlpLevel.Clear);
        Warns(await Stored(s.Article)).Should().BeFalse();
        await using var asRobin = SignedIn(app, Robin);
        var history = await asRobin.ServiceProvider.GetRequiredService<HistoryService>().ArticleAsync(s.Article);
        history.Entries.Count(h => h.What == "Kept “Why it matters” at TLP:CLEAR").Should().Be(2, "each keep says the marking it kept");
    }

    [Fact]
    public async Task ItLapses_WhenTheMarkingChanges()
    {
        var s = await Brief(304);
        await Keep(s, Robin);

        await Edit(s.Article, f => f.WhyItMattersTlp = TlpLevel.Green);

        Warns(await Stored(s.Article)).Should().BeTrue("TLP:GREEN still reaches the company-wide edition, and it wasn't kept at GREEN");
    }

    [Fact]
    public async Task SomeoneWhoCantWriteArticles_IsRefused()
    {
        var s = await Brief(305);

        await FluentActions.Awaiting(() => Keep(s, Owner)).Should().ThrowAsync<ForbiddenException>();

        (await Stored(s.Article)).WhyItMattersKeptHash.Should().BeNull();
    }

    [Fact]
    public async Task AStaleEditVersion_IsRefused()
    {
        var s = await Brief(306);

        await FluentActions.Awaiting(() => Keep(s, Robin, Guid.NewGuid())).Should().ThrowAsync<StaleEditException>();

        (await Stored(s.Article)).WhyItMattersKeptHash.Should().BeNull();
    }

    [Fact]
    public async Task OnASentBrief_OrOneTheArticleIsntOn_ItIsRefused()
    {
        var sent = await Brief(307, sent: true);
        (await FluentActions.Awaiting(() => Keep(sent, Robin)).Should().ThrowAsync<InvalidOperationException>()).WithMessage("*sent*");

        var open = await Brief(308);
        await FluentActions.Awaiting(() => Keep(open with { Brief = Guid.NewGuid() }, Robin)).Should().ThrowAsync<InvalidOperationException>();

        (await Stored(sent.Article)).WhyItMattersKeptHash.Should().BeNull();
        (await Stored(open.Article)).WhyItMattersKeptHash.Should().BeNull();
    }

    [Fact]
    public async Task TheEditionsCarryExactlyWhatTheyDidBefore()
    {
        var s = await Brief(309);
        async Task<string> Editions()
        {
            await using var scope = SignedIn(app, SignIn.Analyst);
            var brief = (await scope.ServiceProvider.GetRequiredService<BriefService>().GetAsync(s.Day))!;
            return string.Join(" / ", new[] { (Edition.Public, TlpLevel.Green), (Edition.Team, TlpLevel.AmberStrict) }.Select(e =>
                string.Join(" | ", BriefEditions.Build(brief, e.Item1, e.Item2).Articles.Select(x => $"{x.Article.Id} {x.ShowWhy} {x.WithheldParts} {x.Marking}"))
                + " nothing: " + string.Join(",", BriefSteps.NothingToSend(brief, TlpLevel.AmberStrict, TlpLevel.Green))));
        }
        var before = await Editions();
        before.Should().Contain($"{s.Article} True 0 {TlpLevel.Clear}", "the company-wide edition carries the paragraph");

        await Keep(s, Robin);

        (await Editions()).Should().Be(before);
    }

    // The brief's page, as /briefs/{day} draws it.
    private Task<Rendered<BriefPage>> Open(Setup s, SignIn who) =>
        RenderAsync<BriefPage>(app, $"/briefs/{s.Day:yyyy-MM-dd}", new() { ["Day"] = $"{s.Day:yyyy-MM-dd}" }, who);

    [Fact]
    public async Task TheRunningOrder_OffersKeepIt_BesideSetToAmber_AndOneClickClearsTheWarningAndBoth()
    {
        var s = await Brief(310);
        await using var r = await Open(s, Robin);

        var html = await r.Html();
        html.Should().Contain(">Set to AMBER</button>").And.Contain(">Keep it</button>")
            .And.Contain($"aria-label=\"Keep it: Why it matters on &#x201C;{s.Title}&#x201D; goes to the company-wide edition at TLP:CLEAR\"");
        html.IndexOf(">Keep it<", StringComparison.Ordinal).Should().BeGreaterThan(html.IndexOf(">Set to AMBER<", StringComparison.Ordinal), "it sits after the fix");

        await r.Click(p => p.Fix(s.Article, ArticleFix.KeepWhyItMatters));

        await using var again = await Open(s, Robin);
        html = await again.Html();
        html.Should().NotContain(Warned).And.NotContain("Set to AMBER").And.NotContain(">Keep it<").And.NotContain("will reach the company-wide edition");
        (await Stored(s.Article)).WhyItMattersTlp.Should().Be(TlpLevel.Clear, "nothing was raised");
        var toast = r.Scope.ServiceProvider.GetRequiredService<ToastService>().Toasts.Should().ContainSingle().Subject;
        toast.Message.Should().Be($"Kept Why it matters on “{s.Title}” at TLP:CLEAR. It goes to the company-wide edition as it is.");
        toast.Undo.Should().BeNull();
    }

    [Fact]
    public async Task TheRunningOrder_DoesntOfferKeepIt_ToAnActionOwner_OrOnASentBrief()
    {
        var open = await Brief(311);
        await using (var owner = await Open(open, Owner))
        {
            (await owner.Html()).Should().Contain(open.Title).And.NotContain(">Keep it<");
            await owner.Click(p => p.Fix(open.Article, ArticleFix.KeepWhyItMatters));
            (await Stored(open.Article)).WhyItMattersKeptHash.Should().BeNull("the page doesn't act for someone who can't edit");
        }

        var sent = await Brief(312, sent: true);
        await using var r = await Open(sent, Robin);
        (await r.Html()).Should().Contain(Warned).And.NotContain(">Keep it<");
    }

    [Fact]
    public async Task AColleaguesSaveMeanwhile_IsRefused_FromThePage()
    {
        var s = await Brief(313);
        await using var r = await Open(s, Robin);
        await Edit(s.Article, f => f.Bluf = "Patch it today.");

        await r.Click(p => p.Fix(s.Article, ArticleFix.KeepWhyItMatters));

        (await Stored(s.Article)).WhyItMattersKeptHash.Should().BeNull("the page's copy was out of date");
        r.Scope.ServiceProvider.GetRequiredService<ToastService>().Toasts.Should().Contain(t => t.Message.Contains("Someone else saved this article"));
    }

    [Fact]
    public async Task AColleaguesOpenPage_DropsTheWarning_WhenItIsKept()
    {
        var s = await Brief(314);
        await using var colleague = await Open(s, SignIn.Analyst);
        (await colleague.Html()).Should().Contain(Warned);

        await Keep(s, Robin);

        var gone = false;
        for (var i = 0; i < 150 && !gone; i++)
        {
            gone = !(await colleague.Html()).Contains(Warned, StringComparison.Ordinal);
            if (!gone) await Task.Delay(100);
        }
        gone.Should().BeTrue("the colleague's page hears the change and reads the brief again");
    }
}
