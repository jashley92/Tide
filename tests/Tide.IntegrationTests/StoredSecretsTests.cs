using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Tide.Application;
using Tide.Application.Abstractions;
using Tide.Application.Api;
using Tide.Application.Assist;
using Tide.Application.Intake;
using Tide.Application.Security;
using Tide.Application.Settings;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Web.Components.Pages.Admin;
using static Tide.IntegrationTests.ComponentHarness;

namespace Tide.IntegrationTests;

/// <summary>
/// The secrets an administrator may save in the app (Administration › Secrets): stored encrypted under a purpose of their
/// own, never in the audit trail, used where the server's configuration leaves one blank (which always wins), in force at
/// once, and said to be unreadable when the database comes back without its keys.
/// </summary>
public sealed class StoredSecretsTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly ServiceProvider _sp;

    public StoredSecretsTests()
    {
        _db.User.Roles = new HashSet<AppRole> { AppRole.Admin };
        _sp = Rig([]);
    }

    private ServiceProvider Rig(Dictionary<string, string?> server)
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().AddInMemoryCollection(server).Build();
        services.AddSingleton<IConfiguration>(config);
        services.AddApplication(config);
        services.AddSingleton<IAppDbContextFactory>(_db);
        services.AddSingleton<ICurrentUser>(_db.User);
        services.AddSingleton<ISecretProtector, FakeSecrets>();
        services.AddLogging();
        return services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _sp.Dispose();
        _db.Dispose();
    }

    private StoredSecretService Service => _sp.CreateScope().ServiceProvider.GetRequiredService<StoredSecretService>();

    [Fact]
    public async Task ASavedSecret_IsStoredEncrypted_UnderItsOwnPurpose_AndAuditedOnlyAsSet()
    {
        await Service.SaveAsync("Cve:NvdApiKey", "  nvd-key-123  ");

        await using var ctx = _db.Context();
        var row = await ctx.StoredSecrets.SingleAsync();
        row.Protected.Should().Be("p:Tide.Secrets.NvdApiKey.v1:nvd-key-123", "encrypted, trimmed, under the NVD key's own purpose");
        row.SavedByName.Should().Be(_db.User.DisplayName);
        new FakeSecrets().Unprotect(StoredSecretDefinition.AiApiKey.Purpose, row.Protected).Should().BeNull("one secret never opens as another");

        var audit = await ctx.AuditLog.Where(a => a.EntityType == nameof(StoredSecret)).SingleAsync();
        audit.ChangesJson.Should().NotContain("nvd-key-123").And.NotContain("p:Tide.Secrets", "not even the ciphertext");

        var status = Service.List().Single(s => s.Definition == StoredSecretDefinition.NvdApiKey);
        (status.Saved, status.SavedBy, status.Unreadable, status.ServerSets).Should().Be((true, _db.User.DisplayName, false, false));
        status.Definition.Label.Should().Be("CVE enrichment · NVD API key");
    }

    [Fact]
    public async Task ASavedSecret_FillsInWhatTheServerLeavesBlank_AtOnce_AndClearingItTakesItAway()
    {
        var cve = _sp.GetRequiredService<IOptionsMonitor<CveOptions>>();
        var ai = _sp.GetRequiredService<IOptionsMonitor<AiOptions>>();
        cve.CurrentValue.NvdApiKey.Should().BeEmpty();

        await Service.SaveAsync("Cve:NvdApiKey", "nvd-key-123");
        await Service.SaveAsync("Ai:ApiKey", "ai-key-456");
        (cve.CurrentValue.NvdApiKey, ai.CurrentValue.ApiKey).Should().Be(("nvd-key-123", "ai-key-456"), "in force with no restart");

        await Service.SaveAsync("Cve:NvdApiKey", "nvd-key-789");
        cve.CurrentValue.NvdApiKey.Should().Be("nvd-key-789", "a replacement too");

        await Service.ClearAsync("Cve:NvdApiKey");
        cve.CurrentValue.NvdApiKey.Should().BeEmpty();
        Service.List().Single(s => s.Definition == StoredSecretDefinition.NvdApiKey).Saved.Should().BeFalse();
    }

    [Fact]
    public async Task TheServersOwnSecret_AlwaysWins_AndThePageSaysSo()
    {
        using var server = Rig(new() { ["Cve:NvdApiKey"] = "from-the-server" });
        await using var scope = server.CreateAsyncScope();
        var svc = scope.ServiceProvider.GetRequiredService<StoredSecretService>();
        await svc.SaveAsync("Cve:NvdApiKey", "saved-in-the-app");

        server.GetRequiredService<IOptionsMonitor<CveOptions>>().CurrentValue.NvdApiKey.Should().Be("from-the-server");
        var status = svc.List().Single(s => s.Definition == StoredSecretDefinition.NvdApiKey);
        (status.Saved, status.ServerSets).Should().Be((true, true));
    }

    [Fact]
    public async Task ASecretThatWontDecrypt_IsLeftOut_AndMarkedUnreadable()
    {
        await using (var ctx = _db.Context())
        {
            ctx.StoredSecrets.Add(new StoredSecret { Key = "Cve:NvdApiKey", Protected = "from another server's keys", SavedByName = "Someone" });
            await ctx.SaveChangesAsync();
        }
        var store = _sp.GetRequiredService<StoredSecrets>();
        await store.RefreshAsync(_db);

        store.TryGet(StoredSecretDefinition.NvdApiKey, out _).Should().BeFalse();
        _sp.GetRequiredService<IOptionsMonitor<CveOptions>>().CurrentValue.NvdApiKey.Should().BeEmpty();
        var status = Service.List().Single(s => s.Definition == StoredSecretDefinition.NvdApiKey);
        (status.Saved, status.Unreadable).Should().Be((true, true));
        Service.InForceKeys().Should().BeEmpty("one that won't open isn't in force");
    }

    [Fact]
    public async Task OnlyAnAdministrator_SavesOrSeesThem_AndOnlyKnownSecrets_NotBlank_NotTooLong()
    {
        await Service.Invoking(s => s.SaveAsync("Email:From", "x")).Should().ThrowAsync<ArgumentException>();
        await Service.Invoking(s => s.SaveAsync("Cve:NvdApiKey", "   ")).Should().ThrowAsync<ArgumentException>().WithMessage("*Clear*");
        await Service.Invoking(s => s.SaveAsync("Cve:NvdApiKey", new string('k', 201))).Should().ThrowAsync<ArgumentException>().WithMessage("*200*");

        _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst };
        await Service.Invoking(s => s.SaveAsync("Cve:NvdApiKey", "k")).Should().ThrowAsync<ForbiddenException>();
        await Service.Invoking(s => s.ClearAsync("Cve:NvdApiKey")).Should().ThrowAsync<ForbiddenException>();
        Service.Invoking(s => s.List()).Should().Throw<ForbiddenException>();
    }

    [Fact]
    public void WithoutDataProtection_NothingIsStoredUnprotected()
    {
        var none = new NoSecretProtector();
        none.Invoking(p => p.Protect("purpose", "secret")).Should().Throw<InvalidOperationException>();
        none.Unprotect("purpose", "anything").Should().BeNull();
    }
}

