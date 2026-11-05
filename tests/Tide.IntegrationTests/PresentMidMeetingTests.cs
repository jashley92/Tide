using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Abstractions;
using Tide.Application.Articles;
using Tide.Application.Briefs;
using Tide.Application.Org;
using Tide.Application.Stack;
using Tide.Domain.Enums;
using Tide.Web.Components.Pages;
using Tide.Web.Components.Shared;
using Tide.Web.Services;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>The app, with its live meetings on a clock a test moves on (a presenter's grace period, without the wait).</summary>
public sealed class MeetingClockApp : TideApp
{
    public TestClock Clock { get; } = new(DateTimeOffset.UtcNow);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(s => s.AddSingleton(_ => new LiveMeetings(Clock)));
    }
}

/// <summary>
/// A meeting while things change under it: the meeting handed on or taken over with a card open, the presenter's page
/// reloaded, a follower's page reading the brief again, the brief put back to ready as the meeting ends, and a product
/// edited on Our stack while its card is open.
/// </summary>
public class PresentMidMeetingTests(MeetingClockApp app) : IClassFixture<MeetingClockApp>
{
    private static readonly SignIn Robin = new("dev:midmeeting-robin", "Robin Analyst", "Analyst");
    private static readonly SignIn Owner = new("dev:midmeeting-owner", "Olive Owner", "ActionOwner");

    private DateOnly Today => app.Services.GetRequiredService<OrgClock>().CurrentBriefDate;
    private LiveMeetings Meetings => app.Services.GetRequiredService<LiveMeetings>();

    private sealed record Setup(DateOnly Day, Guid Brief, Guid First, Guid Product, string Name);

    // A past day's brief with two articles, the first tagged with a product we run.
    private async Task<Setup> Brief(int daysBack)
    {
        var day = Today;
        for (var i = 0; i < daysBack; i++) day = WorkingDays.Weekdays.Previous(day);
        await using var scope = SignedIn(app, SignIn.Analyst);
        var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
        var briefs = scope.ServiceProvider.GetRequiredService<BriefService>();
        var stack = scope.ServiceProvider.GetRequiredService<StackService>();
        var brief = await briefs.GetOrStartAsync(day);
        var first = await articles.CreateAsync($"Mid-meeting first {daysBack}", "Vendor", null, TlpLevel.Clear);
        var second = await articles.CreateAsync($"Mid-meeting second {daysBack}", "Vendor", null, TlpLevel.Clear);
        await briefs.AddArticleAsync(brief.Id, first);
        await briefs.AddArticleAsync(brief.Id, second);
        var name = $"Northwind Edge Router {daysBack}";
        var product = await stack.SaveAsync(new StackForm { Name = name, Versions = "4.2", Notes = "Two in the branch offices." });
        await stack.TagArticleAsync(first, product);
        return new Setup(day, brief.Id, first, product, name);
    }

    private Task<Rendered<Present>> Open(DateOnly day, SignIn? who = null) =>
        RenderAsync<Present>(app, $"/present/{day:yyyy-MM-dd}", new() { ["Day"] = $"{day:yyyy-MM-dd}" }, who);

    private static async Task FirstArticle(Rendered<Present> r)
    {
        await r.Click(p => p.OnPresentKey("last"));
        await r.Click(p => p.OnPresentKey("prev"));
        await r.Click(p => p.OnPresentKey("prev"));
    }

    private static Task Do(Rendered<Present> r, Func<Present, Task> what) =>
        r.Click(async p =>
        {
            await what(p);
            await p.OnPresentKey("render");
        });

    private static Task Do(Rendered<Present> r, Action<Present> what) => Do(r, p =>
    {
        what(p);
        return Task.CompletedTask;
    });

    private static Change Heard(string topic) => new(topic, Robin.UserId, Robin.Name, Guid.NewGuid());

    private void EndMeeting(DateOnly day)
    {
        if (Meetings.Get(day) is { } live) Meetings.End(Meetings.Join(day, live.PresenterId, "Someone", true, "", Guid.NewGuid(), 0)!);
    }

