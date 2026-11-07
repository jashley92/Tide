using Microsoft.EntityFrameworkCore;
using Tide.Application.Abstractions;
using Tide.Application.Articles;
using Tide.Application.Org;
using Tide.Application.Security;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.Application.Actions;

/// <summary>A playbook as its form edits it: its steps one per line, in order. <see cref="EditVersion"/> is the version the edit began on.</summary>
public sealed class PlaybookForm
{
    public Guid? Id { get; set; }
    public Guid EditVersion { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public ActionKind? Kind { get; set; }
    public List<string> Steps { get; set; } = [];

    public static PlaybookForm From(Playbook p) => new()
    {
        Id = p.Id, EditVersion = p.EditVersion, Name = p.Name, Description = p.Description, Kind = p.Kind,
        Steps = p.Steps.OrderBy(s => s.Order).Select(s => s.Text).ToList(),
    };
}

/// <summary>A playbook on the list, with how many actions' checklists were started from it.</summary>
public sealed record PlaybookRow(Playbook Playbook, int Actions);

/// <summary>
/// The team's playbooks: checklists for a kind of action or for any, which whoever creates or works an action starts its
/// checklist from (<see cref="ActionService.ApplyPlaybookAsync"/>). Kept as the saved hunt queries are
/// (<see cref="SavedQueryService"/>): anyone who works actions adds, edits, reorders, retires and restores them, and
/// deletes one they made; an administrator deletes any. The starters, made by TIDE, are an administrator's to delete.
/// One an action was started from is retired, not deleted, so the action's history still names it. Each change is in the
/// audit trail and on the change feed, and an edit begun on an older copy is refused.
/// </summary>
public sealed class PlaybookService(IAppDbContextFactory dbs, ICurrentUser user)
{
    private readonly TeamLibrary<Playbook> _library = new(dbs, user, db => db.Playbooks, new(
        "That playbook no longer exists.",
        "Someone else changed this playbook since you opened it. Reload to see their changes.",
        "Only people who work actions can change the playbooks.",
        "Only the person who made it, or an administrator, can delete a playbook. Retire it instead."));

    /// <summary>Whether the current user may add, edit, reorder, retire and restore playbooks.</summary>
    public bool CanWrite => _library.CanWrite;

    /// <summary>Whether the current user may delete <paramref name="p"/>: an administrator, or whoever made it.</summary>
    public bool CanDelete(Playbook p) => _library.CanDelete(p.CreatedBy);

    /// <summary>Every playbook with its steps, in the pickers' order; retired ones too when <paramref name="includeRetired"/>.</summary>
    public async Task<IReadOnlyList<Playbook>> ListAsync(bool includeRetired = false, CancellationToken ct = default)
    {
        RequireRead();
        await using var db = await dbs.CreateAsync(ct);
        return await ReadAsync(db, includeRetired, ct);
    }

