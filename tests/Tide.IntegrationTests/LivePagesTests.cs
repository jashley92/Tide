using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Articles;
using Tide.Application.Rotation;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Web.Components.Pages;
using Tide.Web.Services;

namespace Tide.IntegrationTests;

/// <summary>
/// The rotation and the articles list follow a colleague's saves, as the brief and the actions board do: each page is
/// rendered in its own scope (its own circuit), and the colleague saves from another.
/// </summary>
public class LivePagesTests(TideApp app) : IClassFixture<TideApp>
{
    private static readonly ComponentHarness.SignIn Robin = new("dev:live-robin", "Robin", "Analyst");
    private static readonly ComponentHarness.SignIn Sam = new("dev:live-sam", "Sam", "Analyst");

    // What the page's circuit has been told in a toast, once it has (the reload waits for a burst to settle).
    private static async Task<IReadOnlyList<string>> ToastsOnce<T>(ComponentHarness.Rendered<T> page, string text)
    {
        var toasts = page.Scope.ServiceProvider.GetRequiredService<ToastService>();
        for (var i = 0; i < 50 && !toasts.Toasts.Any(t => t.Message == text); i++) await Task.Delay(100);
        return toasts.Toasts.Select(t => t.Message).ToList();
    }

    [Fact]
    public async Task TheRotation_ShowsARequestToThePartner_AndItsApprovalToTheRequester_WithoutAReload()
    {
        DateOnly week;
        await using (var admin = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst))
        {
            var rotation = admin.ServiceProvider.GetRequiredService<RotationService>();
            await rotation.AddMemberAsync(Robin.UserId, Robin.Name);
            await rotation.AddMemberAsync(Sam.UserId, Sam.Name);
            week = RotationService.WeekStart(rotation.Today).AddDays(14);
            await rotation.SetWeekAsync(week, Robin.UserId);
        }
        await using var sams = await ComponentHarness.RenderAsync<RotationPage>(app, "/rotation", null, Sam);
        await using var robins = await ComponentHarness.RenderAsync<RotationPage>(app, "/rotation", null, Robin);
        (await sams.Html()).Should().NotContain("Waiting for you");

        RotationChange change;
        await using (var robin = ComponentHarness.SignedIn(app, Robin))
        {
            change = await robin.ServiceProvider.GetRequiredService<RotationService>()
                .RequestAsync(new RotationRequest { PartnerId = Sam.UserId, GiveFrom = week, GiveTo = week });
        }

        (await sams.HtmlOnce("Waiting for you")).Should().Contain("Waiting for you").And.Contain(">Approve</button>");
        (await ToastsOnce(sams, "Robin sent you a swap or cover request.")).Should().Contain("Robin sent you a swap or cover request.");
        (await robins.HtmlOnce("Waiting for Sam")).Should().Contain("Waiting for Sam", "Robin's other tab shows the request too");

        await using (var sam = ComponentHarness.SignedIn(app, Sam))
            await sam.ServiceProvider.GetRequiredService<RotationService>().ApproveAsync(change.Id);

        (await ToastsOnce(robins, "Sam approved your request.")).Should().Contain("Sam approved your request.");
        (await robins.Html()).Should().NotContain("Waiting for Sam");
    }

    [Fact]
    public async Task ApprovingOnTheRotationPage_SaysSo_WithoutPromisingTheManagerWasTold()
    {
        var giver = new ComponentHarness.SignIn("dev:toast-giver", "Giver", "Analyst");
        var taker = new ComponentHarness.SignIn("dev:toast-taker", "Taker", "Analyst");
        DateOnly day;
        await using (var admin = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst))
        {
            var rotation = admin.ServiceProvider.GetRequiredService<RotationService>();
            await rotation.AddMemberAsync(giver.UserId, giver.Name);
            await rotation.AddMemberAsync(taker.UserId, taker.Name);
            day = RotationService.WeekStart(rotation.Today).AddDays(35);
            await rotation.SetWeekAsync(day, giver.UserId);
        }
        RotationChange change;
        await using (var g = ComponentHarness.SignedIn(app, giver))
            change = await g.ServiceProvider.GetRequiredService<RotationService>().RequestAsync(new RotationRequest { PartnerId = taker.UserId, GiveFrom = day, GiveTo = day });

        await using var page = await ComponentHarness.RenderAsync<RotationPage>(app, "/rotation", null, taker);
        await page.Click(c => c.Decide(change.Id, approve: true));

        var toasts = page.Scope.ServiceProvider.GetRequiredService<ToastService>().Toasts.Select(t => t.Message).ToList();
        toasts.Should().Contain("Approved: the rotation is updated.").And.NotContain(t => t.Contains("manager"));
    }

    [Fact]
    public async Task TheArticlesList_ShowsAColleaguesNewArticle_KeepingItsFilter()
    {
        await using var list = await ComponentHarness.RenderAsync<Articles>(app, "/articles?q=Zephyrlive");
        (await list.Html()).Should().NotContain("Zephyrlive outage");

        await using (var robin = ComponentHarness.SignedIn(app, Robin))
        {
            var articles = robin.ServiceProvider.GetRequiredService<ArticleService>();
            await articles.CreateAsync("Zephyrlive outage", "Vendor", null, TlpLevel.Clear);
            await articles.CreateAsync("Live list: another article", "Vendor", null, TlpLevel.Clear);
        }

        var html = await list.HtmlOnce("Zephyrlive outage");
        html.Should().Contain("Zephyrlive outage").And.NotContain("Live list: another article", "the list is still the search's");
        (await ToastsOnce(list, "Robin updated the articles.")).Should().Contain("Robin updated the articles.");
    }
}

public class RotationHeardTests
{
    private static RotationChange Change(RotationChangeStatus status = RotationChangeStatus.Pending) => new() { Status = status };

    private static Upcoming With(IReadOnlyList<RotationChange>? toApprove = null, IReadOnlyList<RotationChange>? mine = null) =>
        new([], toApprove ?? [], mine ?? []);

    [Fact]
    public void ARequestWaitingForThem_IsSaid_OnceEach()
    {
        var old = Change();
        RotationPage.Heard(With([old]), With([old, Change()]), [], "Robin").Should().Be("Robin sent you a swap or cover request.");
        RotationPage.Heard(null, With([Change(), Change()]), [], "Robin").Should().Be("Robin sent you 2 swap or cover requests.");
        RotationPage.Heard(With([old]), With([old]), [], "Robin").Should().BeNull("nothing new is waiting");
    }

    [Theory]
    [InlineData(RotationChangeStatus.Approved, "Sam approved your request.")]
    [InlineData(RotationChangeStatus.Declined, "Sam declined your request.")]
    [InlineData(RotationChangeStatus.Cancelled, "Sam answered your request.")]
    public void AnAnswerToTheirRequest_IsSaid(RotationChangeStatus status, string said)
    {
        var mine = Change();
        var decided = new RotationChange { Id = mine.Id, Status = status };

        RotationPage.Heard(With(mine: [mine]), With(), [decided], "Sam").Should().Be(said);
        RotationPage.Heard(With(mine: [mine]), With(), [], "Sam").Should().Be("Sam answered your request.", "Today doesn't read the changes");
    }
}
