using System.Globalization;

namespace Tide.Application.Actions;

/// <summary>A parent action's parts at a glance, kept pure so it's tested on its own.</summary>
public static class ActionRollup
{
    /// <summary>
    /// "3 of 5 done · 1 overdue · next due Nov 20": how many of <paramref name="total"/> parts are closed (Done, Cancelled
    /// or Not applicable: either way there's nothing left to do), how many open ones are overdue, and the soonest an open one
    /// is due. Empty with no parts.
    /// </summary>
    public static string Text(int total, int closed, int overdue, DateOnly? nextDue)
    {
        if (total <= 0) return "";
        var parts = new List<string> { $"{closed} of {total} done" };
        if (overdue > 0) parts.Add($"{overdue} overdue");
        if (closed < total && nextDue is { } d) parts.Add("next due " + d.ToString("MMM d", CultureInfo.InvariantCulture));
        return string.Join(" · ", parts);
    }

    /// <summary>Whether a part due <paramref name="child"/> is due after its parent (<paramref name="parent"/>): it's
    /// allowed, but said, since the parent can't be done on time then.</summary>
    public static bool LaterThanParent(DateOnly? parent, DateOnly? child) => parent is { } p && child is { } c && c > p;

    /// <summary>What a part due after its parent is warned with.</summary>
    public static string LaterText(DateOnly parent) =>
        "Due after its parent (" + ActionService.Day(parent) + "), so the parent can't be done on time.";

    /// <summary>A split part's title: the parent's, with the team's name ("Patch the VPN – Network Engineering"), cut to fit.</summary>
    public static string PartTitle(string parent, string team, int max = Domain.Entities.ActionItem.MaxTitle)
    {
        // A team's name is at most 100 characters and a title 300, so the parent's always keeps some of its own.
        var suffix = " – " + team;
        if (parent.Length + suffix.Length <= max) return parent + suffix;
        return parent[..(max - suffix.Length - 1)].TrimEnd() + "…" + suffix;
    }
}
