using System.Security.Claims;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tide.Application.Abstractions;
using Tide.Application.Security;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Infrastructure.Persistence;
using Tide.Infrastructure.Security;
using Tide.TestSupport;
using Tide.Web.Security;

namespace Tide.IntegrationTests.Audit;

/// <summary>
/// The real role directory against the test database: how it answers sign-ins from the mappings, and what it does when
/// the database won't answer a reload (the change is saved, but sign-ins still resolve the previous mappings).
/// </summary>
public sealed class RoleDirectoryTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly FlakyDbs _dbs;
    private readonly CapturingLogger _log = new();
    private readonly ILoggerFactory _loggers;
    private readonly ServiceProvider _services;
    private Action? _beforeNextRead;
    private int _reads;

    public RoleDirectoryTests()
    {
        _dbs = new FlakyDbs(_db);
        _loggers = LoggerFactory.Create(b => b.AddProvider(_log));
        _services = new ServiceCollection().AddScoped(_ =>
        {
            Interlocked.Increment(ref _reads);
            if (Interlocked.Exchange(ref _beforeNextRead, null) is { } hook)
            {
                hook();
                return _db.Context();
            }
            return (AppDbContext)_dbs.CreateAsync().GetAwaiter().GetResult();
        }).BuildServiceProvider();
        // Retried often, so a test sees several retries in moments rather than one every 30 s.
        Directory = new RoleDirectory(_services.GetRequiredService<IServiceScopeFactory>(), new Logger<RoleDirectory>(_loggers))
            { RetryEvery = TimeSpan.FromMilliseconds(20) };
    }

    private RoleDirectory Directory { get; }

    public void Dispose()
    {
        _dbs.Down = false;
        _services.Dispose();
        _loggers.Dispose();
        _db.Dispose();
    }

    private async Task Map(params (string Group, string Role)[] maps)
    {
        await using var ctx = _db.Context();
        ctx.RoleMappings.AddRange(maps.Select(m => new AdGroupRoleMapping
            { AdGroup = m.Group, RoleName = m.Role, UpdatedBy = "test", UpdatedAtUtc = DateTimeOffset.UtcNow }));
        await ctx.SaveChangesAsync();
    }

    private async Task Unmap(string group)
    {
        await using var ctx = _db.Context();
        ctx.RoleMappings.RemoveRange(ctx.RoleMappings.Where(m => m.AdGroup == group));
        await ctx.SaveChangesAsync();
    }

    private List<string> Logged(LogLevel level, string containing)
    {
        lock (_log.Lines) return _log.Lines.Where(l => l.Level == level && l.Message.Contains(containing)).Select(l => l.Message).ToList();
    }

    [Fact]
    public async Task ASignIn_GetsEveryRoleItsGroupsMap_WhateverTheirCase_AndNoRoleThatDoesNotExist()
    {
        await Map(("CORP\\SOC-Admins", "Admin"), ("CORP\\SOC-Leads", "Admin"), ("corp\\soc-leads", "analyst"),
            ("CORP\\SOC-Readers", "Reader"), ("CORP\\SOC-Readers", "Overlord"), ("CORP\\Old-Team", "Nobody"));
        Directory.RolesForGroups(["CORP\\SOC-Admins"]).Should().BeEmpty("nothing is read until the first reload");

        Directory.Invalidate();

        Directory.IsCurrent.Should().BeTrue();
        Directory.RolesForGroups(["corp\\SOC-ADMINS"]).Should().BeEquivalentTo(["Admin"], "AD group names match whatever their case");
        Directory.RolesForGroups(["CORP\\SOC-LEADS"]).Should().BeEquivalentTo(["Admin", "analyst"],
            "two mappings for one group, written in different cases, are the same group's");
        Directory.RolesForGroups(["CORP\\SOC-Readers"]).Should().BeEquivalentTo(["Reader"], "a role that doesn't exist grants nothing");
        Directory.RolesForGroups(["CORP\\Old-Team"]).Should().BeEmpty();
        Directory.RolesForGroups(["CORP\\SOC-Admins", "CORP\\SOC-Readers", "CORP\\Not-Mapped"]).Should()
            .BeEquivalentTo(["Admin", "Reader"], "a member of several groups gets each one's roles");
        Directory.RolesForGroups([]).Should().BeEmpty();
    }

    [Fact]
    public async Task AReloadTheDatabaseRefuses_SaysSo_KeepsTryingInTheBackground_AndTakesOnceItAnswers()
    {
        await Map(("CORP\\SOC-Admins", "Admin"));
        Directory.Invalidate();
        Directory.RolesForGroups(["CORP\\SOC-Admins"]).Should().BeEquivalentTo(["Admin"]);

        // The administrator removes the mapping, and the database fails the reload that should take it away.
        await Unmap("CORP\\SOC-Admins");
        _dbs.Down = true;
        Directory.Invalidate();

        Directory.IsCurrent.Should().BeFalse("sign-ins still resolve the mapping the administrator removed");
        Logged(LogLevel.Critical, "couldn't reload from the database").Should().ContainSingle();
        Directory.RolesForGroups(["CORP\\SOC-Admins"]).Should().BeEquivalentTo(["Admin"], "the previous mappings stay until a reload works");
        var reads = Volatile.Read(ref _reads);
        (await Poll.Until(() => Volatile.Read(ref _reads) >= reads + 3)).Should().BeTrue("the reload is retried on a timer");
        Directory.IsCurrent.Should().BeFalse();
        Logged(LogLevel.Critical, "couldn't reload from the database").Should().ContainSingle("a failed retry doesn't log again");

        _dbs.Down = false;
        (await Poll.Until(() => Directory.IsCurrent)).Should().BeTrue("a retry that works makes the directory current");
        Directory.RolesForGroups(["CORP\\SOC-Admins"]).Should().BeEmpty("the removal is in effect");
        Logged(LogLevel.Warning, "The role directory reloaded").Should().ContainSingle();
    }

    [Fact]
    public async Task ARetryThatReadsAnOlderChange_DoesNotSayTheDirectoryIsCurrent_WhileANewerChangeHasNotLoaded()
    {
        await Map(("CORP\\SOC-Admins", "Admin"));
        _dbs.Down = true;
        Directory.Invalidate();
        Directory.IsCurrent.Should().BeFalse();

        // The next retry reads the database, but while it does a newer change comes in whose own reload fails. The retry's
        // read predates that change, so it mustn't stop the retries or say the changes are in effect.
        var hooked = false;
        _beforeNextRead = () =>
        {
            Directory.Invalidate();
            hooked = true;
        };
        (await Poll.Until(() => Volatile.Read(ref hooked))).Should().BeTrue("a retry runs");
        var reads = Volatile.Read(ref _reads);
        (await Poll.Until(() => Volatile.Read(ref _reads) >= reads + 3)).Should().BeTrue("retrying goes on");

        Directory.RolesForGroups(["CORP\\SOC-Admins"]).Should().BeEquivalentTo(["Admin"], "the older read was still stored");
        Directory.IsCurrent.Should().BeFalse("the newer change hasn't loaded");
        Logged(LogLevel.Warning, "The role directory reloaded").Should().BeEmpty();

        _dbs.Down = false;
        (await Poll.Until(() => Directory.IsCurrent)).Should().BeTrue("a later retry that loads everything makes the directory current");
        Logged(LogLevel.Warning, "The role directory reloaded").Should().ContainSingle();
    }
}

