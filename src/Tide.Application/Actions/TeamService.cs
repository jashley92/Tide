using System.Net.Mail;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
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
public sealed class TeamService(IAppDbContextFactory dbs, ICurrentUser user, IOptionsMonitor<ReminderOptions> reminders)
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

    /// <summary>Why <paramref name="team"/> won't be emailed its reminders, or null when it will be (<see cref="ExternalCadence.NotEmailedWhy"/>).</summary>
    public string? NotEmailedWhy(Team team) => ExternalCadence.NotEmailedWhy(team, reminders.CurrentValue.ExternalReminders.Enabled);

    /// <summary>Why <paramref name="team"/> won't be emailed, with its cause, or null (<see cref="ExternalCadence.NotEmailed"/>).</summary>
    public NotEmailedReason? NotEmailed(Team team) => ExternalCadence.NotEmailed(team, reminders.CurrentValue.ExternalReminders.Enabled);

    /// <summary>
    /// Turns an external team's own reminders on, from the note that said they're off: its schedule stays as it was. For
    /// whoever may keep the external teams; audited, and moves the team's edit version as any change to it does.
    /// </summary>
    public async Task TurnRemindersOnAsync(Guid id, CancellationToken ct = default)
    {
        RequireExternal();
        await using var db = await dbs.CreateAsync(ct);
        var team = await TeamAsync(db, id, ct);
        if (!team.IsExternal) throw new ArgumentException("Only an external team's reminders are turned on here.");
        if (team.RemindersOn) return;
        team.RemindersOn = true;
        await SaveMovedAsync(db, team, ct);
    }

    /// <summary>A blank form for a new external team: its reminders on or off, and their schedule, as an administrator
    /// set for new teams (<see cref="ReminderOptions.ExternalReminderOptions"/>), the rest at their defaults.</summary>
    public ExternalTeamForm NewExternalForm()
    {
        var o = reminders.CurrentValue.ExternalReminders;
        return new() { RemindersOn = o.NewTeamsOn, RemindBeforeDays = o.BeforeDays, RemindOverdueEveryDays = o.EveryDays };
    }

    /// <summary>An external team with just a name and, optionally, its contact's email (the split dialog's quick add),
    /// its reminders on or off as set for new teams.</summary>
    public Task<Guid> AddExternalAsync(string name, string? contactEmail, CancellationToken ct = default)
    {
        var form = NewExternalForm();
        (form.Name, form.ContactEmail) = (name, contactEmail);
        return AddExternalAsync(form, ct);
    }

    /// <summary>
    /// Saves an external team's form: its profile, reminders and escalation contact, and, when the caller administers,
    /// its limit. Refused for an internal team (an administrator's), and when someone else changed the team since
    /// <paramref name="form"/> opened (its <see cref="ExternalTeamForm.EditVersion"/>). One save: all or none, one audit
    /// entry, one change heard.
    /// </summary>
    public async Task UpdateExternalAsync(Guid id, ExternalTeamForm form, CancellationToken ct = default)
    {
        RequireExternal();
        var admin = user.Has(Permission.Administer);
        if (admin) CheckLimit(form.ActiveLimit, form.LimitMode);
        await using var db = await dbs.CreateAsync(ct);
        var team = await TeamAsync(db, id, ct);
        if (!team.IsExternal) throw new ForbiddenException("Only an administrator can change an internal team.");
        NotStale(team, form.EditVersion);
        form.ApplyTo(team);
        if (admin) (team.ActiveLimit, team.LimitMode) = (form.ActiveLimit, form.LimitMode);
        await SaveMovedAsync(db, team, ct);
    }

    /// <summary>
    /// Saves an internal team's form (Teams &amp; SLA): its name, description, mailbox, limit and escalation contact.
    /// Refused when someone else changed the team since <paramref name="form"/> opened (its
    /// <see cref="TeamForm.EditVersion"/>). One save: all or none, one audit entry, one change heard.
    /// </summary>
    public async Task UpdateAsync(Guid id, TeamForm form, CancellationToken ct = default)
    {
        RequireAdmin();
        CheckLimit(form.ActiveLimit, form.LimitMode);
        var (name, description, email) = (AdminLists.Name(form.Name), AdminLists.Description(form.Description), Email(form.Email));
        var escalation = EscalationContact(form.EscalationName, form.EscalationEmail);
        await using var db = await dbs.CreateAsync(ct);
        var team = await TeamAsync(db, id, ct);
        NotStale(team, form.EditVersion);
        (team.Name, team.Description, team.Email) = (name, description, email);
        (team.ActiveLimit, team.LimitMode) = (form.ActiveLimit, form.LimitMode);
        (team.EscalationName, team.EscalationEmail) = escalation;
        await SaveMovedAsync(db, team, ct);
    }

    /// <summary>Sets a team's active limit (null: none) and what happens at it. Administrators only, for any team.</summary>
    public async Task SetLimitAsync(Guid id, int? limit, ActiveLimitMode mode, CancellationToken ct = default)
    {
        RequireAdmin();
        CheckLimit(limit, mode);
        await using var db = await dbs.CreateAsync(ct);
        var team = await TeamAsync(db, id, ct);
        (team.ActiveLimit, team.LimitMode) = (limit, mode);
        await SaveMovedAsync(db, team, ct);
    }

    private static void CheckLimit(int? limit, ActiveLimitMode mode)
    {
        if (limit is < 1 or > MaxActiveLimit) throw new ArgumentException($"A limit is 1 to {MaxActiveLimit} active actions, or blank for none.");
        if (!Enum.IsDefined(mode)) throw new ArgumentException("Choose what happens at the limit.");
    }

    /// <summary>The highest active limit accepted.</summary>
    public const int MaxActiveLimit = 999;

    /// <summary>Whether the current user may add and change external teams: anyone who works actions.</summary>
    public bool CanManageExternal => user.Has(Permission.WorkActions) || user.Has(Permission.Administer);

    private void RequireExternal()
    {
        if (!CanManageExternal) throw new ForbiddenException("Only someone who works actions can change the external teams.");
    }

    /// <summary>Changes an internal team's name, description and mailbox.</summary>
    public async Task UpdateAsync(Guid id, string name, string? description, string? email, CancellationToken ct = default)
    {
        RequireAdmin();
        await using var db = await dbs.CreateAsync(ct);
        var team = await TeamAsync(db, id, ct);
        team.Name = AdminLists.Name(name);
        team.Description = AdminLists.Description(description);
        team.Email = Email(email);
        await SaveMovedAsync(db, team, ct);
    }

    /// <summary>
    /// Sets who the escalation ladder copies about a team's slipping actions: a name and an email, both optional (blank
    /// clears them). An internal team's is an administrator's to set; an external team's, whoever keeps the external
    /// teams'. Audited like any change to a team.
    /// </summary>
    public async Task SetEscalationContactAsync(Guid id, string? name, string? email, CancellationToken ct = default)
    {
        await using var db = await dbs.CreateAsync(ct);
        var team = await TeamAsync(db, id, ct);
        if (team.IsExternal) RequireExternal();
        else RequireAdmin();
        (team.EscalationName, team.EscalationEmail) = EscalationContact(name, email);
        await SaveMovedAsync(db, team, ct);
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
        // A deleted action counts: it's kept (an administrator may restore it), and its foreign key holds the team.
        if (retired && !await db.Actions.IgnoreQueryFilters().AnyAsync(a => a.TeamId == id || a.HandedFromTeamId == id, ct) && !await db.ActionCardRules.AnyAsync(r => r.TeamId == id, ct))
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
    // skips the check (a form not opened on the team).
    private static void NotStale(Team team, Guid? began)
    {
        if (began is { } v && v != team.EditVersion) throw new StaleEditException(Stale(team));
    }

    // Every change to the team's own fields moves its version, so EF's UPDATE ... WHERE EditVersion = @old also refuses
    // a save that lost a race the read didn't see.
    private static async Task SaveMovedAsync(IAppDbContext db, Team team, CancellationToken ct)
    {
        team.EditVersion = Guid.NewGuid();
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw new StaleEditException(Stale(team)); }
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

/// <summary>An internal team's form on Teams &amp; SLA, saved whole by <see cref="TeamService.UpdateAsync(Guid, TeamForm, CancellationToken)"/>.</summary>
public sealed class TeamForm
{
    /// <summary>The team's <see cref="Team.EditVersion"/> when the form opened; a save over someone else's change made
    /// since is refused. Null skips the check.</summary>
    public Guid? EditVersion { get; set; }

    public string Name { get; set; } = "";
    public string? Description { get; set; }

    /// <summary>The team's mailbox.</summary>
    public string? Email { get; set; }

    /// <summary>The most actions it may have active at once (null: none), and what happens at it.</summary>
    public int? ActiveLimit { get; set; }
    public ActiveLimitMode LimitMode { get; set; } = ActiveLimitMode.Show;

    /// <summary>Who the escalation ladder copies when its actions slip.</summary>
    public string? EscalationName { get; set; }
    public string? EscalationEmail { get; set; }

    public static TeamForm From(Team t) => new()
    {
        EditVersion = t.EditVersion, Name = t.Name, Description = t.Description, Email = t.Email, ActiveLimit = t.ActiveLimit, LimitMode = t.LimitMode,
        EscalationName = t.EscalationName, EscalationEmail = t.EscalationEmail,
    };
}

/// <summary>An external team's profile, reminders and limit, as its form edits them (the limit is an administrator's).</summary>
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

    /// <summary>The most actions it may have active at once (null: none), and what happens at it. Saved only when an
    /// administrator saves the form; a new team has none.</summary>
    public int? ActiveLimit { get; set; }
    public ActiveLimitMode LimitMode { get; set; } = ActiveLimitMode.Show;

    public static ExternalTeamForm From(Team t) => new()
    {
        EditVersion = t.EditVersion, Name = t.Name, Description = t.Description, ContactName = t.ContactName, ContactEmail = t.ContactEmail, Engage = t.Engage,
        RemindersOn = t.RemindersOn, RemindBeforeDays = t.RemindBeforeDays, RemindOverdueEveryDays = t.RemindOverdueEveryDays,
        EscalationName = t.EscalationName, EscalationEmail = t.EscalationEmail, ActiveLimit = t.ActiveLimit, LimitMode = t.LimitMode,
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
