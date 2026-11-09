using System.Net.Mail;
using Microsoft.EntityFrameworkCore;
using Tide.Application.Abstractions;

namespace Tide.Application.Distribution;

/// <summary>A template's standing copies: addresses every email from it is copied to, seen by its recipients (CC) or not (BCC).</summary>
public sealed record TemplateCopies(IReadOnlyList<string> Cc, IReadOnlyList<string> Bcc)
{
    public static TemplateCopies None { get; } = new([], []);

    public bool Any => Cc.Count > 0 || Bcc.Count > 0;
}

/// <summary>
/// The rules for standing copies (Administration › Email templates). A copy is a standing way for mail to leave, so each
/// is checked: a few addresses at most, real addresses only, BCC only on a template that goes to people outside TIDE (so
/// they never see an internal address), an address outside the organization's domain only when an administrator
/// confirms it, and the archive copy, which gets every email TIDE sends (a brief's editions included), inside it always.
/// Kept as <c>Settings</c> rows, audited like any change, and never carried in a configuration bundle: an import can't
/// add a copy nobody confirmed.
/// </summary>
public static class EmailCopyRules
{
    public const string KeyPrefix = "EmailCopy:";

    /// <summary>The archive copy: one address that is blind-copied on every email TIDE sends.</summary>
    public const string ArchiveKey = KeyPrefix + "Archive";

    /// <summary>The most addresses in one template's CC, or its BCC.</summary>
    public const int MaxPerList = 5;

    public static string Key(string templateId, bool bcc) => $"{KeyPrefix}{templateId}:{(bcc ? "Bcc" : "Cc")}";

    public static bool IsKey(string key) => key.StartsWith(KeyPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the template's emails go to people who don't use TIDE (an external team, an escalation contact):
    /// its copies are blind only.</summary>
    public static bool GoesOutside(EmailTemplate t) => t == EmailTemplateCatalog.ExternalReminder || t == EmailTemplateCatalog.Escalation;

    /// <summary>The addresses in <paramref name="text"/> (separated by commas, semicolons, spaces or lines), each once, in
    /// order. Throws <see cref="ArgumentException"/> for one that isn't an address, or for more than <paramref name="max"/>.</summary>
    public static IReadOnlyList<string> Parse(string? text, string what, int max = MaxPerList)
    {
        var list = new List<string>();
        foreach (var raw in (text ?? "").Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!MailAddress.TryCreate(raw, out var parsed) || !string.Equals(parsed.Address, raw, StringComparison.OrdinalIgnoreCase) || !raw.Contains('.', StringComparison.Ordinal))
                throw new ArgumentException($"{what}: “{raw}” isn't an email address.");
            if (!list.Contains(parsed.Address, StringComparer.OrdinalIgnoreCase)) list.Add(parsed.Address);
        }
        if (list.Count > max) throw new ArgumentException($"{what}: at most {max} address{(max == 1 ? "" : "es")}.");
        return list;
    }

    public static string Join(IEnumerable<string> addresses) => string.Join("; ", addresses);

    /// <summary>The organization's domain: the sending address's (<c>Email:From</c>), or null when none is set.</summary>
    public static string? HomeDomain(string? from) =>
        MailAddress.TryCreate(from ?? "", out var a) ? a.Host.ToLowerInvariant() : null;

    /// <summary>Whether <paramref name="address"/> is in <paramref name="home"/> or one of its subdomains. With no home
    /// domain known, nothing is.</summary>
    public static bool IsInside(string address, string? home)
    {
        if (home is null || !MailAddress.TryCreate(address, out var a)) return false;
        var host = a.Host.ToLowerInvariant();
        return host == home || host.EndsWith("." + home, StringComparison.Ordinal);
    }

    /// <summary>
    /// The addresses to copy <paramref name="email"/> to: its template's, then the archive, as CC and BCC, leaving out any
    /// already on it. A template that goes outside TIDE gets them all blind, whatever was saved.
    /// </summary>
    public static (IReadOnlyList<string> Cc, IReadOnlyList<string> Bcc) For(OutgoingEmail email, TemplateCopies copies, string? archive)
    {
        var template = EmailTemplateCatalog.ById(email.Template);
        var cc = template is not null && GoesOutside(template) ? [] : copies.Cc;
        var bcc = (template is not null && GoesOutside(template) ? copies.Cc.Concat(copies.Bcc) : copies.Bcc)
            .Concat(archive is null ? [] : [archive]);
        var seen = new HashSet<string>(email.To.Concat(email.Cc).Concat(email.Bcc), StringComparer.OrdinalIgnoreCase);
        var addCc = cc.Where(seen.Add).ToList();
        var addBcc = bcc.Where(seen.Add).ToList();
        return (addCc, addBcc);
    }
}

/// <summary>The standing copies in force, read from <c>Settings</c> at startup and after each save.</summary>
public sealed class EmailCopyStore
{
    private IReadOnlyDictionary<string, string> _values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public TemplateCopies For(EmailTemplate t) =>
        new(List(EmailCopyRules.Key(t.Id, bcc: false)), List(EmailCopyRules.Key(t.Id, bcc: true)));

    public TemplateCopies For(string? templateId) => EmailTemplateCatalog.ById(templateId) is { } t ? For(t) : TemplateCopies.None;

    public string? Archive => _values.TryGetValue(EmailCopyRules.ArchiveKey, out var v) && v.Length > 0 ? v : null;

    private IReadOnlyList<string> List(string key) =>
        _values.TryGetValue(key, out var v) ? v.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : [];

    public async Task RefreshAsync(IAppDbContextFactory dbs, CancellationToken ct = default)
    {
        await using var db = await dbs.CreateAsync(ct);
        _values = await db.Settings.AsNoTracking().Where(s => s.Key.StartsWith(EmailCopyRules.KeyPrefix))
            .ToDictionaryAsync(s => s.Key, s => s.Value ?? "", StringComparer.OrdinalIgnoreCase, ct);
    }
}

/// <summary>
/// Adds the standing copies to each email on its way to the relay: its template's CC and BCC, and the archive's BCC
/// (<see cref="EmailCopyRules.For"/>). Everything TIDE sends goes through it.
/// </summary>
public sealed class CopyingEmailSender(IEmailSender inner, EmailCopyStore copies) : IEmailSender
{
    public bool Enabled => inner.Enabled;

    public Task<EmailSendResult> SendAsync(OutgoingEmail email, CancellationToken ct = default)
    {
        var (cc, bcc) = EmailCopyRules.For(email, copies.For(email.Template), copies.Archive);
        return inner.SendAsync(cc.Count == 0 && bcc.Count == 0 ? email : email with { Cc = [.. email.Cc, .. cc], Bcc = [.. email.Bcc, .. bcc] }, ct);
    }
}

/// <summary>A copy address outside the organization's domain, not yet confirmed: the page asks, naming them.</summary>
public sealed class OutsideCopiesException(IReadOnlyList<string> outside, string? home)
    : InvalidOperationException(home is null
        ? $"Your organization's domain isn't known (Email:From), so {EmailCopyRules.Join(outside)} can't be checked. Copy {(outside.Count == 1 ? "it" : "them")} anyway?"
        : $"{EmailCopyRules.Join(outside)} {(outside.Count == 1 ? "is" : "are")} outside {home}. Every email from this template will be copied there. Copy anyway?")
{
    public IReadOnlyList<string> Outside { get; } = outside;
}
