using System.Text;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Tide.Application.Intake;
using Tide.Application.Operations;
using Tide.Domain.Enums;

namespace Tide.UnitTests;

public class ReleaseVersionTests
{
    [Theory]
    [InlineData("v0.1.0", "0.1.0")]
    [InlineData("0.1.0-alpha.2", "0.1.0-alpha.2")]
    [InlineData("V1.2.3-RC.1", "1.2.3-rc.1")]
    [InlineData("0.1.0-alpha.2+0123456789abcdef", "0.1.0-alpha.2")]
    public void Parse_ReadsTagsAndAssemblyVersions(string text, string expected) =>
        ReleaseVersion.Parse(text)!.ToString().Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("1.2.x")]
    [InlineData("1.2.3-preview.1")]
    [InlineData("1.2.3-alpha")]
    [InlineData("-1.2.3")]
    public void Parse_RefusesWhatIsntAReleaseVersion(string? text) => ReleaseVersion.Parse(text).Should().BeNull();

    [Fact]
    public void Versions_OrderAsSemVer_PrereleasesBeforeTheirRelease_NumbersAsNumbers()
    {
        var ordered = new[] { "0.1.0-alpha.2", "0.1.0-alpha.10", "0.1.0-beta.1", "0.1.0-rc.1", "0.1.0", "0.1.1-alpha.1", "0.2.0", "1.0.0" }
            .Select(v => ReleaseVersion.Parse(v)!).ToList();
        ordered.Should().BeInAscendingOrder();
        (ordered[0] < ordered[1]).Should().BeTrue();
        (ordered[4] > ordered[3]).Should().BeTrue();
        (ordered[4] >= ReleaseVersion.Parse("v0.1.0")!).Should().BeTrue();
        (ordered[4] <= ReleaseVersion.Parse("v0.1.0")!).Should().BeTrue();
        ordered[0].CompareTo(null).Should().BePositive();
        ordered[2].Tag.Should().Be("v0.1.0-beta.1");
        ordered[2].IsPrerelease.Should().BeTrue();
        ordered[4].IsPrerelease.Should().BeFalse();
    }

    [Fact]
    public void AppVersion_ADevelopmentBuild_HasNoReleaseVersion()
    {
        AppVersionInfo.ReleaseOf("1.0.0+abcdef0123").Should().BeNull("the SDK's default for a build with no version");
        AppVersionInfo.DisplayOf("1.0.0+abcdef0123").Should().Be("development build (abcdef0)");
        AppVersionInfo.DisplayOf("0.1.0-alpha.2+abcdef0123").Should().Be("0.1.0-alpha.2 (abcdef0)");
        AppVersionInfo.DisplayOf("0.1.0").Should().Be("0.1.0");
        AppVersionInfo.CommitOf("0.1.0+").Should().BeNull();
        AppVersionInfo.Display.Should().NotBeNullOrWhiteSpace();
    }
}

public class ReleaseFeedTests
{
    private const string Repo = "jashley92/Tide";

    internal static string Release(string tag, bool prerelease = false, bool draft = false, string? page = null, string? body = "Notes",
        string? asset = null) =>
        $$"""
        {"tag_name":"{{tag}}","prerelease":{{(prerelease ? "true" : "false")}},"draft":{{(draft ? "true" : "false")}},
         "html_url":"{{page ?? $"https://github.com/{Repo}/releases/tag/{tag}"}}","published_at":"2026-10-28T12:00:00Z","body":{{(body is null ? "null" : $"\"{body}\"")}},
         "assets":[{"name":"tide-{{tag}}.zip","browser_download_url":"{{asset ?? $"https://github.com/{Repo}/releases/download/{tag}/tide-{tag}.zip"}}"},
                   {"name":"tide-{{tag}}.zip.sha256","browser_download_url":"https://github.com/{{Repo}}/releases/download/{{tag}}/tide-{{tag}}.zip.sha256"}]}
        """;

    internal static string Feed(params string[] releases) => "[" + string.Join(",", releases) + "]";

