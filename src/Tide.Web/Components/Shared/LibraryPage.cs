using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Tide.Application.Abstractions;
using Tide.Application.Org;
using Tide.Web.Services;

namespace Tide.Web.Components.Shared;

/// <summary>A library entry as its list names it (<see cref="LibraryBrowser"/>): its name, what it's for in a line, its kind
/// (a query's language, a template's TLP ceiling, the kind of action a playbook is for), how often it was used, and the
/// heading it's listed under, when the list is grouped (a query's platform).</summary>
public sealed record LibraryEntry(Guid Id, string Name, string? Description, string Kind, string Used, bool Retired, string? Group = null);

/// <summary>
/// What the team's libraries (Saved queries, Outreach templates, Playbooks; each a tab of the Library page) share: the
/// rows read with or without the retired ones, the words typed and the library's own filters, 20 entries a page with the
/// page in the address (?page=2), the entry selected from the list and shown in full, in the address too (?row=), a
/// colleague's change re-read in place, and one step at a time. A library says how it reads its rows, which of them its
/// filters leave and what it calls them; its markup, form and the selected entry's card are its own.
/// </summary>
public abstract class LibraryPage<TRow> : ComponentBase, IDisposable where TRow : class
{
    [Inject] protected NavigationManager Nav { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] protected ToastService Toasts { get; set; } = default!;
    [Inject] protected OrgClock Clock { get; set; } = default!;
    [Inject] private ChangeFeed Feed { get; set; } = default!;
    [Inject] private ChangeOrigin Origin { get; set; } = default!;

    internal const int PageSize = 20;

    private protected IReadOnlyList<TRow>? _rows;
    private protected IReadOnlyList<TRow> _shown = [];
    private protected IReadOnlyList<TRow> _onPage = [];
    private IReadOnlyList<LibraryEntry> _entries = [];
    private Guid? _selectedId, _reveal, _rowSaid;

    // Where focus goes once the next render is on screen: a form that opened, or back to the list when one closed or the
    // entry shown left it. The library's markup captures the three places.
    private enum FocusOn { Form, List }
    private FocusOn? _focus;
    private protected LibraryBrowser? _browser;
    private protected ElementReference _formStart, _empty;
    private string? _pageAsked, _rowAsked, _wordsAsked;
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

    /// <summary>The page's rows as the list names them.</summary>
    protected IReadOnlyList<LibraryEntry> Entries => _entries;

    /// <summary>The row shown in full: the one chosen from the list or named in the address, else the page's first.</summary>
    internal TRow? Selected => _onPage.FirstOrDefault(r => IdOf(r) == _selectedId);

    protected Guid? SelectedId => _selectedId;

    // A reload, Back or a copied link keeps the page. A filter starts again from the first page; an edit, or a colleague's
    // change, keeps the page, or shows the last one when a delete or retire took this page away.
    [SupplyParameterFromQuery(Name = "page")] public string? PageParam { get; set; }

    // The selected entry, so a link to one opens it (on whichever page of the list it's on). The address is kept in step
    // with the one shown in the browser (SayRow), without a navigation, so focus stays in the list.
    [SupplyParameterFromQuery(Name = "row")] public string? RowParam { get; set; }

    /// <summary>The Library page's: called after a step here changed the rows, so the tab's count is read again.</summary>
    [Parameter] public EventCallback OnChanged { get; set; }

    /// <summary>The Library's search words (its ?q=): the list shows only the entries that match every one.</summary>
    [Parameter] public string? Words { get; set; }

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

    /// <summary>A row as the list names it.</summary>
    protected abstract LibraryEntry Entry(TRow row);

    /// <summary>The entry whose edit form is open, if one is.</summary>
    protected abstract Guid? EditingId { get; }

