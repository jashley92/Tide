using System.Text.RegularExpressions;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Tide.Application.Articles;
using Tide.Application.Briefs;
using Tide.Application.Org;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Infrastructure.Persistence;
using Tide.Web.Components.Layout;
using Tide.Web.Services;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>The app with its SQL commands counted, and its background jobs off so only what a test does is counted.</summary>
public sealed class CountingApp : WeekdayApp
{
    public TestDb.CommandLog Commands { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(s =>
        {
            // Every one of the app's own, wherever it lives: the webhook sender (in the API's namespace) reads the job lease
            // and its due events once a minute, and on a loaded machine those reads landed inside a page's count.
            foreach (var job in s.Where(d => d.ServiceType == typeof(IHostedService)
                         && d.ImplementationType?.Assembly == typeof(LiveMeetings).Assembly).ToList())
                s.Remove(job);
            s.ConfigureDbContext<AppDbContext>(o => o.AddInterceptors(Commands));
        });
    }
}

/// <summary>What the brief chip tests share: today's brief, changed as a colleague's save would change it, and the chip
/// as a page draws it.</summary>
internal static partial class BriefChipPage
{
    public static DateOnly Day(TideApp app) => app.Services.GetRequiredService<OrgClock>().CurrentBriefDate;

    /// <summary>When the chip says the brief meets ("10:30 AM", with the day in front on a day off).</summary>
    public static string Meets(TideApp app) => BriefChipView.MeetsAt(app.Services.GetRequiredService<OrgClock>(), Day(app));

    /// <summary>Changes today's brief through the app's own context, so the change feed hears it. Returns its id.</summary>
    public static async Task<Guid> Change(TideApp app, Action<Brief> change)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var day = Day(app);
        var brief = await db.Briefs.Include(b => b.Items).SingleAsync(b => b.BriefDate == day);
        change(brief);
        await db.SaveChangesAsync();
        return brief.Id;
    }

    /// <summary>The chip in <paramref name="html"/> (the link, whole), or null when the page has none.</summary>
    public static string? In(string html) => Chip().Match(html) is { Success: true } m ? m.Value : null;

    /// <summary>The chip on a page fetched as the app's signed-in person.</summary>
    public static async Task<string?> On(TideApp app, string path = "/articles") => In(await app.CreateClient().GetStringAsync(path));

    /// <summary>The chip's wording in a wide window, as the markup has it.</summary>
    public static string Full(string text) => $"<span class=\"tide-brief-chip-full\">{Markup(text)}</span>";

    /// <summary>The same in a narrow window.</summary>
    public static string Short(string text) => $"<span class=\"tide-brief-chip-short\">{Markup(text)}</span>";

    /// <summary>The link's accessible name, the same at every width.</summary>
    public static string Named(string text) => $" aria-label=\"{Markup(text)}\"";

    /// <summary>Text as the renderer writes it (the middle dot as a character reference).</summary>
    public static string Markup(string text) => System.Text.Encodings.Web.HtmlEncoder.Default.Encode(text);

    [GeneratedRegex("<a class=\"tide-brief-chip[^>]*>.*?</a>", RegexOptions.Singleline)]
    private static partial Regex Chip();
}

/// <summary>
/// The brief chip in the top bar: when today's brief meets and which of the five steps it's on, read off the same steps
/// as Today's strip, as a link to Today. The sample data has today's brief as a draft with three articles.
/// </summary>
public partial class BriefChipTests(WeekdayApp app) : IClassFixture<WeekdayApp>
{
    private string Meets => BriefChipPage.Meets(app);

    [Fact]
    public async Task ADraft_IsOnWriteOrOrder_AsTodaysStripHasIt_AndTheChipLinksToToday()
    {
        bool needsWork;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var brief = await BriefService.Load(db.Briefs.AsNoTracking()).SingleAsync(b => b.BriefDate == BriefChipPage.Day(app));
            needsWork = brief.Items.Any(i => ArticleChecks.For(i.Article!).Any(c => c.Blocking));
        }
        await BriefChipPage.Change(app, b => b.Status = BriefStatus.Draft);

        var html = await app.CreateClient().GetStringAsync("/articles");
        var chip = BriefChipPage.In(html);