/// <summary>A role directory whose roles grant nothing once <see cref="DenyAll"/> is set, as removing their mappings
/// would; <see cref="Fail"/> makes the next read throw.</summary>
internal sealed class SwitchableRoles : IRoleDirectory
{
    public bool DenyAll { get; set; }

    public IReadOnlySet<Permission> PermissionsForRoles(IEnumerable<string> roleNames) =>
        DenyAll ? new HashSet<Permission>() : RoleDefinitions.PermissionsForRoleNames(roleNames);
    public bool IsRole(string name) => RoleDefinitions.TryParseRole(name, out _);
    public IReadOnlySet<string> RolesForGroups(IEnumerable<string> adGroups) => new HashSet<string>();
    public IReadOnlySet<string> RolesMappedByName => new HashSet<string>();
    public void Invalidate() { }
    public bool IsCurrent => true;
}

/// <summary>
/// The circuit's periodic re-check, on a clock the test moves: a session open for days loses a role revoked while it was
/// open, one failed re-check doesn't end the re-checking, and a sign-in that can't be copied is logged.
/// </summary>
public sealed class SessionRecheckTests : IDisposable
{
    private readonly SwitchableRoles _roles = new();
    private readonly ManualClock _clock = new();
    private readonly CapturingLogger _log = new();
    private readonly ILoggerFactory _loggers;
    private int _failNextReads;

