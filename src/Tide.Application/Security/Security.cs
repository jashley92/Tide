using Tide.Domain.Common;
using Tide.Domain.Enums;

namespace Tide.Application.Security;

/// <summary>
/// The built-in roles and the permissions each grants. Roles are fixed in code; which AD groups hold them is
/// administered in the app. Authorization runs on the permissions, never on a role name.
/// </summary>
public static class RoleDefinitions
{
    public static readonly IReadOnlyDictionary<AppRole, Permission[]> SystemRoles = new Dictionary<AppRole, Permission[]>
    {
        [AppRole.Analyst] =
        [
            Permission.ReadPublished, Permission.ReadTeamEdition, Permission.WriteArticles, Permission.PresentBriefs,
            Permission.WorkActions, Permission.EnterMetrics,
        ],
        [AppRole.ActionOwner] = [Permission.ReadPublished, Permission.ReadTeamEdition, Permission.WorkActions],
        [AppRole.Reader] = [Permission.ReadPublished],
        // Administrators hold every permission, including ones added to the enum later.
        [AppRole.Admin] = Enum.GetValues<Permission>(),
    };

    /// <summary>The union of permissions granted by the given roles.</summary>
    public static IReadOnlySet<Permission> PermissionsFor(IEnumerable<AppRole> roles)
    {
        var set = new HashSet<Permission>();
        foreach (var role in roles)
            if (SystemRoles.TryGetValue(role, out var perms))
                set.UnionWith(perms);
        return set;
    }

    /// <summary>The union of permissions for role names (names that aren't a role are ignored).</summary>
    public static IReadOnlySet<Permission> PermissionsForRoleNames(IEnumerable<string> roleNames) =>
        PermissionsFor(roleNames.Select(n => TryParseRole(n, out var r) ? (AppRole?)r : null).OfType<AppRole>());

    /// <summary>A role by its name only (case-insensitive): never a number, a padded name or a comma list.</summary>
    public static bool TryParseRole(string? name, out AppRole role) => EnumNames.TryParseExact(name, out role, ignoreCase: true);
}

/// <summary>Authorization policy names, one per permission.</summary>
public static class Policies
{
    public const string ReadPublished = nameof(ReadPublished);
    public const string ReadTeamEdition = nameof(ReadTeamEdition);
    public const string WriteArticles = nameof(WriteArticles);
    public const string PresentBriefs = nameof(PresentBriefs);
    public const string WorkActions = nameof(WorkActions);
    public const string Administer = nameof(Administer);
    public const string EnterMetrics = nameof(EnterMetrics);

    /// <summary>
    /// The permission each policy requires. The Web layer registers one policy per entry requiring the matching
    /// permission claim, so no policy is tied to a role.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, Permission> Required = new Dictionary<string, Permission>
    {
        [ReadPublished] = Permission.ReadPublished,
        [ReadTeamEdition] = Permission.ReadTeamEdition,
        [WriteArticles] = Permission.WriteArticles,
        [PresentBriefs] = Permission.PresentBriefs,
        [WorkActions] = Permission.WorkActions,
        [Administer] = Permission.Administer,
        [EnterMetrics] = Permission.EnterMetrics,
    };

    /// <summary>What a person without the permission is told it allows.</summary>
    public static string Describe(Permission p) => p switch
    {
        Permission.ReadPublished => "read the published brief archive",
        Permission.ReadTeamEdition => "read the full (team) edition of briefs",
        Permission.WriteArticles => "write articles and build briefs",
        Permission.PresentBriefs => "present and send briefs",
        Permission.WorkActions => "work action items",
        Permission.Administer => "administer TIDE",
        Permission.EnterMetrics => "enter the day's opening metrics",
        _ => p.ToString(),
    };
}

/// <summary>Thrown when a service is asked for something the current user's permissions don't allow.</summary>
public sealed class ForbiddenException(string message) : Exception(message);
