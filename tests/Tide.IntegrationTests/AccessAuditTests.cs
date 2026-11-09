using System.Net;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Actions;
using Tide.Application.Articles;
using Tide.Application.History;
using Tide.Application.Operations;
using Tide.Application.Preferences;
using Tide.Application.Settings;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>Downloads over HTTP leave a record: an export above TLP:GREEN, every file, the configuration bundle and the
/// audit log's CSV are in the audit log, as the Download kind and the downloaded action.</summary>
public class DownloadAuditTests(TideApp app) : IClassFixture<TideApp>
{
    private async Task<T> As<T>(Func<IServiceProvider, Task<T>> work)
    {
        await using var scope = SignedIn(app, SignIn.Analyst);
        return await work(scope.ServiceProvider);
    }

    // The audit log's downloads of one record, newest first, as the page reads them.
    private Task<IReadOnlyList<AuditRow>> Downloads(string key) => As(async sp =>
        (await sp.GetRequiredService<AuditLogService>().PageAsync(new AuditFilter { Kind = DownloadAudit.EntityType, Key = key })).Rows);

    private async Task<(Guid ActionId, Guid FileId)> ActionWithFile(string name, byte[] bytes) => await As(async sp =>
    {
        var team = (await sp.GetRequiredService<TeamService>().ListAsync())[0].Id;
        var actions = sp.GetRequiredService<ActionService>();
        var number = await actions.CreateAsync(new ActionForm { Title = "download-audit " + name, TeamId = team });
        var id = (await actions.GetAsync(number))!.Action.Id;
        var file = (await sp.GetRequiredService<ActionFileService>().AddAsync(id, [new ActionFileUpload(name, bytes)])).Single();
        return (id, file.FileId);
    });

    private Task SetZip(Application.Files.ZipDownloads mode) => As(async sp =>
    {
        await sp.GetRequiredService<OperationalSettingsService>().SaveAsync(new Dictionary<string, string?> { [OperationalSettings.ZipDownloads] = mode.ToString() });
        return true;
    });

    [Fact]
    public async Task AnExport_IsAudited_OnlyAboveGreen_WithItsCeilingAndSize()
    {
        var article = await As(sp => sp.GetRequiredService<ArticleService>().CreateAsync("Download audit export", "Vendor", null, TlpLevel.Amber));
        var key = $"article/{article}";

        (await app.CreateClient().GetAsync($"/exports/article/{article}?tlp=green")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Downloads(key)).Should().BeEmpty("an export held to TLP:GREEN is in the access log only");

        var amber = await app.CreateClient().GetAsync($"/exports/article/{article}?format=csv&tlp=amber");
        amber.StatusCode.Should().Be(HttpStatusCode.OK);
        var size = (await amber.Content.ReadAsByteArrayAsync()).Length;
        (await app.CreateClient().GetAsync($"/exports/article/{article}")).StatusCode.Should().Be(HttpStatusCode.OK, "no ceiling is every level");

        var rows = await Downloads(key);
        rows.Should().HaveCount(2);
        rows.Should().OnlyContain(r => r.Action == AuditAction.Downloaded && r.Kind == "Download" && r.What == "downloaded");
        var csv = rows.Single(r => r.Record.EndsWith(".csv", StringComparison.Ordinal));
        csv.Record.Should().Be("tide-article-download-audit-export-amber.csv");
        csv.Changes.Should().Contain(c => c.Field == "TLP ceiling" && c.After == "TLP:AMBER");
        csv.Changes.Should().Contain(c => c.Field == "Route" && c.After == "/exports/{scope}/{key}");
        csv.Changes.Should().Contain(c => c.Field == "Bytes" && c.After == size.ToString(System.Globalization.CultureInfo.InvariantCulture));
        rows.Single(r => r != csv).Changes.Should().Contain(c => c.Field == "TLP ceiling" && c.After == "TLP:RED");
    }