    [Fact]
    public async Task HandingOff_WithACardOpenUnderALock_ClosesItOnTheOldPresentersPage()
    {
        var s = await Brief(81);
        await using var pat = await Open(s.Day);
        await Do(pat, p => p.StartMeeting());
        await using var robin = await Open(s.Day, Robin);
        await Do(robin, p => p.JoinIfLive());
        await FirstArticle(pat);
        await Do(pat, p => p.ToggleLock());
        await Do(pat, p => p.ShowProduct(s.Product));
        await Do(robin, p => p.Follow());
        (await robin.Html()).Should().Contain("present-product-title");

        await Do(pat, p => p.HandOff(new ChangeEventArgs { Value = Robin.UserId }));
        Meetings.Get(s.Day)!.Should().Match<LiveMeetingView>(v => v.PresenterId == Robin.UserId && v.Product == null && v.Locked);
        (await pat.Html()).Should().NotContain("present-product-title", "the room's card closed with the hand-off, and a lock wouldn't let them close it")
            .And.Contain("locked by the presenter");
        await Do(robin, p => p.Follow());
        (await robin.Html()).Should().NotContain("present-product-title");
        EndMeeting(s.Day);
    }

    [Fact]
    public async Task TakingOver_ClosesTheCardTheTakerHadOpen_SoTheyDontThinkTheRoomSeesIt()
    {
        var s = await Brief(82);
        var pat = await Open(s.Day);
        await Do(pat, p => p.StartMeeting());
        await using var robin = await Open(s.Day, Robin);
        await Do(robin, p => p.JoinIfLive());
        await FirstArticle(pat);
        await Do(pat, p => p.ShowProduct(s.Product));
        await Do(robin, p => p.Follow());
        (await robin.Html()).Should().Contain("present-product-title").And.Contain("Tester is showing this");

        await pat.DisposeAsync();   // the presenter's tab closed
        app.Clock.Now += LiveMeetings.Grace + TimeSpan.FromSeconds(1);
        await Do(robin, p => p.Follow());
        await Do(robin, p => p.TakeOver());
        Meetings.Get(s.Day)!.Should().Match<LiveMeetingView>(v => v.PresenterId == Robin.UserId && v.Product == null);
        (await robin.Html()).Should().NotContain("present-product-title").And.NotContain("Shown to everyone following");
        EndMeeting(s.Day);
    }

    [Fact]
    public async Task ThePresentersReload_ShowsTheCardTheRoomSees_AndOneGoneSinceIsClosedForTheRoom()
    {
        var s = await Brief(83);
        var pat = await Open(s.Day);
        await Do(pat, p => p.StartMeeting());
        await FirstArticle(pat);
        await Do(pat, p => p.ShowProduct(s.Product));
        await pat.DisposeAsync();   // reloaded: the old page goes, the new one comes

        await using var again = await Open(s.Day);
        await Do(again, p => p.JoinIfLive());
        Meetings.Get(s.Day)!.Should().Match<LiveMeetingView>(v => v.PresenterId == "dev:tester" && v.Product == s.Product);
        (await again.Html()).Should().Contain("present-product-title").And.Contain("Shown to everyone following");
        await Do(again, p => p.CloseProduct());
        Meetings.Get(s.Day)!.Product.Should().BeNull("they can close it for the room");

        // The presenter's other tab shows a product this page can't read: it isn't left open for the room.
        using (var tab = Meetings.Join(s.Day, "dev:tester", "Tester", true, "", Guid.NewGuid(), Meetings.Get(s.Day)!.Slide)!)
        {
            Meetings.Show(tab, Guid.NewGuid()).Should().BeTrue();
            await Do(again, p => p.Follow());
            Meetings.Get(s.Day)!.Product.Should().BeNull();
        }
        EndMeeting(s.Day);
    }

