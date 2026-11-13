using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Tide.Application.Intake;
using Tide.Application.Operations;
using Tide.Application.Org;
using Tide.Application.Security;
using Tide.Application.Settings;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.TestSupport;

namespace Tide.IntegrationTests;

/// <summary>Pull all on Triage's Sources tab: every polled source outside the pull-now window, one at a time, through
/// Pull now's own checks; and what Today says when the feeds haven't been pulled since the morning.</summary>
public class SourcePullAllTests : IDisposable
{
    private readonly TestDb _db = new(connectionPerContext: true);
    // Wednesday 4 November 2026, 9:00 in New York (14:00 UTC).
    private readonly TestClock _clock = new(new DateTimeOffset(2026, 11, 4, 14, 0, 0, TimeSpan.Zero));

    public SourcePullAllTests() => As(AppRole.Analyst);

    public void Dispose() => _db.Dispose();

    private void As(params AppRole[] roles) => _db.User.Roles = roles.ToHashSet();

    private IntakeService Intake(bool enabled = true, string pulledBy = "08:00") =>
        _db.Intake(new IntakeOptions { Enabled = enabled, MinPollMinutes = 5, PullNowMinutes = 15, PulledBy = pulledBy }, _clock);

    private OrgClock Org(IReadOnlyDictionary<DateOnly, string>? holidays = null)
    {
        var clock = new OrgClock(Microsoft.Extensions.Options.Options.Create(new OrgOptions { TimeZone = "America/New_York" }), _clock);
        if (holidays is not null) clock.UseHolidays(holidays);
        return clock;
    }

    private static string Rss(string key, int n) =>
        "<rss version=\"2.0\"><channel><title>News</title>" +
        string.Concat(Enumerable.Range(1, n).Select(i => $"<item><title>{key} story number {i} of the day</title><link>https://{key}.example.com/{i}</link></item>")) +
        "</channel></rss>";

    private async Task<Guid> Source(string name, DateTimeOffset? lastPolled = null, string? error = null, bool enabled = true)
    {
        await using var ctx = _db.Context();
        var s = new Source
        {
            Name = name, Url = $"https://{name.Replace(' ', '-').ToLowerInvariant()}.example.com/feed", PollMinutes = 60, Enabled = enabled,
            LastPolledAtUtc = lastPolled, LastSuccessAtUtc = error is null ? lastPolled : null, LastError = error,
        };
        ctx.Sources.Add(s);
        await ctx.SaveChangesAsync();
        return s.Id;
    }

    private static string Feed(string name) => $"https://{name.Replace(' ', '-').ToLowerInvariant()}.example.com/feed";

    // --- Pull all --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task PullAll_PullsEachSourceOutsideTheWindow_OneAtATime_InOrderOfName()
    {
        string[] names = ["Gamma news", "Alpha news", "Beta news"];
        foreach (var n in names) await Source(n, _clock.Now.AddHours(-3));
        _db.Http.Set(Feed("Alpha news"), Rss("alpha", 2));
        _db.Http.Set(Feed("Beta news"), Rss("beta", 1));
        _db.Http.Set(Feed("Gamma news"), Rss("gamma", 3));
        var steps = new List<PullAllStep>();
        var atOnce = new List<int>();
        var intake = Intake();

        var result = await intake.PullAllAsync(step =>
        {
            steps.Add(step);
            // Before each source starts, none is being polled: the one before it has finished.
            atOnce.Add(names.Count(n => _db.Polls.IsRunning(SourceId(n))));
        });

        result.Should().Match<PullAllResult>(r => r.Pulled == 3 && r.New == 6 && r.Recent.Count == 0 && r.Busy.Count == 0 && r.Failed.Count == 0 && r.NotReached == 0);
        steps.Select(s => s.Text).Should().Equal("Pulling 1 of 3: Alpha news…", "Pulling 2 of 3: Beta news…", "Pulling 3 of 3: Gamma news…");
        atOnce.Should().OnlyContain(n => n == 0, "sources are pulled one after another, never side by side");
        _db.Http.Requests.Select(r => r.Url).Should().Equal(Feed("Alpha news"), Feed("Beta news"), Feed("Gamma news"));
        _db.Polls.AllRunning.Should().BeFalse("the run lets go when it ends");
    }