    [Fact]
    public async Task EveryFileDownload_PlainOrZipped_IsAudited()
    {
        var (_, fileId) = await ActionWithFile("audited.log", Samples.Text("one line"));
        (await app.CreateClient().GetAsync($"/files/{fileId}/download")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await app.CreateClient().GetAsync($"/files/{fileId}/zip")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await app.CreateClient().GetAsync($"/files/{Guid.NewGuid()}/download")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var rows = await Downloads(fileId.ToString());
        rows.Select(r => r.Record).Should().BeEquivalentTo(["audited.log", "audited.log.zip"]);
        rows.Should().OnlyContain(r => r.Changes.Any(c => c.Field == "Bytes" && c.After == "8"), "the zip's record gives the file's own size");
        rows.Select(r => r.Changes.Single(c => c.Field == "Route").After).Should().BeEquivalentTo(["/files/{id:guid}/download", "/files/{id:guid}/zip"]);
    }

    [Fact]
    public async Task TheConfigurationBundle_AndTheAuditCsv_AreAudited_AndTheLogFindsThemByActionAndKind()
    {
        (await app.CreateClient().GetAsync("/admin/system/config-bundle.json")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await app.CreateClient().GetAsync("/admin/system/tide.config.psd1")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await app.CreateClient().GetAsync("/admin/system/audit.csv")).StatusCode.Should().Be(HttpStatusCode.OK);

        (await Downloads("/admin/system/config-bundle.json")).Should().ContainSingle().Which.Record.Should().StartWith("tide-config-bundle-");
        (await Downloads("/admin/system/tide.config.psd1")).Should().ContainSingle();
        (await Downloads("/admin/system/audit.csv")).Should().ContainSingle().Which.Record.Should().StartWith("tide-audit-log-");

        var byAction = await As(async sp => (await sp.GetRequiredService<AuditLogService>().PageAsync(new AuditFilter { Action = AuditAction.Downloaded })).Rows);
        byAction.Should().NotBeEmpty().And.OnlyContain(r => r.EntityType == DownloadAudit.EntityType);
        AuditKinds.All.Should().Contain(k => k.Type == DownloadAudit.EntityType && k.Name == "Download");

        var csv = await (await app.CreateClient().GetAsync("/admin/system/audit.csv?kind=Download")).Content.ReadAsStringAsync();
        csv.Should().Contain(",downloaded,Download,");
    }

    [Fact]
    public async Task ARiskyFilesPlainDownload_GoesToTheZip_UnlessAsIsWasChosen_AndUnderAlwaysIsRefused()
    {
        var (_, risky) = await ActionWithFile("dropper.exe", Samples.FakeProgram);
        var (_, calm) = await ActionWithFile("calm.txt", Samples.Text("calm"));
        var client = app.CreateClient(new() { AllowAutoRedirect = false });

        await SetZip(Application.Files.ZipDownloads.RiskyOnly);
        var bare = await client.GetAsync($"/files/{risky}/download");
        bare.StatusCode.Should().Be(HttpStatusCode.Redirect, "a bare link (one in a comment, say) doesn't send a file that can run");
        bare.Headers.Location!.OriginalString.Should().EndWith($"/files/{risky}/zip");
        var asIs = await client.GetAsync($"/files/{risky}/download?plain=1");
        asIs.StatusCode.Should().Be(HttpStatusCode.OK);
        (await asIs.Content.ReadAsByteArrayAsync()).Should().Equal(Samples.FakeProgram);
        (await client.GetAsync($"/files/{calm}/download")).StatusCode.Should().Be(HttpStatusCode.OK, "a file that can't run isn't zipped first");
        (await Downloads(risky.ToString())).Should().ContainSingle("the redirect sent nothing");

        try
        {
            await SetZip(Application.Files.ZipDownloads.Always);
            (await client.GetAsync($"/files/{calm}/download")).StatusCode.Should().Be(HttpStatusCode.Redirect);
            var refused = await client.GetAsync($"/files/{risky}/download?plain=1");
            refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await refused.Content.ReadAsStringAsync()).Should().Contain("zip");
            (await client.GetAsync($"/files/{calm}/download?plain=1")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await client.GetAsync($"/files/{calm}/zip")).StatusCode.Should().Be(HttpStatusCode.OK);

            await SetZip(Application.Files.ZipDownloads.Never);
            (await client.GetAsync($"/files/{risky}/download")).StatusCode.Should().Be(HttpStatusCode.OK, "under Never every file is sent as it is");
        }
        finally { await SetZip(Application.Files.ZipDownloads.RiskyOnly); }
    }
}

/// <summary>Reminders go only to people who still have access: roles (as last seen) that show what the email shows, and a
/// sign-in within Notifications:ActiveWithinDays. A team mailbox is never held to it; the run's note counts who was left out.</summary>
public class InactiveRecipientTests : IDisposable
{
    private readonly TestDb _db = new();

