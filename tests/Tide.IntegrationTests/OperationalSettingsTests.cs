using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Tide.Application;
using Tide.Application.Abstractions;
using Tide.Application.Actions;
using Tide.Application.Files;
using Tide.Application.Intake;
using Tide.Application.Metrics;
using Tide.Application.Operations;
using Tide.Application.Org;
using Tide.Application.Security;
using Tide.Application.Settings;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Infrastructure.Storage;
using Tide.TestSupport;
using Tide.Web.Services;

namespace Tide.IntegrationTests;

/// <summary>The application's services as the app registers them, over a test database and the given server
/// configuration, so the saved settings are laid over the server's as they are in the app.</summary>
public sealed class SettingsRig : IDisposable
{
    public TestDb Db { get; }
    public ServiceProvider Provider { get; }

    // A connection per context when two saves run at once, as two administrators' would.
    public SettingsRig(Dictionary<string, string?>? server = null, bool connectionPerContext = false)
    {
        Db = new TestDb(connectionPerContext: connectionPerContext);
        Db.User.Roles = new HashSet<AppRole> { AppRole.Admin };
        var services = new ServiceCollection();
        // The organization's boundary is the sending address's domain, example.org, unless the test's server says otherwise.
        var config = new Dictionary<string, string?> { ["Email:From"] = "tide@example.org" };
        foreach (var (key, value) in server ?? []) config[key] = value;
        services.AddApplication(new ConfigurationBuilder().AddInMemoryCollection(config).Build());
        services.AddSingleton<IAppDbContextFactory>(Db);
        services.AddSingleton<ICurrentUser>(Db.User);
        services.AddSingleton<IOutboundHttp>(Db.Http);
        services.AddSingleton<IFileStore>(Db.Store);
        services.AddSingleton<ISecretProtector, FakeSecrets>();   // the web app's is Data Protection
        services.AddSingleton<IRoleDirectory>(new CountingRoleDirectory());
        // As the app has: the job lease, which a job run by hand asks for, logs.
        services.AddLogging();
        Provider = services.BuildServiceProvider();
    }

    public T Get<T>() where T : notnull => Provider.GetRequiredService<T>();

    public T Now<T>() where T : class => Get<IOptionsMonitor<T>>().CurrentValue;

    public Task Save(string key, string? value) => Get<OperationalSettingsService>().SaveAsync(new Dictionary<string, string?> { [key] = value });

    public void Dispose()
    {
        Provider.Dispose();
        Db.Dispose();
    }
}

public class OperationalSettingsTests
{
    private static readonly Dictionary<string, string?> Server = new()
    {
        ["Org:TimeZone"] = "Europe/London",
        ["Org:MeetingTime"] = "10:30",
        ["Org:TeamName"] = "Server CTI",
        ["Notifications:ReminderTime"] = "07:30",
        ["Notifications:BriefNotReady:Recipients:0"] = "server@example.org",
        ["Intake:Enabled"] = "true",
        ["Intake:Proxy"] = "http://proxy.example.org:8080",
        ["Cve:RefreshHours"] = "12",
        ["Metrics:Import:PollMinutes"] = "10",
        ["Updates:CheckIntervalHours"] = "48",
    };

    [Fact]
    public void Unsaved_TheServersValuesHold()
    {
        using var rig = new SettingsRig(Server);
        var clock = rig.Get<OrgClock>();
        clock.Zone.Id.Should().Be("Europe/London");
        clock.TeamName.Should().Be("Server CTI");
        rig.Now<ReminderOptions>().ReminderTime.Should().Be("07:30");
        rig.Now<ReminderOptions>().BriefNotReady.Recipients.Should().Equal("server@example.org");
        rig.Now<IntakeOptions>().Enabled.Should().BeTrue();
        rig.Now<CveOptions>().RefreshHours.Should().Be(12);
        rig.Now<MetricImportOptions>().PollMinutes.Should().Be(10);
        rig.Now<UpdateOptions>().CheckIntervalHours.Should().Be(48);
    }

