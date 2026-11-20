namespace Tide.Application.Api;

/// <summary>Webhook settings (config section <c>Webhooks</c>), set on the server.</summary>
public sealed class WebhookOptions
{
    /// <summary>
    /// Whether webhooks may be posted to private addresses (10/8, 172.16/12, 192.168/16, 100.64/10, IPv6 unique-local).
    /// On by default: a receiver (a SOAR platform, a ticket queue) is often internal. Loopback, link-local (cloud
    /// metadata among them), unspecified and multicast addresses are never posted to.
    /// </summary>
    public bool AllowPrivateNetworks { get; set; } = true;
}
