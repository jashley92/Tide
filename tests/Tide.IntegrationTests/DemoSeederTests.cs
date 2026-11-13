using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Tide.Application.Stack;
using Tide.Domain.Enums;
using Tide.Infrastructure.Persistence;
using Tide.Web.Services;

namespace Tide.IntegrationTests;

/// <summary>The app with the screenshot tool's demo data (Demo:Seed), as tools/screenshots/capture.mjs starts it.</summary>
public class DemoTideApp : TideApp
{
    public string InfoFile { get; } = Path.Combine(Path.GetTempPath(), "tide-demo-" + Guid.NewGuid().ToString("N"), "demo.json");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Demo:Seed", "true");
        builder.UseSetting("Demo:InfoFile", InfoFile);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Directory.Delete(Path.GetDirectoryName(InfoFile)!, recursive: true); } catch (IOException) { /* best effort */ }
    }
}

public class DemoSeederTests(DemoTideApp app) : IClassFixture<DemoTideApp>
{
    private DemoSeeder.Seeded Info()
    {
        app.CreateClient().Dispose();   // starts the app, which seeds
        return JsonSerializer.Deserialize<DemoSeeder.Seeded>(File.ReadAllText(app.InfoFile))!;
    }

    private static DateOnly Day(string day) => DateOnly.Parse(day, System.Globalization.CultureInfo.InvariantCulture);

    private AppDbContext Db() => app.Services.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>();

    [Fact]
    public async Task EverySeededActionAndArticle_HasADefinedPriority()
    {
        Info();
        await using var db = Db();
        var actions = await db.Actions.AsNoTracking().Select(a => new { a.Number, a.Priority }).ToListAsync();
        actions.Should().NotBeEmpty().And.OnlyContain(a => Enum.IsDefined(a.Priority), "P1 to P4: no action shows a \"0\" badge");
        var articles = await db.Articles.AsNoTracking().Where(a => a.Priority != null).Select(a => a.Priority!.Value).ToListAsync();
        articles.Should().NotBeEmpty().And.OnlyContain(p => Enum.IsDefined(p), "an article's priority, which its actions take, is P1 to P4");
    }

    [Fact]
    public async Task SeededActions_AreDatedInTheirStatusAsTheirHistoryHasIt_NotWhenTheSeedRan()
    {
        Info();
        await using var db = Db();
        var actions = await db.Actions.AsNoTracking().Include(a => a.Events).ToListAsync();
        actions.Where(a => !a.IsOpen && a.ClosedAtUtc is not null).Should().NotBeEmpty()
            .And.OnlyContain(a => a.StatusChangedAtUtc == a.ClosedAtUtc, "a closed one: when it closed");
        DateTimeOffset LineAt(string title, string text) => actions.Single(a => a.IsOpen && a.Title == title).Events.Single(e => e.Text.StartsWith(text, StringComparison.Ordinal)).AtUtc;
        var byTitle = actions.Where(a => a.IsOpen).ToDictionary(a => a.Title, a => a.StatusChangedAtUtc);
        byTitle["Block the remote monitoring relay and alert on unapproved agents"].Should().Be(
            LineAt("Block the remote monitoring relay and alert on unapproved agents", "Status: Open → In progress"), "its move to In progress");
        byTitle["Confirm the vendor's patch window for the file transfer server"].Should().Be(
            LineAt("Confirm the vendor's patch window for the file transfer server", "Status: Open → Blocked"), "blocked three days ago");
        byTitle["Shorten session lifetime for unmanaged devices"].Should().Be(
            LineAt("Shorten session lifetime for unmanaged devices", "Raised from the brief"), "raised at the brief before yesterday's and never moved");
        actions.Where(a => a.IsOpen && a.Number > 2).Should().OnlyContain(a => a.StatusChangedAtUtc < DateTimeOffset.UtcNow.AddHours(-1),
            "every seeded open action was raised at an earlier brief, not as the seed ran");
    }

