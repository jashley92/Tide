using System.Net;
using System.Text;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Tide.Application.Abstractions;
using Tide.Application.Admin;
using Tide.Application.Operations;
using Tide.Application.Org;
using Tide.Application.Security;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Web.Services;

namespace Tide.IntegrationTests;

/// <summary>A role directory that only counts reloads.</summary>
public sealed class CountingRoleDirectory : IRoleDirectory
{
    public int Reloads { get; private set; }
    public IReadOnlySet<Permission> PermissionsForRoles(IEnumerable<string> roleNames) => new HashSet<Permission>();
    public bool IsRole(string name) => true;
    public IReadOnlySet<string> RolesForGroups(IEnumerable<string> adGroups) => new HashSet<string>();
    public IReadOnlySet<string> RolesMappedByName => new HashSet<string>();
    public void Invalidate() => Reloads++;
    public bool IsCurrent => true;
}

public sealed class NoUsers : IUserDirectory
{
    public List<AppUser> Users { get; } = [];
    public Task TouchAsync(string userId, string displayName, string? upn, string? email, string rolesCsv, bool nameIsStandIn = false, CancellationToken ct = default) => Task.CompletedTask;
    public Task<IReadOnlyList<AppUser>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AppUser>>(Users);
}

public sealed class ConfigSourceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tide-config-" + Guid.NewGuid().ToString("N"));
    private readonly string _prefix = "TIDETEST" + Guid.NewGuid().ToString("N")[..8] + "_";

    public ConfigSourceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(_prefix + "Email__SmtpPassword", null);
        Environment.SetEnvironmentVariable(_prefix + "Org__TeamName", null);
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private IConfigurationRoot Build()
    {
        File.WriteAllText(Path.Combine(_dir, "appsettings.json"), """
            { "Org": { "TeamName": "From the base file", "TimeZone": "America/New_York" },
              "Email": { "SmtpHost": "base.example.org", "SmtpPassword": "" },
              "Distribution": { "TeamRecipients": [ "a@example.org" ] } }
            """);
        File.WriteAllText(Path.Combine(_dir, "appsettings.Production.json"), """
            { "Email": { "SmtpHost": "relay.contoso.com" },
              "ConnectionStrings": { "Default": "Server=SQL01;Database=Tide;User ID=tide;Password=hunter2" },
              "Distribution": { "TeamRecipients": [ "cti@contoso.com", "soc@contoso.com" ] } }
            """);
        Environment.SetEnvironmentVariable(_prefix + "Email__SmtpPassword", "env-secret");
        Environment.SetEnvironmentVariable(_prefix + "Org__TeamName", "From the environment");
        return new ConfigurationBuilder()
            .SetBasePath(_dir)
            .AddJsonFile("appsettings.json")
            .AddJsonFile("appsettings.Production.json")
            .AddEnvironmentVariables(_prefix)
            .AddInMemoryCollection(new Dictionary<string, string?> { ["App:PathBase"] = "/tide" })
            .Build();
    }

    [Fact]
    public void Resolve_NamesTheProviderThatWins()
    {
        var config = Build();
        ConfigSource.Resolve(config, "Org:TimeZone").Should().Be(new ConfigResolution("America/New_York", "appsettings.json"));
        ConfigSource.Resolve(config, "Email:SmtpHost").Should().Be(new ConfigResolution("relay.contoso.com", "appsettings.Production.json (installer)"));
        ConfigSource.Resolve(config, "Org:TeamName").Should().Be(new ConfigResolution("From the environment", "Environment variable"));
        ConfigSource.Resolve(config, "App:PathBase").Source.Should().Be("Host settings");
        ConfigSource.Resolve(config, "Nothing:Here").Should().Be(new ConfigResolution(null, ConfigSource.Unset));
    }

    [Fact]
    public void Resolve_AList_ComesFromTheLastProviderSettingAnyOfIt()
    {
        var r = ConfigSource.Resolve(Build(), "Distribution:TeamRecipients");
        r.Source.Should().Be("appsettings.Production.json (installer)");
        r.Value.Should().Be("cti@contoso.com; soc@contoso.com");
    }

    [Fact]
    public void Resolve_WithoutAProviderChain_SaysOnlyThatItsConfigured()
    {
        IConfiguration section = Build().GetSection("Org");
        ConfigSource.Resolve(section, "TimeZone").Should().Be(new ConfigResolution("America/New_York", "Configuration"));
        ConfigSource.Resolve(section, "Missing").Source.Should().Be(ConfigSource.Unset);
    }

    [Fact]
    public async Task ServerSettings_NeverShowASecret_AndSayWhereEachCameFrom()
    {
        var config = Build();
        using var db = new TestDb();
        var settings = Settings(config, db);
        var rows = settings.Server();
        var password = rows.Single(r => r.Info.Key == "Email:SmtpPassword");
        password.Shown.Should().Be(SecretMask.SetSecret);
        password.Source.Should().Be("Environment variable");
        var connection = rows.Single(r => r.Info.Key == "ConnectionStrings:Default");
        connection.Shown.Should().Contain("server=SQL01").And.NotContain("hunter2").And.NotContain("tide;");
        rows.SelectMany(r => new[] { r.Shown, r.Source }).Should().NotContain(v => v != null && (v.Contains("hunter2") || v.Contains("env-secret")));
        rows.Single(r => r.Info.Key == "Cve:NvdApiKey").Shown.Should().BeNull("not set anywhere");

        // An in-app setting saved in Administration wins, and says who saved it.
        db.User.Roles = new HashSet<AppRole> { AppRole.Admin };
        await db.Settings().SaveRecipientsAsync(Edition.Team, "team@contoso.com");
        var inApp = await settings.InAppAsync();
        var team = inApp.Single(r => r.Info.Key == "Distribution:TeamRecipients");
        team.Should().Match<SettingRow>(r => r.FromDatabase && r.Source == SystemSettings.SavedInApp && r.Shown == "team@contoso.com" && r.SavedBy == "Test Analyst");
        var pub = inApp.Single(r => r.Info.Key == "Distribution:PublicRecipients");
        pub.Source.Should().Be(ConfigSource.Unset);
    }

    [Fact]
    public async Task InstallFacts_ComeFromTheSettings_AndTheGroupsMappedByName()
    {
        using var db = new TestDb();
        db.User.Roles = new HashSet<AppRole> { AppRole.Admin };
        var settings = Settings(Build(), db, sqlServer: true);
        var roles = new RoleMappingService(db, db.User, new CountingRoleDirectory());
        await roles.AddAsync("CONTOSO\\CTI-Analysts", AppRole.Analyst);
        await roles.AddAsync("S-1-5-21-1-2-3-1001", AppRole.Analyst);
        await roles.AddAsync("CONTOSO\\CTI-Admins", AppRole.Admin);
        var facts = await settings.InstallFactsAsync("/tide");
        facts.SqlInstance.Should().Be("SQL01");
        facts.AppPath.Should().Be("tide");
        facts.TeamName.Should().Be("Cyber Threat Intelligence");
        facts.Groups[AppRole.Analyst].Should().Equal("CONTOSO\\CTI-Analysts");
        facts.Origin.Should().Contain("Test Analyst");
        InstallAnswers.Write(facts).Should().NotContain("hunter2").And.NotContain("S-1-5-21");
    }

    private static SystemSettings Settings(IConfigurationRoot config, TestDb db, bool sqlServer = false)
    {
        if (sqlServer) config["Database:Provider"] = "SqlServer";
        db.User.Roles = new HashSet<AppRole> { AppRole.Admin };
        var env = new FakeEnv();
        var users = new NoUsers();
        users.Users.Add(new AppUser { Sid = db.User.UserId, DisplayName = db.User.DisplayName });
        return new SystemSettings(config, db, users, env, new RoleMappingService(db, db.User, new CountingRoleDirectory()),
            new OrgClock(Options.Create(new OrgOptions())), db.User, TimeProvider.System);
    }

    private sealed class FakeEnv : Microsoft.AspNetCore.Hosting.IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = "";
        public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
        public string ApplicationName { get; set; } = "Tide.Web";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
        public string ContentRootPath { get; set; } = "C:\\inetpub\\tide";
        public string EnvironmentName { get; set; } = "Production";
    }
}

