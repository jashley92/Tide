using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Text.RegularExpressions;
using Tide.Application.Abstractions;
using Tide.Application.Intake;
using Tide.Application.Org;
using Tide.Application.Settings;
using Tide.Domain.Entities;
using Tide.TestSupport;
using Tide.Web.Components.Pages;
using Tide.Web.Components.Pages.Admin;
using Tide.Web.Services;
using Microsoft.EntityFrameworkCore;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>The app with polling on, its outbound requests answered by <see cref="Http"/>, on a clock a test sets:
/// Wednesday 4 November 2026, 9:00 in New York (the organization's zone), an hour after the default cutoff.</summary>
public sealed class FeedsApp : TideApp
{
    public FakeHttp Http { get; } = new();
    public TestClock Clock { get; } = new(new DateTimeOffset(2026, 11, 4, 14, 0, 0, TimeSpan.Zero));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Intake:Enabled", "true");
        builder.ConfigureTestServices(s =>
        {
            s.AddSingleton<IOutboundHttp>(Http);
            s.AddSingleton<TimeProvider>(Clock);
        });
    }
}

/// <summary>Today's word on the feeds not pulled since the morning, with Pull all, by role and by state; and Pull all
/// on Triage's Sources tab.</summary>
public class FeedsPullAllPageTests(FeedsApp app) : IClassFixture<FeedsApp>
{
    private static readonly SignIn Analyst = new("dev:feeds-analyst", "Feeds Analyst", "Analyst");
    private static readonly SignIn Owner = new("dev:feeds-owner", "Feeds Owner", "ActionOwner");
    private static readonly SignIn Viewer = new("dev:feeds-viewer", "Feeds Viewer", "Viewer");
    private static readonly SignIn Admin = new("dev:feeds-admin", "Feeds Admin", "Admin");

    private DateOnly Today => app.Services.GetRequiredService<OrgClock>().CurrentBriefDate;

    private Task<Rendered<BriefPage>> RenderToday(SignIn who) =>
        RenderAsync<BriefPage>(app, "/", new() { ["Day"] = $"{Today:yyyy-MM-dd}", ["AsToday"] = true }, who);

    // A feed the scheduled pass leaves alone (its interval is a week), last pulled that long before the clock.
    private async Task<Guid> Source(string name, TimeSpan ago, string? error = null)
    {
        await using var scope = app.Services.CreateAsyncScope();
        await using var db = await scope.ServiceProvider.GetRequiredService<IAppDbContextFactory>().CreateAsync();
        var at = app.Clock.Now - ago;
        var s = new Source
        {
            Name = name, Url = $"https://{name.Replace(' ', '-').ToLowerInvariant()}.example.com/feed", PollMinutes = 7 * 24 * 60,
            LastPolledAtUtc = at, LastSuccessAtUtc = error is null ? at : null, LastError = error,
        };
        db.Sources.Add(s);
        await db.SaveChangesAsync();
        return s.Id;
    }

    private static string Feed(string name) => $"https://{name.Replace(' ', '-').ToLowerInvariant()}.example.com/feed";

    private static string Rss(string key) =>
        $"<rss version=\"2.0\"><channel><title>N</title><item><title>{key} breaking story about a breach</title><link>https://{key}.example.com/1</link></item></channel></rss>";

    private async Task Save(string key, string value)
    {
        await using var scope = SignedIn(app, SignIn.Analyst);
        await scope.ServiceProvider.GetRequiredService<OperationalSettingsService>().SaveAsync(new Dictionary<string, string?> { [key] = value });
    }

    [Theory]
    [MemberData(nameof(CantPull))]
    public async Task Today_SaysNothingAboutFeeds_ToThoseWhoCantPullThem(string role)
    {
        await Source($"Stale for {role}", TimeSpan.FromHours(15));
        var who = role == "ActionOwner" ? Owner : Viewer;
        await using var r = await RenderToday(who);
        (await r.Html()).Should().NotContain("tide-feeds-nudge").And.NotContain("Pull all");
    }

    public static TheoryData<string> CantPull => ["ActionOwner", "Viewer"];

