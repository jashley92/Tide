using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Actions;
using Tide.Application.Articles;
using Tide.Application.Briefs;
using Tide.Application.Org;
using Tide.Domain.Enums;

using Tide.Web.Components.Pages;
using Tide.Web.Components.Shared;
using Tide.Web.Services;
using static Tide.IntegrationTests.ComponentHarness;
using static Tide.IntegrationTests.PresentHarness;

namespace Tide.IntegrationTests;

/// <summary>Present mode as a meeting several people join: start, join, follow, hands, notes, ending.</summary>
public class PresentLiveTests(TideApp app) : IClassFixture<TideApp>
{
    private static readonly SignIn Owner = new("dev:live-owner", "Olive Owner", "ActionOwner");
    private static readonly SignIn Robin = new("dev:live-robin", "Robin Analyst", "Analyst");
    private static readonly SignIn Reader = new("dev:live-reader", "Reese Reader", "Reader");

    // Read once: a test that straddles midnight mustn't see two days.
    private DateOnly Today { get; } = app.Services.GetRequiredService<OrgClock>().CurrentBriefDate;
    private LiveMeetings Meetings => app.Services.GetRequiredService<LiveMeetings>();

    private static DateOnly BusinessDaysBefore(DateOnly day, int n)
    {
        for (var i = 0; i < n; i++) day = WorkingDays.Weekdays.Previous(day);
        return day;
    }

    // A past day's brief with two articles.
    private async Task<(DateOnly Day, Guid Brief, Guid First)> Brief(int daysBack)
    {
        var day = BusinessDaysBefore(Today, daysBack);
        await using var scope = SignedIn(app, SignIn.Analyst);
        var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
        var briefs = scope.ServiceProvider.GetRequiredService<BriefService>();
        var brief = await briefs.GetOrStartAsync(day);
        var first = await articles.CreateAsync($"Live meeting first {daysBack}", "Vendor", null, TlpLevel.Clear);
        var second = await articles.CreateAsync($"Live meeting second {daysBack}", "Vendor", null, TlpLevel.Clear);
        await briefs.AddArticleAsync(brief.Id, first);
        await briefs.AddArticleAsync(brief.Id, second);
        return (day, brief.Id, first);
    }

    // The first article's slide: the one before the second article, which is the one before the wrap-up (however many
    // slides, the numbers and the open actions, come before the articles).
    private static async Task FirstArticle(Rendered<Present> r)
    {
        await r.Click(p => p.OnPresentKey("last"));
        await r.Click(p => p.OnPresentKey("prev"));
        await r.Click(p => p.OnPresentKey("prev"));
    }