    [Fact]
    public async Task ASavedValue_WinsAtOnce_AndIsAudited()
    {
        using var rig = new SettingsRig(Server);
        await rig.Get<OperationalSettingsService>().SaveAsync(new Dictionary<string, string?>
        {
            [OperationalSettings.ReminderTime] = "6:45",
            [OperationalSettings.Digest] = "TRUE",
            [OperationalSettings.DigestDay] = "friday",
            [OperationalSettings.BriefNotReadyRecipients] = " lead@example.org, desk@example.org\nlead@example.org ",
            [OperationalSettings.IntakeEnabled] = "false",
            [OperationalSettings.RetentionDays] = "30",
            [OperationalSettings.MinPollMinutes] = "20",
            [OperationalSettings.ReadSeconds] = "15",
            [OperationalSettings.CveEnabled] = "true",
            [OperationalSettings.CveRefreshHours] = "6",
            [OperationalSettings.DropFolderEnabled] = "true",
            [OperationalSettings.DropFolderPollMinutes] = "2",
            [OperationalSettings.UpdateIntervalHours] = "72",
        });

        var reminders = rig.Now<ReminderOptions>();
        reminders.ReminderTime.Should().Be("06:45");
        reminders.Digest.Enabled.Should().BeTrue();
        reminders.Digest.Day.Should().Be(DayOfWeek.Friday);
        reminders.BriefNotReady.Recipients.Should().Equal("lead@example.org", "desk@example.org");
        reminders.DueSoon.Enabled.Should().BeFalse("what wasn't saved keeps the server's value");
        rig.Now<IntakeOptions>().Should().Match<IntakeOptions>(o => !o.Enabled && o.RetentionDays == 30 && o.MinPollMinutes == 20
            && o.ReadBudget == TimeSpan.FromSeconds(15) && o.Proxy == "http://proxy.example.org:8080");
        rig.Now<CveOptions>().Should().Match<CveOptions>(o => o.Enabled && o.RefreshHours == 6);
        rig.Now<MetricImportOptions>().Should().Match<MetricImportOptions>(o => o.DropFolderEnabled && o.PollMinutes == 2);
        UpdateCheckHostedService.Interval(rig.Now<UpdateOptions>()).Should().Be(TimeSpan.FromHours(72));

        await using var ctx = rig.Db.Context();
        ctx.Settings.Single(s => s.Key == OperationalSettings.BriefNotReadyRecipients).Value.Should().Be("lead@example.org; desk@example.org");
        ctx.Settings.Single(s => s.Key == OperationalSettings.ReminderTime).UpdatedBy.Should().Be(rig.Db.User.UserId);
        ctx.AuditLog.Should().Contain(a => a.EntityType == nameof(AppSetting), "a setting saved in the app is audited like any change");
    }

    // What two administrators' pages showed when both opened, before either saved.
    private static Dictionary<string, string?> OrgPage(string reminder = "07:30", string zone = "Europe/London") => new()
    {
        [OperationalSettings.ReminderTime] = reminder,
        [OperationalSettings.TimeZone] = zone,
        [OperationalSettings.TeamName] = "Server CTI",
    };

    [Fact]
    public async Task TwoAdministratorsOnStalePages_ChangingDifferentSettings_BothChangesStand()
    {
        using var rig = new SettingsRig(Server);
        var settings = rig.Get<OperationalSettingsService>();
        var loaded = OrgPage();

        await settings.SaveAsync(OrgPage(reminder: "06:00"), loaded: loaded);
        await settings.SaveAsync(OrgPage(zone: "Asia/Tokyo"), loaded: loaded);

        rig.Now<ReminderOptions>().ReminderTime.Should().Be("06:00", "the second page didn't change it, so it doesn't put back what it showed");
        rig.Get<OrgClock>().Zone.Id.Should().Be("Asia/Tokyo");
        await using var ctx = rig.Db.Context();
        ctx.Settings.Select(s => s.Key).Should().BeEquivalentTo([OperationalSettings.ReminderTime, OperationalSettings.TimeZone],
            "a setting neither changed isn't written");
        ctx.AuditLog.Count(a => a.EntityType == nameof(AppSetting)).Should().Be(2, "each save audits only what it changed");
    }

    [Fact]
    public async Task TwoAdministratorsOnStalePages_ChangingTheSameSetting_TheSecondIsRefused_AndNothingIsOverwritten()
    {
        using var rig = new SettingsRig(Server);
        var settings = rig.Get<OperationalSettingsService>();
        var loaded = OrgPage();
        await settings.SaveAsync(OrgPage(reminder: "06:00"), loaded: loaded);

        var second = () => settings.SaveAsync(OrgPage(reminder: "08:15", zone: "Asia/Tokyo"), loaded: loaded);

        (await second.Should().ThrowAsync<Tide.Application.Articles.StaleEditException>())
            .Which.Message.Should().Be("Someone else changed a setting you changed here since you opened this page, so nothing was saved. " +
                                       "Reload to see their change, then make yours again.");
        rig.Now<ReminderOptions>().ReminderTime.Should().Be("06:00");
        rig.Get<OrgClock>().Zone.Id.Should().Be("Europe/London", "the refused save is refused whole");

        // Reloaded, the same change saves.
        await settings.SaveAsync(OrgPage(reminder: "08:15"), loaded: OrgPage(reminder: "06:00"));
        rig.Now<ReminderOptions>().ReminderTime.Should().Be("08:15");
    }

