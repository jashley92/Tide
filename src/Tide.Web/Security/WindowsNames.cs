using System.Collections.Concurrent;
using System.DirectoryServices.AccountManagement;
using System.Runtime.Versioning;

namespace Tide.Web.Security;

/// <summary>
/// What AD says about a person: their display name and their email address (the <c>mail</c> attribute). Either may be
/// missing. The email is only ever what AD holds; a UPN that looks like an address is never taken for one.
/// </summary>
public sealed record DirectoryPerson(string? DisplayName, string? Email);

/// <summary>
/// Turns a Windows account name (<c>DOMAIN\sam</c>) into the person's real display name and email address. Windows
/// authentication only gives us <c>DOMAIN\sam</c>; this looks up the AD display name (e.g. "Jordan
/// Rivera") so the UI never shows the domain or the bare logon name, and their <c>mail</c> in the same call, as a Windows
/// sign-in carries no email claim. A person found is cached for the process lifetime;
/// every failure path degrades gracefully to the bare username (never the domain), and is tried again after
/// <see cref="RetryUnresolved"/>, so one domain-controller blip doesn't fix "jdoe" as someone's name until the
/// app pool recycles (written into the user mirror and into hashed records such as assignments).
/// <para>
/// It runs on every authenticated request, so the lookup is bounded (<see cref="BoundedDirectoryCalls{T}"/>): one
/// per name at a time, waited on for <see cref="Timeout"/>, after which the bare username is used (not kept, so it's
/// looked up again) and a name that lands later is kept. A domain with <see cref="MaxHung"/> lookups still blocked isn't
/// asked again until one returns, so a hung domain controller holds a few threads, not one per request.
/// </para>
/// </summary>
public sealed class WindowsNames
{
    public static readonly TimeSpan RetryUnresolved = TimeSpan.FromMinutes(5);

    /// <summary>How long a request waits for a display name (and email) before showing the bare username. Short: only a
    /// name and an address are at stake, and they're put right once the lookup lands.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    /// <summary>Lookups running at once in one domain, including ones still blocked after their callers gave up.</summary>
    public const int MaxInFlight = 8;

    /// <summary>Lookups in one domain still blocked after timing out, past which it isn't asked again.</summary>
    public const int MaxHung = 2;

    /// <summary>The process-wide lookup the sign-in, <see cref="CurrentUser"/> and the user mirror share.</summary>
    internal static WindowsNames Shared { get; } = new();

    private readonly ConcurrentDictionary<string, (DirectoryPerson? Person, DateTimeOffset RetryAfterUtc)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly BoundedDirectoryCalls<DirectoryPerson?> _calls;
    private readonly TimeSpan _timeout;
    private readonly TimeProvider _time;

    /// <param name="query">What AD holds for a <c>DOMAIN\sam</c>, or null (tests stand in for AD here).</param>
    /// <param name="log">Told when a lookup is written off as never returning; the shared, failure-proof
    /// <see cref="DirectoryLog"/> by default (the shared instance outlives any one host).</param>
    public WindowsNames(Func<string, DirectoryPerson?>? query = null, TimeSpan? timeout = null, TimeProvider? time = null, ILogger? log = null)
    {
        var lookup = query ?? ResolvePerson;
        _timeout = timeout ?? Timeout;
        _time = time ?? TimeProvider.System;
        _calls = new BoundedDirectoryCalls<DirectoryPerson?>(
            names => names.ToDictionary(n => n, n => Safe(lookup, n), StringComparer.OrdinalIgnoreCase),
            Landed, DomainOf, MaxInFlight, MaxHung, _time, log ?? DirectoryLog.Instance);
    }

    /// <summary>
    /// A human-friendly name for an identity: the AD display name for a <c>DOMAIN\sam</c> account, the
    /// bare sAMAccountName if AD can't be reached (or doesn't answer in time), or the value unchanged when it isn't a
    /// domain account (e.g. the dev "Dev Analyst", or a UPN). Null in, null out.
    /// </summary>
    public static string? Friendly(string? identityName) => Shared.Resolve(identityName);

    /// <summary>As <see cref="Friendly"/>, without holding the caller's thread while AD is asked.</summary>
    public static Task<string?> FriendlyAsync(string? identityName) => Shared.ResolveAsync(identityName);

    public string? Resolve(string? identityName)
    {
        if (Known(identityName, out var known)) return known;
        _calls.Wait([identityName!], _timeout);
        return Kept(identityName!);
    }

    public async Task<string?> ResolveAsync(string? identityName)
    {
        if (Known(identityName, out var known)) return known;
        await _calls.WaitAsync([identityName!], _timeout);
        return Kept(identityName!);
    }

    /// <summary>
    /// As <see cref="ResolveAsync"/>, saying too whether the name is only a stand-in: the bare username of a
    /// <c>DOMAIN\sam</c> AD gave no display name for (it didn't answer in time, failed, or has none). The user mirror
    /// doesn't put a stand-in over a name it already holds. A name that isn't a domain account is never one.
    /// </summary>
    public async Task<(string? Name, bool StandIn)> ResolveForMirrorAsync(string? identityName) =>
        Named(identityName, await ResolveAsync(identityName));