public class ConfigBundleServiceTests
{
    private static (TestDb Db, ConfigBundleService Service, CountingRoleDirectory Roles) Admin()
    {
        var db = new TestDb();
        db.User.Roles = new HashSet<AppRole> { AppRole.Admin };
        var roles = new CountingRoleDirectory();
        return (db, new ConfigBundleService(db, db.User, roles, TimeProvider.System), roles);
    }

    private static async Task Seed(TestDb db)
    {
        await db.Settings().SaveAsync(TlpLevel.Clear, TlpLevel.Amber);
        await db.Settings().SaveRecipientsAsync(Edition.Team, "cti@contoso.com");
        await new RoleMappingService(db, db.User, new CountingRoleDirectory()).AddAsync("CONTOSO\\CTI-Admins", AppRole.Admin);
        await db.Scorecard().AddCategoryAsync("Ransomware", "Extortion crews");
        await using var ctx = db.Context();
        ctx.Settings.Add(new AppSetting { Key = "Cve:LastRun", Value = "2026-10-01T00:00:00Z", UpdatedBy = "system" });
        ctx.Sources.Add(new Source { Name = "Vendor blog", Url = "https://vendor.example.org/feed" });
        ctx.Sources.Add(new Source { Name = "Paid feed", Url = "https://intel.example.org/rss?apikey=SECRET123&topic=all", PollMinutes = 30 });
        ctx.QueryTemplates.Add(new QueryTemplate { Dialect = "kql", Kind = HuntValueKind.Domain, Template = "DnsEvents | where Name == '{value}'", UpdatedBy = "x" });
        await ctx.SaveChangesAsync();
    }

