using System.Net;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Tide.Web.Services;
using Tide.Application.Abstractions;
using Tide.Application.Actions;
using Tide.Application.Files;
using Tide.Application.Operations;
using Tide.Application.Security;
using Tide.Domain.Enums;
using Tide.Web.Security;

namespace Tide.IntegrationTests.Audit;

/// <summary>A role directory with the mappings a test gives it.</summary>
file sealed class FakeRoleDirectory : IRoleDirectory
{
    public Dictionary<string, HashSet<string>> Groups { get; } = new(StringComparer.OrdinalIgnoreCase);

    public FakeRoleDirectory Map(string group, AppRole role)
    {
        if (!Groups.TryGetValue(group, out var roles)) Groups[group] = roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        roles.Add(role.ToString());
        return this;
    }

    /// <summary>Grants no permission to any role, as removing every mapping would.</summary>
    public bool DenyAll { get; set; }

    public IReadOnlySet<Permission> PermissionsForRoles(IEnumerable<string> roleNames) =>
        DenyAll ? new HashSet<Permission>() : RoleDefinitions.PermissionsForRoleNames(roleNames);
    public bool IsRole(string name) => RoleDefinitions.TryParseRole(name, out _);

    public IReadOnlySet<string> RolesForGroups(IEnumerable<string> adGroups) =>
        adGroups.Where(Groups.ContainsKey).SelectMany(g => Groups[g]).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public IReadOnlySet<string> RolesMappedByName =>
        Groups.Where(g => !g.Key.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase)).SelectMany(g => g.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public void Invalidate() { }
    public bool IsCurrent => true;
}

file sealed class FakeEnvironment(string name) : IHostEnvironment
{
    public string EnvironmentName { get; set; } = name;
    public string ApplicationName { get; set; } = "Tide.Web";
    public string ContentRootPath { get; set; } = ".";
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

/// <summary>Sign-in and session checks from the security audit: the auth-mode guard, and how a Windows sign-in's groups
/// become roles and are re-checked while a session is open.</summary>
public class SignInAuditTests
{
    private const string AdminSid = "S-1-5-21-1000-2000-3000-1001";
    private const string AdminGroup = "CORP\\SOC-Admins";

    private static IConfiguration Config(params (string Key, string? Value)[] pairs) =>
        new ConfigurationBuilder().AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value))).Build();

    [Theory]
    [InlineData("Production", null)]
    [InlineData("Production", "Dev")]
    [InlineData("Production", "dev")]
    [InlineData("Production", "Negotiate")]
    [InlineData("Staging", null)]
    [InlineData("Staging", "Dev")]
    public void DevSignIn_NeverStartsWhereItIsNotAllowed(string environment, string? mode)
    {
        var act = () => AuthModeGuard.UseWindows(new FakeEnvironment(environment), Config(("Auth:Mode", mode)));
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Production_RefusesTheDemoOptIn()
    {
        var act = () => AuthModeGuard.UseWindows(new FakeEnvironment("Production"),
            Config(("Auth:Mode", "Dev"), (AuthModeGuard.AllowOutsideDevelopmentKey, "true")));
        act.Should().Throw<InvalidOperationException>("the opt-in is for demo and test boxes, never Production");
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Development")]
    public void WindowsSignIn_IsAcceptedEverywhere(string environment)
    {
        AuthModeGuard.UseWindows(new FakeEnvironment(environment), Config(("Auth:Mode", "windows"))).Should().BeTrue();
    }

    [Fact]
    public void DevSignIn_RunsInDevelopment_AndOnADemoBoxThatOptsIn()
    {
        AuthModeGuard.UseWindows(new FakeEnvironment("Development"), Config()).Should().BeFalse();
        AuthModeGuard.UseWindows(new FakeEnvironment("Staging"), Config((AuthModeGuard.AllowOutsideDevelopmentKey, "true"))).Should().BeFalse();
    }

    // A Windows sign-in as the Negotiate handler hands it over: the account name, and the groups as SIDs.
    private static ClaimsPrincipal WindowsSignIn(params string[] groupSids)
    {
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "CORP\\jdoe")], "Negotiate", ClaimTypes.Name, ClaimTypes.GroupSid);
        foreach (var sid in groupSids) identity.AddClaim(new Claim(ClaimTypes.GroupSid, sid));
        return new ClaimsPrincipal(identity);
    }

    private static SidNames Names(IReadOnlyDictionary<string, string> names) => new(_ => names, TimeSpan.FromSeconds(5));

    [Fact]
    public async Task AWindowsSignIn_GetsItsRolesFromItsGroups_Once()
    {
        var directory = new FakeRoleDirectory().Map(AdminGroup, AppRole.Admin);
        var sids = Names(new Dictionary<string, string> { [AdminSid] = AdminGroup });
        var transformer = new RoleClaimsTransformer(directory, sids: sids);

        var principal = await transformer.TransformAsync(WindowsSignIn(AdminSid));
        principal.HasClaim(AppClaimTypes.Permission, nameof(Permission.Administer)).Should().BeTrue();
        principal.IsInRole(nameof(AppRole.Admin)).Should().BeTrue();
        principal.Identity!.IsAuthenticated.Should().BeTrue("the Windows identity stays the primary one");
        RoleClaimsTransformer.GroupsIncomplete(principal).Should().BeFalse();

        // Transformed again on the same request, the grants aren't added a second time.
        var again = await transformer.TransformAsync(principal);
        again.Identities.Count(i => i.Label == RoleClaimsTransformer.GrantsLabel).Should().Be(1);
        again.FindAll(AppClaimTypes.Permission).Count(c => c.Value == nameof(Permission.Administer)).Should().Be(1);
    }

    [Fact]
    public async Task AWindowsSignIn_InAnUnmappedGroup_GetsNothing()
    {
        var directory = new FakeRoleDirectory().Map(AdminGroup, AppRole.Admin);
        var sids = Names(new Dictionary<string, string> { ["S-1-5-21-1000-2000-3000-1002"] = "CORP\\Everyone-Else" });
        var principal = await new RoleClaimsTransformer(directory, sids: sids).TransformAsync(WindowsSignIn("S-1-5-21-1000-2000-3000-1002"));
        principal.FindAll(AppClaimTypes.Permission).Should().BeEmpty();
        principal.FindAll(ClaimTypes.Role).Should().BeEmpty();
    }

    [Fact]
    public async Task AGroupNotNamedInTime_GrantsNothingByName_AndTheRequestSaysSo()
    {
        var directory = new FakeRoleDirectory().Map(AdminGroup, AppRole.Admin);
        var sids = new SidNames(_ => throw new InvalidOperationException("the DC is down"), TimeSpan.FromMilliseconds(200));
        var principal = await new RoleClaimsTransformer(directory, sids: sids).TransformAsync(WindowsSignIn(AdminSid));
        principal.FindAll(AppClaimTypes.Permission).Should().BeEmpty("a mapping by name can't match a SID that wasn't named");
        RoleClaimsTransformer.GroupsIncomplete(principal).Should().BeTrue("so the user mirror doesn't record the short role list");
    }

    [Fact]
    public async Task AMappingBySid_WorksWithoutTheDirectory()
    {
        var directory = new FakeRoleDirectory().Map(AdminSid, AppRole.Reader);
        var sids = new SidNames(_ => throw new InvalidOperationException("the DC is down"), TimeSpan.FromMilliseconds(200));
        var principal = await new RoleClaimsTransformer(directory, sids: sids).TransformAsync(WindowsSignIn(AdminSid));
        principal.HasClaim(AppClaimTypes.Permission, nameof(Permission.ReadPublished)).Should().BeTrue();
        principal.HasClaim(AppClaimTypes.Permission, nameof(Permission.Administer)).Should().BeFalse();
    }

    [Fact]
    public async Task AnOpenSession_LosesARevokedRole_AndKeepsOneAPartialAnswerCannotRuleOut()
    {
        var directory = new FakeRoleDirectory().Map(AdminGroup, AppRole.Admin);
        var sids = Names(new Dictionary<string, string> { [AdminSid] = AdminGroup });
        var session = await new RoleClaimsTransformer(directory, sids: sids).TransformAsync(WindowsSignIn(AdminSid));
        var refresher = new SessionPermissionRefresher(() => directory, windowsMode: true, sids);

        (await refresher.RefreshAsync(session)).Should().BeNull("nothing changed");

        // The administrator moves the group to Reader: the open session follows within a re-check.
        directory.Groups[AdminGroup] = [nameof(AppRole.Reader)];
        var updated = await refresher.RefreshAsync(session);
        updated.Should().NotBeNull();
        updated!.HasClaim(AppClaimTypes.Permission, nameof(Permission.Administer)).Should().BeFalse();
        updated.HasClaim(AppClaimTypes.Permission, nameof(Permission.ReadPublished)).Should().BeTrue();
        updated.IsInRole(nameof(AppRole.Admin)).Should().BeFalse();
        updated.Identity!.IsAuthenticated.Should().BeTrue();
        updated.FindFirstValue(ClaimTypes.Name).Should().Be("CORP\\jdoe", "identity claims are kept as they were");
        updated.FindAll(ClaimTypes.GroupSid).Should().ContainSingle(c => c.Value == AdminSid, "so the next re-check can resolve the groups again");

        // The mapping is removed altogether: the session loses everything.
        directory.Groups.Clear();
        var revoked = await refresher.RefreshAsync(updated);
        revoked.Should().NotBeNull();
        revoked!.FindAll(AppClaimTypes.Permission).Should().BeEmpty();

        // During an AD outage (no SID can be named) a session keeps a role some mapping by name could still be granting:
        // a partial answer takes away only what it can rule out, and never adds.
        directory.Map(AdminGroup, AppRole.Admin);
        var outage = new SessionPermissionRefresher(() => directory,  windowsMode: true,
            new SidNames(_ => throw new InvalidOperationException("the DC is down"), TimeSpan.FromMilliseconds(200)));
        (await outage.RefreshAsync(session)).Should().BeNull("the Admin role held is still mapped by name, so it's kept");
        (await outage.RefreshAsync(revoked)).Should().BeNull("nothing is granted on a partial answer");
    }

    [Fact]
    public async Task ADevSession_KeepsItsRoles_AndReReadsTheirPermissions()
    {
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "Dev"), new Claim(ClaimTypes.Role, nameof(AppRole.Reader))], "Dev", ClaimTypes.Name, ClaimTypes.Role);
        var refresher = new SessionPermissionRefresher(() => new FakeRoleDirectory(), windowsMode: false);
        var updated = await refresher.RefreshAsync(new ClaimsPrincipal(identity));
        updated.Should().NotBeNull("the permission claims were missing");
        updated!.FindAll(AppClaimTypes.Permission).Select(c => c.Value).Should().Equal(nameof(Permission.ReadPublished));
        (await refresher.RefreshAsync(updated)).Should().BeNull();
    }
}

