using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Actions;
using Tide.Application.Articles;
using Tide.Application.Briefs;
using Tide.Application.Org;
using Tide.Application.Security;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Web.Components.Pages;
using Tide.Web.Services;
using static Tide.IntegrationTests.ComponentHarness;
using static Tide.IntegrationTests.PresentHarness;

namespace Tide.IntegrationTests;

/// <summary>
/// An action raised from a recommended line keeps a stored link to that line: raised through ActionService (checked,
/// audited), at most one open action per line, refused for a line the article no longer has.
/// </summary>
public class RecommendationLinkServiceTests : IDisposable
{
    internal const string Lines = "- Apply the vendor hotfix to both gateways\n- Hunt for the indicators in VPN logs";
    private readonly TestDb _db = new();

    public RecommendationLinkServiceTests() => _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };

    public void Dispose() => _db.Dispose();

    private async Task<(Guid Article, Guid Team)> Article(string? recommended = Lines)
    {
        var svc = _db.Articles();
        var id = await svc.CreateAsync("Edge VPN zero-day", "Vendor", null, TlpLevel.Clear);
        var form = ArticleForm.From((await svc.GetAsync(id))!);
        form.Bluf = "Exploited.";
        form.RecommendedActions = recommended;
        await svc.SaveAsync(form);
        return (id, await _db.Teams().AddAsync("Vulnerability Management", null, null));
    }

    [Fact]
    public async Task RaisedFromALine_TheActionStoresThatLine_UnderATitleOfItsOwn()
    {
        var (article, team) = await Article();
        var actions = _db.Actions();
        var n = await actions.QuickAddAsync(article, "Patch both gateways", ActionKind.PatchRemediate, team, null,
            recommendation: "apply the vendor  hotfix to both gateways");
        var a = (await actions.GetAsync(n))!.Action;
        a.RecommendationLine.Should().Be("Apply the vendor hotfix to both gateways", "the line as the article has it");
        a.Title.Should().Be("Patch both gateways");

        var byHand = await actions.QuickAddAsync(article, "Brief the service desk", ActionKind.Awareness, team, null);
        (await actions.GetAsync(byHand))!.Action.RecommendationLine.Should().BeNull("raised by hand");
    }

    [Fact]
    public async Task ALineTheArticleDoesntHave_IsRefused_AndSoIsASecondOpenActionForALine()
    {
        var (article, team) = await Article();
        var actions = _db.Actions();
        var raise = () => actions.QuickAddAsync(article, "Hunt", ActionKind.ThreatHunt, team, null, recommendation: "Hunt for the indicators in VPN logs");
        var first = await raise();
        await raise.Should().ThrowAsync<InvalidOperationException>().WithMessage($"*A-{first}*");
        await actions.Awaiting(s => s.QuickAddAsync(article, "Block it", ActionKind.Block, team, null, recommendation: "Block the C2"))
            .Should().ThrowAsync<ArgumentException>();

        // Deleted (or cancelled), the line is free again.
        await actions.DeleteAsync((await actions.GetAsync(first))!.Action.Id, "A duplicate.");
        (await raise()).Should().BeGreaterThan(first);
    }

    [Fact]
    public async Task TheLink_IsCheckedWhereverAnActionIsCreated_NotOnlyInTheMeeting()
    {
        var (article, team) = await Article();
        var actions = _db.Actions();
        await actions.Awaiting(s => s.CreateAsync(new ActionForm { Title = "Hunt", TeamId = team, RecommendationLine = "Hunt for the indicators in VPN logs" }))
            .Should().ThrowAsync<ArgumentException>("a recommendation is an article's");
        await actions.Awaiting(s => s.CreateAsync(new ActionForm { Title = "Block", TeamId = team, ArticleId = article, RecommendationLine = "Block the C2" }))
            .Should().ThrowAsync<ArgumentException>();
        var n = await actions.CreateAsync(new ActionForm { Title = "Hunt", TeamId = team, ArticleId = article, RecommendationLine = "hunt for the indicators in vpn logs" });
        (await actions.GetAsync(n))!.Action.RecommendationLine.Should().Be("Hunt for the indicators in VPN logs");
    }

    [Fact]
    public async Task AReopenedAction_WhoseLineAnotherNowHolds_KeepsItsTitle_AndJoinsTheOtherActions()
    {
        var (article, team) = await Article();
        var actions = _db.Actions();
        const string line = "Hunt for the indicators in VPN logs";
        var first = await actions.QuickAddAsync(article, "Hunt", ActionKind.ThreatHunt, team, null, recommendation: line);
        var a = (await actions.GetAsync(first))!.Action;
        await actions.SetStatusAsync(a.Id, ActionStatus.Cancelled, ActionOutcome.None, "Raised too early.");
        var second = await actions.QuickAddAsync(article, "Hunt again", ActionKind.ThreatHunt, team, null, recommendation: line);

        await actions.SetStatusAsync(a.Id, ActionStatus.Open, ActionOutcome.None, null);
        var reopened = (await actions.GetAsync(first))!;
        reopened.Action.RecommendationLine.Should().BeNull("A-" + second + " holds the line now");
        reopened.Action.Title.Should().Be("Hunt");
        (await actions.GetAsync(second))!.Action.RecommendationLine.Should().Be(line);

        // Reopened while no other action holds its line, it keeps it.
        await actions.SetStatusAsync((await actions.GetAsync(second))!.Action.Id, ActionStatus.Cancelled, ActionOutcome.None, "Duplicate.");
        var third = await actions.QuickAddAsync(article, "Patch", ActionKind.PatchRemediate, team, null, recommendation: "Apply the vendor hotfix to both gateways");
        var patch = (await actions.GetAsync(third))!.Action;
        await actions.SetStatusAsync(patch.Id, ActionStatus.Cancelled, ActionOutcome.None, "Wrong team.");
        await actions.SetStatusAsync(patch.Id, ActionStatus.Open, ActionOutcome.None, null);
        (await actions.GetAsync(third))!.Action.RecommendationLine.Should().Be("Apply the vendor hotfix to both gateways");
    }

    [Fact]
    public async Task AnActionMovedBetweenStandingStatuses_KeepsItsLine_ThoughAnotherHoldsItToo()
    {
        var (article, team) = await Article();
        var actions = _db.Actions();
        const string line = "Hunt for the indicators in VPN logs";
        var first = await actions.QuickAddAsync(article, "Hunt", ActionKind.ThreatHunt, team, null, recommendation: line);
        var second = await actions.QuickAddAsync(article, "Hunt as well", ActionKind.ThreatHunt, team, null);
        await using (var db = _db.Context())   // two raised at the same moment, both standing on the line
        {
            var other = db.Actions.Single(x => x.Number == second);
            other.RecommendationLine = line;
            await db.SaveChangesAsync();
        }
        var a = (await actions.GetAsync(first))!.Action;

        await actions.SetStatusAsync(a.Id, ActionStatus.InProgress, ActionOutcome.None, null);

        (await actions.GetAsync(first))!.Action.RecommendationLine.Should().Be(line, "only a move back from Cancelled lets the line go");
    }

    [Fact]
    public async Task TheUpgrade_LinksAnOldActionToTheLineItsTitleIs_AsTheArticleWritesIt_OneActionPerLine()
    {
        var (article, team) = await Article("- **Block** the C2 at the proxy\n- Hunt for the indicators in VPN logs for 30 days.\n- Patch the gateways");
        await using (var ctx = _db.Context())
        {
            ctx.Actions.AddRange(
                new ActionItem { Number = 901, Title = "block the C2 at the proxy", TeamId = team, ArticleId = article },
                new ActionItem { Number = 902, Title = "Hunt for the indicators in VPN logs for 30 days", TeamId = team, ArticleId = article },
                new ActionItem { Number = 903, Title = "Patch the gateways", TeamId = team, ArticleId = article, Status = ActionStatus.Done, Outcome = ActionOutcome.Other },
                new ActionItem { Number = 904, Title = "Patch the gateways", TeamId = team, ArticleId = article },
                new ActionItem { Number = 905, Title = "Patch the gateways", TeamId = team, ArticleId = article },
                new ActionItem { Number = 906, Title = "Patch the gateways", TeamId = team, ArticleId = article, Status = ActionStatus.Cancelled });
            ctx.Settings.RemoveRange(ctx.Settings.Where(s => s.Key == Tide.Infrastructure.Persistence.DatabaseInitializer.RecommendationLinksKey));
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = _db.Context())
            (await Tide.Infrastructure.Persistence.DatabaseInitializer.LinkRecommendationsAsync(ctx, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance)).Should().Be(2);

        await using (var ctx = _db.Context())
        {
            var lines = ctx.Actions.Where(a => a.Number > 900).ToDictionary(a => a.Number, a => a.RecommendationLine);
            lines[901].Should().Be("Block the C2 at the proxy", "the line as the article has it, emphasis aside");
            lines[902].Should().BeNull("the line ends in a full stop the title hasn't: not the line's words");
            lines[904].Should().Be("Patch the gateways", "the oldest open one of the three");
            lines[903].Should().BeNull("closed: an open one has the line");
            lines[905].Should().BeNull("one action per line");
            lines[906].Should().BeNull("cancelled");
        }
        await using (var ctx = _db.Context())
            (await Tide.Infrastructure.Persistence.DatabaseInitializer.LinkRecommendationsAsync(ctx, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance)).Should().Be(0, "once");
    }

    [Fact]
    public async Task SomeoneWhoCantCreateActions_CantRaiseOne()
    {
        var (article, team) = await Article();
        _db.User.Roles = new HashSet<AppRole> { AppRole.Viewer };
        await _db.Actions().Awaiting(s => s.QuickAddAsync(article, "Hunt", ActionKind.ThreatHunt, team, null, recommendation: "Hunt for the indicators in VPN logs"))
            .Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task TheArticlesActions_CarryTheirLines_ForTheEditorsCount()
    {
        var (article, team) = await Article();
        var actions = _db.Actions();
        await actions.QuickAddAsync(article, "Patch", ActionKind.PatchRemediate, team, null, recommendation: "Apply the vendor hotfix to both gateways");
        var rows = await actions.ForArticleAsync(article);
        RecommendedActions.Unraised(Lines, rows, r => r.RecommendationLine).Should().Equal("Hunt for the indicators in VPN logs");
    }
}