    [Fact]
    public async Task Seeds_yesterdays_brief_sent_in_both_editions_and_names_it()
    {
        var info = Info();
        var sent = Day(info.Sent);
        Day(info.Today).Should().BeAfter(sent);

        await using var db = Db();
        var brief = await db.Briefs.SingleAsync(b => b.BriefDate == sent);
        brief.Status.Should().Be(BriefStatus.Sent);
        var sends = await db.BriefSends.Where(s => s.BriefId == brief.Id && !s.IsTest).Select(s => s.Edition).ToListAsync();
        sends.Should().BeEquivalentTo([Edition.Team, Edition.Public]);
        (await db.ArticleScreenshots.CountAsync()).Should().Be(3);
    }

    [Fact]
    public async Task Seeds_today_as_the_day_the_Today_page_shows_with_its_brief_not_yet_presented()
    {
        var info = Info();
        var today = app.Services.GetRequiredService<Tide.Application.Org.OrgClock>().CurrentBriefDate;
        Day(info.Today).Should().Be(today, "Today and /present/<today> show the brief day it names");
        await using var db = Db();
        (await db.Briefs.SingleAsync(b => b.BriefDate == today)).Status.Should().NotBe(BriefStatus.Presented).And.NotBe(BriefStatus.Sent);
    }