    [Fact]
    public async Task Export_CarriesTheAppsSetup_NeverASecretOrBookkeeping()
    {
        var (db, service, _) = Admin();
        using var _db = db;
        await Seed(db);
        var bundle = await service.ExportAsync();
        bundle.Format.Should().Be(ConfigBundle.FormatName);
        bundle.Settings.Select(s => s.Key).Should().Contain("Distribution:TeamRecipients").And.NotContain("Cve:LastRun");
        bundle.RoleMappings.Should().ContainSingle(m => m.AdGroup == "CONTOSO\\CTI-Admins" && m.Role == "Admin");
        var paid = bundle.Sources.Single(s => s.Name == "Paid feed");
        paid.UrlHidden.Should().BeTrue();
        paid.Url.Should().NotContain("SECRET123").And.Contain("topic=all");
        bundle.Sources.Single(s => s.Name == "Vendor blog").UrlHidden.Should().BeFalse();
        bundle.ToJson().Should().NotContain("SECRET123").And.NotContain("urlHidden\": false");
        bundle.QueryTemplates.Should().ContainSingle(q => q.Dialect == "kql" && q.Kind == "Domain");
        bundle.Scorecard.Should().ContainSingle(c => c.Name == "Ransomware");
    }

    [Fact]
    public async Task Import_IntoAnotherTide_PreviewsThenAddsAndUpdates_RemovingNothing()
    {
        var (source, sourceService, _) = Admin();
        using var _s = source;
        await Seed(source);
        var bundle = await sourceService.ExportAsync();

        var (target, service, roles) = Admin();
        using var _t = target;
        await using (var ctx = target.Context())
        {
            ctx.Sources.Add(new Source { Name = "PAID FEED", Url = "https://intel.example.org/rss?apikey=MINE&topic=all", PollMinutes = 60 });
            ctx.Sources.Add(new Source { Name = "Only here", Url = "https://only.example.org/feed" });
            await ctx.SaveChangesAsync();
        }

        var preview = await service.PreviewAsync(bundle);
        preview.HasChanges.Should().BeTrue();
        preview.Changes.Should().Contain(c => c.Area == "AD groups" && c.Kind == BundleChangeKind.Add);
        preview.Changes.Single(c => c.Item == "Paid feed").Should().Match<BundleChange>(c => c.Kind == BundleChangeKind.Update && c.Detail!.Contains("keeps its own address"));
        preview.Changes.Single(c => c.Item == "Vendor blog").Kind.Should().Be(BundleChangeKind.Add);
        await using (var ctx = target.Context())
            ctx.RoleMappings.Should().BeEmpty("a preview writes nothing");

        var done = await service.PreviewAndImportAsync(bundle);
        done.Count(BundleChangeKind.Add).Should().Be(preview.Count(BundleChangeKind.Add));
        roles.Reloads.Should().Be(1, "a new mapping takes effect at once");
        await using (var ctx = target.Context())
        {
            ctx.RoleMappings.Should().ContainSingle(m => m.AdGroup == "CONTOSO\\CTI-Admins");
            var paid = ctx.Sources.Single(s => s.Name == "PAID FEED");
            paid.Url.Should().Contain("apikey=MINE", "the bundle hid the key, so the target keeps its own");
            paid.PollMinutes.Should().Be(30);
            ctx.Sources.Should().Contain(s => s.Name == "Only here", "import never removes");
            ctx.Settings.Single(s => s.Key == "Distribution:TeamRecipients").Value.Should().Be("cti@contoso.com");
            ctx.Settings.Should().NotContain(s => s.Key == "Cve:LastRun");
            ctx.QueryTemplates.Should().ContainSingle(q => q.Dialect == "kql" && q.Kind == HuntValueKind.Domain);
            ctx.AuditLog.Should().Contain(a => a.EntityType == nameof(AdGroupRoleMapping), "imports are audited like any change");
        }

        // The same bundle again changes nothing.
        (await service.PreviewAsync(bundle)).HasChanges.Should().BeFalse();
    }