    /// <summary>As <see cref="ResolveForMirrorAsync"/>, for a caller that can't await (<see cref="CurrentUser"/>): the same
    /// bounded wait as <see cref="Friendly"/>, and no further lookup.</summary>
    public static (string? Name, bool StandIn) FriendlyOrStandIn(string? identityName) =>
        Shared.Named(identityName, Shared.Resolve(identityName));

    // The name found, and whether it's only the bare username standing in for a DOMAIN\sam AD gave no name for.
    private (string? Name, bool StandIn) Named(string? identityName, string? name)
    {
        var slash = identityName?.IndexOf('\\') ?? -1;
        if (slash < 0 || slash == identityName!.Length - 1) return (name, name is null);
        return _cache.TryGetValue(identityName, out var cached) && cached.Person?.DisplayName is { } found
            ? (found, false)
            : (identityName[(slash + 1)..], true);
    }

    /// <summary>
    /// The AD email address (<c>mail</c>) of a <c>DOMAIN\sam</c> account, from the same lookup and cache as its display
    /// name (one AD call gives both). Null when it isn't a domain account, AD has no <c>mail</c> for it, or AD didn't answer
    /// in time (then it's asked again, as for the name). Never throws.
    /// </summary>
    public async Task<string?> EmailAsync(string? identityName)
    {
        if (!Known(identityName, out _)) await _calls.WaitAsync([identityName!], _timeout);
        return identityName is not null && _cache.TryGetValue(identityName, out var cached) ? cached.Person?.Email : null;
    }

    // The answer without asking AD: for a name that isn't DOMAIN\sam, one kept, or one that failed a moment ago.
    private bool Known(string? identityName, out string? name)
    {
        name = identityName;
        if (string.IsNullOrWhiteSpace(identityName)) return true;
        var slash = identityName.IndexOf('\\');
        if (slash < 0) return true;                         // not DOMAIN\sam (dev auth, UPN, plain name)
        name = identityName[(slash + 1)..];
        if (name.Length == 0) return true;                  // no sam to look up
        if (_cache.TryGetValue(identityName, out var cached) && (cached.Person is not null || _time.GetUtcNow() < cached.RetryAfterUtc))
        {
            name = cached.Person?.DisplayName ?? name;
            return true;
        }
        return false;
    }

    // What's kept once the wait ends; the bare username when the lookup hasn't landed (or found nothing).
    private string Kept(string identityName) =>
        _cache.TryGetValue(identityName, out var cached) && cached.Person?.DisplayName is { } name
            ? name
            : identityName[(identityName.IndexOf('\\') + 1)..];

    // A person with a name or an address is kept; one with neither (or no answer) is tried again after RetryUnresolved.
    private void Landed(IReadOnlyList<string> names, IReadOnlyDictionary<string, DirectoryPerson?> found)
    {
        var retry = _time.GetUtcNow() + RetryUnresolved;
        foreach (var n in names)
            _cache[n] = found.GetValueOrDefault(n) is { } person ? (person, DateTimeOffset.MaxValue) : (null, retry);
    }

    private static string DomainOf(string domainAndSam) => domainAndSam[..domainAndSam.IndexOf('\\')];

    private static DirectoryPerson? Safe(Func<string, DirectoryPerson?> lookup, string domainAndSam)
    {
        try
        {
            if (lookup(domainAndSam) is not { } found) return null;
            var person = new DirectoryPerson(Trimmed(found.DisplayName), Trimmed(found.Email));
            return person.DisplayName is null && person.Email is null ? null : person;
        }
        catch { return null; }   // degrade to the bare username and no email, tried again after RetryUnresolved
    }

    private static string? Trimmed(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static DirectoryPerson? ResolvePerson(string domainAndSam)
    {
        if (!OperatingSystem.IsWindows()) return null;
        return ResolveOnWindows(domainAndSam);
    }

    [SupportedOSPlatform("windows")]
    private static DirectoryPerson? ResolveOnWindows(string domainAndSam)
    {
        try
        {
            var slash = domainAndSam.IndexOf('\\');
            var domain = domainAndSam[..slash];
            var sam = domainAndSam[(slash + 1)..];

            // The account's own domain, not the server's. A trusted-domain user's sam looked up in the server's
            // domain found a namesake there, whose name was shown and stored. A local account (the server's own name)
            // is looked up on the machine. An authenticated account can read display-name attributes. Kept
            // short-lived; the result is cached by the caller.
            using var ctx = string.Equals(domain, Environment.MachineName, StringComparison.OrdinalIgnoreCase)
                ? new PrincipalContext(ContextType.Machine)
                : new PrincipalContext(ContextType.Domain, domain.Length == 0 ? null : domain);
            using var user = UserPrincipal.FindByIdentity(ctx, IdentityType.SamAccountName, sam);
            if (user is null) return null;

            var name = user.DisplayName;
            if (string.IsNullOrWhiteSpace(name))
                name = string.Join(' ',
                    new[] { user.GivenName, user.Surname }.Where(s => !string.IsNullOrWhiteSpace(s)));

            // The mail attribute, read in the same call. The UPN is not an address, however much it may look like one.
            return new DirectoryPerson(name, user.EmailAddress);
        }
        catch
        {
            // AD unreachable, insufficient rights, or a non-domain account — degrade to the bare username, no email.
            return null;
        }
    }
}