/// <summary>
/// The article slide: each recommended line shows the action it became; the actions raised by hand keep a list of their
/// own; a line edited or removed, or its action deleted, puts things back where they belong. Nothing of an article an
/// edition can't show reaches it.
/// </summary>
public class RecommendationSlideTests(TideApp app) : IClassFixture<TideApp>
{
    private const string Lines = RecommendationLinkServiceTests.Lines;

    private DateOnly Today { get; } = app.Services.GetRequiredService<OrgClock>().CurrentBriefDate;

    private async Task<(DateOnly Day, Guid Article, Guid Team)> Brief(int daysBack, TlpLevel tlp = TlpLevel.Clear)
    {
        var day = Today;
        for (var i = 0; i < daysBack; i++) day = WorkingDays.Weekdays.Previous(day);
        await using var scope = SignedIn(app, SignIn.Analyst);
        var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
        var briefs = scope.ServiceProvider.GetRequiredService<BriefService>();
        var brief = await briefs.GetOrStartAsync(day);
        var id = await articles.CreateAsync($"Recommendation slide {daysBack}", "Vendor", null, tlp);
        var form = ArticleForm.From((await articles.GetAsync(id))!);
        form.Bluf = "Exploited.";
        form.RecommendedActions = Lines;
        await articles.SaveAsync(form);
        await briefs.AddArticleAsync(brief.Id, id);
        var team = await scope.ServiceProvider.GetRequiredService<Tide.Application.Actions.TeamService>().AddAsync($"Slide team {daysBack}", null, null);
        return (day, id, team);
    }

