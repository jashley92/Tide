using Tide.Application.Abstractions;
using Tide.Application.Indicators;
using Tide.Application.Operations;

namespace Tide.Web.Services;

/// <summary>
/// Gives the indicators saved before their lookup keys were kept those keys (<see cref="IndicatorKeyFill"/>), once, after
/// the app is listening (<see cref="OneTimeFillService"/>): <see cref="BatchesPerPass"/> batches a run.
/// </summary>
public sealed class IndicatorKeyFillHostedService(IServiceScopeFactory scopes, JobMonitor jobs, IJobLease lease, IHostApplicationLifetime life,
    TimeProvider time, ILogger<IndicatorKeyFillHostedService> log) : OneTimeFillService(scopes, jobs, lease, life, time, log)
{
    /// <summary>Batches filled in one run.</summary>
    public const int BatchesPerPass = 25;

    private int _filled;

    protected override JobDefinition Job => JobMonitor.IndicatorKeys;

    protected override string Filled => "Every stored indicator has its lookup keys";

    protected override Task<bool> DoneAsync(IAppDbContext db, CancellationToken ct) => IndicatorKeyFill.DoneAsync(db, ct);

    protected override async Task<(bool Done, string Result)> FillAsync(IAppDbContext db, CancellationToken ct)
    {
        var (filled, done) = await IndicatorKeyFill.FillAsync(db, BatchesPerPass, ct: ct);
        _filled += filled;
        return (done, done ? $"done: {_filled:N0} keyed" : $"{filled:N0} keyed, {await IndicatorKeyFill.LeftAsync(db, ct):N0} left");
    }
}