    [Fact]
    public async Task TwoAdministratorsSavingTheSameSettingAtTheSameMoment_TheSecondIsRefused_AndTheFirstStands()
    {
        using var rig = new SettingsRig(Server, connectionPerContext: true);
        var settings = rig.Get<OperationalSettingsService>();
        var loaded = OrgPage();
        var saving = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Db.BeforeSave = ctx =>
        {
            if (!ctx.ChangeTracker.Entries<AppSetting>().Any(e => e.State == EntityState.Added)) return;
            if (saving.TrySetResult()) Thread.Sleep(400);   // the first has checked and is about to save
        };

        var first = Task.Run(() => settings.SaveAsync(OrgPage(reminder: "06:00"), loaded: loaded));
        await saving.Task;
        var second = Task.Run(() => settings.SaveAsync(OrgPage(reminder: "08:15"), loaded: loaded));
        await first;
        await second.Invoking(t => t).Should().ThrowAsync<Tide.Application.Articles.StaleEditException>();
        rig.Db.BeforeSave = null;

        rig.Now<ReminderOptions>().ReminderTime.Should().Be("06:00", "the second waited, read what the first saved and was refused");
        await using var ctx = rig.Db.Context();
        ctx.Settings.Single(s => s.Key == OperationalSettings.ReminderTime).Value.Should().Be("06:00");
    }

    [Fact]
    public async Task SomeoneElseHavingSavedTheSameValue_DoesntRefuseTheSave()
    {
        using var rig = new SettingsRig(Server);
        var settings = rig.Get<OperationalSettingsService>();
        var loaded = OrgPage();
        await settings.SaveAsync(OrgPage(reminder: "06:00"), loaded: loaded);

        await settings.SaveAsync(OrgPage(reminder: "6:00", zone: "Asia/Tokyo"), loaded: loaded);

        rig.Now<ReminderOptions>().ReminderTime.Should().Be("06:00");
        rig.Get<OrgClock>().Zone.Id.Should().Be("Asia/Tokyo", "nothing of theirs would be lost, so the rest saves");
    }

    [Fact]
    public async Task ASaveWithNoChanges_WritesNothing()
    {
        using var rig = new SettingsRig(Server);
        await rig.Get<OperationalSettingsService>().SaveAsync(OrgPage(), loaded: OrgPage());
        await rig.Get<OperationalSettingsService>().SaveAsync(OrgPage(reminder: "7:30"), loaded: OrgPage());   // the same time, typed differently
        await using var ctx = rig.Db.Context();
        ctx.Settings.Should().BeEmpty("the server's values stand rather than being written over with the same");
        ctx.AuditLog.Should().NotContain(a => a.EntityType == nameof(AppSetting));
    }

    [Fact]
    public async Task AnotherSaveAddingTheSameSettingsRowFirst_RefusesTheSave_AsStale()
    {
        using var rig = new SettingsRig(Server);
        var added = false;
        // Another administrator's save adds the row in the moment between this save's check and its write.
        rig.Db.BeforeSave = ctx =>
        {
            if (added || !ctx.ChangeTracker.Entries<AppSetting>().Any(e => e.State == EntityState.Added)) return;
            added = true;
            ctx.Database.ExecuteSqlRaw("INSERT INTO \"Settings\" (\"Key\", \"Value\", \"UpdatedAtUtc\", \"UpdatedBy\") VALUES ({0}, '06:00', 0, 'someone')",
                OperationalSettings.ReminderTime);
        };

        var save = () => rig.Get<OperationalSettingsService>().SaveAsync(OrgPage(reminder: "08:15"), loaded: OrgPage());

        await save.Should().ThrowAsync<Tide.Application.Articles.StaleEditException>();
        rig.Db.BeforeSave = null;
        rig.Now<ReminderOptions>().ReminderTime.Should().Be("07:30", "nothing was saved");
    }