        chip.Should().NotBeNull("an analyst sees the chip on every page");
        chip.Should().StartWith("<a class=\"tide-brief-chip\" href ", "one click returns to Today, the app's start page");
        chip.Should().Contain(BriefChipPage.Full(needsWork ? $"Brief {Meets} · step 2 of 5 · Write" : $"Brief {Meets} · step 3 of 5 · Order"))
            .And.Contain(BriefChipPage.Short(needsWork ? $"{Meets} · 2/5" : $"{Meets} · 3/5"))
            .And.Contain(BriefChipPage.Named(needsWork ? $"Brief at {Meets}, step 2 of 5, Write" : $"Brief at {Meets}, step 3 of 5, Order"),
                "the short text alone says nothing to someone who hears only the link's name");
        chip.Should().NotContain("aria-current", "Articles isn't Today");
        html.IndexOf("tide-brief-chip", StringComparison.Ordinal).Should()
            .BeGreaterThan(html.IndexOf("class=\"top-row\"", StringComparison.Ordinal))
            .And.BeLessThan(html.IndexOf("id=\"main-content\"", StringComparison.Ordinal), "the chip is in the top bar");

        // The step Today's own strip marks as current is the one the chip names.
        var today = await app.CreateClient().GetStringAsync("/");
        var current = CurrentStep().Match(today).Value;
        current.Should().Contain(needsWork ? ">Write<" : ">Order<");
        BriefChipPage.In(today).Should().Contain(" aria-current=\"page\"", "on Today the chip is the current page's link, as the sidebar's is");
        BriefChipPage.In(await app.CreateClient().GetStringAsync("/?tab=distribute")).Should().Contain(" aria-current=\"page\"");
    }

    [Fact]
    public void OnADayOff_TheNarrowChipNamesTheDay_NotTheTime_SoItIsNoLongerThanAWorkingDays()
    {
        // A weekend: Today shows Monday's brief. The wide chip and its name say the day and the time.
        var step = new BriefStep(BriefStepKind.Order, "Order", "Not marked ready", StepState.Current);
        var monday = new BriefChipView(new DateOnly(2026, 11, 16), Guid.NewGuid(), "Mon 10:30 AM", 3, 5, step);
        monday.Short.Should().Be("Mon · 3/5");
        monday.Full.Should().Be("Brief Mon 10:30 AM · step 3 of 5 · Order");
        monday.Name.Should().Be("Brief at Mon 10:30 AM, step 3 of 5, Order");
        (monday with { Meets = "10:30 AM" }).Short.Should().Be("10:30 AM · 3/5", "on the brief's own day it's the time");
        (monday with { Step = null }).Short.Should().Be("Mon 10:30 AM", "not started, the chip has no step to give room to");
    }

    [Fact]
    public async Task ABriefMarkedReady_IsOnPresent_AndOnePresented_IsOnSend()
    {
        await BriefChipPage.Change(app, b => b.Status = BriefStatus.Ready);
        (await BriefChipPage.On(app)).Should().Contain(BriefChipPage.Full($"Brief {Meets} · step 4 of 5 · Present"))
            .And.Contain(BriefChipPage.Short($"{Meets} · 4/5")).And.Contain(BriefChipPage.Named($"Brief at {Meets}, step 4 of 5, Present"));
    }

    [Fact]
    public async Task OnceTheBriefHasMet_TheChipDropsTheTime_AndOnceSent_SaysWhatTodaysStripSays()
    {
        // The meeting is over: when it was to meet is no longer news (Today says when it met).
        await BriefChipPage.Change(app, b => b.Status = BriefStatus.Presented);
        var presented = await BriefChipPage.On(app, "/actions");
        presented.Should().Contain(BriefChipPage.Full("Brief presented · step 5 of 5 · Send")).And.Contain(BriefChipPage.Short("Presented · 5/5"))
            .And.Contain(BriefChipPage.Named("Brief presented, step 5 of 5, Send"));
        presented.Should().NotContain(Meets);

        await BriefChipPage.Change(app, b => b.Status = BriefStatus.Sent);
        var sent = await BriefChipPage.On(app);
        sent.Should().Contain(BriefChipPage.Full("Brief · Sent")).And.Contain(BriefChipPage.Short("Sent")).And.Contain(BriefChipPage.Named("Brief, Sent"));
        sent.Should().NotContain(Meets).And.NotContain("step");
        // Today's strip, for the same brief: its last step, done, says the same word.
        var today = await app.CreateClient().GetStringAsync("/");
        today.Should().Contain("<span class=\"tide-step-detail\" id=\"step-send-detail\" title=\"Sent\">Sent</span>");
    }

    [Fact]
    public async Task OneOfTheTwoEditionsSent_TodaysHeaderTheChipAndTheStripAllSay1Of2Sent_AndSentOnlyOnceBothHaveGone()
    {
        // A real send of the team edition makes the brief Sent; the company-wide edition (the sample brief has articles
        // below its ceiling) is still to go.
        var briefId = await BriefChipPage.Change(app, b => b.Status = BriefStatus.Sent);
        var team = await AddSend(briefId, Edition.Team);
        Guid? company = null;
        try
        {
            var chip = await BriefChipPage.On(app);
            chip.Should().Contain(BriefChipPage.Full("Brief · 1 of 2 sent")).And.Contain(BriefChipPage.Short("1 of 2 sent"))
                .And.Contain(BriefChipPage.Named("Brief, 1 of 2 sent"));

            var today = await app.CreateClient().GetStringAsync("/");
            BriefChipPage.In(today).Should().Contain(BriefChipPage.Full("Brief · 1 of 2 sent"));
            today.Should().Contain("<span class=\"tide-step-detail\" id=\"step-send-detail\" title=\"1 of 2 sent\">1 of 2 sent</span>", "the strip says it");
            today.Should().MatchRegex("<span class=\"tide-badge[^\"]*\">1 of 2 sent</span>", "the header's status says it");
            today.Should().NotMatchRegex("<span class=\"tide-badge[^\"]*\">Sent</span>")
                .And.NotContain("This brief has been sent.")
                .And.Contain("Only the security team edition has been sent", "the note under the header says which went");

            company = await AddSend(briefId, Edition.Public);
            (await BriefChipPage.On(app)).Should().Contain(BriefChipPage.Full("Brief · Sent"));
            today = await app.CreateClient().GetStringAsync("/");
            today.Should().MatchRegex("<span class=\"tide-badge[^\"]*\">Sent</span>").And.Contain("This brief has been sent.")
                .And.NotContain("1 of 2 sent");
        }
        finally
        {
            await using var scope = app.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.BriefSends.Where(s => s.Id == team || s.Id == company).ExecuteDeleteAsync();
        }
    }

    // A real send of edition, recorded as the send log has it, published as the brief's change so the chip re-reads.
    private async Task<Guid> AddSend(Guid briefId, Edition edition)
    {
        var send = new BriefSend
        {
            BriefId = briefId, BriefDate = BriefChipPage.Day(app), Edition = edition, Status = SendStatus.Sent, Subject = "Brief",
            SentAtUtc = DateTimeOffset.UtcNow, SentById = "dev:someone", SentByName = "Someone",
        };
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.BriefSends.Add(send);
            await db.SaveChangesAsync();
        }
        await BriefChipPage.Change(app, b => b.EditVersion = Guid.NewGuid());
        return send.Id;
    }

    [Fact]
    public async Task WhileTheMeetingIsLive_TheChipSaysSo_AndLeavesJoiningToTheBanner()
    {
        var briefId = await BriefChipPage.Change(app, b => b.Status = BriefStatus.Ready);
        var meetings = app.Services.GetRequiredService<LiveMeetings>();
        var seat = meetings.Start(BriefChipPage.Day(app), briefId, "dev:someone", "Someone", "c-someone", Guid.NewGuid(), 0);
        try
        {
            var html = await app.CreateClient().GetStringAsync("/articles");
            var chip = BriefChipPage.In(html);

            chip.Should().StartWith("<a class=\"tide-brief-chip is-live\" href ");
            chip.Should().Contain(BriefChipPage.Full("Brief is live")).And.Contain(BriefChipPage.Short("Live")).And.Contain(BriefChipPage.Named("Brief is live"));
            chip.Should().NotContain("step").And.NotContain("Join", "the banner below the top bar is where you join");
            html.Should().Contain("Brief meeting is live");
        }
        finally { meetings.End(seat); }

        (await BriefChipPage.On(app)).Should().Contain("step 4 of 5", "the meeting over, the chip is back to the step");
    }

    [Fact]
    public async Task AMeetingStartedOnADraft_ReadsAsLive_AndEndedUnready_TheChipIsBackOnTheStripsStep()
    {
        // Presenting doesn't wait for Mark ready. Live, the chip names no step, so it can't disagree with the strip,
        // which still has the draft's; the meeting left without the brief marked ready, both name that step again.
        var briefId = await BriefChipPage.Change(app, b => b.Status = BriefStatus.Draft);
        var meetings = app.Services.GetRequiredService<LiveMeetings>();
        var seat = meetings.Start(BriefChipPage.Day(app), briefId, "dev:someone", "Someone", "c-someone", Guid.NewGuid(), 0);
        try { (await BriefChipPage.On(app)).Should().Contain(BriefChipPage.Full("Brief is live")).And.NotContain("step"); }
        finally { meetings.End(seat); }

        var today = await app.CreateClient().GetStringAsync("/");
        var current = CurrentStepLabel().Match(today).Groups[1].Value;
        var step = current.Contains(">Write<", StringComparison.Ordinal) ? "step 2 of 5 · Write" : "step 3 of 5 · Order";
        current.Should().MatchRegex(">(Write|Order)<", "a draft is on Write or Order");
        BriefChipPage.In(today).Should().Contain(BriefChipPage.Full($"Brief {Meets} · {step}"));
    }

    [Fact]
    public async Task PresentModesLayout_HasNoChip()
    {
        await using var page = await RenderAsync<PageIn<BlankLayout>>(app, "/present/2026-10-29");
        (await page.Html()).Should().Contain("<h1>A page</h1>").And.NotContain("tide-brief-chip");
    }

    [Theory]
    [InlineData("Reader")]
    [InlineData("Visitor")]
    [InlineData("ActionOwner")]
    [InlineData("Viewer")]
    public async Task SomeoneWhoDoesNotMakeTheBrief_GetsNoChip(string role)
    {
        await using var page = await RenderAsync<PageIn<MainLayout>>(app, "/published", @as: new SignIn("dev:chip-" + role, role + " Person", role));
        var html = await page.Html();
        html.Should().Contain("class=\"top-row\"").And.Contain("<h1>A page</h1>");
        html.Should().NotContain("tide-brief-chip", $"a {role} doesn't read the team edition and write the brief");
    }

    [GeneratedRegex("<li class=\"tide-step is-current\" aria-current=\"step\">.*?<span class=\"tide-step-label\">(.*?)</span>", RegexOptions.Singleline)]
    private static partial Regex CurrentStepLabel();

    [GeneratedRegex("<li class=\"tide-step is-current\" aria-current=\"step\">.*?</li>", RegexOptions.Singleline)]
    private static partial Regex CurrentStep();
}

