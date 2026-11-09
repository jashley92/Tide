using Tide.Domain.Common;

namespace Tide.Domain.Entities;

/// <summary>
/// A user mirrored from Active Directory on sign-in, so stable ids resolve to names in pickers and history. Roles are
/// resolved from AD group membership each sign-in and cached here for display. No passwords are ever stored.
/// </summary>
public class AppUser : Entity
{
    /// <summary>AD security identifier (or the dev sign-in's id): the stable key.</summary>
    public string Sid { get; set; } = string.Empty;
    public string UserPrincipalName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public DateTimeOffset LastSeenUtc { get; set; }

    /// <summary>Comma-separated <see cref="Enums.AppRole"/> names resolved from AD groups.</summary>
    public string RolesCsv { get; set; } = string.Empty;
}

/// <summary>
/// One person's choice of how something is shown to them (the Actions page as a list or a board), kept on the server so
/// it follows them to another computer. Keyed by the person's stable id and the preference's name; the names and the
/// values each may hold are <c>UserPreferences</c>' in the application layer. Not shared data, so not audited.
/// </summary>
public class UserPreference
{
    /// <summary>The person's stable id (<see cref="AppUser.Sid"/>, the signed-in user's id).</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Which preference, e.g. "actions.layout".</summary>
    public string Name { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

/// <summary>
/// One person's named combination of the Actions page's tab, layout and filters, shown beside its tabs. Kept as the
/// page's own query string, checked and put in a fixed order by <c>SavedViewService</c> before it's stored. One
/// person's, so not shared and not audited.
/// </summary>
public class SavedActionView : Entity
{
    public const int MaxName = 60;

    /// <summary>The longest query kept: every parameter at its longest, escaped, fits.</summary>
    public const int MaxQuery = 2000;

    /// <summary>The person's stable id (<see cref="AppUser.Sid"/>).</summary>
    public string UserId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    /// <summary>What follows <c>actions?</c>, e.g. "view=open&amp;layout=list&amp;priority=p1,p2". Never the list's page.</summary>
    public string Query { get; set; } = string.Empty;

    /// <summary>Where it sits among the person's views, from 0.</summary>
    public int SortOrder { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}

/// <summary>
/// Grants a built-in role to everyone in an AD security group. A group may grant several roles and a role may come
/// from several groups. Seeded from the <c>RoleMapping</c> configuration section on first run, then administered in
/// the app.
/// </summary>
public class AdGroupRoleMapping : Entity
{
    /// <summary>AD security group name (<c>DOMAIN\Group</c> or <c>Group</c>) or SID whose members receive the role.</summary>
    public string AdGroup { get; set; } = string.Empty;

    /// <summary>The <see cref="Enums.AppRole"/> name granted to members of <see cref="AdGroup"/>.</summary>
    public string RoleName { get; set; } = string.Empty;

    public DateTimeOffset UpdatedAtUtc { get; set; }
    public string UpdatedBy { get; set; } = string.Empty;
}

/// <summary>
/// One change to a record, written in the same transaction as the change by the audit interceptor. Append-only: the
/// app never updates or deletes these rows.
/// </summary>
public class AuditLogEntry : Entity
{
    public DateTimeOffset AtUtc { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;

    /// <summary>Added, Modified or Deleted.</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>The entity type's name, e.g. "Article".</summary>
    public string EntityType { get; set; } = string.Empty;

    /// <summary>The changed row's key, as text (link rows have a composite key).</summary>
    public string EntityKey { get; set; } = string.Empty;

    /// <summary>The changed properties as JSON: <c>{"Name": ["old", "new"]}</c>. Long text is cut short.</summary>
    public string? ChangesJson { get; set; }
}

/// <summary>A setting an administrator saved in the app, overriding the configured default for that key.</summary>
public class AppSetting
{
    /// <summary>The configuration key it overrides, e.g. "Distribution:PublicMaxTlp".</summary>
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public string UpdatedBy { get; set; } = string.Empty;
}

/// <summary>
/// A file kept on disk by the file store (screenshots today). The database holds what it is; the bytes live under
/// <c>Files:RootPath</c>, outside the web root, named by <see cref="Entity.Id"/>.
/// </summary>
public class StoredFile : AuditableEntity
{
    public string FileName { get; set; } = string.Empty;

    /// <summary>The type sniffed from the bytes, never the one the browser claimed.</summary>
    public string ContentType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }

    /// <summary>Lower-case hex SHA-256 of the bytes.</summary>
    public string Sha256 { get; set; } = string.Empty;
}
