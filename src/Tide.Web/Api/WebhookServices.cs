using Tide.Application.Api;
using Tide.Application.Operations;

namespace Tide.Web.Api;

/// <summary>Posts a webhook: 15 seconds at most, no redirects followed (a redirect is reported, not obeyed).</summary>
public sealed class HttpWebhookPoster(IHttpClientFactory clients) : IWebhookPoster
{
    public const string Client = "webhooks";

    public async Task<(int? Status, string? Error)> PostAsync(string url, string body, IReadOnlyDictionary<string, string> headers, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
        foreach (var (name, value) in headers) request.Headers.TryAddWithoutValidation(name, value);
        using var response = await clients.CreateClient(Client).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        var status = (int)response.StatusCode;
        return (status, status is >= 200 and < 300 ? null : $"The address answered {status} {response.ReasonPhrase}.");
    }
}

/// <summary>Sends webhooks' events every minute (see <see cref="WebhookService.DeliverDueAsync"/>). A process that doesn't
/// hold the job lease skips its passes (another process sends them) and records no schedule, so Diagnostics shows it
/// standing by, not due. The job is on while a webhook is switched on, and runs only then.</summary>
public sealed class WebhookHostedService(IServiceScopeFactory scopes, JobMonitor jobs, IJobLease lease, ILogger<WebhookHostedService> log) : BackgroundService
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await PassAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>One poll: a run of the job when this process holds the lease and a webhook is on. A failure is recorded
    /// and logged, never thrown (except for shutting down).</summary>
    internal async Task PassAsync(CancellationToken stoppingToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var webhooks = scope.ServiceProvider.GetRequiredService<WebhookService>();
        await jobs.PassAsync(JobMonitor.Webhooks, lease, PollInterval, () => webhooks.AnyEnabledAsync(stoppingToken), async () =>
        {
            var posted = await webhooks.DeliverDueAsync(stoppingToken);
            return posted == 0 ? null : $"{posted} sent";
        }, log, "Sending webhooks failed; it's tried again at the next run.", stoppingToken);
    }
}