/// <summary>Service-level checks from the security audit, against the real services on an in-memory database.</summary>
public sealed class ServiceAuditTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task AHunt_RefusesAnotherCommentsFileAsItsEvidence()
    {
        _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst, AppRole.Admin };
        var team = await _db.Teams().AddAsync("Cyber Defense", null, null);
        var svc = _db.Actions();
        var first = (await svc.GetAsync(await svc.CreateAsync(new ActionForm { Title = "First", TeamId = team })))!.Action.Id;
        var second = (await svc.GetAsync(await svc.CreateAsync(new ActionForm { Title = "Second", TeamId = team })))!.Action.Id;
        await svc.AddCommentAsync(first, "Evidence attached.", [new ScreenshotUpload("proof.png", TestDb.Png)]);
        Guid commentFile;
        using (var db = _db.Context()) commentFile = db.ActionCommentFiles.Single().FileId;

        var hunt = await svc.AddHandWrittenHuntAsync(second, "kql", "DeviceEvents | take 1");
        var act = () => svc.RecordHuntResultAsync(hunt.Id, HuntResult.Hits, 1, null, commentFile);
        await act.Should().ThrowAsync<ArgumentException>("deleting the comment would delete the bytes from under the hunt");

        var own = await _db.Screenshots().StoreEvidenceAsync(new ScreenshotUpload("proof.png", TestDb.Png));
        await svc.RecordHuntResultAsync(hunt.Id, HuntResult.Hits, 1, null, own);
    }

    [Fact]
    public async Task AReader_SeesNoScreenshotThatWasNeverPublished_ByAnyRoute()
    {
        var article = await _db.Articles().CreateAsync("Red article");
        var shot = (await _db.Screenshots().AddAsync(article, [new ScreenshotUpload("a.png", TestDb.Png)])).Single();
        var evidence = await _db.Screenshots().StoreEvidenceAsync(new ScreenshotUpload("e.png", TestDb.Png));

        _db.User.Roles = new HashSet<AppRole> { AppRole.Reader };
        var shots = _db.Screenshots();
        (await shots.OpenAsync(shot.FileId)).Should().BeNull();
        (await shots.OpenAttachmentAsync(shot.FileId)).Should().BeNull();
        (await shots.OpenAsync(evidence)).Should().BeNull();
        (await shots.OpenAttachmentAsync(evidence)).Should().BeNull();

        _db.User.Roles = new HashSet<AppRole>();
        (await shots.OpenAsync(shot.FileId)).Should().BeNull("someone with no role gets nothing");
    }

    // A5-4 (Agent 5's acceptance test): a bullet that saves (within its limit) must never stop the brief rendering or going.
    [Fact]
    public async Task AStoredBullet_WithinItsLimit_CanAlwaysBeRenderedAndSent()
    {
        var article = await _db.Articles().CreateAsync("Deep");
        var brief = await _db.Briefs().GetOrStartAsync(_db.Clock.CurrentBriefDate);
        await _db.Briefs().AddArticleAsync(brief.Id, article);
        var form = Tide.Application.Articles.ArticleForm.From((await _db.Articles().GetAsync(article))!);
        form.Bullets = [new Tide.Application.Articles.BulletForm { Text = new string('[', 200) + "x" }];
        await _db.Articles().SaveAsync(form);
        await FluentActions.Invoking(() => _db.Distribution().PreviewEmailAsync(_db.Clock.CurrentBriefDate, Edition.Team, _ => "x")).Should().NotThrowAsync();
        await FluentActions.Invoking(() => _db.Distribution().SendTestAsync(_db.Clock.CurrentBriefDate, Edition.Team)).Should().NotThrowAsync();
    }

    [Fact]
    public async Task AnOrphanedEvidenceFile_IsServedToNobody()
    {
        // Stored but never attached to a hunt: its id leads nowhere, even for the team.
        var evidence = await _db.Screenshots().StoreEvidenceAsync(new ScreenshotUpload("e.png", TestDb.Png));
        (await _db.Screenshots().OpenAsync(evidence)).Should().BeNull();
        (await _db.Screenshots().OpenAttachmentAsync(evidence)).Should().BeNull();
    }
}

