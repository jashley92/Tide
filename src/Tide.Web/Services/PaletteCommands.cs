using Tide.Application.Abstractions;
using Tide.Domain.Enums;

namespace Tide.Web.Services;

/// <summary>
/// A verb the command palette can run: what it says, other words it's found by, the permission it needs (null when the
/// page's own state already decides, as an action page's flows do), where it applies ("This article", "Today",
/// "Triage", "A-12"), and what it does: the same method, or the same dialog, as the page's own button. Nothing here
/// grants anything; the service it calls still checks (ADR 0002).
/// </summary>
public sealed record PaletteCommand(string Label, string Keywords, Permission? Permission, string Scope, Func<Task> Run, string Icon = "bi-lightning-charge");

/// <summary>
/// The verbs the page on screen offers the command palette (one per circuit). A page registers a list, read each time
/// the palette opens or filters so it follows the page's state (a draft that's now on the brief, a row selected), and
/// disposes the registration when it goes, so a verb is only ever offered on its own page.
/// </summary>
public sealed class PaletteCommands
{
    private readonly Lock _lock = new();
    private readonly List<Func<IEnumerable<PaletteCommand>>> _sources = [];

    /// <summary>Offers <paramref name="verbs"/> until the result is disposed (the page's Dispose).</summary>
    public IDisposable Register(Func<IEnumerable<PaletteCommand>> verbs)
    {
        lock (_lock) _sources.Add(verbs);
        return new Registration(this, verbs);
    }

    /// <summary>The verbs offered now that <paramref name="user"/> holds the permission for.</summary>
    public IReadOnlyList<PaletteCommand> For(ICurrentUser user)
    {
        Func<IEnumerable<PaletteCommand>>[] sources;
        lock (_lock) sources = [.. _sources];
        return sources.SelectMany(s => s()).Where(c => c.Permission is not { } p || user.Has(p)).ToList();
    }

    /// <summary>
    /// A page's method as a verb: run, then the page drawn again, as a click on its own button would have it (the palette
    /// is another component, so its event doesn't redraw the page).
    /// </summary>
    public static Func<Task> Then(Func<Task> run, Action redraw) => async () =>
    {
        try { await run(); }
        finally { redraw(); }
    };

    private sealed class Registration(PaletteCommands owner, Func<IEnumerable<PaletteCommand>> verbs) : IDisposable
    {
        public void Dispose()
        {
            lock (owner._lock) owner._sources.Remove(verbs);
        }
    }
}
