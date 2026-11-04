namespace Tide.Infrastructure.Notifications;

/// <summary>Email delivery through the organization's SMTP relay (config section <c>Email</c>). Server configuration only.</summary>
public sealed class EmailOptions
{
    /// <summary>When false (the default), nothing is sent and the send buttons say email is off.</summary>
    public bool Enabled { get; set; }

    public string SmtpHost { get; set; } = "";
    public int SmtpPort { get; set; } = 25;

    /// <summary>Encrypt the connection (STARTTLS). On by default: the brief carries TLP-marked content. Turn it off only
    /// for a relay on this host, or one that doesn't offer STARTTLS on a trusted network.</summary>
    public bool EnableSsl { get; set; } = true;

    /// <summary>The account to sign in to the relay as (AUTH); blank sends without signing in.</summary>
    public string SmtpUsername { get; set; } = "";

    /// <summary><see cref="SmtpUsername"/>'s password. Keep it out of the file: set it as an environment variable
    /// (<c>Email__SmtpPassword</c>) on the app pool, or use <see cref="SmtpUseDefaultCredentials"/>.</summary>
    public string SmtpPassword { get; set; } = "";

    /// <summary>Sign in as the app-pool account (integrated Windows authentication), with no password anywhere: for
    /// on-premises Exchange. Can't be used with <see cref="SmtpUsername"/>.</summary>
    public bool SmtpUseDefaultCredentials { get; set; }

    /// <summary>How long a send may take before it's given up; 5 to 300 seconds.</summary>
    public int SmtpTimeoutSeconds { get; set; } = 30;

    /// <summary>The sender address, and the name shown beside it.</summary>
    public string From { get; set; } = "tide@localhost";
    public string FromName { get; set; } = "TIDE";

    /// <summary>
    /// Development and testing: a folder that messages are written to as .eml files instead of going to the relay. Blank
    /// (the default) sends to <see cref="SmtpHost"/>.
    /// </summary>
    public string PickupDirectory { get; set; } = "";

    public TimeSpan Timeout => TimeSpan.FromSeconds(Math.Clamp(SmtpTimeoutSeconds, 5, 300));

    /// <summary>Why the sign-in settings can't be used, or null when they can.</summary>
    public string? SignInProblem()
    {
        var user = !string.IsNullOrWhiteSpace(SmtpUsername);
        var password = !string.IsNullOrWhiteSpace(SmtpPassword);
        if (SmtpUseDefaultCredentials && (user || password))
            return "Email:SmtpUseDefaultCredentials is on and Email:SmtpUsername or Email:SmtpPassword is set too; keep one way of signing in.";
        if (user != password) return "Email:SmtpUsername and Email:SmtpPassword go together; set both or neither.";
        return null;
    }
}
