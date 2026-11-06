using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Actions;
using Tide.Application.Preferences;
using Tide.Application.Settings;
using Tide.Web.Components.Pages;
using Tide.Web.Components.Pages.Admin;
using Tide.Web.Services;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>
/// My notifications and the counts on Teams &amp; SLA, rendered in the real app (its reminders are off, as a new server's
/// are, until a test turns one on): a kind that's off shows so, disabled; a switch saves at once; an administrator sees
/// how many chose, and who, but changes nothing.
/// </summary>
public class NotificationPreferencePagesTests(TideApp app) : IClassFixture<TideApp>
{
    private static SignIn Person(string role = "Analyst") => new($"dev:notify-{Guid.NewGuid():N}", "Notify tester", role);

    private static IReadOnlyList<ToastService.Toast> Toasts<T>(Rendered<T> r) => r.Scope.ServiceProvider.GetRequiredService<ToastService>().Toasts;

    private async Task TurnOn(string key)
    {
        await using var scope = SignedIn(app, SignIn.Analyst);
        await scope.ServiceProvider.GetRequiredService<OperationalSettingsService>().SaveAsync(new Dictionary<string, string?> { [key] = "true" });
    }

    [Fact]
    public async Task MyNotifications_ShowsTheAddress_AndWhatsOffForEveryone_Disabled()
    {
        await using var r = await RenderAsync<MyNotifications>(app, "/notifications", @as: Person());
        var html = await r.Html();

        html.Should().Contain("<h1>My notifications</h1>").And.Contain("tester@example.com")
            .And.Contain("Off for everyone (set by an administrator)")
            .And.Contain("Rotation requests and notices").And.Contain("Personal digest");
        html.Should().MatchRegex("id=\"notify-notify-overdue\"[^>]*disabled", "an administrator has overdue reminders off");
        html.Should().NotMatchRegex("id=\"notify-notify-rotationRequests\"[^>]*disabled", "rotation requests have no switch for everyone");

        var overdue = r.Component.GetType().GetField("_mine", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var mine = (MyNotificationChoices)overdue.GetValue(r.Component)!;
        await r.Click(c => c.Set(mine.Switches.Single(s => s.Name == UserPreferences.NotifyOverdue), false));
        Toasts(r).Should().Contain(t => t.IsError && t.Message.Contains("Off for everyone"));
    }

    [Fact]
    public async Task MyNotifications_SavesEachSwitchAtOnce_WithAToast_AndHintsAtTheDigest()
    {
        await TurnOn(OperationalSettings.DueSoon);
        await TurnOn(OperationalSettings.Presenting);
        var who = Person();
        await using var r = await RenderAsync<MyNotifications>(app, "/notifications", @as: who);
        (await r.Html()).Should().Contain(NotificationPreferenceService.DutyNote.Replace("'", "&#x27;", StringComparison.Ordinal))
            .And.NotContain(NotificationPreferenceService.AlsoInDigest);

        await r.Click(c => c.SetDigest(UserPreferences.Daily));
        Toasts(r).Should().Contain(t => !t.IsError && t.Message == "You'll get a daily digest.");
        (await r.Html()).Should().Contain(NotificationPreferenceService.AlsoInDigest, "due-soon is on, and the digest lists the same actions");

        var mine = await Mine(who);
        await r.Click(c => c.Set(mine.Switches.Single(s => s.Name == UserPreferences.NotifyDueSoon), false));
        Toasts(r).Should().Contain(t => t.Message == "Due-soon reminders off.");
        (await Mine(who)).Switches.Single(s => s.Name == UserPreferences.NotifyDueSoon).On.Should().BeFalse();
        (await Mine(who)).Digest.Should().Be(UserPreferences.Daily);
    }

    private async Task<MyNotificationChoices> Mine(SignIn who)
    {
        await using var scope = SignedIn(app, who);
        return await scope.ServiceProvider.GetRequiredService<NotificationPreferenceService>().GetMineAsync();
    }

    [Fact]
    public async Task TeamsAndSla_CountsWhoChose_AndListsThem()
    {
        var who = Person();
        await using (var scope = SignedIn(app, who))
        {
            var prefs = scope.ServiceProvider.GetRequiredService<NotificationPreferenceService>();
            await prefs.SetMineAsync(UserPreferences.NotifyRotationRequests, UserPreferences.Off);
            await prefs.SetMineAsync(UserPreferences.NotifyDigest, UserPreferences.Weekly);
        }

        // The other tests here choose too, so the counts are read as an administrator would before the page.
        IReadOnlyDictionary<string, int> counts;
        await using (var admin = SignedIn(app, SignIn.Analyst))
            counts = await admin.ServiceProvider.GetRequiredService<NotificationPreferenceService>().CountsAsync();
        counts[UserPreferences.NotifyRotationRequests].Should().BeGreaterThan(0);
        counts[UserPreferences.NotifyDigest].Should().BeGreaterThan(0);

        await using var r = await RenderAsync<TeamsAdmin>(app, "/admin/teams");
        var html = await r.Html();
        html.Should().Contain(TeamsAdmin.CountText(UserPreferences.NotifyRotationRequests, counts[UserPreferences.NotifyRotationRequests]))
            .And.Contain(TeamsAdmin.CountText(UserPreferences.NotifyDigest, counts[UserPreferences.NotifyDigest])).And.Contain("No one opted out");

        await r.Click(c => c.ShowChoosers(UserPreferences.NotifyDigest, ReminderKind.PersonalDigest));
        html = await r.Html();
        html.Should().Contain(who.UserId, "not yet in the user mirror, so listed by id").And.Contain("weekly").And.Contain("tide-optlist");
        await r.Click(c => c.ShowChoosers(UserPreferences.NotifyDigest, ReminderKind.PersonalDigest));
        (await r.Html()).Should().NotContain("tide-optlist\"", "a second click closes it");
    }

    [Fact]
    public async Task TeamsAndSla_ASharedChoice_OpensOneListUnderTheRowClicked()
    {
        // The brief rotation reminder and the brief-not-ready nudge are both the "Rotation reminders" choice.
        await using (var scope = SignedIn(app, Person()))
            await scope.ServiceProvider.GetRequiredService<NotificationPreferenceService>().SetMineAsync(UserPreferences.NotifyRotationReminders, UserPreferences.Off);

        await using var r = await RenderAsync<TeamsAdmin>(app, "/admin/teams");
        await r.Click(c => c.ShowChoosers(UserPreferences.NotifyRotationReminders, ReminderKind.Presenting));
        var html = await r.Html();

        System.Text.RegularExpressions.Regex.Matches(html, "<table[^>]*tide-optlist").Should().HaveCount(1, "the other row with the same choice stays closed");
        html.Should().Contain("id=\"optlist-notify-rotationReminders-Presenting\"");
    }

    [Theory]
    [InlineData(UserPreferences.NotifyDueSoon, 0, "No one opted out")]
    [InlineData(UserPreferences.NotifyDueSoon, 3, "3 people opted out")]
    [InlineData(UserPreferences.NotifyDigest, 0, "No one gets a personal digest")]
    [InlineData(UserPreferences.NotifyDigest, 5, "5 people get a personal digest")]
    public void TheCounts_ReadAsTheySay(string pref, int n, string text) => TeamsAdmin.CountText(pref, n).Should().Be(text);
}
