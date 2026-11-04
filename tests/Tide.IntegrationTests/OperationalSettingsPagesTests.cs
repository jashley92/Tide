using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Abstractions;
using Tide.Application.Intake;
using Tide.Application.Operations;
using Tide.Application.Org;
using Tide.Application.Settings;
using Tide.Web.Components.Pages.Admin;

namespace Tide.IntegrationTests;

/// <summary>The settings forms, rendered in the real app: each shows the setting in force and saves it.</summary>
public class OperationalSettingsPagesTests(TideApp app) : IClassFixture<TideApp>
{
    private async Task<string?> Saved(string key)
    {
        await using var scope = app.Services.CreateAsyncScope();
        await using var db = await scope.ServiceProvider.GetRequiredService<IAppDbContextFactory>().CreateAsync();
        return db.Settings.Where(s => s.Key == key).Select(s => s.Value).FirstOrDefault();
    }

    [Fact]
    public async Task OrganizationSettings_ListTheHolidays_WithWaysToAddThem()
    {
        // A day far ahead, so no other test's brief day or deadline meets it; removed again afterwards.
        var day = new DateOnly(2031, 3, 4);
        var clock = app.Services.GetRequiredService<OrgClock>();
        await using (var scope = app.Services.CreateAsyncScope())
        await using (var db = await scope.ServiceProvider.GetRequiredService<IAppDbContextFactory>().CreateAsync())
        {
            db.Holidays.Add(new Tide.Domain.Entities.Holiday { Date = day, Name = "Founders' Day" });
            await db.SaveChangesAsync();
            await HolidayService.LoadAsync(db, clock);
        }
        try
        {
            await using var r = await ComponentHarness.RenderAsync<OrganizationAdmin>(app, "/admin/organization");
            var html = await r.Html();
            html.Should().Contain("id=\"holidays-title\"").And.Contain("Tue Mar 4, 2031").And.Contain("Founders&#x27; Day")
                .And.Contain("id=\"holiday-date\"").And.Contain("id=\"holiday-ics\"").And.Contain("Add holiday");
        }
        finally
        {
            await using var scope = app.Services.CreateAsyncScope();
            await using var db = await scope.ServiceProvider.GetRequiredService<IAppDbContextFactory>().CreateAsync();
            db.Holidays.Remove(db.Holidays.Single(h => h.Date == day));
            await db.SaveChangesAsync();
            await HolidayService.LoadAsync(db, clock);
        }
    }

    [Fact]
    public async Task OrganizationSettings_ShowTheZoneInForce_AndAskBeforeChangingIt()
    {
        await using var r = await ComponentHarness.RenderAsync<OrganizationAdmin>(app, "/admin/organization");
        var html = await r.Html();
        html.Should().Contain("<h1 class=\"tide-admin-title\">Organization settings</h1>")
            .And.Contain("id=\"org-zone\"").And.Contain("America/New York (UTC")
            .And.Contain("id=\"org-meeting\"").And.Contain("value=\"Cyber Threat Intelligence\"");

        // The same zone saves without asking.
        await r.Click(c => c.Save());
        (await Saved(OperationalSettings.TeamName)).Should().Be("Cyber Threat Intelligence");

        r.Component.Zone = "Asia/Tokyo";
        await r.Press(c => c.Save());
        var asked = await r.HtmlOnce("Change the time zone to Asia/Tokyo?");
        asked.Should().Contain("Change the time zone to Asia/Tokyo?").And.Contain("reminder");
        (await Saved(OperationalSettings.TimeZone)).Should().NotBe("Asia/Tokyo", "nothing changes until it's confirmed");
        app.Services.GetRequiredService<OrgClock>().Zone.Id.Should().NotBe("Asia/Tokyo");
    }

    [Fact]
    public async Task TeamsAndSla_EditsTheReminders()
    {
        await using var r = await ComponentHarness.RenderAsync<TeamsAdmin>(app, "/admin/teams");
        var html = await r.Html();
        html.Should().Contain("id=\"rem-time\"").And.Contain("id=\"rem-on-DueSoon\"").And.Contain("id=\"rem-digest-day\"")
            .And.Contain("id=\"rem-bnr-to\"").And.Contain("Save reminders").And.Contain("Run now");
        await r.Click(c => c.SaveReminders());
        (await Saved(OperationalSettings.ReminderTime)).Should().Be("08:00", "the server's value, saved as the form showed it");
        (await Saved(OperationalSettings.DigestDay)).Should().Be("Monday");
        (await Saved(OperationalSettings.DueSoon)).Should().Be("false");
    }

