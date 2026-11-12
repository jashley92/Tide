using System.Text.RegularExpressions;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Articles;
using Tide.Application.Briefs;
using Tide.Application.Metrics;
using Tide.Application.Org;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Infrastructure.Persistence;
using Tide.Web.Components.Pages;
using Tide.Web.Components.Pages.Admin;
using static Tide.IntegrationTests.ComponentHarness;
using static Tide.IntegrationTests.PresentHarness;

namespace Tide.IntegrationTests;

/// <summary>
/// The slides say where the meeting is and what's coming, and the presenter deals with the day's loose ends from them:
/// the footer names each slide; the title slide lists what comes before the articles; an opening note carried from the
/// last brief, and a number not yet entered, are dealt with on their slides by the presenter, and by no one else. Its
/// own app, since metrics are global.
/// </summary>
public class PresentSlidesTests(TideApp app) : IClassFixture<TideApp>
{
    private static readonly SignIn Owner = new("dev:slides-owner", "Olive Owner", "ActionOwner");
    private static readonly SignIn Robin = new("dev:slides-robin", "Robin Analyst", "Analyst");

    private DateOnly Today { get; } = app.Services.GetRequiredService<OrgClock>().CurrentBriefDate;

    private static readonly SemaphoreSlim Once = new(1, 1);
    private static (Guid Entered, Guid Missing)? _metrics;

    // One group of two numbers (made once, for every day): "Open cases", entered, and "Phish reported", left to enter.
    private async Task<(Guid Entered, Guid Missing)> Metrics()
    {
        await Once.WaitAsync();
        try
        {
            if (_metrics is { } done) return done;
            await using var scope = SignedIn(app, SignIn.Analyst);
            var svc = scope.ServiceProvider.GetRequiredService<MetricsService>();
            var g = await svc.AddGroupAsync("Slide numbers");
            var entered = await svc.SaveMetricAsync(new MetricForm { GroupId = g, Name = "Open cases", Direction = MetricDirection.LowerIsBetter });
            var missing = await svc.SaveMetricAsync(new MetricForm { GroupId = g, Name = "Phish reported", Direction = MetricDirection.LowerIsBetter });
            _metrics = (entered, missing);
            return _metrics.Value;
        }
        finally { Once.Release(); }
    }

    // A past day's brief with two articles, one number entered, and (when asked) an opening note carried from the last
    // brief and not yet looked at.
    private async Task<(DateOnly Day, Guid Brief, Guid Missing)> Day(int daysBack, bool carriedNote = true)
    {
        var (entered, missing) = await Metrics();
        var day = await TwoArticleBrief(app, daysBack, "Slides");
        await using var scope = SignedIn(app, SignIn.Analyst);
        await scope.ServiceProvider.GetRequiredService<MetricsService>().SaveValueAsync(entered, day, 7, null);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var brief = await db.Briefs.SingleAsync(b => b.BriefDate == day);
        if (carriedNote)
        {
            brief.TeamNote = "Payroll lures are up again.";
            brief.TeamNoteReviewedAtUtc = null;
            await db.SaveChangesAsync();
        }
        return (day, brief.Id, missing);
    }

    private static string Footer(string html)
    {
        var at = html.IndexOf("<footer class=\"tide-present-bottom\"", StringComparison.Ordinal);
        return html[at..html.IndexOf("</footer>", at, StringComparison.Ordinal)];
    }

    /// <summary>What only the presenter has on a slide.</summary>
    private const string PresenterTools = "tide-slide-presenter";

