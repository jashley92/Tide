namespace Tide.Web.Services;

/// <summary>Someone in a live meeting: the slide they're on, and whether they could present it.</summary>
public sealed record MeetingPerson(string UserId, string Name, int Slide, bool CanPresent);

/// <summary>A raised hand: who, on which article (null: the meeting as a whole), and since when.</summary>
public sealed record RaisedHand(string UserId, string Name, Guid? ArticleId, DateTimeOffset AtUtc);

/// <summary>
/// A live meeting as it stands. <see cref="Summons"/> counts the presenter's "Bring everyone here" and locking, so a page
/// that sees it go up follows again. <see cref="CanTakeOver"/>: the presenter has been gone for the grace period.
/// <see cref="Locked"/>: the presenter has everyone following, with no browsing off. <see cref="SlideKey"/> says what
/// the presenter's slide is ("article:{id}", "wrap", …), so a page whose slides number differently (an open-actions slide
/// it hasn't loaded yet) still goes to the same one. <see cref="Action"/>: the action the presenter has open over their
/// slide (its number), which those following see with them; <see cref="Product"/>, likewise, the Our stack product whose
/// card the presenter has open over an article's slide (team edition only). One of the two at most is open.
/// <see cref="Scroll"/>: how far down their slide the presenter has scrolled, as a fraction of the way (0 the top, 1 the
/// bottom), so a page that joins late lands where they are.
/// </summary>
public sealed record LiveMeetingView(DateOnly Day, Guid BriefId, string PresenterId, string PresenterName, int Slide, int Summons,
    bool PresenterHere, bool CanTakeOver, IReadOnlyList<MeetingPerson> People, IReadOnlyList<RaisedHand> Hands, DateTimeOffset StartedAtUtc,
    bool Locked = false, string? SlideKey = null, int? Action = null, Guid? Product = null, double Scroll = 0)
{
    public bool IsPresenter(string userId) => PresenterId == userId;

    /// <summary>Who's on another slide than the presenter's.</summary>
    public bool Away(MeetingPerson p) => p.UserId != PresenterId && p.Slide != Slide;

    /// <summary>Whether <paramref name="userId"/> is held on the presenter's slide: it's locked, they aren't presenting, and
    /// the presenter is there (a lock doesn't outlast a presenter who dropped out: the others can browse, take over or end).</summary>
    public bool HeldOn(string userId) => Locked && PresenterHere && !IsPresenter(userId);
}

/// <summary>A day's meeting changed, from <see cref="Origin"/> (the page's ChangeOrigin; empty for the sweep), so a page can
/// skip what it did itself, as it does a ChangeFeed change.</summary>
public readonly record struct MeetingChange(DateOnly Day, Guid Origin);

/// <summary>
/// The presenter scrolled the body of their slide (<see cref="SlideKey"/>, <see cref="Slide"/>) to <see cref="Fraction"/>
/// of the way down: scrollTop / (scrollHeight - clientHeight), so windows of other sizes line up roughly. From
/// <see cref="Origin"/>, the presenter's page.
/// </summary>
public readonly record struct MeetingScroll(DateOnly Day, Guid Origin, string? SlideKey, int Slide, double Fraction);

/// <summary>A page's seat in a live meeting. Disposing it leaves.</summary>
public interface IMeetingSeat : IDisposable
{
    DateOnly Day { get; }
    string UserId { get; }

    /// <summary>
    /// The slide this page is on (and what it is, <paramref name="key"/>), and whether it's following the presenter. The
    /// presenter's moves the meeting, and with it everyone following, so a follower needn't say so itself each time.
    /// </summary>
    void At(int slide, bool following = false, string? key = null);
}

/// <summary>
/// The brief meetings going on now, one per brief day, as present mode runs them for several people: who presents, the
/// slide they're on (the others follow it), who's there and on which slide, and raised hands. In memory, per app, like
/// <see cref="Presence"/> (ADR 0001: one process), so a meeting doesn't survive a restart; what's kept of it (who
/// attended, the notes) is the database's. A tab that closes without leaving is cleared with its circuit; one whose
/// connection drops is away (not shown, not presenting) until it's back.
/// </summary>
public sealed class LiveMeetings : IDisposable, Tide.Application.Briefs.IMeetingsInProgress
{
    /// <summary>How long the presenter may be gone (a dropped connection, a reload) before someone else can take over.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(30);