    [Fact]
    public async Task Sources_EditsPollingAndCveEnrichment()
    {
        await using var r = await ComponentHarness.RenderAsync<SourcesAdmin>(app, "/admin/sources");
        var html = await r.Html();
        html.Should().Contain("id=\"intake-on\"").And.Contain("id=\"intake-keep\"").And.Contain("id=\"intake-min\"").And.Contain("id=\"intake-read\"")
            .And.Contain("id=\"cve-on\"").And.Contain("id=\"cve-hours\"").And.NotContain("Intake:Enabled");
        await r.Click(c => c.SaveIntake());
        await r.Click(c => c.SaveCve());
        (await Saved(OperationalSettings.RetentionDays)).Should().Be("90");
        (await Saved(OperationalSettings.ReadSeconds)).Should().Be("6");
        (await Saved(OperationalSettings.CveRefreshHours)).Should().Be("24");
    }

    [Fact]
    public async Task Sources_TheStarterList_TicksTheRecommended_AndAddsThemPaused()
    {
        await using var r = await ComponentHarness.RenderAsync<SourcesAdmin>(app, "/admin/sources");
        (await r.Html()).Should().Contain("Starter sources").And.NotContain("id=\"starter-title\"", "it opens on request");
        await r.Click(c => c.OpenStarter());
        var html = await r.Html();
        html.Should().Contain("id=\"starter-title\"").And.Contain("<details").And.Contain("More sources (")
            .And.Contain("id=\"starter-TheRecord\"").And.Contain("id=\"starter-TheHackerNews\"");
        var recommended = StarterSources.All.Where(s => s.Recommended).Select(s => s.Name).ToList();
        r.Component.Starter.Should().BeEquivalentTo(recommended);
        html.Should().Contain($"Add {recommended.Count} sources");

        try
        {
            await r.Click(c => c.AddStarter());
            r.Component.Starter.Should().BeNull("the list closes once they're added");
            await using var scope = app.Services.CreateAsyncScope();
            await using var db = await scope.ServiceProvider.GetRequiredService<IAppDbContextFactory>().CreateAsync();
            var added = db.Sources.Where(s => recommended.Contains(s.Name)).ToList();
            added.Should().HaveCount(recommended.Count).And.OnlyContain(s => !s.Enabled);

            await r.Click(c => c.OpenStarter());
            r.Component.Starter.Should().BeEmpty("each is already here");
            (await r.Html()).Should().Contain("Added");
        }
        finally
        {
            await using var scope = app.Services.CreateAsyncScope();
            await using var db = await scope.ServiceProvider.GetRequiredService<IAppDbContextFactory>().CreateAsync();
            db.Sources.RemoveRange(db.Sources.Where(s => recommended.Contains(s.Name)));
            await db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task Sources_EditsTheSponsoredItems_AndTestsAPhraseAgainstTheListsAsTyped()
    {
        await using var r = await ComponentHarness.RenderAsync<SourcesAdmin>(app, "/admin/sources");
        var html = await r.Html();
        html.Should().Contain("id=\"sponsored-on\"").And.Contain("role=\"switch\"").And.Contain("Skip sponsored items")
            .And.Contain("id=\"sponsored-markers\"").And.Contain("Sponsored by").And.Contain("id=\"sponsored-exceptions\"").And.Contain("state-sponsored")
            .And.Contain("id=\"sponsored-test\"").And.NotContain("Intake:SkipSponsored");
        var at = html.IndexOf("id=\"sponsored-on\"", StringComparison.Ordinal);
        html[html.LastIndexOf('<', at)..html.IndexOf('>', at)].Should().Contain("checked", "it's on by default");

        html.Should().Contain("Uses the lists as typed");

        r.Component.Probe = "Sponsored by TryHackMe";
        r.Component.Verdict().Marker.Should().Be("Sponsored by");
        r.Component.Probe = "State-sponsored hackers breach telecom giants";
        r.Component.Verdict().Should().Be(new SponsoredCheck(null, "state-sponsored"));
        r.Component.Probe = "[Partner] How MDR cuts dwell time";
        r.Component.Verdict().Sponsored.Should().BeFalse();
        r.Component.Markers += "\n[Partner]";
        r.Component.Verdict().Marker.Should().Be("[Partner]", "the Test box uses the lists as typed");

        await r.Click(c => c.SaveSponsored());
        (await Saved(OperationalSettings.SkipSponsored)).Should().Be("true");
        (await Saved(OperationalSettings.SponsoredMarkers)).Should().EndWith("; Advertorial; [Partner]");
        (await Saved(OperationalSettings.SponsoredExceptions)).Should().Be("state-sponsored; nation-state sponsored; government-sponsored");
    }

    [Fact]
    public async Task Sources_EditsTheAdvertLists_AndRefusesASelectorThatIsntOne()
    {
        await using var r = await ComponentHarness.RenderAsync<SourcesAdmin>(app, "/admin/sources");
        (await r.Html()).Should().Contain("id=\"ad-selectors\"").And.Contain("ins.adsbygoogle").And.Contain("id=\"ad-labels\"")
            .And.Contain("Story continues below*").And.NotContain("Intake:AdSelectors");

        r.Component.AdSelectors += "\n.promo-inline";
        r.Component.AdLabels = "Advertisement\nRead more*";
        await r.Click(c => c.SaveAds());
        (await Saved(OperationalSettings.AdSelectors)).Should().EndWith("; amp-sticky-ad; .promo-inline");
        (await Saved(OperationalSettings.AdLabels)).Should().Be("Advertisement; Read more*");

        r.Component.AdSelectors = "div[";
        await r.Click(c => c.SaveAds());
        (await Saved(OperationalSettings.AdSelectors)).Should().EndWith(".promo-inline", "a selector the parser refuses isn't saved");
    }

    [Fact]
    public async Task MetricImports_TurnsTheDropFolderOnAndOff()
    {
        await using var r = await ComponentHarness.RenderAsync<MetricImportsAdmin>(app, "/admin/metric-imports");
        (await r.Html()).Should().Contain("id=\"drop-on\"").And.Contain("id=\"drop-every\"").And.NotContain("Metrics:Import:DropFolderEnabled");
        await r.Click(c => c.SaveDropFolder());
        (await Saved(OperationalSettings.DropFolderPollMinutes)).Should().Be("5");
    }

    [Fact]
    public async Task Updates_SetsTheCheckInterval()
    {
        await using var r = await ComponentHarness.RenderAsync<UpdatesAdmin>(app, "/admin/system/updates");
        (await r.Html()).Should().Contain("id=\"upd-hours\"").And.Contain("min=\"6\"").And.Contain("max=\"168\"");
        await r.Click(c => c.SaveInterval());
        (await Saved(OperationalSettings.UpdateIntervalHours)).Should().Be("24");
    }

    [Fact]
    public async Task ServerConfiguration_NoLongerListsTheMovedSettings_AndSaysWhyTheRestStay()
    {
        await using var r = await ComponentHarness.RenderAsync<ServerConfigAdmin>(app, "/admin/system/server");
        var html = await r.Html();
        foreach (var key in OperationalSettings.Keys) html.Should().NotContain($">{key}<", key);
        html.Should().Contain(">Intake:Proxy<").And.Contain(">Email:SmtpHost<").And.Contain(">Updates:Repository<")
            .And.Contain("These control sign-in");
    }

    [Fact]
    public async Task SettingSources_ListTheMovedSettings_WithWhereToChangeThem()
    {
        await using var r = await ComponentHarness.RenderAsync<SettingSourcesAdmin>(app, "/admin/system/sources");
        var html = await r.Html();
        html.Should().Contain(">Org:TimeZone<").And.Contain("href=\"admin/organization\"")
            .And.Contain(">Notifications:Digest:Day<").And.Contain(">Metrics:Import:PollMinutes<");
        SettingsCatalog.InApp.Should().Contain(s => s.Key == OperationalSettings.IntakeEnabled && s.EditedIn == "admin/sources");
    }
}
