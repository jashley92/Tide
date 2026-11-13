using System.Globalization;
using Tide.Application.Abstractions;
using Tide.Application.Org;
using Tide.Application.Preferences;

namespace Tide.Application.Operations;

/// <summary>
/// Whether a page's own "Show me around" link, under its subtitle, shows for the signed-in person. It's a way in for
/// someone new to the page, not a fixture: it shows only while they haven't run that page's guide (to the end or part
/// way) or put the link away with its ×, and only within the organization's window (<see cref="OrgClock.GuideLinkDays"/>)
/// of their first sign-in, or of the day they saw What's new for a release whose highlights changed that page (it has a
/// tip in that release). Help's "Show page guide links" turns them on regardless of the window, until put away again, or off.
/// The keyboard shortcuts' "Show me around this page" is always there. Kept in the person's preferences.
/// </summary>
public sealed class GuideLinkService(UserPreferenceService prefs, ICurrentUser user, WhatsNewService whatsNew, OrgClock clock)
{
    /// <summary>Where a page's guide link stands for the person: "" (the window decides), "on" or "off" (Help's switch).</summary>
    public const string Links = "guides.links";

    /// <summary>The day the person first signed in, as the user mirror records it ("yyyy-MM-dd").</summary>
    public const string Since = "guides.since";

    /// <summary>The release whose What's new the person last saw, and the day they saw it ("1.13.0@2026-11-13").</summary>
    public const string Release = "guides.release";

    /// <summary>A page guide's own: "done" once its guide has run, or its link was put away.</summary>
    public static string DoneKey(Guide guide) => "guide." + guide.Key;

    /// <summary>The links shown regardless of the window, hidden, or as the window says; a page guide's "done".</summary>
    public const string On = "on", Off = "off", DoneValue = "done";

    /// <summary>A day as these preferences keep it ("2026-11-13").</summary>
    public static string Day(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static bool TryDay(string value, out DateOnly day) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day);

    /// <summary>A date preference's value: a day, or nothing.</summary>
    public static bool IsDayOrNone(string value) => value.Length == 0 || TryDay(value, out _);

    /// <summary>A release preference's value: "version@day", or nothing.</summary>
    public static bool IsReleaseOrNone(string value) =>
        value.Length == 0 || value.Split('@') is [var v, var d] && ReleaseVersion.Parse(v) is not null && TryDay(d, out _);

    /// <summary>Whether the link to <paramref name="guide"/> shows on its page, <paramref name="page"/> (as a link from the
    /// app's root has it, without its query).</summary>
    public async Task<bool> ShowsAsync(Guide guide, string page, CancellationToken ct = default)
    {
        if (!user.Has(guide.Needs)) return false;
        var mine = await prefs.GetAsync([Links, Since, Release, DoneKey(guide)], ct);
        if (mine[Links] == Off || mine[DoneKey(guide)] == DoneValue) return false;
        if (mine[Links] == On) return true;
        var today = clock.Today;
        bool Within(string day) => TryDay(day, out var from) && today.DayNumber - from.DayNumber < clock.GuideLinkDays;
        if (Within(mine[Since])) return true;
        // A release that changed the page: from the day the person saw its What's new (WhatsNewService.MarkSeenAsync).
        if (whatsNew.Running is not { } running || PageTips.For(page)?.Version != running) return false;
        return mine[Release].Split('@') is [var version, var since] && version == running.ToString() && Within(since);
    }

    /// <summary>The guide has run (to the end or part way), or its link was put away: it doesn't show again.</summary>
    public Task DoneAsync(Guide guide, CancellationToken ct = default) =>
        guide.Travels || guide.Rehearsal || !user.Has(guide.Needs) ? Task.CompletedTask : prefs.SetAsync(DoneKey(guide), DoneValue, ct);

    /// <summary>Whether Help's "Show page guide links" is on: the links aren't switched off.</summary>
    public async Task<bool> SwitchedOnAsync(CancellationToken ct = default) => await prefs.GetAsync(Links, ct) != Off;

    /// <summary>Help's "Show page guide links": on brings every page's link back, whatever the window, until put away
    /// again; off hides them all.</summary>
    public async Task SwitchAsync(bool on, CancellationToken ct = default)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal) { [Links] = on ? On : Off };
        if (on)
            foreach (var g in Guides.OnPages.Where(g => user.Has(g.Needs))) values[DoneKey(g)] = "";
        await prefs.SetAsync(values, ct);
    }
}