    /// <summary>How long a meeting nobody is in is kept before it's dropped.</summary>
    public static readonly TimeSpan Abandoned = TimeSpan.FromMinutes(15);

    private readonly TimeProvider _time;
    private readonly ITimer _sweeper;
    private readonly Lock _gate = new();
    private readonly Dictionary<DateOnly, Meeting> _meetings = [];

    public LiveMeetings(TimeProvider time)
    {
        _time = time;
        _sweeper = time.CreateTimer(_ => Sweep(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
    }

    /// <summary>Raised when a day's meeting changed: started, moved, joined, left, ended.</summary>
    public event Action<MeetingChange>? Changed;

    /// <summary>
    /// Raised when the presenter scrolled their slide (present.js sends at most ten a second, the last always). Apart from
    /// <see cref="Changed"/>, as it comes often and a page needs only to scroll, not to draw the slides again.
    /// </summary>
    public event Action<MeetingScroll>? Scrolled;

    public LiveMeetingView? Get(DateOnly day)
    {
        lock (_gate) return _meetings.TryGetValue(day, out var m) ? View(m) : null;
    }

    /// <summary>Whether the brief <paramref name="briefId"/>'s meeting is going on (Back to ready waits for it to end).</summary>
    public bool IsLive(Guid briefId)
    {
        lock (_gate) return _meetings.Values.Any(m => m.BriefId == briefId);
    }

    /// <summary>
    /// The meetings <paramref name="userId"/> is in now: a seat of theirs whose connection is up. A seat whose page left, or
    /// whose meeting ended, is gone at once, so a Visitor's reads through it stop with it (MeetingAccess).
    /// </summary>
    public IReadOnlyList<Tide.Application.Briefs.SeatedMeeting> SeatedIn(string userId)
    {
        lock (_gate)
            return _meetings.Values.Where(m => Here(m).Any(s => s.UserId == userId))
                .Select(m => new Tide.Application.Briefs.SeatedMeeting(m.Day, m.BriefId, m.Action, m.Product)).ToList();
    }

    /// <summary>Every meeting going on, oldest first.</summary>
    public IReadOnlyList<LiveMeetingView> Live()
    {
        lock (_gate) return _meetings.Values.OrderBy(m => m.StartedAtUtc).Select(View).ToList();
    }

    /// <summary>Starts <paramref name="day"/>'s meeting with me presenting. Refused when it's already going.</summary>
    public IMeetingSeat Start(DateOnly day, Guid briefId, string userId, string name, string circuitId, Guid origin, int slide)
    {
        Seat seat;
        lock (_gate)
        {
            if (_meetings.ContainsKey(day)) throw new InvalidOperationException("This brief's meeting has already started.");
            var m = new Meeting(day, briefId, userId, name, _time.GetUtcNow()) { Slide = slide };
            _meetings[day] = m;
            seat = Sit(m, userId, name, canPresent: true, circuitId, origin, slide);
        }
        Notify(day, origin);
        return seat;
    }

    /// <summary>Joins <paramref name="day"/>'s meeting, following it, or null when none is going.</summary>
    public IMeetingSeat? Join(DateOnly day, string userId, string name, bool canPresent, string circuitId, Guid origin, int slide)
    {
        Seat seat;
        lock (_gate)
        {
            if (!_meetings.TryGetValue(day, out var m)) return null;
            seat = Sit(m, userId, name, canPresent, circuitId, origin, slide);
        }
        Notify(day, origin);
        return seat;
    }

    /// <summary>Raises my hand on <paramref name="articleId"/>, or lowers it.</summary>
    public void Hand(IMeetingSeat seat, bool up, Guid? articleId = null) => Change(seat, (m, s) =>
    {
        if (!up) return m.Hands.Remove(s.UserId);
        if (m.Hands.TryGetValue(s.UserId, out var raised) && raised.ArticleId == articleId) return false;
        m.Hands[s.UserId] = new RaisedHand(s.UserId, s.Name, articleId, _time.GetUtcNow());
        return true;
    });

    /// <summary>The presenter lowers <paramref name="userId"/>'s hand, or everyone's (null).</summary>
    public bool ClearHands(IMeetingSeat seat, string? userId = null) => Change(seat, presenter: true, (m, _) =>
    {
        if (userId is not null) return m.Hands.Remove(userId);
        if (m.Hands.Count == 0) return false;
        m.Hands.Clear();
        return true;
    });

    /// <summary>The presenter brings everyone who wandered off back to the slide they're on.</summary>
    public bool Summon(IMeetingSeat seat) => Change(seat, presenter: true, (m, _) =>
    {
        BringBack(m);
        return true;
    });

    /// <summary>
    /// The presenter has everyone follow, and stay following: no one can browse off until it's unlocked. Locking brings
    /// back whoever had wandered off. A new presenter (handed to, or taking over) keeps the lock.
    /// </summary>
    public bool Lock(IMeetingSeat seat, bool locked) => Change(seat, presenter: true, (m, _) =>
    {
        if (m.Locked == locked) return false;
        m.Locked = locked;
        if (locked) BringBack(m);
        return true;
    });

    /// <summary>
    /// The presenter opens an action over their slide (its number), or closes it (null), for those following too. Moving
    /// to another slide closes it, and so does handing the meeting on. Opening one closes a product's card.
    /// </summary>
    public bool Open(IMeetingSeat seat, int? action) => Change(seat, presenter: true, (m, _) =>
    {
        if (m.Action == action) return false;
        m.Action = action;
        if (action is not null) m.Product = null;
        return true;
    });

    /// <summary>
    /// The presenter opens an Our stack product's card over their slide, or closes it (null), for those following too, as
    /// <see cref="Open"/> does an action: moving on or handing the meeting over closes it, and opening it closes an action.
    /// </summary>
    public bool Show(IMeetingSeat seat, Guid? product) => Change(seat, presenter: true, (m, _) =>
    {
        if (m.Product == product) return false;
        m.Product = product;
        if (product is not null) m.Action = null;
        return true;
    });

    /// <summary>
    /// The presenter scrolled the body of their slide to <paramref name="fraction"/> of the way down (0 to 1, clamped here
    /// and only here). Kept for those who join later, and passed on to the room at once (<see cref="Scrolled"/>): the
    /// browser already sends at most ten a second. Refused for anyone but the presenter, and for a scroll of a slide
    /// (<paramref name="slideKey"/>, as the page names it) the meeting has moved on from: the last one, sent late.
    /// </summary>
    public bool Scroll(IMeetingSeat seat, double fraction, string? slideKey = null)
    {
        fraction = ScrollFraction.Clamp(fraction);
        MeetingScroll said;
        lock (_gate)
        {
            if (Seated(seat) is not (var m, var s) || m.PresenterId != s.UserId || Math.Abs(m.Scroll - fraction) < 1e-9) return false;
            if (slideKey is not null && m.SlideKey is not null && slideKey != m.SlideKey) return false;
            (m.Scroll, m.ScrollOrigin) = (fraction, s.Origin);
            said = new MeetingScroll(m.Day, m.ScrollOrigin, m.SlideKey, m.Slide, m.Scroll);
        }
        Together.Notify(Scrolled, said);
        return true;
    }

    private static void BringBack(Meeting m)
    {
        m.Summons++;
        foreach (var s in m.Seats.Values.Where(s => s.UserId != m.PresenterId)) (s.Slide, s.Key, s.Following) = (m.Slide, m.SlideKey, true);
    }

    /// <summary>The presenter hands the meeting to <paramref name="userId"/>, who must be there and able to present.</summary>
    public bool HandTo(IMeetingSeat seat, string userId) => Change(seat, presenter: true, (m, _) =>
        Here(m).FirstOrDefault(s => s.UserId == userId && s.CanPresent) is { } to && MakePresenter(m, to));

    /// <summary>Takes over a meeting whose presenter has been gone for the grace period.</summary>
    public bool TakeOver(IMeetingSeat seat) => Change(seat, (m, s) => s.CanPresent && CanTakeOver(m) && MakePresenter(m, s));

    /// <summary>
    /// Ends the meeting (the presenter, or anyone who can present once the presenter has gone): it stops being live, and
    /// the result is when each person who came last left, now for those still there.
    /// </summary>
    public IReadOnlyDictionary<string, DateTimeOffset>? End(IMeetingSeat seat)
    {
        Dictionary<string, DateTimeOffset> left;
        Guid origin;
        lock (_gate)
        {
            if (Seated(seat) is not (var m, var s) || m.PresenterId != s.UserId && !(s.CanPresent && CanTakeOver(m))) return null;
            var now = _time.GetUtcNow();
            left = new Dictionary<string, DateTimeOffset>(m.LastLeftAtUtc, StringComparer.Ordinal);
            foreach (var g in m.Seats.Values.GroupBy(x => x.UserId, StringComparer.Ordinal))
            {
                var last = g.Max(x => x.DownSinceUtc ?? now);   // a dropped seat left when its connection went
                left[g.Key] = left.TryGetValue(g.Key, out var before) && before > last ? before : last;
            }
            _meetings.Remove(m.Day);
            origin = s.Origin;
        }
        Notify(seat.Day, origin);
        return left;
    }

    /// <summary>Leaves every meeting a circuit's pages are in: the tab closed or dropped without saying so.</summary>
    public void RemoveCircuit(string circuitId)
    {
        if (circuitId.Length == 0) return;
        List<Seat> gone;
        lock (_gate) gone = _meetings.Values.SelectMany(m => m.Seats.Values).Where(s => s.CircuitId == circuitId).ToList();
        foreach (var s in gone) s.Dispose();
    }

    /// <summary>
    /// A circuit's connection dropped (a closed lid, lost Wi-Fi) or came back. While it's down its seats are away: not
    /// shown, not presenting, so a presenter's drop starts the grace period at once rather than when the circuit is given
    /// up on, minutes later.
    /// </summary>
    public void SetConnected(string circuitId, bool connected)
    {
        if (circuitId.Length == 0) return;
        var changed = new List<(DateOnly, Guid)>();
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            foreach (var m in _meetings.Values)
            {
                var seats = m.Seats.Values.Where(s => s.CircuitId == circuitId && s.Down == connected).ToList();
                if (seats.Count == 0) continue;
                foreach (var s in seats) (s.Down, s.DownSinceUtc) = (!connected, connected ? null : now);
                if (!connected && Here(m).All(s => s.UserId != seats[0].UserId)) m.Hands.Remove(seats[0].UserId);
                Settle(m, seats[0].UserId, now);
                changed.Add((m.Day, seats[0].Origin));
            }
        }
        foreach (var (day, origin) in changed) Notify(day, origin);
    }

    /// <summary>
    /// Says so when a presenter's grace period runs out (the others can take over), and drops meetings nobody has been in
    /// for <see cref="Abandoned"/>. Runs every few seconds.
    /// </summary>
    public void Sweep()
    {
        var changed = new List<DateOnly>();
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            foreach (var m in _meetings.Values.ToList())
            {
                if (!Here(m).Any() && m.EmptySinceUtc is { } empty && now - empty >= Abandoned)
                {
                    _meetings.Remove(m.Day);
                    changed.Add(m.Day);
                }
                else if (CanTakeOver(m) && !m.TakeOverSaid)
                {
                    m.TakeOverSaid = true;
                    changed.Add(m.Day);
                }
            }
        }
        foreach (var day in changed) Notify(day, Guid.Empty);
    }