    [Fact]
    public async Task Import_ANewSourceWhoseKeyWasHidden_ComesInTurnedOff()
    {
        var (source, sourceService, _) = Admin();
        using var _s = source;
        await Seed(source);
        var bundle = await sourceService.ExportAsync();
        var (target, service, _) = Admin();
        using var _t = target;
        var done = await service.PreviewAndImportAsync(bundle);
        done.Changes.Single(c => c.Item == "Paid feed").Detail.Should().Contain("turned off");
        await using var ctx = target.Context();
        ctx.Sources.Single(s => s.Name == "Paid feed").Enabled.Should().BeFalse();
        ctx.Sources.Single(s => s.Name == "Vendor blog").Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task Import_SkipsWhatTheAppsOwnEditorsWouldRefuse()
    {
        var (db, service, _) = Admin();
        using var _db = db;
        var bundle = new ConfigBundle(ConfigBundle.FormatName, 1, DateTimeOffset.UtcNow, "x", "dev",
            [new("Distribution:PublicMaxTlp", "Purple"), new("ConnectionStrings:Default", "Server=evil"), new("Actions:Sla:Priority:P1", "2")],
            [new("CONTOSO\\X", "Superuser"), new("", "Admin")],
            [new("Bad", "Feed", "file:///etc/passwd", "Clear", "A", null, 60, true, false, null, null, null, null),
             new("Odd kind", "Carrier pigeon", "https://x.example.org", "Clear", "A", null, 60, true, false, null, null, null, null),
             new("Too often", "Feed", "https://x.example.org", "Clear", "A", null, 1, true, false, null, null, null, null)],
            [new BundleScorecardCategory("", "No key", null, 0, false, []),
             new BundleScorecardCategory("new-cat", "New category", null, 9, false, [new BundleScorecardTag("new-tag", "New tag", null, 0, false), new BundleScorecardTag("", "", null, 0, false)])],
            [new("nosuchdialect", "Domain", "x"), new("kql", "Domain", "")]);
        var done = await service.PreviewAndImportAsync(bundle);
        done.Changes.Where(c => c.Kind == BundleChangeKind.Skipped).Should().HaveCount(11);
        done.Changes.Where(c => c.Kind == BundleChangeKind.Add).Select(c => c.Item).Should().BeEquivalentTo("Actions:Sla:Priority:P1", "New category", "New category › New tag");
        await using var ctx = db.Context();
        ctx.ScorecardTags.Single(t => t.Key == "new-tag").CategoryId.Should().Be(ctx.ScorecardCategories.Single(c => c.Key == "new-cat").Id);
    }

    [Fact]
    public async Task OnlyAnAdministrator_ExportsOrImports()
    {
        using var db = new TestDb();
        var service = new ConfigBundleService(db, db.User, new CountingRoleDirectory(), TimeProvider.System);
        await service.Invoking(s => s.ExportAsync()).Should().ThrowAsync<ForbiddenException>();
        var bundle = new ConfigBundle(ConfigBundle.FormatName, 1, DateTimeOffset.UtcNow, "x", "dev", [], [], [], [], []);
        await service.Invoking(s => s.PreviewAsync(bundle)).Should().ThrowAsync<ForbiddenException>();
        await service.Invoking(s => s.ImportAsync(bundle, "", acknowledged: true)).Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task UpdateSettings_TheSavedChoiceWinsOverTheServers()
    {
        using var db = new TestDb();
        var settings = new UpdateSettings(db, db.User, Options.Create(new UpdateOptions { CheckEnabled = true }));
        (await settings.GetEnabledAsync()).Should().BeTrue("the server's default until one is saved");
        await settings.Invoking(s => s.SaveEnabledAsync(false)).Should().ThrowAsync<ForbiddenException>("an analyst can't");
        db.User.Roles = new HashSet<AppRole> { AppRole.Admin };
        await settings.SaveEnabledAsync(false);
        (await settings.GetEnabledAsync()).Should().BeFalse();
        await settings.SaveEnabledAsync(true);
        (await settings.GetEnabledAsync()).Should().BeTrue();
    }
}

public class UpdateCheckHostedServiceTests
{
    private const string Api = "https://api.github.com/repos/jashley92/Tide/releases";

    private sealed record Rig(UpdateCheckHostedService Hosted, UpdateCheckService Updates, JobMonitor Jobs, ServiceProvider Provider) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Updates.Dispose();
            await Provider.DisposeAsync();
        }
    }

