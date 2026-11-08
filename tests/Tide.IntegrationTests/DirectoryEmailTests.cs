using System.Security.Claims;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Abstractions;
using Tide.Application.Security;
using Tide.Domain.Enums;
using Tide.Infrastructure.Persistence;
using Tide.Infrastructure.Security;
using Tide.Web.Security;

namespace Tide.IntegrationTests;

/// <summary>A role directory that maps one group (a SID) to the roles a test gives it.</summary>
internal sealed class OneGroupRoles(string group, params AppRole[] roles) : IRoleDirectory
{
    public IReadOnlySet<string> RolesForGroups(IEnumerable<string> adGroups) =>
        adGroups.Contains(group, StringComparer.OrdinalIgnoreCase)
            ? roles.Select(r => r.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlySet<Permission> PermissionsForRoles(IEnumerable<string> roleNames) => RoleDefinitions.PermissionsForRoleNames(roleNames);
    public bool IsRole(string name) => RoleDefinitions.TryParseRole(name, out _);
    public IReadOnlySet<string> RolesMappedByName => new HashSet<string>();
    public void Invalidate() { }
    public bool IsCurrent => true;
}

/// <summary>A Windows sign-in carries no email: TIDE reads the person's AD <c>mail</c> with their display name, puts it on
/// the principal, and the user mirror records it.</summary>
public class DirectoryEmailTests
{
    internal const string AnalystSid = "S-1-5-21-1000-2000-3000-1101";
    private const string Account = "CONTOSO\\jdoe";

