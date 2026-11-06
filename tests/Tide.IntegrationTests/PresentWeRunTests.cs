using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Actions;
using Tide.Application.Articles;
using Tide.Application.Briefs;
using Tide.Application.Org;
using Tide.Application.Stack;
using Tide.Domain.Enums;
using Tide.Web.Components.Pages;
using Tide.Web.Components.Shared;
using Tide.Web.Services;
using static Tide.IntegrationTests.ComponentHarness;
using static Tide.IntegrationTests.PresentHarness;

namespace Tide.IntegrationTests;

/// <summary>
/// The Our stack products an article is tagged with, in present mode: a "We run" chip on the team edition's slide (none
/// on the company-wide one), and the product's card over the slide, which the presenter opens for everyone following.
/// </summary>
public class PresentWeRunTests(TideApp app) : IClassFixture<TideApp>
{
    private static readonly SignIn Owner = new("dev:werun-owner", "Olive Owner", "ActionOwner");
    private static readonly SignIn Robin = new("dev:werun-robin", "Robin Analyst", "Analyst");

    private DateOnly Today => app.Services.GetRequiredService<OrgClock>().CurrentBriefDate;
    private LiveMeetings Meetings => app.Services.GetRequiredService<LiveMeetings>();

    private static DateOnly BusinessDaysBefore(DateOnly day, int n)
    {
        for (var i = 0; i < n; i++) day = WorkingDays.Weekdays.Previous(day);
        return day;
    }

    private sealed record Setup(DateOnly Day, Guid First, string FirstTitle, Guid Product, string Name, Guid Retired, string Other);

    // A past day's brief with two articles, both tagged with a product we run; the first also with a retired one, and
    // another article (not on the brief) that names the product.
    private async Task<Setup> Brief(int daysBack)
    {
        var day = BusinessDaysBefore(Today, daysBack);
        await using var scope = SignedIn(app, SignIn.Analyst);
        var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
        var briefs = scope.ServiceProvider.GetRequiredService<BriefService>();
        var stack = scope.ServiceProvider.GetRequiredService<StackService>();
        var team = (await scope.ServiceProvider.GetRequiredService<TeamService>().ListAsync())[0];
        var brief = await briefs.GetOrStartAsync(day);
        var firstTitle = $"We run first {daysBack}";
        var first = await articles.CreateAsync(firstTitle, "Vendor", null, TlpLevel.Clear);
        var second = await articles.CreateAsync($"We run second {daysBack}", "Vendor", null, TlpLevel.Clear);
        await briefs.AddArticleAsync(brief.Id, first);
        await briefs.AddArticleAsync(brief.Id, second);

        var name = $"Contoso VPN Gateway {daysBack}";
        var product = await stack.SaveAsync(new StackForm
        {
            Name = name, OtherNames = $"CVG {daysBack}", Versions = "9.1R18", Notes = "**Two** appliances in the DMZ.",
            TeamId = team.Id, CheckedOn = new DateOnly(2026, 10, 1),
        });
        var retired = await stack.SaveAsync(new StackForm { Name = $"Contoso Legacy Proxy {daysBack}", Versions = new string('9', StackService.ChipVersionsMax + 1) });
        await stack.SetRetiredAsync(retired, true);
        await stack.TagArticleAsync(first, product);
        await stack.TagArticleAsync(second, product);
        await stack.TagArticleAsync(first, retired);
        var other = $"{name} flaw exploited in the wild";
        await articles.CreateAsync(other, "Vendor", null, TlpLevel.Clear);
        return new Setup(day, first, firstTitle, product, name, retired, other);
    }

    private static async Task FirstArticle(Rendered<Present> r)
    {
        await r.Click(p => p.OnPresentKey("last"));
        await r.Click(p => p.OnPresentKey("prev"));
        await r.Click(p => p.OnPresentKey("prev"));
    }

    private static Task Edition(Rendered<Present> r, Edition edition) =>
        Do(r, p => p.SetEdition(new ChangeEventArgs { Value = edition.ToString() }));

