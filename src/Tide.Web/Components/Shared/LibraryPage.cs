using Microsoft.AspNetCore.Components;
using Tide.Application.Abstractions;
using Tide.Application.Org;
using Tide.Web.Services;

namespace Tide.Web.Components.Shared;

/// <summary>
/// What the team's library pages (Saved queries, Outreach templates, Playbooks) share: the rows read with or without the
/// retired ones, the words typed and the page's own filters, 20 cards a page with the page in the address (?page=2), a
/// colleague's change re-read in place, and one step at a time. A page says how it reads its rows, which of them its
/// filters leave and what it calls them; its markup, form and cards are its own.
/// </summary>
public abstract class LibraryPage<TRow> : ComponentBase, IDisposable
{
    [Inject] protected NavigationManager Nav { get; set; } = default!;
    [Inject] protected ToastService Toasts { get; set; } = default!;
    [Inject] protected OrgClock Clock { get; set; } = default!;
    [Inject] private ChangeFeed Feed { get; set; } = default!;
    [Inject] private ChangeOrigin Origin { get; set; } = default!;

    internal const int PageSize = 20;

    private protected IReadOnlyList<TRow>? _rows;
    private protected IReadOnlyList<TRow> _shown = [];
    private protected IReadOnlyList<TRow> _onPage = [];
    private protected int _page = 1;
    private protected bool _busy, _showRetired;
    private protected string? _words;
    private LiveChanges? _live;

    internal IReadOnlyList<TRow>? Rows => _rows;

    /// <summary>The rows the filters leave (<see cref="Filtered"/>).</summary>
    internal IReadOnlyList<TRow> Shown => _shown;

    /// <summary>The rows on the page shown, of <see cref="Shown"/>.</summary>
    internal IReadOnlyList<TRow> OnPage => _onPage;

    /// <summary>The page shown, from 1.</summary>
    internal int Page => _page;

    // A reload, Back or a copied link keeps the page. A filter starts again from the first page; an edit, or a colleague's
    // change, keeps the page, or shows the last one when a delete or retire took this page away.
    [SupplyParameterFromQuery(Name = "page")] public string? PageParam { get; set; }

    /// <summary>The change feed topic the rows are published on.</summary>
    protected abstract string Topic { get; }

    /// <summary>What one row is called and many, as the count says them ("template", "templates"), and the library, as a colleague's change says it ("outreach templates").</summary>
    protected abstract (string One, string Many, string Library) Names { get; }

    /// <summary>The rows, the retired ones too when <paramref name="includeRetired"/>, in the list's order.</summary>
    protected abstract Task<IReadOnlyList<TRow>> ReadAsync(bool includeRetired);

    /// <summary>The rows the page's filters and <paramref name="words"/> leave, in the order they're shown.</summary>
    protected abstract IEnumerable<TRow> Filtered(IReadOnlyList<TRow> rows, IReadOnlyList<string> words);

    /// <summary>A row's entry's id.</summary>
    protected abstract Guid IdOf(TRow row);

    /// <summary>The entry whose edit form is open, if one is.</summary>
    protected abstract Guid? EditingId { get; }

    /// <summary>
    /// Whether the entry being edited isn't among this page's cards (a colleague retired, deleted or renamed it away, or a
    /// filter now leaves it out): the page shows its form above the list instead, so what's typed can still be saved or
    /// cancelled rather than vanishing with the card.
    /// </summary>
    internal bool EditingOffPage => EditingId is { } id && !_onPage.Any(r => IdOf(r) == id);

    /// <summary>Clears the page's own filters (a language, a kind), for Clear.</summary>
    protected virtual void ClearOwnFilters() { }

    /// <summary>After the rows or the filters changed: anything the page works out from them once, not on each redraw.</summary>
    protected virtual void Filtering() { }

    protected override async Task OnInitializedAsync()
    {
        _page = TableView.PageFrom(PageParam);
        _live = new LiveChanges(Feed, Origin, c => c.Topic == Topic, InvokeAsync, OnChangedElsewhere);
        await Load();
    }

    public void Dispose()
    {
        _live?.Dispose();
        GC.SuppressFinalize(this);
    }

