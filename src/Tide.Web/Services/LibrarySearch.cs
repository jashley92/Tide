using Tide.Application.Abstractions;
using Tide.Application.Actions;
using Tide.Application.Security;
using Tide.Web.Components.Layout;
using Tide.Web.Components.Shared;

namespace Tide.Web.Services;

/// <summary>What the Library's search found in one tab: the entries in use that match, in the tab's order.</summary>
public sealed record LibraryHits(LibraryTab Tab, IReadOnlyList<Guid> Ids);

/// <summary>
/// The Library's one search: the words looked for in every tab the person may open, each tab's entries in use matched as
/// that tab's list matches them. A tab is searched only when the person has its permission (<see cref="LibraryTab.VisibleTo"/>)
/// and its library's own read check lets them in: what a tab they can't open holds is never read, let alone counted.
/// </summary>
public sealed class LibrarySearch(ICurrentUser user, SavedQueryService queries, OutreachTemplateService templates, PlaybookService playbooks, CardRuleService rules)
{
    /// <summary>The matches in each tab the person may open, in the tabs' order; none for no words.</summary>
    public Task<IReadOnlyList<LibraryHits>> FindAsync(string? text, CancellationToken ct = default) => FindAsync(text, LibraryTabs.All, ct);

    /// <summary><see cref="FindAsync(string?, CancellationToken)"/> over <paramref name="tabs"/>, each still held to its permission.</summary>
    internal async Task<IReadOnlyList<LibraryHits>> FindAsync(string? text, IReadOnlyList<LibraryTab> tabs, CancellationToken ct = default)
    {
        var words = TableView.Words(text);
        if (words.Count == 0) return [];
        // Each library is its own read (a service opens its own database context), so they run together.
        var found = await Task.WhenAll(tabs.Where(t => t.VisibleTo(user)).Select(async tab =>
        {
            try { return new LibraryHits(tab, await MatchesAsync(tab.Key, words, ct)); }
            catch (ForbiddenException) { return null; }   // the library's own check keeps them out: nothing from it
        }));
        return [.. found.OfType<LibraryHits>()];
    }

    private async Task<IReadOnlyList<Guid>> MatchesAsync(string key, IReadOnlyList<string> words, CancellationToken ct) => key switch
    {
        LibraryTabs.Queries => [.. (await queries.OfferedAsync(ct)).Where(q => SavedQueryService.Matches(q, words)).Select(q => q.Id)],
        LibraryTabs.Templates => [.. (await templates.OfferedAsync(ct)).Where(t => OutreachTemplateService.Matches(t, words)).Select(t => t.Id)],
        LibraryTabs.Playbooks => [.. (await playbooks.ListAsync(ct: ct)).Where(p => PlaybookService.Matches(p, words)).Select(p => p.Id)],
        _ => [.. (await rules.ListAsync(ct: ct)).Where(r => CardRuleService.Matches(r, words)).Select(r => r.Id)],
    };
}
