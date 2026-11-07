using Microsoft.EntityFrameworkCore;
using Tide.Application.Abstractions;
using Tide.Application.Articles;
using Tide.Application.Security;
using Tide.Application.Settings;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.Application.Actions;

/// <summary>An outreach template as its form edits it. <see cref="EditVersion"/> is the version the edit began on.</summary>
public sealed class OutreachTemplateForm
{
    public Guid? Id { get; set; }
    public Guid EditVersion { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    public string? To { get; set; }
    public TlpLevel Ceiling { get; set; } = TlpLevel.Clear;

    public static OutreachTemplateForm From(OutreachTemplate t) => new()
    {
        Id = t.Id, EditVersion = t.EditVersion, Name = t.Name, Description = t.Description, Subject = t.Subject, Body = t.Body, To = t.To,
        Ceiling = t.Ceiling,
    };
}

/// <summary>A template on the list, with how many emails were written from it.</summary>
public sealed record OutreachTemplateRow(OutreachTemplate Template, int Emails);

/// <summary>
/// The team's library of outreach emails (ADR 0019): a question to a vendor, a partner, an ISAC or an internal team, kept
/// so a colleague can fill it from their own Outreach action rather than write it again. Kept as the saved hunt queries
/// are (<see cref="SavedQueryService"/>): anyone who works actions saves, edits, retires and restores them, and deletes one
/// they saved; an administrator deletes any. Each change is in the audit trail and on the change feed, and an edit begun
/// on an older copy is refused.
/// </summary>
public sealed class OutreachTemplateService(IAppDbContextFactory dbs, ICurrentUser user)
{
    private readonly TeamLibrary<OutreachTemplate> _library = new(dbs, user, db => db.OutreachTemplates, new(
        "That template no longer exists.",
        "Someone else changed this template since you opened it. Reload to see their changes.",
        "Only people who work actions can change the outreach templates.",
        "Only the person who saved it, or an administrator, can delete a template. Retire it instead."));

    /// <summary>Whether the current user may save, edit, retire and restore templates.</summary>
    public bool CanWrite => _library.CanWrite;

    /// <summary>Whether the current user may delete <paramref name="t"/>: an administrator, or whoever saved it.</summary>
    public bool CanDelete(OutreachTemplate t) => _library.CanDelete(t.CreatedBy);

    /// <summary>Every template, retired ones too when <paramref name="includeRetired"/>, by name, each with how many
    /// emails were marked sent from it.</summary>
    public async Task<IReadOnlyList<OutreachTemplateRow>> ListAsync(bool includeRetired = false, CancellationToken ct = default)
    {
        RequireRead();
        await using var db = await dbs.CreateAsync(ct);
        var all = await db.OutreachTemplates.AsNoTracking().Where(t => includeRetired || !t.IsRetired).ToListAsync(ct);
        var ids = all.Select(t => t.Id).ToList();
        // Counted in the database, for the templates read.
        var used = await db.OutreachMessages.Where(m => m.TemplateId != null && ids.Contains(m.TemplateId.Value))
            .GroupBy(m => m.TemplateId!.Value)
            .Select(g => new { g.Key, Emails = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Emails, ct);
        return Ordered(all).Select(t => new OutreachTemplateRow(t, used.GetValueOrDefault(t.Id))).ToList();
    }

    /// <summary>The templates an Outreach action offers (not retired), by name.</summary>
    public async Task<IReadOnlyList<OutreachTemplate>> OfferedAsync(CancellationToken ct = default)
    {
        RequireRead();
        await using var db = await dbs.CreateAsync(ct);
        return Ordered(await db.OutreachTemplates.AsNoTracking().Where(t => !t.IsRetired).ToListAsync(ct)).ToList();
    }

    private static IOrderedEnumerable<OutreachTemplate> Ordered(IEnumerable<OutreachTemplate> templates) =>
        templates.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase);

    public async Task<OutreachTemplate?> GetAsync(Guid id, CancellationToken ct = default)
    {
        RequireRead();
        await using var db = await dbs.CreateAsync(ct);
        return await db.OutreachTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);
    }

