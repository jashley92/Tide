using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Abstractions;
using Tide.Application.Articles;
using Tide.Application.Briefs;
using Tide.Application.Org;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Web.Components.Pages;
using Tide.Web.Components.Shared;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>
/// The pages for reopening a meeting ("Back to ready" on the brief page, and present mode hearing it) and for finishing a
/// brief as "Not sent" from the Send dialog, read in the archive and on the brief page.
/// </summary>
public class BriefReopenPageTests(TideApp app) : IClassFixture<TideApp>
{
    private DateOnly Today => app.Services.GetRequiredService<OrgClock>().CurrentBriefDate;

    private static DateOnly Before(DateOnly day, int n)
    {
        for (var i = 0; i < n; i++) day = WorkingDays.Weekdays.Previous(day);
        return day;
    }

    // A past day's brief, presented, with one article discussed and one carried to the next brief.
    private async Task<(DateOnly Day, Guid Brief, DateOnly Next)> Presented(int daysBack, bool carry = true)
    {
        var day = Before(Today, daysBack);
        await using var scope = SignedIn(app, SignIn.Analyst);
        var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
        var briefs = scope.ServiceProvider.GetRequiredService<BriefService>();
        var brief = await briefs.GetOrStartAsync(day);
        var first = await articles.CreateAsync($"Reopen first {daysBack}", "Vendor", null, TlpLevel.Clear);
        var second = await articles.CreateAsync($"Reopen second {daysBack}", "Vendor", null, TlpLevel.Clear);
        await briefs.AddArticleAsync(brief.Id, first);
        await briefs.AddArticleAsync(brief.Id, second);
        var next = await briefs.MarkPresentedAsync(brief.Id, carry: carry ? [second] : null);
        return (day, brief.Id, next ?? day);
    }

    [Fact]
    public async Task BackToReady_SaysWhatComesBack_AndPutsTheBriefBackToReady()
    {
        var (day, brief, next) = await Presented(81);
        await using var r = await RenderAsync<BriefPage>(app, $"/briefs/{day:yyyy-MM-dd}", new() { ["Day"] = $"{day:yyyy-MM-dd}" });
        (await r.Html()).Should().Contain("bi-arrow-counterclockwise");

        Task? asking = null;
        await r.Renderer.Dispatcher.InvokeAsync(() => { asking = r.Pressed(p => p.AskReopen()); });
        var asked = await r.HtmlOnce("Back to ready?");
        asked.Should().Contain("The 1 article it carried to " + Ui.Day(next) + " comes off that brief and back to this one.");

        // Confirmed on the same sign-in the question was asked on (Click would sign in again, which ends the first's).
        await r.Renderer.Dispatcher.InvokeAsync(() => r.Component.ConfirmAsked());
        await r.Renderer.Dispatcher.InvokeAsync(() => asking!);
        await using var scope = SignedIn(app, SignIn.Analyst);
        var briefs = scope.ServiceProvider.GetRequiredService<BriefService>();
        r.Scope.ServiceProvider.GetRequiredService<Tide.Web.Services.ToastService>().Toasts.Select(t => t.Message).Should().Contain("Back to ready. 1 article came back from " + Ui.Day(next) + ".");
        (await briefs.GetAsync(day))!.Status.Should().Be(BriefStatus.Ready);
        (await briefs.GetAsync(next))!.Items.Should().BeEmpty("the carried article came back");

        await using var again = await RenderAsync<BriefPage>(app, $"/briefs/{day:yyyy-MM-dd}", new() { ["Day"] = $"{day:yyyy-MM-dd}" });
        (await again.Html()).Should().NotContain("bi-arrow-counterclockwise").And.Contain("meets at");
    }

    [Fact]
    public async Task BackToReady_WhenTheNextBriefMovedOn_SaysSo_AndChangesNothing()
    {
        var (day, brief, next) = await Presented(83);
        await using (var scope = SignedIn(app, SignIn.Analyst))
        {
            var briefs = scope.ServiceProvider.GetRequiredService<BriefService>();
            await briefs.MarkPresentedAsync((await briefs.GetAsync(next))!.Id);
        }
        await using var r = await RenderAsync<BriefPage>(app, $"/briefs/{day:yyyy-MM-dd}", new() { ["Day"] = $"{day:yyyy-MM-dd}" });
        await r.Click(p => p.AskReopen());
        (await r.Html()).Should().NotContain("Back to ready?", "refused before it's asked");
        await using var check = SignedIn(app, SignIn.Analyst);
        (await check.ServiceProvider.GetRequiredService<BriefService>().GetAsync(day))!.Status.Should().Be(BriefStatus.Presented);
    }

    [Fact]
    public async Task BackToReady_IsntOffered_ToSomeoneWhoCantPresent()
    {
        var (day, _, _) = await Presented(85, carry: false);
        await using var r = await RenderAsync<BriefPage>(app, $"/briefs/{day:yyyy-MM-dd}", new() { ["Day"] = $"{day:yyyy-MM-dd}" },
            new SignIn("dev:reopen-owner", "Olive Owner", "ActionOwner"));
        (await r.Html()).Should().NotContain("bi-arrow-counterclockwise");
    }

