using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Abstractions;
using Tide.Application.Briefs;
using Tide.Application.Operations;
using Tide.Domain.Enums;
using Tide.Infrastructure.Persistence;
using Tide.Web.Components.Layout;
using Tide.Web.Components.Pages;
using Tide.Web.Components.Shared;
using Tide.Web.Services;
using static Tide.IntegrationTests.ComponentHarness;
using static Tide.IntegrationTests.PresentHarness;

namespace Tide.IntegrationTests;

/// <summary>
/// Help's guides: listed for the pages a person can use, started only when asked, and "Learn to present", which opens
/// present mode in rehearsal: no meeting, nobody following, nothing shared or saved, never while the day's meeting is
/// live, and never for a follower or a Visitor.
/// </summary>
public partial class GuidePagesTests(WeekdayApp app) : IClassFixture<WeekdayApp>
{
    private static SignIn Person(params string[] roles) => new($"dev:guide-{Guid.NewGuid():N}", "Guide tester", roles);

    private const string Rehearsing = "Rehearsal: nothing here is shared or saved";

    // Today's brief (a rehearsal is only ever of today's), with two more articles named for the test (`n`).
    private async Task<DateOnly> Day(int n)
    {
        var day = await TwoArticleBrief(app, 0, $"Guide {n}");
        await using var scope = SignedIn(app, SignIn.Analyst);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var brief = await db.Briefs.Include(b => b.Items).ThenInclude(i => i.Article).SingleAsync(b => b.BriefDate == day);
        foreach (var item in brief.Items) item.Article!.RecommendedActions = "- Patch the gateways\n- Ask the vendor for the fixed build";
        await db.SaveChangesAsync();
        return day;
    }

    [Fact]
    public async Task Help_ListsLearnToPresent_ForWhoeverCanPresent_AndNobodyElse()
    {
        await using (var r = await RenderAsync<HelpPage>(app, "/help", @as: Person("Analyst")))
        {
            var html = await r.Html();
            html.Should().Contain("id=\"help-guides\"").And.Contain("Learn to present").And.Contain("id=\"help-guide-present\"");
        }
        await using (var r = await RenderAsync<HelpPage>(app, "/help", @as: Person("Admin")))
            (await r.Html()).Should().Contain("Learn to present");
        foreach (var role in new[] { "Viewer", "ActionOwner" })
        {
            await using var r = await RenderAsync<HelpPage>(app, "/help", @as: Person(role));
            (await r.Html()).Should().NotContain("Learn to present", role).And.NotContain("rehearse", role);
        }
    }

    [Fact]
    public async Task AGuide_NeverStartsByItself_OnlyWhenAsked()
    {
        var who = Person("Analyst");
        await using var r = await RenderAsync<OnboardingHost>(app, "/", @as: who);
        (await r.Html()).Should().NotContain("tide-tour");
        var asked = r.Scope.ServiceProvider.GetRequiredService<TourRequests>();
        asked.AskGuide("no-such-guide");
        (await r.Html()).Should().NotContain("tide-tour", "a guide that isn't theirs, or isn't there, starts nothing");
    }

    [Fact]
    public async Task Rehearsal_ShowsTheGuide_AndSaysNothingIsSharedOrSaved()
    {
        var day = await Day(301);
        await using var r = await Rehearse(app, day, Person("Analyst"));
        var html = await r.Html();
        html.Should().Contain(Rehearsing).And.Contain("tide-tour").And.Contain("Step 1 of ").And.Contain("Learn to present");
        html.Should().NotContain("tide-notes-btn");
        // Start the meeting and Copy link are shown, for the guide to point at, and can't be used.
        html.Should().Contain("id=\"present-start\"").And.Contain("Starts the real meeting; not in a rehearsal").And.Contain("id=\"present-copy-link\"");
        html.Should().Contain("aria-modal=\"true\"").And.Contain("End guide");
    }

    [System.Text.RegularExpressions.GeneratedRegex("<span class=\"tide-tour-title\">(?<t>[^<]+)</span>")]
    private static partial System.Text.RegularExpressions.Regex StepTitle();

    // The guide's steps, by title, walked from the first to the last, and the page's markup at each.
    private static async Task<List<(string Title, string Html)>> Walk(Rendered<Present> r)
    {
        var seen = new List<(string, string)>();
        for (var i = 0; i < 40; i++)
        {
            var html = await r.Html();
            var title = System.Net.WebUtility.HtmlDecode(StepTitle().Match(html).Groups["t"].Value);
            if (title.Length == 0 || seen.Count > 0 && seen[^1].Item1 == title) break;
            seen.Add((title, html));
            await Do(r, p => p.GuideNext());
        }
        return seen;
    }