/// <summary>The Secrets page, and Server configuration and Setting sources naming a secret saved there.</summary>
public class StoredSecretsPageTests(TideApp app) : IClassFixture<TideApp>
{
    [Fact]
    public async Task ThePage_ListsEachSecret_NeverItsValue_AndTheServerPagesSayWhereItsSaved()
    {
        await using (var page = await RenderAsync<SecretsAdmin>(app, "/admin/system/secrets"))
        {
            var html = System.Net.WebUtility.HtmlDecode(await page.Html());
            html.Should().Contain("Email · Relay password").And.Contain("CVE enrichment · NVD API key").And.Contain("AI drafting · API key")
                .And.Contain("type=\"password\"").And.Contain("autocomplete=\"new-password\"");
        }

        await using (var scope = SignedIn(app, SignIn.Analyst))
            await scope.ServiceProvider.GetRequiredService<StoredSecretService>().SaveAsync("Cve:NvdApiKey", "page-test-key-123");
        try
        {
            await using (var page = await RenderAsync<SecretsAdmin>(app, "/admin/system/secrets"))
            {
                var html = await page.Html();
                html.Should().Contain("bi-lock-fill").And.Contain("by Tester").And.Contain("Replace it").And.Contain(">Remove<span class=\"visually-hidden\"> the saved CVE enrichment")
                    .And.NotContain("page-test-key-123", "never shown again");
            }
            await using (var server = await RenderAsync<ServerConfigAdmin>(app, "/admin/system/server"))
                (await server.Html()).Should().Contain("href=\"admin/system/secrets\"").And.Contain("Saved in the app").And.NotContain("page-test-key-123");
            await using (var sources = await RenderAsync<SettingSourcesAdmin>(app, "/admin/system/sources"))
                (await sources.Html()).Should().Contain("Saved in the app, encrypted").And.NotContain("page-test-key-123");
        }
        finally
        {
            await using var scope = SignedIn(app, SignIn.Analyst);
            await scope.ServiceProvider.GetRequiredService<StoredSecretService>().ClearAsync("Cve:NvdApiKey");
        }
    }
}