    public InactiveRecipientTests() => _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };

    public void Dispose() => _db.Dispose();

    private async Task Person(string sid, string roles, int daysSinceSeen = 0)
    {
        await using var ctx = _db.Context();
        ctx.Users.Add(new AppUser
        {
            Sid = sid, DisplayName = sid, Email = sid.ToLowerInvariant() + "@example.org", RolesCsv = roles,
            LastSeenUtc = DateTimeOffset.UtcNow.AddDays(-daysSinceSeen),
        });
        await ctx.SaveChangesAsync();
    }

    private async Task<Guid> TeamOf(string? mailbox, params string[] members)
    {
        var team = await _db.Teams().AddAsync("Team " + Guid.NewGuid().ToString("N")[..6], null, mailbox);
        foreach (var m in members) await _db.Teams().AddMemberAsync(team, m, m);
        return team;
    }

    private IEnumerable<string> To => _db.Email.Sent.SelectMany(m => m.To);

    [Fact]
    public async Task Overdue_SkipsAMemberWhoHasntSignedInLately_OrHasNoRoleToReadActions_AndSaysHowMany()
    {
        await Person("S-ACTIVE", "ActionOwner");
        await Person("S-STALE", "Analyst", daysSinceSeen: 40);
        await Person("S-READER", "Reader");
        var team = await TeamOf(null, "S-ACTIVE", "S-STALE", "S-READER");
        await _db.Actions().CreateAsync(new ActionForm { Title = "Patch it", TeamId = team, DueDate = _db.Clock.Today.AddDays(-3) });
        var log = new ReminderLog();

        (await _db.Reminders(log: log).RunAsync(ReminderKind.Overdue)).Should().Be(1);

        To.Should().Equal("s-active@example.org");
        log.Last(ReminderKind.Overdue)!.Note.Should().Be(ReminderService.InactiveNote(2, 30));
    }

    [Fact]
    public async Task AnInactiveAssignee_IsPassedOver_ForTheTeamMailbox_WhichIsNeverHeldToTheRule()
    {
        await Person("S-GONE", "Analyst", daysSinceSeen: 90);
        await Person("S-NOROLE", "");
        var team = await TeamOf("defense@example.org", "S-NOROLE");
        await _db.Actions().CreateAsync(new ActionForm { Title = "Gone's", TeamId = team, DueDate = _db.Clock.Today, AssigneeId = "S-GONE", AssigneeName = "Gone" });
        var log = new ReminderLog();

        (await _db.Reminders(log: log).RunAsync(ReminderKind.DueSoon)).Should().Be(1);

        To.Should().Equal("defense@example.org");
        log.Last(ReminderKind.DueSoon)!.Note.Should().Be(ReminderService.InactiveNote(1, 30));
    }

    [Fact]
    public async Task TheSetting_SetsHowLatelyCounts()
    {
        await Person("S-STALE", "Analyst", daysSinceSeen: 40);
        var team = await TeamOf(null, "S-STALE");
        await _db.Actions().CreateAsync(new ActionForm { Title = "Patch it", TeamId = team, DueDate = _db.Clock.Today.AddDays(-3) });
        var log = new ReminderLog();

        (await _db.Reminders(new ReminderOptions { ActiveWithinDays = 60 }, log: log).RunAsync(ReminderKind.Overdue)).Should().Be(1);
        To.Should().Equal("s-stale@example.org");
        log.Last(ReminderKind.Overdue)!.Note.Should().BeNull();

        new ReminderOptions().ActiveWithinDays.Should().Be(30, "the default");
        new ReminderOptions { ActiveWithinDays = 0 }.ActiveDays.Should().Be(1);
        OperationalSettings.Normalize(OperationalSettings.ActiveWithinDays, " 45 ").Should().Be("45");
        OperationalSettings.Problem(OperationalSettings.ActiveWithinDays, "0").Should().Be("Days since someone last signed in is a whole number from 1 to 365.");
        OperationalSettings.Problem(OperationalSettings.ActiveWithinDays, "366").Should().NotBeNull();
        SettingsCatalog.InApp.Should().ContainSingle(s => s.Key == "Notifications:ActiveWithinDays" && s.Group == "Reminders" && s.EditedIn == "admin/teams");
    }

    [Fact]
    public async Task TheWeeklyDigest_GoesOnlyToActiveMembers()
    {
        await Person("S-ACTIVE", "Viewer");
        await Person("S-READER", "Reader");
        var team = await TeamOf(null, "S-ACTIVE", "S-READER");
        await _db.Actions().CreateAsync(new ActionForm { Title = "Open one", TeamId = team });
        var log = new ReminderLog();

        (await _db.Reminders(log: log).RunAsync(ReminderKind.Digest)).Should().BeGreaterThan(0);

        To.Should().Contain("s-active@example.org").And.NotContain("s-reader@example.org");
        log.Last(ReminderKind.Digest)!.Note.Should().Be(ReminderService.InactiveNote(1, 30));
    }

    [Fact]
    public async Task APersonalDigest_IsntSentToSomeoneWhoHasntSignedInLately()
    {
        await Person("S-ACTIVE", "ActionOwner");
        await Person("S-STALE", "ActionOwner", daysSinceSeen: 31);
        var team = await TeamOf(null);
        await using (var ctx = _db.Context())
        {
            foreach (var who in new[] { "S-ACTIVE", "S-STALE" })
                ctx.UserPreferences.Add(new UserPreference { UserId = who, Name = UserPreferences.NotifyDigest, Value = UserPreferences.Daily, UpdatedAtUtc = DateTimeOffset.UtcNow });
            await ctx.SaveChangesAsync();
        }
        foreach (var who in new[] { "S-ACTIVE", "S-STALE" })
            await _db.Actions().CreateAsync(new ActionForm { Title = who + "'s", TeamId = team, AssigneeId = who, AssigneeName = who });
        var log = new ReminderLog();

        (await _db.Reminders(log: log).RunAsync(ReminderKind.PersonalDigest)).Should().Be(1);

        To.Should().Equal("s-active@example.org");
        log.Last(ReminderKind.PersonalDigest)!.Note.Should().Be(ReminderService.InactiveNote(1, 30));
    }
}
