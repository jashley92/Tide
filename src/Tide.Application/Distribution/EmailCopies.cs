using System.Net.Mail;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Tide.Application.Abstractions;
using Tide.Application.Content;
using Tide.Application.Settings;

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
        foreach (var raw in (text ?? "").Split(DistributionSettings.Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!DistributionSettings.IsAddress(raw) || !raw.Contains('.', StringComparison.Ordinal))
                throw new ArgumentException($"{what}: “{raw}” isn't an email address.");
            if (!list.Contains(raw, StringComparer.OrdinalIgnoreCase)) list.Add(raw);
        }
        if (list.Count > max) throw new ArgumentException($"{what}: at most {Words.Plural(max, "address", "addresses")}.");
        return list;
    }

    public static string Join(IEnumerable<string> addresses) => string.Join("; ", addresses);

    /// <summary>The row naming a template's copies an administrator confirmed outside the organization: kept when it's
    /// sent. Any other copy outside it (one that was inside when saved, before the domains changed) is dropped then.</summary>
    public static string ConfirmedKey(string templateId) => $"{KeyPrefix}{templateId}:Outside";

    /// <summary>
    /// The addresses to copy <paramref name="email"/> to: its template's, then the archive, as CC and BCC, leaving out any
    /// already on it. A template that goes outside TIDE gets them all blind, whatever was saved.
    /// </summary>
    public static (IReadOnlyList<string> Cc, IReadOnlyList<string> Bcc) For(OutgoingEmail email, TemplateCopies copies, string? archive)
    {
        var (cc, bcc) = Routed(EmailTemplateCatalog.ById(email.Template), copies);
        var seen = new HashSet<string>(email.To.Concat(email.Cc).Concat(email.Bcc), StringComparer.OrdinalIgnoreCase);
        return (cc.Where(seen.Add).ToList(), bcc.Concat(archive is null ? [] : [archive]).Where(seen.Add).ToList());
    }

    /// <summary>A template's copies as they go out: one that goes outside TIDE gets them all blind, whatever was saved.</summary>
    public static (IReadOnlyList<string> Cc, IReadOnlyList<string> Bcc) Routed(EmailTemplate? t, TemplateCopies copies) =>
        t is not null && GoesOutside(t) ? ([], [.. copies.Cc, .. copies.Bcc]) : (copies.Cc, copies.Bcc);
}

/// <summary>The standing copies in force, read from <c>Settings</c> at startup and after each save.</summary>
public sealed class EmailCopyStore
{
    private IReadOnlyDictionary<string, string> _values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public TemplateCopies For(EmailTemplate t) =>
        new(List(EmailCopyRules.Key(t.Id, bcc: false)), List(EmailCopyRules.Key(t.Id, bcc: true)));

    public TemplateCopies For(string? templateId) => EmailTemplateCatalog.ById(templateId) is { } t ? For(t) : TemplateCopies.None;

    public string? Archive => _values.TryGetValue(EmailCopyRules.ArchiveKey, out var v) && v.Length > 0 ? v : null;

    /// <summary>The template's copies an administrator confirmed outside the organization when saving them.</summary>
    public IReadOnlyList<string> ConfirmedOutside(string? templateId) =>
        EmailTemplateCatalog.ById(templateId) is { } t ? List(EmailCopyRules.ConfirmedKey(t.Id)) : [];

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
/// (<see cref="EmailCopyRules.For"/>). Everything TIDE sends goes through it. Each copy is checked against the
/// organization's boundary again as it goes: one outside it that an administrator didn't confirm (it was inside when
/// saved, and the domains have changed since), or an archive now outside it, is dropped, with a warning in the log that
/// counts them (never naming an address).
/// </summary>
public sealed class CopyingEmailSender(IEmailSender inner, EmailCopyStore copies, EmailBoundary? boundary = null,
    ILogger<CopyingEmailSender>? logger = null) : IEmailSender
{
    public bool Enabled => inner.Enabled;

    public Task<EmailSendResult> SendAsync(OutgoingEmail email, CancellationToken ct = default)
    {
        var template = copies.For(email.Template);
        var archive = copies.Archive;
        if (boundary is not null)
        {
            var inside = boundary.Current;
            var confirmed = copies.ConfirmedOutside(email.Template);
            var dropped = template.Cc.Concat(template.Bcc).Where(a => !inside.IsInside(a) && !confirmed.Contains(a, StringComparer.OrdinalIgnoreCase))
                .Concat(archive is not null && !inside.IsInside(archive) ? [archive] : []).ToList();
            if (dropped.Count > 0)
            {
                bool Keep(string a) => !dropped.Contains(a, StringComparer.OrdinalIgnoreCase);
                (template, archive) = (new([.. template.Cc.Where(Keep)], [.. template.Bcc.Where(Keep)]), archive is not null && Keep(archive) ? archive : null);
                // A count, never the addresses: the log carries none.
                logger?.LogWarning("{Count} copy address{Plural} outside the organization {Verb} dropped from an email ({Template}); check Email templates.",
                    dropped.Count, dropped.Count == 1 ? "" : "es", dropped.Count == 1 ? "was" : "were", email.Template ?? "a brief's edition");
            }
        }
        var (cc, bcc) = EmailCopyRules.For(email, template, archive);
        return inner.SendAsync(cc.Count == 0 && bcc.Count == 0 ? email : email with { Cc = [.. email.Cc, .. cc], Bcc = [.. email.Bcc, .. bcc] }, ct);
    }
}

/// <summary>A copy address outside the organization's boundary, not yet confirmed: the page asks, naming them.</summary>
public sealed class OutsideCopiesException(IReadOnlyList<string> outside, OrganizationBoundary boundary)
    : OutsideAddressesException(outside, Say(outside, boundary, "Every email from this template will be copied there.", "Copy anyway?"));