public class BriefChipReaderTests(ReaderApp app) : IClassFixture<ReaderApp>
{
    [Fact]
    public async Task AReadersPages_HaveNoChip()
    {
        var html = await app.CreateClient().GetStringAsync("/published");
        html.Should().Contain("class=\"top-row\"").And.NotContain("tide-brief-chip");
    }
}

public class BriefChipNotStartedTests(TideApp app) : IClassFixture<TideApp>
{
    [Fact]
    public async Task WithNoBriefYet_TheChipSaysNotStarted_AsTodayDoes()
    {
        // Today's brief becomes a long-gone day's: today has none.
        await BriefChipPage.Change(app, b => b.BriefDate = BriefChipPage.Day(app).AddYears(-3));

        var chip = await BriefChipPage.On(app);

        var meets = BriefChipPage.Meets(app);
        chip.Should().Contain(BriefChipPage.Full($"Brief {meets} · not started")).And.Contain(BriefChipPage.Short(meets))
            .And.Contain(BriefChipPage.Named($"Brief at {meets}, not started"));
        (await app.CreateClient().GetStringAsync("/")).Should().Contain("brief yet.").And.NotContain("aria-label=\"Brief progress\"");
    }
}

/// <summary>The chip costs nothing on a navigation: the top bar is drawn on every page, and what the chip says is read
/// once per circuit and kept until the change feed says the brief changed.</summary>
public class BriefChipCostTests(CountingApp app) : IClassFixture<CountingApp>
{
    private static async Task<string> Draw<T>(IServiceProvider circuit) where T : IComponent
    {
        await using var renderer = new HtmlRenderer(circuit, circuit.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.RenderComponentAsync<T>();
            await root.QuiescenceTask;
            return root.ToHtmlString();
        });
    }

    // One scope, as a circuit is, with the address set as the circuit sets it.
    private AsyncServiceScope Circuit()
    {
        var scope = SignedIn(app, SignIn.Analyst);
        if (scope.ServiceProvider.GetRequiredService<NavigationManager>() is IHostEnvironmentNavigationManager nav)
            nav.Initialize("http://localhost/", "http://localhost/articles");
        return scope;
    }

    [Fact]
    public async Task DrawingTheChipAgain_ReadsNothing_UntilTheBriefChanges()
    {
        await BriefChipPage.Change(app, b => b.Status = BriefStatus.Draft);
        await using var circuit = Circuit();

        app.Commands.Reset();
        var first = await Draw<BriefChip>(circuit.ServiceProvider);
        first.Should().Contain("tide-brief-chip");
        app.Commands.Count.Should().BePositive("the first draw reads the brief");

        app.Commands.Reset();
        var second = await Draw<BriefChip>(circuit.ServiceProvider);
        second.Should().Be(first);
        app.Commands.Count.Should().Be(0, "what the chip says is kept for the circuit");

        // A colleague marks the brief ready: the change feed says so, and the next draw reads it again.
        await BriefChipPage.Change(app, b => b.Status = BriefStatus.Ready);
        app.Commands.Reset();
        (await Draw<BriefChip>(circuit.ServiceProvider)).Should().Contain(BriefChipPage.Markup("step 4 of 5 · Present"));
        app.Commands.Count.Should().BePositive();

        // Something the chip doesn't show (another day's brief) leaves what's kept alone.
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Briefs.Add(new Brief { BriefDate = BriefChipPage.Day(app).AddYears(-2), AnalystId = "dev:tester", AnalystName = "Tester" });
            await db.SaveChangesAsync();
        }
        app.Commands.Reset();
        (await Draw<BriefChip>(circuit.ServiceProvider)).Should().Contain(BriefChipPage.Markup("step 4 of 5 · Present"));
        app.Commands.Count.Should().Be(0);
    }

    [Fact]
    public async Task AChangeThatLandsWhileTheFirstReadIsUnderWay_IsHeard()
    {
        await BriefChipPage.Change(app, b => b.Status = BriefStatus.Draft);
        await using var circuit = Circuit();
        var source = circuit.ServiceProvider.GetRequiredService<BriefChipSource>();
        // A colleague marks the brief ready after the chip read it, but before the chip knew which brief to follow.
        source.AfterBriefRead = () =>
        {
            source.AfterBriefRead = null;
            return BriefChipPage.Change(app, b => b.Status = BriefStatus.Ready);
        };

        (await source.GetAsync())!.Full.Should().NotContain("Present", "the first read was of the draft");
        (await source.GetAsync())!.Full.Should().Contain("step 4 of 5 · Present", "the change it missed is read next time");
    }

    [Fact]
    public async Task DrawingTheWholeLayoutASecondTime_AddsNoCommandsForTheChip()
    {
        await using var circuit = Circuit();

        (await Draw<PageIn<MainLayout>>(circuit.ServiceProvider)).Should().Contain("tide-brief-chip");
        app.Commands.Record = true;
        app.Commands.Reset();
        (await Draw<PageIn<MainLayout>>(circuit.ServiceProvider)).Should().Contain("tide-brief-chip");
        var again = app.Commands.Sql;
        app.Commands.Record = false;

        again.Where(sql => sql.Contains("\"Briefs\"", StringComparison.Ordinal) || sql.Contains("\"BriefSends\"", StringComparison.Ordinal)
                || sql.Contains("\"Settings\"", StringComparison.Ordinal))
            .Should().BeEmpty("the second draw of the layout reads nothing for the chip");

    }
}
