using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Briefs;
using Tide.Application.History;
using Tide.Application.Rotation;
using Tide.Application.Security;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Web.Services;

namespace Tide.IntegrationTests;

/// <summary>
/// The owner's decisions on briefs: no "Not sent" while a send is going, the API saying how a brief was finished, no raw
/// user ids in History, no Back to ready mid-meeting, and a held meeting's presenter kept when the rota moves.
/// </summary>
public class OwnerDecisionsBriefsTests : IDisposable
{
    private readonly TestDb _db = new();
    private static readonly DateOnly Day = new(2026, 10, 26);
    private const string Why = "The relay is down for the day.";

    public OwnerDecisionsBriefsTests()
    {
        _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst };
        _db.Email.Enabled = true;
    }

    public void Dispose() => _db.Dispose();

    private async Task<Guid> Presented()
    {
        var id = await _db.Articles().CreateAsync("Decision story", "Vendor", null, TlpLevel.Clear);
        var brief = await _db.Briefs().GetOrStartAsync(Day);
        await _db.Briefs().AddArticleAsync(brief.Id, id);
        await _db.Briefs().MarkPresentedAsync(brief.Id);
        return brief.Id;
    }

    private static BriefSend Send(Guid brief, SendStatus status, bool test = false) => new()
    {
        BriefId = brief, BriefDate = Day, Edition = Edition.Team, Status = status, IsTest = test, Ceiling = TlpLevel.Green, Marking = TlpLevel.Green,
        Subject = "Daily brief", Recipients = "soc@example.org", RecipientCount = 1, SentAtUtc = DateTimeOffset.UtcNow,
        SentById = "x", SentByName = "X", EmailHtml = "<p>brief</p>", WebHtml = "<p>brief</p>",
    };

    private async Task<Brief> Read()
    {
        await using var ctx = _db.Context();
        return await ctx.Briefs.AsNoTracking().SingleAsync(b => b.BriefDate == Day);
    }

    // --- 1. Finish as Not sent waits for a send going, as Back to ready does -----------------------------------------

    [Theory]
    [InlineData(SendStatus.Sending)]
    [InlineData(SendStatus.Unknown)]
    public async Task NotSent_IsRefused_WhileARealSendIsGoing_OrItsOutcomeIsUnknown(SendStatus status)
    {
        var brief = await Presented();
        await using (var ctx = _db.Context())
        {
            ctx.BriefSends.Add(Send(brief, status));
            ctx.BriefSends.Add(Send(brief, SendStatus.Sending, test: true));
            await ctx.SaveChangesAsync();
        }

        var finish = () => _db.Distribution().FinishNotSentAsync(Day, Why);
        await finish.Should().ThrowAsync<InvalidOperationException>().WithMessage(Tide.Application.Distribution.DistributionService.NotSentWhileSending);
        (await Read()).Should().Match<Brief>(b => b.Status == BriefStatus.Presented && b.NotSentAtUtc == null);

        await using (var ctx = _db.Context())
        {
            (await ctx.BriefSends.SingleAsync(s => !s.IsTest)).Status = SendStatus.Failed;
            await ctx.SaveChangesAsync();
        }
        await _db.Distribution().FinishNotSentAsync(Day, Why);
        (await Read()).NotSentReason.Should().Be(Why, "a failed send didn't go, and a test copy never holds it");
    }

    [Fact]
    public async Task NotSent_RacingASendThatStarts_IsRefused_SayingSo()
    {
        var brief = await Presented();
        // A send freezes between the finish's check and its save: it adds its row and moves the brief's edit version.
        _db.BeforeSave = _ =>
        {
            _db.BeforeSave = null;
            using var ctx = _db.PlainContext();
            ctx.BriefSends.Add(Send(brief, SendStatus.Sending));
            ctx.SaveChanges();
            ctx.Briefs.Where(b => b.Id == brief).ExecuteUpdate(u => u.SetProperty(b => b.EditVersion, Guid.NewGuid()));
        };

        var finish = () => _db.Distribution().FinishNotSentAsync(Day, Why);
        await finish.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Check the send log first.");
        (await Read()).NotSentAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task NotSent_RacingAnotherEdit_SaysSomeoneElseChangedIt()
    {
        var brief = await Presented();
        _db.BeforeSave = _ =>
        {
            _db.BeforeSave = null;
            using var ctx = _db.PlainContext();
            ctx.Briefs.Where(b => b.Id == brief).ExecuteUpdate(u => u.SetProperty(b => b.EditVersion, Guid.NewGuid()));
        };
        var finish = () => _db.Distribution().FinishNotSentAsync(Day, Why);
        await finish.Should().ThrowAsync<InvalidOperationException>().WithMessage("Someone else changed the brief*");
    }

    // --- 2. The API says how a Sent brief was finished ---------------------------------------------------------------

    [Fact]
    public async Task Api_SaysHowTheBriefWasFinished_NotSent_ThenEmailed()
    {
        var brief = await Presented();
        (await _db.Api().BriefAsync(Day, null))!.FinishedAs.Should().BeNull("it isn't finished");

        await _db.Distribution().FinishNotSentAsync(Day, Why);
        var api = (await _db.Api().BriefAsync(Day, null))!;
        api.Status.Should().Be("Sent");
        api.FinishedAs.Should().Be("notSent");

        await using (var ctx = _db.Context())
        {
            ctx.BriefSends.Add(Send(brief, SendStatus.Sent, test: true));
            await ctx.SaveChangesAsync();
        }
        (await _db.Api().BriefAsync(Day, null))!.FinishedAs.Should().Be("notSent", "a test copy isn't the brief going out");

        await using (var ctx = _db.Context())
        {
            ctx.BriefSends.Add(Send(brief, SendStatus.Sent));
            await ctx.SaveChangesAsync();
        }
        (await _db.Api().BriefAsync(Day, null))!.FinishedAs.Should().Be("emailed", "emailed after all, it's an ordinary sent brief");
    }

    [Fact]
    public async Task Api_SaysDistributed_ForABriefMarkedDistributedWithEmailOff()
    {
        await Presented();
        _db.Email.Enabled = false;
        await _db.Distribution().MarkDistributedAsync(Day);
        (await _db.Api().BriefAsync(Day, null))!.Should().Match<Tide.Application.Api.ApiBrief>(b => b.Status == "Sent" && b.FinishedAs == "distributed");
    }

    [Fact]
    public void FinishedAs_ReadsAsTheSendStepDoes()
    {
        var brief = new Brief { Status = BriefStatus.Presented };
        BriefSteps.FinishedAs(brief, emailed: false).Should().BeNull();
        brief.Status = BriefStatus.Sent;
        BriefSteps.FinishedAs(brief, emailed: false).Should().Be("emailed", "Sent with neither mark is a sent brief");
        brief.DistributedAtUtc = DateTimeOffset.UtcNow;
        BriefSteps.FinishedAs(brief, emailed: false).Should().Be("distributed");
        BriefSteps.FinishedAs(brief, emailed: true).Should().Be("emailed");
        brief.NotSentAtUtc = DateTimeOffset.UtcNow;
        BriefSteps.FinishedAs(brief, emailed: false).Should().Be("notSent");
        BriefSteps.FinishedAs(brief, emailed: true).Should().Be("emailed");
    }

    // --- 3. History leaves out user ids --------------------------------------------------------------------------------

    [Fact]
    public async Task History_LeavesOutTheUserIds_BesideTheNames()
    {
        var brief = await Presented();
        await _db.Distribution().FinishNotSentAsync(Day, Why);

        var page = await new HistoryService(_db, _db.User).BriefAsync(brief);
        var fields = page.Entries.SelectMany(e => e.Changes).Select(c => c.Field).ToList();
        fields.Should().Contain("Finished as not sent by").And.NotContain(f => f.EndsWith("by id", StringComparison.Ordinal));

        var json = """{"DistributedById":[null,"S-1-5-21-1"],"DistributedByName":[null,"Pat"],"SentById":["a","b"],"RunById":[null,"c"],"EnteredById":[null,"d"]}""";
        HistoryFormatter.Changes(json).Should().ContainSingle().Which.Should().Be(new HistoryChange("Distributed by", null, "Pat"));
        HistoryFormatter.IsUserId("ById").Should().BeFalse();
        HistoryFormatter.IsUserId(nameof(Brief.SharePointPostedById)).Should().BeTrue();
    }

    // --- 4. Back to ready waits for the meeting in present mode -----------------------------------------------------

    private sealed class Live(Guid brief) : IMeetingsInProgress
    {
        public bool IsLive(Guid briefId) => briefId == brief;
    }

    [Fact]
    public async Task BackToReady_IsRefused_WhileTheMeetingRunsInPresentMode()
    {
        var brief = await Presented();
        var during = new BriefService(_db, _db.User, _db.Clock, _db.Rotation(), new Live(brief));

        (await during.ReopeningAsync(brief)).Blocked.Should().Be("This brief's meeting is running in present mode. End the meeting first.");
        var reopen = () => during.ReopenAsync(brief);
        await reopen.Should().ThrowAsync<InvalidOperationException>().WithMessage(BriefService.MeetingLive);
        (await Read()).Status.Should().Be(BriefStatus.Presented);

        var other = new BriefService(_db, _db.User, _db.Clock, _db.Rotation(), new Live(Guid.NewGuid()));
        (await other.ReopenAsync(brief)).Blocked.Should().BeNull("another brief's meeting doesn't hold this one");
        (await Read()).Status.Should().Be(BriefStatus.Ready);
        NoMeetingsInProgress.Instance.IsLive(brief).Should().BeFalse();
    }

    [Fact]
    public void LiveMeetings_SaysWhichBriefsMeetingIsRunning()
    {
        using var meetings = new LiveMeetings(new TestClock(DateTimeOffset.UtcNow));
        var brief = Guid.NewGuid();
        IMeetingsInProgress asked = meetings;
        asked.IsLive(brief).Should().BeFalse();
        var seat = meetings.Start(Day, brief, "pat", "Pat", "c-pat", Guid.NewGuid(), 0);
        asked.IsLive(brief).Should().BeTrue();
        asked.IsLive(Guid.NewGuid()).Should().BeFalse();
        meetings.End(seat);
        asked.IsLive(brief).Should().BeFalse();
    }
}