    [Fact]
    public void OnlyARowAddedTwice_CountsAsTheKeyTaken()
    {
        static DbUpdateException Failed(string? inner) => new("save failed", inner is null ? null : new InvalidOperationException(inner));
        OperationalSettingsService.KeyTaken(Failed("SQLite Error 19: 'UNIQUE constraint failed: Settings.Key'.")).Should().BeTrue();
        OperationalSettingsService.KeyTaken(Failed("Violation of PRIMARY KEY constraint 'PK_Settings'.")).Should().BeTrue();
        OperationalSettingsService.KeyTaken(Failed("UNIQUE constraint failed: Articles.Id")).Should().BeFalse("another table's key");
        OperationalSettingsService.KeyTaken(Failed(null)).Should().BeFalse("no database error to read");
        OperationalSettingsService.KeyTaken(new DbUpdateConcurrencyException("stale", new InvalidOperationException("PK_Settings")))
            .Should().BeFalse("a concurrency failure isn't a row added twice");
    }

    [Theory]
    [InlineData("Pacific Standard Time")]   // a server's Windows id: shown, but not one the app saves
    [InlineData(null)]
    public async Task APageThatShowedAValueTheAppCantSave_SavesTheZoneChosenInItsPlace(string? shown)
    {
        using var rig = new SettingsRig(Server);
        var loaded = OrgPage();
        loaded[OperationalSettings.TimeZone] = shown;

        await rig.Get<OperationalSettingsService>().SaveAsync(OrgPage(zone: "Asia/Tokyo"), loaded: loaded);

        rig.Get<OrgClock>().Zone.Id.Should().Be("Asia/Tokyo");
        await using var ctx = rig.Db.Context();
        ctx.Settings.Select(s => s.Key).Should().Equal(OperationalSettings.TimeZone);
    }

    [Fact]
    public void ASavedValue_IsLaidOverTheDefaultOptionsOnly()
    {
        var applied = new List<string>();
        using var saved = new SavedSettings();
        var overlay = new OptionsOverlay<SavedSettings, OrgOptions>(saved, (_, o) => applied.Add(o.TimeZone));
        overlay.PostConfigure("named", new OrgOptions { TimeZone = "named" });
        overlay.PostConfigure(Options.DefaultName, new OrgOptions { TimeZone = "default" });
        applied.Should().Equal("default");
        overlay.Name.Should().Be(Options.DefaultName);
    }

    [Fact]
    public async Task OrgClock_FollowsASavedTimeZone_MeetingTimeAndName()
    {
        using var rig = new SettingsRig(Server);
        var clock = rig.Get<OrgClock>();
        var day = new DateOnly(2026, 11, 2);
        clock.MeetingStart(day).Offset.Should().Be(TimeSpan.Zero, "London is on GMT in November");

        await rig.Get<OperationalSettingsService>().SaveAsync(new Dictionary<string, string?>
        {
            [OperationalSettings.TimeZone] = "Asia/Tokyo",
            [OperationalSettings.MeetingTime] = "09:15",
            [OperationalSettings.TeamName] = "  Blue Team  ",
        });

        clock.Zone.Id.Should().Be("Asia/Tokyo");
        clock.MeetingTime.Should().Be(new TimeOnly(9, 15));
        clock.TeamName.Should().Be("Blue Team");
        clock.MeetingStart(day).Should().Be(new DateTimeOffset(2026, 11, 2, 9, 15, 0, TimeSpan.FromHours(9)));
        clock.Now.Offset.Should().Be(TimeSpan.FromHours(9));
    }

    [Fact]
    public async Task TheBriefsDeadlines_DefaultTo1130And1200_AndFollowASavedValue()
    {
        using var rig = new SettingsRig(Server);
        var clock = rig.Get<OrgClock>();
        (clock.SentBy, clock.PostedBy).Should().Be((new TimeOnly(11, 30), new TimeOnly(12, 0)));

        await rig.Get<OperationalSettingsService>().SaveAsync(new Dictionary<string, string?>
        {
            [OperationalSettings.SentBy] = "9:45",
            [OperationalSettings.PostedBy] = "13:00",
        });

        (clock.SentBy, clock.PostedBy).Should().Be((new TimeOnly(9, 45), new TimeOnly(13, 0)));
    }

