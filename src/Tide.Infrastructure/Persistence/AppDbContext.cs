using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Tide.Application.Abstractions;
using Tide.Domain.Entities;

namespace Tide.Infrastructure.Persistence;

/// <summary>
/// TIDE's database. One model for both providers: SQLite in development and tests, SQL Server in production. The
/// provider-specific parts (how times are stored) are decided here, and each provider has its own migrations set.
/// </summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options), IAppDbContext
{
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<UserPreference> UserPreferences => Set<UserPreference>();
    public DbSet<AdGroupRoleMapping> RoleMappings => Set<AdGroupRoleMapping>();
    public DbSet<AuditLogEntry> AuditLog => Set<AuditLogEntry>();
    public DbSet<AppSetting> Settings => Set<AppSetting>();
    public DbSet<StoredFile> Files => Set<StoredFile>();
    public DbSet<ScorecardCategory> ScorecardCategories => Set<ScorecardCategory>();
    public DbSet<ScorecardTag> ScorecardTags => Set<ScorecardTag>();
    public DbSet<Article> Articles => Set<Article>();
    public DbSet<ArticleBullet> ArticleBullets => Set<ArticleBullet>();
    public DbSet<ArticleScreenshot> ArticleScreenshots => Set<ArticleScreenshot>();
    public DbSet<ArticleTag> ArticleTags => Set<ArticleTag>();
    public DbSet<StackProduct> StackProducts => Set<StackProduct>();
    public DbSet<ArticleProduct> ArticleProducts => Set<ArticleProduct>();
    public DbSet<AiDraft> AiDrafts => Set<AiDraft>();
    public DbSet<ApiToken> ApiTokens => Set<ApiToken>();
    public DbSet<Adversary> Adversaries => Set<Adversary>();
    public DbSet<ArticleAdversary> ArticleAdversaries => Set<ArticleAdversary>();
    public DbSet<DetectionRule> DetectionRules => Set<DetectionRule>();
    public DbSet<DetectionGap> DetectionGaps => Set<DetectionGap>();
    public DbSet<ArticleReview> ArticleReviews => Set<ArticleReview>();
    public DbSet<ReviewComment> ReviewComments => Set<ReviewComment>();
    public DbSet<Webhook> Webhooks => Set<Webhook>();
    public DbSet<ArticleTechnique> ArticleTechniques => Set<ArticleTechnique>();
    public DbSet<ArticleIndicator> ArticleIndicators => Set<ArticleIndicator>();
    public DbSet<ArticleCve> ArticleCves => Set<ArticleCve>();
    public DbSet<ArticleLink> ArticleLinks => Set<ArticleLink>();
    public DbSet<Brief> Briefs => Set<Brief>();
    public DbSet<BriefItem> BriefItems => Set<BriefItem>();
    public DbSet<BriefSend> BriefSends => Set<BriefSend>();
    public DbSet<MeetingAttendee> MeetingAttendees => Set<MeetingAttendee>();
    public DbSet<MeetingNote> MeetingNotes => Set<MeetingNote>();
    public DbSet<BriefSendImage> BriefSendImages => Set<BriefSendImage>();
    public DbSet<BriefCorrection> BriefCorrections => Set<BriefCorrection>();
    public DbSet<MetricGroup> MetricGroups => Set<MetricGroup>();
    public DbSet<MetricDefinition> MetricDefinitions => Set<MetricDefinition>();
    public DbSet<MetricValue> MetricValues => Set<MetricValue>();
    public DbSet<MetricImport> MetricImports => Set<MetricImport>();
    public DbSet<Team> Teams => Set<Team>();
    public DbSet<TeamMember> TeamMembers => Set<TeamMember>();
    public DbSet<ActionItem> Actions => Set<ActionItem>();
    public DbSet<ActionComment> ActionComments => Set<ActionComment>();
    public DbSet<ActionEvent> ActionEvents => Set<ActionEvent>();
    public DbSet<ActionCommentFile> ActionCommentFiles => Set<ActionCommentFile>();
    public DbSet<ActionFile> ActionFiles => Set<ActionFile>();
    public DbSet<ActionChecklistItem> ActionChecklistItems => Set<ActionChecklistItem>();
    public DbSet<Playbook> Playbooks => Set<Playbook>();
    public DbSet<PlaybookStep> PlaybookSteps => Set<PlaybookStep>();
    public DbSet<ActionCardRule> ActionCardRules => Set<ActionCardRule>();
    public DbSet<HuntQuery> HuntQueries => Set<HuntQuery>();
    public DbSet<QueryTemplate> QueryTemplates => Set<QueryTemplate>();
    public DbSet<SavedQuery> SavedQueries => Set<SavedQuery>();
    public DbSet<ActionIndicator> ActionIndicators => Set<ActionIndicator>();
    public DbSet<OutreachTemplate> OutreachTemplates => Set<OutreachTemplate>();
    public DbSet<OutreachMessage> OutreachMessages => Set<OutreachMessage>();
    public DbSet<NotificationMark> NotificationMarks => Set<NotificationMark>();
    public DbSet<Source> Sources => Set<Source>();
    public DbSet<FeedItem> FeedItems => Set<FeedItem>();
    public DbSet<FeedItemText> FeedItemTexts => Set<FeedItemText>();
    public DbSet<TriageSplit> TriageSplits => Set<TriageSplit>();
    public DbSet<Story> Stories => Set<Story>();
    public DbSet<CveRecord> CveRecords => Set<CveRecord>();
    public DbSet<RotationMember> RotationMembers => Set<RotationMember>();
    public DbSet<RotationWeek> RotationWeeks => Set<RotationWeek>();
    public DbSet<RotationChange> RotationChanges => Set<RotationChange>();
    public DbSet<RotationOverride> RotationOverrides => Set<RotationOverride>();
    public DbSet<Holiday> Holidays => Set<Holiday>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<AppUser>(e =>
        {
            e.Property(x => x.Sid).HasMaxLength(256);
            e.HasIndex(x => x.Sid).IsUnique();
            e.Property(x => x.UserPrincipalName).HasMaxLength(256);
            e.Property(x => x.DisplayName).HasMaxLength(256);
            e.Property(x => x.Email).HasMaxLength(256);
            e.Property(x => x.RolesCsv).HasMaxLength(500);
        });

        b.Entity<UserPreference>(e =>
        {
            e.HasKey(x => new { x.UserId, x.Name });
            e.Property(x => x.UserId).HasMaxLength(256);
            e.Property(x => x.Name).HasMaxLength(64);
            e.Property(x => x.Value).HasMaxLength(200);
        });

        b.Entity<AdGroupRoleMapping>(e =>
        {
            e.Property(x => x.AdGroup).HasMaxLength(256);
            e.Property(x => x.RoleName).HasMaxLength(64);
            e.Property(x => x.UpdatedBy).HasMaxLength(256);
            e.HasIndex(x => new { x.AdGroup, x.RoleName }).IsUnique();
        });

        b.Entity<AuditLogEntry>(e =>
        {
            e.Property(x => x.UserId).HasMaxLength(256);
            e.Property(x => x.UserName).HasMaxLength(256);
            e.Property(x => x.Action).HasMaxLength(16);
            e.Property(x => x.EntityType).HasMaxLength(64);
            e.Property(x => x.EntityKey).HasMaxLength(100);
            e.HasIndex(x => x.AtUtc);
            e.HasIndex(x => new { x.EntityType, x.EntityKey });
            e.HasIndex(x => new { x.UserId, x.AtUtc });
        });

        b.Entity<AppSetting>(e =>
        {
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(200);
            e.Property(x => x.Value).HasMaxLength(4000);
            e.Property(x => x.UpdatedBy).HasMaxLength(256);
        });

        b.Entity<StoredFile>(e =>
        {
            e.Property(x => x.FileName).HasMaxLength(260);
            e.Property(x => x.ContentType).HasMaxLength(100);
            e.Property(x => x.Sha256).HasMaxLength(64);
            Audited(e);
        });

        b.Entity<ScorecardCategory>(e =>
        {
            e.Property(x => x.Key).HasMaxLength(64);
            e.HasIndex(x => x.Key).IsUnique();
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Description).HasMaxLength(500);
            e.HasMany(x => x.Tags).WithOne(x => x.Category).HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
            Audited(e);
        });

        b.Entity<ScorecardTag>(e =>
        {
            e.Property(x => x.Key).HasMaxLength(64);
            e.HasIndex(x => x.Key).IsUnique();
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Description).HasMaxLength(500);
            Audited(e);
        });

        b.Entity<Article>(e =>
        {
            e.Property(x => x.Title).HasMaxLength(300);
            e.Property(x => x.SourceName).HasMaxLength(200);
            e.Property(x => x.SourceUrl).HasMaxLength(2000);
            e.Property(x => x.Bluf).HasMaxLength(600);
            e.Property(x => x.Actors).HasMaxLength(500);
            e.Property(x => x.Sectors).HasMaxLength(500);
            Person(e, x => x.AuthorId, x => x.AuthorName);
            e.Property(x => x.EditVersion).IsConcurrencyToken();
            e.HasIndex(x => x.CreatedAtUtc);
            e.HasIndex(x => x.Status);
            e.HasMany(x => x.Bullets).WithOne().HasForeignKey(x => x.ArticleId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Screenshots).WithOne().HasForeignKey(x => x.ArticleId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Tags).WithOne().HasForeignKey(x => x.ArticleId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Techniques).WithOne().HasForeignKey(x => x.ArticleId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Indicators).WithOne().HasForeignKey(x => x.ArticleId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Cves).WithOne().HasForeignKey(x => x.ArticleId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Story>().WithMany().HasForeignKey(x => x.StoryId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(x => x.StoryId);
            Audited(e);
        });

        b.Entity<ArticleBullet>(e =>
        {
            e.Property(x => x.Text).HasMaxLength(2000);
            e.HasIndex(x => new { x.ArticleId, x.SortOrder });
        });

        b.Entity<ArticleScreenshot>(e =>
        {
            e.Property(x => x.Caption).HasMaxLength(500);
            e.HasOne(x => x.File).WithMany().HasForeignKey(x => x.FileId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.FileId).IsUnique();
        });

        b.Entity<ArticleTag>(e =>
        {
            e.HasKey(x => new { x.ArticleId, x.TagId });
            e.HasOne(x => x.Tag).WithMany().HasForeignKey(x => x.TagId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.TagId);
        });

        b.Entity<StackProduct>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(StackProduct.MaxName);
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.OtherNames).HasMaxLength(StackProduct.MaxOtherNames);
            e.Property(x => x.Versions).HasMaxLength(StackProduct.MaxVersions);
            e.Property(x => x.Notes).HasMaxLength(StackProduct.MaxNotes);
            e.Property(x => x.ContactName).HasMaxLength(StackProduct.MaxContactName);
            e.Property(x => x.ContactEmail).HasMaxLength(StackProduct.MaxContactEmail);
            e.HasOne(x => x.Team).WithMany().HasForeignKey(x => x.TeamId).OnDelete(DeleteBehavior.SetNull);
            Audited(e);
        });

        b.Entity<ArticleProduct>(e =>
        {
            e.HasKey(x => new { x.ArticleId, x.ProductId });
            e.HasOne<Article>().WithMany(a => a.Products).HasForeignKey(x => x.ArticleId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.ProductId);
        });

        b.Entity<Adversary>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(Adversary.MaxName);
            e.HasIndex(x => x.Name);
            e.Property(x => x.Aliases).HasMaxLength(Adversary.MaxAliases);
            e.Property(x => x.MitreId).HasMaxLength(16);
            e.HasIndex(x => x.MitreId);
            e.Property(x => x.Motivation).HasMaxLength(Adversary.MaxMotivation);
            e.Property(x => x.Sectors).HasMaxLength(Adversary.MaxSectors);
            e.Property(x => x.Notes).HasMaxLength(Adversary.MaxNotes);
            Audited(e);
        });

        b.Entity<ArticleAdversary>(e =>
        {
            e.HasKey(x => new { x.ArticleId, x.AdversaryId });
            e.HasOne<Article>().WithMany().HasForeignKey(x => x.ArticleId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Adversary).WithMany().HasForeignKey(x => x.AdversaryId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.AdversaryId);
        });

        b.Entity<DetectionRule>(e =>
        {
            e.Property(x => x.ExternalId).HasMaxLength(DetectionRule.MaxExternalId);
            e.HasIndex(x => new { x.Source, x.ExternalId }).IsUnique();
            e.Property(x => x.Name).HasMaxLength(DetectionRule.MaxName);
            e.Property(x => x.Techniques).HasMaxLength(DetectionRule.MaxTechniques);
            e.Property(x => x.Platform).HasMaxLength(DetectionRule.MaxPlatform);
        });

        b.Entity<ArticleReview>(e =>
        {
            OfArticle(e, x => x.ArticleId);
            e.Property(x => x.RequestedById).HasMaxLength(256);
            e.Property(x => x.RequestedByName).HasMaxLength(200);
            e.Property(x => x.ReviewerId).HasMaxLength(256);
            e.HasIndex(x => new { x.ReviewerId, x.Status });
            e.Property(x => x.ReviewerName).HasMaxLength(200);
            e.Property(x => x.Note).HasMaxLength(ArticleReview.MaxNote);
            e.Property(x => x.Checks).HasMaxLength(ArticleReview.MaxChecks);
            e.HasMany(x => x.Comments).WithOne().HasForeignKey(c => c.ReviewId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ReviewComment>(e =>
        {
            e.Property(x => x.Field).HasMaxLength(ReviewComment.MaxField);
            e.Property(x => x.Quote).HasMaxLength(ReviewComment.MaxQuote);
            e.Property(x => x.Text).HasMaxLength(ReviewComment.MaxText);
            e.Property(x => x.AuthorId).HasMaxLength(256);
            e.Property(x => x.AuthorName).HasMaxLength(200);
            e.Property(x => x.ResolvedByName).HasMaxLength(200);
        });

        b.Entity<DetectionGap>(e =>
        {
            e.HasKey(x => x.TechniqueId);
            e.Property(x => x.TechniqueId).HasMaxLength(16);
            e.HasOne<ActionItem>().WithMany().HasForeignKey(x => x.ActionId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.ActionId);
        });

        b.Entity<ApiToken>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(ApiToken.MaxName);
            e.Property(x => x.Hash).HasMaxLength(64);
            e.HasIndex(x => x.Hash).IsUnique();
            e.Property(x => x.Hint).HasMaxLength(8);
            e.Property(x => x.Scopes).HasMaxLength(400);
            Audited(e);
        });

        b.Entity<Webhook>(e =>
        {
            e.Property(x => x.Url).HasMaxLength(2000);
            e.Property(x => x.Events).HasMaxLength(200);
            e.Property(x => x.Secret).HasMaxLength(1000);
            e.Property(x => x.LastError).HasMaxLength(500);
            Audited(e);
        });

        b.Entity<AiDraft>(e =>
        {
            OfArticle(e, x => x.ArticleId);
            e.Property(x => x.Provider).HasMaxLength(40);
            e.Property(x => x.Model).HasMaxLength(100);
            e.Property(x => x.InputHash).HasMaxLength(64);
            e.Property(x => x.BasedOn).HasMaxLength(AiDraft.MaxBasedOn);
            e.Property(x => x.Suggestions).HasMaxLength(AiDraft.MaxSuggestions);
            Audited(e);
        });

        b.Entity<ArticleTechnique>(e =>
        {
            e.HasKey(x => new { x.ArticleId, x.TechniqueId });
            e.Property(x => x.TechniqueId).HasMaxLength(16);
            e.HasIndex(x => x.TechniqueId);
        });

        b.Entity<ArticleIndicator>(e =>
        {
            e.Property(x => x.Value).HasMaxLength(2000);
            e.Property(x => x.Note).HasMaxLength(300);
            e.HasIndex(x => x.ArticleId);
        });

        b.Entity<ArticleLink>(e =>
        {
            OfArticle(e, x => x.ArticleId);
            e.Property(x => x.Url).HasMaxLength(2000);
            e.Property(x => x.Title).HasMaxLength(500);
            e.Property(x => x.SourceName).HasMaxLength(200);
            e.Property(x => x.AddedByName).HasMaxLength(256);
        });

        b.Entity<ArticleCve>(e =>
        {
            e.HasKey(x => new { x.ArticleId, x.CveId });
            e.Property(x => x.CveId).HasMaxLength(24);
            e.HasIndex(x => x.CveId);
        });

        b.Entity<Brief>(e =>
        {
            e.HasIndex(x => x.BriefDate).IsUnique();
            Person(e, x => x.AnalystId, x => x.AnalystName);
            e.Property(x => x.Intro).HasMaxLength(2000);
            e.Property(x => x.TeamNote).HasMaxLength(Tide.Application.Briefs.BriefService.TeamNoteMax);
            Person(e, x => x.DistributedById, x => x.DistributedByName);
            Person(e, x => x.NotSentById, x => x.NotSentByName);
            e.Property(x => x.NotSentReason).HasMaxLength(Tide.Application.Distribution.DistributionService.NotSentReasonMax);
            Person(e, x => x.SharePointPostedById, x => x.SharePointPostedByName);
            e.Property(x => x.EditVersion).IsConcurrencyToken();
            e.HasMany(x => x.Items).WithOne().HasForeignKey(x => x.BriefId).OnDelete(DeleteBehavior.Cascade);
            Audited(e);
        });

        b.Entity<BriefItem>(e =>
        {
            e.HasOne(x => x.Article).WithMany().HasForeignKey(x => x.ArticleId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.BriefId, x.ArticleId }).IsUnique();
            e.HasIndex(x => x.ArticleId);
            e.HasIndex(x => x.CarriedFromBriefId);
        });

        b.Entity<MeetingAttendee>(e =>
        {
            e.HasOne<Brief>().WithMany().HasForeignKey(x => x.BriefId).OnDelete(DeleteBehavior.Cascade);
            Person(e, x => x.UserId, x => x.Name);
            e.HasIndex(x => new { x.BriefId, x.UserId, x.Name }).IsUnique();
        });

        b.Entity<MeetingNote>(e =>
        {
            e.HasOne<Brief>().WithMany().HasForeignKey(x => x.BriefId).OnDelete(DeleteBehavior.Cascade);
            Person(e, x => x.AuthorId, x => x.AuthorName);
            e.Property(x => x.Text).HasMaxLength(8000);   // a note written now is at most 2,000; one carried over from the old single notes, up to 8,000
            e.HasIndex(x => new { x.BriefId, x.AtUtc });
        });

        b.Entity<BriefSend>(e =>
        {
            e.HasOne<Brief>().WithMany().HasForeignKey(x => x.BriefId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Subject).HasMaxLength(300);
            e.Property(x => x.Recipients).HasMaxLength(8000);
            Person(e, x => x.SentById, x => x.SentByName);
            e.Property(x => x.Error).HasMaxLength(1000);
            e.HasMany(x => x.Images).WithOne().HasForeignKey(x => x.SendId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.BriefId, x.SentAtUtc });
            e.HasIndex(x => new { x.Edition, x.IsTest, x.Status, x.BriefDate });
        });

        b.Entity<BriefSendImage>(e =>
        {
            e.Property(x => x.ContentId).HasMaxLength(100);
            e.Property(x => x.Caption).HasMaxLength(500);
            e.HasOne(x => x.File).WithMany().HasForeignKey(x => x.FileId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.FileId).IsUnique();
            e.HasIndex(x => x.SendId);
        });

        b.Entity<BriefCorrection>(e =>
        {
            e.HasOne(x => x.CorrectsSend).WithMany().HasForeignKey(x => x.CorrectsSendId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Brief>().WithMany().HasForeignKey(x => x.BriefId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Note).HasMaxLength(4000);
            e.Property(x => x.ArticleIds).HasMaxLength(2000);
            e.Property(x => x.Subject).HasMaxLength(320);
            e.Property(x => x.Recipients).HasMaxLength(8000);
            Person(e, x => x.SentById, x => x.SentByName);
            e.Property(x => x.Error).HasMaxLength(1000);
            e.HasIndex(x => new { x.CorrectsSendId, x.Status });
            e.HasIndex(x => new { x.BriefDate, x.Edition });
        });

        b.Entity<MetricGroup>(e =>
        {
            e.Property(x => x.Key).HasMaxLength(64);
            e.HasIndex(x => x.Key).IsUnique();
            e.Property(x => x.Name).HasMaxLength(100);
            e.HasMany(x => x.Metrics).WithOne(x => x.Group).HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Restrict);
            Audited(e);
        });

        b.Entity<MetricDefinition>(e =>
        {
            e.Property(x => x.Key).HasMaxLength(64);
            e.HasIndex(x => x.Key).IsUnique();
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.WarnThreshold).HasPrecision(18, 4);
            e.Property(x => x.CriticalThreshold).HasPrecision(18, 4);
            Audited(e);
        });

        b.Entity<MetricValue>(e =>
        {
            e.HasOne<MetricDefinition>().WithMany().HasForeignKey(x => x.DefinitionId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.DefinitionId, x.BriefDate }).IsUnique();
            e.HasIndex(x => x.BriefDate);
            e.Property(x => x.Value).HasPrecision(18, 4);
            e.Property(x => x.Note).HasMaxLength(300);
            Person(e, x => x.EnteredById, x => x.EnteredByName);
            e.HasOne<MetricImport>().WithMany().HasForeignKey(x => x.ImportId).OnDelete(DeleteBehavior.Restrict);
            Audited(e);
        });

        b.Entity<MetricImport>(e =>
        {
            e.Property(x => x.FileName).HasMaxLength(260);
            e.Property(x => x.ImportedByName).HasMaxLength(256);
            e.Property(x => x.Problems).HasMaxLength(4000);
            e.HasIndex(x => x.ReadAtUtc);
            Audited(e);
        });

        b.Entity<Team>(e =>
        {
            e.Property(x => x.Key).HasMaxLength(64);
            e.HasIndex(x => x.Key).IsUnique();
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Description).HasMaxLength(500);
            e.Property(x => x.Email).HasMaxLength(256);
            e.Property(x => x.ContactName).HasMaxLength(Team.MaxContactName);
            e.Property(x => x.ContactEmail).HasMaxLength(256);
            e.Property(x => x.Engage).HasMaxLength(Team.MaxEngage);
            e.Property(x => x.EscalationName).HasMaxLength(Team.MaxContactName);
            e.Property(x => x.EscalationEmail).HasMaxLength(256);
            e.HasMany(x => x.Members).WithOne().HasForeignKey(x => x.TeamId).OnDelete(DeleteBehavior.Cascade);
            Audited(e);
        });

        b.Entity<TeamMember>(e =>
        {
            e.HasKey(x => new { x.TeamId, x.UserId });
            Person(e, x => x.UserId, x => x.DisplayName);
            e.HasIndex(x => x.UserId);
        });

        b.Entity<ActionItem>(e =>
        {
            e.HasIndex(x => x.Number).IsUnique();
            e.Property(x => x.Title).HasMaxLength(ActionItem.MaxTitle);
            e.Property(x => x.Description).HasMaxLength(8000);
            Person(e, x => x.AssigneeId, x => x.AssigneeName);
            e.Property(x => x.OutcomeNote).HasMaxLength(2000);
            e.Property(x => x.TicketRef).HasMaxLength(100);
            e.Property(x => x.TicketUrl).HasMaxLength(2000);
            e.Property(x => x.EditVersion).IsConcurrencyToken();
            e.HasOne(x => x.Team).WithMany().HasForeignKey(x => x.TeamId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Article).WithMany().HasForeignKey(x => x.ArticleId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ActionItem>().WithMany().HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Comments).WithOne().HasForeignKey(x => x.ActionId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Events).WithOne().HasForeignKey(x => x.ActionId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Hunts).WithOne().HasForeignKey(x => x.ActionId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Checklist).WithOne().HasForeignKey(x => x.ActionId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Indicators).WithOne().HasForeignKey(x => x.ActionId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Outreach).WithOne().HasForeignKey(x => x.ActionId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Files).WithOne().HasForeignKey(x => x.ActionId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.PlaybookName).HasMaxLength(Playbook.MaxName);
            Person(e, x => x.HandedById, x => x.HandedByName);
            e.Property(x => x.HandoffReason).HasMaxLength(ActionItem.MaxHandoffReason);
            e.HasOne<Playbook>().WithMany().HasForeignKey(x => x.PlaybookId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.Status, x.DueDate });
            e.HasIndex(x => x.TeamId);
            e.HasIndex(x => x.AssigneeId);
            e.HasIndex(x => x.ArticleId);
            e.HasIndex(x => x.ParentId);
            // Only flagged actions are in it, so Today's Escalated card and the meeting's list read a handful of rows.
            // Square brackets quote a name in SQL Server and SQLite alike.
            e.HasIndex(x => x.EscalatedForDue).HasFilter("[EscalatedForDue] IS NOT NULL");
            Audited(e);
        });

        b.Entity<ActionComment>(e =>
        {
            e.Property(x => x.Text).HasMaxLength(8000);
            Person(e, x => x.AuthorId, x => x.AuthorName);
            e.Property(x => x.EditedByName).HasMaxLength(256);
            e.Property(x => x.EditVersion).IsConcurrencyToken();
            e.HasIndex(x => new { x.ActionId, x.AtUtc });
            e.HasMany(x => x.Files).WithOne().HasForeignKey(x => x.CommentId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ActionCommentFile>(e =>
        {
            e.Property(x => x.FileName).HasMaxLength(200);
            e.Property(x => x.Description).HasMaxLength(ActionFile.MaxDescription);
            e.Property(x => x.RiskReason).HasMaxLength(200);
            Person(e, x => x.RemovedById, x => x.RemovedByName);
            e.Property(x => x.RemovedReason).HasMaxLength(ActionFile.MaxReason);
            e.HasOne<StoredFile>().WithMany().HasForeignKey(x => x.FileId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.CommentId);
            e.HasIndex(x => x.FileId);
        });

        b.Entity<ActionFile>(e =>
        {
            e.Property(x => x.FileName).HasMaxLength(200);
            e.Property(x => x.Sha256).HasMaxLength(64);
            e.Property(x => x.DeclaredType).HasMaxLength(100);
            e.Property(x => x.TypeNote).HasMaxLength(200);
            e.Property(x => x.RiskReason).HasMaxLength(200);
            e.Property(x => x.Description).HasMaxLength(ActionFile.MaxDescription);
            Person(e, x => x.AddedById, x => x.AddedByName);
            Person(e, x => x.RemovedById, x => x.RemovedByName);
            e.Property(x => x.RemovedReason).HasMaxLength(ActionFile.MaxReason);
            e.Property(x => x.FromCommentBy).HasMaxLength(256);
            e.HasOne<StoredFile>().WithMany().HasForeignKey(x => x.FileId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.ActionId);
            e.HasIndex(x => x.FileId).IsUnique();
        });

        b.Entity<ActionIndicator>(e =>
        {
            e.Property(x => x.Value).HasMaxLength(2000);
            e.Property(x => x.Note).HasMaxLength(300);
            e.Property(x => x.AddedByName).HasMaxLength(256);
            e.HasIndex(x => x.ActionId);
        });

        b.Entity<ActionChecklistItem>(e =>
        {
            e.Property(x => x.Text).HasMaxLength(ActionChecklistItem.MaxText);
            e.Property(x => x.DoneByName).HasMaxLength(256);
            e.HasIndex(x => new { x.ActionId, x.SortOrder });
        });

        b.Entity<Playbook>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(Playbook.MaxName);
            e.Property(x => x.Description).HasMaxLength(Playbook.MaxDescription);
            e.Property(x => x.CreatedByName).HasMaxLength(256);
            e.Property(x => x.ModifiedByName).HasMaxLength(256);
            e.Property(x => x.EditVersion).IsConcurrencyToken();
            e.HasMany(x => x.Steps).WithOne().HasForeignKey(x => x.PlaybookId).OnDelete(DeleteBehavior.Cascade);
            Audited(e);
        });

        b.Entity<PlaybookStep>(e =>
        {
            e.Property(x => x.Text).HasMaxLength(ActionChecklistItem.MaxText);
            e.HasIndex(x => new { x.PlaybookId, x.Order });
        });

        b.Entity<ActionCardRule>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(60);
            e.HasOne<Team>().WithMany().HasForeignKey(x => x.TeamId).OnDelete(DeleteBehavior.Restrict);
            Audited(e);
        });

        b.Entity<ActionEvent>(e =>
        {
            e.Property(x => x.ByName).HasMaxLength(256);
            e.Property(x => x.Text).HasMaxLength(1000);
            e.HasIndex(x => new { x.ActionId, x.AtUtc });
        });

        b.Entity<HuntQuery>(e =>
        {
            e.Property(x => x.Dialect).HasMaxLength(16);
            e.Property(x => x.GeneratedText).HasMaxLength(HuntQuery.MaxText);
            e.Property(x => x.Text).HasMaxLength(HuntQuery.MaxText);
            e.Property(x => x.CreatedByName).HasMaxLength(256);
            e.Property(x => x.Notes).HasMaxLength(2000);
            Person(e, x => x.RunById, x => x.RunByName);
            e.HasOne<StoredFile>().WithMany().HasForeignKey(x => x.EvidenceFileId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.ActionId);
            e.HasIndex(x => x.EvidenceFileId);
            e.Property(x => x.SavedQueryName).HasMaxLength(SavedQuery.MaxName);
            // A saved query can be deleted; the hunts added from it keep its name.
            e.HasOne<SavedQuery>().WithMany().HasForeignKey(x => x.SavedQueryId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(x => x.SavedQueryId);
        });

        b.Entity<SavedQuery>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(SavedQuery.MaxName);
            e.Property(x => x.Dialect).HasMaxLength(16);
            e.HasIndex(x => new { x.Dialect, x.Name }).IsUnique();
            e.Property(x => x.Text).HasMaxLength(SavedQuery.MaxText);
            e.Property(x => x.Description).HasMaxLength(SavedQuery.MaxDescription);
            e.Property(x => x.CreatedByName).HasMaxLength(256);
            e.Property(x => x.ModifiedByName).HasMaxLength(256);
            e.Property(x => x.EditVersion).IsConcurrencyToken();
            Audited(e);
        });

        b.Entity<OutreachTemplate>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(OutreachTemplate.MaxName);
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.Description).HasMaxLength(OutreachTemplate.MaxDescription);
            e.Property(x => x.Subject).HasMaxLength(OutreachTemplate.MaxSubject);
            e.Property(x => x.Body).HasMaxLength(OutreachTemplate.MaxBody);
            e.Property(x => x.To).HasMaxLength(OutreachTemplate.MaxTo);
            e.Property(x => x.CreatedByName).HasMaxLength(256);
            e.Property(x => x.ModifiedByName).HasMaxLength(256);
            e.Property(x => x.EditVersion).IsConcurrencyToken();
            Audited(e);
        });

        b.Entity<OutreachMessage>(e =>
        {
            e.Property(x => x.TemplateName).HasMaxLength(OutreachTemplate.MaxName);
            // A template can be deleted; the emails written from it keep its name.
            e.HasOne<OutreachTemplate>().WithMany().HasForeignKey(x => x.TemplateId).OnDelete(DeleteBehavior.SetNull);
            e.Property(x => x.To).HasMaxLength(OutreachMessage.MaxTo);
            e.Property(x => x.Subject).HasMaxLength(OutreachMessage.MaxSubject);
            e.Property(x => x.Body).HasMaxLength(OutreachMessage.MaxBody);
            e.Property(x => x.SentById).HasMaxLength(256);
            e.Property(x => x.SentByName).HasMaxLength(256);
            e.Property(x => x.ReplyRecordedByName).HasMaxLength(256);
            e.Property(x => x.ReplyNotes).HasMaxLength(OutreachMessage.MaxNotes);
            // The reply's file is an ActionFile on the same action, and those are never deleted while the action stands
            // (removing one keeps its row), so it's held by id alone: a second cascade path to the action would be
            // refused by SQL Server.
            e.HasIndex(x => x.ActionId);
            e.HasIndex(x => x.TemplateId);
            // The follow-up scan reads those still waiting.
            e.HasIndex(x => x.RepliedAtUtc);
        });

        b.Entity<QueryTemplate>(e =>
        {
            e.HasKey(x => new { x.Dialect, x.Kind });
            e.Property(x => x.Dialect).HasMaxLength(16);
            e.Property(x => x.Template).HasMaxLength(4000);
            e.Property(x => x.UpdatedBy).HasMaxLength(256);
        });

        b.Entity<NotificationMark>(e =>
        {
            e.HasKey(x => new { x.Kind, x.Key });
            e.Property(x => x.Kind).HasMaxLength(32);
            e.Property(x => x.Key).HasMaxLength(200);
            e.HasIndex(x => x.SentAtUtc);
        });

        b.Entity<Source>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(100);
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.Url).HasMaxLength(2000);
            e.Property(x => x.LicenseNote).HasMaxLength(500);
            e.Property(x => x.OnlyCategories).HasMaxLength(2000);
            e.Property(x => x.SkipCategories).HasMaxLength(2000);
            e.Property(x => x.SeenCategories).HasMaxLength(2000);
            e.Property(x => x.OnlyTitleWords).HasMaxLength(2000);
            e.Property(x => x.SkipTitleWords).HasMaxLength(2000);
            e.Property(x => x.ArticleBodySelector).HasMaxLength(Source.MaxBodySelector);
            e.Property(x => x.ArticleRemoveSelector).HasMaxLength(Source.MaxRemoveSelector);
            e.Property(x => x.LastError).HasMaxLength(500);
            e.Property(x => x.ETag).HasMaxLength(200);
            e.Property(x => x.LastModified).HasMaxLength(100);
            Audited(e);
        });

        b.Entity<FeedItemText>(e =>
        {
            e.HasKey(x => x.FeedItemId);
            e.HasOne<FeedItem>().WithOne().HasForeignKey<FeedItemText>(x => x.FeedItemId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Text).HasMaxLength(Application.Assist.ArticleText.MaxChars);
            e.Property(x => x.How).HasMaxLength(FeedItemText.MaxHow);
            e.HasIndex(x => x.FetchedAtUtc);
        });

        b.Entity<FeedItem>(e =>
        {
            e.HasOne(x => x.Source).WithMany().HasForeignKey(x => x.SourceId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Url).HasMaxLength(2000);
            e.Property(x => x.UrlHash).HasMaxLength(64);
            e.HasIndex(x => x.UrlHash).IsUnique();
            e.Property(x => x.Title).HasMaxLength(500);
            e.Property(x => x.TitleKey).HasMaxLength(500);
            e.Property(x => x.Excerpt).HasMaxLength(FeedItem.MaxExcerpt);
            e.Property(x => x.Cves).HasMaxLength(1000);
            e.Property(x => x.DismissReason).HasMaxLength(200);
            e.Property(x => x.StatusByName).HasMaxLength(256);
            e.HasOne<Article>().WithMany().HasForeignKey(x => x.ArticleId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(x => new { x.Status, x.FetchedAtUtc });
            e.HasIndex(x => x.FetchedAtUtc);
            e.HasIndex(x => x.SourceId);
            e.HasIndex(x => x.ArticleId);
        });

        b.Entity<TriageSplit>(e =>
        {
            e.HasOne<FeedItem>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.Batch, x.ItemId }).IsUnique();
            e.HasIndex(x => x.ItemId);
            e.Property(x => x.SplitBy).HasMaxLength(TriageSplit.MaxName);
            e.Property(x => x.SplitByName).HasMaxLength(TriageSplit.MaxName);
        });

        b.Entity<Story>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Summary).HasMaxLength(4000);
            e.HasIndex(x => x.Status);
            Audited(e);
        });

        b.Entity<CveRecord>(e =>
        {
            e.HasKey(x => x.CveId);
            e.Property(x => x.CveId).HasMaxLength(24);
            e.Property(x => x.Description).HasMaxLength(2000);
            e.Property(x => x.CvssScore).HasPrecision(4, 1);
            e.Property(x => x.CvssSeverity).HasMaxLength(16);
            e.Property(x => x.CvssVector).HasMaxLength(200);
            e.Property(x => x.EpssScore).HasPrecision(8, 5);
            e.Property(x => x.EpssPercentile).HasPrecision(8, 5);
            e.Property(x => x.Vendor).HasMaxLength(200);
            e.Property(x => x.Product).HasMaxLength(200);
            e.HasIndex(x => x.IsKev);
        });

        b.Entity<RotationMember>(e =>
        {
            e.HasKey(x => x.UserId);
            Person(e, x => x.UserId, x => x.DisplayName);
        });

        b.Entity<RotationWeek>(e =>
        {
            e.HasKey(x => x.WeekStart);
            Person(e, x => x.AnalystId, x => x.AnalystName);
            e.Property(x => x.UpdatedBy).HasMaxLength(256);
            e.HasIndex(x => x.AnalystId);
        });

        b.Entity<RotationChange>(e =>
        {
            Person(e, x => x.RequesterId, x => x.RequesterName);
            Person(e, x => x.PartnerId, x => x.PartnerName);
            Person(e, x => x.CreatedById, x => x.CreatedByName);
            e.Property(x => x.DecidedByName).HasMaxLength(256);
            e.Property(x => x.Note).HasMaxLength(500);
            e.Property(x => x.DecisionNote).HasMaxLength(500);
            e.HasIndex(x => new { x.Status, x.PartnerId });
            e.HasIndex(x => x.RequesterId);
        });

        b.Entity<RotationOverride>(e =>
        {
            e.HasKey(x => x.Date);
            Person(e, x => x.AnalystId, x => x.AnalystName);
            e.HasOne<RotationChange>().WithMany().HasForeignKey(x => x.ChangeId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.AnalystId);
            e.HasIndex(x => x.ChangeId);
        });

        b.Entity<Holiday>(e =>
        {
            e.HasKey(x => x.Date);
            e.Property(x => x.Name).HasMaxLength(Holiday.MaxName);
        });

        // Ids are made in code (Entity sets a new Guid), never by the database. Left as generated, EF would read a new
        // child (its key already set) reached through a tracked parent as an existing row and update it.
        foreach (var entityType in b.Model.GetEntityTypes())
            foreach (var key in entityType.GetKeys())
                foreach (var property in key.Properties.Where(p => p.ClrType == typeof(Guid)))
                    property.ValueGenerated = ValueGenerated.Never;

        // SQLite can't order by or compare DateTimeOffset. Store it as UTC ticks there so sorting and range filters
        // work; SQL Server keeps its native datetimeoffset.
        if (Database.IsSqlite())
        {
            var toTicks = new ValueConverter<DateTimeOffset, long>(v => v.UtcTicks, v => new DateTimeOffset(v, TimeSpan.Zero));
            var toTicksNullable = new ValueConverter<DateTimeOffset?, long?>(
                v => v.HasValue ? v.Value.UtcTicks : null,
                v => v.HasValue ? new DateTimeOffset(v.Value, TimeSpan.Zero) : null);
            foreach (var entityType in b.Model.GetEntityTypes())
                foreach (var property in entityType.GetProperties())
                {
                    if (property.ClrType == typeof(DateTimeOffset)) property.SetValueConverter(toTicks);
                    else if (property.ClrType == typeof(DateTimeOffset?)) property.SetValueConverter(toTicksNullable);
                }
        }
    }

    private static void Audited<T>(EntityTypeBuilder<T> e) where T : Domain.Common.AuditableEntity
    {
        e.Property(x => x.CreatedBy).HasMaxLength(256);
        e.Property(x => x.ModifiedBy).HasMaxLength(256);
    }

    // A person as a row records them: their stable id and the display name they had then.
    private static void Person<T>(EntityTypeBuilder<T> e, Expression<Func<T, string?>> id, Expression<Func<T, string?>> name) where T : class
    {
        e.Property(id).HasMaxLength(256);
        e.Property(name).HasMaxLength(256);
    }

    // A row belonging to one article: deleted with it, and looked up by it.
    private static void OfArticle<T>(EntityTypeBuilder<T> e, Expression<Func<T, object?>> articleId) where T : class
    {
        e.HasOne<Article>().WithMany().HasForeignKey(articleId).OnDelete(DeleteBehavior.Cascade);
        e.HasIndex(articleId);
    }
}
