using Microsoft.EntityFrameworkCore;
using Tide.Application.Abstractions;
using Tide.Application.Briefs;
using Tide.Application.Security;
using Tide.Domain.Enums;

namespace Tide.Application.Intake;

/// <summary>
/// What a promote from Triage came to (<see cref="TriagePromoter.PromoteAsync"/>): the promote itself, and, when the form
/// asked for it, the brief the draft went on (<see cref="BriefDate"/>) or why it didn't (<see cref="NotOnBrief"/>).
/// </summary>
public sealed record TriagePromotion(PromoteOutcome Outcome, DateOnly? BriefDate = null, string? NotOnBrief = null)
{
    /// <summary>What the toast's Undo takes back, when there's something this promote made to take back: the promote's
    /// own, unless the brief step adds to it.</summary>
    public PromoteUndo? Undo { get; init; } = Outcome.Undo;
}

/// <summary>
/// Triage's promote, with its one extra: "Add to today's brief". The promote is <see cref="IntakeService"/>'s, unchanged;
/// the draft then goes on the end of the brief for <see cref="BriefService.CurrentDate"/> (started if there's none), as
/// the editor's "Add to today's brief" puts it there.
/// </summary>
/// <remarks>
/// Two steps, not one transaction. The promote is what was asked for and stands on its own: if the brief can't take the
/// draft (it was sent meanwhile), the draft is kept and the analyst is told why, as they would be from the editor. Adding
/// to a brief is one save of its own (<see cref="BriefService.AddArticleAsync"/> checks the brief can still change as it
/// writes, and a colleague's change to the brief at the same moment is refused by its edit version), so a draft is never
/// half on a brief. Each step is audited as it is from its own page.
/// </remarks>
public sealed class TriagePromoter(IntakeService intake, BriefService briefs, ICurrentUser user, Rotation.IPresenterLookup? presenters = null)
{
    /// <summary>
    /// Promotes the reports (one, or a group's as one article) with the form's choices, then, when
    /// <see cref="PromoteForm.AddToBrief"/>, puts the draft on today's brief. Building the brief is checked before anything
    /// is claimed, so someone who may not add to a brief isn't left with a promote they didn't ask for on its own. A
    /// draft a colleague made first (the outcome isn't <see cref="PromoteOutcome.Made"/>) isn't put on the brief: it's
    /// theirs to place.
    /// </summary>
    public async Task<TriagePromotion> PromoteAsync(IReadOnlyCollection<Guid> ids, PromoteForm form, CancellationToken ct = default)
    {
        // The brief's own rule (BriefService: building a brief), asked first.
        if (form.AddToBrief && !user.Has(Permission.WriteArticles)) throw new ForbiddenException("Only analysts can build briefs.");
        var outcome = await intake.PromoteOrJoinAsync(ids, form, ct);
        if (!form.AddToBrief) return new(outcome);
        if (!outcome.Made) return new(outcome, NotOnBrief: $"{outcome.By} made this article, so it wasn't added to the brief.");
        try
        {
            var day = briefs.CurrentDate;
            var had = await briefs.GetAsync(day, ct) is not null;
            var brief = await briefs.GetOrStartAsync(day, ct);
            await briefs.AddArticleAsync(brief.Id, outcome.ArticleId, ct: ct);
            // A brief this step started, as it is now (the article on it): Undo removes it while it's still just that.
            var started = had ? null : (await briefs.GetAsync(day, ct))?.EditVersion;
            return new(outcome, brief.BriefDate) { Undo = outcome.Undo! with { BriefId = brief.Id, StartedBriefVersion = started } };
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or KeyNotFoundException)
        {
            // The brief was sent, or its day can't have one: the draft stays, and why it isn't on the brief is said.
            return new(outcome, NotOnBrief: ex.Message);
        }
        catch (DbUpdateException)
        {
            // A colleague changed the brief at the same moment: theirs stands, and this one can be added from Today.
            return new(outcome, NotOnBrief: "The brief changed at the same moment, so the draft wasn't added. Add it from Today.");
        }
    }

    /// <summary>The toast's Undo: <see cref="IntakeService.UndoPromoteAsync"/>.</summary>
    public Task<bool> UndoAsync(PromoteUndo undo, CancellationToken ct = default) => intake.UndoPromoteAsync(undo, ct);

    /// <summary>The day "today's brief" is (the next working day's, on a weekend or holiday).</summary>
    public DateOnly BriefDate => briefs.CurrentDate;

    /// <summary>Whether the signed-in person presents <see cref="BriefDate"/>'s brief, by the rotation (with its cover and
    /// swaps). It decides which remembered tick the promote form starts with: on for the presenter, off for others.</summary>
    public async Task<bool> PresentsAsync(CancellationToken ct = default) =>
        presenters is not null && await presenters.PresenterAsync(BriefDate, ct) is { } p && p.Id == user.UserId;
}
