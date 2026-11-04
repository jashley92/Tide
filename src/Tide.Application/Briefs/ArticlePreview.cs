using Tide.Application.Actions;
using Tide.Application.Articles;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.Application.Briefs;

/// <summary>How much of an article one edition carries.</summary>
public enum EditionVerdict
{
    /// <summary>All of it.</summary>
    Included,

    /// <summary>The article, with some of its parts left out for being above the ceiling.</summary>
    Partly,

    /// <summary>None of it: the article itself is above the ceiling.</summary>
    LeftOut,
}

/// <summary>
/// An article being written, as one edition will carry it: the article the form would save as, its cut
/// (<see cref="BriefEditions.BuildArticle"/>, null when the whole article is left out), and the readiness checks on it.
/// Built in memory from the editor's form, so it follows unsaved edits; what's left out is said here so the editor's
/// preview can show it to the writer, where the edition itself only leaves it out.
/// </summary>
public sealed record ArticlePreview(Article Article, Edition Edition, TlpLevel Ceiling, EditionArticle? Cut, IReadOnlyList<ArticleCheck> Checks)
{
    /// <summary>The cut for <paramref name="edition"/> under <paramref name="ceiling"/> of what <paramref name="form"/>
    /// would save as. <paramref name="actions"/>, the ones raised from the article, are carried by the team edition only.
    /// <paramref name="companyCeiling"/>, the company-wide edition's ceiling, adds the checks that depend on it
    /// (<see cref="ArticleChecks.WhyReachesCompany"/>), whichever edition is shown.</summary>
    public static ArticlePreview Build(ArticleForm form, Edition edition, TlpLevel ceiling, IReadOnlyList<EditionAction>? actions = null,
        TlpLevel? companyCeiling = null)
    {
        var article = form.ToPreview();
        return new ArticlePreview(article, edition, ceiling, BriefEditions.BuildArticle(article, edition, ceiling, actions),
            ArticleChecks.For(article, companyCeiling));
    }

    /// <summary>
    /// The actions the team edition lists for an article, from the article's action rows: cancelled ones left out, in
    /// number order, as <see cref="BriefEditions.ActionsAsync"/> gives them for a whole brief.
    /// </summary>
    public static IReadOnlyList<EditionAction> TeamActions(IEnumerable<ActionRow> rows) => rows
        .Where(r => r.Status != ActionStatus.Cancelled)
        .OrderBy(r => r.Number)
        .Select(r => new EditionAction(r.Number, r.Title, r.TeamName, r.DueDate, r.Status))
        .ToList();

    /// <summary>The edition named in a link (<c>?edition=company</c>); anything else is the team edition.</summary>
    public static Edition ParseEdition(string? key) =>
        string.Equals(key, "company", StringComparison.OrdinalIgnoreCase) ? Edition.Public : Edition.Team;

    /// <summary>The edition's name in a link: <c>company</c>, or null for the team edition (the default).</summary>
    public static string? EditionKey(Edition edition) => edition == Edition.Public ? "company" : null;

    /// <summary>Whether the whole article is above the ceiling.</summary>
    public bool LeftOut => Cut is null;

    public EditionVerdict Verdict => Cut is null ? EditionVerdict.LeftOut : Cut.WithheldParts > 0 ? EditionVerdict.Partly : EditionVerdict.Included;

    /// <summary>
    /// The company-wide edition never carries indicators or follow-up actions: true when this article has some that it
    /// leaves out for that reason (<paramref name="actions"/> being the article's team-edition actions).
    /// </summary>
    public bool DropsTeamOnlyParts(int actions) =>
        Edition == Edition.Public && Cut is not null && (Article.Indicators.Count > 0 || actions > 0);
}
