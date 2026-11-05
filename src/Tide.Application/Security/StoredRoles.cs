using Tide.Domain.Enums;

namespace Tide.Application.Security;

/// <summary>A stored user's roles (<c>RolesCsv</c>, as last seen at sign-in), for those who act on others' behalf.</summary>
internal static class StoredRoles
{
    /// <summary>Whether the roles in <paramref name="rolesCsv"/> grant <paramref name="permission"/> (names that aren't a
    /// role are ignored).</summary>
    public static bool Has(string rolesCsv, Permission permission) =>
        RoleDefinitions.PermissionsForRoleNames(rolesCsv.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .Contains(permission);
}