    [Theory]
    [InlineData("Org:TimeZone", "Mars/Olympus_Mons")]
    [InlineData("Org:TimeZone", "Tokyo Standard Time")]
    [InlineData("Org:TimeZone", "")]
    [InlineData("Org:MeetingTime", "25:00")]
    [InlineData("Org:MeetingTime", "10.30")]
    [InlineData("Org:SentBy", "11:75")]
    [InlineData("Org:PostedBy", "noon")]
    [InlineData("Org:TeamName", "  ")]
    [InlineData("Notifications:ReminderTime", "8am")]
    [InlineData("Notifications:DueSoon:Enabled", "yes")]
    [InlineData("Notifications:Digest:Day", "Funday")]
    [InlineData("Notifications:Digest:Day", "3")]
    [InlineData("Notifications:BriefNotReady:Recipients", "lead@example.org; not an address")]
    [InlineData("Intake:RetentionDays", "3")]
    [InlineData("Intake:MinPollMinutes", "1")]
    [InlineData("Intake:MinPollMinutes", "-5")]
    [InlineData("Cve:RefreshHours", "0")]
    [InlineData("Cve:RefreshHours", "169")]
    [InlineData("Metrics:Import:PollMinutes", "61")]
    [InlineData("Updates:CheckIntervalHours", "5")]
    [InlineData("Updates:CheckIntervalHours", "169")]
    [InlineData("Intake:Proxy", "http://evil.example.org")]
    [InlineData("Email:Enabled", "true")]
    public async Task ABadValue_IsRefused_AndNothingIsSaved(string key, string value)
    {
        using var rig = new SettingsRig(Server);
        var save = () => rig.Get<OperationalSettingsService>().SaveAsync(new Dictionary<string, string?>
        {
            [OperationalSettings.ReminderTime] = "09:00",
            [key] = value,
        });
        await save.Should().ThrowAsync<ArgumentException>();
        await using var ctx = rig.Db.Context();
        ctx.Settings.Should().BeEmpty("the lot is refused, not just the bad one");
        rig.Now<ReminderOptions>().ReminderTime.Should().Be("07:30");
    }

    [Fact]
    public void TooManyNudgeRecipients_AreRefused()
    {
        var many = string.Join(";", Enumerable.Range(1, OperationalSettings.MaxNudgeRecipients + 1).Select(i => $"a{i}@example.org"));
        OperationalSettings.Problem(OperationalSettings.BriefNotReadyRecipients, many).Should().Contain("team mailbox");
        OperationalSettings.Problem(OperationalSettings.BriefNotReadyRecipients, "").Should().BeNull("nobody is a choice");
        OperationalSettings.Problem(OperationalSettings.TeamName, new string('x', OperationalSettings.MaxTeamName + 1)).Should().NotBeNull();
    }

    [Fact]
    public async Task OnlyAnAdministrator_Saves()
    {
        using var rig = new SettingsRig(Server);
        rig.Db.User.Roles = new HashSet<AppRole> { AppRole.Analyst };
        await rig.Invoking(r => r.Save(OperationalSettings.IntakeEnabled, "false")).Should().ThrowAsync<ForbiddenException>();
        rig.Now<IntakeOptions>().Enabled.Should().BeTrue();
        await using var ctx = rig.Db.Context();
        ctx.Settings.Should().BeEmpty();
    }

    [Fact]
    public async Task ARowThatNoLongerPassesItsCheck_IsIgnored_SoTheServersValueHolds()
    {
        using var rig = new SettingsRig(Server);
        await using (var ctx = rig.Db.Context())
        {
            ctx.Settings.Add(new AppSetting { Key = OperationalSettings.TimeZone, Value = "Nowhere/Atlantis", UpdatedBy = "x" });
            ctx.Settings.Add(new AppSetting { Key = OperationalSettings.MinPollMinutes, Value = "30", UpdatedBy = "x" });
            await ctx.SaveChangesAsync();
        }
        await rig.Get<SavedSettings>().RefreshAsync(rig.Db);
        rig.Get<OrgClock>().Zone.Id.Should().Be("Europe/London");
        rig.Now<IntakeOptions>().MinPollMinutes.Should().Be(30);
    }

    [Fact]
    public void TheTimeZoneChoices_AreIanaIds_WithTheirOffsets()
    {
        var zones = OperationalSettings.TimeZoneChoices("America/New_York");
        zones.Should().Contain(z => z.Id == "America/New_York" && z.Label.StartsWith("America/New York (UTC"));
        zones.Should().Contain(z => z.Id == "UTC");
        zones.Select(z => z.Id).Should().OnlyHaveUniqueItems().And.NotContain("Eastern Standard Time");
        zones.Should().OnlyContain(z => OperationalSettings.Problem(OperationalSettings.TimeZone, z.Id) == null);
    }