    [Fact]
    public async Task SomeoneWhoCantPresent_SeesNothingUntilTheMeetingStarts_ThenFollowsThePresenter()
    {
        var (day, brief, first) = await Brief(61);
        await using var owner = await Open(app, day, Owner);
        (await owner.Html()).Should().Contain("No meeting going on").And.NotContain("Start the meeting");

        await using var pat = await Open(app, day);
        (await pat.Html()).Should().Contain("Start the meeting");
        await Do(pat, p => p.StartMeeting());
        (await pat.Html()).Should().Contain("Copy link").And.NotContain("Start the meeting");

        await Do(owner, p => p.JoinIfLive());
        var html = await owner.Html();
        PresentFollowPillTests.Shown(html).Should().Be("Following Tester");
        html.Should().Contain("Raise hand").And.NotContain("End the meeting").And.NotContain("Mark discussed");
        Meetings.Get(day)!.People.Select(p => p.Name).Should().Contain(["Tester", "Olive Owner"]);

        await FirstArticle(pat);
        await Do(owner, p => p.Follow());
        var slide = Meetings.Get(day)!.Slide;
        Meetings.Get(day)!.People.Single(p => p.Name == "Olive Owner").Slide.Should().Be(slide, "following the presenter");
        (await owner.Html()).Should().Contain($"aria-label=\"Slide {slide + 1} of");

        // Wandering off (to the second article) stops following; the presenter sees where they are and can bring them back.
        await owner.Click(p => p.OnPresentKey("next"));
        PresentFollowPillTests.Shown(await owner.Html()).Should().Be($"Tester is on slide {slide + 1} · Back to Tester");
        await Do(pat, p => p.Follow());
        (await pat.Html()).Should().Contain("Bring everyone here").And.Contain($"Olive Owner (on slide {slide + 2})");

        await Do(owner, p => p.ToggleHand());
        Meetings.Get(day)!.Hands.Single().Should().Match<RaisedHand>(h => h.Name == "Olive Owner" && h.ArticleId != null);
        await Do(pat, p => p.Follow());
        await Do(pat, p => p.ToggleHandsList());
        (await pat.Html()).Should().Contain("Lower Olive Owner&#x27;s hand", "the presenter lowers it from the list of hands");

        await using (var scope = SignedIn(app, SignIn.Analyst))
        {
            var record = await scope.ServiceProvider.GetRequiredService<MeetingService>().GetAsync(brief);
            record.Attendees.Select(a => a.Name).Should().BeEquivalentTo(["Tester", "Olive Owner"], "joining is recorded");
        }

        await Do(pat, p => p.EndMeeting());
        Meetings.Get(day).Should().BeNull();
        await Do(owner, p => p.Follow());
        (await owner.Html()).Should().Contain("<h2>That's the brief</h2>", "they land on the wrap-up").And.NotContain("No meeting going on");

        await using var scope2 = SignedIn(app, SignIn.Analyst);
        await using var check = await scope2.ServiceProvider.GetRequiredService<Tide.Application.Abstractions.IAppDbContextFactory>().CreateAsync();
        (await check.MeetingAttendees.Where(a => a.BriefId == brief).ToListAsync()).Should().HaveCount(2).And.OnlyContain(a => a.LeftAtUtc != null, "ending records when each left");
        (await check.Briefs.SingleAsync(b => b.Id == brief)).Status.Should().Be(BriefStatus.Presented);
        (await check.BriefItems.SingleAsync(i => i.ArticleId == first)).Discussed.Should().BeFalse();
    }

    [Fact]
    public async Task WhenThePresenterLocks_SomeoneFollowingCantMoveOff_UntilItsUnlocked()
    {
        var (day, _, _) = await Brief(65);
        await using var pat = await Open(app, day);
        await Do(pat, p => p.StartMeeting());
        await using var owner = await Open(app, day, Owner);
        await Do(owner, p => p.JoinIfLive());

        await Do(pat, p => p.ToggleLock());
        (await pat.Html()).Should().Contain("id=\"present-rail-lock\" class=\"btn btn-sm btn-primary\" aria-pressed=\"true\"");
        await Do(owner, p => p.Follow());
        PresentFollowPillTests.Heard(await owner.Html()).Should().Be("Following Tester · locked by the presenter");
        (await owner.Html()).Should().Contain("disabled aria-label=\"Next slide\"", "the slide buttons are off");

        await owner.Click(p => p.OnPresentKey("next"));
        Meetings.Get(day)!.People.Single(p => p.Name == "Olive Owner").Slide.Should().Be(0, "held on the presenter's slide");
        (await owner.Html()).Should().Contain("aria-label=\"Slide 1 of");

        await pat.Click(p => p.OnPresentKey("next"));
        await Do(owner, p => p.Follow());
        (await owner.Html()).Should().Contain("aria-label=\"Slide 2 of", "moved with the presenter");

        await Do(pat, p => p.ToggleLock());
        await Do(owner, p => p.Follow());
        await owner.Click(p => p.OnPresentKey("next"));
        (await owner.Html()).Should().Contain("aria-label=\"Slide 3 of", "unlocked: free to move").And.Contain("Back to Tester</button>");
        PresentFollowPillTests.Shown(await owner.Html()).Should().Be("Tester is on slide 2 · Back to Tester");

        EndMeeting(app, day);
    }

