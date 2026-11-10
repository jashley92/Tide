using System.Globalization;
using Tide.Application.Abstractions;
using Tide.Application.Briefs;
using Tide.Application.Distribution;
using Tide.Domain.Enums;
using Tide.Application.Org;
using Tide.Application.Security;
using Tide.Application.Settings;
using Tide.Web.Components.Pages;

namespace Tide.Web.Services;

/// <summary>
/// What the top bar's brief chip says: the brief Today shows (<see cref="Day"/>), when it meets, and the step it's on
/// (<see cref="BriefSteps.On"/> over the same steps as Today's strip). <see cref="Step"/> is null while the brief
/// hasn't been started.
/// </summary>
public sealed record BriefChipView(DateOnly Day, Guid? BriefId, string Meets, int Number, int Of, BriefStep? Step)
{
    /// <summary>"step 3 of 5 · Order"; what Send says once it's done ("Sent"); "not started" with no brief yet.</summary>
    public string Where => Step switch
    {
        null => "not started",
        { State: StepState.Done } done => done.Detail,
        _ => $"step {Number} of {Of} · {Step.Label}",
    };

    /// <summary>The same in a narrow window: "3/5", "done", or nothing with no brief yet.</summary>
    public string? WhereShort => Step switch
    {
        null => null,
        { State: StepState.Done } => "done",
        _ => $"{Number}/{Of}",
    };

    /// <summary>When the brief meets, as the chip says it: "10:30 AM", with the day in front ("Mon 10:30 AM") when
    /// that isn't today (a weekend or a holiday, when Today shows the next brief).</summary>
    public static string MeetsAt(OrgClock clock, DateOnly day) =>
        (day == clock.Today ? "" : day.ToString("ddd ", CultureInfo.InvariantCulture))
        + clock.MeetingTime.ToString("h:mm tt", CultureInfo.InvariantCulture);
}

/// <summary>
/// Where today's brief stands, for the top bar's chip, read once per circuit and kept. The top bar is drawn on every
/// page, so a navigation never reads the database for it: the reading is kept until the change feed says the brief, one
/// of its articles or its sends changed (the topics Today's own page re-reads on), or the day turns. It is for those
/// who read the team edition and write the brief, the people Today shows the step strip to; anyone else gets none.
/// </summary>
public sealed class BriefChipSource : IDisposable
{
    private readonly BriefService _briefs;
    private readonly DistributionService _sends;
    private readonly DistributionSettings _ceilings;
    private readonly OrgClock _clock;
    private readonly ICurrentUser _user;
    private readonly ChangeFeed _feed;
    private readonly ILogger<BriefChipSource> _log;

    private sealed record Kept(long Version, DateOnly Today, DateOnly Day, Task<BriefChipView> Reading);

    private Kept? _kept;
    private long _version;
    private volatile HashSet<string> _follow = [];

    public BriefChipSource(BriefService briefs, DistributionService sends, DistributionSettings ceilings, OrgClock clock, ICurrentUser user,
        ChangeFeed feed, ILogger<BriefChipSource> log)
    {
        (_briefs, _sends, _ceilings, _clock, _user, _feed, _log) = (briefs, sends, ceilings, clock, user, feed, log);
        _feed.Changed += OnChanged;
    }

    /// <summary>Whether <paramref name="change"/> is to what the chip was read from. Safe on the publisher's thread.</summary>
    public bool Follows(Change change) => _follow.Contains(change.Topic);

    // On the publisher's thread, from any circuit (this one too: Today's own save moves the chip): only marks the
    // reading as out of date. The next GetAsync reads again.
    private void OnChanged(Change change)
    {
        if (Follows(change)) Interlocked.Increment(ref _version);
    }

    /// <summary>The chip for the person signed in, or null when they get none. Reads the database only when nothing is
    /// kept, or what's kept is out of date. The top bar sits outside the page's error boundary, so a failed read gives
    /// no chip rather than ending the session, and isn't kept: the next page tries again.</summary>
    public async Task<BriefChipView?> GetAsync()
    {
        if (!_user.Has(Permission.ReadTeamEdition) || !_user.Has(Permission.WriteArticles)) return null;
        var (version, today, day) = (Interlocked.Read(ref _version), _clock.Today, _clock.CurrentBriefDate);
        if (_kept is not { } kept || kept.Version != version || kept.Today != today || kept.Day != day)
            _kept = kept = new Kept(version, today, day, ReadAsync(day));
        try { return await kept.Reading; }
        catch (Exception ex)
        {
            if (ReferenceEquals(_kept, kept))
            {
                _kept = null;
                _log.LogWarning(ex, "The top bar's brief chip couldn't be read; it's left out until a read works.");
            }
            return null;
        }
    }

    private async Task<BriefChipView> ReadAsync(DateOnly day)
    {
        var meets = BriefChipView.MeetsAt(_clock, day);
        var brief = await _briefs.GetAsync(day);
        // The topics Today's page re-reads on: the day's brief (or its start), each article on it, and its sends
        // (which publish as the brief's).
        _follow = BriefPage.Follow(brief, day);
        if (brief is null) return new BriefChipView(day, null, meets, 0, 0, null);
        var (publicMax, teamMax) = await _ceilings.GetAsync();
        // Today's strip, without the feed's count and the day's numbers: neither moves the current step (BriefSteps.On).
        var steps = BriefSteps.For(brief, newInFeed: null, missingNumbers: 0, meets, await _sends.SendsAsync(day),
            BriefSteps.NothingToSend(brief, teamMax, publicMax), _sends.SharePointRequired);
        var (number, step) = BriefSteps.On(steps);
        return new BriefChipView(day, brief.Id, meets, number, steps.Count, step);
    }

    public void Dispose() => _feed.Changed -= OnChanged;
}
