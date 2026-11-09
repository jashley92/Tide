using Microsoft.AspNetCore.DataProtection;
using Tide.Application.Api;
using Tide.Application.Operations;

namespace Tide.Web.Api;

/// <summary>A webhook's signing secret, encrypted with the server's data protection keys (DataProtection:KeyPath).</summary>
public sealed class DataProtectionSecrets(IDataProtectionProvider provider) : ISecretProtector
{
    private readonly IDataProtector _protector = provider.CreateProtector("Tide.Webhooks.Secret.v1");

    public string Protect(string secret) => _protector.Protect(secret);

    public string? Unprotect(string protectedSecret)
    {
        try { return _protector.Unprotect(protectedSecret); }
        catch (System.Security.Cryptography.CryptographicException) { return null; }
    }
}

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
/// hold the job lease skips its passes (another process sends them).</summary>
public sealed class WebhookHostedService(IServiceScopeFactory scopes, JobMonitor jobs, IJobLease lease, TimeProvider time, ILogger<WebhookHostedService> log) : BackgroundService
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await lease.HoldAsync(stoppingToken))
                    await jobs.RunAsync(JobMonitor.Webhooks, async () =>
                    {
                        await using var scope = scopes.CreateAsyncScope();
                        var posted = await scope.ServiceProvider.GetRequiredService<WebhookService>().DeliverDueAsync(stoppingToken);
                        return posted == 0 ? null : $"{posted} sent";
                    });
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                // Recorded and logged (a Warning, with the job's event id) by the monitor.
                log.LogDebug(ex, "Sending webhooks failed; it's tried again at the next run.");
            }
            jobs.Scheduled(JobMonitor.Webhooks, true, time.GetUtcNow() + PollInterval);
            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}