    /// <summary>As <see cref="ListAsync"/>, each with how many actions' checklists were started from it.</summary>
    public async Task<IReadOnlyList<PlaybookRow>> RowsAsync(bool includeRetired = false, CancellationToken ct = default)
    {
        RequireRead();
        await using var db = await dbs.CreateAsync(ct);
        var all = await ReadAsync(db, includeRetired, ct);
        var ids = all.Select(p => p.Id).ToList();
        // Counted in the database, for the playbooks read.
        var used = await db.Actions.Where(a => a.PlaybookId != null && ids.Contains(a.PlaybookId.Value))
            .GroupBy(a => a.PlaybookId!.Value)
            .Select(g => new { g.Key, Actions = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Actions, ct);
        return all.Select(p => new PlaybookRow(p, used.GetValueOrDefault(p.Id))).ToList();
    }

    /// <summary>The active playbooks that suit an action of <paramref name="kind"/>: its kind's, then those for any kind.</summary>
    public async Task<IReadOnlyList<Playbook>> ForKindAsync(ActionKind kind, CancellationToken ct = default) =>
        (await ListAsync(ct: ct)).Where(p => Suits(p, kind)).OrderBy(p => p.Kind is null ? 1 : 0).ToList();

    /// <summary>Whether <paramref name="p"/> is offered on an action of <paramref name="kind"/>: one for that kind, or for any.</summary>
    public static bool Suits(Playbook p, ActionKind kind) => p.Kind is null || p.Kind == kind;

    /// <summary>Whether <paramref name="p"/> matches every word typed, in its name, description, kind or steps, ignoring case.</summary>
    public static bool Matches(Playbook p, IReadOnlyList<string> words) => words.All(w =>
        p.Name.Contains(w, StringComparison.OrdinalIgnoreCase)
        || (p.Description?.Contains(w, StringComparison.OrdinalIgnoreCase) ?? false)
        || (p.Kind is { } k && ActionService.KindName(k).Contains(w, StringComparison.OrdinalIgnoreCase))
        || p.Steps.Any(s => s.Text.Contains(w, StringComparison.OrdinalIgnoreCase)));

    // The playbooks with their steps in order, in the pickers' order.
    private static async Task<List<Playbook>> ReadAsync(IAppDbContext db, bool includeRetired, CancellationToken ct)
    {
        var all = await db.Playbooks.AsNoTracking().Include(p => p.Steps).Where(p => includeRetired || !p.IsRetired).ToListAsync(ct);
        foreach (var p in all) p.Steps = p.Steps.OrderBy(s => s.Order).ToList();
        return Ordered(all).ToList();
    }

    // The pickers' order, then name.
    private static IOrderedEnumerable<Playbook> Ordered(IEnumerable<Playbook> playbooks) =>
        playbooks.OrderBy(p => p.SortOrder).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Adds a playbook, or changes one; its id. A change begun on a copy someone has since changed is refused.</summary>
    public async Task<Guid> SaveAsync(PlaybookForm form, CancellationToken ct = default)
    {
        _library.RequireWrite();
        var (name, description, steps) = Checked(form);

        await using var db = await dbs.CreateAsync(ct);
        Playbook p;
        if (form.Id is { } id)
        {
            p = await _library.ForEditAsync(db.Playbooks.Include(x => x.Steps), id, form.EditVersion, ct);
            var sameSteps = p.Steps.OrderBy(s => s.Order).Select(s => s.Text).SequenceEqual(steps);
            if (p.Name == name && p.Description == description && p.Kind == form.Kind && sameSteps) return p.Id;
            _library.Changed(p);
            // The steps are written again only when they changed, so the audit trail shows a rename as just that.
            if (!sameSteps) db.PlaybookSteps.RemoveRange(p.Steps);
            else steps = [];
        }
        else p = _library.Added(db, new Playbook { SortOrder = (await db.Playbooks.Select(x => (int?)x.SortOrder).MaxAsync(ct) ?? -1) + 1 });
        // Matched ignoring case on both providers (LIKE, as search does: ADR 0011); SQL Server's collation would, SQLite wouldn't.
        var exact = Search.SearchText.Literal(name);
        if (await db.Playbooks.AnyAsync(x => x.Id != p.Id && EF.Functions.Like(x.Name, exact, Search.SearchText.Escape), ct))
            throw new ArgumentException($"There's already a playbook called {name}.");
        p.Name = name;
        p.Description = description;
        p.Kind = form.Kind;
        db.PlaybookSteps.AddRange(steps.Select((s, i) => new PlaybookStep { PlaybookId = p.Id, Order = i, Text = s }));
        await _library.SaveEditAsync(db, ct);
        return p.Id;
    }

    /// <summary>
    /// A form's name, description and steps as they're kept (blank steps dropped), or refused in the form's words. Also
    /// what the starter set (<see cref="StarterPlaybooks"/>) is held to.
    /// </summary>
    internal static (string Name, string? Description, List<string> Steps) Checked(PlaybookForm form)
    {
        var name = ArticleService.Clean(form.Name, Playbook.MaxName);
        if (name.Length == 0) throw new ArgumentException("A playbook needs a name.");
        if (form.Kind is { } k && !Enum.IsDefined(k)) throw new ArgumentException("Choose a kind of action.");
        var steps = form.Steps.Select(s => ArticleService.Clean(s, ActionChecklistItem.MaxText)).Where(s => s.Length > 0).ToList();
        if (steps.Count == 0) throw new ArgumentException("A playbook needs at least one step.");
        if (steps.Count > Playbook.MaxSteps) throw new ArgumentException($"A playbook can have at most {Playbook.MaxSteps} steps.");
        return (name, ArticleService.CleanOrNull(form.Description, Playbook.MaxDescription, multiline: true), steps);
    }

    /// <summary>Retires a playbook (it's no longer offered on actions, and stays on the list to read) or restores it.</summary>
    public Task SetRetiredAsync(Guid id, bool retired, CancellationToken ct = default) => _library.SetRetiredAsync(id, retired, ct);

    /// <summary>
    /// Deletes a playbook: whoever made it, or an administrator. One an action's checklist was started from is kept, so
    /// that action's history still names it: it's retired instead.
    /// </summary>
    public Task DeleteAsync(Guid id, CancellationToken ct = default) => _library.DeleteAsync(id, async (db, p, token) =>
    {
        if (await db.Actions.AnyAsync(a => a.PlaybookId == p.Id, token))
            throw new InvalidOperationException($"Actions' checklists were started from {p.Name}, so it's kept for their history. Retire it instead.");
    }, ct);

    /// <summary>
    /// Moves a playbook up or down the pickers' order: it swaps places with the next one offered that way, so a retired
    /// one between them doesn't take a click to pass, and stays where it was.
    /// </summary>
    public async Task MoveAsync(Guid id, int by, CancellationToken ct = default)
    {
        _library.RequireWrite();
        await using var db = await dbs.CreateAsync(ct);
        var all = Ordered(await db.Playbooks.ToListAsync(ct)).ToList();
        var offered = all.Where(p => !p.IsRetired).ToList();
        var at = offered.FindIndex(p => p.Id == id);
        if (at < 0 || at + by < 0 || at + by >= offered.Count) return;
        var from = all.IndexOf(offered[at]);
        // Each one whose place changes is changed as any edit is: its version moves, and it names who moved it.
        AdminLists.Swap(all, from, all.IndexOf(offered[at + by]) - from, (p, i) =>
        {
            if (p.SortOrder == i) return;
            p.SortOrder = i;
            _library.Changed(p);
        });
        await _library.SaveEditAsync(db, ct);
    }

    // Reading is as it was when only administrators kept them: whoever reads the team edition, or works actions.
    private void RequireRead()
    {
        if (!user.Has(Permission.ReadTeamEdition) && !user.Has(Permission.WorkActions)) throw new ForbiddenException("You don't have access to actions.");
    }
}

/// <summary>
/// Card rules: administrators say which actions stand out on the board and list (a colour and a label), and which of
/// those are offered as a filter. Matching is <see cref="ActionRules"/>; the rules themselves are read by everyone who reads
/// actions (the Board rules page shows them, read-only).
/// </summary>
public sealed class CardRuleService(IAppDbContextFactory dbs, ICurrentUser user)
{
    /// <summary>The rules, in order; inactive ones too when <paramref name="includeInactive"/>.</summary>
    public async Task<IReadOnlyList<ActionCardRule>> ListAsync(bool includeInactive = false, CancellationToken ct = default)
    {
        // Whoever reads actions: the board's colours and filters come from them, and Board rules lists them.
        if (!user.Has(Permission.ReadActions) && !user.Has(Permission.ReadTeamEdition) && !user.Has(Permission.WorkActions)) throw new ForbiddenException("You don't have access to actions.");
        await using var db = await dbs.CreateAsync(ct);
        return (await db.ActionCardRules.AsNoTracking().Where(r => includeInactive || r.IsActive).ToListAsync(ct))
            .OrderBy(r => r.SortOrder).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Adds (a new id) or changes a rule from <paramref name="form"/>, a detached copy. Returns its id.</summary>
    public async Task<Guid> SaveAsync(ActionCardRule form, CancellationToken ct = default)
    {
        RequireAdmin();
        var name = ArticleService.Clean(form.Name, 60);
        if (name.Length == 0) throw new ArgumentException("A rule needs a label: it's shown on the cards it matches.");
        if (!Enum.IsDefined(form.Colour) || !Enum.IsDefined(form.Sla) || (form.Kind is { } k && !Enum.IsDefined(k))
            || (form.PriorityAtLeast is { } p && !Enum.IsDefined(p)) || (form.Status is { } s && !Enum.IsDefined(s)))
            throw new ArgumentException("Choose from the lists.");
        if (form.Kind is null && form.PriorityAtLeast is null && form.TeamId is null && form.Status is null && form.Sla == CardRuleSla.Any
            && !form.Unassigned && !form.HasChecklist && !form.IsPart)
            throw new ArgumentException("Set at least one condition, or the rule would match every action.");

        await using var db = await dbs.CreateAsync(ct);
        if (form.TeamId is { } team && !await db.Teams.AnyAsync(t => t.Id == team, ct)) throw new ArgumentException("That team no longer exists.");
        var rule = await db.ActionCardRules.FirstOrDefaultAsync(r => r.Id == form.Id, ct);
        if (rule is null)
        {
            rule = new ActionCardRule { SortOrder = (await db.ActionCardRules.Select(r => (int?)r.SortOrder).MaxAsync(ct) ?? -1) + 1 };
            db.ActionCardRules.Add(rule);
        }
        rule.Name = name;
        rule.Colour = form.Colour;
        rule.IsActive = form.IsActive;
        rule.ShowAsFilter = form.ShowAsFilter;
        rule.Kind = form.Kind;
        rule.PriorityAtLeast = form.PriorityAtLeast;
        rule.TeamId = form.TeamId;
        rule.Status = form.Status;
        rule.Sla = form.Sla;
        rule.Unassigned = form.Unassigned;
        rule.HasChecklist = form.HasChecklist;
        rule.IsPart = form.IsPart;
        await db.SaveChangesAsync(ct);
        return rule.Id;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        RequireAdmin();
        await using var db = await dbs.CreateAsync(ct);
        if (await db.ActionCardRules.FirstOrDefaultAsync(r => r.Id == id, ct) is not { } rule) return;
        db.ActionCardRules.Remove(rule);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Moves a rule up or down: the first matching rule gives a card its colour.</summary>
    public async Task MoveAsync(Guid id, int by, CancellationToken ct = default)
    {
        RequireAdmin();
        await using var db = await dbs.CreateAsync(ct);
        var all = (await db.ActionCardRules.ToListAsync(ct)).OrderBy(r => r.SortOrder).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        AdminLists.Swap(all, all.FindIndex(r => r.Id == id), by, (r, i) => r.SortOrder = i);
        await db.SaveChangesAsync(ct);
    }

    private void RequireAdmin()
    {
        if (!user.Has(Permission.Administer)) throw new ForbiddenException("Only administrators manage card rules.");
    }
}

/// <summary>Which card rules an action matches: every condition the rule sets.</summary>
public static class ActionRules
{
    public static bool Matches(ActionCardRule rule, ActionRow row, DateOnly today, WorkingDays? workingDays = null)
    {
        if (rule.Kind is { } kind && row.Kind != kind) return false;
        if (rule.PriorityAtLeast is { } p && row.Priority > p) return false;
        if (rule.TeamId is { } team && row.TeamId != team) return false;
        if (rule.Status is { } status && row.Status != status) return false;
        if (rule.Unassigned && row.AssigneeId is not null) return false;
        if (rule.HasChecklist && row.ChecklistTotal == 0) return false;
        if (rule.IsPart && row.ParentId is null) return false;
        var sla = row.Sla(today, workingDays);
        return rule.Sla switch
        {
            CardRuleSla.Overdue => sla == SlaState.Overdue,
            CardRuleSla.DueSoon => sla == SlaState.DueSoon,
            CardRuleSla.OverdueOrDueSoon => sla is SlaState.Overdue or SlaState.DueSoon,
            _ => true,
        };
    }

    /// <summary>The rules <paramref name="row"/> matches, in order (the first gives its colour).</summary>
    public static IReadOnlyList<ActionCardRule> For(IEnumerable<ActionCardRule> rules, ActionRow row, DateOnly today, WorkingDays? workingDays = null) =>
        rules.Where(r => r.IsActive && Matches(r, row, today, workingDays)).ToList();

    /// <summary>A rule's conditions in words ("P2 or higher · overdue · unassigned").</summary>
    public static string Describe(ActionCardRule r, Func<Guid, string?> teamName)
    {
        var parts = new List<string>();
        if (r.Kind is { } k) parts.Add(ActionService.KindName(k));
        if (r.PriorityAtLeast is { } p) parts.Add(p == Priority.P1 ? "P1" : $"{p} or higher");
        if (r.TeamId is { } t) parts.Add(teamName(t) ?? "a removed team");
        if (r.Status is { } s) parts.Add(ActionService.StatusName(s));
        parts.Add(r.Sla switch
        {
            CardRuleSla.Overdue => "overdue",
            CardRuleSla.DueSoon => "due soon",
            CardRuleSla.OverdueOrDueSoon => "overdue or due soon",
            _ => "",
        });
        if (r.Unassigned) parts.Add("unassigned");
        if (r.HasChecklist) parts.Add("has a checklist");
        if (r.IsPart) parts.Add("a part of another action");
        return string.Join(" · ", parts.Where(x => x.Length > 0));
    }
}
