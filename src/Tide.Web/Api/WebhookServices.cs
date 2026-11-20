using System.Net;
using Microsoft.Extensions.Options;
using Tide.Application.Api;
using Tide.Application.Operations;
using Tide.Infrastructure.Notifications;

namespace Tide.Web.Api;

/// <summary>
/// Posts a webhook: 15 seconds at most, no redirects followed (a redirect is reported, not obeyed). Only addresses
/// <see cref="AddressPolicy"/> allows are posted to: never loopback, link-local (cloud metadata among them), unspecified
/// or multicast, and private ranges only while <see cref="WebhookOptions.AllowPrivateNetworks"/> is on (the default).
/// The handler checks the address each connection actually reaches (<see cref="AddressGuard.ConnectAsync"/>); through
/// the system proxy, which makes the connection itself, the host is checked before the post instead.
/// </summary>
public sealed class HttpWebhookPoster(IHttpClientFactory clients, IOptionsMonitor<WebhookOptions> options) : IWebhookPoster
{
    public const string Client = "webhooks";

    /// <summary>The address rule as the settings are now.</summary>
    public static Func<IPAddress, string?> Refusal(IOptionsMonitor<WebhookOptions> options) =>
        ip => AddressPolicy.Refusal(ip, options.CurrentValue.AllowPrivateNetworks);

    /// <summary>The named client's handler: redirects off, and a connection only to an address <paramref name="refusal"/> allows.</summary>
    public static SocketsHttpHandler Handler(Func<IPAddress, string?> refusal) => new()
    {
        AllowAutoRedirect = false,
        ConnectCallback = (context, ct) => AddressGuard.ConnectAsync(context, refusal, ct),
    };

    public async Task<(int? Status, string? Error)> PostAsync(string url, string body, IReadOnlyDictionary<string, string> headers, CancellationToken ct = default)
    {
        try
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && AddressGuard.SystemProxied(uri))
                await AddressGuard.CheckHostAsync(uri, proxied: true, Refusal(options), ct);
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
            foreach (var (name, value) in headers) request.Headers.TryAddWithoutValidation(name, value);
            using var response = await clients.CreateClient(Client).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            var status = (int)response.StatusCode;
            return (status, status is >= 200 and < 300 ? null : $"The address answered {status} {response.ReasonPhrase}.");
        }
        catch (Exception ex) when (AddressGuard.Refused(ex) is { } refused)
        {
            // A rule, not the receiver failing: say which rule, and nothing about the connection.
            return (null, $"Refused: {refused.Host} is {refused.Why}, which webhooks aren't sent to."
                          + (refused.Why.Contains("private", StringComparison.Ordinal)
                              ? " An administrator can allow private networks (Webhooks:AllowPrivateNetworks)." : ""));
        }
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
