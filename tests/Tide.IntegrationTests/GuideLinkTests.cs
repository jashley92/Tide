using System.Globalization;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Abstractions;
using Tide.Application.Operations;
using Tide.Application.Org;
using Tide.Application.Preferences;
using Tide.Application.Settings;
using Tide.Infrastructure.Persistence;
using Tide.Web.Components.Layout;
using Tide.Web.Components.Shared;
using Tide.Web.Services;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>
/// A page's Show me around link, under its subtitle, is for a while, not for ever: from a person's first sign-in, or from
/// the first time they used a release that changed the page, for the organization's number of days; gone once they've
/// run the page's guide or put the link away; Help's switch overrides the window. The ? menu always has it.
/// </summary>
public class GuideLinkTests(TideApp app) : IClassFixture<TideApp>
{
    private static SignIn Person(string role = "Viewer") => new($"dev:guidelink-{Guid.NewGuid():N}", "Link tester", role);

    private async Task<T> As<T>(SignIn who, Func<IServiceProvider, Task<T>> what)
    {
        await using var scope = SignedIn(app, who);
        return await what(scope.ServiceProvider);
    }

    private Task Set(SignIn who, string name, string value) =>
        As(who, async sp => { await sp.GetRequiredService<UserPreferenceService>().SetAsync(name, value); return 0; });

    private Task<bool> Shows(SignIn who, string key, string page) => As(who, sp =>
        sp.GetRequiredService<GuideLinkService>().ShowsAsync(Guides.Find(key, sp.GetRequiredService<ICurrentUser>().Has)!, page));