    /// <summary>
    /// Whether the entry being edited isn't the one shown (another was chosen from the list, a colleague retired, deleted
    /// or renamed it away, or a filter now leaves it out): the library shows its form above the list instead, so what's
    /// typed can still be saved or cancelled rather than vanishing; otherwise the form is where the entry is shown.
    /// </summary>
    internal bool EditingAway => EditingId is { } id && id != _selectedId;

    /// <summary>Clears the page's own filters (a language, a kind), for Clear.</summary>
    protected virtual void ClearOwnFilters() { }

    /// <summary>After the rows or the filters changed: anything the page works out from them once, not on each redraw.</summary>
    protected virtual void Filtering() { }

    protected override async Task OnInitializedAsync()
    {
        _page = TableView.PageFrom(PageParam);
        (_pageAsked, _rowAsked, _selectedId) = (PageParam, RowParam, RowId(RowParam));
        _rowSaid = _selectedId;
        _words = _wordsAsked = Words;
        // A link to an entry alone (no page): the page of the list it's on.
        if (PageParam is null) _reveal = _selectedId;
        _live = new LiveChanges(Feed, Origin, c => c.Topic == Topic, InvokeAsync, OnChangedElsewhere);
        await Load();
    }

    private static Guid? RowId(string? text) => Guid.TryParse(text, out var id) ? id : null;

    public void Dispose()
    {
        _live?.Dispose();
        GC.SuppressFinalize(this);
    }

    // Another page or entry in the address (a pager link, Back): show it. A page's link names no entry, so its first is
    // shown. Only when the address changed: the page shown may be ahead of it for a moment (a filter's first page, a
    // saved entry's page, said to the address in place), and a redraw meanwhile (the Library page counting again)
    // mustn't go back to the address's page.
    protected override void OnParametersSet()
    {
        // New words in the Library's search: the list again from its first page (the address dropped the page with them).
        if (Words != _wordsAsked)
        {
            (_wordsAsked, _words, _page) = (Words, Words, 1);
            Filter();
        }
        if (PageParam == _pageAsked && RowParam == _rowAsked) return;
        var sameRow = RowParam == _rowAsked;
        (_pageAsked, _rowAsked, _rowSaid) = (PageParam, RowParam, RowId(RowParam));
        var page = TableView.PageFrom(PageParam);
        if (page == _page && sameRow) return;
        (_page, _selectedId) = (page, RowId(RowParam));
        Slice();
    }

    // The page shown, said to the address in place when it says another: a filter that starts again from the first page,
    // a page that's gone, the page of an entry just saved or linked to.
    private void SayPage() => TableView.SayPage(Nav, RendererInfo, _page);

    // Worked out when the rows or the filters change, not on each redraw.
    private void Filter()
    {
        _shown = Filtered(_rows ?? [], TableView.Words(_words)).ToList();
        Filtering();
        Slice();
    }

    // This page's rows, or the last page's when this one has gone; and the one shown in full, which stays the same entry
    // while it's on the page, else the page's first.
    private void Slice()
    {
        var asked = _page;
        (_onPage, _page) = TableView.PageOf(_shown, _page, PageSize);
        if (_page != asked) SayPage();
        _entries = _onPage.Select(Entry).ToList();
        if (Selected is null) _selectedId = _onPage.Count > 0 ? IdOf(_onPage[0]) : null;
    }

    // Shows an entry: the page of the list it's on, with it selected. Not when the filters leave it out, or it's gone.
    private void Show(Guid id)
    {
        var at = _shown.Select(IdOf).ToList().IndexOf(id);
        if (at < 0) return;
        (_page, _selectedId) = (at / PageSize + 1, id);
        Slice();
        SayPage();
    }

    /// <summary>Chooses the entry shown in full, as a click on its row or moving to it with the keys does.</summary>
    internal void Pick(Guid id)
    {
        if (id == _selectedId || !_onPage.Any(r => IdOf(r) == id)) return;
        _selectedId = id;
        StateHasChanged();
    }

