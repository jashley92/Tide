using Microsoft.EntityFrameworkCore;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.Application.Abstractions;

/// <summary>The database as the application layer sees it. Implemented by the Infrastructure DbContext.</summary>
public interface IAppDbContext : IAsyncDisposable, IDisposable
{
    DbSet<AppUser> Users { get; }
    DbSet<UserPreference> UserPreferences { get; }
    DbSet<AdGroupRoleMapping> RoleMappings { get; }
    DbSet<AuditLogEntry> AuditLog { get; }
    DbSet<AppSetting> Settings { get; }
    DbSet<StoredFile> Files { get; }
    DbSet<ScorecardCategory> ScorecardCategories { get; }
    DbSet<ScorecardTag> ScorecardTags { get; }
    DbSet<Article> Articles { get; }
    DbSet<ArticleBullet> ArticleBullets { get; }
    DbSet<ArticleScreenshot> ArticleScreenshots { get; }
    DbSet<ArticleTag> ArticleTags { get; }
    DbSet<StackProduct> StackProducts { get; }
    DbSet<ArticleProduct> ArticleProducts { get; }
    DbSet<AiDraft> AiDrafts { get; }
    DbSet<ApiToken> ApiTokens { get; }
    DbSet<Adversary> Adversaries { get; }
    DbSet<ArticleAdversary> ArticleAdversaries { get; }
    DbSet<DetectionRule> DetectionRules { get; }
    DbSet<DetectionGap> DetectionGaps { get; }
    DbSet<ArticleReview> ArticleReviews { get; }
    DbSet<ReviewComment> ReviewComments { get; }
    DbSet<Webhook> Webhooks { get; }
    DbSet<ArticleTechnique> ArticleTechniques { get; }
    DbSet<ArticleIndicator> ArticleIndicators { get; }
    DbSet<ArticleCve> ArticleCves { get; }
    DbSet<ArticleLink> ArticleLinks { get; }
    DbSet<Brief> Briefs { get; }
    DbSet<BriefItem> BriefItems { get; }
    DbSet<BriefSend> BriefSends { get; }
    DbSet<MeetingAttendee> MeetingAttendees { get; }
    DbSet<MeetingNote> MeetingNotes { get; }
    DbSet<BriefSendImage> BriefSendImages { get; }
    DbSet<BriefCorrection> BriefCorrections { get; }
    DbSet<MetricGroup> MetricGroups { get; }
    DbSet<MetricDefinition> MetricDefinitions { get; }
    DbSet<MetricValue> MetricValues { get; }
    DbSet<MetricImport> MetricImports { get; }
    DbSet<Team> Teams { get; }
    DbSet<TeamMember> TeamMembers { get; }
    DbSet<ActionItem> Actions { get; }
    DbSet<ActionComment> ActionComments { get; }
    DbSet<ActionEvent> ActionEvents { get; }
    DbSet<ActionCommentFile> ActionCommentFiles { get; }
    DbSet<ActionFile> ActionFiles { get; }
    DbSet<ActionChecklistItem> ActionChecklistItems { get; }
    DbSet<Playbook> Playbooks { get; }
    DbSet<PlaybookStep> PlaybookSteps { get; }
    DbSet<ActionCardRule> ActionCardRules { get; }
    DbSet<HuntQuery> HuntQueries { get; }
    DbSet<QueryTemplate> QueryTemplates { get; }
    DbSet<NotificationMark> NotificationMarks { get; }
    DbSet<Source> Sources { get; }
    DbSet<FeedItem> FeedItems { get; }
    DbSet<FeedItemText> FeedItemTexts { get; }
    DbSet<Story> Stories { get; }
    DbSet<CveRecord> CveRecords { get; }
    DbSet<RotationMember> RotationMembers { get; }
    DbSet<RotationWeek> RotationWeeks { get; }
    DbSet<RotationChange> RotationChanges { get; }
    DbSet<RotationOverride> RotationOverrides { get; }
    DbSet<Holiday> Holidays { get; }

    /// <summary>The connection's transactions, for a claim that takes several rows or none (a group promote).</summary>
    Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade Database { get; }

    Task<int> SaveChangesAsync(CancellationToken ct = default);
}

/// <summary>
/// Makes a short-lived context per operation. Blazor Server keeps a scope for a whole circuit, so one shared context
/// would be used by overlapping event handlers ("a second operation was started on this context").
/// </summary>
public interface IAppDbContextFactory
{
    Task<IAppDbContext> CreateAsync(CancellationToken ct = default);
}

/// <summary>The signed-in user, for plain requests and Blazor circuits alike.</summary>
public interface ICurrentUser
{
    bool IsAuthenticated { get; }

    /// <summary>The stable id (the Windows SID, or the dev sign-in's id); "system" when no one is signed in.</summary>
    string UserId { get; }
    string DisplayName { get; }
    string? UserPrincipalName { get; }
    string? Email { get; }
    IReadOnlySet<AppRole> Roles { get; }
    IReadOnlySet<Permission> Permissions { get; }
    bool Has(Permission permission);
}

/// <summary>
/// An in-memory view of the AD-group→role mappings, used at sign-in to resolve a user's roles and permissions without a
/// query per request. Rebuilt on startup and whenever the mappings change (<see cref="Invalidate"/>).
/// </summary>
public interface IRoleDirectory
{
    /// <summary>The union of permissions granted by the named roles (unknown names ignored).</summary>
    IReadOnlySet<Permission> PermissionsForRoles(IEnumerable<string> roleNames);

    /// <summary>Whether <paramref name="name"/> is a TIDE role, as opposed to, say, an AD group SID carried as a role
    /// claim.</summary>
    bool IsRole(string name);

    /// <summary>The role names granted to a user in the given AD security groups.</summary>
    IReadOnlySet<string> RolesForGroups(IEnumerable<string> adGroups);

    /// <summary>The roles some mapping by group name (not by SID) grants: those a group whose name didn't come back in
    /// time could still be giving a user.</summary>
    IReadOnlySet<string> RolesMappedByName { get; }

    /// <summary>Rebuilds the cached snapshot from the database (call after any mapping change).</summary>
    void Invalidate();

    /// <summary>False while the last reload failed (and is being retried), so the snapshot still holds the mappings from
    /// before the latest change.</summary>
    bool IsCurrent { get; }
}

/// <summary>Mirrors signed-in users into <see cref="AppUser"/> so ids resolve to names.</summary>
public interface IUserDirectory
{
    /// <summary>Records a sign-in. Throttled: a user seen in the last while isn't written again. A
    /// <paramref name="nameIsStandIn"/> name (the bare username, when the directory didn't answer in time) keeps the one
    /// recorded, and is written only for someone with none yet.</summary>
    Task TouchAsync(string userId, string displayName, string? upn, string? email, string rolesCsv, bool nameIsStandIn = false, CancellationToken ct = default);

    /// <summary>Everyone seen, by name: the analyst picker on a brief.</summary>
    Task<IReadOnlyList<AppUser>> ListAsync(CancellationToken ct = default);

    /// <summary>The name recorded for <paramref name="userId"/>, or null when there's none: for a signed-in user whose
    /// directory name is only a stand-in this time (the bare username), so records keep their real name. Cheap: kept in
    /// memory once read.</summary>
    string? RecordedName(string userId) => null;
}

/// <summary>Where uploaded files' bytes are kept (on disk, outside the web root).</summary>
public interface IFileStore
{
    Task WriteAsync(Guid id, Stream content, CancellationToken ct = default);

    /// <summary>The file's bytes, or null when they're missing.</summary>
    Stream? OpenRead(Guid id);

    void Delete(Guid id);
}
