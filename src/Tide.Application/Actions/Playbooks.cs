using Microsoft.EntityFrameworkCore;
using Tide.Application.Abstractions;
using Tide.Application.Articles;
using Tide.Application.Org;
using Tide.Application.Security;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.Application.Actions;

/// <summary>A playbook as the administrator edits it: its steps one per line, in order.</summary>
public sealed class PlaybookForm
{
    public Guid? Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public ActionKind? Kind { get; set; }
    public bool IsActive { get; set; } = true;
    public List<string> Steps { get; set; } = [];

    public static PlaybookForm From(Playbook p) => new()
    {
        Id = p.Id, Name = p.Name, Description = p.Description, Kind = p.Kind, IsActive = p.IsActive,
        Steps = p.Steps.OrderBy(s => s.Order).Select(s => s.Text).ToList(),
    };
}

/// <summary>
/// Playbooks: administrators write them, for a kind of action or for any; whoever creates or works an
/// action starts its checklist from one (<see cref="ActionService.ApplyPlaybookAsync"/>). One that an action was started
/// from is retired, not deleted, so the action's history still names it.
/// </summary>
public sealed class PlaybookService(IAppDbContextFactory dbs, ICurrentUser user)
{
    /// <summary>Every playbook with its steps, in order; retired ones too when <paramref name="includeRetired"/>.</summary>
    public async Task<IReadOnlyList<Playbook>> ListAsync(bool includeRetired = false, CancellationToken ct = default)
    {
        if (!user.Has(Permission.ReadTeamEdition) && !user.Has(Permission.WorkActions)) throw new ForbiddenException("You don't have access to actions.");
        await using var db = await dbs.CreateAsync(ct);
        var all = await db.Playbooks.AsNoTracking().Include(p => p.Steps).Where(p => includeRetired || p.IsActive).ToListAsync(ct);
        foreach (var p in all) p.Steps = p.Steps.OrderBy(s => s.Order).ToList();
        return all.OrderBy(p => p.SortOrder).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The active playbooks that suit an action of <paramref name="kind"/>: its kind's, then those for any kind.</summary>
    public async Task<IReadOnlyList<Playbook>> ForKindAsync(ActionKind kind, CancellationToken ct = default) =>
        (await ListAsync(ct: ct)).Where(p => p.Kind is null || p.Kind == kind).OrderBy(p => p.Kind is null ? 1 : 0).ToList();

    /// <summary>Adds or changes a playbook. Returns its id.</summary>
    public async Task<Guid> SaveAsync(PlaybookForm form, CancellationToken ct = default)
    {
        RequireAdmin();
        var name = ArticleService.Clean(form.Name, 100);
        if (name.Length == 0) throw new ArgumentException("A playbook needs a name.");
        if (form.Kind is { } k && !Enum.IsDefined(k)) throw new ArgumentException("Choose a kind of action.");
        var steps = form.Steps.Select(s => ArticleService.Clean(s, ActionChecklistItem.MaxText)).Where(s => s.Length > 0).ToList();
        if (steps.Count == 0) throw new ArgumentException("A playbook needs at least one step.");
        if (steps.Count > Playbook.MaxSteps) throw new ArgumentException($"A playbook can have at most {Playbook.MaxSteps} steps.");

        await using var db = await dbs.CreateAsync(ct);
        Playbook p;
        if (form.Id is { } id)
        {
            p = await db.Playbooks.Include(x => x.Steps).FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException("That playbook no longer exists.");
            db.PlaybookSteps.RemoveRange(p.Steps);
        }
        else
        {
            p = new Playbook { SortOrder = (await db.Playbooks.Select(x => (int?)x.SortOrder).MaxAsync(ct) ?? -1) + 1 };
            db.Playbooks.Add(p);
        }
        // Matched ignoring case on both providers (LIKE, as search does: ADR 0011); SQL Server's collation would, SQLite wouldn't.
        var exact = Search.SearchText.Literal(name);
        if (await db.Playbooks.AnyAsync(x => x.Id != p.Id && EF.Functions.Like(x.Name, exact, Search.SearchText.Escape), ct))
            throw new ArgumentException($"There's already a playbook called {name}.");
        p.Name = name;
        p.Description = ArticleService.CleanOrNull(form.Description, 1000, multiline: true);
        p.Kind = form.Kind;
        p.IsActive = form.IsActive;
        db.PlaybookSteps.AddRange(steps.Select((s, i) => new PlaybookStep { PlaybookId = p.Id, Order = i, Text = s }));
        await db.SaveChangesAsync(ct);
        return p.Id;
    }

    /// <summary>Removes a playbook: deleted if no action was started from it, otherwise retired (hidden from the pickers).</summary>
    public async Task RemoveAsync(Guid id, CancellationToken ct = default)
    {
        RequireAdmin();
        await using var db = await dbs.CreateAsync(ct);
        var p = await db.Playbooks.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return;
        if (await db.Actions.AnyAsync(a => a.PlaybookId == id, ct)) p.IsActive = false;
        else db.Playbooks.Remove(p);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Moves a playbook up or down the pickers' order.</summary>
    public async Task MoveAsync(Guid id, int by, CancellationToken ct = default)
    {
        RequireAdmin();
        await using var db = await dbs.CreateAsync(ct);
        var all = (await db.Playbooks.ToListAsync(ct)).OrderBy(p => p.SortOrder).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        AdminLists.Swap(all, all.FindIndex(p => p.Id == id), by, (p, i) => p.SortOrder = i);
        await db.SaveChangesAsync(ct);
    }

    private void RequireAdmin()
    {
        if (!user.Has(Permission.Administer)) throw new ForbiddenException("Only administrators manage playbooks.");
    }
}

/// <summary>
/// Card rules: administrators say which actions stand out on the board and list (a colour and a label), and which of
/// those are a filter chip. Matching is <see cref="ActionRules"/>; the rules themselves are read by everyone who reads
/// actions.
/// </summary>
public sealed class CardRuleService(IAppDbContextFactory dbs, ICurrentUser user)
{
    /// <summary>The rules, in order; inactive ones too when <paramref name="includeInactive"/>.</summary>
    public async Task<IReadOnlyList<ActionCardRule>> ListAsync(bool includeInactive = false, CancellationToken ct = default)
    {
        if (!user.Has(Permission.ReadTeamEdition) && !user.Has(Permission.WorkActions)) throw new ForbiddenException("You don't have access to actions.");
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