    public SessionRecheckTests() => _loggers = LoggerFactory.Create(b => b.AddProvider(_log));

    public void Dispose() => _loggers.Dispose();

    private PermissionRevalidatingAuthStateProvider Provider()
    {
        var refresher = new SessionPermissionRefresher(() => Interlocked.Decrement(ref _failNextReads) >= 0
            ? throw new InvalidOperationException("the role directory is unavailable") : _roles, windowsMode: false);
        return new PermissionRevalidatingAuthStateProvider(refresher, new Logger<PermissionRevalidatingAuthStateProvider>(_loggers), _clock);
    }

    private static Task<AuthenticationState> AnAdminSignedIn() => Task.FromResult(new AuthenticationState(new ClaimsPrincipal(
        new ClaimsIdentity([new Claim(ClaimTypes.Name, "Dev"), new Claim(ClaimTypes.Role, nameof(AppRole.Admin)),
            new Claim(AppClaimTypes.Permission, nameof(Permission.Administer))], "Dev", ClaimTypes.Name, ClaimTypes.Role))));

    private static async Task<bool> CanAdminister(PermissionRevalidatingAuthStateProvider provider) =>
        (await provider.GetAuthenticationStateAsync()).User.HasClaim(AppClaimTypes.Permission, nameof(Permission.Administer));

    private List<string> Warnings(string containing)
    {
        lock (_log.Lines) return _log.Lines.Where(l => l.Level == LogLevel.Warning && l.Message.Contains(containing)).Select(l => l.Message).ToList();
    }

    [Fact]
    public async Task AnOpenSession_LosesARevokedRole_OnTheNextTick_WithNothingElseAskingForIt()
    {
        using var provider = Provider();
        provider.SetAuthenticationState(AnAdminSignedIn());
        _clock.Pending.Should().Be(1, "the re-check's timer starts with the session");

        _roles.DenyAll = true;
        (await CanAdminister(provider)).Should().BeTrue("nothing has re-checked yet");

        _clock.Advance(PermissionRevalidatingAuthStateProvider.Interval);
        (await Poll.Until(() => !CanAdminister(provider).GetAwaiter().GetResult())).Should().BeTrue("the tick re-checks the session");
    }

    [Fact]
    public async Task AFailedRecheck_IsLogged_AndTheNextTickStillRevokes()
    {
        using var provider = Provider();
        provider.SetAuthenticationState(AnAdminSignedIn());
        _roles.DenyAll = true;

        Volatile.Write(ref _failNextReads, 1);
        _clock.Advance(PermissionRevalidatingAuthStateProvider.Interval);
        (await Poll.Until(() => Warnings("Session permission re-check failed").Count == 1)).Should().BeTrue("the failed re-check is logged");
        (await CanAdminister(provider)).Should().BeTrue("the failed re-check changed nothing");

        _clock.Advance(PermissionRevalidatingAuthStateProvider.Interval);
        (await Poll.Until(() => !CanAdminister(provider).GetAwaiter().GetResult())).Should().BeTrue("the loop survived the failure and re-checked again");
        Warnings("Session permission re-check failed").Should().ContainSingle();
    }