    /// <summary>
    /// J on the page's last entry or K on its first (RowKeys): the next or previous page of the list, its first entry
    /// selected after J and its last after K, and the address says the page.
    /// </summary>
    internal void Turn(RowMove move)
    {
        var page = _page + (move == RowMove.NextPage ? 1 : -1);
        if (move == RowMove.Within || page < 1 || page > Pages) return;
        (_page, _selectedId) = (page, null);
        Slice();
        if (move == RowMove.PreviousPage && _onPage.Count > 0) _selectedId = IdOf(_onPage[^1]);
        SayPage();
        StateHasChanged();
    }

    /// <summary>How many pages the list has.</summary>
    protected int Pages => TableView.Pages(_shown.Count, PageSize);

    /// <summary>After a save: the entry saved is the one shown, once the rows are read again.</summary>
    protected void Reveal(Guid id) => _reveal = id;

    // "1–20 of 34 playbooks", or "12 playbooks" on one page.
    protected string CountText => TableView.Count((_page - 1) * PageSize + 1, _onPage.Count, _shown.Count, null, false, Names.One, Names.Many);

    // A page's address: this tab's, with no entry named.
    protected string PageHref(int page) => Address(page, null);

    /// <summary>The address of what's shown: this page of the list with the selected entry, or no entry when nothing is.</summary>
    internal string AddressShown => Address(_page, _selectedId);

    /// <summary>A form opened (New, Edit): its first box takes focus.</summary>
    protected void FocusForm() => _focus = FocusOn.Form;

    /// <summary>
    /// A form closed (Save, Cancel) or the entry shown left the list (Delete, Retire): focus goes to the selected row,
    /// so J and K carry on from it, or to the empty state when the list has none.
    /// </summary>
    protected void FocusList() => _focus = FocusOn.List;