    [Fact]
    public void TheCatalog_ListsEachOperationalSettingInTheApp_AndNotOnTheServer()
    {
        var server = SettingsCatalog.Server.Select(s => s.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var key in OperationalSettings.Keys)
        {
            server.Should().NotContain(key);
            SettingsCatalog.InApp.Should().ContainSingle(s => s.Key == key && s.EditedIn != null, key);
            SettingsCatalog.IsInApp(key).Should().BeTrue();
        }
        server.Should().Contain(["Auth:Mode", "ConnectionStrings:Default", "Files:RootPath", "Email:Enabled", "Email:SmtpHost", "Email:From",
            "Intake:Proxy", "Intake:AllowPrivateNetworks", "Intake:TimeoutSeconds", "Intake:MaxResponseKB", "Cve:NvdApiKey", "Cve:NvdUrl",
            "Updates:Repository"]);
        SettingsCatalog.InApp.Select(s => s.Key).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task TheConfigurationBundle_CarriesTheOperationalSettings_AndAnImportTakesEffect()
    {
        using var source = new SettingsRig(Server);
        await source.Save(OperationalSettings.DigestDay, "Wednesday");
        await source.Save(OperationalSettings.TimeZone, "Asia/Tokyo");
        var bundle = await source.Get<ConfigBundleService>().ExportAsync();
        bundle.Settings.Should().Contain(new BundleSetting(OperationalSettings.DigestDay, "Wednesday"));

        using var target = new SettingsRig(Server);
        var tampered = bundle with
        {
            Settings = [.. bundle.Settings, new BundleSetting(OperationalSettings.CveRefreshHours, "9999"), new BundleSetting(OperationalSettings.ReminderTime, "7:05")],
        };
        var done = await target.Get<ConfigBundleService>().PreviewAndImportAsync(tampered);
        done.Changes.Single(c => c.Item == OperationalSettings.CveRefreshHours).Kind.Should().Be(BundleChangeKind.Skipped);
        done.Changes.Single(c => c.Item == OperationalSettings.DigestDay).Kind.Should().Be(BundleChangeKind.Add);
        target.Now<ReminderOptions>().Digest.Day.Should().Be(DayOfWeek.Wednesday, "an import takes effect at once");
        target.Now<ReminderOptions>().ReminderTime.Should().Be("07:05");
        target.Get<OrgClock>().Zone.Id.Should().Be("Asia/Tokyo");
        target.Now<CveOptions>().RefreshHours.Should().Be(12);
        (await target.Get<ConfigBundleService>().PreviewAsync(tampered)).Changes
            .Where(c => c.Area == "Settings" && c.Kind != BundleChangeKind.Skipped).Should().OnlyContain(c => c.Kind == BundleChangeKind.Unchanged);
    }
}

public class IntakeHostedServiceSettingsTests
{
    private const string FeedUrl = "https://news.example.com/feed.xml";

    [Fact]
    public async Task TurningPollingOff_InTheApp_StopsTheNextPass()
    {
        using var rig = new SettingsRig(new() { ["Intake:Enabled"] = "true", ["Intake:MinPollMinutes"] = "5" });
        rig.Db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };
        rig.Db.Http.Set(FeedUrl, "<rss version=\"2.0\"><channel><title>News</title>"
            + "<item><title>Patch now</title><link>https://news.example.com/patch-now</link></item></channel></rss>");
        await rig.Get<IntakeService>().SaveSourceAsync(new SourceForm { Name = "News", Kind = SourceKind.Feed, Url = FeedUrl, DefaultTlp = TlpLevel.Clear, PollMinutes = 30 });
        var jobs = new JobMonitor(TimeProvider.System);
        var hosted = new IntakeHostedService(rig.Get<IServiceScopeFactory>(), new ChangeFeed(), jobs, new HeldLease(), rig.Get<IOptionsMonitor<IntakeOptions>>(),
            rig.Get<IOptionsMonitor<CveOptions>>(), TimeProvider.System, NullLogger<IntakeHostedService>.Instance);

        await hosted.PassAsync(CancellationToken.None);
        rig.Db.Http.Requests.Should().ContainSingle(r => r.Url == FeedUrl, "polling is on on the server");
        jobs.Get(JobMonitor.FeedPoll).Should().Match<JobState>(s => s.Enabled && s.LastDetail == "1 new item", "Diagnostics says what the pass took");
        jobs.Get(JobMonitor.Prune).LastDetail.Should().Be("0 removed, 0 kept texts expired");

        await rig.Save(OperationalSettings.IntakeEnabled, "false");
        await using (var ctx = rig.Db.Context())
        {
            await ctx.Sources.ExecuteUpdateAsync(s => s.SetProperty(x => x.LastPolledAtUtc, (DateTimeOffset?)null));
        }
        await hosted.PassAsync(CancellationToken.None);
        rig.Db.Http.Requests.Should().ContainSingle("the next pass reads the saved setting: polling is off");
        jobs.Get(JobMonitor.FeedPoll).Should().Match<JobState>(s => !s.Enabled && s.NextDueUtc == null);
    }

