using System.Globalization;
using Tide.Application.Content;
using Tide.Application.Org;

namespace Tide.Application.Intake;

/// <summary>Pull all reaching a source: its place in the run ("3 of 9") and its name.</summary>
public sealed record PullAllStep(int Number, int Of, Guid Id, string Name)
{
    /// <summary>"Pulling 3 of 9: Sample news…"</summary>
    public string Text => $"Pulling {Number} of {Of}: {Name}…";
}

/// <summary>
/// What a Pull all did: the sources pulled and the new items they brought, those skipped (pulled within the pull-now
/// window, or being pulled by someone else), those whose pull failed, and how many weren't reached because the run was
/// stopped (the page that started it was left).
/// </summary>
public sealed record PullAllResult(int Pulled, int New, IReadOnlyList<string> Recent, IReadOnlyList<string> Busy, IReadOnlyList<string> Failed,
    int NotReached)
{
    /// <summary>Whether any source's pull failed (the toast warns).</summary>
    public bool SomeFailed => Failed.Count > 0;

    /// <summary>Whether every source tried failed (the toast is an error).</summary>
    public bool AllFailed => SomeFailed && Pulled == 0;

    /// <summary>"Pulled 7 · 12 new items · 2 skipped (pulled in the last 15 min) · 1 failed: Sample news".</summary>
    public string Summary(TimeSpan window)
    {
        var tried = Pulled + Failed.Count;
        List<string> parts = tried == 0 && NotReached == 0
            ? ["Nothing to pull"]
            : [$"Pulled {Pulled}", PullTimes.NewItems(New)];
        if (Recent.Count > 0) parts.Add($"{Recent.Count} skipped (pulled in the last {PullTimes.Wait(window)})");
        if (Busy.Count > 0) parts.Add($"{Busy.Count} skipped (being pulled)");
        if (Failed.Count > 0) parts.Add($"{Failed.Count} failed: {string.Join(", ", Failed)}");
        if (NotReached > 0) parts.Add($"stopped: {NotReached} not pulled");
        return string.Join(" · ", parts);
    }
}

/// <summary>
/// Whether the polled feeds have been pulled since this working day's cutoff (<see cref="IntakeOptions.PulledBy"/>), for
/// Today's Triage step: how many there are, how many weren't pulled since it (a failing feed among them: it was tried,
/// but brought nothing), the failing ones by name, and the latest pull of any. <paramref name="OnlyFailing"/>: every other
/// feed was pulled since, and the failing ones were tried since too, so pulling again now is no help.
/// </summary>
public sealed record FeedFreshness(int Sources, int NotPulled, IReadOnlyList<string> Failing, DateTimeOffset CutoffUtc,
    DateTimeOffset? LastPullUtc, bool OnlyFailing = false)
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>
    /// Today's cutoff, as an instant: <paramref name="pulledBy"/> on today in <paramref name="zone"/>. None on a day off
    /// (a weekend or a holiday: nobody is nagged then), and none before it (the feeds aren't expected yet, and yesterday's
    /// cutoff would only nag about what the morning's polls haven't had a chance to take). A time the clocks skip that day
    /// is when they change.
    /// </summary>
    public static DateTimeOffset? Cutoff(DateTimeOffset nowUtc, TimeZoneInfo zone, WorkingDays days, TimeOnly pulledBy)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(nowUtc, zone).DateTime);
        if (!days.IsWorkingDay(today)) return null;
        var cutoff = OrgClock.At(zone, today, pulledBy);
        return nowUtc < cutoff ? null : cutoff;
    }

    /// <summary>What's to say of <paramref name="sources"/> against the cutoff; null when nothing is (none are polled, or
    /// every one has been pulled since it and none is failing).</summary>
    public static FeedFreshness? Of(IReadOnlyCollection<PolledSource> sources, DateTimeOffset cutoffUtc)
    {
        bool Untried(PolledSource s) => s.LastPolledAtUtc is not { } at || at < cutoffUtc;
        var notPulled = sources.Count(s => s.Failing || Untried(s));
        return notPulled == 0 ? null
            : new FeedFreshness(sources.Count, notPulled, [.. sources.Where(s => s.Failing).Select(s => s.Name)], cutoffUtc,
                sources.Max(s => s.LastPolledAtUtc), OnlyFailing: !sources.Any(Untried));
    }

    /// <summary>"8 of 9 feeds not pulled since 8:00 AM (1 failing) · last pull 6:40 PM yesterday"; with only failing feeds
    /// left, "1 feed failing: Sample news".</summary>
    public string Text(TimeZoneInfo zone, DateTimeOffset nowUtc)
    {
        if (OnlyFailing) return $"{Words.Plural(Failing.Count, "feed")} failing: {string.Join(", ", Failing)}";
        var since = $"{NotPulled} of {Words.Plural(Sources, "feed")} not pulled since {Clock(TimeZoneInfo.ConvertTime(CutoffUtc, zone))}"
            + (Failing.Count > 0 ? $" ({Failing.Count} failing)" : "");
        var last = LastPullUtc is { } at ? "last pull " + When(TimeZoneInfo.ConvertTime(at, zone), TimeZoneInfo.ConvertTime(nowUtc, zone)) : "never pulled";
        return $"{since} · {last}";
    }

    private static string Clock(DateTimeOffset local) => local.ToString("h:mm tt", Invariant);

    // "7:55 AM" today, "6:40 PM yesterday", "Fri 6:40 PM" within the week, "Oct 20, 9:05 AM" before that.
    private static string When(DateTimeOffset local, DateTimeOffset now)
    {
        var days = DateOnly.FromDateTime(now.DateTime).DayNumber - DateOnly.FromDateTime(local.DateTime).DayNumber;
        return days switch
        {
            <= 0 => Clock(local),
            1 => Clock(local) + " yesterday",
            < 7 => local.ToString("ddd ", Invariant) + Clock(local),
            _ => local.ToString("MMM d, ", Invariant) + Clock(local),
        };
    }
}