    [Fact]
    public async Task Today_SaysNothing_WhenPollingIsOff_BeforeTheCutoff_OrOnAWeekend()
    {
        await Source("Stale while off", TimeSpan.FromHours(15));
        await using (var r = await RenderToday(Analyst))
            (await r.Html()).Should().Contain("tide-feeds-nudge");

        var was = app.Clock.Now;
        try
        {
            await Save(OperationalSettings.IntakeEnabled, "false");
            await using (var r = await RenderToday(Analyst))
                (await r.Html()).Should().NotContain("tide-feeds-nudge", "polling is off");
            await Save(OperationalSettings.IntakeEnabled, "true");

            await Save(OperationalSettings.PulledBy, "09:30");
            await using (var r = await RenderToday(Analyst))
                (await r.Html()).Should().NotContain("tide-feeds-nudge", "it's 9:00, before the 9:30 cutoff, which takes effect at once");
            await Save(OperationalSettings.PulledBy, "08:00");

            app.Clock.Now = new DateTimeOffset(2026, 11, 7, 15, 0, 0, TimeSpan.Zero);   // Saturday
            await using (var r = await RenderToday(Analyst))
                (await r.Html()).Should().NotContain("tide-feeds-nudge", "nobody is nagged on a weekend");
        }
        finally
        {
            app.Clock.Now = was;
            await Save(OperationalSettings.IntakeEnabled, "true");
            await Save(OperationalSettings.PulledBy, "08:00");
        }
    }

    [Fact]
    public async Task SourcesTab_HasPullAll_WhichPullsWhatsOutsideTheWindow_AndSaysSo()
    {
        await Source("Tab stale feed", TimeSpan.FromHours(15));
        app.Http.Set(Feed("Tab stale feed"), Rss("tabstale"));
        await using var r = await RenderAsync<Triage>(app, "/triage?tab=sources", @as: Analyst);
        var html = await r.HtmlOnce("Tab stale feed");
        html.Should().Contain("class=\"tide-pull-head").And.Contain(" Pull all</button>")
            .And.Contain("one at a time");

        await r.Click(t => t.PullAll());

        var toasts = r.Scope.ServiceProvider.GetRequiredService<ToastService>().Toasts;
        toasts.Should().Contain(t => t.Message.StartsWith("Pulled ", StringComparison.Ordinal) && t.Message.Contains("new item", StringComparison.Ordinal));
        (await r.Html()).Should().Contain("role=\"status\"").And.Contain("Pulled ");
        app.Services.GetRequiredService<SourcePollGuard>().AllRunning.Should().BeFalse();
    }

    [Fact]
    public async Task PullAll_WhileAnotherRunIsUnderWay_IsRefusedAndSaysWhy()
    {
        await Source("Busy run feed", TimeSpan.FromHours(15));
        var guard = app.Services.GetRequiredService<SourcePollGuard>();
        guard.TryStartAll().Should().BeTrue("a colleague's Pull all is under way");
        try
        {
            await using var r = await RenderAsync<Triage>(app, "/triage?tab=sources", @as: Analyst);
            System.Net.WebUtility.HtmlDecode(await r.HtmlOnce("Busy run feed")).Should().Contain("Pulling every source…");
            await r.Click(t => t.PullAll());
            r.Scope.ServiceProvider.GetRequiredService<ToastService>().Toasts.Should()
                .Contain(t => t.Message.Contains("already pulling every source", StringComparison.Ordinal));
        }
        finally { guard.AllFinished(); }
    }

    [Fact]
    public async Task Sources_AdministratorSetsWhenFeedsShouldBePulledBy()
    {
        await using var r = await RenderAsync<SourcesAdmin>(app, "/admin/sources", @as: Admin);
        var html = await r.Html();
        html.Should().Contain("id=\"intake-pulled-by\"").And.Contain("type=\"time\"").And.Contain("value=\"08:00\"");
        try
        {
            r.Component.PulledBy = new TimeOnly(7, 45);
            await r.Click(c => c.SaveIntake());
            app.Services.GetRequiredService<IOptionsMonitor<IntakeOptions>>().CurrentValue.PulledByTime.Should().Be(new TimeOnly(7, 45));
        }
        finally { await Save(OperationalSettings.PulledBy, "08:00"); }
    }

    // The page's markup, decoded, once it holds what's asked (up to 15 seconds), else as it is.
    private static async Task<string> HtmlWhen<T>(Rendered<T> r, Func<string, bool> holds)
    {
        var html = "";
        for (var i = 0; i < 150; i++)
        {
            html = System.Net.WebUtility.HtmlDecode(await r.Html());
            if (holds(html)) return html;
            await Task.Delay(100);
        }
        return html;
    }

    private static string RowOf(string html, string name)
    {
        var at = html.IndexOf($"<td class=\"fw-semibold\">{name}", StringComparison.Ordinal);
        at.Should().BeGreaterThan(0, name + " is listed");
        return html[at..html.IndexOf("</tr>", at, StringComparison.Ordinal)];
    }