    private static Rig Build(TestDb db, bool registerSettings = true)
    {
        var services = new ServiceCollection();
        if (registerSettings)
        {
            services.AddSingleton<IAppDbContextFactory>(db);
            services.AddSingleton<ICurrentUser>(db.User);
            services.AddSingleton(Options.Create(new UpdateOptions()));
            services.AddScoped<UpdateSettings>();
        }
        var provider = services.BuildServiceProvider();
        var monitor = new StaticOptionsMonitor<UpdateOptions>(new UpdateOptions());
        var updates = new UpdateCheckService(db.Http, monitor, Microsoft.Extensions.Logging.Abstractions.NullLogger<UpdateCheckService>.Instance,
            running: ReleaseVersion.Parse("0.1.0-alpha.1"), useBuildVersion: false);
        var jobs = new JobMonitor(TimeProvider.System);
        var hosted = new UpdateCheckHostedService(provider.GetRequiredService<IServiceScopeFactory>(), updates, monitor, jobs, TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<UpdateCheckHostedService>.Instance);
        return new Rig(hosted, updates, jobs, provider);
    }

    private static async Task TurnOn(TestDb db)
    {
        db.User.Roles = new HashSet<AppRole> { AppRole.Admin };
        await new UpdateSettings(db, db.User, Options.Create(new UpdateOptions())).SaveEnabledAsync(true);
    }

    [Fact]
    public async Task Off_APassAsksNothing()
    {
        using var db = new TestDb();
        await using var rig = Build(db);
        await rig.Hosted.PassAsync(CancellationToken.None);
        db.Http.Requests.Should().BeEmpty();
        rig.Jobs.Get(JobMonitor.UpdateCheck).Should().Match<JobState>(s => !s.Enabled && s.NextDueUtc == null);
    }

