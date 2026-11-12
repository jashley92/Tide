using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Tide.Application.Actions;
using Tide.Application.Articles;
using Tide.Application.Briefs;
using Tide.Application.Org;
using Tide.Application.Settings;
using Tide.Domain.Enums;
using Tide.Web.Components.Pages;
using Tide.Web.Components.Pages.Admin;
using static Tide.IntegrationTests.ComponentHarness;
using static Tide.IntegrationTests.PresentHarness;

namespace Tide.IntegrationTests;

/// <summary>
/// An action opened in a meeting, beside the slide rather than over it (<see cref="OrgOptions.ActionBesideSlide"/>): off
/// until an administrator turns it on after a rehearsal. Everyone following sees it there as the presenter does, and it
/// shows no more than the card over the slide would.
/// </summary>
public class PresentActionBesideTests(TideApp app) : IClassFixture<TideApp>
{
    private static readonly SignIn Owner = new("dev:beside-owner", "Bea Owner", "ActionOwner");
    private static readonly SignIn Visitor = new("dev:beside-visitor", "Val Visitor", "Visitor");

    private DateOnly Today { get; } = app.Services.GetRequiredService<OrgClock>().CurrentBriefDate;

    private const string Beside = "tide-present-action is-beside";

    private async Task Turn(bool on)
    {
        await using var scope = SignedIn(app, SignIn.Analyst);
        await scope.ServiceProvider.GetRequiredService<OperationalSettingsService>()
            .SaveAsync(new Dictionary<string, string?> { [OperationalSettings.ActionBesideSlide] = on ? "true" : "false" });
    }

    // A past day's brief with one article and an action raised from it; and an action from an article above the team
    // edition's ceiling (in no edition), with words of its own that mustn't show.
    private async Task<(DateOnly Day, int Action, int Above)> Brief(int daysBack)
    {
        var day = Today;
        for (var i = 0; i < daysBack; i++) day = WorkingDays.Weekdays.Previous(day);
        await using var scope = SignedIn(app, SignIn.Analyst);
        var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
        var briefs = scope.ServiceProvider.GetRequiredService<BriefService>();
        var actions = scope.ServiceProvider.GetRequiredService<ActionService>();
        var team = (await scope.ServiceProvider.GetRequiredService<TeamService>().ListAsync())[0].Id;
        var brief = await briefs.GetOrStartAsync(day);
        var first = await articles.CreateAsync($"Beside article {daysBack}", "Vendor", null, TlpLevel.Clear);
        await briefs.AddArticleAsync(brief.Id, first);
        var number = await actions.QuickAddAsync(first, $"Sweep beside {daysBack}", ActionKind.IocSweep, team, null, day);
        var red = await articles.CreateAsync($"Red article {daysBack}", "Vendor", null, TlpLevel.Red);
        var above = await actions.CreateAsync(new ActionForm
        {
            Title = $"Named only {daysBack}", Description = $"Red details {daysBack}", TeamId = team, ArticleId = red,
        });
        return (day, number, above);
    }

    private static async Task FirstArticle(Rendered<Present> r)
    {
        await r.Click(p => p.OnPresentKey("last"));
        await r.Click(p => p.OnPresentKey("prev"));
    }

    [Fact]
    public void TheSetting_IsOff_ByDefault_AndASwitch()
    {
        new OrgOptions().ActionBesideSlide.Should().BeFalse("the overlay stays until a live rehearsal");
        OperationalSettings.Normalize(OperationalSettings.ActionBesideSlide, "True").Should().Be("true");
        OperationalSettings.Problem(OperationalSettings.ActionBesideSlide, "sometimes").Should().NotBeNull();
        Tide.Application.Operations.SettingsCatalog.InApp.Should().Contain(s => s.Key == OperationalSettings.ActionBesideSlide);
        app.Services.GetRequiredService<IOptionsMonitor<OrgOptions>>().CurrentValue.ActionBesideSlide.Should().BeFalse("nothing saved: the server's default");
    }

    [Fact]
    public async Task TurnedOnOrOff_WhileAnActionIsOpen_ItMovesThereForEveryone_WithoutOpeningItAgain()
    {
        await Turn(false);
        try
        {
            var (day, number, _) = await Brief(127);
            await using var pat = await Open(app, day);
            await Do(pat, p => p.StartMeeting());
            await using var owner = await Open(app, day, Owner);
            await Do(owner, p => p.JoinIfLive());
            await FirstArticle(pat);
            await Do(owner, p => p.Follow());
            await Do(pat, p => p.ShowAction(number));
            await Do(owner, p => p.Follow());
            (await owner.Html()).Should().Contain("present-action-title").And.NotContain("is-beside");

            await Turn(true);
            (await pat.HtmlOnce("has-beside")).Should().Contain(Beside, "the presenter's open card moves beside the slide");
            (await owner.HtmlOnce("has-beside")).Should().Contain(Beside, "and the room's with it");

            await Turn(false);
            for (var i = 0; i < 50 && (await owner.Html()).Contains("has-beside", StringComparison.Ordinal); i++) await Task.Delay(100);
            (await pat.Html()).Should().NotContain("has-beside").And.Contain("present-action-title");
            (await owner.Html()).Should().NotContain("has-beside").And.Contain("present-action-title");
            EndMeeting(app, day);
        }
        finally { await Turn(false); }
    }