    [Fact]
    public async Task APageWatchingSomeoneElsesRun_LearnsWhenItEnds_OnTheSourcesTabAndOnToday()
    {
        var id = await Source("Watched feed", TimeSpan.FromHours(15));
        var guard = app.Services.GetRequiredService<SourcePollGuard>();
        // A colleague's Pull all is under way, pulling this source, as these pages open (or are reloaded).
        guard.TryStartAll().Should().BeTrue();
        guard.TryStart(id).Should().BeTrue();
        await using var tab = await RenderAsync<Triage>(app, "/triage?tab=sources", @as: Analyst);
        await using var today = await RenderToday(Analyst);
        try
        {
            var html = await HtmlWhen(tab, h => h.Contains("Watched feed", StringComparison.Ordinal));
            html.Should().Contain("Pulling every source…");
            RowOf(html, "Watched feed").Should().Contain("Pulling…");
            System.Net.WebUtility.HtmlDecode(await today.Html()).Should().Contain("Pulling every source…");
        }
        finally
        {
            // Ended from the tab's own thread, signed in, as a circuit's reads are (the harness has no circuit to carry the
            // user to a read the end starts).
            await tab.Click(_ =>
            {
                guard.Finished(id);
                guard.AllFinished();
                return Task.CompletedTask;
            });
        }

        var after = await HtmlWhen(tab, h => !h.Contains("Pulling", StringComparison.Ordinal));
        after.Should().NotContain("Pulling every source…", "the run's end is heard").And.NotContain("Pulling…");
        RowOf(after, "Watched feed").Should().Contain(" Pull now</button>");
        (await HtmlWhen(today, h => !h.Contains("Pulling every source", StringComparison.Ordinal))).Should().NotContain("Pulling every source…")
            .And.MatchRegex("<button type=\"button\" class=\"btn btn-outline-primary btn-sm\" title=\"[^\"]*\">\\s*<span class=\"bi bi-arrow-repeat\"", "Pull all is pressable again");
    }

    [Theory]
    [InlineData("")]
    [InlineData("25:00")]
    [InlineData("abc")]
    [InlineData("8:00 am")]
    public async Task Sources_AFeedsPulledByThatIsntATime_IsRefusedOnTheField_AndNothingIsSaid(string typed)
    {
        await using var r = await RenderAsync<SourcesAdmin>(app, "/admin/sources", @as: Admin);
        var toasts = r.Scope.ServiceProvider.GetRequiredService<ToastService>();

        await r.Click(c => c.PulledByBox!.OnChange(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = typed }));
        await r.Click(c => c.SaveIntake());

        var html = System.Net.WebUtility.HtmlDecode(await r.Html());
        html.Should().Contain("id=\"intake-pulled-by-error\"").And.Contain("Feeds pulled by is a 24-hour time such as 08:00.")
            .And.MatchRegex("id=\"intake-pulled-by\"[^>]*aria-invalid=\"true\"|aria-invalid=\"true\"[^>]*id=\"intake-pulled-by\"");
        toasts.Toasts.Should().NotContain(t => !t.IsError, "nothing was saved, so nothing says it was");
        app.Services.GetRequiredService<IOptionsMonitor<IntakeOptions>>().CurrentValue.PulledByTime.Should().Be(new TimeOnly(8, 0));

        // Put right, it saves, and the message goes.
        await r.Click(c => c.PulledByBox!.OnChange(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = "08:00" }));
        await r.Click(c => c.SaveIntake());
        (await r.Html()).Should().NotContain("intake-pulled-by-error");
        toasts.Toasts.Should().Contain(t => !t.IsError);
    }
}

/// <summary>Pull all on the Sources tab with polling off: offered, and the tab says sources are only pulled by hand.</summary>
public class PullAllPollingOffTests(PullApp app) : IClassFixture<PullApp>
{
    [Fact]
    public async Task SourcesTab_WithPollingOff_OffersPullAll_AndSaysSourcesArePulledOnlyByHand()
    {
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Tide.Infrastructure.Persistence.AppDbContext>();
            db.Sources.Add(new Source { Name = "Off feed", Url = "https://off.example.com/feed", PollMinutes = 60 });
            await db.SaveChangesAsync();
        }
        await using var r = await RenderAsync<Triage>(app, "/triage?tab=sources", @as: new SignIn("dev:off-analyst", "Off Analyst", "Analyst"));
        (await r.HtmlOnce("Off feed")).Should().Contain("Scheduled polling is off, so sources are only pulled by hand").And.Contain(" Pull all</button>");
    }
}