/// <summary>The rota moving a day keeps the presenter of a brief whose meeting was held, though it was reopened.</summary>
public class OwnerDecisionsRotationTests : IDisposable
{
    private readonly TestDb _db = new();
    private const string Me = "S-1-5-21-TEST-1", Robin = "dev:robin", Sam = "dev:sam";
    private readonly DateOnly _week1;

    public OwnerDecisionsRotationTests()
    {
        _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };
        _week1 = RotationService.WeekStart(_db.Clock.Today).AddDays(14);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task TheRotaMoving_KeepsAHeldMeetingsPresenter_AttendeesNotesOrItsTimesInTheTrail()
    {
        var r = _db.Rotation();
        await r.AddMemberAsync(Me, "Test Analyst");
        await r.AddMemberAsync(Robin, "Robin");
        await r.AddMemberAsync(Sam, "Sam");
        await r.SetWeekAsync(_week1, Sam);

        var days = Enumerable.Range(0, 5).Select(i => _week1.AddDays(i)).ToList();
        var briefs = new List<Guid>();
        foreach (var d in days) briefs.Add((await _db.Briefs().GetOrStartAsync(d)).Id);
        await using (var ctx = _db.Context())
        {
            ctx.MeetingAttendees.Add(new MeetingAttendee { BriefId = briefs[0], Name = "In the room" });
            ctx.MeetingNotes.Add(new MeetingNote { BriefId = briefs[1], AuthorId = Sam, AuthorName = "Sam", AtUtc = DateTimeOffset.UtcNow, Text = "Patch Friday." });
            (await ctx.Briefs.SingleAsync(b => b.Id == briefs[2])).MeetingNotes = "Kept on the brief, from before.";
            await ctx.SaveChangesAsync();
        }
        // Ended with nobody recorded, then taken back to Ready: the trail keeps the times.
        await using (var ctx = _db.Context())
        {
            (await ctx.Briefs.SingleAsync(b => b.Id == briefs[3])).PresentedAtUtc = DateTimeOffset.UtcNow;
            await ctx.SaveChangesAsync();
        }
        await using (var ctx = _db.Context())
        {
            (await ctx.Briefs.SingleAsync(b => b.Id == briefs[3])).PresentedAtUtc = null;
            await ctx.SaveChangesAsync();
        }

        await r.SetWeekAsync(_week1, Robin);

        foreach (var d in days.Take(4)) (await _db.Briefs().GetAsync(d))!.AnalystName.Should().Be("Sam", $"the meeting of {d} was held");
        (await _db.Briefs().GetAsync(days[4]))!.AnalystName.Should().Be("Robin", "no meeting was held, so it follows the rota");
    }
}

