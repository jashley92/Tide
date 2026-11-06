namespace Tide.Web.Services;

/// <summary>
/// A fraction of the way down a slide's body: scrollTop / (scrollHeight - clientHeight), 0 at the top and 1 at the
/// bottom, so windows of different sizes land roughly on the same lines.
/// </summary>
public static class ScrollFraction
{
    /// <summary><paramref name="fraction"/> kept between 0 and 1 and to four places; anything that isn't a number is the top.</summary>
    public static double Clamp(double fraction) => double.IsFinite(fraction) ? Math.Round(Math.Clamp(fraction, 0, 1), 4) : 0;
}

/// <summary>
/// One follower's following of the presenter's scroll. Someone following scrolls with the presenter on the same slide;
/// scrolling by hand pauses that for the slide they're on only (they still follow the presenter's slide changes), until
/// they go back to the presenter's place, or the slide changes. In a meeting the presenter has locked, scrolling by hand
/// is still allowed (it's only looking), but the presenter's next scroll brings them back.
/// </summary>
public sealed class ScrollFollow
{
    private string? _pausedOn;

    /// <summary>Whether following the presenter's scroll is paused on the slide <paramref name="slideKey"/>. A pause is for
    /// one slide: on any other, it isn't.</summary>
    public bool Paused(string? slideKey) => slideKey is not null && _pausedOn == slideKey;

    /// <summary>This page now shows the slide <paramref name="slideKey"/>: a pause on any other is over, and doesn't come
    /// back with it.</summary>
    public void Showing(string? slideKey)
    {
        if (_pausedOn != slideKey) _pausedOn = null;
    }

    /// <summary>The person scrolled the slide <paramref name="slideKey"/> by hand: following the presenter's scroll pauses
    /// on it. Whether that's news (it wasn't paused there already).</summary>
    public bool ScrolledByHand(string? slideKey)
    {
        if (slideKey is null || _pausedOn == slideKey) return false;
        _pausedOn = slideKey;
        return true;
    }

    /// <summary>Back to the presenter's place: following their scroll again.</summary>
    public void Resume() => _pausedOn = null;

    /// <summary>
    /// The presenter scrolled their slide <paramref name="presenterKey"/> to <paramref name="fraction"/>; this page shows
    /// <paramref name="myKey"/>. Where this page scrolls to, or null to stay put: not following, on another slide, or
    /// paused (unless <paramref name="locked"/>, when the presenter's scroll ends the pause).
    /// </summary>
    public double? Heard(string? presenterKey, double fraction, string? myKey, bool following, bool locked)
    {
        if (!following || presenterKey is null || presenterKey != myKey) return null;
        if (Paused(myKey))
        {
            if (!locked) return null;
            _pausedOn = null;
        }
        return fraction;   // clamped once, where the meeting keeps it (LiveMeetings.Scroll)
    }
}