/// <summary>Today's word on the feeds, counted: an app of its own, so no other test's feeds are among them.</summary>
public partial class FeedsTodayCountTests(FeedsApp app) : IClassFixture<FeedsApp>
{
    private static readonly SignIn Analyst = new("dev:feeds-analyst", "Feeds Analyst", "Analyst");
    private static readonly SignIn Owner = new("dev:feeds-owner", "Feeds Owner", "ActionOwner");
    private static readonly SignIn Viewer = new("dev:feeds-viewer", "Feeds Viewer", "Viewer");
    private static readonly SignIn Admin = new("dev:feeds-admin", "Feeds Admin", "Admin");

    private DateOnly Today => app.Services.GetRequiredService<OrgClock>().CurrentBriefDate;

    private Task<Rendered<BriefPage>> RenderToday(SignIn who) =>
        RenderAsync<BriefPage>(app, "/", new() { ["Day"] = $"{Today:yyyy-MM-dd}", ["AsToday"] = true }, who);

    // A feed the scheduled pass leaves alone (its interval is a week), last pulled that long before the clock.
    private async Task<Guid> Source(string name, TimeSpan ago, string? error = null)
    {
        await using var scope = app.Services.CreateAsyncScope();
        await using var db = await scope.ServiceProvider.GetRequiredService<IAppDbContextFactory>().CreateAsync();
        var at = app.Clock.Now - ago;
        var s = new Source
        {
            Name = name, Url = $"https://{name.Replace(' ', '-').ToLowerInvariant()}.example.com/feed", PollMinutes = 7 * 24 * 60,
            LastPolledAtUtc = at, LastSuccessAtUtc = error is null ? at : null, LastError = error,
        };
        db.Sources.Add(s);
        await db.SaveChangesAsync();
        return s.Id;
    }

    private static string Feed(string name) => $"https://{name.Replace(' ', '-').ToLowerInvariant()}.example.com/feed";

    private static string Rss(string key) =>
        $"<rss version=\"2.0\"><channel><title>N</title><item><title>{key} breaking story about a breach</title><link>https://{key}.example.com/1</link></item></channel></rss>";

    private async Task Save(string key, string value)
    {
        await using var scope = SignedIn(app, SignIn.Analyst);
        await scope.ServiceProvider.GetRequiredService<OperationalSettingsService>().SaveAsync(new Dictionary<string, string?> { [key] = value });
    }

    private static int NewInFeed(string html) =>
        int.Parse(NewInTheFeed().Match(html).Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task Today_SaysWhichFeedsArentPulledSinceTheCutoff_ToAnAnalyst_AndPullAllPullsThem()
    {
        // No feeds polled yet: nothing is said.
        await using (var r = await RenderToday(Analyst))
            (await r.Html()).Should().Contain("tide-steps").And.NotContain("tide-feeds-nudge");

        await Source("Stale feed", TimeSpan.FromHours(15));
        await Source("Fresh feed", TimeSpan.FromMinutes(10));
        var broken = await Source("Broken feed", TimeSpan.FromMinutes(40), "Timed out.");
        app.Http.Set(Feed("Stale feed"), Rss("stale"));
        app.Http.Set(Feed("Broken feed"), Rss("broken"));

        await using var menu = await RenderAsync<Tide.Web.Components.Layout.NavMenu>(app, "/", @as: Analyst);
        await using (var r = await RenderToday(Analyst))
        {
            var html = System.Net.WebUtility.HtmlDecode(await r.Html());
            html.Should().Contain("class=\"tide-feeds-nudge\"")
                .And.Contain("2 of 3 feeds not pulled since 8:00 AM (1 failing) · last pull 8:50 AM")
                .And.Contain(">Triage<", "it's Triage's word, beside its step")
                .And.Contain(" Pull all</button>");
            html.IndexOf("tide-feeds-nudge", StringComparison.Ordinal).Should().BeGreaterThan(html.IndexOf("tide-steps", StringComparison.Ordinal))
                .And.BeLessThan(html.IndexOf("running-order-h", StringComparison.Ordinal), "under the steps, above the running order");
            var before = NewInFeed(html);
            (await menu.Html()).Should().Contain($"Triage, {before} new");

            await r.Click(t => t.Freshness!.PullAll!.RunAsync());

            r.Scope.ServiceProvider.GetRequiredService<ToastService>().Toasts.Should()
                .Contain(t => t.Message == "Pulled 2 · 2 new items · 1 skipped (pulled in the last 15 min)" && !t.IsError && !t.IsWarning);
            (await r.HtmlOnce("tide-steps")).Should().NotContain("tide-feeds-nudge", "every feed has been pulled since 8:00 now");
            (await r.HtmlOnce($"{before + 2} new in the feed")).Should().Contain($"{before + 2} new in the feed", "Triage's step counts the new items at once");
            (await menu.HtmlOnce($"Triage, {before + 2} new")).Should().Contain($"Triage, {before + 2} new", "and so does the sidebar");
        }
        app.Http.Requests.Select(q => q.Url).Should().Contain([Feed("Stale feed"), Feed("Broken feed")]).And.NotContain(Feed("Fresh feed"));

        // A feed that failed after the cutoff, everything else pulled: named, with the Sources tab, and nothing to pull.
        await using (var scope = app.Services.CreateAsyncScope())
        await using (var db = await scope.ServiceProvider.GetRequiredService<IAppDbContextFactory>().CreateAsync())
            await db.Sources.Where(x => x.Id == broken).ExecuteUpdateAsync(u => u.SetProperty(x => x.LastError, "The server answered 503."));
        await using (var r = await RenderToday(Analyst))
        {
            var html = System.Net.WebUtility.HtmlDecode(await r.Html());
            html.Should().Contain("1 feed failing: Broken feed").And.Contain("href=\"triage?tab=sources\">see Sources</a>")
                .And.NotContain("Pull all</button>", "pulling again now would only be refused");
        }
    }

    [GeneratedRegex(@"(\d+) new in the feed")]
    private static partial Regex NewInTheFeed();
}

/// <summary>Pull all when the browser's connection drops, and its toast when some sources fail: an app of their own.</summary>
public class PullAllRunTests(FeedsApp app) : IClassFixture<FeedsApp>
{
    private static readonly SignIn Analyst = new("dev:run-analyst", "Run Analyst", "Analyst");