    [Fact]
    public async Task PresentMode_HearsTheMeetingReopened_AndOffersToStartItAgain()
    {
        var (day, brief, _) = await Presented(87, carry: false);
        await using var pat = await RenderAsync<Present>(app, $"/present/{day:yyyy-MM-dd}", new() { ["Day"] = $"{day:yyyy-MM-dd}" });
        (await pat.Html()).Should().NotContain("Start the meeting");

        await using (var scope = SignedIn(app, new SignIn("dev:reopen-robin", "Robin Analyst", "Analyst")))
            await scope.ServiceProvider.GetRequiredService<BriefService>().ReopenAsync(brief);
        await pat.Click(async p =>
        {
            await p.OnChangedElsewhere([new Change(ChangeFeed.Brief(brief), "dev:reopen-robin", "Robin Analyst", Guid.NewGuid())]);
            await p.OnPresentKey("render");
        });

        (await pat.Html()).Should().Contain("Start the meeting");
    }

    [Fact]
    public async Task NotSent_FromTheSendDialog_ShowsInTheArchive_AndOnTheBriefPage()
    {
        var (day, _, _) = await Presented(89, carry: false);
        Brief? read;
        await using (var scope = SignedIn(app, SignIn.Analyst))
            read = await scope.ServiceProvider.GetRequiredService<BriefService>().GetAsync(day);
        await using (var panel = await RenderAsync<DistributePanel>(app, "/", new() { ["Date"] = day, ["Brief"] = read }))
        {
            var html = await panel.Html();
            html.Should().Contain("Finish without sending").And.Contain("It won't be sent").And.Contain("send-not-sent-why");
            await panel.Click(d => { d.NotSentReason("Office closed for the storm"); return d.FinishNotSent(); });
        }

        await using (var scope = SignedIn(app, SignIn.Analyst))
            (await scope.ServiceProvider.GetRequiredService<BriefService>().GetAsync(day))!.NotSentReason.Should().Be("Office closed for the storm");

        await using (var archive = await RenderAsync<BriefsArchive>(app, $"/briefs?q={Ui.IsoDay(day)}"))
        {
            var html = await archive.Html();
            var row = html.IndexOf(Ui.Day(day), StringComparison.Ordinal);
            row.Should().BePositive();
            html[row..].Should().Contain("Not sent").And.Contain("Tester").And.Contain("Office closed for the storm");
        }

        await using var page = await RenderAsync<BriefPage>(app, $"/briefs/{day:yyyy-MM-dd}", new() { ["Day"] = $"{day:yyyy-MM-dd}" });
        var brief = await page.Html();
        brief.Should().Contain("This brief was finished without being sent").And.Contain("Why: Office closed for the storm")
            .And.NotContain("bi-arrow-counterclockwise", "a finished brief's meeting can't be reopened");
    }

    [Fact]
    public async Task NotSent_RefusedFromTheDialog_SaysWhy_BesideTheButton()
    {
        var day = Before(Today, 91);
        Brief? read;
        await using (var scope = SignedIn(app, SignIn.Analyst))
        {
            var briefs = scope.ServiceProvider.GetRequiredService<BriefService>();
            var b = await briefs.GetOrStartAsync(day);
            await briefs.AddArticleAsync(b.Id, await scope.ServiceProvider.GetRequiredService<ArticleService>().CreateAsync("Not ended", "Vendor", null, TlpLevel.Clear));
            read = await briefs.GetAsync(day);
        }
        await using var panel = await RenderAsync<DistributePanel>(app, "/", new() { ["Date"] = day, ["Brief"] = read, ["MeetingHeld"] = false });
        await panel.Click(d => { d.NotSentReason("No reason needed?"); return d.FinishNotSent(); });
        (await panel.Html()).Should().Contain("End the meeting first");
    }

    // Distributed while email was off, and email on since: it's finished, so there's nothing to finish without sending, and
    // no empty "Finish without sending" card is drawn.
    [Fact]
    public async Task NotSent_IsntOffered_ForABriefDistributedBeforeEmailWasOn()
    {
        var (day, _, _) = await Presented(93, carry: false);
        Brief? read;
        await using (var scope = SignedIn(app, SignIn.Analyst))
            read = await scope.ServiceProvider.GetRequiredService<BriefService>().GetAsync(day);
        read!.Status = BriefStatus.Sent;
        read.DistributedAtUtc = DateTimeOffset.UtcNow;
        read.DistributedByName = "Tester";
        await using var panel = await RenderAsync<DistributePanel>(app, "/", new() { ["Date"] = day, ["Brief"] = read });
        (await panel.Html()).Should().NotContain("Finish without sending");
    }
}
