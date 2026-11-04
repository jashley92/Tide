using System.Text.RegularExpressions;

namespace Tide.Web.Services;

/// <summary>
/// Reads <c>App:PathBase</c>, the sub-path TIDE is served under when a reverse proxy (not IIS, which sets it itself)
/// forwards one, such as <c>/tide</c>. Blank or <c>/</c> means the app is at the root of its host.
/// </summary>
public static partial class PathBaseSetting
{
    /// <summary>
    /// The path base as <c>/segment[/segment]</c>, or null for none. Throws on anything else, so a typo stops startup
    /// rather than serving every page at the wrong address.
    /// </summary>
    public static string? Normalize(string? value)
    {
        var trimmed = (value ?? "").Trim().Trim('/');
        if (trimmed.Length == 0) return null;
        if (!Segments().IsMatch(trimmed))
            throw new InvalidOperationException(
                $"App:PathBase must be a path such as /tide (letters, digits, '-', '_' and '.', in '/'-separated parts); got '{value}'.");
        return "/" + trimmed;
    }

    [GeneratedRegex(@"^(?!\.+(/|$))[A-Za-z0-9_\-.]+(/(?!\.+(/|$))[A-Za-z0-9_\-.]+)*$")]
    private static partial Regex Segments();
}