    private static readonly SignIn Robin = new("dev:reco-robin", "Robin Analyst", "Analyst");

    private async Task<int> Raise(Guid article, Guid team, string title, string? line, SignIn? who = null)
    {
        await using var scope = SignedIn(app, who ?? SignIn.Analyst);
        return await scope.ServiceProvider.GetRequiredService<ActionService>().QuickAddAsync(article, title, ActionKind.Other, team, null, recommendation: line);
    }

    /// <summary>The slide's recommended lines and its other actions, as markup.</summary>
    private static (string Reco, string Others) Slide(string html)
    {
        static string Cut(string html, string start, string end)
        {
            var at = html.IndexOf(start, StringComparison.Ordinal);
            return at < 0 ? "" : html[at..html.IndexOf(end, at, StringComparison.Ordinal)];
        }
        return (Cut(html, "<ul class=\"tide-reco\"", "</ul>"), Cut(html, "<div class=\"tide-slide-actions\"", "<aside id=\"present-rail\""));
    }

    private static async Task FirstArticle(Rendered<Present> r)
    {
        await r.Click(p => p.OnPresentKey("last"));
        await r.Click(p => p.OnPresentKey("prev"));
    }

    [Fact]
    public async Task EachLine_ShowsTheActionItBecame_AndActionsRaisedByHand_KeepAListOfTheirOwn()
    {
        var (day, article, team) = await Brief(201);
        var linked = await Raise(article, team, "Patch both gateways tonight", "Apply the vendor hotfix to both gateways");
        var byHand = await Raise(article, team, "Brief the service desk", null, Robin);   // Robin's: Tester can take it
        await using var pat = await Open(app, day);
        await FirstArticle(pat);
        var html = await pat.Html();
        var (reco, others) = Slide(html);
        reco.Should().Contain("Apply the vendor hotfix to both gateways").And.Contain($"A-{linked}").And.Contain("Hunt for the indicators in VPN logs");
        html.Should().Contain("aria-label=\"Kind for: Hunt for the indicators in VPN logs\"", "the line not raised yet is raised from the rail");
        others.Should().Contain($"A-{byHand}").And.NotContain($"A-{linked}", "listed once, against its line").And.Contain("Other actions");
        html.Should().Contain($"aria-label=\"Take A-{byHand}\"", "Take it says which action (in the rail)");
    }