/// <summary>The endpoints that aren't pages, as a Reader (the least-privileged role) reaches them.</summary>
public class ReaderEndpointAuditTests(ReaderApp app) : IClassFixture<ReaderApp>
{
    [Theory]
    [InlineData("/sends/00000000-0000-0000-0000-000000000001/email")]
    [InlineData("/rotation/my.ics")]
    [InlineData("/trends/articles.csv")]
    [InlineData("/briefs/2026-10-26/sharepoint-images.zip")]
    [InlineData("/admin/system/config-bundle.json")]
    [InlineData("/admin/system/tide.config.psd1")]
    public async Task AReader_IsRefused(string path)
    {
        var client = app.CreateClient(new() { AllowAutoRedirect = false });
        (await client.GetAsync(path)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AReader_CannotDownloadAnyFile()
    {
        (await app.CreateClient().GetAsync($"/files/{Guid.NewGuid()}/download")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}

/// <summary>The dev sign-in's <c>?as=</c> switch takes a name, not a payload.</summary>
public class DevSignInAuditTests(TideApp app) : IClassFixture<TideApp>
{
    [Fact]
    public async Task TheDevSignInName_IsCleanedAndCapped()
    {
        var res = await app.CreateClient().GetAsync("/health?as=" + Uri.EscapeDataString("Sam <script>alert(1)</script>;" + new string('x', 200)));
        var cookie = res.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("dev_as=", StringComparison.Ordinal));
        var value = cookie["dev_as=".Length..cookie.IndexOf(';')];
        value.Should().NotContain("<").And.NotContain("%3C").And.NotContain(";");
        Uri.UnescapeDataString(value).Length.Should().BeLessThanOrEqualTo(DevAuthenticationHandler.MaxAsNameLength);
    }
}

// --- Round 2 -----------------------------------------------------------------------------------------------------

/// <summary>Keeps every line logged, for a test to read.</summary>
public sealed class CapturingLogger : ILogger, ILoggerProvider
{
    public List<(LogLevel Level, string Category, string Message)> Lines { get; } = [];
    private readonly string _category;

    public CapturingLogger(string category = "") => _category = category;

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName) { Sink = Sink ?? this };
    private CapturingLogger? Sink { get; init; }
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Dispose() { }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var sink = Sink ?? this;
        lock (sink.Lines) sink.Lines.Add((logLevel, _category, formatter(state, exception)));
    }
}

/// <summary>Who may list the teams (with their members' ids and names) and the scorecard tags: whoever reads actions or
/// the team edition, never a Reader.</summary>
public sealed class ListingAuditTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task AReader_CannotListTeamsTheirMembersOrTheTags()
    {
        _db.User.Roles = new HashSet<AppRole> { AppRole.Admin };
        var team = await _db.Teams().AddAsync("Cyber Defense", null, null);
        await _db.Teams().AddMemberAsync(team, "S-1-5-21-MEMBER", "Member");

        _db.User.Roles = new HashSet<AppRole> { AppRole.Reader };
        var teams = () => _db.Teams().ListAsync();
        await teams.Should().ThrowAsync<ForbiddenException>();
        var mine = () => _db.Teams().TeamsOfAsync("S-1-5-21-MEMBER");
        await mine.Should().ThrowAsync<ForbiddenException>();
        var tags = () => _db.Scorecard().ListAsync();
        await tags.Should().ThrowAsync<ForbiddenException>();

        _db.User.Roles = new HashSet<AppRole>();
        await teams.Should().ThrowAsync<ForbiddenException>("someone with no role gets nothing either");
    }

