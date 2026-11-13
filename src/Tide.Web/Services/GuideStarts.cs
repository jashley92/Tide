using Tide.Application.Actions;
using Tide.Application.Articles;
using Tide.Application.Operations;
using Tide.Web.Components.Shared;

namespace Tide.Web.Services;

/// <summary>
/// Where a guide started from Help opens: its page, or for a page of one article or one action, a real one the person
/// can open (the newest article; the open action due first), read through the services' own permission checks.
/// </summary>
public sealed class GuideStarts(ArticleService articles, ActionService actions)
{
    /// <summary>The page to open for <paramref name="guide"/>, as a link from the app's root has it; null when there's no
    /// article or action to show it on yet.</summary>
    public async Task<string?> PageAsync(Guide guide, CancellationToken ct = default)
    {
        if (guide.Page.Contains("{id}", StringComparison.Ordinal))
            return await articles.ListAsync(take: 1, ct: ct) is [var article, ..]
                ? guide.Page.Replace("{id}", article.Id.ToString(), StringComparison.Ordinal) : null;
        if (guide.Page.Contains("{n}", StringComparison.Ordinal))
            return (await actions.PageAsync(new ActionFilter(), size: 1, ct: ct)).Rows is [var action, ..] ? Ui.ActionUrl(action.Number) : null;
        return guide.Page;
    }
}