    // One change by someone seated (the presenter, when it's theirs to make), under the lock; said if it changed anything.
    private bool Change(IMeetingSeat seat, Func<Meeting, Seat, bool> change) => Change(seat, presenter: false, change);

    private bool Change(IMeetingSeat seat, bool presenter, Func<Meeting, Seat, bool> change)
    {
        Guid origin;
        lock (_gate)
        {
            if (Seated(seat) is not (var m, var s) || presenter && m.PresenterId != s.UserId || !change(m, s)) return false;
            origin = s.Origin;
        }
        Notify(seat.Day, origin);
        return true;
    }

    // The new presenter takes the meeting from the slide they're on; whoever presented before follows them.
    private static bool MakePresenter(Meeting m, Seat to)
    {
        foreach (var s in m.Seats.Values.Where(s => s.UserId == m.PresenterId)) s.Following = true;
        (m.PresenterId, m.PresenterName, m.PresenterGoneAtUtc, m.TakeOverSaid, m.Slide, m.SlideKey, m.Action, m.Product) = (to.UserId, to.Name, null, false, to.Slide, to.Key, null, null);
        m.Scroll = 0;   // a new slide starts at its top
        to.Following = false;
        foreach (var s in m.Seats.Values.Where(s => s.Following)) (s.Slide, s.Key) = (to.Slide, to.Key);
        if (m.Locked) BringBack(m);   // a lock carried over holds everyone on the new presenter's slide
        return true;
    }

