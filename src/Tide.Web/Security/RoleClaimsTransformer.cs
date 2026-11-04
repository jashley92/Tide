using System.Security.Claims;
using System.Security.Principal;
using Tide.Application.Abstractions;
using Tide.Application.Security;
using Microsoft.AspNetCore.Authentication;

namespace Tide.Web.Security;

/// <summary>
/// For Windows-authenticated users, translates AD group membership into role and permission claims
/// using the database-backed <see cref="IRoleDirectory"/>. Applied only in Windows auth mode.
/// </summary>
public sealed class RoleClaimsTransformer : IClaimsTransformation
{
    // SID -> account name, kept (an unresolved one only for a while) and bounded on the request path.
    private static readonly SidNames Sids = new();

    // The lookups below are static (shared with the session re-check), so they log through the most recent instance's
    // logger: the serving host's, never one left behind by a host that has stopped (several run in one test process).
    // A logger that fails (its provider gone with its host) is dropped: nothing here may throw into a sign-in.
    private static ILogger? _log;
    private readonly IRoleDirectory _directory;
    private readonly SidNames? _sids;

    /// <param name="sids">Tests only: stands in for the shared SID lookup.</param>
    public RoleClaimsTransformer(IRoleDirectory directory, ILogger<RoleClaimsTransformer>? log = null, SidNames? sids = null)
    {
        _directory = directory;
        _sids = sids;
        if (log is not null) _log = log;
        DirectoryLog.Use(log);
    }

    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        // Run again on the same principal (each AuthenticateAsync in a request transforms it), it added another
        // identity each time; the grants are made once.
        if (principal.Identity?.IsAuthenticated != true || principal.Identities.Any(i => i.Label == GrantsLabel))
            return principal;
        // An API token's caller has its scopes' permissions, never an AD group's role.
        if (ApiTokenAuthenticationHandler.IsToken(principal))
            return principal;

        // A group not named in time gives this request only its by-SID mappings (failing closed).
        var groups = await GroupCandidatesAsync(principal, _sids);
        // And the request says so, so the user mirror doesn't store its short role list as the person's roles.
        if (!groups.Complete && !GroupsIncomplete(principal))
            principal.AddIdentity(new ClaimsIdentity { Label = IncompleteLabel });
        var roleNames = _directory.RolesForGroups(groups.Names);
        if (roleNames.Count == 0)
            return principal;

        // Unauthenticated (no authentication type), so it's never read as the sign-in's groups.
        var identity = new ClaimsIdentity { Label = GrantsLabel };
        foreach (var role in roleNames)
        {
            // From the claims. A WindowsPrincipal's IsInRole translated the name in AD (unbounded, on every
            // request) and said yes for an AD group named like the role, whose claim was then never added.
            if (!principal.HasClaim(c => c.Type == ClaimTypes.Role && string.Equals(c.Value, role, StringComparison.OrdinalIgnoreCase)))
                identity.AddClaim(new Claim(ClaimTypes.Role, role));
        }

        // Permission claims are what policies authorize on.
        foreach (var perm in _directory.PermissionsForRoles(roleNames))
        {
            if (!principal.HasClaim(AppClaimTypes.Permission, perm.ToString()))
                identity.AddClaim(new Claim(AppClaimTypes.Permission, perm.ToString()));
        }

