using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Tide.Application.Intake;
using Tide.Application.Operations;
using Tide.Application.Security;
using Tide.Application.Settings;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.TestSupport;
using Tide.Web.Components.Pages;

namespace Tide.IntegrationTests;

/// <summary>Triage's Sources tab: the polled sources, and Pull now held to the analyst pull-now window.</summary>
public class SourcePullTests : IDisposable
{
    private readonly TestDb _db = new(connectionPerContext: true);
    private readonly TestClock _clock = new(new DateTimeOffset(2026, 11, 4, 9, 0, 0, TimeSpan.Zero));
    private const string FeedUrl = "https://news.example.com/pull.xml";

    public SourcePullTests() => As(AppRole.Analyst, AppRole.Admin);

    public void Dispose() => _db.Dispose();

    private void As(params AppRole[] roles) => _db.User.Roles = roles.ToHashSet();

    private IntakeService Intake(int pullMinutes = 15, bool enabled = true) =>
        _db.Intake(new IntakeOptions { Enabled = enabled, MinPollMinutes = 5, PullNowMinutes = pullMinutes }, _clock);

    private static string Rss(params string[] titles) =>
        "<rss version=\"2.0\"><channel><title>News</title>" +
        string.Concat(titles.Select(t => $"<item><title>{t}</title><link>https://news.example.com/{Uri.EscapeDataString(t)}</link></item>")) +
        "</channel></rss>";

    private async Task<Guid> Source(string name = "Sample news", string url = FeedUrl, bool enabled = true, SourceKind kind = SourceKind.Feed)
    {
        var roles = _db.User.Roles;
        As(AppRole.Admin);
        var id = await Intake().SaveSourceAsync(new SourceForm { Name = name, Kind = kind, Url = url, PollMinutes = 30, Enabled = enabled });
        _db.User.Roles = roles;
        return id;
    }

    // --- permissions ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task PullNow_AnAnalyst_PullsTheSource()
    {
        var id = await Source();
        _db.Http.Set(FeedUrl, Rss("One", "Two", "Three"));
        As(AppRole.Analyst);

        var result = await Intake().PullNowAsync(id);

        result.Should().Match<PollResult>(r => r.Error == null && r.New == 3);
        (await Intake().PolledSourcesAsync()).Single().Should().Match<PolledSource>(s => s.LastPolledAtUtc == _clock.Now && s.LastNewItems == 3 && !s.Failing);
        (await Intake().NewCountAsync()).Should().Be(3);
    }