    internal static ClaimsPrincipal WindowsSignIn(string name = Account, string? email = null, params string[] groupSids)
    {
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, name)], "Negotiate", ClaimTypes.Name, ClaimTypes.GroupSid);
        if (email is not null) identity.AddClaim(new Claim(ClaimTypes.Email, email));
        foreach (var sid in groupSids) identity.AddClaim(new Claim(ClaimTypes.GroupSid, sid));
        return new ClaimsPrincipal(identity);
    }

    internal static SidNames NoSidNames() => new(_ => new Dictionary<string, string>(), TimeSpan.FromSeconds(5));

    internal static WindowsNames Ad(string? displayName, string? email) =>
        new(_ => new DirectoryPerson(displayName, email), TimeSpan.FromSeconds(5));

    internal static CurrentUser UserOf(ClaimsPrincipal principal) =>
        new(new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = principal } }, new ServerAuthenticationStateProvider());

    // --- the lookup -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheLookup_GivesTheNameAndTheEmail_FromOneCall()
    {
        var calls = 0;
        var names = new WindowsNames(_ => { Interlocked.Increment(ref calls); return new DirectoryPerson("  Jordan Rivera ", " jordan.rivera@contoso.com "); },
            TimeSpan.FromSeconds(5));

        names.Resolve(Account).Should().Be("Jordan Rivera");
        (await names.EmailAsync(Account)).Should().Be("jordan.rivera@contoso.com");
        (await names.ResolveAsync(Account)).Should().Be("Jordan Rivera");
        calls.Should().Be(1, "the name and the address come from the same cached AD call");
    }

    [Fact]
    public async Task TheLookup_WithNoMail_GivesNoEmail_AndStillTheName()
    {
        var names = Ad("Jordan Rivera", "   ");
        (await names.EmailAsync(Account)).Should().BeNull("a UPN or anything else isn't guessed as the address");
        names.Resolve(Account).Should().Be("Jordan Rivera");
    }

    [Fact]
    public async Task TheLookup_WithAnAddressAndNoName_KeepsTheAddress_AndShowsTheBareUsername()
    {
        var names = Ad(null, "jdoe@contoso.com");
        (await names.EmailAsync(Account)).Should().Be("jdoe@contoso.com");
        names.Resolve(Account).Should().Be("jdoe");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Dev Analyst")]
    [InlineData("jdoe@contoso.com")]
    [InlineData("CONTOSO\\")]
    public async Task ANameThatIsNotADomainAccount_IsNeverLookedUp(string? name)
    {
        var names = new WindowsNames(_ => throw new InvalidOperationException("not asked"), TimeSpan.FromSeconds(5));
        (await names.EmailAsync(name)).Should().BeNull();
    }

    [Fact]
    public async Task ALookupThatFails_GivesNoEmail_WithoutThrowing()
    {
        var names = new WindowsNames(_ => throw new InvalidOperationException("the DC is down"), TimeSpan.FromSeconds(5));
        (await names.EmailAsync(Account)).Should().BeNull();
        names.Resolve(Account).Should().Be("jdoe");
    }

    [Fact]
    public async Task ALookupThatTimesOut_GivesNoEmail_AndTheLateAnswerIsKept()
    {
        using var release = new ManualResetEventSlim();
        var names = new WindowsNames(_ => { release.Wait(TimeSpan.FromSeconds(10)); return new DirectoryPerson("Jordan Rivera", "jdoe@contoso.com"); },
            TimeSpan.FromMilliseconds(50));

        (await names.EmailAsync(Account)).Should().BeNull("AD didn't answer in time");
        names.Resolve(Account).Should().Be("jdoe");

        release.Set();
        var deadline = DateTime.UtcNow.AddSeconds(15);
        string? email = null;
        while (email is null && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
            email = await names.EmailAsync(Account);
        }
        email.Should().Be("jdoe@contoso.com", "the answer that lands later is kept");
    }

    // A person found is asked about again after RecheckFound; until the new answer lands, and while AD fails, the old one is used.
    [Fact]
    public async Task APersonFound_IsLookedUpAgainAfter24Hours_KeepingTheOldValueWhileAdFails()
    {
        var clock = new Tide.TestSupport.TestClock(new DateTimeOffset(2026, 11, 5, 9, 0, 0, TimeSpan.Zero));
        var calls = 0;
        DirectoryPerson? answer = new("Jordan Rivera", "jordan.rivera@contoso.com");
        Exception? down = null;
        var names = new WindowsNames(_ =>
        {
            Interlocked.Increment(ref calls);
            return down is null ? answer : throw down;
        }, TimeSpan.FromSeconds(5), clock);

        names.Resolve(Account).Should().Be("Jordan Rivera");
        clock.Now += WindowsNames.RecheckFound - TimeSpan.FromMinutes(1);
        names.Resolve(Account).Should().Be("Jordan Rivera");
        calls.Should().Be(1, "within 24 hours the name found is kept");

        // Past the interval, with AD down: asked again, and the old name and address are still used.
        down = new InvalidOperationException("the DC is down");
        clock.Now += TimeSpan.FromMinutes(2);
        names.Resolve(Account).Should().Be("Jordan Rivera");
        (await names.EmailAsync(Account)).Should().Be("jordan.rivera@contoso.com");
        (await names.ResolveForMirrorAsync(Account)).Should().Be(("Jordan Rivera", false), "the old name isn't a stand-in");
        calls.Should().Be(2, "a failed re-check isn't asked again on every request");

        // AD back after RetryUnresolved, with a new name and address: they replace the old ones.
        down = null;
        answer = new DirectoryPerson("Jordan Rivera-Lee", "jordan.lee@contoso.com");
        clock.Now += WindowsNames.RetryUnresolved;
        names.Resolve(Account).Should().Be("Jordan Rivera-Lee");
        (await names.EmailAsync(Account)).Should().Be("jordan.lee@contoso.com");
        calls.Should().Be(3);
    }

    [Fact]
    public async Task ARecheckThatTimesOut_KeepsTheOldValue_AndTheLateAnswerReplacesIt()
    {
        var clock = new Tide.TestSupport.TestClock(new DateTimeOffset(2026, 11, 5, 9, 0, 0, TimeSpan.Zero));
        using var release = new ManualResetEventSlim(initialState: true);
        var answer = new DirectoryPerson("Jordan Rivera", "jordan.rivera@contoso.com");
        var names = new WindowsNames(_ => { release.Wait(TimeSpan.FromSeconds(10)); return answer; }, TimeSpan.FromMilliseconds(50), clock);
        names.Resolve(Account).Should().Be("Jordan Rivera");

        release.Reset();
        answer = new DirectoryPerson("Jordan Rivera-Lee", "jordan.lee@contoso.com");
        clock.Now += WindowsNames.RecheckFound;
        names.Resolve(Account).Should().Be("Jordan Rivera", "the re-check didn't answer in time");
        (await names.EmailAsync(Account)).Should().Be("jordan.rivera@contoso.com");

        release.Set();
        var deadline = DateTime.UtcNow.AddSeconds(15);
        string? name = null;
        while (name != "Jordan Rivera-Lee" && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
            name = await names.ResolveAsync(Account);
        }
        name.Should().Be("Jordan Rivera-Lee", "the answer that lands later is kept");
    }

    // --- the principal --------------------------------------------------------------------------------------------

    [Fact]
    public async Task AWindowsSignIn_GetsItsAdEmail_EvenWithNoRole()
    {
        var principal = await new RoleClaimsTransformer(new OneGroupRoles(AnalystSid, AppRole.Analyst), sids: NoSidNames(),
            names: Ad("Jordan Rivera", "jordan.rivera@contoso.com")).TransformAsync(WindowsSignIn());

        principal.FindFirstValue(ClaimTypes.Email).Should().Be("jordan.rivera@contoso.com");
        principal.FindAll(AppClaimTypes.Permission).Should().BeEmpty();
        UserOf(principal).Email.Should().Be("jordan.rivera@contoso.com");

        // Transformed again on the same request, the address isn't added twice.
        var again = await new RoleClaimsTransformer(new OneGroupRoles(AnalystSid, AppRole.Analyst), sids: NoSidNames(),
            names: Ad("Jordan Rivera", "other@contoso.com")).TransformAsync(principal);
        again.FindAll(ClaimTypes.Email).Should().ContainSingle().Which.Value.Should().Be("jordan.rivera@contoso.com");
    }

    [Fact]
    public async Task AnEmailTheSignInCarries_IsNeverReplaced()
    {
        var names = new WindowsNames(_ => throw new InvalidOperationException("not asked"), TimeSpan.FromSeconds(5));
        var principal = await new RoleClaimsTransformer(new OneGroupRoles(AnalystSid, AppRole.Analyst), sids: NoSidNames(), names: names)
            .TransformAsync(WindowsSignIn(email: "signed.in@example.com", groupSids: AnalystSid));

        principal.FindAll(ClaimTypes.Email).Should().ContainSingle().Which.Value.Should().Be("signed.in@example.com");
        principal.HasClaim(AppClaimTypes.Permission, nameof(Permission.PresentBriefs)).Should().BeTrue();
    }

    [Fact]
    public async Task ALookupThatFails_LeavesTheEmailNull_AndTheSignInGoesOn()
    {
        var names = new WindowsNames(_ => throw new InvalidOperationException("the DC is down"), TimeSpan.FromMilliseconds(200));
        var principal = await new RoleClaimsTransformer(new OneGroupRoles(AnalystSid, AppRole.Analyst), sids: NoSidNames(), names: names)
            .TransformAsync(WindowsSignIn(groupSids: AnalystSid));

        principal.FindFirstValue(ClaimTypes.Email).Should().BeNull();
        principal.HasClaim(AppClaimTypes.Permission, nameof(Permission.PresentBriefs)).Should().BeTrue();
        UserOf(principal).Email.Should().BeNull();
    }

    [Fact]
    public async Task TheSessionReCheck_KeepsTheAdEmail_WhenTheRolesChange()
    {
        var directory = new OneGroupRoles(AnalystSid, AppRole.Analyst);
        var session = await new RoleClaimsTransformer(directory, sids: NoSidNames(), names: Ad("Jordan Rivera", "jordan.rivera@contoso.com"))
            .TransformAsync(WindowsSignIn(groupSids: AnalystSid));

        var refreshed = await new SessionPermissionRefresher(() => new OneGroupRoles(AnalystSid, AppRole.Reader), windowsMode: true, NoSidNames())
            .RefreshAsync(session);

        refreshed.Should().NotBeNull();
        refreshed!.HasClaim(AppClaimTypes.Permission, nameof(Permission.PresentBriefs)).Should().BeFalse();
        refreshed.FindAll(ClaimTypes.Email).Should().ContainSingle().Which.Value.Should().Be("jordan.rivera@contoso.com");
        UserOf(refreshed).Email.Should().Be("jordan.rivera@contoso.com");
    }

    [Fact]
    public async Task ASessionThatStartedWithoutTheEmail_GetsIt_OnTheReCheck_OnceAdAnswers()
    {
        // AD down when the circuit connected: no address on the session, and the roles are right.
        var directory = new OneGroupRoles(AnalystSid, AppRole.Analyst);
        var down = new WindowsNames(_ => throw new InvalidOperationException("the DC is down"), TimeSpan.FromMilliseconds(200));
        var session = await new RoleClaimsTransformer(directory, sids: NoSidNames(), names: down).TransformAsync(WindowsSignIn(groupSids: AnalystSid));
        session.FindFirstValue(ClaimTypes.Email).Should().BeNull();

        // Still down at the re-check: nothing changes.
        (await new SessionPermissionRefresher(() => directory, windowsMode: true, NoSidNames(), down).RefreshAsync(session)).Should().BeNull();

        // AD has answered: the session gets the address, with its roles as they were.
        var refreshed = await new SessionPermissionRefresher(() => directory, windowsMode: true, NoSidNames(), Ad("Jordan Rivera", "jordan.rivera@contoso.com"))
            .RefreshAsync(session);
        refreshed.Should().NotBeNull();
        UserOf(refreshed!).Email.Should().Be("jordan.rivera@contoso.com");
        refreshed.HasClaim(AppClaimTypes.Permission, nameof(Permission.PresentBriefs)).Should().BeTrue();

        // And the next re-check has nothing to add.
        (await new SessionPermissionRefresher(() => directory, windowsMode: true, NoSidNames(), Ad("Jordan Rivera", "other@contoso.com"))
            .RefreshAsync(refreshed)).Should().BeNull();
    }

    [Fact]
    public async Task TheReCheck_NeverAddsAnEmail_ToADevSession()
    {
        var names = new WindowsNames(_ => throw new InvalidOperationException("not asked"), TimeSpan.FromSeconds(5));
        var dev = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "CONTOSO\\dev"), new Claim(ClaimTypes.Role, nameof(AppRole.Analyst)),
            new Claim(AppClaimTypes.Permission, nameof(Permission.PresentBriefs))], "Dev"));
        var refreshed = await new SessionPermissionRefresher(() => new OneGroupRoles(AnalystSid, AppRole.Analyst), windowsMode: false, names: names)
            .RefreshAsync(dev);
        (refreshed ?? dev).FindFirstValue(ClaimTypes.Email).Should().BeNull();
    }

    // --- the user mirror ------------------------------------------------------------------------------------------

    private static UserDirectory Mirror(TestDb db)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => db.Context());
        return new UserDirectory(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), TimeProvider.System);
    }

    [Fact]
    public async Task TheMirror_RecordsTheAddress_AndKeepsItWhenALaterLookupHasNone()
    {
        using var db = new TestDb();
        var mirror = Mirror(db);

        await mirror.TouchAsync("S-1-5-21-1-2-3-500", "jdoe", null, null, "");
        await using (var ctx = db.Context())
            (await ctx.Users.SingleAsync()).Email.Should().BeNull();

        // AD answers on the next visit: the existing user heals.
        await mirror.TouchAsync("S-1-5-21-1-2-3-500", "Jordan Rivera", null, "jordan.rivera@contoso.com", "");
        await using (var ctx = db.Context())
            (await ctx.Users.SingleAsync()).Email.Should().Be("jordan.rivera@contoso.com");

        // The lookup times out on a later request: no address, and the known one stays.
        await mirror.TouchAsync("S-1-5-21-1-2-3-500", "jdoe", null, null, "");
        await using (var ctx = db.Context())
        {
            var row = await ctx.Users.SingleAsync();
            row.Email.Should().Be("jordan.rivera@contoso.com");
            row.DisplayName.Should().Be("jdoe");
        }
    }
}