    [Fact]
    public async Task On_APassChecksOncePerInterval_AndRecordsWhatItFound()
    {
        using var db = new TestDb();
        await TurnOn(db);
        db.Http.Set(Api, """[{"tag_name":"v0.1.0-alpha.3","prerelease":true,"html_url":"https://github.com/jashley92/Tide/releases/tag/v0.1.0-alpha.3"}]""");
        await using var rig = Build(db);
        await rig.Hosted.PassAsync(CancellationToken.None);
        await rig.Hosted.PassAsync(CancellationToken.None);
        db.Http.Requests.Should().ContainSingle("the next check waits for Updates:CheckIntervalHours");
        rig.Updates.UpdateAvailable.Should().BeTrue();
        rig.Jobs.Get(JobMonitor.UpdateCheck).Should().Match<JobState>(s =>
            s.Enabled && s.LastSucceeded == true && s.LastDetail == "0.1.0-alpha.3 is available" && s.NextDueUtc != null);
    }

    [Fact]
    public async Task AFailedCheck_OrAPassThatThrows_IsRecorded_NotThrown()
    {
        using var db = new TestDb();
        await TurnOn(db);
        await using (var rig = Build(db))
        {
            await rig.Hosted.PassAsync(CancellationToken.None);
            rig.Jobs.Get(JobMonitor.UpdateCheck).Should().Match<JobState>(s => s.LastSucceeded == false && s.LastError!.StartsWith("No canned response"));
        }
        await using (var broken = Build(db, registerSettings: false))
        {
            await broken.Hosted.PassAsync(CancellationToken.None);
            broken.Jobs.Get(JobMonitor.UpdateCheck).LastError.Should().StartWith("InvalidOperationException");
        }
        UpdateCheckHostedService.Interval(new UpdateOptions { CheckIntervalHours = 1 }).Should().Be(TimeSpan.FromHours(6));
        UpdateCheckHostedService.Interval(new UpdateOptions { CheckIntervalHours = 0 }).Should().Be(TimeSpan.FromHours(24));
    }
}

public class SystemPagesTests(TideApp app) : IClassFixture<TideApp>
{
    [Theory]
    [InlineData("/admin/system", "Diagnostics")]
    [InlineData("/admin/system/server", "Server configuration")]
    [InlineData("/admin/system/sources", "Setting sources")]
    [InlineData("/admin/system/bundle", "Configuration bundle")]
    [InlineData("/admin/system/audit", "Audit log")]
    [InlineData("/admin/system/updates", "Updates")]
    public async Task ThePages_Render_ForAnAdministrator_WithoutASecret(string path, string title)
    {
        var res = await app.CreateClient().GetAsync(path);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await res.Content.ReadAsStringAsync();
        html.Should().Contain(title);
        html.Should().NotContain("href=\"/admin", "links stay relative so a sub-path install works");
    }

