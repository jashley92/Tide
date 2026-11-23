using Tide.Application.Abstractions;
using Tide.Application.Intake;
using Tide.Application.Operations;

namespace Tide.Web.Services;

/// <summary>
/// Gives the articles and links saved before their address's hash was kept that hash (<see cref="UrlHashFill"/>), once,
/// after the app is listening (<see cref="OneTimeFillService"/>): <see cref="BatchesPerPass"/> batches a run.
/// </summary>
public sealed class UrlHashFillHostedService(IServiceScopeFactory scopes, JobMonitor jobs, IJobLease lease, IHostApplicationLifetime life,
    TimeProvider time, ILogger<UrlHashFillHostedService> log) : OneTimeFillService(scopes, jobs, lease, life, time, log)
{
    /// <summary>Batches filled in one run.</summary>
    public const int BatchesPerPass = 20;

    private readonly UrlHashProgress _progress = new();

    protected override JobDefinition Job => JobMonitor.UrlHashes;

    protected override string Filled => "Every stored article and link address has its hash";

    protected override Task<bool> DoneAsync(IAppDbContext db, CancellationToken ct) => UrlHashFill.DoneAsync(db, ct);

    protected override async Task<(bool Done, string Result)> FillAsync(IAppDbContext db, CancellationToken ct)
    {
        var before = _progress.Filled;
        var done = await UrlHashFill.FillAsync(db, _progress, BatchesPerPass, ct: ct);
        return (done, done ? $"done: {_progress.Filled:N0} filled"
            : $"{_progress.Filled - before:N0} filled, {await UrlHashFill.LeftAsync(db, _progress, ct):N0} left");
    }
}