    private Seat Sit(Meeting m, string userId, string name, bool canPresent, string circuitId, Guid origin, int slide)
    {
        var following = userId != m.PresenterId;
        var seat = new Seat(this, m.Day, userId, name, canPresent, circuitId, origin)
        {
            Slide = following ? m.Slide : slide, Key = following ? m.SlideKey : null, Following = following,
        };
        m.Seats[seat.SeatId] = seat;
        Settle(m, userId, _time.GetUtcNow());
        return seat;
    }

    private void Moved(Seat seat, int slide, bool following, string? key)
    {
        lock (_gate)
        {
            if (Seated(seat) is not (var m, _)) return;
            var presenting = seat.UserId == m.PresenterId;
            // Locked: on the presenter's slide, and moved with it (while the presenter is there to hold it).
            if (m.Locked && !presenting && PresenterHere(m)) return;
            if (seat.Slide == slide && seat.Key == key && seat.Following == following && (!presenting || m.Slide == slide)) return;
            (seat.Slide, seat.Key, seat.Following) = (following && !presenting && key is not null && key == m.SlideKey ? m.Slide : slide, key, following && !presenting);
            if (presenting)
            {
                // They were open over the last slide. Told by its key where both pages give one: the same slide renumbered
                // (a colleague added the opening note) keeps them, and another slide come to the same number doesn't.
                var moved = key is not null && m.SlideKey is not null ? key != m.SlideKey : m.Slide != slide;
                if (moved)
                {
                    (m.Action, m.Product, m.Scroll) = (null, null, 0);   // the new slide starts at its top
                }
                (m.Slide, m.SlideKey) = (slide, key);
                // Everyone following, and the presenter's own other tabs (a projector window), move with the presenter.
                foreach (var s in m.Seats.Values.Where(s => s.Following || s.UserId == m.PresenterId)) (s.Slide, s.Key) = (slide, key);
            }
        }
        Notify(seat.Day, seat.Origin);
    }

