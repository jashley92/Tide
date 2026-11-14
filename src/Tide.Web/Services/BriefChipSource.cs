using System.Globalization;
using Tide.Application.Abstractions;
using Tide.Application.Briefs;
using Tide.Application.Distribution;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Application.Org;
using Tide.Application.Security;
using Tide.Application.Settings;
using Tide.Web.Components.Pages;

namespace Tide.Web.Services;

/// <summary>
/// What the top bar's brief chip says: the brief Today shows (<see cref="Day"/>), when it meets, and the step it's on
/// (<see cref="BriefSteps.On"/> over the same steps as Today's strip). <see cref="Step"/> is null while the brief
/// hasn't been started. <see cref="Met"/> is the strip's own fact that the meeting is over (its Present step is done):
/// from then the chip says "presented" where the time was, as Today says when the brief met. With one of the two
/// editions gone and the other still to go (the Send step's <see cref="BriefStep.PartlySent"/>), the chip says
/// "1 of 2 sent", as Today's header and strip do.
/// </summary>
public sealed record BriefChipView(DateOnly Day, Guid? BriefId, string Meets, int Number, int Of, BriefStep? Step, bool Met = false)
{
    // Send done, or one edition of two gone: the chip says what the strip's last step says ("Sent", "Distributed", "Not
    // sent", "1 of 2 sent").
    private bool Finished => Step is { State: StepState.Done } or { PartlySent: true };

    /// <summary>What's seen in a wide window: "Brief 10:30 AM · step 3 of 5 · Order", "Brief 10:30 AM · not started",
    /// "Brief presented · step 5 of 5 · Send", "Brief · Sent", "Brief · 1 of 2 sent".</summary>
    public string Full => Step switch
    {
        null => $"Brief {Meets} · not started",
        _ when Finished => $"Brief · {Said}",
        _ => $"Brief {(Met ? "presented" : Meets)} · step {Number} of {Of} · {Step.Label}",
    };

    /// <summary>What's seen in a narrow one: "10:30 AM · 3/5", "10:30 AM", "Presented · 5/5", "Sent". On a day off,
    /// when the brief meets on another day, the step names the day rather than the time ("Mon · 3/5"), so the narrow chip
    /// is never longer than a working day's; the full wording is its name and title.</summary>
    public string Short => Step switch
    {
        null => Meets,
        _ when Finished => Said.Split(" · ")[0],
        _ => $"{(Met ? "Presented" : MeetsDay)} · {Number}/{Of}",
    };

    // "Mon" from "Mon 10:30 AM" (MeetsAt with the day in front); the time alone otherwise.
    private string MeetsDay => Meets.Split(' ') is [var day, _, _] ? day : Meets;

    /// <summary>The link's name at every width, which the short text alone wouldn't carry: "Brief at 10:30 AM, step 3
    /// of 5, Order".</summary>
    public string Name => Step switch
    {
        null => $"Brief at {Meets}, not started",
        _ when Finished => $"Brief, {Said.Replace(" · ", ", ")}",
        _ => $"Brief {(Met ? "presented" : "at " + Meets)}, step {Number} of {Of}, {Step.Label}",
    };

    // What a finished chip says: the strip's last step, or "1 of 2 sent" while the other edition is still to go (even
    // when the strip has a failed send of it to point out).
    private string Said => Step!.PartlySent ? BriefSteps.PartlySentLabel : Step.Detail;

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
    // reading as out of date. The next GetAsync reads again. While a read is under way, what isn't followed is noted
    // for it: the read learns which brief to follow only once it has it, and a change to that brief may land first.
    private void OnChanged(Change change)
    {
        lock (_gate)
        {
            if (Follows(change)) Interlocked.Increment(ref _version);
            else foreach (var heard in _reading) heard.Add(change.Topic);
        }
    }

    private readonly Lock _gate = new();
    private readonly List<List<string>> _reading = [];

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
            if (!ReferenceEquals(_kept, kept)) return null;
            _kept = null;
            // A read the circuit didn't outlive (a quick navigation away, a closed tab: its services were disposed under
            // it) isn't a fault: nothing is left to show the chip.
            if (!_disposed && ex is not ObjectDisposedException)
                _log.LogWarning(ex, "The top bar's brief chip couldn't be read; it's left out until a read works.");
            return null;
        }
    }

    private async Task<BriefChipView> ReadAsync(DateOnly day)
    {
        var meets = BriefChipView.MeetsAt(_clock, day);
        List<string> heard = [];
        lock (_gate) _reading.Add(heard);
        Brief? brief;
        try
        {
            brief = await _briefs.GetAsync(day);
            if (AfterBriefRead is { } hook) await hook();
        }
        catch
        {
            lock (_gate) _reading.Remove(heard);
            throw;
        }
        // The topics Today's page re-reads on: the day's brief (or its start), each article on it, and its sends (which
        // publish as the brief's). One of them changed after the read began: what was read is out of date.
        lock (_gate)
        {
            _reading.Remove(heard);
            var follow = _follow = BriefPage.Follow(brief, day);
            if (heard.Exists(follow.Contains)) Interlocked.Increment(ref _version);
        }
        if (brief is null) return new BriefChipView(day, null, meets, 0, 0, null);
        var (publicMax, teamMax) = await _ceilings.GetAsync();
        // Today's strip, without the feed's count and the day's numbers: neither moves the current step (BriefSteps.On).
        var steps = BriefSteps.For(brief, newInFeed: null, missingNumbers: 0, meets, await _sends.SendsAsync(day),
            BriefSteps.NothingToSend(brief, teamMax, publicMax), _sends.SharePointRequired);
        var (number, step) = BriefSteps.On(steps);
        var met = steps.Any(s => s is { Kind: BriefStepKind.Present, State: StepState.Done });
        return new BriefChipView(day, brief.Id, meets, number, steps.Count, step, met);
    }

    /// <summary>For tests: runs after the brief is read and before what's followed is set, where a colleague's change
    /// can land.</summary>
    internal Func<Task>? AfterBriefRead { get; set; }

    private volatile bool _disposed;

    public void Dispose()
    {
        _disposed = true;
        _feed.Changed -= OnChanged;
    }
}