    [Fact]
    public void Newer_ListsReleasesAfterTheRunningOne_NewestFirst_WithTheirBundles()
    {
        var json = Feed(Release("v0.1.0-alpha.3", prerelease: true), Release("v0.1.0-alpha.1", prerelease: true), Release("v0.1.0-alpha.10", prerelease: true));
        var newer = ReleaseFeed.Newer(json, Repo, ReleaseVersion.Parse("0.1.0-alpha.2"), includePrereleases: true);
        newer.Select(r => r.Tag).Should().Equal("v0.1.0-alpha.10", "v0.1.0-alpha.3");
        newer[0].BundleUrl.Should().Be($"https://github.com/{Repo}/releases/download/v0.1.0-alpha.10/tide-v0.1.0-alpha.10.zip");
        newer[0].Sha256Url.Should().EndWith(".zip.sha256");
        newer[0].PublishedAtUtc.Should().Be(new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero));
        newer[0].Notes.Should().Be("Notes");
    }

    [Fact]
    public void Newer_SkipsDrafts_OtherTags_AndPrereleasesUnlessAsked()
    {
        var json = Feed(Release("v0.3.0", draft: true), Release("v0.2.0-alpha.1", prerelease: true), Release("nightly"), Release("v0.2.0"),
            Release("v0.2.0"), "42", Release("v0.2.1-beta.1"));
        ReleaseFeed.Newer(json, Repo, ReleaseVersion.Parse("0.1.0"), includePrereleases: false).Select(r => r.Tag).Should().Equal("v0.2.0");
        ReleaseFeed.Newer(json, Repo, ReleaseVersion.Parse("0.1.0"), includePrereleases: true).Select(r => r.Tag)
            .Should().Equal("v0.2.1-beta.1", "v0.2.0", "v0.2.0-alpha.1");
        ReleaseFeed.Newer(json, Repo, null, includePrereleases: false).Should().ContainSingle();
    }

    [Fact]
    public void Newer_KeepsOnlyLinksIntoThisRepositorysOwnReleases()
    {
        var json = Feed(Release("v0.2.0", page: "https://evil.example/phish", asset: "https://evil.example/tide-v0.2.0.zip", body: null));
        var r = ReleaseFeed.Newer(json, Repo, ReleaseVersion.Parse("0.1.0"), includePrereleases: false).Single();
        r.PageUrl.Should().Be($"https://github.com/{Repo}/releases/tag/v0.2.0");
        r.BundleUrl.Should().BeNull();
        r.Notes.Should().BeEmpty();
    }

    [Fact]
    public void Newer_CapsLongNotes_AndRefusesWhatIsntAList()
    {
        var json = Feed(Release("v0.2.0", body: new string('x', ReleaseFeed.MaxNotesChars + 10)));
        ReleaseFeed.Newer(json, Repo, null, true)[0].Notes.Should().HaveLength(ReleaseFeed.MaxNotesChars + 3);
        var act = () => ReleaseFeed.Newer("""{"message":"Not Found"}""", Repo, null, true);
        act.Should().Throw<System.Text.Json.JsonException>();
    }

    [Theory]
    [InlineData("jashley92/Tide", true)]
    [InlineData("owner/name.with-dots_1", true)]
    [InlineData("", false)]
    [InlineData("no-slash", false)]
    [InlineData("a/b/c", false)]
    [InlineData("../etc/passwd", false)]
    public void IsRepository_TakesOwnerAndName(string repo, bool ok) => ReleaseFeed.IsRepository(repo).Should().Be(ok);
}

public class SecretMaskTests
{
    [Theory]
    [InlineData("Email:SmtpPassword", true)]
    [InlineData("Cve:NvdApiKey", true)]
    [InlineData("Some:ClientSecret", true)]
    [InlineData("Some:AccessToken", true)]
    [InlineData("DataProtection:KeyPath", false)]
    [InlineData("Email:SmtpUseDefaultCredentials", false)]
    [InlineData("Auth:Mode", false)]
    [InlineData("Email:SmtpUsername", false)]
    public void LooksSecret_ByTheKeysOwnName(string key, bool secret) => SecretMask.LooksSecret(key).Should().Be(secret);

    [Fact]
    public void Display_ShowsASecretOnlyAsSet_EvenIfListedAsPlain()
    {
        SecretMask.Display("Email:SmtpPassword", SettingKind.Secret, "hunter2").Should().Be(SecretMask.SetSecret);
        SecretMask.Display("Other:ApiKey", SettingKind.Plain, "abc123").Should().Be(SecretMask.SetSecret, "the name says it's a key");
        SecretMask.Display("Email:SmtpPassword", SettingKind.Secret, "").Should().BeNull("not set");
        SecretMask.Display("Org:TeamName", SettingKind.Plain, "CTI").Should().Be("CTI");
    }

