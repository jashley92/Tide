using System.Text.RegularExpressions;
using Tide.Domain.Enums;

namespace Tide.Domain.Observables;

/// <summary>
/// Normalizes "defanged" indicator notation that analysts paste — <c>hxxp://evil[.]com</c>,
/// <c>1.1.1[.]1</c>, <c>user[at]evil(dot)com</c> — back to its canonical, live form so that
/// exact-match correlation and the IOC exports
/// all operate on real values instead of verbatim defanged strings. It also produces the
/// inverse <see cref="Defang"/> form for safe on-screen copy/paste.
/// </summary>
/// <remarks>
/// Refanging is applied only to network/address indicators (<see cref="IsRefangable"/>). Hashes,
/// file names, accounts, processes and registry keys are left byte-for-byte as entered — they can
/// legitimately contain brackets or the literal text "dot"/"at", and re-writing them would corrupt
/// the observable.
/// </remarks>
public static partial class IocObservable
{
    /// <summary>Indicator types that carry defang notation and are safe to refang.</summary>
    public static bool IsRefangable(IndicatorType type) => type is
        IndicatorType.IpAddress or IndicatorType.Domain or IndicatorType.Url or IndicatorType.EmailAddress;

    /// <summary>
    /// Best-effort classification of a pasted indicator into an <see cref="IndicatorType"/> — used to
    /// auto-type IOCs pulled from an article so the analyst doesn't pick a type per line.
    /// Only the unambiguous network/hash/email shapes are detected; anything else falls back to
    /// <see cref="IndicatorType.Other"/> and can be re-typed on the article. The value is refanged
    /// first, so a defanged paste (<c>1.1.1[.]1</c>, <c>hxxp://evil[.]com</c>) classifies correctly.
    /// </summary>
    /// <summary>
    /// The family two entities must share to be the same indicator across articles. An account and an email
    /// address with the same value are one identity (a UPN is filed either way); every other type is its own family.
    /// </summary>
    public static IndicatorType MatchFamily(IndicatorType type) => type == IndicatorType.EmailAddress ? IndicatorType.Account : type;

    /// <summary>The types in <paramref name="type"/>'s match family.</summary>
    public static IReadOnlyList<IndicatorType> FamilyTypes(IndicatorType type) =>
        MatchFamily(type) == IndicatorType.Account ? [IndicatorType.Account, IndicatorType.EmailAddress] : [type];

    public static IndicatorType DetectType(string? value)
    {
        var v = Refang(value);
        if (v.Length == 0) return IndicatorType.Other;
        // A program followed by arguments ("powershell.exe -enc …", "cmd /c …") is a command line. Checked on the
        // value as typed: refanging could rewrite an argument.
        if (CommandLineRx().IsMatch((value ?? "").Trim())) return IndicatorType.CommandLine;

        if (UrlRx().IsMatch(v)) return IndicatorType.Url;
        if (EmailRx().IsMatch(v)) return IndicatorType.EmailAddress;
        // IPAddress.TryParse is lenient (it accepts partials like "1.2.3" as 1.2.0.3), so require a real
        // dotted-quad for IPv4; IPv6 is unambiguous (it always carries ':'). IpText also reads each IPv4
        // part as decimal, so 010.0.0.1 is 10.0.0.1, never octal, and 0x0A.0.0.1 isn't an address.
        if (IpText.TryParse(v, out var ip) &&
            (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ||
             (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && CountDots(v) == 3)))
            return IndicatorType.IpAddress;
        // MD5 / SHA-1 / SHA-256 are fixed-length hex strings.
        if (v.Length is 32 or 40 or 64 && HexRx().IsMatch(v)) return IndicatorType.FileHash;
        // Wallet addresses with an unambiguous shape: Ethereum (0x and 40 hex digits) and Bitcoin bech32 (bc1…). A legacy
        // Bitcoin address (base58) looks like too many other things to guess; the analyst picks the type for it.
        if (WalletRx().IsMatch(v)) return IndicatorType.CryptoWallet;
        // A registry path (HKLM\…, HKEY_CURRENT_USER\…) before anything else with a backslash.
        if (RegistryRx().IsMatch(v)) return IndicatorType.RegistryKey;
        // Svchost.exe and invoice.docm have the shape of a domain; a common file extension reads as a file
        // name. The analyst can still pick Domain for the rare real one (a .zip domain, say).
        if (HasFileExtension(v)) return IndicatorType.FileName;
        // DOMAIN\sAMAccountName (or .\name for a local account) is an account. A file in a folder was taken above.
        if (DownLevelLogonRx().IsMatch(v)) return IndicatorType.Account;
        if (DomainRx().IsMatch(v)) return IndicatorType.Domain;
        return IndicatorType.Other;
    }