    private Guid SourceId(string name)
    {
        using var ctx = _db.Context();
        return ctx.Sources.AsNoTracking().Single(s => s.Name == name).Id;
    }

    [Fact]
    public async Task PullAll_SkipsThosePulledInTheWindow_AndThoseBeingPulled_AndSaysWhichFailed()
    {
        await Source("Recent news", _clock.Now.AddMinutes(-5));
        await Source("Old news", _clock.Now.AddHours(-2));
        await Source("Broken news", _clock.Now.AddHours(-2), "Timed out.");
        var busy = await Source("Busy news", _clock.Now.AddHours(-2));
        await Source("Paused news", _clock.Now.AddHours(-2), enabled: false);
        _db.Http.Set(Feed("Old news"), Rss("old", 2));
        _db.Http.Responses[Feed("Broken news")] = new HttpFetch(FetchStatus.Failed, Error: "The server answered 503.");
        _db.Polls.TryStart(busy).Should().BeTrue("a colleague's Pull now is under way");

        var result = await Intake().PullAllAsync();

        _db.Polls.Finished(busy);
        result.Recent.Should().Equal("Recent news");
        result.Busy.Should().Equal("Busy news");
        result.Failed.Should().Equal("Broken news");
        result.Should().Match<PullAllResult>(r => r.Pulled == 1 && r.New == 2);
        result.Summary(TimeSpan.FromMinutes(15)).Should().Be("Pulled 1 · 2 new items · 1 skipped (pulled in the last 15 min) · 1 skipped (being pulled) · 1 failed: Broken news");
        _db.Http.Requests.Select(r => r.Url).Should().BeEquivalentTo([Feed("Broken news"), Feed("Old news")], "the recent, the busy and the paused aren't fetched");
    }

    [Fact]
    public async Task PullAll_ASourcePulledByHandWhileTheRunWasOnItsWay_IsSkippedNotPulledTwice()
    {
        await Source("Alpha news", _clock.Now.AddHours(-2));
        var beta = await Source("Beta news", _clock.Now.AddHours(-2));
        _db.Http.Set(Feed("Alpha news"), Rss("alpha", 1));
        _db.Http.Set(Feed("Beta news"), Rss("beta", 1));
        // While the run fetches Alpha, a colleague pulls Beta by hand.
        _db.Http.During = async () => await Intake().PullNowAsync(beta);

        var result = await Intake().PullAllAsync();

        result.Pulled.Should().Be(1);
        result.Recent.Should().Equal("Beta news");
        _db.Http.Requests.Count(r => r.Url == Feed("Beta news")).Should().Be(1);
    }