    // NVD can take minutes over a run (slow answers, tried again): the feeds are polled meanwhile, so Polling the sources
    // isn't held up and called overdue, and the next pass doesn't start a second refresh.
    [Fact]
    public async Task ASlowCveRefresh_DoesntHoldUpThePasses()
    {
        // A connection per context, as the app has: the refresh reads the database on its own thread while the pass prunes,
        // and one shared SQLite connection used from both at once fails either of them at random.
        using var rig = new SettingsRig(new() { ["Intake:Enabled"] = "false", ["Cve:Enabled"] = "true" }, connectionPerContext: true);
        var o = rig.Now<CveOptions>();
        var slow = new TaskCompletionSource();
        rig.Db.Http.Holds[o.KevUrl] = slow.Task;
        var jobs = new JobMonitor(TimeProvider.System);
        var hosted = new IntakeHostedService(rig.Get<IServiceScopeFactory>(), new ChangeFeed(), jobs, new HeldLease(), rig.Get<IOptionsMonitor<IntakeOptions>>(),
            rig.Get<IOptionsMonitor<CveOptions>>(), TimeProvider.System, NullLogger<IntakeHostedService>.Instance);

        await hosted.PassAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
        await hosted.PassAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
        jobs.Get(JobMonitor.Prune).LastSucceeded.Should().BeTrue("the pass went on while the refresh waits on KEV");
        // Diagnostics and the Sources card say it's under way, with its own limit before it's called hung. The refresh
        // runs beside the passes, so it says so once it has started: waited for, not assumed.
        (await Tide.TestSupport.Poll.Until(() => jobs.Get(JobMonitor.CveRefresh).Running)).Should().BeTrue("the refresh has started");
        jobs.Get(JobMonitor.CveRefresh).Should().Match<JobState>(s => s.Running && s.LastStartedUtc != null && s.LastFinishedUtc == null
            && s.Job.Timeout == CveService.LongestRun(o));
        var cves = rig.Get<CveRunLog>();
        cves.StartedAtUtc.Should().Be(jobs.Get(JobMonitor.CveRefresh).LastStartedUtc);

        slow.SetResult();
        (await Tide.TestSupport.Poll.Until(() => jobs.Get(JobMonitor.CveRefresh).LastFinishedUtc is not null)).Should().BeTrue();
        rig.Db.Http.Requests.Count(r => r.Url.StartsWith(o.KevUrl, StringComparison.Ordinal)).Should().Be(1, "one refresh at a time");
        jobs.Get(JobMonitor.CveRefresh).Should().Match<JobState>(s => !s.Running && s.LastSucceeded != null);
        cves.StartedAtUtc.Should().BeNull();
        cves.Last.Should().NotBeNull();
    }
}

