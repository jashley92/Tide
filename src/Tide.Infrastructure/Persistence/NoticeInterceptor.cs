using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Tide.Application.Abstractions;
using Tide.Application.Content;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.Infrastructure.Persistence;

/// <summary>
/// Writes the For you <see cref="Notice"/>s a save calls for, in that same save, so a notice and what it's about commit
/// or fail together and every path that makes the change is heard (a button, the editor, a bulk change):
/// <list type="bullet">
/// <item>an article whose writer became someone else: the new writer (<see cref="NoticeKind.ArticleHandedToYou"/>);</item>
/// <item>a review asked: the reviewer;</item>
/// <item>an action handed to another team: each of that team's members, unless it's external (its reminders tell it);</item>
/// <item>an action now assigned to someone, made or changed: them;</item>
/// <item>a brief rotation request made: the partner; decided: the requester, and whoever asked for them.</item>
/// </list>
/// Never for whoever made the change, and nothing for a save with nobody signed in. Runs before
/// <see cref="ChangeInterceptor"/> collects its topics, so the person's open pages hear their new notice.
/// </summary>
public sealed class NoticeInterceptor(ICurrentUser user, TimeProvider time) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (eventData.Context is { } ctx && Wanted(ctx) is { Count: > 0 } wanted)
            Add(ctx, wanted, Members(ctx, wanted)?.ToList() ?? []);
        return result;
    }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } ctx && Wanted(ctx) is { Count: > 0 } wanted)
            Add(ctx, wanted, Members(ctx, wanted) is { } q ? await q.ToListAsync(cancellationToken) : []);
        return result;
    }

    // One notice to write: for a person, or (Team set) for each member of a team.
    private sealed record Want(NoticeKind Kind, Guid SubjectId, string? Detail, string? UserId = null, Guid? Team = null);

    private sealed record Member(Guid TeamId, string UserId);

    private List<Want> Wanted(DbContext ctx)
    {
        if (string.IsNullOrEmpty(user.UserId)) return [];
        var wanted = new List<Want>();
        // Entries() detects changes first; only the four kinds of row a notice can be about are looked at.
        foreach (var e in ctx.ChangeTracker.Entries())
        {
            if (e.State is not (EntityState.Added or EntityState.Modified)) continue;
            switch (e.Entity)
            {
                case Article a when e.State == EntityState.Modified && Changed(e, nameof(Article.WriterId)) && a.WriterId is { } writer:
                    wanted.Add(new(NoticeKind.ArticleHandedToYou, a.Id, a.HandoffNote, writer));
                    break;
                case ArticleReview r when e.State == EntityState.Added && r.Status == ReviewStatus.Asked:
                    wanted.Add(new(NoticeKind.ReviewAsked, r.ArticleId, r.Note, r.ReviewerId));
                    break;
                case ActionItem a:
                    if (e.State == EntityState.Modified && Changed(e, nameof(ActionItem.TeamId)) && Changed(e, nameof(ActionItem.HandedAtUtc)))
                        wanted.Add(new(NoticeKind.ActionHandedToTeam, a.Id, a.HandoffReason, Team: a.TeamId));
                    if (a.AssigneeId is { } held && (e.State == EntityState.Added || Changed(e, nameof(ActionItem.AssigneeId))))
                        wanted.Add(new(NoticeKind.ActionAssigned, a.Id, null, held));
                    break;
                case RotationChange c when e.State == EntityState.Added && c.Status == RotationChangeStatus.Pending:
                    wanted.Add(new(NoticeKind.RotationAsked, c.Id, c.Note, c.PartnerId));
                    break;
                case RotationChange c when e.State == EntityState.Modified && Changed(e, nameof(RotationChange.Status))
                                           && c.Status is RotationChangeStatus.Approved or RotationChangeStatus.Declined:
                    wanted.Add(new(NoticeKind.RotationDecided, c.Id, c.DecisionNote, c.RequesterId));
                    if (c.CreatedById != c.RequesterId) wanted.Add(new(NoticeKind.RotationDecided, c.Id, c.DecisionNote, c.CreatedById));
                    break;
            }
        }
        return wanted;
    }

    private static bool Changed(EntityEntry e, string property)
    {
        var p = e.Property(property);
        return p.IsModified && !Equals(p.OriginalValue, p.CurrentValue);
    }

    // The members of the internal teams actions were handed to, in one query; null when no action was.
    private static IQueryable<Member>? Members(DbContext ctx, List<Want> wanted)
    {
        var teams = wanted.Where(w => w.Team is not null).Select(w => w.Team!.Value).Distinct().ToList();
        if (teams.Count == 0) return null;
        var internalTeams = ctx.Set<Team>().Where(t => teams.Contains(t.Id) && !t.IsExternal).Select(t => t.Id);
        return ctx.Set<TeamMember>().AsNoTracking().Where(m => internalTeams.Contains(m.TeamId)).Select(m => new Member(m.TeamId, m.UserId));
    }

    private void Add(DbContext ctx, List<Want> wanted, List<Member> members)
    {
        var now = time.GetUtcNow();
        var people = wanted.SelectMany(w => w.Team is { } team
            ? members.Where(m => m.TeamId == team).Select(m => w with { UserId = m.UserId })
            : [w]);
        // Once per person and subject: assigned to you beats handed to your team; the first otherwise.
        var notices = people.Where(w => !string.IsNullOrEmpty(w.UserId) && w.UserId != user.UserId)
            .OrderBy(w => w.Kind == NoticeKind.ActionAssigned ? 0 : 1)
            .DistinctBy(w => (w.UserId, w.SubjectId))
            .Select(w => new Notice
            {
                UserId = w.UserId!, Kind = w.Kind, SubjectId = w.SubjectId, ActorId = user.UserId, ActorName = user.DisplayName,
                Detail = w.Detail is null ? null : Words.Clip(w.Detail, Notice.MaxDetail), CreatedAtUtc = now,
            });
        ctx.Set<Notice>().AddRange(notices);
    }
}