    // Another page in the address (a pager link, Back): show it.
    protected override void OnParametersSet()
    {
        var page = TableView.PageFrom(PageParam);
        if (page == _page) return;
        _page = page;
        Slice();
    }

    // Worked out when the rows or the filters change, not on each redraw.
    private void Filter()
    {
        _shown = Filtered(_rows ?? [], TableView.Words(_words)).ToList();
        Filtering();
        Slice();
    }

    // This page's cards, or the last page's when this one has gone.
    private void Slice()
    {
        var asked = _page;
        (_onPage, _page) = TableView.PageOf(_shown, _page, PageSize);
        if (_page != asked) TableView.SayPage(Nav, RendererInfo, _page);
    }

    // "1–20 of 34 playbooks", or "12 playbooks" on one page.
    protected string CountText => TableView.Count((_page - 1) * PageSize + 1, _onPage.Count, _shown.Count, null, false, Names.One, Names.Many);

    protected string PageHref(int page) => Nav.GetUriWithQueryParameter("page", page == 1 ? null : page);

    // Clear: the words and the page's own filters; retired ones stay shown or hidden as chosen.
    internal void ClearFilters()
    {
        _words = null;
        ClearOwnFilters();
        Refilter();
    }

    // A filter changed: the first page, and the address says so.
    internal void Refilter()
    {
        _page = 1;
        Filter();
        TableView.SayPage(Nav, RendererInfo, 1);
    }

    // The words, as they're typed.
    protected void SetWords(string? words)
    {
        _words = words;
        Refilter();
    }

    // A colleague saved, changed, retired or deleted one: re-read in place. An edit under way keeps what's typed; its save
    // is refused as stale if that one was the one changed. When the card being edited was on the page and a change moved
    // it to another (one added before it pushed it on), the page goes with it; a card that's gone, or that a filter now
    // leaves out, keeps its form above the list (EditingOffPage).
    internal async Task OnChangedElsewhere(IReadOnlyList<Change> heard)
    {
        if (_busy) return;   // a step is under way, and it reads theirs when it's done
        var editing = EditingId;
        var wasOnPage = editing is not null && !EditingOffPage;
        await Load();
        if (wasOnPage && EditingOffPage && _shown.Select(IdOf).ToList().IndexOf(editing!.Value) is var at and >= 0)
        {
            _page = at / PageSize + 1;
            Slice();
            TableView.SayPage(Nav, RendererInfo, _page);
        }
        Toasts.Show($"{LiveChanges.Who(heard)} updated the {Names.Library}.");
        StateHasChanged();
    }

    protected async Task Load()
    {
        try { _rows = await ReadAsync(_showRetired); }
        catch (Exception ex) { Toasts.Error(UserFacingError.For(ex)); }
        Filter();
    }

    protected async Task ToggleRetired(ChangeEventArgs e)
    {
        _showRetired = e.Value is true;
        _page = 1;
        TableView.SayPage(Nav, RendererInfo, 1);
        await Load();
    }

    // One step at a time (a second click while a save runs does nothing), then the rows read again.
    protected async Task Run(Func<Task> step)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            await step();
            await Load();
        }
        catch (Exception ex) { Toasts.Error(UserFacingError.For(ex)); }
        finally { _busy = false; }
    }

    // Retires one (it's no longer offered on actions) or restores it, and says so.
    protected Task Retiring(string name, bool retired, Func<Task> change) => Run(async () =>
    {
        await change();
        Toasts.Show(retired ? $"{name} retired. It's no longer offered on actions." : $"{name} restored.");
    });

    // "Not used yet", or how often it was ("Used on 3 actions", "Used for 1 email").
    protected static string Used(int count, string how, string noun) => count == 0 ? "Not used yet" : $"Used {how} {Ui.Plural(count, noun)}";

    // "Saved by …, changed by …". One saved before the library kept names has none for who saved it.
    protected string Provenance<T>(T entry) where T : Domain.Common.AuditableEntity, Domain.Common.ITeamLibraryEntry =>
        Ui.Provenance(Clock, entry, entry.CreatedByName.Length > 0 ? entry.CreatedByName : "someone", entry.ModifiedByName);
}
