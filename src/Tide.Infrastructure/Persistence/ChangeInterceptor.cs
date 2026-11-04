using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Tide.Application.Abstractions;
using Tide.Domain.Entities;

namespace Tide.Infrastructure.Persistence;

/// <summary>
/// Tells the <see cref="ChangeFeed"/> what a save changed once it has committed, so a colleague's open brief, article,
/// action or numbers form refreshes whichever service made the change. The topics are read before the save (a deleted row is gone from
/// the tracker after it) and published only if it succeeds. A save inside a transaction the caller opened is published
/// when that transaction commits, and forgotten if it rolls back (no service opens one today; this keeps it true).
/// </summary>
public sealed class ChangeInterceptor(ChangeFeed feed, ICurrentUser user, ChangeOrigin origin) : SaveChangesInterceptor, IDbTransactionInterceptor
{
    // A scope can save on more than one context at once (overlapping handlers), so the topics are kept per context.
    private readonly ConditionalWeakTable<DbContext, HashSet<string>> _pending = [];

    // Topics of saves made inside a caller's transaction, until it commits or rolls back.
    private readonly ConditionalWeakTable<DbContext, HashSet<string>> _held = [];

    public void TransactionCommitted(System.Data.Common.DbTransaction transaction, TransactionEndEventData eventData) => Release(eventData.Context);

    public Task TransactionCommittedAsync(System.Data.Common.DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Release(eventData.Context);
        return Task.CompletedTask;
    }

    public void TransactionRolledBack(System.Data.Common.DbTransaction transaction, TransactionEndEventData eventData) => Drop(eventData.Context);

    public Task TransactionRolledBackAsync(System.Data.Common.DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Drop(eventData.Context);
        return Task.CompletedTask;
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Collect(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Collect(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        Publish(eventData.Context);
        return result;
    }

    public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
        CancellationToken cancellationToken = default)
    {
        Publish(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData) => Forget(eventData.Context);

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Forget(eventData.Context);
        return Task.CompletedTask;
    }

    /// <summary>What a changed row means to an open page; nothing for rows no page follows.</summary>
    public static IEnumerable<string> TopicsOf(object entity) => entity switch
    {
        Article a => [ChangeFeed.Article(a.Id), ChangeFeed.Articles],   // every save, not only a title or status: the lists debounce
        ArticleBullet x => [ChangeFeed.Article(x.ArticleId)],
        ArticleScreenshot x => [ChangeFeed.Article(x.ArticleId)],
        ArticleTag x => [ChangeFeed.Article(x.ArticleId)],
        ArticleTechnique x => [ChangeFeed.Article(x.ArticleId)],
        ArticleIndicator x => [ChangeFeed.Article(x.ArticleId)],
        ArticleLink x => [ChangeFeed.Article(x.ArticleId)],
        ArticleCve x => [ChangeFeed.Article(x.ArticleId)],
        ArticleAdversary x => [ChangeFeed.ArticleTags(x.ArticleId)],     // only the Related tab's cards: not the editor's
        ArticleProduct x => [ChangeFeed.ArticleTags(x.ArticleId)],
        Brief b => [ChangeFeed.Brief(b.Id), ChangeFeed.BriefDay(b.BriefDate)],
        BriefItem x => [ChangeFeed.Brief(x.BriefId)],
        BriefSend x => [ChangeFeed.Brief(x.BriefId)],
        BriefCorrection x => [ChangeFeed.Brief(x.BriefId)],
        MeetingAttendee x => [ChangeFeed.Meeting(x.BriefId)],   // not the brief's own topic: a join or a note isn't an edit to it
        MeetingNote x => [ChangeFeed.Meeting(x.BriefId)],
        ActionItem a => [ChangeFeed.Action(a.Id), ChangeFeed.Actions],
        ActionComment x => [ChangeFeed.Action(x.ActionId)],
        HuntQuery x => [ChangeFeed.Action(x.ActionId)],
        RotationChange or RotationOverride or RotationWeek or RotationMember => [ChangeFeed.Rotation],
        Story => [ChangeFeed.Stories],
        ActionChecklistItem x => [ChangeFeed.Action(x.ActionId)],
        ActionEvent x => [ChangeFeed.Action(x.ActionId)],
        // A comment's files are added and removed only with the comment itself, whose save is heard.
        MetricValue x => [ChangeFeed.MetricsDay(x.BriefDate)],
        MetricDefinition or MetricGroup => [ChangeFeed.Metrics],
        _ => [],
    };

    private void Collect(DbContext? ctx)
    {
        if (ctx is null) return;
        var topics = ctx.ChangeTracker.Entries()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .SelectMany(e => TopicsOf(e.Entity))
            .ToHashSet(StringComparer.Ordinal);
        if (topics.Count > 0) _pending.AddOrUpdate(ctx, topics);
        else _pending.Remove(ctx);
    }

    private void Publish(DbContext? ctx)
    {
        if (ctx is null || !_pending.TryGetValue(ctx, out var topics)) return;
        _pending.Remove(ctx);
        // Inside a transaction the caller opened, the save isn't committed yet: hold the topics for its commit.
        if (ctx.Database.CurrentTransaction is not null)
        {
            if (_held.TryGetValue(ctx, out var held)) held.UnionWith(topics);
            else _held.AddOrUpdate(ctx, topics);
            return;
        }
        Send(topics);
    }

    private void Send(IEnumerable<string> topics)
    {
        var name = user.IsAuthenticated ? user.DisplayName : "";
        foreach (var topic in topics) feed.Publish(new Change(topic, user.UserId, name, origin.Id));
    }

    private void Release(DbContext? ctx)
    {
        if (ctx is null || !_held.TryGetValue(ctx, out var topics)) return;
        _held.Remove(ctx);
        Send(topics);
    }

    private void Drop(DbContext? ctx)
    {
        if (ctx is not null) _held.Remove(ctx);
    }

    private void Forget(DbContext? ctx)
    {
        if (ctx is not null) _pending.Remove(ctx);
    }
}