    private async Task Source(string name, bool canned = true)
    {
        await using (var scope = app.Services.CreateAsyncScope())
        await using (var db = await scope.ServiceProvider.GetRequiredService<IAppDbContextFactory>().CreateAsync())
        {
            var at = app.Clock.Now.AddHours(-15);
            db.Sources.Add(new Source { Name = name, Url = Feed(name), PollMinutes = 7 * 24 * 60, LastPolledAtUtc = at, LastSuccessAtUtc = at });
            await db.SaveChangesAsync();
        }
        if (canned)
            app.Http.Set(Feed(name), $"<rss version=\"2.0\"><channel><title>N</title><item><title>{name} story</title><link>{Feed(name)}/1</link></item></channel></rss>");
    }

    private static string Feed(string name) => $"https://{name.Replace(' ', '-').ToLowerInvariant()}.example.com/feed";

    [Fact]
    public async Task PullAll_StopsWhenTheBrowsersConnectionDrops_AfterTheSourceUnderWay()
    {
        await Source("Dropped alpha");
        await Source("Dropped beta");
        await using var r = await RenderAsync<Triage>(app, "/triage?tab=sources", @as: Analyst);
        await r.HtmlOnce("Dropped beta");
        var connection = r.Scope.ServiceProvider.GetRequiredService<CircuitConnection>();
        var before = app.Http.Requests.Count;
        // The browser vanishes while the first source is being fetched.
        app.Http.During = () => connection.OnConnectionDownAsync(null!, CancellationToken.None);

        await r.Click(t => t.PullAll());

        app.Http.Requests.Count.Should().Be(before + 1, "the source under way is finished, and no other is started");
        app.Services.GetRequiredService<SourcePollGuard>().AllRunning.Should().BeFalse();

        await connection.OnConnectionUpAsync(null!, CancellationToken.None);
        await r.Click(t => t.PullAll());
        app.Http.Requests.Count.Should().Be(before + 2, "back again, a new run takes the one left");
    }

    [Fact]
    public async Task PullAll_SomeFailing_WarnsRatherThanErrs()
    {
        await Source("Tone fine");
        await Source("Tone broken", canned: false);
        await using var r = await RenderAsync<Triage>(app, "/triage?tab=sources", @as: Analyst);
        await r.HtmlOnce("Tone broken");

        await r.Click(t => t.PullAll());

        r.Scope.ServiceProvider.GetRequiredService<ToastService>().Toasts.Should()
            .Contain(t => t.Message.Contains("1 failed: Tone broken", StringComparison.Ordinal) && t.IsWarning && !t.IsError);
    }
}