    [Fact]
    public async Task AFollowerWhosePageCouldntNameThePresentersSlide_GoesToItOnceItReadsTheBrief()
    {
        var s = await Brief(84);
        await using var pat = await Open(s.Day);
        await Do(pat, p => p.StartMeeting());
        await using var owner = await Open(s.Day, Owner);
        await Do(owner, p => p.JoinIfLive());

        // A colleague adds a third article; the presenter's page reads it first, and they go to it at once.
        var third = "Mid-meeting third 84";
        await using (var scope = SignedIn(app, Robin))
        {
            var id = await scope.ServiceProvider.GetRequiredService<ArticleService>().CreateAsync(third, "Vendor", null, TlpLevel.Clear);
            await scope.ServiceProvider.GetRequiredService<BriefService>().AddArticleAsync(s.Brief, id);
        }
        await Do(pat, p => p.OnChangedElsewhere([Heard(ChangeFeed.Brief(s.Brief))]));
        await pat.Click(p => p.OnPresentKey("last"));
        await pat.Click(p => p.OnPresentKey("prev"));
        (await pat.Html()).Should().Contain($"<h2>{third}</h2>");

        // The follower hears the move before the brief: it lands where it can. Once it reads the brief, it's on theirs.
        await Do(owner, p => p.Follow());
        await Do(owner, p => p.OnChangedElsewhere([Heard(ChangeFeed.Brief(s.Brief))]));
        (await owner.Html()).Should().Contain($"<h2>{third}</h2>").And.Contain("Following Tester");

        // Browsing off (on the second article), a renumbering (the opening note added) moves where the presenter sees them.
        await owner.Click(p => p.OnPresentKey("prev"));
        var off = Meetings.Get(s.Day)!.People.Single(p => p.UserId == Owner.UserId).Slide;
        await using (var scope = SignedIn(app, Robin))
            await scope.ServiceProvider.GetRequiredService<BriefService>().SetTeamNoteAsync(s.Brief, "- Change freeze from Friday");
        await Do(owner, p => p.OnChangedElsewhere([Heard(ChangeFeed.Brief(s.Brief))]));
        (await owner.Html()).Should().Contain("<h2>Mid-meeting second 84</h2>").And.Contain($"aria-label=\"Slide {off + 2} of");
        Meetings.Get(s.Day)!.People.Single(p => p.UserId == Owner.UserId).Slide.Should().Be(off + 1, "the same article, a slide later");
        EndMeeting(s.Day);
    }

    [Fact]
    public async Task EndingTheMeeting_AsTheBriefIsPutBackToReady_EndsNothingUntilTheWrapUpIsSeenAgain()
    {
        var s = await Brief(85);
        await using var pat = await Open(s.Day);
        await Do(pat, p => p.StartMeeting());
        await using (var scope = SignedIn(app, SignIn.Analyst))
            await scope.ServiceProvider.GetRequiredService<BriefService>().MarkPresentedAsync(s.Brief);
        await pat.Click(p => p.OnPresentKey("last"));
        await Do(pat, p => p.OnChangedElsewhere([Heard(ChangeFeed.Brief(s.Brief))]));

        // Put back to ready on the brief page; present mode hasn't heard yet.
        await using (var scope = SignedIn(app, Robin))
            await scope.ServiceProvider.GetRequiredService<BriefService>().ReopenAsync(s.Brief);
        await Do(pat, p => p.EndMeeting());
        Meetings.Get(s.Day).Should().NotBeNull("it isn't ended as if the brief were still presented");
        pat.Scope.ServiceProvider.GetRequiredService<ToastService>().Toasts.Should().Contain(t => t.IsError && t.Message.Contains("put back to ready"));
        (await pat.Html()).Should().Contain("Not marked discussed", "the wrap-up as it is now");

        await Do(pat, p => p.EndMeeting());
        Meetings.Get(s.Day).Should().BeNull();
        await using (var scope = SignedIn(app, SignIn.Analyst))
            (await scope.ServiceProvider.GetRequiredService<BriefService>().GetAsync(s.Day))!.Status.Should().Be(BriefStatus.Presented);
    }

    [Fact]
    public async Task AProductEditedOrRetiredOnOurStack_ShowsOnItsOpenCardAndChip_Quietly()
    {
        var s = await Brief(86);
        await using var pat = await Open(s.Day);
        await Do(pat, p => p.StartMeeting());
        await FirstArticle(pat);
        await Do(pat, p => p.ShowProduct(s.Product));
        (await pat.Html()).Should().Contain("Two in the branch offices.");

        await using (var scope = SignedIn(app, Robin))
        {
            var stack = scope.ServiceProvider.GetRequiredService<StackService>();
            await stack.SaveAsync(new StackForm { Id = s.Product, Name = s.Name, Versions = "4.3", Notes = "Replaced in the north office." });
            await stack.SetRetiredAsync(s.Product, true);
        }
        await Do(pat, p => p.OnChangedElsewhere([Heard(ChangeFeed.Stack)]));
        var html = await pat.Html();
        html.Should().Contain("Replaced in the north office.").And.NotContain("Two in the branch offices.")
            .And.Contain(">Retired<").And.Contain($"We run {s.Name} 4.3 (retired)");
        pat.Scope.ServiceProvider.GetRequiredService<ToastService>().Toasts.Should().NotContain(t => t.Message.StartsWith("Robin"), "quietly");
        EndMeeting(s.Day);
    }
}
