using Tide.Application.Actions;
using Tide.Application.Articles;
using Tide.Application.Operations;
using Tide.Application.Settings;
using Tide.Web.Components.Shared;

namespace Tide.Web.Services;

/// <summary>
/// Where a guide opens: its page, or for a page of one article or one action, a real one the person can open (the newest
/// article; the open action due first); for Search, a lookup of a CVE or an indicator the person can already see
/// (<see cref="LookupAsync"/>). All read through the services' own permission checks; nothing is written.
/// </summary>
public sealed class GuideStarts(ArticleService articles, ActionService actions, DistributionSettings distribution)
{
    // How many of the newest articles a lookup looks through for one with a CVE or an indicator.
    private const int Newest = 20;

    /// <summary>Whether <paramref name="guide"/> looks something up itself (Search's), so is opened on its lookup even
    /// from its own page's Show me around.</summary>
    public static bool LooksUp(Guide guide) => guide.Key == "search";

    /// <summary>The page to open for <paramref name="guide"/>, as a link from the app's root has it, and the value it looks
    /// up (Search's; empty for the others); null when there's no article or action to show it on yet.</summary>
    public async Task<(string Page, string LookedUp)?> StartAsync(Guide guide, CancellationToken ct = default)
    {
        if (guide.Page.Contains("{id}", StringComparison.Ordinal))
            return await articles.ListAsync(take: 1, ct: ct) is [var article, ..]
                ? (guide.Page.Replace("{id}", article.Id.ToString(), StringComparison.Ordinal), "") : null;
        if (guide.Page.Contains("{n}", StringComparison.Ordinal))
            return (await actions.PageAsync(new ActionFilter(), size: 1, ct: ct)).Rows is [var action, ..] ? (Ui.ActionUrl(action.Number), "") : null;
        if (LooksUp(guide) && await LookupAsync(ct) is { } value) return (Ui.LookupUrl(value), value);
        return (guide.Page, "");
    }

    /// <summary>The page to open for <paramref name="guide"/> (<see cref="StartAsync"/>), or null.</summary>
    public async Task<string?> PageAsync(Guide guide, CancellationToken ct = default) => (await StartAsync(guide, ct))?.Page;

    /// <summary>
    /// What Search's guide looks up: the first CVE, else the first indicator, on the newest article the person can read
    /// that's within the team edition's TLP ceiling; null when no such article has one. Never a made-up value, never one
    /// from above the ceiling.
    /// </summary>
    public async Task<string?> LookupAsync(CancellationToken ct = default) =>
        await articles.FirstLookupAsync((await distribution.GetAsync(ct)).Team, Newest, ct);
}
