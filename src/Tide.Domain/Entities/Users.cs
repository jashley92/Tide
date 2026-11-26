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
/// One person's named combination of a list page's tab, layout and filters (Actions, Triage, Articles or Search), shown
/// as a pill on that page. Kept as the page's own query string, checked and put in a fixed order by
/// <c>SavedViewService</c> before it's stored. One person's, so not shared and not audited. The table keeps the name it
/// had when only Actions had views: renaming it wasn't worth a migration.
/// </summary>
public class SavedActionView : Entity
{
    public const int MaxName = 60;

    /// <summary>The longest <see cref="Page"/>.</summary>
    public const int MaxPage = 20;

    /// <summary>The page of every view kept from before the other pages had views.</summary>
    public const string ActionsPage = "actions";

    /// <summary>The longest query kept: every parameter at its longest, escaped, fits.</summary>
    public const int MaxQuery = 2000;

    /// <summary>The person's stable id (<see cref="AppUser.Sid"/>).</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>The page it's a view of: "actions", "triage", "articles" or "search" (<c>SavedViewPage.Key</c>).</summary>
    public string Page { get; set; } = ActionsPage;
    public string Name { get; set; } = string.Empty;

    /// <summary>What follows the page's address and <c>?</c>, e.g. "view=open&amp;layout=list&amp;priority=p1,p2". Never the list's page.</summary>
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

    /// <summary>When its bytes were deleted by the removed-file purge (<c>Files:PurgeRemovedAfterDays</c>, off by default):
    /// every place that used it had removed it that long before. The row and its hash stay, for the record.</summary>
    public DateTimeOffset? BytesPurgedAtUtc { get; set; }
}

/// <summary>
/// Something a colleague did that's addressed to one person: an article handed to them, a review asked of them, an
/// action handed to their team or assigned to them, a brief rotation request of theirs or for them. Written in the same
/// save as what it's about (<c>NoticeInterceptor</c>), so there's never a notice without the change, and shown in the
/// top bar's For you panel. What it's about is read when it's shown, so a renamed article shows its new title and a
/// deleted one shows as gone. Kept until a while after it's seen (<c>NoticeService.PruneAsync</c>). One person's, so not audited.
/// </summary>
public class Notice : Entity
{
    /// <summary>The longest <see cref="Detail"/>: the longest hand-off reason.</summary>
    public const int MaxDetail = 500;

    /// <summary>Whom it's for (<see cref="AppUser.Sid"/>).</summary>
    public string UserId { get; set; } = string.Empty;

    public Enums.NoticeKind Kind { get; set; }

    /// <summary>What it's about: the article (handed on, or its review's), the action, or the rotation request.</summary>
    public Guid SubjectId { get; set; }

    /// <summary>Who did it.</summary>
    public string ActorId { get; set; } = string.Empty;
    public string ActorName { get; set; } = string.Empty;

    /// <summary>What they said with it, if anything: the hand-off's note or reason, the review request's note, the
    /// rotation request's note or decision note.</summary>
    public string? Detail { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    /// <summary>When they clicked it, or marked everything seen; null while it's new.</summary>
    public DateTimeOffset? SeenAtUtc { get; set; }
}