    private void Leave(Seat seat)
    {
        lock (_gate)
        {
            if (Seated(seat) is not (var m, _)) return;
            m.Seats.Remove(seat.SeatId);
            var now = _time.GetUtcNow();
            var left = seat.DownSinceUtc ?? now;
            if (!m.LastLeftAtUtc.TryGetValue(seat.UserId, out var before) || before < left) m.LastLeftAtUtc[seat.UserId] = left;
            if (Here(m).All(s => s.UserId != seat.UserId)) m.Hands.Remove(seat.UserId);
            Settle(m, seat.UserId, now);
        }
        Notify(seat.Day, seat.Origin);
    }

    // After someone's seats changed (left, dropped, back): the presenter's absence and the meeting's emptiness follow.
    private static void Settle(Meeting m, string userId, DateTimeOffset now)
    {
        if (userId == m.PresenterId)
        {
            if (PresenterHere(m))
            {
                if (m.PresenterGoneAtUtc is not null && m.Locked) BringBack(m);   // the lock holds again: everyone on their slide
                (m.PresenterGoneAtUtc, m.TakeOverSaid) = (null, false);
            }
            else m.PresenterGoneAtUtc ??= now;
        }
        if (Here(m).Any()) m.EmptySinceUtc = null;
        else m.EmptySinceUtc ??= now;
    }

    // The seats whose connection is up: who's in the meeting now.
    private static IEnumerable<Seat> Here(Meeting m) => m.Seats.Values.Where(s => !s.Down);

    private static bool PresenterHere(Meeting m) => Here(m).Any(s => s.UserId == m.PresenterId);