    [Fact]
    public async Task ATeamMember_AnAnalyst_AndAnAdministrator_ListThem()
    {
        _db.User.Roles = new HashSet<AppRole> { AppRole.Admin };
        await _db.Scorecard().SeedStarterSetAsync();
        var team = await _db.Teams().AddAsync("Cyber Defense", null, null);
        await _db.Teams().AddMemberAsync(team, "S-1-5-21-OWNER", "Owner");
        (await _db.Teams().ListAsync()).Should().ContainSingle(t => t.Id == team);
        (await _db.Scorecard().ListAsync()).Should().NotBeEmpty("the starter set is there");

        _db.User.Roles = new HashSet<AppRole> { AppRole.ActionOwner };
        _db.User.UserId = "S-1-5-21-OWNER";
        (await _db.Teams().ListAsync()).Should().ContainSingle(t => t.Members.Any(m => m.UserId == "S-1-5-21-OWNER"));
        (await _db.Teams().TeamsOfAsync("S-1-5-21-OWNER")).Should().Equal(team);

        _db.User.Roles = new HashSet<AppRole> { AppRole.Analyst };
        (await _db.Teams().ListAsync()).Should().HaveCount(1);
        (await _db.Scorecard().ListAsync()).Should().NotBeEmpty();
    }
}

