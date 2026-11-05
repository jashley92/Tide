namespace Tide.Web.Services;

/// <summary>
/// Something a page in present mode can open over the slide that the presenter can show to the room (an Our stack
/// product's card, by its id): what this page has open, whether it opened it alone, and the presenter's open one it last
/// went along with, so one a follower closed for themselves stays closed until the presenter opens another.
/// </summary>
public sealed class MeetingPopup
{
    private Guid? _seen;

    /// <summary>What this page has open, if anything.</summary>
    public Guid? Shown { get; private set; }

    /// <summary>Opened by this page alone (a follower's own look), not the presenter's shown to the room.</summary>
    public bool Own { get; private set; }

    /// <summary>
    /// This page opens <paramref name="id"/>: the presenter's opening shows it to the room (and is the one seen); anyone
    /// else's in a meeting is theirs alone.
    /// </summary>
    public void Open(Guid id, bool presenting, bool inMeeting)
    {
        Shown = id;
        Own = inMeeting && !presenting;
        if (presenting) _seen = id;
    }

    /// <summary>This page closes it (or it goes with the slide it was open over).</summary>
    public void Close() => (Shown, Own) = (null, false);

    /// <summary>
    /// Closes it and forgets the presenter's: a move to another slide (back on the presenter's, theirs shows again), a
    /// hand-off, the meeting's end, a new day.
    /// </summary>
    public void Reset() => (Shown, Own, _seen) = (null, false, null);

    /// <summary>
    /// Goes along with what the presenter has open (<paramref name="shared"/>): always while held by their lock; otherwise
    /// when following, unless this page closed that one itself. Their closing it closes it here, unless this page opened
    /// its own. True when what's shown changed.
    /// </summary>
    public bool Sync(Guid? shared, bool following, bool held)
    {
        var was = Shown;
        if (held)
        {
            (_seen, Own, Shown) = (shared, false, shared);
            return was != Shown;
        }
        if (!following || shared == _seen) return false;
        _seen = shared;
        if (shared is not null) (Own, Shown) = (false, shared);
        else if (!Own) Shown = null;
        return was != Shown;
    }
}