    [Fact]
    public void ConnectionString_KeepsTheServerAndDatabase_NeverAPasswordOrAPartNobodyListed()
    {
        var shown = SecretMask.Display("ConnectionStrings:Default", SettingKind.ConnectionString,
            "Server=SQL01\\TIDE;Database=Tide;User ID=tide_app;Password=S3cret!;Encrypt=True;Column Encryption Key Store Provider=x");
        shown.Should().Contain("server=SQL01\\TIDE").And.Contain("database=Tide").And.Contain("encrypt=True");
        shown.Should().NotContain("S3cret").And.NotContain("tide_app").And.NotContain("Provider=x");
        shown.Should().Contain("password=" + SecretMask.Hidden);
        SecretMask.ConnectionString("Data Source=App_Data/tide.db").Should().Be("data source=App_Data/tide.db");
        SecretMask.ConnectionString("this isn't = = one;;=").Should().StartWith(SecretMask.SetSecret);
    }

    [Fact]
    public void Url_HidesUserInfo_AndKeysInTheQuery()
    {
        var proxy = SecretMask.Url("http://svc:P%40ss@proxy.example.org:8080/");
        proxy.Should().NotContain("svc").And.NotContain("P%40ss").And.Contain("proxy.example.org:8080").And.Contain(SecretMask.Hidden + "@");
        var feed = SecretMask.Url("https://feeds.example.org/rss?apikey=abc123&topic=ransomware&token=zzz");
        feed.Should().NotContain("abc123").And.NotContain("zzz").And.Contain("topic=ransomware").And.Contain("apikey=" + SecretMask.Hidden);
        SecretMask.Url("https://services.nvd.nist.gov/rest/json/cves/2.0").Should().Be("https://services.nvd.nist.gov/rest/json/cves/2.0");
        SecretMask.Url("not a url").Should().Be("not a url");
        SecretMask.Url("user:pw@host").Should().Be(SecretMask.Hidden);
        SecretMask.UrlHasSecret("https://x.example/?key=1").Should().BeTrue();
        SecretMask.UrlHasSecret("https://x.example/?q=1").Should().BeFalse();
        SecretMask.UrlHasSecret("nope").Should().BeFalse();
        SecretMask.Display("App:BaseUrl", SettingKind.Url, "https://a:b@tide.example.org").Should().NotContain(":b@");
    }

    [Fact]
    public void Scrub_HidesCredentialsInErrorText()
    {
        var text = SecretMask.Scrub("Login failed: Password=hunter2; apikey: 'abc' token=xyz at https://bob:pw@relay.example.org/");
        text.Should().NotContain("hunter2").And.NotContain("abc").And.NotContain("xyz").And.NotContain("bob:pw");
        text.Should().Contain("Password=" + SecretMask.Hidden).And.Contain("relay.example.org");
        SecretMask.Scrub("The relay refused the message.").Should().Be("The relay refused the message.");
    }
}

public class SettingsCatalogTests
{
    [Fact]
    public void TheCatalog_MarksEverySecretLookingKeyAsSecret()
    {
        SettingsCatalog.Server.Where(s => SecretMask.LooksSecret(s.Key)).Should().OnlyContain(s => s.Kind == SettingKind.Secret);
        SettingsCatalog.Server.Single(s => s.Key == "ConnectionStrings:Default").Kind.Should().Be(SettingKind.ConnectionString);
        SettingsCatalog.Server.Select(s => s.Key).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void InApp_ListsWhatAdministratorsSave_NotTheAppsBookkeeping()
    {
        SettingsCatalog.IsInApp("Distribution:TeamRecipients").Should().BeTrue();
        SettingsCatalog.IsInApp("Actions:Sla:Priority:P1").Should().BeTrue();
        SettingsCatalog.IsInApp(SettingsCatalog.UpdateCheckKey).Should().BeTrue();
        SettingsCatalog.IsInApp("Cve:LastRun").Should().BeFalse();
        SettingsCatalog.InApp.Should().OnlyContain(s => s.InApp && s.EditedIn!.Length > 0 && !s.EditedIn.StartsWith('/'), "links stay relative for a sub-path install");
    }
}

public class JobMonitorTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 29, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ARun_IsRecorded_WithItsDetail()
    {
        var clock = new TestClock(Start);
        var jobs = new JobMonitor(clock);
        jobs.Get(JobMonitor.FeedPoll).LastSucceeded.Should().BeNull("not run yet");
        jobs.Scheduled(JobMonitor.FeedPoll, true, clock.Now.AddMinutes(1));
        await jobs.RunAsync(JobMonitor.FeedPoll, () => Task.FromResult<string?>("3 new items"));
        var s = jobs.Get(JobMonitor.FeedPoll);
        s.Should().Match<JobState>(x => x.Enabled && x.LastSucceeded == true && x.LastDetail == "3 new items" && !x.Running && x.NextDueUtc == clock.Now.AddMinutes(1));
        jobs.States.Select(x => x.Job).Should().Equal(JobMonitor.All);
    }