/// <summary>The pages: the Send dialog holding "Finish as not sent" while a send is going, and the brief page holding Back
/// to ready while its meeting runs, redrawn as it starts and ends.</summary>
public class OwnerDecisionsBriefPagesTests(TideApp app) : IClassFixture<TideApp>
{
    private DateOnly Today => app.Services.GetRequiredService<Tide.Application.Org.OrgClock>().CurrentBriefDate;

    private static DateOnly Before(DateOnly day, int n)
    {
        for (var i = 0; i < n; i++) day = Tide.Application.Org.WorkingDays.Weekdays.Previous(day);
        return day;
    }

    private async Task<(DateOnly Day, Guid Brief)> Presented(int daysBack)
    {
        var day = Before(Today, daysBack);
        await using var scope = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst);
        var sp = scope.ServiceProvider;
        var briefs = sp.GetRequiredService<BriefService>();
        var articles = sp.GetRequiredService<Tide.Application.Articles.ArticleService>();
        var brief = await briefs.GetOrStartAsync(day);
        await briefs.AddArticleAsync(brief.Id, await articles.CreateAsync($"Decision page {daysBack}", "Vendor", null, TlpLevel.Clear));
        await briefs.MarkPresentedAsync(brief.Id);
        return (day, brief.Id);
    }

    [Fact]
    public async Task TheSendDialog_HoldsFinishAsNotSent_WithTheReason_WhileASendIsGoing()
    {
        var (day, brief) = await Presented(141);
        Brief? read;
        await using (var scope = ComponentHarness.SignedIn(app, ComponentHarness.SignIn.Analyst))
        {
            var sp = scope.ServiceProvider;
            var db = sp.GetRequiredService<Tide.Infrastructure.Persistence.AppDbContext>();
            db.BriefSends.Add(new BriefSend
            {
                BriefId = brief, BriefDate = day, Edition = Edition.Team, Status = SendStatus.Sending, Ceiling = TlpLevel.Green, Marking = TlpLevel.Green,
                Subject = "Daily brief", Recipients = "soc@example.org", RecipientCount = 1, SentAtUtc = DateTimeOffset.UtcNow,
                SentById = "x", SentByName = "X", EmailHtml = "<p>brief</p>", WebHtml = "<p>brief</p>",
            });
            await db.SaveChangesAsync();
            read = await sp.GetRequiredService<BriefService>().GetAsync(day);
        }
        await using var panel = await ComponentHarness.RenderAsync<Tide.Web.Components.Shared.DistributePanel>(app, "/", new() { ["Date"] = day, ["Brief"] = read });
        var html = await panel.Html();
        html.Should().Contain("Finish without sending").And.Contain("id=\"send-not-sent-pending\"")
            .And.Contain("so it can").And.Contain("be finished as not sent. Check the send log first.")
            .And.Contain("aria-describedby=\"send-not-sent-pending\"");
    }

    [Fact]
    public async Task TheBriefPage_HoldsBackToReady_WhileTheMeetingRuns_AndLetsGoWhenItEnds()
    {
        var (day, brief) = await Presented(143);
        await using var r = await ComponentHarness.RenderAsync<Tide.Web.Components.Pages.BriefPage>(app, $"/briefs/{day:yyyy-MM-dd}",
            new() { ["Day"] = $"{day:yyyy-MM-dd}" });
        (await r.Html()).Should().Contain("bi-arrow-counterclockwise").And.NotContain("reopen-why");

        // Started and ended from present mode, heard on the page's own sign-in (as its circuit would be).
        var meetings = app.Services.GetRequiredService<LiveMeetings>();
        IMeetingSeat? seat = null;
        await r.Click(_ => { seat = meetings.Start(day, brief, "dev:tester", "Tester", "c-decisions", Guid.NewGuid(), 0); return Task.CompletedTask; });
        var live = await r.Html();
        live.Should().Contain("id=\"reopen-why\"").And.Contain("running in present mode. End the meeting first.")
            .And.Contain("aria-describedby=\"reopen-why\"");

        await r.Click(_ => { meetings.End(seat!); return Task.CompletedTask; });
        var after = await r.Html();
        after.Should().NotContain("reopen-why").And.Contain("bi-arrow-counterclockwise");
    }
}
