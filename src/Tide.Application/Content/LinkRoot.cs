namespace Tide.Application.Content;

/// <summary>
/// Where a Markdown link or image that starts at the server's root (<c>[x](/actions/12)</c>) points when it's shown.
/// Under a sub-path (<c>/tide</c>) the server's root isn't TIDE, so on a page the sub-path goes in front
/// (<see cref="Web"/>), and in an email, which has no page to be relative to, the app's address does (<see cref="Email"/>).
/// One that already starts with the sub-path (<c>/tide/actions/12</c>) isn't given it twice. A protocol-relative
/// address (<c>//host</c>) isn't root-relative: it names another host, and <see cref="MarkdownService"/> refuses it.
/// </summary>
public sealed class LinkRoot
{
    private readonly string _origin;     // "https://tide.example.org" in an email, "" on a page
    private readonly string _pathBase;   // "/tide", or "" at the server's root
    private readonly bool _nowhere;      // an email with no App:BaseUrl: the link is left as its text

    private LinkRoot(string origin, string pathBase, bool nowhere) => (_origin, _pathBase, _nowhere) = (origin, pathBase, nowhere);

    /// <summary>Root-relative links as they were typed.</summary>
    public static LinkRoot None { get; } = new("", "", false);

    /// <summary>On a page served under <paramref name="pathBase"/> (<c>/tide</c>; blank or <c>/</c> at the root).</summary>
    public static LinkRoot Web(string? pathBase)
    {
        var p = (pathBase ?? "").Trim().TrimEnd('/');
        if (p.Length == 0) return None;
        return new("", p.StartsWith('/') ? p : "/" + p, false);
    }

    /// <summary>On a page whose base address is <paramref name="baseUri"/> (<c>NavigationManager.BaseUri</c>,
    /// <c>https://host/tide/</c>): its path is the sub-path.</summary>
    public static LinkRoot FromBaseUri(string? baseUri) =>
        Uri.TryCreate(baseUri, UriKind.Absolute, out var u) ? Web(u.AbsolutePath) : None;

    /// <summary>In an email, with the app's address (<c>App:BaseUrl</c>, server and sub-path). When that isn't set
    /// the link is left out and its text kept, as the CSV exports leave the address out: <c>/actions/12</c> on its own
    /// is nothing a mail client can open.</summary>
    public static LinkRoot Email(string? appUrl)
    {
        if (string.IsNullOrWhiteSpace(appUrl) || !Uri.TryCreate(appUrl.Trim(), UriKind.Absolute, out var u)
            || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps))
            return new("", "", true);
        return new(u.GetLeftPart(UriPartial.Authority), u.AbsolutePath.TrimEnd('/'), false);
    }

    /// <summary>Whether <paramref name="url"/> starts at the server's root: one <c>/</c>, not <c>//</c>.</summary>
    public static bool IsRootRelative(string? url) =>
        url is { Length: > 0 } && url[0] == '/' && (url.Length == 1 || (url[1] != '/' && url[1] != '\\'));

    /// <summary>A root-relative <paramref name="url"/> pointed here, or null when there's nowhere to point it.</summary>
    public string? Resolve(string url)
    {
        if (_nowhere) return null;
        return _origin + (HasPathBase(url) ? url : _pathBase + url);
    }

    private bool HasPathBase(string url) =>
        _pathBase.Length == 0 || (url.StartsWith(_pathBase, StringComparison.OrdinalIgnoreCase)
            && (url.Length == _pathBase.Length || url[_pathBase.Length] is '/' or '?' or '#'));
}