    [Fact]
    public async Task TheTeamEdition_ShowsWeRunChips_AndTheirCard_AndTheCompanyWideEditionNothingOfThem()
    {
        var s = await Brief(69);
        await using var pat = await Open(app, s.Day);
        await FirstArticle(pat);
        var html = await pat.Html();
        html.Should().Contain("tide-slide-werun").And.Contain($"aria-label=\"We run {s.Name} 9.1R18: open its notes\"")
            .And.Contain("tide-slide-werun-retired", "a retired product is muted").And.Contain($"We run Contoso Legacy Proxy 69 (retired)")
            .And.NotContain(new string('9', StackService.ChipVersionsMax + 1), "long versions are left to the card");

        await Do(pat, p => p.ShowProduct(s.Product));
        html = await pat.Html();
        html.Should().Contain("present-product-title").And.Contain("<strong>Two</strong> appliances in the DMZ.", "the notes render as Markdown")
            .And.Contain("CVG 69").And.Contain("9.1R18").And.Contain(Ui.Day(new DateOnly(2026, 10, 1)))
            .And.Contain(s.Other, "another article naming it").And.Contain("We run second 69", "another article tagged with it")
            .And.NotContain($"</span> {s.FirstTitle}</li>", "not the article on the slide")
            .And.Contain($"href=\"stack/{s.Product}\" target=\"_blank\"");
        await Do(pat, p => p.OnPresentKey("close"));
        (await pat.Html()).Should().NotContain("present-product-title", "Esc closes it");

        // The company-wide edition has neither chips nor card: switching to it closes one that's open.
        await Do(pat, p => p.ShowProduct(s.Product));
        await Edition(pat, Tide.Domain.Enums.Edition.Public);
        html = await pat.Html();
        html.Should().NotContain("present-product-title").And.NotContain("tide-slide-werun").And.NotContain("Contoso");
        await Do(pat, p => p.ShowProduct(s.Product));
        (await pat.Html()).Should().NotContain("Contoso", "it can't be opened there either");

        await Edition(pat, Tide.Domain.Enums.Edition.Team);
        (await pat.Html()).Should().Contain("tide-slide-werun").And.NotContain("present-product-title", "back on the team edition, nothing reopens by itself");

        // A colleague tags another product on the article: its chip shows, without a toast.
        await using (var scope = SignedIn(app, SignIn.Analyst))
        {
            var stack = scope.ServiceProvider.GetRequiredService<StackService>();
            await stack.TagArticleAsync(s.First, await stack.SaveAsync(new StackForm { Name = "Fabrikam Mail Relay 69" }));
        }
        var heard = new Tide.Application.Abstractions.Change(Tide.Application.Abstractions.ChangeFeed.ArticleTags(s.First), "x", "Robin", Guid.NewGuid());
        await Do(pat, p => p.OnChangedElsewhere([heard]));
        (await pat.Html()).Should().Contain("We run Fabrikam Mail Relay 69: open its notes");
        pat.Scope.ServiceProvider.GetRequiredService<ToastService>().Toasts.Should().NotContain(t => t.Message.StartsWith("Robin"), "a tag is quiet");
    }

