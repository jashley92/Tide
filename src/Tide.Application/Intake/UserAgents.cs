using System.Globalization;
using Tide.Application.Operations;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.Application.Intake;

/// <summary>
/// The User-Agent outbound requests send. Globally (<c>Intake:UserAgent</c>): TIDE's own, naming the running release and
/// the project (the default); a current desktop browser's; or a string the administrator typed. A source can choose its
/// own for its feed and for reading its articles in triage (<see cref="Source.UserAgent"/>), or inherit the global one.
/// Every value sent is printable ASCII, so a header can't be split.
/// </summary>
public static class UserAgents
{
    public const string ProjectUrl = "https://github.com/jashley92/Tide";

    /// <summary>
    /// A current Chrome on Windows, in the reduced form Chrome itself sends (the minor version numbers are zeros). Needs
    /// an occasional refresh: Chrome ships a new major version every few weeks, and a site that turns non-browsers away
    /// may in time treat a very old one as a bot too. Chrome 157, stable from November 2026.
    /// </summary>
    public const string Browser = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/157.0.0.0 Safari/537.36";

    /// <summary>TIDE's own for this build: "TIDE/1.14.0 (+https://github.com/jashley92/Tide)".</summary>
    public static string Tide { get; } = TideFor(AppVersionInfo.Informational, AppVersionInfo.IsReleaseBuild);

    /// <summary>TIDE's own for a build's informational version: the release ("1.14.0", "1.15.0-rc.1"), or "dev" with the
    /// commit for a development build ("dev+a1b2c3d").</summary>
    public static string TideFor(string informational, bool releaseBuild = false)
    {
        var version = AppVersionInfo.ReleaseOf(informational, releaseBuild)?.ToString()
                      ?? (AppVersionInfo.CommitOf(informational) is { } commit ? "dev+" + commit : "dev");
        return $"TIDE/{version} (+{ProjectUrl})";
    }

    /// <summary>Why <paramref name="value"/> can't be sent as a User-Agent, or null when it can: 1 to
    /// <see cref="Source.MaxUserAgent"/> printable ASCII characters once trimmed (no line breaks, tabs or other control
    /// characters, so no header can be added through it).</summary>
    public static string? Problem(string? value)
    {
        var v = (value ?? "").Trim();
        if (v.Length == 0) return "Enter the user agent to send.";
        if (v.Length > Source.MaxUserAgent)
            return $"A user agent is at most {Source.MaxUserAgent.ToString(CultureInfo.InvariantCulture)} characters.";
        return v.All(c => c is >= ' ' and <= '~') ? null
            : "A user agent is plain printable ASCII: letters, digits, spaces and punctuation, with no line breaks or accented letters.";
    }

    /// <summary>The value trimmed when it can be sent; null when it can't (<see cref="Problem"/>).</summary>
    public static string? Valid(string? value) => Problem(value) is null ? value!.Trim() : null;

    /// <summary>The value as sent: trimmed, or <see cref="ArgumentException"/> saying what's wrong. Blank stays blank
    /// (the global custom string when nothing is typed).</summary>
    public static string Normalize(string? value)
    {
        var v = (value ?? "").Trim();
        if (v.Length == 0) return "";
        return Problem(v) is { } problem ? throw new ArgumentException(problem) : v;
    }

    /// <summary>What a choice sends; null for <see cref="UserAgentChoice.Inherit"/>, or a custom string that can't be sent.</summary>
    public static string? Choose(UserAgentChoice choice, string? custom) => choice switch
    {
        UserAgentChoice.Tide => Tide,
        UserAgentChoice.Browser => Browser,
        UserAgentChoice.Custom => Valid(custom),
        _ => null,
    };

    /// <summary>What every request sends unless its source chooses otherwise: TIDE's own when nothing else is set.</summary>
    public static string Global(IntakeOptions options) => Choose(options.UserAgent, options.CustomUserAgent) ?? Tide;

    /// <summary>The source's own choice for its feed and articles; null when it inherits the global one.</summary>
    public static string? For(Source? source) => source is null ? null : Choose(source.UserAgent, source.CustomUserAgent);
}