    [Fact]
    public async Task Diagnostics_FindsTheDatabaseUpToDate()
    {
        using var scope = app.Services.CreateScope();
        Audit.ServiceScopes.SignIn(scope, Permission.Administer);   // the service checks the user itself, as the page does
        var report = await scope.ServiceProvider.GetRequiredService<SystemDiagnostics>().RunAsync();
        report.Database.Reachable.Should().BeTrue();
        report.Database.Pending.Should().BeEmpty();
        report.Database.Applied.Should().BePositive();
        report.Checks.Should().Contain(c => c.Label == "Database" && c.Level == CheckLevel.Ok);
        report.Checks.Should().Contain(c => c.Label == "Email" && c.Level == CheckLevel.Ok, "development writes email to a pickup folder");
        report.Email.Delivery.Should().StartWith("Written to the pickup folder");
        report.Storage[0].Writable.Should().BeTrue();
        report.Jobs.Should().HaveCount(JobMonitor.All.Count);
        report.Build.Version.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task TheDownloads_AreTheAnswersFileAndTheBundle()
    {
        var client = app.CreateClient();
        var answers = await client.GetAsync("/admin/system/tide.config.psd1");
        answers.StatusCode.Should().Be(HttpStatusCode.OK);
        answers.Content.Headers.ContentDisposition!.FileNameStar.Should().Be("tide.config.psd1");
        var text = await answers.Content.ReadAsStringAsync();
        text.Should().Contain("SchemaMode").And.Contain("TeamName").And.NotContainEquivalentOf("password =");

        var bundle = await client.GetAsync("/admin/system/config-bundle.json");
        bundle.StatusCode.Should().Be(HttpStatusCode.OK);
        bundle.Content.Headers.ContentDisposition!.FileNameStar.Should().StartWith("tide-config-bundle-");
        var parsed = ConfigBundle.Parse(Encoding.UTF8.GetBytes(await bundle.Content.ReadAsStringAsync()));
        parsed.Scorecard.Should().NotBeEmpty();
    }
}

public class SystemReaderTests(ReaderApp app) : IClassFixture<ReaderApp>
{
    [Theory]
    [InlineData("/admin/system/tide.config.psd1")]
    [InlineData("/admin/system/config-bundle.json")]
    [InlineData("/admin/system/audit.csv")]
    public async Task AReader_CantDownloadTheConfiguration(string path)
    {
        var client = app.CreateClient(new() { AllowAutoRedirect = false });
        (await client.GetAsync(path)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AReader_IsSentAwayFromTheSystemPages()
    {
        var client = app.CreateClient(new() { AllowAutoRedirect = false });
        var page = await client.GetAsync("/admin/system/server");
        page.StatusCode.Should().Be(HttpStatusCode.Redirect);
        page.Headers.Location!.OriginalString.Should().StartWith("/access-denied");
    }
}

public class SystemSubPathTests(SubPathApp app) : IClassFixture<SubPathApp>
{
    [Fact]
    public async Task TheSystemPagesAndDownloads_AnswerUnderThePathBase()
    {
        var client = app.CreateClient(new() { AllowAutoRedirect = false });
        (await client.GetAsync("/tide/admin/system")).StatusCode.Should().Be(HttpStatusCode.OK);
        var answers = await (await client.GetAsync("/tide/admin/system/tide.config.psd1")).Content.ReadAsStringAsync();
        answers.Should().Contain("AppPath               = 'tide'", "the answers file says it's under a sub-path");
    }
}

/// <summary>The app with the update check on and answered by a canned release list, so Updates shows how to upgrade.</summary>
public class UpdateAvailableApp : TideApp
{
    public FakeHttp Http { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Updates:CheckEnabled", "true");
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<UpdateCheckService>();
            services.AddSingleton(sp => new UpdateCheckService(Http, sp.GetRequiredService<IOptionsMonitor<UpdateOptions>>(),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<UpdateCheckService>.Instance,
                running: ReleaseVersion.Parse("0.1.0-alpha.1"), useBuildVersion: false));
        });
    }
}

public class UpdatesUpgradeCommandTests(UpdateAvailableApp app) : IClassFixture<UpdateAvailableApp>
{
    [Fact]
    public async Task ANewerRelease_ShowsTheUpgradeScript_NotTheInstaller()
    {
        app.Http.Set("https://api.github.com/repos/jashley92/Tide/releases",
            """[{"tag_name":"v0.1.0-alpha.3","prerelease":true,"html_url":"https://github.com/jashley92/Tide/releases/tag/v0.1.0-alpha.3"}]""");
        var updates = app.Services.GetRequiredService<UpdateCheckService>();
        updates.Enabled = true;
        (await updates.CheckAsync()).State.Should().Be(UpdateState.Available);

        await using var r = await ComponentHarness.RenderAsync<Tide.Web.Components.Pages.Admin.UpdatesAdmin>(app, "/admin/system/updates");
        var html = await r.HtmlOnce("Upgrade-Tide.ps1");
        html.Should().Contain("How to upgrade to v0.1.0-alpha.3")
            .And.Contain(@"cd .\tide-v0.1.0-alpha.3\deploy")
            .And.Contain(@".\Upgrade-Tide.ps1 -BundleZip ..\..\tide-v0.1.0-alpha.3.zip")
            .And.NotContain("Install-Tide.ps1", "the upgrade script, not a re-run of the installer");
    }
}