    // The meeting this seat is in, and the seat, while it's in it (not after it ended or the seat left).
    private (Meeting, Seat)? Seated(IMeetingSeat seat) =>
        seat is Seat s && _meetings.TryGetValue(s.Day, out var m) && m.Seats.ContainsKey(s.SeatId) ? (m, s) : null;

    private bool CanTakeOver(Meeting m) => m.PresenterGoneAtUtc is { } gone && _time.GetUtcNow() - gone >= Grace && !PresenterHere(m);

    private LiveMeetingView View(Meeting m)
    {
        // Each person once, however many tabs: on the slide their latest tab moved to.
        var people = Here(m).GroupBy(s => s.UserId, StringComparer.Ordinal)
            .Select(g =>
            {
                var latest = g.MaxBy(s => s.MovedAt)!;
                return new MeetingPerson(g.Key, latest.Name, latest.Slide, g.Any(s => s.CanPresent));
            })
            .OrderBy(p => p.UserId != m.PresenterId).ThenBy(p => p.Name, StringComparer.CurrentCulture)
            .ToList();
        return new LiveMeetingView(m.Day, m.BriefId, m.PresenterId, m.PresenterName, m.Slide, m.Summons,
            people.Any(p => p.UserId == m.PresenterId), CanTakeOver(m), people, m.Hands.Values.OrderBy(h => h.AtUtc).ToList(), m.StartedAtUtc,
            m.Locked, m.SlideKey, m.Action, m.Product, m.Scroll);
    }

    private void Notify(DateOnly day, Guid origin) => Together.Notify(Changed, new MeetingChange(day, origin));

    public void Dispose() => _sweeper.Dispose();

    private sealed class Meeting(DateOnly day, Guid briefId, string presenterId, string presenterName, DateTimeOffset startedAtUtc)
    {
        public DateOnly Day { get; } = day;
        public Guid BriefId { get; } = briefId;
        public DateTimeOffset StartedAtUtc { get; } = startedAtUtc;
        public string PresenterId { get; set; } = presenterId;
        public string PresenterName { get; set; } = presenterName;
        public int Slide { get; set; }
        public string? SlideKey { get; set; }
        public int Summons { get; set; }
        public bool Locked { get; set; }
        public int? Action { get; set; }
        public Guid? Product { get; set; }

        /// <summary>How far down their slide the presenter has scrolled (0 to 1), and from which page.</summary>
        public double Scroll { get; set; }
        public Guid ScrollOrigin { get; set; }
        public DateTimeOffset? PresenterGoneAtUtc { get; set; }
        public bool TakeOverSaid { get; set; }
        public DateTimeOffset? EmptySinceUtc { get; set; }
        public Dictionary<Guid, Seat> Seats { get; } = [];
        public Dictionary<string, RaisedHand> Hands { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, DateTimeOffset> LastLeftAtUtc { get; } = new(StringComparer.Ordinal);
    }

    private sealed class Seat(LiveMeetings owner, DateOnly day, string userId, string name, bool canPresent, string circuitId, Guid origin) : IMeetingSeat
    {
        private static long _moves;

        public Guid SeatId { get; } = Guid.NewGuid();
        public DateOnly Day => day;
        public string UserId => userId;
        public string Name => name;
        public bool CanPresent => canPresent;
        public string CircuitId => circuitId;
        public Guid Origin => origin;
        public int Slide { get; set; }

        /// <summary>What <see cref="Slide"/> is, as the page names it.</summary>
        public string? Key { get; set; }

        /// <summary>On the presenter's slide, moving with it.</summary>
        public bool Following { get; set; }

        /// <summary>Its circuit's connection is down (since when): away until it's back.</summary>
        public bool Down { get; set; }
        public DateTimeOffset? DownSinceUtc { get; set; }

        public long MovedAt { get; private set; } = Interlocked.Increment(ref _moves);

        public void At(int slide, bool following = false, string? key = null)
        {
            MovedAt = Interlocked.Increment(ref _moves);
            owner.Moved(this, slide, following, key);
        }

        // Leaving twice (the page's dispose, then its circuit's) is a no-op: the second finds the seat gone.
        public void Dispose() => owner.Leave(this);
    }
}