/// <summary>The audit trail never keeps a credential, and a refusal is logged with who and where.</summary>
public sealed class TrailAuditTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task TheAuditTrail_KeepsAFeedAddressWithoutItsKey()
    {
        _db.User.Roles = new HashSet<AppRole> { AppRole.Admin };
        var id = await _db.Intake().SaveSourceAsync(new Tide.Application.Intake.SourceForm
        {
            Name = "Vendor feed", Url = "https://feeds.example.com/x.xml?apikey=SECRET123&format=rss", PollMinutes = 60,
        });
        var form = Tide.Application.Intake.SourceForm.From((await _db.Intake().SourcesAsync()).Single(s => s.Id == id));
        form.Url = "https://user:HUNTER2@feeds.example.com/y.xml";
        await _db.Intake().SaveSourceAsync(form);

        using var db = _db.Context();
        var rows = db.AuditLog.Where(e => e.EntityType == "Source").Select(e => e.ChangesJson ?? "").ToList();
        rows.Should().HaveCount(2);
        var all = string.Join("\n", rows);
        all.Should().NotContain("SECRET123").And.NotContain("HUNTER2");
        all.Should().Contain("feeds.example.com", "the address itself is still recorded").And.Contain("format=rss");
        db.Sources.Single(s => s.Id == id).Url.Should().Contain("HUNTER2", "only the trail is masked, not the setting");
    }

    // A5-R-3 (Agent 5's acceptance test).
    [Fact]
    public async Task TheAuditTrail_NeverHoldsASettingNamedLikeASecret()
    {
        _db.User.Roles = new HashSet<AppRole> { AppRole.Admin, AppRole.Analyst };
        await using (var ctx = _db.Context())
        {
            ctx.Settings.Add(new Tide.Domain.Entities.AppSetting { Key = "Cve:NvdApiKey", Value = "TOPSECRET-KEY", UpdatedAtUtc = DateTimeOffset.UtcNow, UpdatedBy = "x" });
            ctx.Settings.Add(new Tide.Domain.Entities.AppSetting { Key = "Email:SmtpPassword", Value = "TOPSECRET-PW", UpdatedAtUtc = DateTimeOffset.UtcNow, UpdatedBy = "x" });
            ctx.Settings.Add(new Tide.Domain.Entities.AppSetting { Key = "Rotation:ManagerEmails", Value = "boss@example.org", UpdatedAtUtc = DateTimeOffset.UtcNow, UpdatedBy = "x" });
            await ctx.SaveChangesAsync();
        }
        await using var check = _db.Context();
        var trail = string.Join("\n", check.AuditLog.Where(a => a.EntityType == "AppSetting").Select(a => a.ChangesJson));
        trail.Should().NotContain("TOPSECRET").And.Contain("(set)", "the secret is recorded as set").And.Contain("boss@example.org", "a plain setting is recorded as before");
    }

    // R6-1: the mask is a list of secret-bearing properties, so the trail stays readable (ADR 0008).
    [Fact]
    public async Task TheAuditTrail_RecordsAnAssigneeAndATeamsKeyInClear()
    {
        _db.User.Roles = new HashSet<AppRole> { AppRole.Admin, AppRole.Analyst };
        var team = await _db.Teams().AddAsync("Cyber Defense", null, null);
        var svc = _db.Actions();
        var number = await svc.CreateAsync(new ActionForm { Title = "Sweep", TeamId = team });
        var action = (await svc.GetAsync(number))!.Action;
        var form = ActionForm.From(action);
        form.AssigneeId = "S-1-5-21-OWNER";
        form.AssigneeName = "Jordan Rivera";
        await svc.SaveAsync(form);

        await using var db = _db.Context();
        var actions = string.Join("\n", db.AuditLog.Where(a => a.EntityType == "ActionItem").Select(a => a.ChangesJson));
        actions.Should().Contain("Jordan Rivera").And.Contain("S-1-5-21-OWNER").And.NotContain(SecretMask.SetSecret);
        var teams = string.Join("\n", db.AuditLog.Where(a => a.EntityType == "Team").Select(a => a.ChangesJson));
        teams.Should().Contain("cyber-defense", "a team's Key is a slug, not a secret").And.NotContain(SecretMask.SetSecret);
    }

    [Fact]
    public async Task AFeedsStoredError_NeverCarriesItsKey()
    {
        _db.User.Roles = new HashSet<AppRole> { AppRole.Admin };
        const string url = "https://feeds.example.com/x.xml?apikey=SECRET123";
        var id = await _db.Intake().SaveSourceAsync(new Tide.Application.Intake.SourceForm { Name = "Keyed", Url = url, PollMinutes = 60 });
        // What an HttpRequestException or a parser can say: the address asked for, key and all.
        _db.Http.Throws[url] = new HttpRequestException($"Request to https://bob:hunter2@feeds.example.com/x.xml?apikey=SECRET123 failed (503).");
        var result = await _db.Intake().PollNowAsync(id);
        result.Error.Should().NotContain("SECRET123").And.NotContain("hunter2").And.Contain("feeds.example.com");
        var stored = (await _db.Intake().SourcesAsync()).Single(s => s.Id == id).LastError;
        stored.Should().NotBeNull().And.NotContain("SECRET123").And.NotContain("hunter2");
    }

    [Fact]
    public async Task ARefusal_IsLoggedWithWhoAndWhere()
    {
        var log = new CapturingLogger("Tide.Access");
        _db.User.Roles = new HashSet<AppRole> { AppRole.Reader };
        _db.User.UserId = "S-1-5-21-READER";
        Exception caught;
        try { await _db.Teams().ListAsync(); throw new InvalidOperationException("not refused"); }
        catch (ForbiddenException ex) { caught = ex; }

        // Outside any request or circuit activity there's no log to write to, and nothing is thrown.
        UserFacingError.For(caught, _db.User).Should().Be(caught.Message, "the person sees the reason");
        log.Lines.Should().BeEmpty();

        using (AmbientUser.Begin(null, log))
        {
            UserFacingError.For(caught, _db.User).Should().Be(caught.Message);
            UserFacingError.For(caught, operation: "the teams page");
            log.Lines.Should().HaveCount(2).And.OnlyContain(l => l.Level == LogLevel.Warning);
            log.Lines[0].Message.Should().Contain("S-1-5-21-READER").And.Contain("TeamService.ListAsync").And.Contain(caught.Message);
            log.Lines[1].Message.Should().Contain("unknown").And.Contain("the teams page");

            UserFacingError.For(new ArgumentException("not a refusal"));
            log.Lines.Should().HaveCount(2, "only refusals are logged here");
        }

        // A page's handler passes no user; the circuit activity's ambient user is logged instead.
        using (AmbientUser.Begin(_db.User, log)) UserFacingError.For(caught);
        log.Lines[2].Message.Should().Contain("S-1-5-21-READER");
        AmbientUser.Current.Should().BeNull("the scope ended");
        AmbientUser.AccessLog.Should().BeNull();
    }

    [Fact]
    public async Task ACircuitActivity_RunsWithItsUserAndItsHostsLogAmbient()
    {
        var log = new CapturingLogger();
        using var loggers = LoggerFactory.Create(b => b.AddProvider(log));
        var handler = new AmbientUserCircuitHandler(_db.User, loggers);
        ICurrentUser? seen = null;
        await handler.CreateInboundActivityHandler(_ =>
        {
            seen = AmbientUser.Current;
            UserFacingError.For(new ForbiddenException("Only analysts can."), operation: "a click");
            return Task.CompletedTask;
        })(null!);
        seen.Should().BeSameAs(_db.User);
        log.Lines.Should().ContainSingle(l => l.Category == "Tide.Access" && l.Message.Contains(_db.User.UserId) && l.Message.Contains("a click"));
        AmbientUser.Current.Should().BeNull();
        AmbientUser.AccessLog.Should().BeNull();
    }

    private sealed class ThrowingLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            throw new AggregateException("An error occurred while writing to logger(s).", new ObjectDisposedException("EventLogInternal"));
    }

    [Fact]
    public void TheDirectoryLookupsLog_NeverThrows_WhateverLoggerTheServingHostGaveIt()
    {
        // DirectoryLog is process-wide and every request of every host in this process sets it, so which host's logger
        // it holds at any instant isn't something a test can pin down; what must hold is that it never throws.
        DirectoryLog.Use(new ThrowingLogger());
        FluentActions.Invoking(() => DirectoryLog.Instance.LogWarning("three")).Should().NotThrow();
        FluentActions.Invoking(() => DirectoryLog.Instance.IsEnabled(LogLevel.Warning)).Should().NotThrow();
        FluentActions.Invoking(() => DirectoryLog.Use(null)).Should().NotThrow();

        // The lookups are wired to it: a name resolver built with no logger of its own writes through it.
        new WindowsNames(_ => null, TimeSpan.FromMilliseconds(50)).Resolve("CORP\\jdoe").Should().Be("jdoe");
        new SidNames(_ => new Dictionary<string, string>(), TimeSpan.FromMilliseconds(50)).Resolve(["S-1-5-21-1-2-3-4"]).Names.Should().BeEmpty();
    }

    // A5-R-5: a change or a presence note is published on the publisher's thread, inside their circuit activity. Each
    // subscriber is called with that user cleared, so a refusal from work the subscriber starts isn't logged as theirs;
    // the host's log stays, so it's still logged, as nobody's.
    [Fact]
    public async Task APublisher_DoesNotLendItsUserToAnotherCircuitsSubscriber()
    {
        var log = new CapturingLogger("Tide.Access");
        ChangeFeed.AroundHandler = AmbientUser.WithoutUser;   // as Program.cs sets it
        var feed = new ChangeFeed();
        var presence = new Tide.Web.Services.Presence();
        var inHandler = new List<string?>();
        var afterYield = new List<TaskCompletionSource<string?>>();
        void Subscriber()
        {
            inHandler.Add(AmbientUser.Current?.UserId);
            var later = new TaskCompletionSource<string?>();
            afterYield.Add(later);
            _ = Task.Run(async () =>
            {
                await Task.Yield();
                UserFacingError.For(new ForbiddenException("Subscriber B was refused."), operation: "Triage.Load");
                later.TrySetResult(AmbientUser.Current?.UserId);
            });
        }
        feed.Changed += _ => Subscriber();
        presence.Changed += _ => Subscriber();

        var publisher = new FakeUser(AppRole.Analyst) { UserId = "publisher-A" };
        using (AmbientUser.Begin(publisher, log))
        {
            feed.Publish("article:1");
            presence.Join("article:1", "publisher-A", "A").Dispose();
            AmbientUser.Current.Should().BeSameAs(publisher, "the publisher's own work keeps its user");
        }
        inHandler.Should().HaveCount(3).And.OnlyContain(id => id == null);
        foreach (var later in afterYield) (await later.Task).Should().BeNull();
        log.Lines.Should().NotContain(l => l.Message.Contains("publisher-A"), "the refusal was the subscriber's, never the publisher's");
        log.Lines.Should().HaveCount(3, "each subscriber's refusal is still logged")
            .And.OnlyContain(l => l.Message.Contains("Access denied for unknown in Triage.Load"));
    }

    [Fact]
    public void ALoggerThatFails_NeverReachesThePerson()
    {
        using (AmbientUser.Begin(_db.User, new ThrowingLogger()))
            FluentActions.Invoking(() => UserFacingError.For(new ForbiddenException("Only analysts can."))).Should().NotThrow()
                .Which.Should().Be("Only analysts can.");
    }

    [Fact]
    public async Task AnOpenSession_LosesAccessWhenItsRoleIsRemoved_WithoutReconnecting()
    {
        // The circuit's auth-state provider, re-checking a dev session whose roles come from the directory's permissions.
        var directory = new FakeRoleDirectory();
        var refresher = new SessionPermissionRefresher(() => directory, windowsMode: false);
        using var provider = new PermissionRevalidatingAuthStateProvider(refresher, Microsoft.Extensions.Logging.Abstractions.NullLogger<PermissionRevalidatingAuthStateProvider>.Instance);
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "Dev"), new Claim(ClaimTypes.Role, nameof(AppRole.Admin)),
            new Claim(AppClaimTypes.Permission, nameof(Permission.Administer))], "Dev", ClaimTypes.Name, ClaimTypes.Role);
        provider.SetAuthenticationState(Task.FromResult(new Microsoft.AspNetCore.Components.Authorization.AuthenticationState(new ClaimsPrincipal(identity))));
        var current = new CurrentUser(new Microsoft.AspNetCore.Http.HttpContextAccessor(), provider);
        current.Has(Permission.Administer).Should().BeTrue();

        // In dev mode the roles stay and their permissions are re-read; here the directory grants Admin nothing, as a
        // removed mapping would. Services ask CurrentUser, which drops its cached principal on the change.
        directory.DenyAll = true;
        (await provider.RevalidateAsync()).Should().BeTrue();
        current.Has(Permission.Administer).Should().BeFalse("the next service call is refused");
        (await provider.GetAuthenticationStateAsync()).User.FindAll(AppClaimTypes.Permission).Should().BeEmpty();
    }
}

