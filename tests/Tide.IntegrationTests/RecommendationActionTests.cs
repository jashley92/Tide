using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Actions;
using Tide.Application.Articles;
using Tide.Application.Briefs;
using Tide.Application.Org;
using Tide.Application.Security;
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

    private async Task<int> Raise(Guid article, Guid team, string title, string? line)
    {
        await using var scope = SignedIn(app, SignIn.Analyst);
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
        return (Cut(html, "<ul class=\"tide-reco\"", "</ul>"), Cut(html, "<div class=\"tide-slide-actions\"", "<div class=\"tide-quick-add\""));
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
        var byHand = await Raise(article, team, "Brief the service desk", null);
        await using var pat = await Open(app, day);
        await FirstArticle(pat);
        var (reco, others) = Slide(await pat.Html());
        reco.Should().Contain("Apply the vendor hotfix to both gateways").And.Contain($"A-{linked}").And.Contain("Hunt for the indicators in VPN logs")
            .And.Contain(" Raise</button>", "the line not raised yet");
        others.Should().Contain($"A-{byHand}").And.NotContain($"A-{linked}", "listed once, against its line").And.Contain("Other actions");
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
        wrap[..wrap.IndexOf("</div></div>", StringComparison.Ordinal)].Should().Contain("Hunt for the indicators in VPN logs").And.NotContain("Apply the vendor hotfix");
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
