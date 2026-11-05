using System.Net.Mail;
using Microsoft.EntityFrameworkCore;
using Tide.Application.Abstractions;
using Tide.Application.Content;
using Tide.Application.Scorecard;
using Tide.Application.Security;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.Application.Actions;

/// <summary>
/// The teams that own actions, and who is on them. Administrators manage them; a team that owns an action is retired,
/// not deleted. Being on a team lets a person with WorkActions work that team's actions.
/// </summary>
public sealed class TeamService(IAppDbContextFactory dbs, ICurrentUser user)
{
    /// <summary>Every team with its members, in order; retired ones too when <paramref name="includeRetired"/>.</summary>
    public async Task<IReadOnlyList<Team>> ListAsync(bool includeRetired = false, CancellationToken ct = default)
    {
        RequireRead();
        await using var db = await dbs.CreateAsync(ct);
        var teams = await db.Teams.AsNoTracking().Include(t => t.Members)
            .Where(t => includeRetired || !t.IsRetired).ToListAsync(ct);
        foreach (var t in teams) t.Members = t.Members.OrderByName(m => m.DisplayName).ToList();
        return teams.OrderBy(t => t.SortOrder).ThenByName(t => t.Name).ToList();
    }

    /// <summary>The ids of the teams <paramref name="userId"/> is on.</summary>
    public async Task<IReadOnlySet<Guid>> TeamsOfAsync(string userId, CancellationToken ct = default)
    {
        RequireRead();
        await using var db = await dbs.CreateAsync(ct);
        return (await db.TeamMembers.AsNoTracking().Where(m => m.UserId == userId).Select(m => m.TeamId).ToListAsync(ct)).ToHashSet();
    }

    public async Task<Guid> AddAsync(string name, string? description, string? email, CancellationToken ct = default)
    {
        RequireAdmin();
        var clean = AdminLists.Name(name);
        await using var db = await dbs.CreateAsync(ct);
        var key = await AdminLists.UniqueKeyAsync(ScorecardService.KeyFor(clean), k => db.Teams.AnyAsync(t => t.Key == k, ct));
        var order = await db.Teams.Select(t => (int?)t.SortOrder).MaxAsync(ct) ?? -1;
        var team = new Team { Key = key, Name = clean, Description = AdminLists.Description(description), Email = Email(email), SortOrder = order + 1 };
        db.Teams.Add(team);
        await db.SaveChangesAsync(ct);
        return team.Id;
    }

    public async Task UpdateAsync(Guid id, string name, string? description, string? email, CancellationToken ct = default)
    {
        RequireAdmin();
        await using var db = await dbs.CreateAsync(ct);
        var team = await db.Teams.FirstOrDefaultAsync(t => t.Id == id, ct) ?? throw new KeyNotFoundException("That team no longer exists.");
        team.Name = AdminLists.Name(name);
        team.Description = AdminLists.Description(description);
        team.Email = Email(email);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Retires (or brings back) a team. One that never owned an action, and that no card rule names, is deleted
    /// instead (a rule's team can't be deleted from under it).</summary>
    public async Task SetRetiredAsync(Guid id, bool retired, CancellationToken ct = default)
    {
        RequireAdmin();
        await using var db = await dbs.CreateAsync(ct);
        var team = await db.Teams.FirstOrDefaultAsync(t => t.Id == id, ct) ?? throw new KeyNotFoundException("That team no longer exists.");
        if (retired && !await db.Actions.AnyAsync(a => a.TeamId == id, ct) && !await db.ActionCardRules.AnyAsync(r => r.TeamId == id, ct))
            db.Teams.Remove(team);
        else team.IsRetired = retired;
        await db.SaveChangesAsync(ct);
    }

    public async Task MoveAsync(Guid id, int by, CancellationToken ct = default)
    {
        RequireAdmin();
        await using var db = await dbs.CreateAsync(ct);
        var all = await db.Teams.OrderBy(t => t.SortOrder).ThenBy(t => t.Name).ToListAsync(ct);
        AdminLists.Move(all, all.FindIndex(t => t.Id == id), by, (t, i) => t.SortOrder = i);
        await db.SaveChangesAsync(ct);
    }

    public async Task AddMemberAsync(Guid teamId, string userId, string displayName, CancellationToken ct = default)
    {
        RequireAdmin();
        if (string.IsNullOrWhiteSpace(userId)) throw new ArgumentException("Choose a person.");
        await using var db = await dbs.CreateAsync(ct);
        if (!await db.Teams.AnyAsync(t => t.Id == teamId, ct)) throw new KeyNotFoundException("That team no longer exists.");
        var id = userId.Trim();
        if (await db.TeamMembers.AnyAsync(m => m.TeamId == teamId && m.UserId == id, ct)) return;
        db.TeamMembers.Add(new TeamMember
        {
            TeamId = teamId, UserId = id, DisplayName = Articles.ArticleService.Clean(displayName, 256), AddedAtUtc = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Takes someone off a team. Their open actions for it go back to the team, unassigned and said so in each one's
    /// history: left with them they'd be on nobody's list (the team's queue reads them as a colleague's, they may no
    /// longer work them, and the reminders would still go only to them). Returns how many were handed back.
    /// </summary>
    public async Task<int> RemoveMemberAsync(Guid teamId, string userId, CancellationToken ct = default)
    {
        RequireAdmin();
        await using var db = await dbs.CreateAsync(ct);
        if (await db.TeamMembers.FirstOrDefaultAsync(m => m.TeamId == teamId && m.UserId == userId, ct) is not { } row) return 0;
        db.TeamMembers.Remove(row);
        var team = await db.Teams.AsNoTracking().Where(t => t.Id == teamId).Select(t => t.Name).FirstAsync(ct);
        var theirs = await db.Actions.WhereOpen().Where(a => a.TeamId == teamId && a.AssigneeId == userId).ToListAsync(ct);
        foreach (var a in theirs)
        {
            a.AssigneeId = null;
            a.AssigneeName = null;
            a.EditVersion = Guid.NewGuid();
            db.ActionEvents.Add(new ActionEvent
            {
                ActionId = a.Id, AtUtc = DateTimeOffset.UtcNow, ByName = user.DisplayName,
                Text = $"Back to {team}, unassigned: {row.DisplayName} left the team.",
            });
        }
        await db.SaveChangesAsync(ct);
        return theirs.Count;
    }

    private static string? Email(string? e)
    {
        var t = (e ?? "").Trim();
        if (t.Length == 0) return null;
        if (t.Length > 256 || !MailAddress.TryCreate(t, out var parsed) || !parsed.Address.Equals(t, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"'{t}' isn't an email address.");
        return t;
    }

    // The teams and who is on them are read by whoever reads actions (the board, an action's team picker, the meeting);
    // a Reader has no page that needs them, and a list of user ids and names isn't theirs to see.
    private void RequireRead()
    {
        if (!user.Has(Permission.ReadTeamEdition) && !user.Has(Permission.WorkActions)) throw new ForbiddenException("You don't have access to the teams.");
    }

    private void RequireAdmin()
    {
        if (!user.Has(Permission.Administer)) throw new ForbiddenException("Only an administrator can change the teams.");
    }
}