/// <summary>A1-R1: two hosts in one process (as the test suite runs them). A refusal logged through the second host after the
/// first is disposed goes to the second's log and never throws, because nothing host-owned is kept in a static.</summary>
public class TwoHostsAuditTests
{
    [Fact]
    public async Task ARefusal_AfterAnotherHostIsDisposed_IsLoggedByTheServingHost_AndNeverThrows()
    {
        var first = new LoggingReaderApp();
        (await first.CreateClient(new() { AllowAutoRedirect = false }).GetAsync("/rotation/my.ics")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        first.Log.Lines.Should().Contain(l => l.Category == "Tide.Access" && l.Message.Contains("/rotation/my.ics"));
        var firstLog = first.Services.GetRequiredService<ILoggerFactory>().CreateLogger(AmbientUser.AccessLogCategory);
        first.Dispose();

        using var second = new LoggingReaderApp();
        (await second.CreateClient(new() { AllowAutoRedirect = false }).GetAsync("/rotation/my.ics")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var before = second.Log.Lines.Count;

        // A refusal shown as a toast while the second host serves: its log gets the line, the first's gets nothing.
        using (var scope = second.Services.CreateScope())
        using (AmbientUser.Begin(scope.ServiceProvider.GetRequiredService<ICurrentUser>(),
                   scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(AmbientUser.AccessLogCategory)))
            UserFacingError.For(new ForbiddenException("Only analysts can create actions."), operation: "ActionStatusControl.Move")
                .Should().Be("Only analysts can create actions.");
        second.Log.Lines.Skip(before).Should().ContainSingle(l => l.Category == "Tide.Access" && l.Message.Contains("ActionStatusControl.Move"));
        first.Log.Lines.Should().NotContain(l => l.Message.Contains("ActionStatusControl.Move"));

        // Even a logger that belongs to the disposed host (what the static used to hold) can't reach the person.
        using (AmbientUser.Begin(null, firstLog))
            FluentActions.Invoking(() => UserFacingError.For(new ForbiddenException("x"))).Should().NotThrow();
    }
}

/// <summary>The endpoints' policies and headers: rate limits where a script could ask for a lot, a 403 that's logged, and
/// an inline image the browser re-checks each time.</summary>
public sealed class LoggingReaderApp : TideApp
{
    public CapturingLogger Log { get; } = new();
    protected override string? Role => "Reader";

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(s => s.AddLogging(l => l.AddProvider(Log)));
    }
}

public class EndpointAuditTests(LoggingReaderApp app) : IClassFixture<LoggingReaderApp>
{
    [Fact]
    public async Task ARefusedEndpoint_IsLogged()
    {
        var res = await app.CreateClient(new() { AllowAutoRedirect = false }).GetAsync("/rotation/my.ics");
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        app.Log.Lines.Should().Contain(l => l.Category == "Tide.Access" && l.Level == LogLevel.Warning
            && l.Message.Contains("Access denied") && l.Message.Contains("/rotation/my.ics") && l.Message.Contains("Dev Analyst"));
    }

    [Theory]
    [InlineData("/files/{id:guid}/inline", "files")]
    [InlineData("/files/{id:guid}/download", "files")]
    [InlineData("/briefs/{day}/email/{edition}", "exports")]
    [InlineData("/sends/{id:guid}/email", "exports")]
    [InlineData("/corrections/{id:guid}/email", "exports")]
    [InlineData("/reports/export.csv", "exports")]
    [InlineData("/briefs/{day}/sharepoint-images.zip", "exports")]
    [InlineData("/rotation/my.ics", "exports")]
    [InlineData("/metrics/export.csv", "exports")]
    [InlineData("/trends/export.csv", "exports")]
    [InlineData("/trends/articles.csv", "exports")]
    [InlineData("/exports/{scope}/{key}", "exports")]
    [InlineData("/health", "health")]
    [InlineData("/health/live", "health")]
    public void AnEndpointThatReadsALot_IsRateLimited(string route, string policy)
    {
        var endpoints = app.Services.GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>().Endpoints
            .OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>().Where(e => e.RoutePattern.RawText == route).ToList();
        endpoints.Should().NotBeEmpty();
        endpoints.Should().OnlyContain(e => e.Metadata.GetMetadata<Microsoft.AspNetCore.RateLimiting.EnableRateLimitingAttribute>() != null
            && e.Metadata.GetMetadata<Microsoft.AspNetCore.RateLimiting.EnableRateLimitingAttribute>()!.PolicyName == policy);
    }