    [Fact]
    public async Task LearnToPresent_PointsWhosHereAndWhoAttendedAtTheirPlaces_AndLeavesOutStartAndEnd_OnceItsPresented()
    {
        var day = await Day(304);
        await using (var r = await Rehearse(app, day, Person("Analyst")))
        {
            var steps = await Walk(r);
            steps.Select(s => s.Title).Should().Contain("Going live").And.Contain("Ending the meeting");
            steps.Single(s => s.Title == "Who's here").Html.Should().Contain("<span class=\"tide-present-here\">Nobody here: a rehearsal</span>");
            steps.Single(s => s.Title == "Who attended").Html.Should().Contain("id=\"wrap-attendance\"").And.Contain("Attended (");
        }

        async Task AsAnalyst(Func<BriefService, Task> what)
        {
            await using var scope = SignedIn(app, SignIn.Analyst);
            await what(scope.ServiceProvider.GetRequiredService<BriefService>());
        }
        var brief = Guid.Empty;
        await AsAnalyst(async b => await b.MarkPresentedAsync(brief = (await b.GetAsync(day))!.Id));
        try
        {
            await using var r = await Rehearse(app, day, Person("Analyst"));
            var titles = (await Walk(r)).Select(s => s.Title).ToList();
            titles.Should().NotContain("Going live", "Start the meeting isn't drawn once it's presented").And.NotContain("Inviting the room").And.NotContain("Ending the meeting");
            titles.Should().Contain("Who attended");
        }
        finally { await AsAnalyst(b => b.ReopenAsync(brief)); }
    }

    [Fact]
    public async Task RehearsalsStart_IsShownDisabled_AndPressedAnyway_StartsNothing()
    {
        var day = await Day(303);
        await using var scope = SignedIn(app, SignIn.Analyst);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (audited, attendees) = (await db.AuditLog.CountAsync(), await db.MeetingAttendees.CountAsync());
        await using (var r = await Rehearse(app, day, Person("Analyst")))
        {
            var html = await r.Html();
            html.Should().MatchRegex("<button id=\"present-start\"[^>]*disabled", "shown where it always is, for the guide to ring");
            // A script taking `disabled` away reaches the same handler: it refuses in a rehearsal.
            await Do(r, p => p.StartMeeting());
        }
        app.Services.GetRequiredService<LiveMeetings>().Get(day).Should().BeNull("no meeting was started");
        db.ChangeTracker.Clear();
        (await db.AuditLog.CountAsync()).Should().Be(audited, "nothing was written");
        (await db.MeetingAttendees.CountAsync()).Should().Be(attendees);
    }

    [Fact]
    public async Task Rehearsal_WritesNothing_AndBroadcastsNothing()
    {
        var day = await Day(302);
        var feed = app.Services.GetRequiredService<ChangeFeed>();
        var heard = new List<Change>();
        void Heard(Change c) { lock (heard) heard.Add(c); }
        feed.Changed += Heard;
        try
        {
            await using var scope = SignedIn(app, SignIn.Analyst);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var actionsBefore = await db.Actions.CountAsync();
            var (notesBefore, attendeesBefore) = (await db.MeetingNotes.CountAsync(), await db.MeetingAttendees.CountAsync());

            await using (var r = await Rehearse(app, day, Person("Analyst")))
            {
                await r.Click(p => p.OnRailRoom(true));
                var brief = await db.Briefs.AsNoTracking().Include(b => b.Items).SingleAsync(b => b.BriefDate == day);
                var articleId = brief.Items.OrderBy(i => i.SortOrder).First().ArticleId;
                await Do(r, p => p.StartMeeting());
                await Do(r, p => p.Raise(articleId, "Patch the gateways"));
                await Do(r, p => { p.TypeQuickAddTitle(articleId, "Rehearsed action"); return p.QuickAdd(articleId); });
                await Do(r, p => p.OnPresentKey("next"));
                await Do(r, p => p.OnPresentKey("discussed"));
                await Do(r, p => { p.TypeNote("A rehearsed note"); return p.AddNote(); });
                await Do(r, p => p.OnPresentKey("last"));
                await Do(r, p => p.EndMeeting());
                await Do(r, p => p.EndFromTop());
                (await r.Html()).Should().Contain(Rehearsing, "it's still a rehearsal");
            }

            app.Services.GetRequiredService<LiveMeetings>().Get(day).Should().BeNull("no meeting was started");
            app.Services.GetRequiredService<Presence>().Others($"brief:{day:yyyy-MM-dd}", "dev:nobody").Should().BeEmpty("a rehearsal joins no one");
            db.ChangeTracker.Clear();
            var after = await db.Briefs.Include(b => b.Items).SingleAsync(b => b.BriefDate == day);
            after.Status.Should().NotBe(BriefStatus.Presented);
            after.PresentedAtUtc.Should().BeNull();
            after.Items.Should().OnlyContain(i => !i.Discussed);
            (await db.Actions.CountAsync()).Should().Be(actionsBefore, "nothing was raised");
            (await db.MeetingNotes.CountAsync()).Should().Be(notesBefore);
            (await db.MeetingAttendees.CountAsync()).Should().Be(attendeesBefore);
        }
        finally { feed.Changed -= Heard; }
        lock (heard) heard.Should().BeEmpty("a rehearsal tells nobody anything");
    }

