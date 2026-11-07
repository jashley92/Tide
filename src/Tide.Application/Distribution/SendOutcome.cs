using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Tide.Domain.Enums;

namespace Tide.Application.Distribution;

/// <summary>
/// The part of a frozen send that comes after the freeze, shared by a send and a correction: hand the email to the relay
/// and keep its outcome, whatever happens.
/// </summary>
internal static class SendOutcome
{
    private const int Attempts = 4;

    /// <summary>How long a send stays "with the relay" for the one-at-a-time rule: well past the relay's own time limit, so
    /// a send left "sending" by a stopped server doesn't block the edition for good.</summary>
    internal static readonly TimeSpan InFlight = TimeSpan.FromMinutes(15);

    /// <summary>What a send left "sending" past <see cref="InFlight"/> says in the log: it may have gone. Both a send and a
    /// correction are marked from the log once someone has asked (<c>SettleAsync</c>).</summary>
    internal const string StaleError =
        "This send didn't finish (the server stopped, or its outcome couldn't be saved), so whether the relay took it isn't known. Check with a recipient, then mark it sent or not.";

    /// <summary>What a correction left "sending" past <see cref="InFlight"/> says in the log, as <see cref="StaleError"/>.</summary>
    internal const string StaleCorrectionError =
        "This correction didn't finish (the server stopped, or its outcome couldn't be saved), so whether the relay took it isn't known. Check with a recipient, then mark it sent or not.";

    /// <summary>A send's status as it's read: one still "sending" past <see cref="InFlight"/> never will finish (the
    /// server stopped, or saving its outcome failed after the relay had it), so it's <see cref="SendStatus.Unknown"/>.</summary>
    internal static SendStatus Seen(SendStatus status, DateTimeOffset sentAtUtc) =>
        status == SendStatus.Sending && sentAtUtc <= DateTimeOffset.UtcNow - InFlight ? SendStatus.Unknown : status;

    /// <summary>
    /// Sends <paramref name="message"/>, without the caller's token: once it's handed to the relay, its outcome is recorded
    /// either way. A failure the sender didn't turn into a result is <see cref="SendStatus.Unknown"/> (the relay may or may
    /// not have taken it), so a send never stays "sending".
    /// </summary>
    internal static async Task<(SendStatus Status, string? Error)> DeliverAsync(IEmailSender email, OutgoingEmail message)
    {
        try
        {
            var result = await email.SendAsync(message, CancellationToken.None);
            return (Status(result), Error(result));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return (SendStatus.Unknown,
                $"Sending failed unexpectedly ({ex.GetType().Name}), so whether the relay took it isn't known. Check with a recipient before sending again.");
        }
    }

    /// <summary>Saves an outcome. The relay has the email by now, so a save that fails (the database busy, another brief
    /// edit in the same instant) is tried again with fresh copies before giving up.</summary>
    internal static async Task RecordAsync(Func<Task> save)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await save();
                return;
            }
            catch (Exception ex) when (attempt < Attempts && ex is DbUpdateException or System.Data.Common.DbException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), CancellationToken.None);
            }
        }
    }

    /// <summary>Why a real send or a correction can't go while email is off.</summary>
    internal const string EmailOff = "Email is turned off on this server (Email:Enabled).";

    /// <summary>A hash of what an email would carry, for a confirmation to be checked against at send.</summary>
    internal static string Fingerprint(params string[] parts) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n\u0000", parts))));

    /// <summary>Whether two recipient lists hold the same addresses, in any order and case.</summary>
    internal static bool SameRecipients(IEnumerable<string> a, IEnumerable<string> b) =>
        a.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(b.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);

    /// <summary>What the log says of a send or correction someone settled by hand (<c>SettleAsync</c>).</summary>
    internal static string Settled(string by, bool went) =>
        $"Its outcome wasn't known; {by} checked and marked it {(went ? "sent" : "not sent")}.";

    /// <summary>How a relay's answer is logged: a timeout or a dropped connection may have gone, so it's
    /// <see cref="SendStatus.Unknown"/>, settled by hand, never sent again by itself.</summary>
    internal static SendStatus Status(EmailSendResult r) => r.Status switch
    {
        EmailSendStatus.Sent => SendStatus.Sent,
        EmailSendStatus.Unknown => SendStatus.Unknown,
        _ => SendStatus.Failed,
    };

    internal static string? Error(EmailSendResult r) => r.Status switch
    {
        EmailSendStatus.Sent when r.Refused > 0 => $"{r.Refused} address{(r.Refused == 1 ? " was" : "es were")} refused by the relay.",
        EmailSendStatus.Sent => null,
        EmailSendStatus.Disabled => "Email is turned off on this server.",
        _ => r.Error ?? "The mail relay didn't take the message.",
    };
}