    [Fact]
    public async Task TheHealthProbes_StillAnswer()
    {
        (await app.CreateClient().GetAsync("/health/live")).StatusCode.Should().Be(HttpStatusCode.OK);
    }
}

/// <summary>The circuit hub is offered to this site's pages only: a request to /_blazor from another origin is refused
/// before SignalR hands out a connection (cross-site WebSocket hijacking with the user's Windows credentials).</summary>
public class CircuitOriginAuditTests(TideApp app) : IClassFixture<TideApp>
{
    private static HttpRequestMessage Negotiate(string path, string? origin)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, path);
        if (origin is not null) req.Headers.TryAddWithoutValidation("Origin", origin);
        return req;
    }

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("http://localhost.evil.example")]
    [InlineData("http://localhost:9999")]
    [InlineData("null")]
    [InlineData("file://")]
    public async Task AForeignOrigin_GetsNoConnection(string origin)
    {
        var res = await app.CreateClient().SendAsync(Negotiate("/_blazor/negotiate?negotiateVersion=1", origin));
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await res.Content.ReadAsStringAsync()).Should().NotContain("connectionToken");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("http://localhost")]
    [InlineData("HTTP://LOCALHOST:80")]
    [InlineData("http://localhost:5110")]   // App:BaseUrl in appsettings.Development.json: what the browser sees behind a proxy
    public async Task TheAppsOwnOrigin_OrNone_IsServed(string? origin)
    {
        var res = await app.CreateClient().SendAsync(Negotiate("/_blazor/negotiate?negotiateVersion=1", origin));
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await res.Content.ReadAsStringAsync()).Should().Contain("connectionToken");
    }

    [Fact]
    public async Task ARefusedCircuitRequest_IsLoggedOnce()
    {
        using var host = new LoggingReaderApp();
        var req = Negotiate("/_blazor/negotiate?negotiateVersion=1", "https://evil.example");
        (await host.CreateClient().SendAsync(req)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        host.Log.Lines.Where(l => l.Category == "Tide.Access").Should().ContainSingle()
            .Which.Message.Should().Contain("evil.example").And.Contain("/_blazor/negotiate");
    }

    [Fact]
    public async Task OtherRequests_AreNotOriginChecked()
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        req.Headers.TryAddWithoutValidation("Origin", "https://evil.example");
        (await app.CreateClient().SendAsync(req)).StatusCode.Should().Be(HttpStatusCode.OK, "the check is for the circuit, not a CORS policy");
    }

    [Theory]
    [InlineData("https://tide.example.org", "tide.example.org", null, true)]
    [InlineData("https://tide.example.org:443", "tide.example.org", null, true)]
    [InlineData("https://tide.example.org", "tide.example.org:443", null, true)]
    [InlineData("https://tide.example.org", "backend01:5000", "https://tide.example.org/tide", true)]
    [InlineData("https://tide.example.org", "backend01:5000", null, false)]
    [InlineData("https://tide.example.org.evil.example", "tide.example.org", null, false)]
    [InlineData("https://tide.example.org:8443", "tide.example.org", null, false)]
    [InlineData("javascript://tide.example.org", "tide.example.org", null, false)]
    [InlineData("", "tide.example.org", null, false)]
    public void TheOriginRule(string origin, string host, string? baseUrl, bool allowed)
    {
        var baseAuthority = baseUrl is null ? null : new Uri(baseUrl).Authority;
        CircuitOriginMiddleware.IsAllowed(origin, host, baseAuthority).Should().Be(allowed);
    }
}

public class CircuitOriginUnderPathBaseTests(SubPathApp app) : IClassFixture<SubPathApp>
{
    [Fact]
    public async Task TheCheck_FollowsThePathBase()
    {
        var client = app.CreateClient();
        var evil = new HttpRequestMessage(HttpMethod.Post, "/tide/_blazor/negotiate?negotiateVersion=1");
        evil.Headers.TryAddWithoutValidation("Origin", "https://evil.example");
        (await client.SendAsync(evil)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var own = new HttpRequestMessage(HttpMethod.Post, "/tide/_blazor/negotiate?negotiateVersion=1");
        own.Headers.TryAddWithoutValidation("Origin", "http://localhost");
        (await client.SendAsync(own)).StatusCode.Should().Be(HttpStatusCode.OK);

        // IIS in-process sets the same path base itself, so the WebSocket upgrade under it is checked the same way.
        var upgrade = CircuitTransportAuditTests.Upgrade("/tide/_blazor?id=abc", "https://evil.example");
        (await client.SendAsync(upgrade)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}

/// <summary>Every transport the circuit can use carries the browser's Origin on its request to /_blazor: the WebSocket
/// upgrade, long polling's POSTs and GETs, server-sent events. All are checked, not only negotiate.</summary>
public class CircuitTransportAuditTests(TideApp app) : IClassFixture<TideApp>
{
    public static HttpRequestMessage Upgrade(string path, string origin)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.TryAddWithoutValidation("Origin", origin);
        req.Headers.TryAddWithoutValidation("Connection", "Upgrade");
        req.Headers.TryAddWithoutValidation("Upgrade", "websocket");
        req.Headers.TryAddWithoutValidation("Sec-WebSocket-Version", "13");
        req.Headers.TryAddWithoutValidation("Sec-WebSocket-Key", "dGhlIHNhbXBsZSBub25jZQ==");
        return req;
    }

    private static HttpRequestMessage With(HttpMethod method, string path, string origin)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.TryAddWithoutValidation("Origin", origin);
        if (method == HttpMethod.Post) req.Content = new StringContent("");
        return req;
    }

    [Fact]
    public async Task AForeignOrigin_IsRefusedOnEveryTransport()
    {
        var client = app.CreateClient();
        (await client.SendAsync(Upgrade("/_blazor?id=abc", "https://evil.example"))).StatusCode.Should().Be(HttpStatusCode.Forbidden, "the WebSocket upgrade");
        (await client.SendAsync(With(HttpMethod.Post, "/_blazor?id=abc", "https://evil.example"))).StatusCode.Should().Be(HttpStatusCode.Forbidden, "long polling's send");
        (await client.SendAsync(With(HttpMethod.Get, "/_blazor?id=abc", "https://evil.example"))).StatusCode.Should().Be(HttpStatusCode.Forbidden, "long polling's poll");
        var sse = With(HttpMethod.Get, "/_blazor?id=abc&transport=ServerSentEvents", "https://evil.example");
        sse.Headers.TryAddWithoutValidation("Accept", "text/event-stream");
        (await client.SendAsync(sse)).StatusCode.Should().Be(HttpStatusCode.Forbidden, "server-sent events");
        (await client.SendAsync(With(HttpMethod.Post, "/_blazor/disconnect", "https://evil.example"))).StatusCode.Should().Be(HttpStatusCode.Forbidden, "the disconnect beacon");
    }

    [Fact]
    public async Task TheAppsOwnOrigin_ReachesSignalR_OnEveryTransport()
    {
        // SignalR then answers for itself (an unknown connection id is a 404), which is the point: the check didn't refuse.
        var client = app.CreateClient();
        foreach (var req in new[] { Upgrade("/_blazor?id=abc", "http://localhost"), With(HttpMethod.Post, "/_blazor?id=abc", "http://localhost"), With(HttpMethod.Get, "/_blazor?id=abc", "http://localhost") })
            (await client.SendAsync(req)).StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
    }
}

/// <summary>A reverse proxy that forwards to the app under another host name: the browser's Origin is the public name
/// (App:BaseUrl), the request's Host is the backend's. Both are the app.</summary>
public sealed class ProxiedApp : TideApp
{
    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("App:BaseUrl", "https://tide.example.org/tide");
    }
}