    /// <summary>Whether <paramref name="t"/> matches every word typed, in its name, description, subject or body, ignoring case.</summary>
    public static bool Matches(OutreachTemplate t, IReadOnlyList<string> words) => words.All(w =>
        t.Name.Contains(w, StringComparison.OrdinalIgnoreCase)
        || (t.Description?.Contains(w, StringComparison.OrdinalIgnoreCase) ?? false)
        || t.Subject.Contains(w, StringComparison.OrdinalIgnoreCase)
        || t.Body.Contains(w, StringComparison.OrdinalIgnoreCase));

    /// <summary>Saves a new template, or changes one; its id. A change begun on a copy someone has since changed is refused.</summary>
    public async Task<Guid> SaveAsync(OutreachTemplateForm form, CancellationToken ct = default)
    {
        _library.RequireWrite();
        var (name, description, subject, body, to, ceiling) = Checked(form);

        await using var db = await dbs.CreateAsync(ct);
        OutreachTemplate t;
        if (form.Id is { } id)
        {
            t = await _library.ForEditAsync(db.OutreachTemplates, id, form.EditVersion, ct);
            if (t.Name == name && t.Description == description && t.Subject == subject && t.Body == body && t.To == to && t.Ceiling == ceiling) return t.Id;
            _library.Changed(t);
        }
        else t = _library.Added(db, new OutreachTemplate());
        // One name, ignoring case on both providers (LIKE, as the saved queries' names are).
        var exact = Search.SearchText.Literal(name);
        if (await db.OutreachTemplates.AnyAsync(x => x.Id != t.Id && EF.Functions.Like(x.Name, exact, Search.SearchText.Escape), ct))
            throw new ArgumentException($"There's already a template called {name}.");
        t.Name = name;
        t.Description = description;
        t.Subject = subject;
        t.Body = body;
        t.To = to;
        t.Ceiling = ceiling;
        await _library.SaveEditAsync(db, ct);
        return t.Id;
    }

    /// <summary>
    /// A form's fields as they're kept, or refused in the form's words. Also what the starter set
    /// (<see cref="StarterOutreachTemplates"/>) is held to.
    /// </summary>
    internal static (string Name, string? Description, string Subject, string Body, string? To, TlpLevel Ceiling) Checked(OutreachTemplateForm form)
    {
        var name = ArticleService.Clean(form.Name, OutreachTemplate.MaxName);
        if (name.Length == 0) throw new ArgumentException("A template needs a name.");
        var description = ArticleService.CleanOrNull(form.Description, OutreachTemplate.MaxDescription);
        var subject = OutreachFill.CleanSubject(form.Subject, OutreachTemplate.MaxSubject,
            "A template needs a subject.", $"A subject can be at most {OutreachTemplate.MaxSubject} characters.");
        var body = OutreachFill.CleanBody(form.Body, OutreachTemplate.MaxBody,
            "A template needs a body.", $"A body can be at most {OutreachTemplate.MaxBody:N0} characters.");
        // A placeholder misspelt is refused now, not when an email is written from it.
        OutreachFill.Check(subject);
        OutreachFill.Check(body);
        return (name, description, subject, body, Address(form.To), Tlp.Checked(form.Ceiling));
    }

    /// <summary>A fixed To address as it's kept: one address, or none.</summary>
    internal static string? Address(string? text)
    {
        var to = (text ?? "").Trim();
        if (to.Length == 0) return null;
        if (to.Length > OutreachTemplate.MaxTo || !DistributionSettings.IsAddress(to)) throw new ArgumentException($"Not an email address: {to}");
        return to;
    }

    /// <summary>Retires a template (it's no longer offered on actions, and stays on the list to read) or restores it.</summary>
    public Task SetRetiredAsync(Guid id, bool retired, CancellationToken ct = default) => _library.SetRetiredAsync(id, retired, ct);

    /// <summary>Deletes a template: whoever saved it, or an administrator. Emails written from it keep its name.</summary>
    public Task DeleteAsync(Guid id, CancellationToken ct = default) => _library.DeleteAsync(id, null, ct);

    private void RequireRead()
    {
        if (!user.Has(Permission.ReadActions)) throw new ForbiddenException("You don't have access to the outreach templates.");
    }
}
