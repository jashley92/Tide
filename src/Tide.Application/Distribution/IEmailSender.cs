namespace Tide.Application.Distribution;

/// <summary>An image carried inside an email and referenced from its HTML as <c>cid:{ContentId}</c>.</summary>
public sealed record InlineImage(string ContentId, string FileName, string ContentType, byte[] Bytes);

/// <summary>One email to send: an HTML body with a plain-text alternative, and its inline images.</summary>
public sealed record OutgoingEmail(IReadOnlyList<string> To, string Subject, string Html, string Text, IReadOnlyList<InlineImage> Images);

public enum EmailSendStatus
{
    /// <summary>The relay took it (for some recipients at least).</summary>
    Sent = 0,

    /// <summary>Email is turned off (<c>Email:Enabled</c>); nothing was sent.</summary>
    Disabled = 1,

    /// <summary>The relay didn't take it; <see cref="EmailSendResult.Error"/> says why.</summary>
    Failed = 2,
}

/// <summary>What became of a send. <see cref="Error"/> is safe to show: it never echoes the relay's reply, which can
/// carry addresses.</summary>
public sealed record EmailSendResult(EmailSendStatus Status, string? Error = null, int Refused = 0);

/// <summary>Sends email through the organization's SMTP relay (configured in <c>Email:*</c>).</summary>
public interface IEmailSender
{
    /// <summary>Whether email is turned on.</summary>
    bool Enabled { get; }

    Task<EmailSendResult> SendAsync(OutgoingEmail email, CancellationToken ct = default);
}
