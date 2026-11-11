using Microsoft.AspNetCore.Components;
using Tide.Web.Components.Shared;

namespace Tide.Web.Components.Today;

/// <summary>
/// Keeps a keyboard user's place on a Today card when its list changes under them: focus goes to the same row's button
/// while the row still offers it, else to the next row that offers it, else the one before, else to the card's heading.
/// <list type="bullet">
/// <item>After a row's button did its work (Take it, Add) and the list was read again. The card reads the rows that
/// offer the button as it's pressed, and once the page's work is done notes them (<see cref="Acted"/>): not before, as
/// the page draws the card while it works, with the button disabled.</item>
/// <item>After a colleague's change took away the row that had focus. The card says where focus is (<see cref="Focus"/>)
/// and what it lists each time its rows arrive (<see cref="Listed"/>); focus elsewhere on the page is left alone.</item>
/// </list>
/// Drawn again, the card moves focus (<see cref="FocusAsync"/>).
/// </summary>
public sealed class KeepPlace
{
    private (IReadOnlyList<Guid> Before, Guid Acted)? _pending;
    private IReadOnlyList<Guid> _listed = [];
    private Guid? _focused;

    /// <summary>The button on <paramref name="acted"/>'s row was pressed, while these rows offered it.</summary>
    public void Acted(IEnumerable<Guid> offering, Guid acted) => _pending = ([.. offering], acted);

    /// <summary>Focus came into <paramref name="row"/> (<paramref name="into"/>), or left it.</summary>
    public void Focus(Guid row, bool into)
    {
        if (into) _focused = row;
        else if (_focused == row) _focused = null;
    }

    /// <summary>The rows offering the button, as they arrive: when the one with focus has gone, focus moves on.</summary>
    public void Listed(IEnumerable<Guid> offering)
    {
        var now = offering.ToList();
        if (_pending is null && _focused is { } f && _listed.Contains(f) && !now.Contains(f))
        {
            _pending = (_listed, f);
            _focused = null;
        }
        _listed = now;
    }

    /// <summary>Once the card is drawn again: focus to the button <paramref name="button"/> gives for the row found, or
    /// to <paramref name="heading"/>. Does nothing when there's no place to keep.</summary>
    public async Task FocusAsync(IEnumerable<Guid> offering, Func<Guid, ElementReference?> button, ElementReference heading)
    {
        if (_pending is not { } p) return;
        _pending = null;
        var to = After(p.Before, p.Acted, [.. offering]) is { } id ? button(id) : null;
        await (to ?? heading).TryFocusAsync(orGone: true);
    }

    /// <summary>Which row's button gets focus: the acted one's while it still offers it, else the next that does, else
    /// the one before; null when no row does.</summary>
    public static Guid? After(IReadOnlyList<Guid> before, Guid acted, IReadOnlyCollection<Guid> now)
    {
        if (now.Contains(acted)) return acted;
        var at = before.ToList().IndexOf(acted);
        Guid? First(IEnumerable<Guid> ids) => ids.Where(now.Contains).Select(id => (Guid?)id).FirstOrDefault();
        return First(before.Skip(at + 1)) ?? First(before.Take(Math.Max(at, 0)).Reverse());
    }
}
