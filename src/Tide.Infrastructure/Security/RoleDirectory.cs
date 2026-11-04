using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tide.Application.Abstractions;
using Tide.Application.Security;
using Tide.Domain.Enums;
using Tide.Infrastructure.Persistence;

namespace Tide.Infrastructure.Security;

/// <summary>
/// The file-based AD security-group → role mapping (config section "RoleMapping"). Keys are role names; values are the
/// AD groups (or SIDs) that grant them. Used only to seed the database on first run; at runtime mappings come from the
/// database through <see cref="RoleDirectory"/>.
/// </summary>
public sealed class RoleMappingOptions
{
    public Dictionary<string, string[]> Groups { get; set; } = new();
}

/// <summary>
/// Singleton cache of the AD-group→role mappings. Reads the database on <see cref="Invalidate"/> and
/// answers sign-in lookups from memory, so resolving a user's permissions never costs a query per request. Roles and
/// their permissions are fixed in code (<see cref="RoleDefinitions"/>). Empty until the first <see cref="Invalidate"/>, which
/// startup makes once the schema is in place.
/// </summary>
public sealed class RoleDirectory : IRoleDirectory
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<RoleDirectory> _logger;
    private volatile IReadOnlyDictionary<string, IReadOnlySet<string>> _groupRoles = new Dictionary<string, IReadOnlySet<string>>();
    private readonly object _reload = new();
    private Timer? _retry;
    // Each load is numbered when it starts; a read that finishes after a newer one was stored is dropped, so a slow
    // reload can't put back a mapping a later change removed.
    private long _requested;
    private long _applied;

    /// <summary>How often a failed reload is tried again.</summary>
    internal static TimeSpan RetryEvery { get; set; } = TimeSpan.FromSeconds(30);

    public RoleDirectory(IServiceScopeFactory scopes, ILogger<RoleDirectory>? logger = null)
    {
        _scopes = scopes;
        _logger = logger ?? NullLogger<RoleDirectory>.Instance;
        // Nothing is read here: on a first run the schema isn't there until the startup migration, and reading it would
        // log a failed query. DatabaseInitializer calls Invalidate once the schema is in place.
    }

    public bool IsCurrent => _retry is null;

    public void Invalidate()
    {
        if (TryLoad(out var error))
        {
            ClearRetryIfCurrent();
            return;
        }
        // The change is saved, but sign-ins still resolve the previous mappings, so a revoked grant would stay in force
        // with nothing said. Say so, and keep trying until it takes.
        _logger.LogCritical(error, "The role directory couldn't reload from the database: role mapping changes aren't in " +
            "effect yet. Retrying every {Seconds} s.", RetryEvery.TotalSeconds);
        lock (_reload) _retry ??= new Timer(_ => Retry(), null, RetryEvery, RetryEvery);
    }

    private void Retry()
    {
        if (!TryLoad(out _)) return;
        if (ClearRetryIfCurrent()) _logger.LogWarning("The role directory reloaded: role mapping changes are in effect.");
    }

    private bool ClearRetryIfCurrent()
    {
        lock (_reload)
        {
            if (_retry is null || _applied != Interlocked.Read(ref _requested)) return false;
            _retry.Dispose();
            _retry = null;
            return true;
        }
    }

    private bool TryLoad(out Exception? error)
    {
        error = null;
        var generation = Interlocked.Increment(ref _requested);
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var maps = db.RoleMappings.AsNoTracking().ToList();
            var groupRoles = maps
                .Where(m => RoleDefinitions.TryParseRole(m.RoleName, out _))
                .GroupBy(m => m.AdGroup, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key,
                    g => (IReadOnlySet<string>)g.Select(x => x.RoleName).ToHashSet(StringComparer.OrdinalIgnoreCase),
                    StringComparer.OrdinalIgnoreCase);
            lock (_reload)
            {
                if (generation > _applied)
                {
                    _groupRoles = groupRoles;
                    _applied = generation;
                }
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex;
            return false;
        }
    }

    public IReadOnlySet<Permission> PermissionsForRoles(IEnumerable<string> roleNames) =>
        RoleDefinitions.PermissionsForRoleNames(roleNames);

    public bool IsRole(string name) => RoleDefinitions.TryParseRole(name, out _);

    public IReadOnlySet<string> RolesMappedByName =>
        _groupRoles.Where(g => !g.Key.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase))
            .SelectMany(g => g.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public IReadOnlySet<string> RolesForGroups(IEnumerable<string> adGroups)
    {
        var snap = _groupRoles;
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in adGroups)
            if (snap.TryGetValue(group, out var roles))
                set.UnionWith(roles);
        return set;
    }
}