    [Fact]
    public async Task ALineEditedAway_LeavesItsActionAmongTheOthers_AndADeletedActionFreesItsLine()
    {
        var (day, article, team) = await Brief(202);
        var linked = await Raise(article, team, "Hunt in VPN logs", "Hunt for the indicators in VPN logs");
        await using (var scope = SignedIn(app, SignIn.Analyst))
        {
            var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
            var form = ArticleForm.From((await articles.GetAsync(article))!);
            form.RecommendedActions = "- Apply the vendor hotfix to both gateways\n- Hunt for the indicators in VPN and proxy logs";
            await articles.SaveAsync(form);
        }
        await using var pat = await Open(app, day);
        await FirstArticle(pat);
        var (reco, others) = Slide(await pat.Html());
        others.Should().Contain($"A-{linked}", "its line is gone, so it's one of the article's other actions");
        reco.Should().NotContain($"A-{linked}");

        await using (var scope = SignedIn(app, SignIn.Analyst))
        {
            var actions = scope.ServiceProvider.GetRequiredService<ActionService>();
            await actions.DeleteAsync((await actions.GetAsync(linked))!.Action.Id, "Raised against the old wording.");
        }
        await Do(pat, p => p.OnChangedElsewhere([new Tide.Application.Abstractions.Change(Tide.Application.Abstractions.ChangeFeed.Actions, "x", "Robin", Guid.NewGuid())]));
        (reco, others) = Slide(await pat.Html());
        others.Should().NotContain($"A-{linked}");
    }

    [Fact]
    public async Task TheWrapUp_ListsOnlyTheLinesNoActionWasRaisedFor()
    {
        var (day, article, team) = await Brief(203);
        await Raise(article, team, "Patch both gateways tonight", "Apply the vendor hotfix to both gateways");
        await Raise(article, team, "Hunt for the indicators in VPN logs", null);   // the same words, by hand: not the line's
        await using var pat = await Open(app, day);
        await pat.Click(p => p.OnPresentKey("last"));
        var html = await pat.Html();
        var at = html.IndexOf("Recommended, with no action raised yet", StringComparison.Ordinal);
        at.Should().BeGreaterThan(0);
        var wrap = html[at..];
        wrap[..wrap.IndexOf("</section>", StringComparison.Ordinal)].Should().Contain("Hunt for the indicators in VPN logs").And.NotContain("Apply the vendor hotfix");
    }

    [Fact]
    public async Task AnArticleAboveTheCompanyWideCeiling_LeavesNoTraceOfItsLinesThere()
    {
        var (day, article, team) = await Brief(204, TlpLevel.Red);
        var linked = await Raise(article, team, "Patch both gateways tonight", "Apply the vendor hotfix to both gateways");
        await using var pat = await Open(app, day);
        await Do(pat, p => p.SetEdition(new ChangeEventArgs { Value = Edition.Public.ToString() }));
        await pat.Click(p => p.OnPresentKey("last"));
        var html = await pat.Html();
        html.Should().NotContain("Apply the vendor hotfix").And.NotContain("Hunt for the indicators").And.NotContain($"A-{linked}");
    }
}