    [Fact]
    public async Task PullNow_AnActionOwnerOrAReader_IsRefused()
    {
        var id = await Source();
        foreach (var role in new[] { AppRole.ActionOwner, AppRole.Reader })
        {
            As(role);
            await FluentActions.Invoking(() => Intake().PullNowAsync(id)).Should().ThrowAsync<ForbiddenException>();
            await FluentActions.Invoking(() => Intake().PolledSourcesAsync()).Should().ThrowAsync<ForbiddenException>();
        }
        _db.Http.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task PollNow_StaysAnAdministratorsOnly()
    {
        var id = await Source();
        As(AppRole.Analyst);
        await FluentActions.Invoking(() => Intake().PollNowAsync(id)).Should().ThrowAsync<ForbiddenException>("analysts pull, with the window");
    }

    // --- the window ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task PullNow_InsideTheWindow_IsRefusedSayingWhen_AndAfterItIsAllowed()
    {
        var id = await Source();
        _db.Http.Set(FeedUrl, Rss("One"));
        As(AppRole.Analyst);
        await Intake().PullNowAsync(id);

        _clock.Now += TimeSpan.FromMinutes(4);
        var refused = await FluentActions.Invoking(() => Intake().PullNowAsync(id)).Should().ThrowAsync<PullTooSoonException>();
        refused.Which.Message.Should().Be("Pulled 4 min ago. You can pull it again in 11 min.");
        refused.Which.PullableAtUtc.Should().Be(_clock.Now + TimeSpan.FromMinutes(11));
        _db.Http.Requests.Should().HaveCount(1, "a refused pull fetches nothing");
        (await Intake().PolledSourcesAsync()).Single().Pullable(_clock.Now).Should().BeFalse();

        _clock.Now += TimeSpan.FromMinutes(11);
        (await Intake().PolledSourcesAsync()).Single().Pullable(_clock.Now).Should().BeTrue();
        (await Intake().PullNowAsync(id)).Error.Should().BeNull();
        _db.Http.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task PullNow_TheWindowIsTheAdministratorsSetting()
    {
        var id = await Source();
        _db.Http.Set(FeedUrl, Rss("One"));
        await Intake(pullMinutes: 2).PullNowAsync(id);

        _clock.Now += TimeSpan.FromMinutes(1);
        (await FluentActions.Invoking(() => Intake(pullMinutes: 2).PullNowAsync(id)).Should().ThrowAsync<PullTooSoonException>())
            .Which.Message.Should().Be("Pulled 1 min ago. You can pull it again in 1 min.");
        _clock.Now += TimeSpan.FromMinutes(1);
        (await Intake(pullMinutes: 2).PullNowAsync(id)).Error.Should().BeNull();
        Intake(pullMinutes: 0).PullWindow.Should().Be(TimeSpan.FromMinutes(1), "never under a minute");
    }

    [Fact]
    public async Task PullNow_AScheduledPoll_StartsTheWindowToo()
    {
        var id = await Source();
        _db.Http.Set(FeedUrl, Rss("One", "Two"));

        (await Intake().PollDueAsync()).Should().Be(2, "a source never polled is due");
        _clock.Now += TimeSpan.FromMinutes(3);
        As(AppRole.Analyst);

        (await FluentActions.Invoking(() => Intake().PullNowAsync(id)).Should().ThrowAsync<PullTooSoonException>())
            .Which.Message.Should().Be("Pulled 3 min ago. You can pull it again in 12 min.");
    }

    [Fact]
    public async Task PullNow_AFailedPoll_StartsTheWindowToo_AndTheErrorIsShown()
    {
        var id = await Source();
        _db.Http.Responses[FeedUrl] = new HttpFetch(FetchStatus.Failed, Error: "The server answered 503.");
        As(AppRole.Analyst);

        (await Intake().PullNowAsync(id)).Error.Should().Be("The server answered 503.");

        var row = (await Intake().PolledSourcesAsync()).Single();
        row.Should().Match<PolledSource>(s => s.Failing && s.LastError == "The server answered 503." && s.LastNewItems == null);
        row.NextDueUtc.Should().Be(_clock.Now + TimeSpan.FromMinutes(60), "a failure backs the 30-minute interval off");
        _clock.Now += TimeSpan.FromMinutes(5);
        await FluentActions.Invoking(() => Intake().PullNowAsync(id)).Should().ThrowAsync<PullTooSoonException>();
    }

    [Fact]
    public async Task PullNow_HoldsAnAdministratorToTheWindow_ButPollNowOnSourcesDoesnt()
    {
        var id = await Source();
        _db.Http.Set(FeedUrl, Rss("One"));
        As(AppRole.Analyst, AppRole.Admin);
        await Intake().PullNowAsync(id);

        await FluentActions.Invoking(() => Intake().PullNowAsync(id)).Should().ThrowAsync<PullTooSoonException>("an administrator on Triage is held to it too");
        (await Intake().PollNowAsync(id)).Error.Should().BeNull("Administration › Sources' Poll now isn't held to the window");
        (await Intake().PollNowAsync(id)).Error.Should().BeNull();
        _db.Http.Requests.Should().HaveCount(3);
    }

    [Fact]
    public async Task PullNow_APausedRetiredOrApiSource_IsRefused()
    {
        var paused = await Source("Paused news", "https://paused.example.com/feed", enabled: false);
        As(AppRole.Analyst);
        (await FluentActions.Invoking(() => Intake().PullNowAsync(paused)).Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("paused");
        await FluentActions.Invoking(() => Intake().PullNowAsync(Guid.NewGuid())).Should().ThrowAsync<KeyNotFoundException>();
        await using (var ctx = _db.Context())
        {
            ctx.Sources.Add(new Source { Name = "Proposals", Kind = SourceKind.Api, Url = "https://api.invalid/x", Enabled = true });
            await ctx.SaveChangesAsync();
        }
        var api = (await _db.Context().Sources.SingleAsync(s => s.Kind == SourceKind.Api)).Id;
        (await FluentActions.Invoking(() => Intake().PullNowAsync(api)).Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("through the API");
        _db.Http.Requests.Should().BeEmpty();
    }

    // --- one pull at a time --------------------------------------------------------------------------------------

    [Fact]
    public async Task PullNow_TwoAtOnce_MakeOnePull()
    {
        var id = await Source();
        _db.Http.Set(FeedUrl, Rss("One", "Two"));
        As(AppRole.Analyst);
        Exception? second = null;
        var scheduled = -1;
        var polling = false;
        _db.Http.During = async () =>
        {
            // While the first pull's fetch is under way: a colleague presses too, the scheduled pass comes round, and the
            // tab shows the source as being pulled.
            second = await Record.ExceptionAsync(() => Intake().PullNowAsync(id));
            scheduled = await Intake().PollDueAsync();
            polling = (await Intake().PolledSourcesAsync()).Single().Pulling;
        };

        (await Intake().PullNowAsync(id)).New.Should().Be(2);

        second.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Contain("being pulled now");
        scheduled.Should().Be(0, "the scheduled pass leaves a source being pulled to that pull");
        polling.Should().BeTrue();
        _db.Http.Requests.Should().HaveCount(1);
        _db.Polls.IsRunning(id).Should().BeFalse("the guard is let go when the pull ends");
        (await Intake().PolledSourcesAsync()).Single().Pulling.Should().BeFalse();
    }

    [Fact]
    public async Task TheScheduledPass_DoesntPollAgain_ASourcePulledOrPausedAfterItsListWasRead()
    {
        Guid[] ids =
        [
            await Source("First news", "https://first.example.com/feed"),
            await Source("Second news", "https://second.example.com/feed"),
            await Source("Third news", "https://third.example.com/feed"),
        ];
        _db.Http.Set("https://first.example.com/feed", Rss("One"));
        _db.Http.Set("https://second.example.com/feed", Rss("Two"));
        _db.Http.Set("https://third.example.com/feed", Rss("Three"));
        _db.Http.During = async () =>
        {
            // The pass is fetching its first source (whichever it took): an analyst pulls another, and an administrator
            // pauses the last.
            var others = ids.Where(id => !_db.Polls.IsRunning(id)).ToList();
            others.Should().HaveCount(2);
            As(AppRole.Analyst);
            await Intake().PullNowAsync(others[0]);
            await using var ctx = _db.Context();
            await ctx.Sources.Where(s => s.Id == others[1]).ExecuteUpdateAsync(u => u.SetProperty(s => s.Enabled, false));
        };

        (await Intake().PollDueAsync()).Should().Be(1, "only the first source is the pass's own; the pulled one's item came with the pull");
        _db.Http.Requests.Should().HaveCount(2, "the pull's fetch and the pass's first; the pulled and the paused aren't fetched again");
    }

    [Fact]
    public async Task PollNow_WhileAPullIsUnderWay_IsRefused_AndTheGuardIsLetGoOnAFault()
    {
        var id = await Source();
        _db.Http.Set(FeedUrl, Rss("One"));
        Exception? admin = null;
        _db.Http.During = async () => admin = await Record.ExceptionAsync(() => Intake().PollNowAsync(id));
        await Intake().PullNowAsync(id);
        admin.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Contain("being pulled now");

        _clock.Now += TimeSpan.FromHours(1);
        _db.Http.Throws[FeedUrl] = new InvalidOperationException("Unexpected.");
        (await Intake().PullNowAsync(id)).Error.Should().StartWith("The poll failed");
        _db.Polls.IsRunning(id).Should().BeFalse();
    }

    // --- the list and the tab's count ----------------------------------------------------------------------------

    [Fact]
    public async Task PolledSources_ListsThePolledOnesByName_AndTheTabCountsTheFailing()
    {
        var ok = await Source("Beta news", "https://beta.example.com/feed");
        var failing = await Source("Alpha news", "https://alpha.example.com/feed");
        await Source("Gamma news (paused)", "https://gamma.example.com/feed", enabled: false);
        var retired = await Source("Delta news", "https://delta.example.com/feed");
        var kev = await Source("Known exploited", "https://kev.example.com/kev.json", kind: SourceKind.CisaKev);
        await using (var ctx = _db.Context())
        {
            (await ctx.Sources.SingleAsync(s => s.Id == retired)).IsRetired = true;
            ctx.Sources.Add(new Source { Name = "Proposals", Kind = SourceKind.Api, Url = "https://api.invalid/x", Enabled = true });
            await ctx.SaveChangesAsync();
        }
        _db.Http.Set("https://beta.example.com/feed", Rss("One"));
        _db.Http.Responses["https://alpha.example.com/feed"] = new HttpFetch(FetchStatus.Failed, Error: "Not found.");
        As(AppRole.Analyst);
        var intake = Intake();

        var before = await intake.PolledSourcesAsync();
        before.Select(s => s.Name).Should().Equal("Alpha news", "Beta news", "Known exploited");
        before.Should().OnlyContain(s => s.LastPolledAtUtc == null && s.NextDueUtc == null && s.PullableAtUtc == null && s.Pullable(_clock.Now));
        Triage.FailingCount(before).Should().BeNull("with none failing the tab has no count");

        await intake.PullNowAsync(ok);
        await intake.PullNowAsync(failing);
        var after = await intake.PolledSourcesAsync();

        Triage.FailingCount(after).Should().Be(1);
        after.Single(s => s.Id == failing).LastError.Should().Be("Not found.");
        after.Single(s => s.Id == ok).Should().Match<PolledSource>(s => s.LastNewItems == 1 && s.NextDueUtc == _clock.Now + TimeSpan.FromMinutes(30)
            && s.PullableAtUtc == _clock.Now + TimeSpan.FromMinutes(15));
        after.Single(s => s.Id == kev).Kind.Should().Be(SourceKind.CisaKev);
    }

    // --- the setting ---------------------------------------------------------------------------------------------

    [Fact]
    public void PullNowWindow_DefaultsTo15_FromAMinuteToADay_AndIsListedOnSources()
    {
        new IntakeOptions().PullNowMinutes.Should().Be(15);
        OperationalSettings.Normalize(OperationalSettings.PullNowMinutes, "1").Should().Be("1");
        OperationalSettings.Normalize(OperationalSettings.PullNowMinutes, " 1440 ").Should().Be("1440");
        OperationalSettings.Problem(OperationalSettings.PullNowMinutes, "0").Should().Be("The analyst pull-now window is a whole number from 1 to 1440.");
        OperationalSettings.Problem(OperationalSettings.PullNowMinutes, "1441").Should().NotBeNull();
        OperationalSettings.Problem(OperationalSettings.PullNowMinutes, "ten").Should().NotBeNull();
        SettingsCatalog.Find(OperationalSettings.PullNowMinutes).Should().Match<SettingInfo>(s =>
            s.Group == "Intake" && s.Label == "Analyst pull-now window (minutes)" && s.EditedIn == "admin/sources");
        SettingsCatalog.IsInApp(OperationalSettings.PullNowMinutes).Should().BeTrue("it travels in a configuration bundle");
    }

    [Fact]
    public async Task PullNowWindow_Saved_IsTheOneInForce_AndTheAppHasOneGuard()
    {
        using var rig = new SettingsRig();
        rig.Get<IntakeService>().PullWindow.Should().Be(TimeSpan.FromMinutes(15), "the default, with nothing saved");
        await rig.Save(OperationalSettings.PullNowMinutes, "40");
        rig.Now<IntakeOptions>().PullNowMinutes.Should().Be(40);
        rig.Get<IntakeService>().PullWindow.Should().Be(TimeSpan.FromMinutes(40));
        await FluentActions.Invoking(() => rig.Save(OperationalSettings.PullNowMinutes, "0")).Should().ThrowAsync<ArgumentException>();
        rig.Get<SourcePollGuard>().Should().BeSameAs(rig.Get<SourcePollGuard>(), "one guard for the process (ADR 0001)");
    }

    // --- how the times are said ----------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, "just now")]
    [InlineData(59, "just now")]
    [InlineData(60, "1 min ago")]
    [InlineData(12 * 60 + 30, "12 min ago")]
    [InlineData(3 * 3600 + 120, "3 h ago")]
    [InlineData(3 * 86400, "3 days ago")]
    public void Ago_SaysHowLongAgo(int seconds, string expected) => PullTimes.Ago(TimeSpan.FromSeconds(seconds)).Should().Be(expected);

    [Theory]
    [InlineData(0, "1 min")]
    [InlineData(30, "1 min")]
    [InlineData(10 * 60 + 1, "11 min")]
    [InlineData(3600, "1 h")]
    [InlineData(3900, "1 h 5 min")]
    [InlineData(3 * 86400, "3 days")]
    public void Wait_SaysHowLongToWait_RoundedUp(int seconds, string expected) => PullTimes.Wait(TimeSpan.FromSeconds(seconds)).Should().Be(expected);
}