public class MetricImportHostedServiceTests
{
    // The service as the app builds it, over a drop folder of its own and the options given.
    private sealed class Rig : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "tide-drop-" + Guid.NewGuid().ToString("N"));

        public SettingsRig Settings { get; } = new();
        public JobMonitor Jobs { get; } = new(TimeProvider.System);
        public RecordingLogger<MetricImportHostedService> Log { get; } = new();
        public MetricDropFolder Folder { get; }
        public MetricImportHostedService Hosted { get; }

        public Rig(IOptionsMonitor<MetricImportOptions> folderOptions, IOptionsMonitor<MetricImportOptions>? serviceOptions = null,
            IServiceScopeFactory? scopes = null)
        {
            Settings.Db.User.IsAuthenticated = false;   // the background service: no one signed in
            Folder = new MetricDropFolder(Options.Create(new FileStoreOptions { RootPath = _root }), new TestHostEnvironment(), folderOptions,
                TimeProvider.System, NullLogger<MetricDropFolder>.Instance);
            Hosted = new MetricImportHostedService(scopes ?? Settings.Get<IServiceScopeFactory>(), Folder, serviceOptions ?? folderOptions, Jobs,
                new HeldLease(), TimeProvider.System, Log);
        }

        // A file left long enough to have settled, so the next pass reads it.
        public void Drop(string name)
        {
            Directory.CreateDirectory(Folder.Inbox);
            var path = Path.Combine(Folder.Inbox, name);
            File.WriteAllText(path, "nothing useful");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-5));
        }

        public JobState Job => Jobs.Get(JobMonitor.MetricDropFolder);

        public async ValueTask DisposeAsync()
        {
            await Hosted.StopAsync(CancellationToken.None);
            Hosted.Dispose();
            Settings.Dispose();
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }

    private static StaticOptionsMonitor<MetricImportOptions> On(int pollMinutes = 5) =>
        new(new MetricImportOptions { DropFolderEnabled = true, PollMinutes = pollMinutes });

    // A server file edited while TIDE runs, with a value that won't bind.
    private sealed class Unreadable : IOptionsMonitor<MetricImportOptions>
    {
        public MetricImportOptions CurrentValue => throw new InvalidOperationException("Failed to convert '5m' to type 'System.Int32'.");
        public MetricImportOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<MetricImportOptions, string?> listener) => null;
    }

    [Fact]
    public async Task WithTheDropFolderOn_APassReadsTheFileWaiting_AndSaysHowMany()
    {
        await using var rig = new Rig(On(pollMinutes: 7));
        rig.Drop("queue.csv");

        await rig.Hosted.StartAsync(CancellationToken.None);

        (await Poll.Until(() => rig.Job.NextDueUtc is not null)).Should().BeTrue("the first pass runs at start, then it waits for the next");
        rig.Job.Should().Match<JobState>(s => s.LastSucceeded == true && s.LastDetail == "1 file" && s.Enabled && s.Interval == TimeSpan.FromMinutes(7));
        Directory.GetFiles(rig.Folder.Inbox, "*.csv").Should().BeEmpty("the file was read and moved");
    }

    [Fact]
    public async Task AnUnreadableSetting_FallsBack_AndIsLogged_RatherThanStoppingTheApp()
    {
        // Whether it's on can't be read: it's taken as off, so nothing is read, and it's asked again in a minute.
        await using (var off = new Rig(new Unreadable()))
        {
            off.Drop("queue.csv");
            await off.Hosted.StartAsync(CancellationToken.None);
            // Asked before the pass and again before the wait: both logged, neither thrown.
            (await Poll.Until(() => off.Log.Entries.Count(e => e.Level == LogLevel.Error) >= 2)).Should().BeTrue();
            off.Log.Entries.Should().Contain(e => e.Message.Contains("Metrics:Import setting can't be read"));
            off.Job.Should().Match<JobState>(s => !s.Enabled && s.NextDueUtc == null && s.LastStartedUtc == null);
            Directory.GetFiles(off.Folder.Inbox, "*.csv").Should().ContainSingle("nothing was read");
            off.Hosted.ExecuteTask!.IsCompleted.Should().BeFalse("it keeps asking");
        }

        // On, but how often can't be read: the pass runs, and the next is a minute away.
        await using var on = new Rig(On(), serviceOptions: new Unreadable());
        await on.Hosted.StartAsync(CancellationToken.None);
        (await Poll.Until(() => on.Job.NextDueUtc is not null)).Should().BeTrue();
        on.Job.Should().Match<JobState>(s => s.Enabled && s.LastSucceeded == true && s.Interval == MetricImportHostedService.OffInterval);
        on.Log.Entries.Should().Contain(e => e.Level == LogLevel.Error);
    }

    [Fact]
    public async Task AFailedPass_IsRecordedAndLogged_AndTheServiceKeepsGoing()
    {
        // No import service to read the files with: the pass throws.
        await using var empty = new ServiceCollection().BuildServiceProvider();
        await using var rig = new Rig(On(), scopes: empty.GetRequiredService<IServiceScopeFactory>());
        rig.Drop("queue.csv");

        await rig.Hosted.StartAsync(CancellationToken.None);

        (await Poll.Until(() => rig.Job.NextDueUtc is not null)).Should().BeTrue("a failure waits for the next pass like any other");
        rig.Job.Should().Match<JobState>(s => s.LastSucceeded == false && s.LastError != null && !s.Running);
        rig.Log.Entries.Should().Contain(e => e.Level == LogLevel.Debug && e.Message.Contains("tried again at the next pass"));
        rig.Hosted.ExecuteTask!.IsCompleted.Should().BeFalse("it's waiting to try again, not stopped");
    }

    [Fact]
    public async Task Stopping_EndsTheWait_AndReturns()
    {
        await using var rig = new Rig(On(pollMinutes: 60));
        await rig.Hosted.StartAsync(CancellationToken.None);
        (await Poll.Until(() => rig.Job.NextDueUtc is not null)).Should().BeTrue();

        await rig.Hosted.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));

        rig.Hosted.ExecuteTask!.Status.Should().Be(TaskStatus.RanToCompletion, "an hour's wait ends when the app stops");
    }
}

/// <summary>A logger that keeps every entry, with its level.</summary>
public sealed class RecordingLogger<T> : ILogger<T>
{
    private readonly List<(LogLevel Level, string Message)> _entries = [];

    public IReadOnlyList<(LogLevel Level, string Message)> Entries { get { lock (_entries) return [.. _entries]; } }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_entries) _entries.Add((logLevel, formatter(state, exception)));
    }
}