    /// <summary>
    /// File extensions seen on indicators that would otherwise read as a top-level domain. Country codes that are also
    /// script extensions (.py, .sh, .ps) are left out: those are real domains often enough.
    /// </summary>
    public static readonly IReadOnlySet<string> FileExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "exe", "dll", "sys", "scr", "cpl", "ocx", "drv", "bat", "cmd", "ps1", "psm1", "psd1", "vbs", "vbe", "js",
        "jse", "wsf", "wsh", "hta", "lnk", "msi", "msp", "msix", "appx", "iso", "img", "vhd", "vhdx", "doc",
        "docx", "docm", "dot", "dotm", "xls", "xlsx", "xlsm", "xlsb", "xll", "xlam", "ppt", "pptx", "pptm", "pdf", "rtf",
        "one", "zip", "rar", "7z", "gz", "tgz", "tar", "cab", "jar", "apk", "dmg", "pkg", "elf", "bin", "dat", "tmp",
        "ini", "cfg", "log", "txt", "csv", "json", "xml", "html", "htm", "svg", "eml", "msg", "chm", "reg", "inf",
    };

    private static bool HasFileExtension(string v)
    {
        var dot = v.LastIndexOf('.');
        return dot > 0 && dot < v.Length - 1 && v.IndexOfAny(['/', ':', '@']) < 0 && FileExtensions.Contains(v[(dot + 1)..]);
    }

    // Down-level logon name: a NetBIOS or DNS domain (or "." for the local machine), one backslash, and a sAMAccountName
    // (at most 20 characters, none of the characters Windows forbids in one).
    [GeneratedRegex(@"^(?:\.|[A-Za-z0-9][A-Za-z0-9.\-]{0,62})\\[^\\/\[\]:;|=,+*?<>""@\s]{1,20}$")]
    private static partial Regex DownLevelLogonRx();

    [GeneratedRegex(@"^(?:HKLM|HKCU|HKCR|HKU|HKCC|HKEY_[A-Z_]+)(?::)?\\", RegexOptions.IgnoreCase)]
    private static partial Regex RegistryRx();

    // A program (an executable or script by extension, optionally quoted or with a path, or a well-known shell or LOLBin)
    // then whitespace and at least one argument.
    [GeneratedRegex(@"^(?:""[^""]+\.(?:exe|bat|cmd|ps1|vbs|js|msc|cpl|scr)""|\S+\.(?:exe|bat|cmd|ps1|vbs|js|msc|cpl|scr)|(?:\S*[\\/])?(?:powershell|pwsh|cmd|wscript|cscript|mshta|rundll32|regsvr32|certutil|bitsadmin|wmic|msiexec|schtasks|reg|net|net1|sc|bash|sh|zsh|python3?|perl|curl|wget|nc|ncat))\s+\S", RegexOptions.IgnoreCase)]
    private static partial Regex CommandLineRx();

    /// <summary>
    /// One spelling per address, so the same one written two ways dedupes and matches across cases: IPv6 in its
    /// compressed lower-case form (2001:0db8:0:0:0:0:0:1 is 2001:db8::1), an IPv4-mapped IPv6 address as the IPv4 one
    /// (::ffff:203.0.113.5 is 203.0.113.5), and a zero-padded IPv4 address as a log prints it without the padding
    /// (010.000.000.001 is 10.0.0.1), so an export or a hunt query carries the form a SIEM stores. A range or anything
    /// else is left as written.
    /// </summary>
    private static string CanonicalIp(string v)
    {
        if (!IpText.TryParse(v, out var ip)) return v;
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) return v.IndexOf(':') < 0 ? ip.ToString() : v;
        if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6) return v;
        if (ip.IsIPv4MappedToIPv6) return ip.MapToIPv4().ToString();
        // A named zone ("%eth0") resolves to whatever interface index this machine has (Linux: %2, Windows: none), so the
        // canonical form would depend on the host: keep a numeric zone as written, drop a named one.
        var zone = v.IndexOf('%') is var at and >= 0 ? v[(at + 1)..] : "";
        if (zone.Length > 0 && !zone.All(char.IsAsciiDigit)) ip.ScopeId = 0;
        return ip.ToString();
    }

    private static int CountDots(string s)
    {
        var n = 0;
        foreach (var c in s) if (c == '.') n++;
        return n;
    }

    /// <summary>
    /// A single indicator parsed out of a pasted block: the refanged, canonical <see cref="Value"/> and
    /// its best-effort detected <see cref="Type"/> (which the analyst may correct before committing).
    /// </summary>
    public readonly record struct ParsedIndicator(string Value, IndicatorType Type);

    /// <summary>
    /// Splits a pasted block of indicators — one per line and/or separated by commas, semicolons or tabs —
    /// into distinct, refanged <see cref="ParsedIndicator"/>s in first-seen order, each auto-typed via
    /// <see cref="DetectType"/>. Blank tokens are dropped and case-insensitive duplicates of the same
    /// (type, value) are collapsed. Spaces are NOT separators — indicator values don't contain them, and
    /// treating a space as a break would fragment the odd token. Backs the bulk IOC paste
    /// (an analyst drops a block of IOCs and reviews them before one write).
    /// </summary>
    public static IReadOnlyList<ParsedIndicator> ParseIndicators(string? block)
    {
        var result = new List<ParsedIndicator>();
        if (string.IsNullOrWhiteSpace(block)) return result;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // A cell that reads as a command line is one indicator; its commas and semicolons are part of it.
        // Tabs (a pasted spreadsheet row's cells) are split first, so a command line can't swallow the hash beside it.
        var tokens = block.Split(['\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .SelectMany(cell => DetectType(cell) == IndicatorType.CommandLine
                ? [cell]
                : cell.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        foreach (var token in tokens)
        {
            var type = DetectType(token);
            var value = Normalize(type, token);
            if (value.Length == 0) continue;
            if (!seen.Add($"{(int)type}|{value.ToLowerInvariant()}")) continue;
            result.Add(new ParsedIndicator(value, type));
        }
        return result;
    }

    /// <summary>
    /// Trims and, for a refangable <paramref name="type"/>, refangs the value into its canonical
    /// form for storage and matching. The single choke point used by the article on add/edit.
    /// </summary>
    public static string Normalize(IndicatorType type, string? value)
    {
        var v = (value ?? string.Empty).Trim();
        // A file name defanged like a domain ("invoice[.]docm") is how it was typed as a file name, so it's kept
        // live. Only a bracketed dot, and only when what's left ends in a file extension: "report[1].pdf" and
        // "report[dot]final.docx" stay as they are.
        if (type == IndicatorType.FileName && BracketDotRx().IsMatch(v) && BracketDotRx().Replace(v, ".") is var live && HasFileExtension(live))
            return live;
        // "evil.com." is the same name as "evil.com" (the trailing dot is the DNS root), and DNS ignores case but a SIEM's
        // literal match doesn't ("EVIL.COM" in a hunt query misses the logs' "evil.com"), so a name is kept lower-case.
        if (type == IndicatorType.Domain) return Refang(v).TrimEnd('.').ToLowerInvariant();
        if (type == IndicatorType.IpAddress) return CanonicalIp(Refang(v));
        return IsRefangable(type) ? Refang(v) : v;
    }

    /// <summary>
    /// Converts a defanged indicator back to its live form. Idempotent — a value that is already
    /// live (or only partially defanged) passes through unchanged apart from a trim.
    /// </summary>
    public static string Refang(string? value)
    {
        var s = (value ?? string.Empty).Trim();
        if (s.Length == 0) return s;

        // Protocol scheme: hxxp/hxxps → http/https, fxp → ftp (case-insensitive; canonical lowercase).
        s = HxxpsRx().Replace(s, "https");
        s = HxxpRx().Replace(s, "http");
        s = FxpRx().Replace(s, "ftp");

        // Bracketed / parenthesized / braced separators, with or without the spelled-out word and
        // any padding whitespace: [://] → "://" ; [.] (.) {.} [dot] (dot) → "." ; [:] → ":" ; [/] → "/" ; [@]/[at] → "@".
        s = s.Replace("[://]", "://", StringComparison.Ordinal).Replace("(://)", "://", StringComparison.Ordinal).Replace("{://}", "://", StringComparison.Ordinal);
        s = DotRx().Replace(s, ".");
        s = ColonRx().Replace(s, ":");
        s = SlashRx().Replace(s, "/");
        s = AtRx().Replace(s, "@");

        return s.Trim();
    }

    /// <summary>
    /// Produces a defanged rendering of a live indicator for safe display and copy/paste — neutralizes
    /// the scheme and dots (and <c>@</c> in email addresses) so a value can't be clicked or auto-linked.
    /// An IPv4 address has only its last dot bracketed (<c>203.0.113[.]45</c>), as advisories and most tools write it.
    /// </summary>
    public static string Defang(string? value)
    {
        var s = (value ?? string.Empty).Trim();
        if (s.Length == 0) return s;
        if (Ipv4Rx().IsMatch(s))
        {
            var last = s.LastIndexOf('.');
            return s[..last] + "[.]" + s[(last + 1)..];
        }

        s = HttpSchemeRx().Replace(s, m => m.Value.Length == 5 ? "hxxps" : "hxxp");
        s = s.Replace(".", "[.]");
        s = s.Replace("@", "[at]");
        return s;
    }

    /// <summary>
    /// <paramref name="value"/> defanged when it's a kind that carries defang notation (an address, domain, URL or
    /// email); anything else (a file name, a hash, a host name) as it was typed, since "cook.pth" isn't a link, except
    /// that a web address inside it (a command line's download, "iwr https://…") has its scheme defanged: a mail client
    /// would otherwise make it a live link.
    /// </summary>
    public static string Defang(IndicatorType type, string? value) => IsRefangable(type)
        ? Defang(value)
        : HttpSchemeRx().Replace((value ?? "").Trim(), m => m.Value.Length == 5 ? "hxxps" : "hxxp");

    // Refang patterns. \b avoids rewriting the middle of an unrelated token.
    [GeneratedRegex(@"\bhxxps\b", RegexOptions.IgnoreCase)] private static partial Regex HxxpsRx();
    [GeneratedRegex(@"\bhxxp\b", RegexOptions.IgnoreCase)] private static partial Regex HxxpRx();
    [GeneratedRegex(@"\bfxp(?=:)", RegexOptions.IgnoreCase)] private static partial Regex FxpRx();
    [GeneratedRegex(@"[\[\(\{]\s*(?:\.|dot)\s*[\]\)\}]", RegexOptions.IgnoreCase)] private static partial Regex DotRx();
    [GeneratedRegex(@"[\[\(\{]\s*:\s*[\]\)\}]")] private static partial Regex ColonRx();
    [GeneratedRegex(@"[\[\(\{]\s*\.\s*[\]\)\}]")] private static partial Regex BracketDotRx();
    [GeneratedRegex(@"[\[\(\{]\s*/\s*[\]\)\}]")] private static partial Regex SlashRx();
    [GeneratedRegex(@"[\[\(\{]\s*(?:@|at)\s*[\]\)\}]", RegexOptions.IgnoreCase)] private static partial Regex AtRx();

    // Defang: match an http/https scheme (with its ://) so we can neuter it in one pass.
    [GeneratedRegex(@"\bhttps?(?=://)", RegexOptions.IgnoreCase)] private static partial Regex HttpSchemeRx();

    [GeneratedRegex(@"^\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}$")] private static partial Regex Ipv4Rx();
    [GeneratedRegex(@"^(?:0x[0-9a-fA-F]{40}|bc1[ac-hj-np-z02-9]{11,71})$")] private static partial Regex WalletRx();

    // Classification patterns (DetectType).
    [GeneratedRegex(@"^[a-z][a-z0-9+.\-]*://\S+$", RegexOptions.IgnoreCase)] private static partial Regex UrlRx();
    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")] private static partial Regex EmailRx();
    [GeneratedRegex(@"^[0-9a-f]+$", RegexOptions.IgnoreCase)] private static partial Regex HexRx();
    // Labels of letters and digits (any script, for internationalized names) with hyphens inside, so punycode
    // ("xn--80ak6aa92e.com", the "xn--p1ai" TLD) and "my--bank.com" are domains; a trailing dot (how DNS logs print a name)
    // is allowed, and Normalize drops it.
    [GeneratedRegex(@"^(?=.{1,253}\.?$)(?:[\p{L}\p{N}](?:[\p{L}\p{N}-]{0,61}[\p{L}\p{N}])?\.)+(?:\p{L}{2,63}|xn--[a-z0-9-]{1,59})\.?$",
        RegexOptions.IgnoreCase)] private static partial Regex DomainRx();
}
