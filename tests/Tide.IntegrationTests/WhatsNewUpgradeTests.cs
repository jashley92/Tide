using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Abstractions;
using Tide.Application.Operations;
using Tide.Domain.Entities;
using Tide.Infrastructure.Persistence;
using Tide.Web.Components.Layout;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>An install upgraded from a version that didn't record what it ran (v1.12.0, say), with people who used it.</summary>
public sealed class UpgradedApp : TideApp
{
    public static readonly SignIn LongStanding = new("dev:upgraded-analyst", "Long-standing analyst", "Analyst");
    public static readonly SignIn LongStandingViewer = new("dev:upgraded-viewer", "Long-standing viewer", "Viewer");

    protected override Action<AppDbContext>? Existing => db =>
    {
        foreach (var who in new[] { LongStanding, LongStandingViewer })
            db.Users.Add(new AppUser { Sid = who.UserId, DisplayName = who.Name, RolesCsv = string.Join(',', who.Roles), LastSeenUtc = DateTimeOffset.UtcNow.AddDays(-30) });
    };
}

/// <summary>
/// What's new after an upgrade: someone who has never seen it gets every release's highlights since the version the
/// server ran before, newest first, so an upgrade straight past v1.13.0 still shows its highlights. A new install shows
/// only the newest, and a newcomer gets the tour.
/// </summary>
public class WhatsNewUpgradeTests(UpgradedApp app) : IClassFixture<UpgradedApp>
{
    private static readonly ReleaseVersion V1_13 = new(1, 13, 0), V1_14 = new(1, 14, 0);

    private async Task<Dictionary<string, string>> Record()
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Settings.AsNoTracking().Where(s => s.Key.StartsWith("App:")).ToDictionaryAsync(s => s.Key, s => s.Value);
    }

    [Fact]
    public async Task TheServerRecords_TheVersionItRuns_AndThatItCameFromBeforeTheRecord()
    {
        var running = app.Services.GetRequiredService<ReleaseNotesCatalog>().Running(AppVersionInfo.Current)!;
        var record = await Record();
        record[ReleaseHistory.VersionKey].Should().Be(running.ToString());
        record[ReleaseHistory.UpgradedFromKey].Should().Be(ReleaseHistory.BeforeRecorded.ToString(), "people used it before: an upgrade");
        app.Services.GetRequiredService<ReleaseHistory>().UpgradedFrom.Should().Be(ReleaseHistory.BeforeRecorded);
    }

    [Fact]
    public async Task SomeoneWhoUsedTide_AndHasSeenNone_SeesThisReleasesAndV113sHighlights_NewestFirst()
    {
        await using (var scope = SignedIn(app, UpgradedApp.LongStanding))
        {
            var due = await scope.ServiceProvider.GetRequiredService<WhatsNewService>().DueAsync();
            due.Select(h => h.Version).Should().Equal(V1_14, V1_13);
        }

        await using var r = await RenderAsync<WhatsNewDialog>(app, "/", @as: UpgradedApp.LongStanding);
        await r.Click(c => c.CheckAsync());
        var html = await r.Html();
        html.Should().Contain("What&#x27;s new in TIDE 1.14.0").And.Contain("tide-whatsnew-release\">1.14.0").And.Contain("tide-whatsnew-release\">1.13.0");
        html.IndexOf("tide-whatsnew-release\">1.14.0", StringComparison.Ordinal).Should()
            .BeLessThan(html.IndexOf("tide-whatsnew-release\">1.13.0", StringComparison.Ordinal), "newest first");
        html.Should().Contain("context rail", "v1.13.0's layout change is among them");
    }

    [Fact]
    public async Task AViewer_SeesBothReleasesViewersHighlights()
    {
        await using var scope = SignedIn(app, UpgradedApp.LongStandingViewer);
        var due = await scope.ServiceProvider.GetRequiredService<WhatsNewService>().DueAsync();
        due.Select(h => h.Version).Should().Equal(V1_14, V1_13);
    }

    [Fact]
    public async Task SomeoneSigningInForTheFirstTime_GetsTheTour_WhichSettlesWhatsNew()
    {
        var newcomer = new SignIn($"dev:upgraded-new-{Guid.NewGuid():N}", "Newcomer", "Analyst");
        await app.Services.GetRequiredService<IUserDirectory>().TouchAsync(newcomer.UserId, newcomer.Name, null, null, "Analyst");
        await using var scope = SignedIn(app, newcomer);
        var tour = scope.ServiceProvider.GetRequiredService<TourService>();
        (await tour.IsDueAsync()).Should().BeTrue("a newcomer is shown round, not handed a stack of releases");
        await tour.FinishedAsync();
        (await scope.ServiceProvider.GetRequiredService<WhatsNewService>().DueAsync()).Should().BeEmpty();
    }
}
