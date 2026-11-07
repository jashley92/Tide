using System.Net.Mail;
using Microsoft.EntityFrameworkCore;
using Tide.Application.Abstractions;
using Tide.Application.Articles;
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
        return teams.InOrder(t => t.SortOrder, t => t.Name).ToList();
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
        var team = new Team { Name = clean, Description = AdminLists.Description(description), Email = Email(email) };
        await AddTeamAsync(db, team, ct);
        return team.Id;
    }

    // A new team gets a unique key from its name and goes last.
    private static async Task AddTeamAsync(IAppDbContext db, Team team, CancellationToken ct)
    {
        team.Key = await AdminLists.UniqueKeyAsync(ScorecardService.KeyFor(team.Name), k => db.Teams.AnyAsync(t => t.Key == k, ct));
        team.SortOrder = (await db.Teams.Select(t => (int?)t.SortOrder).MaxAsync(ct) ?? -1) + 1;
        db.Teams.Add(team);
        await db.SaveChangesAsync(ct);
    }

    // --- external teams -----------------------------------------------------------------------------------------

    /// <summary>
    /// Adds a team that's tracked here but works elsewhere (<see cref="Team.IsExternal"/>): anyone who works actions may,
    /// as they hand work to it. It has no members; its contact gets its reminders. Audited like any team.
    /// </summary>
    public async Task<Guid> AddExternalAsync(ExternalTeamForm form, CancellationToken ct = default)
    {
        RequireExternal();
        await using var db = await dbs.CreateAsync(ct);
        var team = new Team { IsExternal = true };
        form.ApplyTo(team);
        await AddTeamAsync(db, team, ct);
        return team.Id;
    }

    /// <summary>An external team with just a name and, optionally, its contact's email (the split dialog's quick add).</summary>
    public Task<Guid> AddExternalAsync(string name, string? contactEmail, CancellationToken ct = default) =>
        AddExternalAsync(new ExternalTeamForm { Name = name, ContactEmail = contactEmail }, ct);

    /// <summary>
    /// Changes an external team's profile and reminders. Refused for an internal team (an administrator's), and when
    /// someone else changed the team since <paramref name="form"/> opened (its <see cref="ExternalTeamForm.EditVersion"/>).
    /// Returns the team's new edit version, for the rest of the same save (its limit).
    /// </summary>
    public async Task<Guid> UpdateExternalAsync(Guid id, ExternalTeamForm form, CancellationToken ct = default)
    {
        RequireExternal();
        await using var db = await dbs.CreateAsync(ct);
        var team = await TeamAsync(db, id, ct);
        if (!team.IsExternal) throw new ForbiddenException("Only an administrator can change an internal team.");
        NotStale(team, form.EditVersion);
        form.ApplyTo(team);
        return await SaveMovedAsync(db, team, ct);
    }

    /// <summary>Sets a team's active limit (null: none) and what happens at it. Administrators only, for any team.
    /// Refused when <paramref name="editVersion"/> is given and someone else changed the team since; returns the new one.</summary>
    public async Task<Guid> SetLimitAsync(Guid id, int? limit, ActiveLimitMode mode, Guid? editVersion = null, CancellationToken ct = default)
    {
        RequireAdmin();
        if (limit is < 1 or > MaxActiveLimit) throw new ArgumentException($"A limit is 1 to {MaxActiveLimit} active actions, or blank for none.");
        if (!Enum.IsDefined(mode)) throw new ArgumentException("Choose what happens at the limit.");
        await using var db = await dbs.CreateAsync(ct);
        var team = await TeamAsync(db, id, ct);
        NotStale(team, editVersion);
        (team.ActiveLimit, team.LimitMode) = (limit, mode);
        return await SaveMovedAsync(db, team, ct);
    }

    /// <summary>The highest active limit accepted.</summary>
    public const int MaxActiveLimit = 999;

    /// <summary>Whether the current user may add and change external teams: anyone who works actions.</summary>
    public bool CanManageExternal => user.Has(Permission.WorkActions) || user.Has(Permission.Administer);

    private void RequireExternal()
    {
        if (!CanManageExternal) throw new ForbiddenException("Only someone who works actions can change the external teams.");
    }

    /// <summary>Changes an internal team's name, description and mailbox (Teams &amp; SLA). Refused when
    /// <paramref name="editVersion"/> is given and someone else changed the team since; returns the new one.</summary>
    public async Task<Guid> UpdateAsync(Guid id, string name, string? description, string? email, Guid? editVersion = null, CancellationToken ct = default)
    {
        RequireAdmin();
        await using var db = await dbs.CreateAsync(ct);
        var team = await TeamAsync(db, id, ct);
        NotStale(team, editVersion);
        team.Name = AdminLists.Name(name);
        team.Description = AdminLists.Description(description);
        team.Email = Email(email);
        return await SaveMovedAsync(db, team, ct);
    }

    /// <summary>
    /// Sets who the escalation ladder copies about a team's slipping actions: a name and an email, both optional (blank
    /// clears them). An internal team's is an administrator's to set; an external team's, whoever keeps the external
    /// teams'. Audited like any change to a team. Refused when <paramref name="editVersion"/> is given and someone else
    /// changed the team since; returns the new one.
    /// </summary>
    public async Task<Guid> SetEscalationContactAsync(Guid id, string? name, string? email, Guid? editVersion = null, CancellationToken ct = default)
    {
        await using var db = await dbs.CreateAsync(ct);
        var team = await TeamAsync(db, id, ct);
        if (team.IsExternal) RequireExternal();
        else RequireAdmin();
        NotStale(team, editVersion);
        (team.EscalationName, team.EscalationEmail) = EscalationContact(name, email);
        return await SaveMovedAsync(db, team, ct);
    }

    // An escalation contact as it's kept: the name tidied, the address checked.
    internal static (string? Name, string? Email) EscalationContact(string? name, string? email) =>
        (Articles.ArticleService.CleanOrNull(name, Team.MaxContactName), Email(email));

    /// <summary>Retires (or brings back) a team. One that never owned an action, and that no card rule names, is deleted
    /// instead (a rule's team can't be deleted from under it). Moves the edit version: a form opened before can't save
    /// over the retirement, and one opened on a retired team can't save once it's back.</summary>
    public async Task SetRetiredAsync(Guid id, bool retired, CancellationToken ct = default)
    {
        await using var db = await dbs.CreateAsync(ct);
        var team = await TeamAsync(db, id, ct);
        // An external team is retired by whoever keeps the external teams; an internal one only by an administrator.
        if (team.IsExternal) RequireExternal();
        else RequireAdmin();
        if (retired && !await db.Actions.AnyAsync(a => a.TeamId == id || a.HandedFromTeamId == id, ct) && !await db.ActionCardRules.AnyAsync(r => r.TeamId == id, ct))
            db.Teams.Remove(team);
        else team.IsRetired = retired;
        await SaveMovedAsync(db, team, ct);
    }

    /// <summary>Moves a team up or down the list. A team's place is on no form, so this moves no edit version.</summary>
    public async Task MoveAsync(Guid id, int by, CancellationToken ct = default)
    {
        RequireAdmin();
        await using var db = await dbs.CreateAsync(ct);
        var all = (await db.Teams.ToListAsync(ct)).InOrder(t => t.SortOrder, t => t.Name).ToList();
        AdminLists.Move(all, all.FindIndex(t => t.Id == id), by, (t, i) => t.SortOrder = i);
        try { await db.SaveChangesAsync(ct); }
        // A colleague's change to one of the teams landed between the read and the save.
        catch (DbUpdateConcurrencyException) { throw new StaleEditException("Someone else changed the teams at the same moment. Try the move again."); }
    }

    private static async Task<Team> TeamAsync(IAppDbContext db, Guid id, CancellationToken ct) =>
        await db.Teams.FirstOrDefaultAsync(t => t.Id == id, ct) ?? throw new KeyNotFoundException("That team no longer exists.");

    // Another person's save, limit or retirement since the form opened: saving would put back what they changed. Null
    // skips the check (a caller that means to overwrite).
    private static void NotStale(Team team, Guid? began)
    {
        if (began is { } v && v != team.EditVersion) throw new StaleEditException(Stale(team));
    }

    // Every change to the team's own fields moves its version, so EF's UPDATE ... WHERE EditVersion = @old also refuses
    // a save that lost a race the read didn't see.
    private static async Task<Guid> SaveMovedAsync(IAppDbContext db, Team team, CancellationToken ct)
    {
        team.EditVersion = Guid.NewGuid();
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw new StaleEditException(Stale(team)); }
        return team.EditVersion;
    }

    private static string Stale(Team t) =>
        $"Someone else changed {t.Name} since you opened it (a save, a limit or a retirement). Reload to see their changes, then make yours again.";

    public async Task AddMemberAsync(Guid teamId, string userId, string displayName, CancellationToken ct = default)
    {
        RequireAdmin();
        if (string.IsNullOrWhiteSpace(userId)) throw new ArgumentException("Choose a person.");
        await using var db = await dbs.CreateAsync(ct);
        var external = await db.Teams.Where(t => t.Id == teamId).Select(t => (bool?)t.IsExternal).FirstOrDefaultAsync(ct)
                       ?? throw new KeyNotFoundException("That team no longer exists.");
        if (external) throw new ArgumentException("An external team works outside TIDE, so it has no members here.");
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

    internal static string? Email(string? e)
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

/// <summary>An external team's profile and reminders, as its form edits them.</summary>
public sealed class ExternalTeamForm
{
    /// <summary>The team's <see cref="Team.EditVersion"/> when the form opened; a save over someone else's change made
    /// since is refused. Null skips the check (a new team, or a caller that means to overwrite).</summary>
    public Guid? EditVersion { get; set; }

    public string Name { get; set; } = "";

    /// <summary>What they do, and the context someone handing work to them needs.</summary>
    public string? Description { get; set; }
    public string? ContactName { get; set; }

    /// <summary>Their mailbox: where their reminders go.</summary>
    public string? ContactEmail { get; set; }

    /// <summary>How to engage them: their ticket queue, a form, who to call.</summary>
    public string? Engage { get; set; }
    public bool RemindersOn { get; set; } = true;
    public int RemindBeforeDays { get; set; } = Team.DefaultRemindBefore;

    /// <summary>Business days between reminders while overdue; null reminds once.</summary>
    public int? RemindOverdueEveryDays { get; set; } = Team.DefaultRemindEvery;

    /// <summary>Who the escalation ladder copies when their actions slip (their manager, say).</summary>
    public string? EscalationName { get; set; }
    public string? EscalationEmail { get; set; }

    public static ExternalTeamForm From(Team t) => new()
    {
        EditVersion = t.EditVersion, Name = t.Name, Description = t.Description, ContactName = t.ContactName, ContactEmail = t.ContactEmail, Engage = t.Engage,
        RemindersOn = t.RemindersOn, RemindBeforeDays = t.RemindBeforeDays, RemindOverdueEveryDays = t.RemindOverdueEveryDays,
        EscalationName = t.EscalationName, EscalationEmail = t.EscalationEmail,
    };

    /// <summary>Checks the form and copies it onto <paramref name="team"/>; throws <see cref="ArgumentException"/> saying what's wrong.</summary>
    internal void ApplyTo(Team team)
    {
        if (RemindBeforeDays is < 0 or > Team.MaxRemindBefore)
            throw new ArgumentException($"Remind them 0 to {Team.MaxRemindBefore} business days before the due date (0: on the day).");
        if (RemindOverdueEveryDays is < Team.MinRemindEvery or > Team.MaxRemindEvery)
            throw new ArgumentException($"While overdue, remind them every {Team.MinRemindEvery} to {Team.MaxRemindEvery} business days, or once.");
        var contact = Articles.ArticleService.CleanOrNull(ContactName, Team.MaxContactName);
        // With no contact email, their reminders have nowhere to go: none is sent until one is given.
        var email = TeamService.Email(ContactEmail);
        var escalation = TeamService.EscalationContact(EscalationName, EscalationEmail);
        team.Name = AdminLists.Name(Name);
        team.Description = AdminLists.Description(Description);
        team.ContactName = contact;
        team.ContactEmail = email;
        team.Engage = Articles.ArticleService.CleanOrNull(Engage, Team.MaxEngage, multiline: true);
        team.RemindersOn = RemindersOn;
        team.RemindBeforeDays = RemindBeforeDays;
        team.RemindOverdueEveryDays = RemindOverdueEveryDays;
        (team.EscalationName, team.EscalationEmail) = escalation;
    }
}