    [Fact]
    public async Task AFailedRun_IsRecordedScrubbedAndCut_AndThrownOn()
    {
        var jobs = new JobMonitor(new TestClock(Start));
        var act = () => jobs.RunAsync(JobMonitor.Reminders, () => throw new InvalidOperationException("SMTP said no; Password=hunter2 " + new string('x', 500)));
        await act.Should().ThrowAsync<InvalidOperationException>();
        var s = jobs.Get(JobMonitor.Reminders);
        s.LastSucceeded.Should().BeFalse();
        s.LastError.Should().StartWith("InvalidOperationException: SMTP said no").And.NotContain("hunter2");
        s.LastError!.Length.Should().Be(JobMonitor.MaxErrorChars + 1);
        s.LastErrorAtUtc.Should().NotBeNull();

        // A later success keeps the earlier error for the page to show as earlier.
        jobs.Succeeded(JobMonitor.Reminders, DateTimeOffset.UtcNow, null);
        jobs.Get(JobMonitor.Reminders).Should().Match<JobState>(x => x.LastSucceeded == true && x.LastError != null);
    }

    [Fact]
    public async Task ACancelledRun_IsntAFailure()
    {
        var jobs = new JobMonitor(new TestClock(Start));
        var act = () => jobs.RunAsync(JobMonitor.Prune, () => throw new OperationCanceledException());
        await act.Should().ThrowAsync<OperationCanceledException>();
        jobs.Get(JobMonitor.Prune).Should().Match<JobState>(x => x.LastSucceeded == null && !x.Running);
    }
}

public class InstallAnswersTests
{
    [Fact]
    public void FromSettings_TakesTheInstanceAndDatabase_NeverACredential()
    {
        var f = InstallFacts.FromSettings("SqlServer", "Server=SQL01\\SHARED;Database=TideProd;User ID=sa;Password=hunter2;Encrypt=True", false,
            "E:\\TideData\\files", "E:\\TideData\\dp-keys", "https://tide.contoso.com/", "/tide/");
        f.Should().Match<InstallFacts>(x => x.SqlInstance == "SQL01\\SHARED" && x.DbName == "TideProd" && !x.AppMigrates
            && x.DataRoot == "E:\\TideData" && x.Hostname == "tide.contoso.com" && x.AppPath == "tide");
        InstallAnswers.Write(f).Should().NotContain("hunter2").And.NotContain("sa;").And.Contain("SchemaMode            = 'DbaApplies'");
    }

    [Fact]
    public void FromSettings_LeavesWhatItCantKnowUnset()
    {
        var f = InstallFacts.FromSettings("Sqlite", "Data Source=App_Data/tide.db", true, "App_Data/files", "", "", "");
        f.Should().Match<InstallFacts>(x => x.SqlInstance == null && x.DbName == null && x.DataRoot == null && x.Hostname == null && x.AppPath == null);
        InstallFacts.FromSettings("SqlServer", "=;=", true, null, "D:\\Data\\dp-keys\\", null, null)
            .Should().Match<InstallFacts>(x => x.SqlInstance == null && x.DataRoot == "D:\\Data");
    }

