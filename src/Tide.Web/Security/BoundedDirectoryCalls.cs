using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Tide.Web.Security;

/// <summary>
/// Blocking directory calls (an AD bind, a SID translation) made on a request's path, with a time limit. Each call
/// runs on its own thread (a blocked one doesn't hold a pool thread), one at a time per key (later callers join it),
/// and is waited on for a bounded time.
/// A call still blocked when a caller stops waiting is counted against its domain; with <c>maxHung</c> of those, or
/// <c>maxInFlight</c> calls running, in a domain, no new call is started there until one returns, and a key whose call
/// is still blocked isn't waited on again.
/// <para>
/// A call blocked for <see cref="HungExpiryFactor"/> times its callers' time limit is taken as one that won't return
/// (logged once per domain): it stops counting as blocked, and its keys can be asked again, so a single stuck call
/// doesn't stop the domain's lookups until the app restarts. It still counts against <c>maxInFlight</c> until it does
/// return, so a directory that never answers holds at most that many threads.
/// </para>
/// <para>
/// What a call found, even once every caller has stopped waiting, is handed to <c>landed</c> (before its waiters are
/// released), so the caller keeps it and a late answer isn't lost. A call that throws hands on no answers. The caller
/// reads what it keeps after <see cref="Wait"/>; a key with nothing kept had no answer in time.
/// </para>
/// </summary>
public sealed class BoundedDirectoryCalls<T>
{
    private readonly Func<IReadOnlyList<string>, IReadOnlyDictionary<string, T>> _call;
    private readonly Action<IReadOnlyList<string>, IReadOnlyDictionary<string, T>> _landed;
    private readonly Func<string, string> _domainOf;
    private readonly int _maxInFlight;
    private readonly int _maxHung;
    private readonly TimeProvider _time;
    private readonly ILogger? _log;
    private readonly object _gate = new();
    private readonly Dictionary<string, Call> _pending = new(StringComparer.OrdinalIgnoreCase);
    // Per domain: the calls running (including those given up on), and those of them still blocked after a timeout.
    private readonly Dictionary<string, int> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _hung = new(StringComparer.OrdinalIgnoreCase);
    // Domains with a call written off since their last call that returned: logged once until one does.
    private readonly HashSet<string> _writtenOff = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How many times its time limit a call may stay blocked before it's no longer counted as blocked.</summary>
    public const int HungExpiryFactor = 10;

    /// <param name="call">Looks up the keys of one domain in one go; a key missing from its answer has none.</param>
    /// <param name="landed">Keeps a call's answers (called on the call's thread, for every call that returns).</param>
    /// <param name="domainOf">The domain a key's call goes to.</param>
    /// <param name="log">Told once when a domain's call is written off as never returning.</param>
    public BoundedDirectoryCalls(Func<IReadOnlyList<string>, IReadOnlyDictionary<string, T>> call,
        Action<IReadOnlyList<string>, IReadOnlyDictionary<string, T>> landed, Func<string, string> domainOf,
        int maxInFlight, int maxHung, TimeProvider? time = null, ILogger? log = null)
    {
        _call = call;
        _landed = landed;
        _domainOf = domainOf;
        _maxInFlight = maxInFlight;
        _maxHung = maxHung;
        _time = time ?? TimeProvider.System;
        _log = log;
    }

    /// <summary>Starts or joins the calls for <paramref name="keys"/> and waits for them, all told, up to
    /// <paramref name="timeout"/>.</summary>
    public void Wait(IEnumerable<string> keys, TimeSpan timeout)
    {
        var calls = Join(keys);
        if (calls.Count == 0) return;
        var started = Stopwatch.GetTimestamp();
        foreach (var call in calls)
        {
            var left = timeout - Stopwatch.GetElapsedTime(started);
            if (left <= TimeSpan.Zero || !call.Task.Wait(left)) break;
        }
        GaveUp(calls, timeout);
    }

    /// <summary>As <see cref="Wait"/>, without holding the caller's thread while it waits.</summary>
    public async Task WaitAsync(IEnumerable<string> keys, TimeSpan timeout)
    {
        var calls = Join(keys);
        if (calls.Count == 0) return;
        try { await Task.WhenAll(calls.Select(c => c.Task)).WaitAsync(timeout); }
        catch (TimeoutException) { /* counted below */ }
        GaveUp(calls, timeout);
    }