    private string DaysAgo(int days) =>
        app.Services.GetRequiredService<OrgClock>().Today.AddDays(-days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // Someone whose first sign-in was `days` ago.
    private async Task<SignIn> SignedInFirst(int days, string role = "Viewer")
    {
        var who = Person(role);
        await Set(who, GuideLinkService.Since, DaysAgo(days));
        return who;
    }

    [Fact]
    public async Task AFirstSignIn_IsRecorded_SoTheLinksShowFromIt()
    {
        var who = Person();
        await using (var scope = app.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<IUserDirectory>().TouchAsync(who.UserId, who.Name, null, null, "Viewer");
        await using var check = SignedIn(app, who);
        (await check.ServiceProvider.GetRequiredService<UserPreferenceService>().GetAsync(GuideLinkService.Since))
            .Should().Be(DaysAgo(0));
    }

    [Fact]
    public async Task TheLink_ShowsForSomeoneNew_AndIsGoneAfterTheWindow()
    {
        (await Shows(await SignedInFirst(0), "metrics", "metrics")).Should().BeTrue("on the day of their first sign-in");
        (await Shows(await SignedInFirst(4), "metrics", "metrics")).Should().BeTrue("on the fifth day of the default five");
        (await Shows(await SignedInFirst(5), "metrics", "metrics")).Should().BeFalse("after five days");
        (await Shows(Person(), "metrics", "metrics")).Should().BeFalse("someone who used TIDE before has no first-sign-in window");
    }

    [Fact]
    public async Task TheLink_IsGone_OnceThePagesGuideHasRun_OrItsPutAway()
    {
        var ran = await SignedInFirst(0);
        await As(ran, async sp => { await sp.GetRequiredService<GuideLinkService>().DoneAsync(Guides.Find("metrics", sp.GetRequiredService<ICurrentUser>().Has)!); return 0; });
        (await Shows(ran, "metrics", "metrics")).Should().BeFalse();
        (await Shows(ran, "trends", "trends")).Should().BeTrue("only that page's");

        var hid = await SignedInFirst(0);
        await using var r = await RenderAsync<GuideLink>(app, "/trends", @as: hid);
        (await r.Html()).Should().Contain("id=\"tide-guide-start\"").And.Contain("id=\"tide-guide-hide\"");
        await r.Click(c => c.HideAsync());
        (await r.Html()).Should().NotContain("tide-guide-start");
        (await Shows(hid, "trends", "trends")).Should().BeFalse("put away for good");
    }

    [Fact]
    public async Task ARunGuide_TakesItsLinkAway()
    {
        var who = await SignedInFirst(0);
        await using var host = await RenderAsync<OnboardingHost>(app, "/metrics", @as: who);
        await host.Click(_ => { host.Scope.ServiceProvider.GetRequiredService<TourRequests>().AskGuide("metrics"); return Task.CompletedTask; });
        await host.Until(h => h.Step is not null, seconds: 5);
        await host.Click(h => h.EndTourAsync());   // part way
        (await Shows(who, "metrics", "metrics")).Should().BeFalse();
    }

    [Fact]
    public async Task AReleaseThatChangedThePage_BringsItBack_ForTheWindow()
    {
        var running = app.Services.GetRequiredService<ReleaseNotesCatalog>().Running(AppVersionInfo.Current)!;
        var changed = PageTips.All.First(t => t.Version == running && Guides.ForPage(PagePath(t), _ => true) is not null);
        var page = PagePath(changed);
        var key = Guides.ForPage(page, _ => true)!.Key;
        var who = Person("Analyst");
        (await Shows(who, key, page)).Should().BeFalse("not before the release's What's new");
        await As(who, async sp => { await sp.GetRequiredService<WhatsNewService>().MarkSeenAsync(); return 0; });
        (await Shows(who, key, page)).Should().BeTrue("seeing the release's What's new starts the window");
        await Set(who, GuideLinkService.Release, $"{running}@{DaysAgo(5)}");
        (await Shows(who, key, page)).Should().BeFalse("five days on");
        await Set(who, GuideLinkService.Release, $"1.0.0@{DaysAgo(30)}");
        (await Shows(who, key, page)).Should().BeFalse("an older release's What's new doesn't count");
        await As(who, async sp => { await sp.GetRequiredService<WhatsNewService>().MarkSeenAsync(); return 0; });
        (await Shows(who, key, page)).Should().BeTrue("this release's starts it again");
    }

    // The page a tip is on, as a guide names pages.
    private static string PagePath(PageTip tip) =>
        Guides.OnPages.Select(g => g.Page.Replace("{n}", "1", StringComparison.Ordinal).Replace("{id}", Guid.Empty.ToString(), StringComparison.Ordinal))
            .First(p => PageTips.For(p)?.Key == tip.Key);

    [Fact]
    public async Task TheAdministratorsSetting_ChangesTheWindow_AtOnce_AndIsAudited()
    {
        var who = await SignedInFirst(7);
        (await Shows(who, "metrics", "metrics")).Should().BeFalse();
        await using (var admin = SignedIn(app, SignIn.Analyst))
        {
            var audited = await admin.ServiceProvider.GetRequiredService<AppDbContext>().AuditLog.CountAsync();
            await admin.ServiceProvider.GetRequiredService<OperationalSettingsService>().SaveAsync(new Dictionary<string, string?>
            {
                [OperationalSettings.GuideLinkDays] = "10",
            });
            (await admin.ServiceProvider.GetRequiredService<AppDbContext>().AuditLog.CountAsync()).Should().BeGreaterThan(audited);
        }
        try
        {
            app.Services.GetRequiredService<OrgClock>().GuideLinkDays.Should().Be(10);
            (await Shows(who, "metrics", "metrics")).Should().BeTrue("seven days is inside ten");
        }
        finally
        {
            await using var admin = SignedIn(app, SignIn.Analyst);
            await admin.ServiceProvider.GetRequiredService<OperationalSettingsService>().SaveAsync(new Dictionary<string, string?>
            {
                [OperationalSettings.GuideLinkDays] = OrgOptions.DefaultGuideLinkDays.ToString(CultureInfo.InvariantCulture),
            });
        }
        OperationalSettings.Problem(OperationalSettings.GuideLinkDays, "91").Should().NotBeNull("whole days, 1 to 90");
        OperationalSettings.Problem(OperationalSettings.GuideLinkDays, "0").Should().NotBeNull();
    }

    [Fact]
    public async Task HelpsSwitch_OverridesTheWindow_BothWays()
    {
        var old = Person();
        (await Shows(old, "metrics", "metrics")).Should().BeFalse();
        await As(old, async sp => { await sp.GetRequiredService<GuideLinkService>().SwitchAsync(on: true); return 0; });
        (await Shows(old, "metrics", "metrics")).Should().BeTrue("on, whatever the window");

        var fresh = await SignedInFirst(0);
        await As(fresh, async sp => { await sp.GetRequiredService<GuideLinkService>().SwitchAsync(on: false); return 0; });
        (await Shows(fresh, "metrics", "metrics")).Should().BeFalse("off, even inside the window");

        await using var help = await RenderAsync<Tide.Web.Components.Pages.HelpPage>(app, "/help", @as: fresh);
        (await help.Html()).Should().Contain("id=\"help-guide-links\"").And.Contain("Show page guide links");
    }
}