    [Fact]
    public async Task PullAll_TwoAtOnce_TheSecondIsRefused_SoNothingIsPolledTwice()
    {
        await Source("Alpha news", _clock.Now.AddHours(-2));
        await Source("Beta news", _clock.Now.AddHours(-2));
        _db.Http.Set(Feed("Alpha news"), Rss("alpha", 1));
        _db.Http.Set(Feed("Beta news"), Rss("beta", 1));
        Exception? second = null;
        _db.Http.During = async () => second = await Record.ExceptionAsync(() => Intake().PullAllAsync());

        (await Intake().PullAllAsync()).Pulled.Should().Be(2);

        second.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Contain("already pulling every source");
        _db.Http.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task PullAll_Stopped_FinishesTheSourceUnderWay_AndStartsNoOther()
    {
        await Source("Alpha news", _clock.Now.AddHours(-2));
        await Source("Beta news", _clock.Now.AddHours(-2));
        await Source("Gamma news", _clock.Now.AddHours(-2));
        _db.Http.Set(Feed("Alpha news"), Rss("alpha", 1));
        using var leave = new CancellationTokenSource();
        // The page is left while Alpha is being fetched.
        _db.Http.During = () => { leave.Cancel(); return Task.CompletedTask; };

        var result = await Intake().PullAllAsync(stop: leave.Token);

        result.Should().Match<PullAllResult>(r => r.Pulled == 1 && r.New == 1 && r.NotReached == 2);
        _db.Http.Requests.Should().ContainSingle();
        (await Intake().PolledSourcesAsync()).Single(s => s.Name == "Alpha news").LastNewItems.Should().Be(1, "the source under way was finished, not left half-pulled");
        _db.Polls.AllRunning.Should().BeFalse();
        (await Intake().PolledSourcesAsync()).Should().OnlyContain(s => !s.Pulling);
    }

    [Fact]
    public async Task PullAll_WorksWithScheduledPollingOff_AsPullNowDoes()
    {
        await Source("Alpha news", _clock.Now.AddHours(-2));
        _db.Http.Set(Feed("Alpha news"), Rss("alpha", 1));
        (await Intake(enabled: false).PullAllAsync()).Pulled.Should().Be(1);
    }

    [Fact]
    public async Task PullAll_IsAnAnalystsAsPullNowIs()
    {
        await Source("Alpha news", _clock.Now.AddHours(-2));
        foreach (var role in new[] { AppRole.ActionOwner, AppRole.Reader })
        {
            As(role);
            await FluentActions.Invoking(() => Intake().PullAllAsync()).Should().ThrowAsync<ForbiddenException>();
        }
        _db.Http.Requests.Should().BeEmpty();
        _db.Polls.AllRunning.Should().BeFalse("a refused run never starts");
    }

    [Fact]
    public async Task PullAll_AFaultInOneSource_IsItsOwn_AndTheGuardIsLetGo()
    {
        await Source("Alpha news", _clock.Now.AddHours(-2));
        await Source("Beta news", _clock.Now.AddHours(-2));
        _db.Http.Throws[Feed("Alpha news")] = new InvalidOperationException("Unexpected.");
        _db.Http.Set(Feed("Beta news"), Rss("beta", 1));

        var result = await Intake().PullAllAsync();

        result.Failed.Should().Equal("Alpha news");
        result.Pulled.Should().Be(1);
        _db.Polls.AllRunning.Should().BeFalse();
    }

    private sealed class StandingByLease : IJobLease
    {
        public Task<bool> HoldAsync(CancellationToken ct = default) => Task.FromResult(false);
        public LeaseStatus Status { get; } = new(LeaseState.StandingBy, "WEB02/4312/abc", null, null, null, null);
        public event Action? AlarmChanged { add { } remove { } }
    }

    [Fact]
    public async Task PullAll_OnAServerStandingBy_IsRefused_AndSaysSoBeforehand()
    {
        await Source("Alpha news", _clock.Now.AddHours(-2));
        var intake = new IntakeService(_db, _db.User, _db.Http, _db.Articles(),
            new StaticOptionsMonitor<IntakeOptions>(new IntakeOptions { Enabled = true }), _clock, polls: _db.Polls, lease: new StandingByLease());

        intake.StandingBy.Should().StartWith("This server is standing by");
        Intake().StandingBy.Should().BeNull("a service with no lease runs as the holder");
        await FluentActions.Invoking(() => intake.PullAllAsync()).Should().ThrowAsync<StandingByException>();
        _db.Http.Requests.Should().BeEmpty();
        _db.Polls.AllRunning.Should().BeFalse();
    }

    // --- what Today says -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Freshness_CountsTheFeedsNotPulledSinceTheCutoff_AndTheFailingApart()
    {
        await Source("Fresh news", _clock.Now.AddMinutes(-30));
        await Source("Stale news", _clock.Now.AddHours(-15));
        await Source("Never news");
        await Source("Broken news", _clock.Now.AddMinutes(-20), "Timed out.");
        await Source("Paused news", _clock.Now.AddDays(-3), enabled: false);

        var f = await Intake().FreshnessAsync(Org());

        f.Should().NotBeNull();
        f!.Should().Match<FeedFreshness>(x => x.Sources == 4 && x.NotPulled == 3 && !x.OnlyFailing);
        f.Failing.Should().Equal("Broken news");
        f.CutoffUtc.Should().Be(new DateTimeOffset(2026, 11, 4, 13, 0, 0, TimeSpan.Zero), "8:00 in New York");
        f.Text(Org().Zone, _clock.Now).Should().Be("3 of 4 feeds not pulled since 8:00 AM (1 failing) · last pull 8:40 AM", "the failing feed's pull, at 8:40, is the latest");
    }

    [Fact]
    public async Task Freshness_SaysNothing_WhenPollingIsOff_ThereAreNoSources_OrAllAreFresh()
    {
        (await Intake().FreshnessAsync(Org())).Should().BeNull("no sources are polled");
        await Source("Stale news", _clock.Now.AddHours(-15));
        (await Intake(enabled: false).FreshnessAsync(Org())).Should().BeNull("polling is off");
        (await Intake().FreshnessAsync(Org())).Should().NotBeNull();

        _db.Http.Set(Feed("Stale news"), Rss("stale", 1));
        await Intake().PullAllAsync();
        (await Intake().FreshnessAsync(Org())).Should().BeNull("every feed has been pulled since 8:00");
    }

    [Fact]
    public async Task Freshness_SaysNothing_BeforeTheCutoff_OnAWeekend_OrOnAHoliday()
    {
        await Source("Stale news", _clock.Now.AddHours(-15));
        (await Intake(pulledBy: "09:30").FreshnessAsync(Org())).Should().BeNull("it's 9:00, before a 9:30 cutoff");
        (await Intake(pulledBy: "09:00").FreshnessAsync(Org())).Should().NotBeNull("at the cutoff's minute it's in force");
        (await Intake().FreshnessAsync(Org(new Dictionary<DateOnly, string> { [new DateOnly(2026, 11, 4)] = "Founders' day" }))).Should().BeNull("a holiday");
        _clock.Now = new DateTimeOffset(2026, 11, 7, 15, 0, 0, TimeSpan.Zero);   // Saturday
        (await Intake().FreshnessAsync(Org())).Should().BeNull("a weekend");
    }

    [Fact]
    public async Task Freshness_IsAnAnalystsToAsk()
    {
        As(AppRole.ActionOwner);
        await FluentActions.Invoking(() => Intake().FreshnessAsync(Org())).Should().ThrowAsync<ForbiddenException>();
    }

    // --- the setting ---------------------------------------------------------------------------------------------

    [Fact]
    public void PulledBy_DefaultsTo8_IsA24HourTime_AndIsListedOnSources()
    {
        new IntakeOptions().PulledBy.Should().Be("08:00");
        new IntakeOptions().PulledByTime.Should().Be(new TimeOnly(8, 0));
        new IntakeOptions { PulledBy = "nonsense" }.PulledByTime.Should().Be(new TimeOnly(8, 0), "a value that can't be read falls back to the default");
        OperationalSettings.Normalize(OperationalSettings.PulledBy, " 7:30 ").Should().Be("07:30");
        OperationalSettings.Problem(OperationalSettings.PulledBy, "25:00").Should().Be("The time feeds are pulled by is a 24-hour time such as 08:00.");
        OperationalSettings.Problem(OperationalSettings.PulledBy, "8am").Should().NotBeNull();
        SettingsCatalog.Find(OperationalSettings.PulledBy).Should().Match<SettingInfo>(s =>
            s.Group == "Intake" && s.Label == "Feeds pulled by (each working day)" && s.EditedIn == "admin/sources");
        SettingsCatalog.IsInApp(OperationalSettings.PulledBy).Should().BeTrue("it travels in a configuration bundle");
    }

    [Fact]
    public async Task PulledBy_Saved_IsTheOneInForceAtOnce_AndAudited()
    {
        using var rig = new SettingsRig();
        rig.Now<IntakeOptions>().PulledByTime.Should().Be(new TimeOnly(8, 0), "the default, with nothing saved");
        await rig.Save(OperationalSettings.PulledBy, "07:15");
        rig.Now<IntakeOptions>().PulledByTime.Should().Be(new TimeOnly(7, 15));
        await FluentActions.Invoking(() => rig.Save(OperationalSettings.PulledBy, "7.15pm")).Should().ThrowAsync<ArgumentException>();
        await using var ctx = rig.Db.Context();
        (await ctx.AuditLog.AnyAsync(a => a.EntityType == nameof(AppSetting) && a.EntityKey == OperationalSettings.PulledBy))
            .Should().BeTrue("a saved setting is audited like any change");
    }
}