    [Fact]
    public void Write_LaysOutTheTemplatesKeys_QuotedForPowerShell_WithTheUnknownsMarked()
    {
        var text = InstallAnswers.Write(new InstallFacts
        {
            SqlInstance = "SQL01", DbName = "Tide", TeamName = "O'Brien's team", TimeZone = "America/New_York", MeetingTime = "10:30",
            Groups = new Dictionary<AppRole, IReadOnlyList<string>>
            {
                [AppRole.Analyst] = ["CONTOSO\\CTI-Analysts", "CONTOSO\\CTI-Leads"],
                [AppRole.Admin] = ["CONTOSO\\CTI-Admins"],
            },
            Origin = "exported for a test",
        });
        text.Should().StartWith("# TIDE install answers file, exported for a test.");
        text.Should().Contain("TeamName              = 'O''Brien''s team'");
        text.Should().Contain("AdGroupAnalysts       = 'CONTOSO\\CTI-Analysts'   # also mapped in the app: CONTOSO\\CTI-Leads");
        text.Should().Contain("AdGroupReaders        = ''");
        text.Should().Contain("AppAccount            = ''   # not known on this server");
        text.Should().Contain("ParentSite            = ''").And.Contain("AppPath               = ''");
        foreach (var key in new[] { "SqlInstance", "DbName", "AppAccount", "SchemaMode", "SiteName", "AppPoolName", "Hostname", "SitePath", "DataRoot",
                     "CertificateThumbprint", "ParentSite", "AppPath", "AdGroupAnalysts", "AdGroupActionOwners", "AdGroupReaders", "AdGroupAdmins",
                     "TimeZone", "MeetingTime", "TeamName" })
            text.Should().Contain("    " + key + " ", "Install-Tide.ps1 reads {0}", key);
        text.TrimEnd().Should().EndWith("}");
    }

    [Fact]
    public void Write_UnderAnotherSite_SaysSo()
    {
        var text = InstallAnswers.Write(new InstallFacts { AppPath = "tide" });
        text.Should().Contain("application under another site, at /tide").And.Contain("AppPath               = 'tide'")
            .And.Contain("ParentSite            = ''   # not known on this server: the site TIDE is an application under");
        InstallAnswers.Escape("a'b\r\nc").Should().Be("a''b c");
    }
}

public class ConfigBundleTests
{
    private static ConfigBundle Empty() => new(ConfigBundle.FormatName, ConfigBundle.CurrentVersion, DateTimeOffset.UtcNow, "me", "dev", [], [], [], [], []);

    [Fact]
    public void Parse_ReadsBackWhatWasWritten()
    {
        var bundle = Empty() with { Settings = [new("Distribution:PublicMaxTlp", "Green")] };
        var back = ConfigBundle.Parse(Encoding.UTF8.GetBytes(bundle.ToJson()));
        back.Settings.Should().ContainSingle().Which.Should().Be(new BundleSetting("Distribution:PublicMaxTlp", "Green"));
    }

    [Fact]
    public void Parse_RefusesOtherFiles_OtherVersions_AndTooBig()
    {
        var read = (string json) => () => ConfigBundle.Parse(Encoding.UTF8.GetBytes(json));
        read("not json").Should().Throw<ArgumentException>().WithMessage("*isn't a TIDE configuration bundle*");
        read("""{"format":"something-else","version":1}""").Should().Throw<ArgumentException>();
        read("""{"format":"tide-config-bundle","version":2}""").Should().Throw<ArgumentException>().WithMessage("*version 2*");
        var nulls = ConfigBundle.Parse(Encoding.UTF8.GetBytes("""{"format":"tide-config-bundle","version":1}"""));
        nulls.Sources.Should().BeEmpty();
        nulls.Settings.Should().BeEmpty();
        var big = () => ConfigBundle.Parse(new byte[ConfigBundle.MaxBytes + 1]);
        big.Should().Throw<ArgumentException>().WithMessage("*at most*");
    }

    [Theory]
    [InlineData("Distribution:PublicMaxTlp", "Green", null)]
    [InlineData("Distribution:PublicMaxTlp", "Purple", "not a TLP level")]
    [InlineData("Actions:Sla:Priority:P1", "3", null)]
    [InlineData("Actions:Sla:Priority:P1", "-3", "not a number*")]
    [InlineData("Actions:Sla:Priority:P1", "9999", "not a number*")]
    [InlineData("Updates:CheckEnabled", "true", null)]
    [InlineData("Updates:CheckEnabled", "yes", "not true or false")]
    [InlineData("Distribution:TeamRecipients", "a@example.org; b@example.org", null)]
    [InlineData("Distribution:TeamRecipients", "a@example.org; nope", "not an email address: nope")]
    [InlineData("Cve:LastRun", "2026-10-01", "not a setting*")]
    [InlineData("ConnectionStrings:Default", "Server=x", "not a setting*")]
    public void SettingProblem_ChecksAsTheAppsOwnPagesDo(string key, string value, string? problem)
    {
        var why = ConfigBundleService.SettingProblem(new BundleSetting(key, value));
        if (problem is null) why.Should().BeNull();
        else why.Should().Match(problem);
    }