    [Fact]
    public async Task AnActionThePresenterOpens_ShowsForThoseFollowing_AndALockKeepsItOpen()
    {
        var (day, _, first) = await Brief(66);
        int number;
        await using (var scope = SignedIn(app, SignIn.Analyst))
        {
            var team = (await scope.ServiceProvider.GetRequiredService<TeamService>().ListAsync())[0].Id;
            number = await scope.ServiceProvider.GetRequiredService<ActionService>().QuickAddAsync(first, "Sweep the proxy logs", ActionKind.IocSweep, team, null, day);
        }
        var label = Ui.ActionLabel(number);
        await using var pat = await Open(app, day);
        await Do(pat, p => p.StartMeeting());
        await using var owner = await Open(app, day, Owner);
        await Do(owner, p => p.JoinIfLive());
        await FirstArticle(pat);
        await Do(owner, p => p.Follow());
        (await owner.Html()).Should().Contain($"class=\"tide-action-ref-btn \" aria-label=\"Open {label}, ", "the action's title opens it");

        await Do(pat, p => p.ShowAction(number));
        Meetings.Get(day)!.Action.Should().Be(number);
        (await pat.Html()).Should().Contain("present-action-title").And.Contain("Shown to everyone following").And.Contain("Comments (0)");
        await Do(owner, p => p.Follow());
        (await owner.Html()).Should().Contain("Tester is showing this").And.Contain("Sweep the proxy logs").And.Contain($"aria-label=\"Close {label}\"");

        // A follower closes it for themselves; the presenter still has it open, and it stays closed for them.
        await Do(owner, p => p.CloseAction());
        await Do(owner, p => p.Follow());
        (await owner.Html()).Should().NotContain("present-action-title");
        Meetings.Get(day)!.Action.Should().Be(number);

        // Locked: it shows, and only the presenter can close it.
        await Do(pat, p => p.ToggleLock());
        await Do(owner, p => p.Follow());
        (await owner.Html()).Should().Contain("present-action-title").And.Contain("showing this &#xB7; locked").And.NotContain($"aria-label=\"Close {label}\"");
        await Do(owner, p => p.CloseAction());
        (await owner.Html()).Should().Contain("present-action-title", "held open by the lock");

        // Moving on closes it for everyone.
        await pat.Click(p => p.OnPresentKey("next"));
        Meetings.Get(day)!.Action.Should().BeNull();
        await Do(owner, p => p.Follow());
        (await owner.Html()).Should().NotContain("present-action-title");

        // Unlocked, back on the article: a follower's own look is theirs alone.
        await Do(pat, p => p.ToggleLock());
        await pat.Click(p => p.OnPresentKey("prev"));
        await Do(owner, p => p.Follow());
        await Do(owner, p => p.ShowAction(number));
        (await owner.Html()).Should().Contain("Only you see this");
        Meetings.Get(day)!.Action.Should().BeNull();
        await Do(pat, p => p.Follow());
        (await pat.Html()).Should().NotContain("present-action-title");

        // The presenter closing theirs leaves a follower's own open; their opening one replaces it.
        await Do(pat, p => p.ShowAction(number));
        await Do(owner, p => p.Follow());
        (await owner.Html()).Should().Contain("Tester is showing this");
        await Do(pat, p => p.CloseAction());
        await Do(owner, p => p.Follow());
        (await owner.Html()).Should().NotContain("present-action-title");

        EndMeeting(app, day);
    }

