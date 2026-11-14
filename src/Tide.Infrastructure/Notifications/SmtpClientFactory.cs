using System.Net;
using System.Net.Mail;

namespace Tide.Infrastructure.Notifications;

/// <summary>The one place an <see cref="SmtpClient"/> is made, so its TLS and sign-in settings are
/// decided once.</summary>
internal static class SmtpClientFactory
{
    // S4423: encrypt by default (Email:EnableSsl). Only a deliberately configured relay that does its own TLS turns it off.
    internal static SmtpClient Create(EmailOptions o)
    {
        if (!string.IsNullOrWhiteSpace(o.PickupDirectory))
        {
            var dir = Path.GetFullPath(o.PickupDirectory);
            Directory.CreateDirectory(dir);
            // Writes .eml files to a folder, never the network; SmtpClient refuses SSL with a pickup directory.
#pragma warning disable S5332 // the analyzer reports it in the build despite NOSONAR
            return new SmtpClient { DeliveryMethod = SmtpDeliveryMethod.SpecifiedPickupDirectory, PickupDirectoryLocation = dir }; // NOSONAR S5332
        }
        SmtpClient client = new(o.SmtpHost, o.SmtpPort) // NOSONAR S5332: EnableSsl is on unless configured off, above
        {
            EnableSsl = o.EnableSsl,
#pragma warning restore S5332
            Timeout = (int)o.Timeout.TotalMilliseconds,
            DeliveryMethod = SmtpDeliveryMethod.Network,
        };
        if (o.SmtpUseDefaultCredentials) client.UseDefaultCredentials = true;
        else if (!string.IsNullOrWhiteSpace(o.SmtpUsername)) client.Credentials = new NetworkCredential(o.SmtpUsername.Trim(), o.SmtpPassword);
        return client;
    }
}