    [Fact]
    public async Task ThePresentersCard_ShowsForThoseFollowing_ALateJoiner_AndClosesForThem_WhileAFollowerOffAlonesIsTheirOwn()
    {
        var s = await Brief(70);
        await using var pat = await Open(app, s.Day);
        await Do(pat, p => p.StartMeeting());
        await using var owner = await Open(app, s.Day, Owner);
        await Do(owner, p => p.JoinIfLive());
        await FirstArticle(pat);
        await Do(owner, p => p.Follow());
        (await owner.Html()).Should().Contain("tide-slide-werun", "anyone on the team edition sees the chips");

        await Do(pat, p => p.ShowProduct(s.Product));
        Meetings.Get(s.Day)!.Product.Should().Be(s.Product);
        (await pat.Html()).Should().Contain("present-product-title").And.Contain("Shown to everyone following");
        await Do(owner, p => p.Follow());
        (await owner.Html()).Should().Contain("present-product-title").And.Contain("Tester is showing this").And.Contain(s.Other);

        // Someone joining late sees it too.
        await using var robin = await Open(app, s.Day, Robin);
        await Do(robin, p => p.JoinIfLive());
        (await robin.Html()).Should().Contain("present-product-title").And.Contain("Tester is showing this");

        // The presenter closes it: it closes for them.
        await Do(pat, p => p.CloseProduct());
        Meetings.Get(s.Day)!.Product.Should().BeNull();
        await Do(owner, p => p.Follow());
        await Do(robin, p => p.Follow());
        (await owner.Html()).Should().NotContain("present-product-title");
        (await robin.Html()).Should().NotContain("present-product-title");

        // Off on another slide, a follower opens one just for themselves.
        await owner.Click(p => p.OnPresentKey("next"));
        await Do(owner, p => p.ShowProduct(s.Product));
        (await owner.Html()).Should().Contain("present-product-title").And.Contain("Only you see this");
        Meetings.Get(s.Day)!.Product.Should().BeNull("theirs alone");
        await Do(robin, p => p.Follow());
        (await robin.Html()).Should().NotContain("present-product-title");

        // A product the room can't resolve closes quietly.
        await Do(pat, p => p.ShowProduct(s.Product));
        await Do(robin, p => p.Follow());
        (await robin.Html()).Should().Contain("present-product-title");
        using (var another = Meetings.Join(s.Day, "dev:tester", "Tester", true, "", Guid.NewGuid(), Meetings.Get(s.Day)!.Slide)!)
        {
            Meetings.Show(another, Guid.NewGuid()).Should().BeTrue("the presenter's other tab");
            await Do(robin, p => p.Follow());
            (await robin.Html()).Should().NotContain("present-product-title");
            robin.Scope.ServiceProvider.GetRequiredService<ToastService>().Toasts.Should().NotContain(t => t.IsError, "closed quietly");
        }

        // One shown to a page on another edition closes there.
        await Do(pat, p => p.ShowProduct(s.Product));
        await Do(robin, p => p.Follow());
        (await robin.Html()).Should().Contain("present-product-title");
        await Edition(robin, Tide.Domain.Enums.Edition.Public);
        await Do(robin, p => p.Follow());
        (await robin.Html()).Should().NotContain("present-product-title").And.NotContain("tide-slide-werun");

        // Moving on closes it for everyone.
        await Do(owner, p => p.ShowProduct(s.Product));
        await pat.Click(p => p.OnPresentKey("next"));
        Meetings.Get(s.Day)!.Product.Should().BeNull();

        EndMeeting(app, s.Day);
    }

    [Fact]
    public async Task ThePresentersCard_StaysWhenTheirSlideIsRenumbered_AndGoesWhenItsArticleLeavesTheBrief()
    {
        var s = await Brief(72);
        await using var pat = await Open(app, s.Day);
        await Do(pat, p => p.StartMeeting());
        await FirstArticle(pat);
        await Do(pat, p => p.ShowProduct(s.Product));
        var slide = Meetings.Get(s.Day)!.Slide;

        // A colleague adds the opening note: the article is a slide later, and still has its card open, for the room too.
        Guid briefId, item;
        await using (var scope = SignedIn(app, Robin))
        {
            var briefs = scope.ServiceProvider.GetRequiredService<BriefService>();
            var brief = (await briefs.GetAsync(s.Day))!;
            (briefId, item) = (brief.Id, brief.Items.Single(i => i.ArticleId == s.First).Id);
            await briefs.SetTeamNoteAsync(briefId, "- Patch window on Thursday");
        }
        var heard = new Tide.Application.Abstractions.Change(Tide.Application.Abstractions.ChangeFeed.Brief(briefId), Robin.UserId, Robin.Name, Guid.NewGuid());
        await Do(pat, p => p.OnChangedElsewhere([heard]));
        Meetings.Get(s.Day)!.Should().Match<LiveMeetingView>(v => v.Slide == slide + 1 && v.Product == s.Product);
        (await pat.Html()).Should().Contain("present-product-title").And.Contain($"<h2>{s.FirstTitle}</h2>");

        // The article leaves the brief: the next one takes its slide, and the card that was over it goes.
        await using (var scope = SignedIn(app, Robin))
            await scope.ServiceProvider.GetRequiredService<BriefService>().RemoveItemAsync(briefId, item);
        await Do(pat, p => p.OnChangedElsewhere([heard]));
        Meetings.Get(s.Day)!.Product.Should().BeNull();
        (await pat.Html()).Should().NotContain("present-product-title").And.Contain("<h2>We run second 72</h2>");
        EndMeeting(app, s.Day);
    }

