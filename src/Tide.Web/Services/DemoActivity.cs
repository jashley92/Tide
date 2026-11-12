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
        var inHand = 0;
        foreach (var (_, hand) in _hands)
            if (hand.Connected) inHand += Volatile.Read(ref hand.Count);
        return new { started = Interlocked.Read(ref _started), inHand };
    }

    public override Func<CircuitInboundActivityContext, Task> CreateInboundActivityHandler(Func<CircuitInboundActivityContext, Task> next) =>
        async context =>
        {
            var hand = _hands.GetOrAdd(context.Circuit.Id, _ => new Hand());
            Interlocked.Increment(ref hand.Count);
            Interlocked.Increment(ref _started);
            try { await next(context); }
            finally { Interlocked.Decrement(ref hand.Count); }
        };

    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        _hands.GetOrAdd(circuit.Id, _ => new Hand()).Connected = true;
        return Task.CompletedTask;
    }

    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        if (_hands.TryGetValue(circuit.Id, out var hand)) hand.Connected = false;
        return Task.CompletedTask;
    }

    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        _hands.TryRemove(circuit.Id, out _);
        return Task.CompletedTask;
    }
}