    private List<Call> Join(IEnumerable<string> keys)
    {
        var calls = new List<Call>();
        lock (_gate)
        {
            // First, so a key whose call was written off is asked again below.
            foreach (var domain in _hung.Keys.ToList()) WriteOffExpired(domain);
            var fresh = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in keys.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (_pending.TryGetValue(key, out var running))
                {
                    // One still blocked after an earlier caller gave up on it isn't waited on again.
                    if (!running.Hung && !calls.Contains(running)) calls.Add(running);
                    continue;
                }
                var domain = _domainOf(key);
                if (!fresh.TryGetValue(domain, out var list)) fresh[domain] = list = [];
                list.Add(key);
            }
            foreach (var (domain, list) in fresh)
            {
                // A domain whose calls are still blocked (or that has as many running as it may) isn't given another
                // thread; its keys have no answer this time and are asked again on a later call.
                if (_hung.GetValueOrDefault(domain) >= _maxHung || _inFlight.GetValueOrDefault(domain) >= _maxInFlight)
                    continue;
                calls.Add(Start(list, domain));
            }
        }
        return calls;
    }

    // Called under _gate.
    private Call Start(List<string> keys, string domain)
    {
        var call = new Call(domain);
        Count(_inFlight, domain, +1);
        foreach (var key in keys) _pending[key] = call;
        call.Task = Task.Factory.StartNew(() =>
        {
            try
            {
                IReadOnlyDictionary<string, T> answers;
                try { answers = _call(keys); }
                catch { answers = new Dictionary<string, T>(); }   // the directory failed: no answers
                _landed(keys, answers);
            }
            catch { /* keeping the answers failed: the keys are asked again */ }
            finally
            {
                lock (_gate)
                {
                    call.Done = true;
                    Count(_inFlight, domain, -1);
                    if (call.Hung && !call.WrittenOff) Count(_hung, domain, -1);
                    if (!call.WrittenOff) _writtenOff.Remove(domain);   // the domain answers again
                    foreach (var key in keys)
                        if (_pending.TryGetValue(key, out var c) && ReferenceEquals(c, call)) _pending.Remove(key);
                }
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        return call;
    }

    private void GaveUp(List<Call> calls, TimeSpan timeout)
    {
        lock (_gate)
        {
            foreach (var call in calls)
                if (!call.Done && !call.Hung)
                {
                    call.Hung = true;
                    call.WriteOffAt = _time.GetUtcNow() + timeout * HungExpiryFactor;
                    Count(_hung, call.Domain, +1);
                }
        }
    }

    // Called under _gate: a domain's calls blocked past their write-off time stop counting as blocked, and their keys
    // are let go (asked again by a new call). Their threads still count in _inFlight until they return.
    private void WriteOffExpired(string domain)
    {
        var now = _time.GetUtcNow();
        var expired = _pending.Values.Distinct()
            .Where(c => c.Hung && !c.WrittenOff && !c.Done && now >= c.WriteOffAt && string.Equals(c.Domain, domain, StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var call in expired)
        {
            call.WrittenOff = true;
            Count(_hung, domain, -1);
            foreach (var key in _pending.Where(p => ReferenceEquals(p.Value, call)).Select(p => p.Key).ToList()) _pending.Remove(key);
            if (_writtenOff.Add(domain))
                _log?.LogWarning("A directory lookup in {Domain} has been blocked for {Factor} times its time limit; it's no longer waited on, " +
                    "and lookups there start again.", domain.Length == 0 ? "(the local authority)" : domain, HungExpiryFactor);
        }
    }

    // Called under _gate; a domain with nothing left isn't kept.
    private static void Count(Dictionary<string, int> counts, string key, int by)
    {
        var n = counts.GetValueOrDefault(key) + by;
        if (n > 0) counts[key] = n;
        else counts.Remove(key);
    }

    private sealed class Call(string domain)
    {
        public readonly string Domain = domain;
        public Task Task = Task.CompletedTask;
        public bool Done;
        public bool Hung;   // given up on, still blocked
        public bool WrittenOff;   // blocked so long it no longer counts as blocked
        public DateTimeOffset WriteOffAt;
    }
}