    [Fact]
    public async Task ThePresentersOtherTab_ShowsAndClosesTheCardTheyOpenedInTheFirst()
    {
        var s = await Brief(73);
        await using var pat = await Open(app, s.Day);
        await Do(pat, p => p.StartMeeting());
        await using var projector = await Open(app, s.Day);
        await Do(projector, p => p.JoinIfLive());
        await FirstArticle(pat);
        await Do(projector, p => p.Follow());
        (await projector.Html()).Should().Contain($"<h2>{s.FirstTitle}</h2>");

        await Do(pat, p => p.ShowProduct(s.Product));
        await Do(projector, p => p.Follow());
        (await projector.Html()).Should().Contain("present-product-title").And.Contain("Shown to everyone following");

        await Do(pat, p => p.CloseProduct());
        await Do(projector, p => p.Follow());
        (await projector.Html()).Should().NotContain("present-product-title");

        // An action, likewise.
        int number;
        await using (var scope = SignedIn(app, SignIn.Analyst))
        {
            var team = (await scope.ServiceProvider.GetRequiredService<TeamService>().ListAsync())[0].Id;
            number = await scope.ServiceProvider.GetRequiredService<ActionService>()
                .QuickAddAsync(s.First, "Check the gateway's logs", ActionKind.PatchRemediate, team, null, s.Day);
        }
        await Do(pat, p => p.ShowAction(number));
        await Do(projector, p => p.Follow());
        (await projector.Html()).Should().Contain("present-action-title");
        await Do(pat, p => p.CloseAction());
        await Do(projector, p => p.Follow());
        (await projector.Html()).Should().NotContain("present-action-title");

        // One that can't be read isn't left open for the room.
        await Do(pat, p => p.ShowProduct(Guid.NewGuid()));
        Meetings.Get(s.Day)!.Product.Should().BeNull();
        await Do(pat, p => p.ShowAction(999_999));
        Meetings.Get(s.Day)!.Action.Should().BeNull();
        EndMeeting(app, s.Day);
    }

    [Fact]
    public async Task OpeningAnActionOverTheSlide_ClosesTheProductsCard_AndViceVersa()
    {
        var s = await Brief(71);
        int number;
        await using (var scope = SignedIn(app, SignIn.Analyst))
        {
            var team = (await scope.ServiceProvider.GetRequiredService<TeamService>().ListAsync())[0].Id;
            number = await scope.ServiceProvider.GetRequiredService<ActionService>()
                .QuickAddAsync(s.First, "Patch the gateway", ActionKind.PatchRemediate, team, null, s.Day);
        }
        await using var pat = await Open(app, s.Day);
        await Do(pat, p => p.StartMeeting());
        await FirstArticle(pat);
        await Do(pat, p => p.ShowProduct(s.Product));
        await Do(pat, p => p.ShowAction(number));
        (await pat.Html()).Should().Contain("present-action-title").And.NotContain("present-product-title");
        Meetings.Get(s.Day)!.Should().Match<LiveMeetingView>(v => v.Action == number && v.Product == null);

        await Do(pat, p => p.ShowProduct(s.Product));
        (await pat.Html()).Should().Contain("present-product-title").And.NotContain("present-action-title");
        Meetings.Get(s.Day)!.Should().Match<LiveMeetingView>(v => v.Action == null && v.Product == s.Product);
        EndMeeting(app, s.Day);

        // Opened before the meeting starts, it goes to the room.
        await using var robin = await Open(app, s.Day, Robin);
        await FirstArticle(robin);
        await Do(robin, p => p.ShowProduct(s.Product));
        await Do(robin, p => p.StartMeeting());
        Meetings.Get(s.Day)!.Product.Should().Be(s.Product);
        Meetings.End(Meetings.Join(s.Day, Robin.UserId, Robin.Name, true, "", Guid.NewGuid(), 0)!);
    }
}