    [Fact]
    public async Task AnActionOpenedAlone_GoesToTheRoomWhenTheMeetingStarts_AndNotOnTheCompanyWideEdition()
    {
        var (day, _, first) = await Brief(67);
        int number;
        await using (var scope = SignedIn(app, SignIn.Analyst))
        {
            var team = (await scope.ServiceProvider.GetRequiredService<TeamService>().ListAsync())[0].Id;
            number = await scope.ServiceProvider.GetRequiredService<ActionService>().QuickAddAsync(first, "Block the domain", ActionKind.Other, team, null, day);
        }
        await using var pat = await Open(app, day);
        await FirstArticle(pat);
        await Do(pat, p => p.ShowAction(number));
        (await pat.Html()).Should().Contain("present-action-title").And.Contain("Block the domain");
        await Do(pat, p => p.OnPresentKey("close"));
        (await pat.Html()).Should().NotContain("present-action-title", "Esc closes it");

        await Do(pat, p => p.ShowAction(number));
        await Do(pat, p => p.StartMeeting());
        Meetings.Get(day)!.Action.Should().Be(number, "what was open goes to the room");
        EndMeeting(app, day);

        // The company-wide edition never shows the team's actions (ADR 0005).
        await using var robin = await Open(app, day, Robin);
        await Do(robin, p => typeof(Present).GetMethod("SetEdition", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(p, [new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = nameof(Edition.Public) }]));
        await Do(robin, p => p.ShowAction(number));
        (await robin.Html()).Should().NotContain("present-action-title");
    }

