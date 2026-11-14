using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Tide.Application.Admin;
using Tide.Application.Operations;
using Tide.Application.Settings;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.IntegrationTests;

/// <summary>Bug round 2, platform: recipient lists, the last Admin mapping, bundle setting keys.</summary>
public class BugRound2PlatformTests : IDisposable
{
    private TestDb _db = new(connectionPerContext: true);   // one test races two removals

    public BugRound2PlatformTests() => _db.User.Roles = new HashSet<AppRole> { AppRole.Admin };

    public void Dispose() => _db.Dispose();

    // The Access page saved the team list, then failed on a bad company-wide address with an error that read as if
    // nothing was saved; the team list had been.
    [Fact]
    public async Task ABadCompanyWideAddress_SavesNeitherList()
    {
        await _db.Settings().SaveRecipientsAsync("cti@contoso.com", "all@contoso.com", confirmOutside: true);

        await FluentActions.Invoking(() => _db.Settings().SaveRecipientsAsync("soc@contoso.com", "not an address"))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*not*");

        (await _db.Settings().GetRecipientsAsync(Edition.Team)).Should().Equal("cti@contoso.com");
        (await _db.Settings().GetRecipientsAsync(Edition.Public)).Should().Equal("all@contoso.com");
    }

    // Two administrators removing the last two Admin mappings at once each counted two, and both went. On SQL Server one
    // of the two is chosen as a deadlock victim and retried by the execution strategy, as production's is.
    [Theory]
    [MemberData(nameof(TestDb.Providers), MemberType = typeof(TestDb))]
    public async Task TwoAdminsRemovingTheLastTwoAdminMappingsAtOnce_LeaveOne(string provider)
    {
        _db = _db.On(provider);
        var roles = new RoleMappingService(_db, _db.User, new CountingRoleDirectory());
        await roles.AddAsync("CONTOSO\\CTI-Admins", AppRole.Admin);
        await roles.AddAsync("CONTOSO\\SOC-Admins", AppRole.Admin);
        var ids = (await roles.ListAsync()).Where(m => m.RoleName == nameof(AppRole.Admin)).Select(m => m.Id).ToList();
        // Each removal pauses as it saves, so both would have counted before either removed under the old check.
        _db.BeforeSave = ctx =>
        {
            if (ctx.ChangeTracker.Entries<AdGroupRoleMapping>().Any(e => e.State == EntityState.Deleted)) Thread.Sleep(200);
        };

        var results = await Task.WhenAll(ids.Select(id => Try(() => new RoleMappingService(_db, _db.User, new CountingRoleDirectory()).RemoveAsync(id))));

        results.Count(ok => ok).Should().Be(1, "the second finds the first gone and refuses to remove the last");
        _db.BeforeSave = null;
        (await roles.ListAsync()).Should().ContainSingle(m => m.RoleName == nameof(AppRole.Admin));

        static async Task<bool> Try(Func<Task> remove)
        {
            try { await Task.Run(remove); return true; }
            catch (InvalidOperationException) { return false; }
        }
    }

    // A hand-edited key in another case ("org:teamname") was matched to the catalogue ignoring case but stored as typed:
    // a second row SQL Server's key refused, and SQLite kept unread, while the preview said it was added. Its checks by
    // key were missed too, so "distribution:publicmaxtlp" was refused as "not an email address".
    [Fact]
    public async Task ABundleSettingKeyInAnotherCase_IsStoredUnderTheAppsKey_AndTakesEffect()
    {
        await using (var ctx = _db.Context())
        {
            ctx.Settings.Add(new AppSetting { Key = OperationalSettings.TeamName, Value = "Old Team", UpdatedBy = "x" });
            await ctx.SaveChangesAsync();
        }
        var saved = new SavedSettings();
        var json = $$"""
            {
              "format": "{{ConfigBundle.FormatName}}", "version": {{ConfigBundle.CurrentVersion}},
              "exportedAtUtc": "2026-11-01T00:00:00Z", "exportedBy": "x", "appVersion": "x",
              "settings": [ { "key": "org:teamname", "value": "Blue Team" }, { "key": "distribution:publicmaxtlp", "value": "Green" } ],
              "roleMappings": [], "sources": [], "scorecard": [], "queryTemplates": [], "holidays": []
            }
            """;
        var bundle = ConfigBundle.Parse(Encoding.UTF8.GetBytes(json));
        var service = new ConfigBundleService(_db, _db.User, new CountingRoleDirectory(), TimeProvider.System, saved);

        var done = await service.PreviewAndImportAsync(bundle);

        done.Changes.Should().Contain(c => c.Item == OperationalSettings.TeamName && c.Kind == BundleChangeKind.Update);
        done.Changes.Should().Contain(c => c.Item == DistributionSettings.PublicMaxTlpKey && c.Kind == BundleChangeKind.Add);
        await using var check = _db.Context();
        (await check.Settings.Select(s => s.Key).ToListAsync()).Should().BeEquivalentTo([OperationalSettings.TeamName, DistributionSettings.PublicMaxTlpKey]);
        saved.TryGet(OperationalSettings.TeamName, out var team).Should().BeTrue();
        team.Should().Be("Blue Team");
    }

    // The bundle took any text MailAddress could parse ("<a@x.org>"); the list read back dropped it, so the preview said
    // it was added while no mail went to it. Held to the page's check now.
    [Fact]
    public void ABundleRecipient_IsCheckedAsThePageChecksIt() =>
        ConfigBundleService.SettingProblem(new BundleSetting(DistributionSettings.TeamRecipientsKey, "<cti@contoso.com>")).Should().NotBeNull();
}

/// <summary>The API's description names its server under the app's path base.</summary>
public class OpenApiUnderThePathBaseTests(SubPathApp app) : IClassFixture<SubPathApp>
{
    [Fact]
    public async Task TheServer_IsTheApiUnderThePathBase()
    {
        var json = await app.CreateClient().GetStringAsync("/tide/api/v1/openapi.json");
        JsonElement.Parse(json).GetProperty("servers")[0].GetProperty("url").GetString().Should().Be("/tide/api/v1");
    }

    [Fact]
    public void AtTheRoot_TheServerIsTheApi() =>
        JsonElement.Parse(Tide.Web.Api.OpenApi.For(default)).GetProperty("servers")[0].GetProperty("url").GetString().Should().Be("/api/v1");
}