    [Fact]
    public async Task Rehearsal_IsOnlyOfTodaysBrief_SoAnotherDayIsRefused_WhateverIsLive()
    {
        var past = await TwoArticleBrief(app, 3, "Guide past");
        await using (var r = await Rehearse(app, past, Person("Analyst")))
        {
            var html = await r.Html();
            html.Should().Contain("Only today&#x27;s brief can be rehearsed").And.NotContain("tide-tour").And.NotContain("present-rail").And.NotContain(Rehearsing);
        }
        var today = await Day(330);
        await using var presenter = await Open(app, today);
        await Do(presenter, p => p.StartMeeting());
        try
        {
            await using var r = await Rehearse(app, past, Person("Analyst"));
            (await r.Html()).Should().NotContain("tide-tour").And.NotContain(Rehearsing);
        }
        finally { EndMeeting(app, today); }
    }

    [Fact]
    public async Task Rehearsal_IsRefused_WhileThatDaysMeetingIsLive()
    {
        var day = await Day(303);
        await using var presenter = await Open(app, day);
        await Do(presenter, p => p.StartMeeting());
        try
        {
            var people = app.Services.GetRequiredService<LiveMeetings>().Get(day)!.People.Count;
            await using var r = await Rehearse(app, day, Person("Analyst"));
            var html = await r.Html();
            html.Should().Contain("The meeting is live").And.NotContain("tide-tour").And.NotContain("present-rail").And.NotContain(Rehearsing);
            app.Services.GetRequiredService<LiveMeetings>().Get(day)!.People.Count.Should().Be(people, "the refused page doesn't join the meeting");
        }
        finally { EndMeeting(app, day); }
    }

    [Fact]
    public async Task Rehearsal_EndsAndIsRefused_WhenTheMeetingStartsMeanwhile()
    {
        var day = await Day(304);
        await using var r = await Rehearse(app, day, Person("Analyst"));
        (await r.Html()).Should().Contain(Rehearsing);
        await using var presenter = await Open(app, day);
        await Do(presenter, p => p.StartMeeting());
        try
        {
            await Do(r, p => p.Follow());
            var html = await r.Html();
            html.Should().Contain("The meeting is live").And.NotContain("tide-tour").And.NotContain("present-rail");
        }
        finally { EndMeeting(app, day); }
    }