    [Fact]
    public async Task ASignInThatCantBeCopied_IsLogged_AndTheSessionKeepsGoing()
    {
        using var provider = Provider();
        provider.SetAuthenticationState(Task.FromException<AuthenticationState>(new ObjectDisposedException("SafeAccessTokenHandle")));

        (await Poll.Until(() => Warnings("Couldn't copy the session's sign-in").Count == 1)).Should().BeTrue("the failed copy is logged");
        _clock.Pending.Should().Be(1, "the re-check still runs");
    }
}

/// <summary>Who the sign-in's claims transformer grants roles to, and its blocking group lookup.</summary>
public class RoleClaimsTransformerPathTests
{
    private const string AdminSid = "S-1-5-21-1000-2000-3000-1001";
    private const string AdminGroup = "CORP\\SOC-Admins";

    /// <summary>Maps <see cref="AdminGroup"/> and <see cref="AdminSid"/> to Admin.</summary>
    private sealed class AdminRoles : IRoleDirectory
    {
        public IReadOnlySet<Permission> PermissionsForRoles(IEnumerable<string> roleNames) => RoleDefinitions.PermissionsForRoleNames(roleNames);
        public bool IsRole(string name) => RoleDefinitions.TryParseRole(name, out _);
        public IReadOnlySet<string> RolesForGroups(IEnumerable<string> adGroups) =>
            adGroups.Any(g => g.Equals(AdminGroup, StringComparison.OrdinalIgnoreCase) || g == AdminSid)
                ? new HashSet<string> { nameof(AppRole.Admin) } : new HashSet<string>();
        public IReadOnlySet<string> RolesMappedByName => new HashSet<string> { nameof(AppRole.Admin) };
        public void Invalidate() { }
        public bool IsCurrent => true;
    }

    private static SidNames Names(params (string Sid, string Name)[] names) =>
        new(_ => names.ToDictionary(n => n.Sid, n => n.Name), TimeSpan.FromSeconds(5));

    [Fact]
    public async Task AnApiToken_GetsNoRoleOrEmail_FromTheGroupsItsClaimsName()
    {
        // A token whose claims happen to name a mapped group, as a role or a group SID, and a person AD knows an email for.
        var token = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "CORP\\jdoe"),
            new Claim(ClaimTypes.GroupSid, AdminSid), new Claim(ClaimTypes.Role, AdminGroup)],
            ApiTokenAuthenticationHandler.SchemeName, ClaimTypes.Name, ClaimTypes.Role));
        var transformer = new RoleClaimsTransformer(new AdminRoles(), sids: Names((AdminSid, AdminGroup)),
            names: DirectoryEmailTests.Ad("J Doe", "jdoe@example.com"));

        var after = await transformer.TransformAsync(token);

        after.Identities.Should().ContainSingle("nothing is added to a token's principal");
        after.HasClaim(AppClaimTypes.Permission, nameof(Permission.Administer)).Should().BeFalse("a token has its scopes' permissions, never a group's role");
        after.FindAll(ClaimTypes.Email).Should().BeEmpty();
    }

    [Fact]
    public async Task TheBlockingGroupLookup_NamesTheSameGroupsAsTheAsyncOne()
    {
        var signIn = DirectoryEmailTests.WindowsSignIn("CORP\\jdoe", null, AdminSid, "S-1-5-21-1000-2000-3000-1002");
        var sids = Names((AdminSid, AdminGroup));

        var blocking = RoleClaimsTransformer.GroupCandidates(signIn, sids);
        var waited = await RoleClaimsTransformer.GroupCandidatesAsync(signIn, sids);

        blocking.Complete.Should().BeTrue();
        blocking.Names.Should().Contain([AdminSid, AdminGroup, "S-1-5-21-1000-2000-3000-1002"]);
        blocking.Names.Should().BeEquivalentTo(waited.Names);

        // No SID named at all (the DC down): only the SIDs themselves, and the answer says it's partial.
        var down = RoleClaimsTransformer.GroupCandidates(signIn,
            new SidNames(_ => throw new InvalidOperationException("the DC is down"), TimeSpan.FromMilliseconds(200)));
        down.Complete.Should().BeFalse();
        down.Names.Should().BeEquivalentTo([AdminSid, "S-1-5-21-1000-2000-3000-1002"]);
    }
}
