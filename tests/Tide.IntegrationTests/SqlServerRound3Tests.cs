using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Tide.Application.Abstractions;
using Tide.Application.Articles;
using Tide.Application.Briefs;
using Tide.Application.Intake;
using Tide.Application.Org;
using Tide.Application.Security;
using Tide.Application.Stack;
using Tide.Domain.Enums;
using Tide.Infrastructure.Persistence;
using Tide.TestSupport;
using Tide.Web.Components.Pages;
using Tide.Web.Services;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>Bug round 3 (SQL Server): Triage's sources in the same order on both providers.</summary>
public class SourceOrderRound3Tests
{
    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task TheSourcesTab_AndTheSourceList_SortByNameIgnoringCase_OnEitherProvider(string provider)
    {
        using var db = new TestDb(provider: provider);
        db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };
        var intake = db.Intake(new IntakeOptions { Enabled = true, MinPollMinutes = 5 }, new TestClock(DateTimeOffset.UtcNow));
        foreach (var name in new[] { "Zeta wire", "alpha blog", "Beta feed" })
            await intake.SaveSourceAsync(new SourceForm { Name = name, Kind = SourceKind.Feed, Url = $"https://news.example.com/{name.Replace(' ', '-')}.xml", PollMinutes = 30, Enabled = true });

        string[] expected = ["alpha blog", "Beta feed", "Zeta wire"];
        (await intake.PolledSourcesAsync()).Select(s => s.Name).Should().Equal(expected, "SQLite's own order put the capitals first");
        (await intake.SourcesAsync()).Select(s => s.Name).Should().Equal(expected, "the source filter beside it lists them the same way");
    }
}

/// <summary>
/// The app with a gate on its database: armed, the next context made waits until released, so a test can hold one page's
/// read open while the same page does something else, as a slower database (SQL Server over the network) leaves room for.
/// </summary>
public class GatedDatabaseTideApp : TideApp
{
    public sealed class Gate
    {
        private TaskCompletionSource? _armed;
        private TaskCompletionSource? _held;
        private TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Holds the next context made until <see cref="ReleaseHeld"/>; returns a task done once one is held.</summary>
        public Task Arm()
        {
            _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _armed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return _entered.Task;
        }

        internal Task Pass()
        {
            if (Interlocked.Exchange(ref _armed, null) is not { } armed) return Task.CompletedTask;
            _entered.TrySetResult();
            _held = armed;
            return armed.Task;
        }

        /// <summary>Lets the held context go on.</summary>
        public void ReleaseHeld() => _held?.TrySetResult();
    }

    private sealed class Gated(IDbContextFactory<AppDbContext> inner, Gate gate) : IAppDbContextFactory
    {
        public async Task<IAppDbContext> CreateAsync(CancellationToken ct = default)
        {
            await gate.Pass();
            return await inner.CreateDbContextAsync(ct);
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        Microsoft.AspNetCore.TestHost.WebHostBuilderExtensions.ConfigureTestServices(builder, services =>
        {
            services.AddSingleton<Gate>();
            services.RemoveAll<IAppDbContextFactory>();
            services.AddScoped<IAppDbContextFactory, Gated>();
        });
    }
}

/// <summary>
/// Bug round 3 (SQL Server): the presenter's page mirroring what another of their tabs opened, while its read of that is
/// still out, doesn't close the card the presenter has opened for the room since.
/// </summary>
public class PresentMirrorRound3Tests(GatedDatabaseTideApp app) : IClassFixture<GatedDatabaseTideApp>
{
    private LiveMeetings Meetings => app.Services.GetRequiredService<LiveMeetings>();
    private GatedDatabaseTideApp.Gate Gate => app.Services.GetRequiredService<GatedDatabaseTideApp.Gate>();

    [Fact]
    public async Task AStaleMirrorOfTheOtherTab_LeavesTheCardThePresenterOpenedSince()
    {
        var day = app.Services.GetRequiredService<OrgClock>().CurrentBriefDate;
        for (var i = 0; i < 81; i++) day = WorkingDays.Weekdays.Previous(day);
        Guid product;
        await using (var scope = SignedIn(app, SignIn.Analyst))
        {
            var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
            var briefs = scope.ServiceProvider.GetRequiredService<BriefService>();
            var stack = scope.ServiceProvider.GetRequiredService<StackService>();
            var brief = await briefs.GetOrStartAsync(day);
            var article = await articles.CreateAsync("Mirror round 3 article", "Vendor", null, TlpLevel.Clear);
            await briefs.AddArticleAsync(brief.Id, article);
            product = await stack.SaveAsync(new StackForm { Name = "Northwind Mirror Gateway 81" });
            await stack.TagArticleAsync(article, product);
        }

        await using var pat = await RenderAsync<Present>(app, $"/present/{day:yyyy-MM-dd}", new() { ["Day"] = $"{day:yyyy-MM-dd}" });
        await pat.Click(p => p.StartMeeting());
        await pat.Click(p => p.OnPresentKey("last"));
        await pat.Click(p => p.OnPresentKey("prev"));

        // The presenter's other tab shows a card this page can't read; this page's read of it is held.
        using var other = Meetings.Join(day, SignIn.Analyst.UserId, SignIn.Analyst.Name, true, "", Guid.NewGuid(), Meetings.Get(day)!.Slide)!;
        var held = Gate.Arm();
        // The page hears of it on this thread: signed in as the page is, as its circuit would be.
        pat.Scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Http.IHttpContextAccessor>().HttpContext = pat.Signedin;
        Meetings.Show(other, Guid.NewGuid()).Should().BeTrue();
        await held.WaitAsync(TimeSpan.FromSeconds(10));

        // Meanwhile the presenter opens a product here, for the room.
        await pat.Click(p => p.ShowProduct(product));
        Meetings.Get(day)!.Product.Should().Be(product);

        // The held read comes back with nothing: it was for the other card, which isn't open any more.
        Gate.ReleaseHeld();
        await pat.Click(_ => Task.CompletedTask);
        await pat.Click(_ => Task.CompletedTask);

        Meetings.Get(day)!.Product.Should().Be(product, "the card the presenter opened since stays open for the room");
        (await pat.Html()).Should().Contain("present-product-title").And.Contain("Northwind Mirror Gateway 81");

        Meetings.End(Meetings.Join(day, SignIn.Analyst.UserId, SignIn.Analyst.Name, true, "", Guid.NewGuid(), 0)!);
    }
}