public class CircuitOriginBehindAProxyTests(ProxiedApp app) : IClassFixture<ProxiedApp>
{
    private static HttpRequestMessage Negotiate(string origin, string host)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/_blazor/negotiate?negotiateVersion=1");
        req.Headers.TryAddWithoutValidation("Origin", origin);
        req.Headers.Host = host;
        return req;
    }

    [Fact]
    public async Task ThePublicName_AndTheBackendsOwn_AreServed_AnythingElseIsNot()
    {
        var client = app.CreateClient();
        (await client.SendAsync(Negotiate("https://tide.example.org", "backend01:5000"))).StatusCode.Should().Be(HttpStatusCode.OK, "the public name, from App:BaseUrl");
        (await client.SendAsync(Negotiate("http://backend01:5000", "backend01:5000"))).StatusCode.Should().Be(HttpStatusCode.OK, "the request's own host");
        (await client.SendAsync(Negotiate("https://tide.example.org:8443", "backend01:5000"))).StatusCode.Should().Be(HttpStatusCode.Forbidden, "another port is another origin");
        (await client.SendAsync(Negotiate("https://evil.example", "backend01:5000"))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.SendAsync(Negotiate("https://tide.example.org.evil.example", "backend01:5000"))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}

public class InlineImageAuditTests(TideApp app) : IClassFixture<TideApp>
{
    // A5-4 (Agent 5's acceptance test).
    [Fact]
    public async Task AnArticleBullet_WithinItsLimit_NeverBreaksThePagesOrTheEmail()
    {
        Guid id;
        var day = app.Services.GetRequiredService<Tide.Application.Org.OrgClock>().CurrentBriefDate;
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Tide.Infrastructure.Persistence.AppDbContext>();
            var article = new Tide.Domain.Entities.Article { Title = "Deep", Tlp = TlpLevel.Clear, Status = ArticleStatus.Ready, AuthorId = "x", AuthorName = "X" };
            article.Bullets.Add(new Tide.Domain.Entities.ArticleBullet { Text = new string('[', 200) + "x", Tlp = TlpLevel.Clear });
            db.Articles.Add(article);
            var brief = Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.Include(db.Briefs, b => b.Items).First(b => b.BriefDate == day);
            brief.Items.Add(new Tide.Domain.Entities.BriefItem { ArticleId = article.Id, SortOrder = 99 });
            await db.SaveChangesAsync();
            id = article.Id;
        }
        var client = app.CreateClient();
        (await client.GetAsync($"/articles/{id}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync($"/briefs/{day:yyyy-MM-dd}/email/team")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync($"/briefs/{day:yyyy-MM-dd}/sharepoint")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // A5-1 (Agent 5's acceptance test).
    [Theory]
    [InlineData("/exports/brief/{day}?format=csv")]
    [InlineData("/exports/brief/{day}")]
    [InlineData("/metrics/export.csv")]
    [InlineData("/trends/export.csv")]
    [InlineData("/trends/articles.csv")]
    [InlineData("/rotation/my.ics")]
    public async Task IndicatorAndMetricExports_AreNotCacheable(string path)
    {
        var day = app.Services.GetRequiredService<Tide.Application.Org.OrgClock>().CurrentBriefDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var res = await app.CreateClient().GetAsync(path.Replace("{day}", day));
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var cache = res.Headers.CacheControl?.ToString() ?? "";
        cache.Should().Match(c => c.Contains("no-store") || c.Contains("private"), path);
        res.Headers.CacheControl!.NoStore.Should().BeTrue(path);
    }

    [Fact]
    public async Task AnInlineImage_IsRecheckedEachTime_AndAnsweredNotModifiedWhileUnchanged()
    {
        Guid fileId;
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Tide.Infrastructure.Persistence.AppDbContext>();
            var store = scope.ServiceProvider.GetRequiredService<IFileStore>();
            var file = new Tide.Domain.Entities.StoredFile { FileName = "a.png", ContentType = "image/png", Sha256 = "abc123" };
            await store.WriteAsync(file.Id, new MemoryStream(TestDb.Png));
            db.Files.Add(file);
            db.ArticleScreenshots.Add(new Tide.Domain.Entities.ArticleScreenshot { ArticleId = db.Articles.First().Id, FileId = file.Id });
            await db.SaveChangesAsync();
            fileId = file.Id;
        }
        var client = app.CreateClient();
        var first = await client.GetAsync($"/files/{fileId}/inline");
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        first.Headers.CacheControl!.Private.Should().BeTrue();
        first.Headers.CacheControl.MaxAge.Should().Be(TimeSpan.FromMinutes(5), "revocation reaches a browser within minutes");
        first.Headers.CacheControl.MustRevalidate.Should().BeTrue();
        first.Headers.ETag!.Tag.Should().Be("\"abc123\"");

        var again = new HttpRequestMessage(HttpMethod.Get, $"/files/{fileId}/inline");
        again.Headers.IfNoneMatch.Add(first.Headers.ETag);
        var notModified = await client.SendAsync(again);
        notModified.StatusCode.Should().Be(HttpStatusCode.NotModified);
        notModified.Headers.ETag!.Tag.Should().Be("\"abc123\"");

        // R6-6: the 304 is answered from the permission check and the hash alone, without opening the file.
        using (var scope = app.Services.CreateScope()) scope.ServiceProvider.GetRequiredService<IFileStore>().Delete(fileId);
        var stillTagged = new HttpRequestMessage(HttpMethod.Get, $"/files/{fileId}/inline");
        stillTagged.Headers.IfNoneMatch.Add(first.Headers.ETag);
        (await client.SendAsync(stillTagged)).StatusCode.Should().Be(HttpStatusCode.NotModified);
        (await client.GetAsync($"/files/{fileId}/inline")).StatusCode.Should().Be(HttpStatusCode.NotFound, "the bytes are gone");
    }
}