    [Fact]
    public async Task ANote_IsTakenOnTheArticleOnScreen_AndShowsForEveryone()
    {
        var (day, brief, first) = await Brief(62);
        await using var pat = await Open(app, day);
        await Do(pat, p => p.StartMeeting());
        await using var owner = await Open(app, day, Owner);
        await Do(owner, p => p.JoinIfLive());
        await FirstArticle(pat);
        await Do(owner, p => p.Follow());

        await Do(owner, p =>
        {
            typeof(Present).GetField("_noteText", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(p, "We'll take the proxy sweep");
            return p.AddNote();
        });

        await using var scope = SignedIn(app, SignIn.Analyst);
        var note = (await scope.ServiceProvider.GetRequiredService<MeetingService>().GetAsync(brief)).Notes.Single();
        note.Should().Match<Tide.Domain.Entities.MeetingNote>(n => n.AuthorName == "Olive Owner" && n.ArticleId == first);

        await pat.Click(p => p.OnPresentKey("last"));
        (await pat.Html()).Should().Contain("Attended (2)").And.Contain("Someone in the room");
        EndMeeting(app, day);
    }

    [Fact]
    public async Task ANoteSomeoneElseAdds_ShowsAsNewOnTheNotesButton_UntilTheNotesAreOpened()
    {
        var (day, brief, _) = await Brief(66);
        await using var pat = await Open(app, day);
        await Do(pat, p => p.StartMeeting());
        await using var owner = await Open(app, day, Owner);
        await Do(owner, p => p.JoinIfLive());

        await Do(owner, p =>
        {
            typeof(Present).GetField("_noteText", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(p, "We'll take it");
            return p.AddNote();
        });
        (await owner.Html()).Should().NotContain("tide-notes-new", "your own note isn't news to you");

        await Do(pat, p => p.OnChangedElsewhere([new Tide.Application.Abstractions.Change(Tide.Application.Abstractions.ChangeFeed.Meeting(brief), "x", "Olive Owner", Guid.NewGuid())]));
        (await pat.Html()).Should().Contain("aria-label=\"Meeting notes, 1 new\"").And.Contain("class=\"tide-notes-new\"");

        await Do(pat, p => p.ToggleNotes());
        (await pat.Html()).Should().NotContain("tide-notes-new").And.Contain("We&#x27;ll take it");
        EndMeeting(app, day);
    }

    [Fact]
    public async Task AMeetingWhoseBriefWasMarkedPresentedElsewhere_CanStillBeEnded()
    {
        var (day, brief, _) = await Brief(67);
        await using var pat = await Open(app, day);
        await Do(pat, p => p.StartMeeting());
        await using (var scope = SignedIn(app, SignIn.Analyst))
            await scope.ServiceProvider.GetRequiredService<BriefService>().MarkPresentedAsync(brief);
        await pat.Click(p => p.OnPresentKey("last"));
        await Do(pat, p => p.OnChangedElsewhere([new Tide.Application.Abstractions.Change(Tide.Application.Abstractions.ChangeFeed.Brief(brief), "x", "Robin", Guid.NewGuid())]));
        (await pat.Html()).Should().Contain("End the meeting");

        await Do(pat, p => p.EndMeeting());
        Meetings.Get(day).Should().BeNull("the live meeting ends though the brief was already presented");
    }

    [Fact]
    public async Task AColleaguesActionSave_ShowsOnTheSlide_Quietly_AndADueDateBeingTypedStaysAsTyped()
    {
        var (day, brief, first) = await Brief(68);
        await using var pat = await Open(app, day);
        await FirstArticle(pat);
        var future = Today.AddDays(20);
        int number;
        Guid id;
        await using (var scope = SignedIn(app, SignIn.Analyst))
        {
            var actions = scope.ServiceProvider.GetRequiredService<ActionService>();
            var team = (await scope.ServiceProvider.GetRequiredService<TeamService>().ListAsync())[0].Id;
            number = await actions.QuickAddAsync(first, "Raised elsewhere", ActionKind.PatchRemediate, team, future, day);
            id = (await actions.GetAsync(number))!.Action.Id;
        }
        Tide.Application.Abstractions.Change Heard(string topic) => new(topic, "x", "Robin", Guid.NewGuid());

        await Do(pat, p => p.OnChangedElsewhere([Heard(Tide.Application.Abstractions.ChangeFeed.Action(id)), Heard(Tide.Application.Abstractions.ChangeFeed.Actions)]));
        (await pat.Html()).Should().Contain("Raised elsewhere").And.Contain($"value=\"{Ui.IsoDay(future)}\"");
        pat.Scope.ServiceProvider.GetRequiredService<ToastService>().Toasts.Should().NotContain(t => t.Message.StartsWith("Robin"), "an action save is quiet");

        // A date typed in the slide's box, not yet saved, outlasts a colleague's move of it.
        var typed = (Dictionary<Guid, string?>)typeof(Present).GetField("_dueTyped", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(pat.Component)!;
        await Do(pat, _ => { typed[id] = Ui.IsoDay(future.AddDays(5)); });
        await using (var scope = SignedIn(app, SignIn.Analyst))
            await scope.ServiceProvider.GetRequiredService<ActionService>().SetDueAsync(id, future.AddDays(1));
        await Do(pat, p => p.OnChangedElsewhere([Heard(Tide.Application.Abstractions.ChangeFeed.Actions)]));
        var html = await pat.Html();
        html.Should().Contain($"value=\"{Ui.IsoDay(future.AddDays(5))}\"").And.Contain("due " + Ui.ShortDay(future.AddDays(1)), "the line says what's saved");

        // A note and an action heard together: the record and the lists, still without a toast.
        await Do(pat, p => p.OnChangedElsewhere([Heard(Tide.Application.Abstractions.ChangeFeed.Actions), Heard(Tide.Application.Abstractions.ChangeFeed.Meeting(brief))]));
        pat.Scope.ServiceProvider.GetRequiredService<ToastService>().Toasts.Should().NotContain(t => t.Message.StartsWith("Robin"));

    }

    [Fact]
    public async Task AReader_CantOpenPresentMode()
    {
        var (day, _, _) = await Brief(63);
        var act = () => Open(app, day, Reader);
        // The page needs the team edition; a reader's render is refused by the brief service as it loads.
        await using var r = await act();
        (await r.Html()).Should().NotContain("Start the meeting").And.NotContain("Raise hand");
    }

    [Fact]
    public async Task AnotherAnalyst_JoinsAsSomeoneWhoCouldTakeOver_AndTheBriefPageOffersJoin()
    {
        var (day, _, _) = await Brief(64);
        await using var pat = await Open(app, day);
        await Do(pat, p => p.StartMeeting());
        await using var robin = await Open(app, day, Robin);
        await Do(robin, p => p.JoinIfLive());

        Meetings.Get(day)!.People.Single(p => p.Name == "Robin Analyst").CanPresent.Should().BeTrue();
        await Do(pat, p => p.Follow());
        (await pat.Html()).Should().Contain("Hand off to");

        var page = await app.CreateClient().GetStringAsync(Ui.BriefUrl(day));
        page.Should().Contain("Join meeting").And.Contain("Brief meeting is live");

        EndMeeting(app, day);
    }
}