    [Fact]
    public async Task A_database_seeded_for_an_earlier_day_isnt_started_on_as_if_it_were_today()
    {
        var info = Info();
        await using var db = Db();
        var marker = await db.Settings.SingleAsync(s => s.Key == "Demo:Seeded");
        marker.Value.Should().Be(info.Today, "the database says which day it was seeded for");
        var kept = marker.Value;
        marker.Value = "2020-01-02";
        await db.SaveChangesAsync();
        try
        {
            var start = () => DemoSeeder.SeedAsync(app.Services, app.Services.GetRequiredService<IConfiguration>(), NullLogger.Instance);
            (await start.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*2020-01-02*" + info.Today + "*fresh*");
        }
        finally
        {
            marker.Value = kept;
            await db.SaveChangesAsync();
        }
        FluentActions.Invoking(() => DemoSeeder.NotStale("1", Day(info.Today))).Should().NotThrow("a database seeded before it said its day");
    }

    [Fact]
    public async Task Each_seeded_action_was_created_when_its_history_starts()
    {
        var info = Info();
        await using var db = Db();
        var actions = await db.Actions.AsNoTracking().Include(a => a.Events).Where(a => a.Number > 2).ToListAsync();
        actions.Should().NotBeEmpty().And.OnlyContain(a => a.Events.All(e => e.AtUtc >= a.CreatedAtUtc), "nothing happened to it before it was made");
        var detail = actions.Single(a => a.Number == info.DetailAction);
        detail.CreatedAtUtc.Should().Be(detail.Events.Single(e => e.Text == "Raised from the brief.").AtUtc);
        app.Services.GetRequiredService<Tide.Application.Org.OrgClock>().DayOf(detail.CreatedAtUtc).Should().Be(Day(info.Sent), "raised at the sent brief's meeting");
    }

    [Fact]
    public async Task The_latest_briefs_dont_repeat_a_story()
    {
        Info();
        await using var db = Db();
        // The past briefs draw their stories from a pool; the latest few, as Recently presented lists them, are each a
        // story of its own.
        var latest = await db.BriefItems.Join(db.Briefs.Where(b => b.Status == BriefStatus.Sent), i => i.BriefId, b => b.Id, (i, b) => new { i, b })
            .OrderByDescending(x => x.b.BriefDate).ThenBy(x => x.i.SortOrder).Take(10).Select(x => x.i.Article!.Title).ToListAsync();
        latest.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task Seeds_history_metrics_actions_and_the_rest_the_pages_show()
    {
        var info = Info();
        await using var db = Db();
        (await db.Briefs.CountAsync(b => b.Status == BriefStatus.Sent)).Should().BeGreaterThan(50, "Trends needs months of briefs");
        (await db.MetricValues.CountAsync()).Should().BeGreaterThan(1000, "a year of six metrics");
        (await db.HuntQueries.SingleAsync()).Result.Should().Be(HuntResult.NoHits);
        (await db.Actions.SingleAsync(a => a.Number == info.DetailAction)).Status.Should().Be(ActionStatus.InProgress);
        (await db.Adversaries.CountAsync()).Should().Be(3);
        (await db.ApiTokens.CountAsync()).Should().Be(3);
        (await db.FeedItemTexts.CountAsync()).Should().Be(4, "each of the demo's own triage items keeps its text for the reading pane");
    }

    [Fact]
    public async Task Seeds_products_on_our_stack_in_each_state_the_page_shows()
    {
        var info = Info();
        await using var db = Db();
        var products = await db.StackProducts.ToListAsync();
        products.Should().HaveCount(5).And.OnlyContain(p => !p.IsRetired);

        var tagged = await db.ArticleProducts.SingleAsync(t => t.ArticleId == Guid.Parse(info.VpnArticle));
        tagged.ProductId.ToString().Should().Be(info.StackProduct, "the product the tool opens is the one on the edge VPN article");

        var entries = products.Select(StackService.Entry).ToList();
        var item = await db.FeedItems.SingleAsync(i => i.Cves == "CVE-2026-0003");
        StackService.Match(entries, item.Title, item.Excerpt)!.Name.Should().Be("File Transfer Server");

        var today = Day(info.Today);
        products.Should().ContainSingle(p => p.CheckedOn < today.AddYears(-1), "one hasn't been checked for over a year");
        products.Should().ContainSingle(p => p.TeamId == null, "one has no owning team");
    }

    [Fact]
    public async Task Seeds_a_metric_group_with_the_previous_briefs_numbers_and_one_missing_today()
    {
        var info = Info();
        var today = Day(info.Today);
        var sent = Day(info.Sent);
        await using var db = Db();
        var group = await db.MetricGroups.Include(g => g.Metrics).SingleAsync(g => g.Key == "exposed-services");
        var ids = group.Metrics.Select(m => m.Id).ToList();
        ids.Should().HaveCount(3);
        (await db.MetricValues.CountAsync(v => ids.Contains(v.DefinitionId) && v.BriefDate == sent)).Should().Be(3);
        (await db.MetricValues.CountAsync(v => ids.Contains(v.DefinitionId) && v.BriefDate == today)).Should().Be(2, "one is left to be entered");
    }

    [Fact]
    public async Task Seeds_an_escalated_and_an_overdue_action_still_open_from_an_earlier_brief()
    {
        var info = Info();
        var today = Day(info.Today);
        var sent = Day(info.Sent);
        await using var db = Db();
        var briefs = db.Briefs.Where(b => b.BriefDate < sent).Select(b => b.Id);
        var earlier = db.BriefItems.Where(i => briefs.Contains(i.BriefId)).Select(i => i.ArticleId);
        var open = (await db.Actions.Where(a => a.ArticleId != null && earlier.Contains(a.ArticleId!.Value)).ToListAsync()).Where(a => a.IsOpen).ToList();
        open.Should().HaveCount(2).And.OnlyContain(a => a.RaisedOn < sent && a.DueDate < today);
        open.Should().ContainSingle(a => a.IsEscalated);
        open.Select(a => a.ArticleId).Distinct().Should().ContainSingle("both came from one article");
    }

    [Fact]
    public async Task Seeds_a_second_person_who_can_follow_a_live_meeting()
    {
        var info = Info();
        await using var db = Db();
        var follower = await db.Users.SingleAsync(u => u.DisplayName == info.Follower);
        follower.Sid.Should().Be("dev:" + info.Follower.ToLowerInvariant(), "the dev sign-in's ?as= resolves to this row");
        follower.RolesCsv.Should().Be(nameof(AppRole.Viewer));

        using var client = app.CreateClient();
        var response = await client.GetAsync($"/present/{info.Today}?as={info.Follower}&roles={follower.RolesCsv}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Runs_once_per_database()
    {
        var info = Info();
        await DemoSeeder.SeedAsync(app.Services, app.Services.GetRequiredService<IConfiguration>(), NullLogger.Instance);
        await using var db = Db();
        (await db.BriefSends.CountAsync(s => !s.IsTest)).Should().Be(2);
        (await db.Adversaries.CountAsync()).Should().Be(3);
        info.HuntAction.Should().Be(1);
    }

    [Theory]
    [InlineData("/briefs/{sent}?tab=preview")]
    [InlineData("/published/{sent}")]
    [InlineData("/actions/{detail}")]
    [InlineData("/trends")]
    [InlineData("/stack")]
    [InlineData("/stack/{product}")]
    public async Task The_captured_pages_render(string route)
    {
        var info = Info();
        using var client = app.CreateClient();
        var response = await client.GetAsync(route.Replace("{sent}", info.Sent).Replace("{detail}", info.DetailAction.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Replace("{product}", info.StackProduct));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task TheCircuitWorkInHand_IsAnsweredForTheScreenshotTool()
    {
        using var response = await app.CreateClient().GetAsync("/demo/activity");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("started").GetInt64().Should().BeGreaterThanOrEqualTo(0);
        json.RootElement.GetProperty("inHand").GetInt32().Should().Be(0, "no circuit is open");
    }
}

/// <summary>Without Demo:Seed, the screenshot tool's /demo/activity isn't there, Development or not.</summary>
public class DemoActivityAbsentTests(TideApp app) : IClassFixture<TideApp>
{
    [Fact]
    public async Task TheCircuitWorkInHand_IsntAnsweredWithoutTheDemo()
    {
        using var response = await app.CreateClient().GetAsync("/demo/activity");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}

/// <summary>The circuit work the screenshot tool waits on: in hand from when it starts until it's done, and only while its
/// circuit's browser is connected.</summary>
public class DemoActivityCountTests
{
    [Fact]
    public async Task Work_IsInHand_UntilItsDone_AndEachStartCounts()
    {
        var activity = new DemoActivity();
        var release = new TaskCompletionSource();

        var work = activity.Track("c1", () => release.Task);
        activity.Counts().Should().Be((1L, 1));

        release.SetResult();
        await work;
        activity.Counts().Should().Be((1L, 0));
    }

    [Fact]
    public async Task Work_ThatFails_IsNoLongerInHand()
    {
        var activity = new DemoActivity();
        var act = () => activity.Track("c1", () => throw new InvalidOperationException("boom"));
        await act.Should().ThrowAsync<InvalidOperationException>();
        activity.Counts().Should().Be((1L, 0));
    }

    [Fact]
    public async Task A_DisconnectedCircuitsWork_IsntWaitedFor_UntilItsBack()
    {
        var activity = new DemoActivity();
        var release = new TaskCompletionSource();
        var work = activity.Track("c1", () => release.Task);
        var other = activity.Track("c2", () => release.Task);

        activity.Connection("c1", up: false);
        activity.Counts().InHand.Should().Be(1, "only the connected circuit's work counts");
        activity.Connection("c1", up: true);
        activity.Counts().InHand.Should().Be(2);

        activity.Connection("unknown", up: false);
        activity.Counts().InHand.Should().Be(2, "a circuit never seen changes nothing");

        release.SetResult();
        await Task.WhenAll(work, other);
    }

    [Fact]
    public void A_ClosedCircuit_IsForgotten()
    {
        var activity = new DemoActivity();
        _ = activity.Track("c1", () => new TaskCompletionSource().Task);
        activity.Connection("c2", up: true);

        activity.Closed("c1");
        activity.Counts().Should().Be((1L, 0));

        var now = JsonSerializer.Serialize(activity.Now());
        now.Should().Be("""{"started":1,"inHand":0}""");
    }
}

public class DemoActivityDoneTests
{
    [Fact]
    public async Task Done_RunsItsStep_AndCompletes()
    {
        var ran = false;
        await DemoActivity.Done(() => ran = true);
        ran.Should().BeTrue();
    }
}