    [Fact]
    public async Task Off_TheActionOpensOverTheSlide_AsBefore()
    {
        await Turn(false);
        var (day, number, _) = await Brief(121);
        await using var pat = await Open(app, day);
        await Do(pat, p => p.StartMeeting());
        await FirstArticle(pat);
        await Do(pat, p => p.ShowAction(number));
        var html = await pat.Html();
        html.Should().Contain("present-action-title").And.NotContain("is-beside").And.NotContain("has-beside");
        EndMeeting(app, day);
    }

    [Fact]
    public async Task On_TheActionOpensBesideTheSlide_ForThePresenterAndEveryoneFollowing()
    {
        await Turn(true);
        try
        {
            var (day, number, _) = await Brief(122);
            await using var pat = await Open(app, day);
            await Do(pat, p => p.StartMeeting());
            await using var owner = await Open(app, day, Owner);
            await Do(owner, p => p.JoinIfLive());
            await FirstArticle(pat);
            await Do(owner, p => p.Follow());

            await Do(pat, p => p.ShowAction(number));
            var html = await pat.Html();
            html.Should().Contain("has-beside").And.Contain(Beside);
            html.IndexOf("id=\"present-rail\"", StringComparison.Ordinal).Should().BeGreaterThan(0, "the presenter's rail stays open beside it");

            await Do(owner, p => p.Follow());
            var theirs = await owner.Html();
            theirs.Should().Contain("has-beside").And.Contain(Beside).And.Contain("Sweep beside 122").And.NotContain("id=\"present-rail\"");

            // Closed by the presenter: the slide has the room back, for everyone.
            await Do(pat, p => p.CloseAction());
            await Do(owner, p => p.Follow());
            (await pat.Html()).Should().NotContain("has-beside");
            (await owner.Html()).Should().NotContain("has-beside").And.NotContain("present-action-title");
            EndMeeting(app, day);
        }
        finally { await Turn(false); }
    }

    [Fact]
    public async Task On_AVisitorFollowing_SeesTheActionBeside_WithNoLinksOut()
    {
        await Turn(true);
        try
        {
            var (day, number, _) = await Brief(123);
            await using var pat = await Open(app, day);
            await Do(pat, p => p.StartMeeting());
            await FirstArticle(pat);
            await using var vic = await RenderAsync<Present>(app, $"/present/{day:yyyy-MM-dd}", new() { ["Day"] = $"{day:yyyy-MM-dd}" }, Visitor);
            await vic.Click(async p => { await p.GoLive(); await p.OnPresentKey("render"); });
            await Do(pat, p => p.ShowAction(number));
            await vic.Click(async p => { await p.Follow(); await p.OnPresentKey("render"); });
            var html = await vic.Html();
            html.Should().Contain(Beside).And.Contain("has-beside").And.NotContain("Open the page").And.NotContain("id=\"present-rail\"");
            EndMeeting(app, day);
        }
        finally { await Turn(false); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnActionFromAnArticleAboveTheCeiling_IsNamed_AndShowsNoMore_BesideOrOver(bool beside)
    {
        await Turn(beside);
        try
        {
            var n = beside ? 124 : 125;
            var (day, _, above) = await Brief(n);
            await using var pat = await Open(app, day);
            await Do(pat, p => p.StartMeeting());
            await using var owner = await Open(app, day, Owner);
            await Do(owner, p => p.JoinIfLive());
            await FirstArticle(pat);
            await Do(owner, p => p.Follow());

            await Do(pat, p => p.ShowAction(above));
            await Do(owner, p => p.Follow());
            foreach (var html in new[] { await pat.Html(), await owner.Html() })
            {
                html.Should().Contain("present-action-title").And.Contain($"Named only {n}", "the action is named, as the Actions list names it");
                html.Should().NotContain($"Red details {n}").And.NotContain("Comments (").And.NotContain("History")
                    .And.NotContain($"Red article {n}");
                html.Should().Contain("above the meeting");
            }
            EndMeeting(app, day);
        }
        finally { await Turn(false); }
    }

    [Fact]
    public async Task AMeetingsReadOfAnAction_FromAnArticleAboveTheCeiling_CarriesOnlyItsName()
    {
        var (_, below, above) = await Brief(126);
        await using var scope = SignedIn(app, Owner);
        var actions = scope.ServiceProvider.GetRequiredService<ActionService>();
        var named = (await actions.MeetingViewAsync(above, TlpLevel.AmberStrict))!;
        named.Withheld.Should().BeTrue();
        named.Action.Title.Should().Be("Named only 126");
        named.Action.Description.Should().BeNull("nothing more of it leaves the service");
        named.Action.Article.Should().BeNull();
        named.Action.Comments.Should().BeEmpty();
        named.CanWork.Should().BeFalse();
        (await actions.MeetingViewAsync(above, TlpLevel.Red))!.Withheld.Should().BeFalse("at or below the ceiling it's read whole");
        (await actions.MeetingViewAsync(below, TlpLevel.AmberStrict))!.Withheld.Should().BeFalse();
    }

    [Fact]
    public async Task AnAdministrator_TurnsItOn_InOrganizationSettings()
    {
        try
        {
            await using var page = await RenderAsync<OrganizationAdmin>(app, "/admin/organization", @as: SignIn.Analyst);
            (await page.Html()).Should().Contain("id=\"org-beside\"").And.Contain("rehears");
            await page.Click(p => { p.ActionBeside = true; return p.Save(); });
            app.Services.GetRequiredService<OrgClock>().ActionBesideSlide.Should().BeTrue("at once, as the org's other settings");
        }
        finally { await Turn(false); }
    }
}
