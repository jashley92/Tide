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
    public void Check(IEnumerable<string?> added, bool confirmed, string consequence, string question = "Save anyway?") =>
        Check(added.Select(a => (a, consequence)), confirmed, question);

    /// <summary>As <see cref="Check(IEnumerable{string?}, bool, string, string)"/>, for a list being saved: only the
    /// addresses it <paramref name="had"/> no longer need asking about.</summary>
    public void CheckAdded(IEnumerable<string> now, IEnumerable<string> had, bool confirmed, string consequence) =>
        Check(now.Except(had, StringComparer.OrdinalIgnoreCase), confirmed, consequence);

    /// <summary>As above, each address with what goes to it: the message says each consequence once, for the addresses
    /// outside.</summary>
    public void Check(IEnumerable<(string? Address, string Consequence)> added, bool confirmed, string question = "Save anyway?")
    {
        if (confirmed) return;
        var outside = added.Where(a => !string.IsNullOrEmpty(a.Address) && !IsInside(a.Address)).ToList();
        if (outside.Count == 0) return;
        var addresses = Outside(outside.Select(a => a.Address));
        var says = string.Join(" ", outside.Select(a => a.Consequence).Distinct());
        throw new OutsideAddressesException(addresses, OutsideAddressesException.Say(addresses, this, says, question));
    }

    /// <summary>The domains in a value as typed (separated as an address list is).</summary>
    internal static IEnumerable<string> Split(string? value) => Settings.OperationalSettings.SplitAddresses(value);

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
/// else the domain of <c>Email:From</c>. Built again only when the options change (a saved change reloads them), so it
/// applies at once and costs nothing on each send. Singleton.
/// </summary>
public sealed class EmailBoundary
{
    private readonly IOptionsMonitor<EmailBoundaryOptions>? _monitor;
    private readonly EmailBoundaryOptions? _fixed;
    private Built? _built;

    // The options a boundary was built from, swapped whole so a reader on another thread never sees half of it.
    private sealed record Built(EmailBoundaryOptions Options, OrganizationBoundary Boundary);

    public EmailBoundary(IOptionsMonitor<EmailBoundaryOptions> options) => _monitor = options;

    private EmailBoundary(EmailBoundaryOptions options) => _fixed = options;

    /// <summary>A boundary that doesn't change: <paramref name="from"/>'s domain, or <paramref name="domains"/>.</summary>
    public static EmailBoundary Fixed(string? from, params string[] domains) => new(new EmailBoundaryOptions { From = from, OrganizationDomains = domains });

    private EmailBoundaryOptions Options => _monitor?.CurrentValue ?? _fixed!;

    /// <summary>The sending address the boundary falls back to.</summary>
    public string? From => Options.From;

    /// <summary>The domains set (in Administration or on the server), none when it falls back to <see cref="From"/>'s.</summary>
    public IReadOnlyList<string> OrganizationDomains => Options.OrganizationDomains;

    public OrganizationBoundary Current
    {
        get
        {
            var o = Options;
            if (_built is { } b && ReferenceEquals(b.Options, o)) return b.Boundary;
            var built = new Built(o, OrganizationBoundary.Of(o.OrganizationDomains, o.From));
            _built = built;
            return built.Boundary;
        }
    }

    /// <summary>The boundary with <paramref name="domains"/> in place of the saved ones (a save that changes them).</summary>
    public OrganizationBoundary With(IEnumerable<string> domains) => OrganizationBoundary.Of(domains, From);
}

public static class EmailBoundaryExtensions
{
    /// <summary>The boundary in force, or <see cref="OrganizationBoundary.Unknown"/> (nothing inside) without one.</summary>
    public static OrganizationBoundary CurrentOrUnknown(this EmailBoundary? boundary) => boundary?.Current ?? OrganizationBoundary.Unknown;
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