    [Fact]
    public async Task ARehearser_IsNeverSeated_CountedOrRecorded_WhenTheMeetingStartsMeanwhile()
    {
        var day = await Day(341);
        var avery = Person("Analyst");
        var meetings = app.Services.GetRequiredService<LiveMeetings>();
        await using var r = await Rehearse(app, day, avery);
        await r.Click(p => p.GoLive());   // the page is live in a browser
        await using var presenter = await Open(app, day);
        await Do(presenter, p => p.StartMeeting());
        try
        {
            // The rehearsal hears the meeting start, as a live page does, and is live again (a reconnect, say).
            await Do(r, p => p.Follow());
            await r.Click(p => p.GoLive());
            await Do(r, p => p.Follow());
            meetings.Get(day)!.People.Should().NotContain(x => x.UserId == avery.UserId, "a rehearsal never takes a seat, so followers never see it");
            app.Services.GetRequiredService<Presence>().Others($"brief:{day:yyyy-MM-dd}", "dev:nobody")
                .Should().NotContain(x => x.UserId == avery.UserId, "nor counts as here");
            await using var scope = SignedIn(app, SignIn.Analyst);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.MeetingAttendees.CountAsync(a => a.UserId == avery.UserId)).Should().Be(0, "nor is recorded as attending");
            (await db.AuditLog.CountAsync(a => a.UserId == avery.UserId)).Should().Be(0, "and saves nothing at all");
        }
        finally { EndMeeting(app, day); }
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("yes")]
    [InlineData("")]
    [InlineData("no")]
    public async Task AnyRehearseInTheAddress_IsARehearsal_SoItFailsSafe(string value)
    {
        var day = await Day(342);
        await using var r = await RenderAsync<Present>(app, $"/present/{day:yyyy-MM-dd}?rehearse={value}", new() { ["Day"] = $"{day:yyyy-MM-dd}" }, Person("Analyst"));
        (await r.Html()).Should().Contain(Rehearsing).And.Contain("aria-disabled=\"true\"").And.Contain("Starts the real meeting; not in a rehearsal");
    }

    [Fact]
    public async Task LeavingARehearsal_NeverAsksToDiscard_ForNothingCanBeSaved()
    {
        var day = await Day(343);
        await using (var r = await Rehearse(app, day, Person("Analyst")))
        {
            await r.Click(p => { p.TypeNote("Typed in a rehearsal"); return Task.CompletedTask; });
            r.Component.AsksBeforeLeaving.Should().BeFalse();
        }
        await using var live = await Open(app, day);
        await live.Click(p => { p.TypeNote("Typed for real"); return Task.CompletedTask; });
        live.Component.AsksBeforeLeaving.Should().BeTrue("a real note typed and not added is worth asking about");
    }

    [Theory]
    [InlineData("Viewer")]
    [InlineData("ActionOwner")]
    [InlineData("Visitor")]
    public async Task AFollowerOrAVisitor_NeverGetsTheRehearsalOrItsGuide(string role)
    {
        var day = await Day(role.Length + 310);
        await using (var r = await Rehearse(app, day, Person(role)))
        {
            var html = await r.Html();
            html.Should().NotContain("tide-tour").And.NotContain(Rehearsing).And.NotContain("present-rail");
        }
        await using var presenter = await Open(app, day);
        await Do(presenter, p => p.StartMeeting());
        try
        {
            await using var follower = await Rehearse(app, day, Person(role));
            var html = await follower.Html();
            html.Should().NotContain("tide-tour").And.NotContain(Rehearsing).And.NotContain("present-rail");
            (await presenter.Html()).Should().NotContain("tide-tour", "the presenter's own page, shared with the room, has no guide");
        }
        finally { EndMeeting(app, day); }
    }

    [Fact]
    public async Task ThePresentersOwnLivePage_NeverShowsTheGuide()
    {
        var day = await Day(320);
        await using var r = await Open(app, day);
        (await r.Html()).Should().NotContain("tide-tour").And.NotContain(Rehearsing);
    }

    [Fact]
    public async Task ShowMeAround_IsOnlyOnAPageWithAGuideForThePerson()
    {
        await using (var r = await RenderAsync<GuideLink>(app, "/help", @as: Person("Analyst")))
            (await r.Html()).Should().NotContain("Show me around", "Help has no guide of its own");
        await using (var r = await RenderAsync<GuideLink>(app, "/present/2026-11-12", @as: Person("Analyst")))
            (await r.Html()).Should().NotContain("Show me around", "nor does present mode");
    }

    // The host moves to a guide's page, which a page rendered outside a circuit can't (it throws).
    private static async Task Ask(Rendered<OnboardingHost> r, Action<TourRequests> what)
    {
        try { await r.Click(_ => { what(r.Scope.ServiceProvider.GetRequiredService<TourRequests>()); return Task.CompletedTask; }); }
        catch (NavigationException) { }
    }

    [Fact]
    public async Task LearnToPresent_AskedOfTheLayout_GoesToTheRehearsal_NotAStepOverThePage()
    {
        await using var r = await RenderAsync<OnboardingHost>(app, "/help", @as: Person("Analyst"));
        await Ask(r, t => t.AskGuide(Guides.Present, fromHelp: true));
        (await r.Html()).Should().NotContain("tide-tour", "its steps are present mode's, in rehearsal, never the layout's");
    }
}