    [Fact]
    public void Preview_CountsTheChanges()
    {
        var p = new BundlePreview(Empty(), [new("A", "1", BundleChangeKind.Add), new("A", "2", BundleChangeKind.Unchanged), new("A", "3", BundleChangeKind.Skipped)]);
        p.Count(BundleChangeKind.Add).Should().Be(1);
        p.HasChanges.Should().BeTrue();
        new BundlePreview(Empty(), [new("A", "2", BundleChangeKind.Unchanged)]).HasChanges.Should().BeFalse();
    }
}

public class UpdateCheckTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 29, 9, 0, 0, TimeSpan.Zero);

    private const string Repo = "jashley92/Tide";

    private sealed class Http : IOutboundHttp
    {
        public Func<string, CancellationToken, Task<HttpFetch>> Answer { get; set; } = (_, _) => Task.FromResult(new HttpFetch(FetchStatus.Failed, Error: "unset"));
        public List<(string Url, OutboundRequest? Request)> Requests { get; } = [];

        public Task<HttpFetch> GetAsync(string url, OutboundRequest? request = null, CancellationToken ct = default)
        {
            Requests.Add((url, request));
            return Answer(url, ct);
        }

        public void Returns(string json) => Answer = (_, _) => Task.FromResult(new HttpFetch(FetchStatus.Ok, Encoding.UTF8.GetBytes(json)));
    }

    private static UpdateCheckService Service(Http http, string? running = "0.1.0-alpha.2", bool enabled = true, string repo = Repo, TestClock? clock = null) =>
        new(http, new StaticOptionsMonitor<UpdateOptions>(new UpdateOptions { CheckEnabled = enabled, Repository = repo }), NullLogger<UpdateCheckService>.Instance, clock ?? new TestClock(Start),
            running is null ? null : ReleaseVersion.Parse(running), useBuildVersion: false);

    [Fact]
    public async Task Off_ItAsksNothing()
    {
        var http = new Http();
        using var updates = Service(http, enabled: false);
        (await updates.CheckAsync(manual: true)).State.Should().Be(UpdateState.Disabled);
        http.Requests.Should().BeEmpty("an install that hasn't turned it on never reaches out");
        updates.UpdateAvailable.Should().BeFalse();
    }

    [Fact]
    public async Task On_ItFindsANewerPrerelease_ForAServerRunningOne()
    {
        var http = new Http();
        http.Returns(ReleaseFeedTests.Feed(ReleaseFeedTests.Release("v0.1.0-alpha.3", prerelease: true), ReleaseFeedTests.Release("v0.1.0-alpha.2", prerelease: true)));
        using var updates = Service(http);
        updates.Status.State.Should().Be(UpdateState.NotChecked);
        var changed = 0;
        updates.Changed += () => changed++;
        var s = await updates.CheckAsync();
        s.State.Should().Be(UpdateState.Available);
        s.Latest!.Tag.Should().Be("v0.1.0-alpha.3");
        updates.UpdateAvailable.Should().BeTrue();
        changed.Should().Be(1);
        http.Requests.Single().Url.Should().Be($"https://api.github.com/repos/{Repo}/releases?per_page=30");
        http.Requests.Single().Request!.Headers!["Accept"].Should().Be("application/vnd.github+json");
    }

    [Fact]
    public async Task AServerOnARelease_IgnoresPrereleases()
    {
        var http = new Http();
        http.Returns(ReleaseFeedTests.Feed(ReleaseFeedTests.Release("v0.2.0-alpha.1", prerelease: true), ReleaseFeedTests.Release("v0.1.0")));
        using var updates = Service(http, running: "0.1.0");
        (await updates.CheckAsync()).State.Should().Be(UpdateState.UpToDate);
    }

    [Fact]
    public async Task ADevelopmentBuild_ListsTheLatestReleases_WithNothingToCompare()
    {
        var http = new Http();
        http.Returns(ReleaseFeedTests.Feed([.. Enumerable.Range(1, 5).Select(n => ReleaseFeedTests.Release($"v0.1.0-alpha.{n}", prerelease: true))]));
        using var updates = Service(http, running: null);
        var s = await updates.CheckAsync();
        s.State.Should().Be(UpdateState.DevBuild);
        s.Releases.Select(r => r.Tag).Should().Equal("v0.1.0-alpha.5", "v0.1.0-alpha.4", "v0.1.0-alpha.3");
        updates.UpdateAvailable.Should().BeFalse();
    }

    [Fact]
    public async Task Offline_ItFailsGracefully_AndKeepsWhatItFoundBefore()
    {
        var http = new Http();
        http.Returns(ReleaseFeedTests.Feed(ReleaseFeedTests.Release("v0.1.0-alpha.3", prerelease: true)));
        var clock = new TestClock(Start);
        using var updates = Service(http, clock: clock);
        await updates.CheckAsync();

        http.Answer = (_, _) => Task.FromResult(new HttpFetch(FetchStatus.Failed, Error: "api.github.com couldn't be reached."));
        var s = await updates.CheckAsync();
        s.State.Should().Be(UpdateState.Failed);
        s.Problem.Should().Be("api.github.com couldn't be reached", "the page adds its own full stop");
        s.Releases.Should().ContainSingle("an earlier answer stays listed");

        http.Answer = (_, _) => throw new HttpRequestException("no route");
        (await updates.CheckAsync()).Problem.Should().Be("couldn't reach GitHub (HttpRequestException)");

        http.Returns("<html>captive portal</html>");
        (await updates.CheckAsync()).Problem.Should().Be("the answer wasn't a list of releases");
    }

    [Theory]
    [InlineData("The server answered HTTP 404 Not Found.", "GitHub has no public releases for jashley92/Tide (404)*")]
    [InlineData("The server answered HTTP 403 Forbidden.", "*may be rate-limiting*")]
    [InlineData(null, "GitHub didn't answer")]
    public async Task GitHubsRefusals_AreExplained(string? error, string expected)
    {
        var http = new Http { Answer = (_, _) => Task.FromResult(new HttpFetch(FetchStatus.Failed, Error: error)) };
        using var updates = Service(http);
        (await updates.CheckAsync()).Problem.Should().Match(expected);
    }

    [Fact]
    public async Task ASlowAnswer_TimesOut_WithoutThrowing()
    {
        var http = new Http
        {
            // As the outbound client reports its own cancellation: the request's token fired, not the caller's.
            Answer = (_, _) => throw new TaskCanceledException("timed out"),
        };
        using var updates = Service(http);
        var s = await updates.CheckAsync();
        s.State.Should().Be(UpdateState.Failed);
        s.Problem.Should().Be("no answer within 10 seconds");

        http.Answer = async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpFetch(FetchStatus.Ok);
        };
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var act = () => updates.CheckAsync(ct: cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>("the caller's own cancellation is passed on");
    }

    [Fact]
    public async Task ABadRepositorySetting_IsReported_NotFetched()
    {
        var http = new Http();
        using var updates = Service(http, repo: "https://github.com/x");
        (await updates.CheckAsync()).Problem.Should().Contain("Updates:Repository");
        http.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task CheckNow_MoreThanOnceAMinute_ReturnsTheLastAnswer()
    {
        var http = new Http();
        http.Returns("[]");
        var clock = new TestClock(Start);
        using var updates = Service(http, clock: clock);
        await updates.CheckAsync(manual: true);
        await updates.CheckAsync(manual: true);
        http.Requests.Should().HaveCount(1);
        clock.Now += UpdateCheckService.ManualThrottle;
        await updates.CheckAsync(manual: true);
        http.Requests.Should().HaveCount(2);
        updates.Status.CheckedAtUtc.Should().Be(clock.Now);
    }

    [Fact]
    public void TheDefaults_AreOff_AndPointAtTide()
    {
        var o = new UpdateOptions();
        o.CheckEnabled.Should().BeFalse("many SOC servers have no internet access");
        o.Repository.Should().Be(Repo);
        o.CheckIntervalHours.Should().Be(24);
    }
}