    [Fact]
    public async Task TheFooterNamesEachSlide_AndTheTitleSlideSaysWhatComesFirst()
    {
        var (day, _, _) = await Day(191);
        await using var pat = await Open(app, day);
        var html = await pat.Html();
        var footer = Footer(html);
        foreach (var name in new[] { "Title", "Opening note", "Slide numbers", "1 · Slides first", "2 · Slides second", "Wrap-up" })
            footer.Should().Contain($"<span class=\"tide-progress-name\">{name.Replace("·", "&#xB7;")}</span>");
        footer.IndexOf(">Title<", StringComparison.Ordinal).Should().BeLessThan(footer.IndexOf(">Opening note<", StringComparison.Ordinal));
        footer.Should().Contain("aria-label=\"1. Slides first 191\"", "the short name's whole title is read out");
        html.Should().Contain("class=\"tide-agenda-first\"").And.Contain("First: the opening note and the numbers.");
    }

    [Fact]
    public async Task ACarriedOpeningNote_TheirsToKeepOrEdit_OnItsSlide_AndNotAFollowers()
    {
        var (day, brief, _) = await Day(192);
        await using var pat = await Open(app, day);
        await Do(pat, p => p.StartMeeting());
        await pat.Click(p => p.OnPresentKey("next"));
        var html = await pat.Html();
        html.Should().Contain("Payroll lures are up again.").And.Contain(PresenterTools).And.Contain("Not updated")
            .And.Contain(">Still current</button>").And.Contain(">Edit</button>");

        await using var owner = await Open(app, day, Owner);
        await Do(owner, p => p.JoinIfLive());
        await using var robin = await Open(app, day, Robin);
        await Do(robin, p => p.JoinIfLive());
        foreach (var follower in new[] { owner, robin })
        {
            await Do(follower, p => p.Follow());
            var theirs = await follower.Html();
            theirs.Should().Contain("Payroll lures are up again.").And.NotContain(PresenterTools, "the presenter's alone, even for someone who could edit it")
                .And.NotContain("Still current").And.NotContain("Not updated");
        }

        // Still current: kept as it is, and no longer flagged.
        await Do(pat, p => p.KeepOpeningNote());
        await using (var scope = SignedIn(app, SignIn.Analyst))
            BriefService.TeamNoteCarriedOver((await scope.ServiceProvider.GetRequiredService<AppDbContext>().Briefs.AsNoTracking().SingleAsync(b => b.Id == brief))).Should().BeFalse();
        (await pat.Html()).Should().NotContain("Not updated").And.NotContain(PresenterTools, "nothing left to deal with");

        EndMeeting(app, day);
    }

    [Fact]
    public async Task TheOpeningNote_EditedOnItsSlide_IsSaved_AndTheRoomSeesIt()
    {
        var (day, brief, _) = await Day(193);
        await using var pat = await Open(app, day);
        await pat.Click(p => p.OnPresentKey("next"));
        await Do(pat, p => p.EditOpeningNote());
        (await pat.Html()).Should().Contain("id=\"slide-note-text\"").And.Contain("Payroll lures are up again.");
        await Do(pat, p => p.SaveOpeningNote("Payroll lures are down this week."));
        await using (var scope = SignedIn(app, SignIn.Analyst))
        {
            var saved = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Briefs.AsNoTracking().SingleAsync(b => b.Id == brief);
            saved.TeamNote.Should().Be("Payroll lures are down this week.");
            BriefService.TeamNoteCarriedOver(saved).Should().BeFalse();
        }
        (await pat.Html()).Should().Contain("Payroll lures are down this week.").And.NotContain("id=\"slide-note-text\"");

        // An empty note isn't saved from the slide: it would take the slide away mid-meeting (Still current or Cancel).
        await Do(pat, p => p.EditOpeningNote());
        (await pat.Html()).Should().Contain("id=\"slide-note-text\"");
        await Do(pat, p => p.SaveOpeningNote("  "));
        await using (var scope = SignedIn(app, SignIn.Analyst))
            (await scope.ServiceProvider.GetRequiredService<AppDbContext>().Briefs.AsNoTracking().SingleAsync(b => b.Id == brief)).TeamNote.Should().Be("Payroll lures are down this week.");
        var html = await pat.Html();
        html.Should().Contain("id=\"slide-note-text\"", "still editing").And.Contain("aria-label=\"Slide 2 of", "on the note's slide still");
    }

    [Fact]
    public async Task AMissingNumber_EnteredOnItsSlideByThePresenter_TheRoomSeesADashUntilThen()
    {
        var (day, _, missing) = await Day(194, carriedNote: false);
        await using var pat = await Open(app, day);
        await Do(pat, p => p.StartMeeting());
        await pat.Click(p => p.OnPresentKey("next"));
        var html = await pat.Html();
        html.Should().Contain("Slide numbers").And.Contain(PresenterTools).And.Contain("1 number missing")
            .And.Contain("aria-label=\"Phish reported for").And.NotContain("aria-label=\"Open cases for", "only the missing one");

        await using var owner = await Open(app, day, Owner);
        await Do(owner, p => p.JoinIfLive());
        await Do(owner, p => p.Follow());
        (await owner.Html()).Should().Contain("Slide numbers").And.NotContain(PresenterTools).And.NotContain("number missing");

        html = await pat.Html();
        html.Should().Contain("not entered", "the room's tile says so under its dash");
        await Do(pat, p => p.EnterMissingNumber(missing, "not a number"));
        html = await pat.Html();
        html.Should().Contain("1 number missing", "nothing was saved").And.Contain("aria-invalid=\"true\"")
            .And.Contain($"aria-describedby=\"slide-number-error-{missing:N}\"").And.Contain($"id=\"slide-number-error-{missing:N}\"");
        await Do(pat, p => p.EnterMissingNumber(missing, "12"));
        await using (var scope = SignedIn(app, SignIn.Analyst))
            (await scope.ServiceProvider.GetRequiredService<MetricsService>().GetDayAsync(day)).Missing.Should().NotContain(m => m.Id == missing);
        html = await pat.Html();
        html.Should().NotContain(PresenterTools).And.NotContain("number missing");
        Regex.IsMatch(html, ">12<").Should().BeTrue("the slide shows it");

        EndMeeting(app, day);
    }
}
