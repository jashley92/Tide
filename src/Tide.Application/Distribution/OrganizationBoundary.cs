using System.Net.Mail;
using Microsoft.Extensions.Options;

namespace Tide.Application.Distribution;

/// <summary>The parts of the <c>Email</c> section the organization's boundary is read from.</summary>
public sealed class EmailBoundaryOptions
{
    /// <summary>The sending address: its domain is the organization's when <see cref="OrganizationDomains"/> is empty.</summary>
    public string? From { get; set; }

    /// <summary>The organization's email domains, each with its subdomains (<c>Email:OrganizationDomains</c>). Set on the
    /// server, or by an administrator on Access &amp; distribution.</summary>
    public string[] OrganizationDomains { get; set; } = [];
}

/// <summary>
/// The organization's email boundary: the domains an address is inside (each with its subdomains). It's
/// <c>Email:OrganizationDomains</c> when set, else the domain of <c>Email:From</c>, else unknown, when no address is
/// inside. Every list that sends mail outward (the editions, the managers told about swaps, who's told there's no brief,
/// the standing copies, the teams' contacts) asks before an address outside it is saved.
/// </summary>
public sealed class OrganizationBoundary
{
    /// <summary>The most domains <c>Email:OrganizationDomains</c> holds.</summary>
    public const int MaxDomains = 20;

    public static OrganizationBoundary Unknown { get; } = new([], fromSender: false);

    private OrganizationBoundary(IReadOnlyList<string> domains, bool fromSender) => (Domains, FromSender) = (domains, fromSender);

    /// <summary>The domains inside, lower case.</summary>
    public IReadOnlyList<string> Domains { get; }

    /// <summary>Whether the domain is the sending address's, as none is set.</summary>
    public bool FromSender { get; }

    public bool Known => Domains.Count > 0;

    /// <summary>The domains as said in a message ("example.org, example.net"), or "your organization's domain".</summary>
    public string Describe => Known ? string.Join(", ", Domains) : "your organization's domain";

    /// <summary>The boundary from the setting's domains, or, when there are none, <paramref name="from"/>'s domain.</summary>
    public static OrganizationBoundary Of(IEnumerable<string>? domains, string? from)
    {
        var list = (domains ?? []).SelectMany(Split).Select(d => d.ToLowerInvariant()).Distinct().ToList();
        if (list.Count > 0) return new(list, fromSender: false);
        return HomeDomain(from) is { } home ? new([home], fromSender: true) : Unknown;
    }

    /// <summary>The domain of the sending address, or null when none is set.</summary>
    public static string? HomeDomain(string? from) =>
        MailAddress.TryCreate(from ?? "", out var a) ? a.Host.ToLowerInvariant() : null;

    /// <summary>Whether <paramref name="address"/> is in one of the domains or a subdomain of one. With none known,
    /// nothing is.</summary>
    public bool IsInside(string? address)
    {
        if (!Known || !MailAddress.TryCreate(address ?? "", out var a)) return false;
        var host = a.Host.ToLowerInvariant();
        return Domains.Any(d => host == d || host.EndsWith("." + d, StringComparison.Ordinal));
    }

    /// <summary>The addresses outside, each once, in order.</summary>
    public IReadOnlyList<string> Outside(IEnumerable<string?> addresses) =>
        [.. addresses.OfType<string>().Where(a => a.Length > 0 && !IsInside(a)).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// Refuses the outside addresses among <paramref name="added"/> unless <paramref name="confirmed"/>: an
    /// <see cref="OutsideAddressesException"/> naming them, saying <paramref name="consequence"/>, for the page to ask
    /// <paramref name="question"/>.
    /// </summary>
    public void Check(IEnumerable<string?> added, bool confirmed, string consequence, string question = "Save anyway?")
    {
        if (confirmed) return;
        var outside = Outside(added);
        if (outside.Count > 0) throw new OutsideAddressesException(outside, OutsideAddressesException.Say(outside, this, consequence, question));
    }

    /// <summary>The domains in a value as typed (separated by commas, semicolons, spaces or lines).</summary>
    internal static IEnumerable<string> Split(string? value) =>
        (value ?? "").Split([';', ',', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// <c>Email:OrganizationDomains</c> as it's stored ("example.org; example.net"): each a domain name (a leading "@" is
    /// dropped), lower case, once; at most <see cref="MaxDomains"/>; empty for none (the sending address's domain then).
    /// Throws <see cref="ArgumentException"/> saying what's wrong.
    /// </summary>
    public static string Normalize(string? value)
    {
        var list = new List<string>();
        foreach (var raw in Split(value))
        {
            var d = raw.TrimStart('@').TrimEnd('.').ToLowerInvariant();
            if (!IsDomain(d)) throw new ArgumentException($"“{raw}” isn't a domain name such as example.org.");
            if (!list.Contains(d)) list.Add(d);
        }
        if (list.Count > MaxDomains) throw new ArgumentException($"At most {MaxDomains} domains.");
        return string.Join("; ", list);
    }

    // A host name with at least two labels, each letters, digits and hyphens (not at either end), as an address's host is.
    private static bool IsDomain(string d)
    {
        if (d.Length is 0 or > 253) return false;
        var labels = d.Split('.');
        return labels.Length >= 2 && labels.All(l => l.Length is > 0 and <= 63 && l[0] != '-' && l[^1] != '-'
                                                     && l.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-'));
    }
}

/// <summary>
/// The organization's boundary in force: <c>Email:OrganizationDomains</c> as saved in Administration (over the server's),
/// else the domain of <c>Email:From</c>. Read afresh each time, so a saved change applies at once. Singleton.
/// </summary>
public sealed class EmailBoundary
{
    private readonly Func<EmailBoundaryOptions> _options;

    public EmailBoundary(IOptionsMonitor<EmailBoundaryOptions> options) : this(() => options.CurrentValue) { }

    private EmailBoundary(Func<EmailBoundaryOptions> options) => _options = options;

    /// <summary>A boundary that doesn't change: <paramref name="from"/>'s domain, or <paramref name="domains"/>.</summary>
    public static EmailBoundary Fixed(string? from, params string[] domains)
    {
        var o = new EmailBoundaryOptions { From = from, OrganizationDomains = domains };
        return new(() => o);
    }

    public OrganizationBoundary Current => OrganizationBoundary.Of(_options().OrganizationDomains, _options().From);

    /// <summary>The boundary with <paramref name="domains"/> in place of the saved ones (a save that changes them).</summary>
    public OrganizationBoundary With(IEnumerable<string> domains) => OrganizationBoundary.Of(domains, _options().From);
}

/// <summary>
/// Addresses outside the organization's boundary that the caller hasn't confirmed: the page asks, naming them, and saves
/// again confirmed (Copy anyway?, Save anyway?).
/// </summary>
public class OutsideAddressesException(IReadOnlyList<string> outside, string message) : InvalidOperationException(message)
{
    public IReadOnlyList<string> Outside { get; } = outside;

    /// <summary>"a@x is outside example.org. {consequence} {question}", or, with no domain known, that they can't be checked.</summary>
    public static string Say(IReadOnlyList<string> outside, OrganizationBoundary boundary, string consequence, string question)
    {
        var them = string.Join("; ", outside);
        var start = boundary.Known
            ? $"{them} {(outside.Count == 1 ? "is" : "are")} outside {boundary.Describe}."
            : $"Your organization's domain isn't known (set it on Access & distribution, or Email:From on the server), so {them} can't be checked.";
        return $"{start} {consequence} {question}";
    }
}