    // Once a render is on screen: focus where a step left it owing (not while the step still runs, when the form or the
    // old row is still drawn), and the address made to name the entry shown, in place (tideRows.remember, as every list
    // remembers its row), when it names another or one that's gone.
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_focus is { } to && !_busy)
        {
            _focus = null;
            if (to == FocusOn.Form) await _formStart.TryFocusAsync(orGone: true);
            else if (_onPage.Count == 0) await _empty.TryFocusAsync(orGone: true);
            else if (_browser is not null) await _browser.FocusSelectedAsync();
        }
        if (_selectedId == _rowSaid || _rows is null) return;
        _rowSaid = _selectedId;
        try { await JS.InvokeVoidAsync("tideRows.remember", AddressShown); }
        catch (JSDisconnectedException) { /* the tab has gone */ }
        catch (InvalidOperationException) { /* not rendered interactively */ }
    }

    private string Address(int page, Guid? row) =>
        Nav.GetUriWithQueryParameters(new Dictionary<string, object?> { ["page"] = page == 1 ? null : page, ["row"] = row });

    // Clear: the page's own filters (the words are the Library's search's, cleared in its box); retired ones stay shown
    // or hidden as chosen.
    internal void ClearFilters()
    {
        ClearOwnFilters();
        Refilter();
    }

    // A filter changed: the first page, and the address says so.
    internal void Refilter()
    {
        _page = 1;
        Filter();
        SayPage();
    }

    // A colleague saved, changed, retired or deleted one: re-read in place. An edit under way keeps what's typed; its save
    // is refused as stale if that one was the one changed. When the entry being edited was the one shown and a change
    // moved it to another page (one added before it pushed it on), the page goes with it; one that's gone, or that a
    // filter now leaves out, keeps its form above the list (EditingAway).
    internal async Task OnChangedElsewhere(IReadOnlyList<Change> heard)
    {
        if (_busy) return;   // a step is under way, and it reads theirs when it's done
        var shown = EditingId is { } editing && !EditingAway ? editing : (Guid?)null;
        await Load();
        if (shown is { } id && EditingAway) Show(id);
        Toasts.Show($"{LiveChanges.Who(heard)} updated the {Names.Library}.");
        StateHasChanged();
    }

    protected async Task Load()
    {
        try { _rows = await ReadAsync(_showRetired); }
        catch (Exception ex) { Toasts.Error(UserFacingError.For(ex)); }
        Filter();
        if (_reveal is { } id)
        {
            _reveal = null;
            Show(id);
        }
    }

    protected async Task ToggleRetired(ChangeEventArgs e)
    {
        _showRetired = e.Value is true;
        _page = 1;
        await Load();
        SayPage();
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
            await OnChanged.InvokeAsync();
        }
        catch (Exception ex) { Toasts.Error(UserFacingError.For(ex)); }
        finally { _busy = false; }
    }

    /// <summary>The rows read again after a step elsewhere used an entry (added to an action from the rail).</summary>
    protected Task Reread() => Run(() => Task.CompletedTask);

    // Retires one (it's no longer offered on actions) or restores it, and says so.
    protected Task Retiring(string name, bool retired, Func<Task> change) => Run(async () =>
    {
        await change();
        Toasts.Show(retired ? $"{name} retired. It's no longer offered on actions." : $"{name} restored.");
        if (retired && !_showRetired) FocusList();   // it left the list, and its buttons with it
    });

    /// <summary>An entry's More menu: Retire (or Restore) for whoever keeps the library, Delete for whoever may delete it.
    /// Each runs the library's own step, with its checks and its confirmation.</summary>
    protected IReadOnlyList<MenuButton.Item> MoreMenu(bool retired, bool canRetire, Func<Task> retire, bool canDelete, Func<Task> delete)
    {
        var items = new List<MenuButton.Item>();
        if (canRetire) items.Add(new(retired ? "Restore" : "Retire", retired ? "bi-arrow-counterclockwise" : "bi-archive", EventCallback.Factory.Create(this, retire)));
        if (canDelete) items.Add(new("Delete", "bi-trash", EventCallback.Factory.Create(this, delete)));
        return items;
    }

    /// <summary>The entry's footnote's last words: what's under its More menu (" Retire and Delete are under More."), or
    /// nothing when it has none.</summary>
    protected static string UnderMore(IReadOnlyList<MenuButton.Item> more) => more.Count == 0 ? ""
        : $" {string.Join(" and ", more.Select(i => i.Label))} {(more.Count == 1 ? "is" : "are")} under More.";

    // "Not used yet", or how often it was ("Used on 3 actions", "Used for 1 email").
    protected static string Used(int count, string how, string noun) => count == 0 ? "Not used yet" : $"Used {how} {Ui.Plural(count, noun)}";

    /// <summary>The rail's "Saved by" fact: who saved it, and the day.</summary>
    protected LibraryRail.LibraryFact SavedBy<T>(T entry) where T : Domain.Common.AuditableEntity, Domain.Common.ITeamLibraryEntry =>
        new("Saved by", $"{(entry.CreatedByName.Length > 0 ? entry.CreatedByName : "someone")}, {Clock.ToLocal(entry.CreatedAtUtc).ToString("MMM d", System.Globalization.CultureInfo.InvariantCulture)}");

    /// <summary>The rail's "Used" fact: "Not yet", or how often ("On 3 actions", "For 1 email").</summary>
    protected static LibraryRail.LibraryFact UsedFact(int count, string how, string noun) =>
        new("Used", count == 0 ? "Not yet" : $"{how} {Ui.Plural(count, noun)}");

    // "Saved by …, changed by …". One saved before the library kept names has none for who saved it.
    protected string Provenance<T>(T entry) where T : Domain.Common.AuditableEntity, Domain.Common.ITeamLibraryEntry =>
        Ui.Provenance(Clock, entry, entry.CreatedByName.Length > 0 ? entry.CreatedByName : "someone", entry.ModifiedByName);
}