        principal.AddIdentity(identity);
        return principal;
    }

    /// <summary>Marks the identity carrying TIDE's grants.</summary>
    internal const string GrantsLabel = "TIDE grants";

    // An empty, unauthenticated identity marking a request whose groups weren't all named in time.
    private const string IncompleteLabel = "TIDE groups incomplete";

    /// <summary>True when this request's roles were decided from a partial answer (some of its groups weren't
    /// named in time), so they may be short of the person's real roles and mustn't be recorded as them.</summary>
    public static bool GroupsIncomplete(ClaimsPrincipal principal) => principal.Identities.Any(i => i.Label == IncompleteLabel);

    /// <summary>
    /// The AD group identifiers on a Windows principal to match against the group→role mappings. Windows groups arrive
    /// as Role/GroupSid claims whose VALUES are SIDs, but mappings are configured by NAME ("SOC-AppAdmins"), so this
    /// yields both the raw SID and its resolved name, with and without the DOMAIN\ prefix. Shared with the
    /// session re-check so both resolve groups identically.
    /// </summary>
    public static GroupNames GroupCandidates(ClaimsPrincipal principal, SidNames? sids = null) =>
        CandidatesFor(GroupValues(principal), sids);

    /// <summary>As <see cref="GroupCandidates"/>, without holding the caller's thread while the names are looked up.</summary>
    public static Task<GroupNames> GroupCandidatesAsync(ClaimsPrincipal principal, SidNames? sids = null) =>
        CandidatesForAsync(GroupValues(principal), sids);

    // Only the signed-in identity's claims. The roles TIDE grants ride on an unauthenticated identity it adds
    // (here and in the session re-check); read as groups, a role named like an AD group picked up that group's mappings, and
    // a removed mapping never took the role away.
    private static List<string> GroupValues(ClaimsPrincipal principal) =>
        principal.Identities.Where(i => i.IsAuthenticated).SelectMany(i => i.Claims)
            .Where(c => c.Type == ClaimTypes.Role || c.Type == ClaimTypes.GroupSid)
            .Select(c => c.Value).ToList();

    /// <summary>The names a set of groups matches mappings by. <see cref="Complete"/> is false when some of them
    /// may be missing (a SID not named in time, or a bare name withheld because the server's domain isn't known yet), so a
    /// caller that must not act on a partial answer can tell.</summary>
    public sealed record GroupNames(IReadOnlySet<string> Names, bool Complete);

    /// <summary>Each group value (a SID, or already a name) with the account name(s) of the SIDs among them.
    /// The SIDs not yet named are translated together, in one bounded wait, rather than one blocking call each.</summary>
    internal static GroupNames CandidatesFor(IEnumerable<string> values, SidNames? sids = null)
    {
        var list = values.ToList();
        var answer = (sids ?? Sids).Resolve(list);
        return Candidates(list, answer, answer.Names.Count > 0 ? ServerDomainSid() : null);
    }

    /// <summary>As <see cref="CandidatesFor"/>, waiting asynchronously.</summary>
    internal static async Task<GroupNames> CandidatesForAsync(IEnumerable<string> values, SidNames? sids = null)
    {
        var list = values.ToList();
        var answer = await (sids ?? Sids).ResolveAsync(list);
        return Candidates(list, answer, answer.Names.Count > 0 ? await ServerDomainSidAsync() : null);
    }

    /// <summary>The account name(s) for each group SID resolved: "DOMAIN\Group" and the bare "Group". Public for a test
    ///, with <paramref name="serverDomainSid"/> as <see cref="IsLocalToServer"/> takes it.</summary>
    public static GroupNames Candidates(IReadOnlyList<string> values, SidNames.Answer answer, string? serverDomainSid)
    {
        var complete = answer.Complete;
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            candidates.Add(value);
            if (!answer.Names.TryGetValue(value, out var resolved)) continue;
            candidates.Add(resolved);                                                   // DOMAIN\Group
            var slash = resolved.IndexOf('\\');
            if (slash < 0) continue;
            // A group name is unique only within its domain, so the bare "Group" is offered only for a group of
            // this server's own domain (or the machine itself). A same-named group made in a trusted domain matches only
            // a mapping that names its domain.
            var domain = resolved[..slash];
            if (IsLocalToServer(value, domain, Environment.MachineName, serverDomainSid)) candidates.Add(resolved[(slash + 1)..]);   // Group
            // Withheld only because the server's domain isn't known yet, so the answer may be short.
            else if (serverDomainSid == "" && IsLocalToServer(value, domain, Environment.MachineName, null)) complete = false;
        }
        return new GroupNames(candidates, complete);
    }

    // The domain this server is joined to, as the domain part of its computer account's SID. Only a found one
    // is kept. A failed lookup was cached for the life of the process as "no domain", which offers bare names for every
    // domain's groups: one DC failover at app-pool start undid the same-domain check until the next recycle, with nothing logged.
    private static string? _serverDomainSid;
    private static DateTime _serverDomainRetryUtc;
    private static bool _serverDomainWarned;
    private static readonly object ServerDomainLock = new();
    private static readonly TimeSpan RetryServerDomain = TimeSpan.FromMinutes(1);

    // Null when the server isn't domain-joined (no other domain to tell apart); "" when it is but its domain couldn't
    // be found, which matches no domain, so only names that carry their domain map until it's found (fail closed).
    // The lookup was made under ServerDomainLock with no time limit, so a hung DC queued every request that
    // asked behind it. It's now bounded like the SIDs' names: while it hasn't answered, the domain isn't known ("").
    private static readonly BoundedDirectoryCalls<string?> ServerDomainLookup = new(
        _ => new Dictionary<string, string?> { [""] = FindServerDomainSid() }, (_, _) => { }, _ => "", 1, 1, log: DirectoryLog.Instance);

    private static string? ServerDomainSid()
    {
        if (KnownServerDomainSid(out var sid)) return sid;
        ServerDomainLookup.Wait([""], SidNames.Timeout);
        return _serverDomainSid ?? "";
    }

    private static async Task<string?> ServerDomainSidAsync()
    {
        if (KnownServerDomainSid(out var sid)) return sid;
        await ServerDomainLookup.WaitAsync([""], SidNames.Timeout);
        return _serverDomainSid ?? "";
    }

    // Whether the answer is to hand without asking the directory.
    private static bool KnownServerDomainSid(out string? sid)
    {
        sid = _serverDomainSid;
        if (sid is not null || !OperatingSystem.IsWindows()) return true;
        lock (ServerDomainLock)
        {
            sid = _serverDomainSid;
            if (sid is not null) return true;
            if (!IsDomainJoined()) return true;   // null: not on a domain
            if (DateTime.UtcNow >= _serverDomainRetryUtc) return false;
            sid = "";
            return true;
        }
    }

    // On the lookup's own thread; records what it found (or when to try again), even after its callers stopped waiting.
    private static string? FindServerDomainSid()
    {
        string? found = null;
        Exception? error = null;
        try
        {
            if (OperatingSystem.IsWindows())
                found = ((SecurityIdentifier)new NTAccount(Environment.MachineName + "$").Translate(typeof(SecurityIdentifier)))
                    .AccountDomainSid?.Value;
        }
        catch (Exception ex) { error = ex; }
        lock (ServerDomainLock)
        {
            if (found is not null)
            {
                if (_serverDomainWarned) Log(l => l.LogWarning("Found this server's domain: bare AD group names map again."));
                _serverDomainWarned = false;
                return _serverDomainSid = found;
            }
            if (!_serverDomainWarned)
                Log(l => l.LogCritical(error, "Couldn't look up this server's AD domain (a DC unreachable?). Until it's found, " +
                    "only role mappings that name the domain (DOMAIN\\Group) take effect. Retrying every {Seconds} s.",
                    RetryServerDomain.TotalSeconds));
            _serverDomainWarned = true;
            _serverDomainRetryUtc = DateTime.UtcNow + RetryServerDomain;
            return null;
        }
    }

    private static void Log(Action<ILogger> write)
    {
        if (_log is not { } log) return;
        try { write(log); }
        catch (Exception) { /* the logger's host is gone, or a provider failed: not this sign-in's problem */ }
    }

    private static bool IsDomainJoined()
    {
        try { return !string.IsNullOrEmpty(System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().DomainName); }
        catch { return true; }   // can't tell: assume joined, so a failed lookup fails closed
    }

    /// <summary>Whether a group (its SID and the domain part of its name) belongs to the server: a local or built-in
    /// group, or one of the server's domain (<paramref name="serverDomainSid"/>; null when the server isn't on a domain,
    /// so any; "" when it is but the domain isn't known yet, so none).</summary>
    public static bool IsLocalToServer(string sid, string domain, string machineName, string? serverDomainSid)
    {
        if (!OperatingSystem.IsWindows()) return true;
        if (string.Equals(domain, machineName, StringComparison.OrdinalIgnoreCase)) return true;   // a local group
        SecurityIdentifier? groupDomain;
        try { groupDomain = new SecurityIdentifier(sid).AccountDomainSid; }
        catch (ArgumentException) { return false; }
        if (groupDomain is null) return true;   // a built-in group (BUILTIN\…, NT AUTHORITY\…): this machine's
        return serverDomainSid is null || string.Equals(groupDomain.Value, serverDomainSid, StringComparison.OrdinalIgnoreCase);
    }
}
