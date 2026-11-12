using System.Collections.Concurrent;
using Microsoft.AspNetCore.Components.Server.Circuits;

namespace Tide.Web.Services;

/// <summary>
/// Development only, with <c>Demo:Seed=true</c>: the circuit work in hand, for the scripts in tools/screenshots to wait
/// on rather than a fixed pause. Each inbound activity (an event, a JS callback, a render acknowledged) counts from when
/// it arrives until it and everything it awaits are done, while its circuit's browser is connected: a closed tab's
/// unfinished work isn't waited for. <c>GET /demo/activity</c> answers <c>{ started, inHand }</c>: the server is still
/// when nothing is in hand and nothing more has started.
/// </summary>
public sealed class DemoActivity : CircuitHandler
{
    private sealed class Hand
    {
        public int Count;
        public volatile bool Connected = true;
    }

    private readonly ConcurrentDictionary<string, Hand> _hands = new();
    private long _started;

    /// <summary>How many inbound activities have started, and how many of the connected circuits' are in hand.</summary>
    public object Now()
    {
        var (started, inHand) = Counts();
        return new { started, inHand };
    }

    internal (long Started, int InHand) Counts()
    {
        var inHand = 0;
        foreach (var (_, hand) in _hands)
            if (hand.Connected) inHand += Volatile.Read(ref hand.Count);
        return (Interlocked.Read(ref _started), inHand);
    }

    /// <summary>Counts <paramref name="work"/>, for circuit <paramref name="circuitId"/>, as in hand until it's done.</summary>
    internal async Task Track(string circuitId, Func<Task> work)
    {
        var hand = _hands.GetOrAdd(circuitId, _ => new Hand());
        Interlocked.Increment(ref hand.Count);
        Interlocked.Increment(ref _started);
        try { await work(); }
        finally { Interlocked.Decrement(ref hand.Count); }
    }

    internal void Connection(string circuitId, bool up)
    {
        if (up) _hands.GetOrAdd(circuitId, _ => new Hand()).Connected = true;
        else if (_hands.TryGetValue(circuitId, out var hand)) hand.Connected = false;
    }

    internal void Closed(string circuitId) => _hands.TryRemove(circuitId, out _);

    public override Func<CircuitInboundActivityContext, Task> CreateInboundActivityHandler(Func<CircuitInboundActivityContext, Task> next) =>
        context => Track(context.Circuit.Id, () => next(context));

    // A Circuit can't be made outside a live one, so these only hand its id to the methods above, which the tests call.
    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken) => Done(() => Connection(circuit.Id, up: true));

    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken) => Done(() => Connection(circuit.Id, up: false));

    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken) => Done(() => Closed(circuit.Id));

    internal static Task Done(Action act)
    {
        act();
        return Task.CompletedTask;
    }
}
