using System.Globalization;
using System.Reflection;

namespace Tide.Application.Operations;

/// <summary>
/// A release's version as its tag names it: "v0.1.0", or a pre-release "v0.1.0-alpha.2" (alpha, beta or rc, then a
/// number), the forms the release workflow accepts. Ordered as SemVer orders them: a pre-release comes before its
/// release, alpha before beta before rc, and the number compares as a number (alpha.10 after alpha.9).
/// </summary>
public sealed record ReleaseVersion(int Major, int Minor, int Patch, string? Stage = null, int StageNumber = 0) : IComparable<ReleaseVersion>
{
    private static readonly string[] Stages = ["alpha", "beta", "rc"];

    public bool IsPrerelease => Stage is not null;

    /// <summary>"0.1.0-alpha.2", or "0.1.0".</summary>
    public override string ToString() => Stage is null
        ? $"{Major}.{Minor}.{Patch}"
        : $"{Major}.{Minor}.{Patch}-{Stage}.{StageNumber}";

    /// <summary>The release tag, "v0.1.0-alpha.2".</summary>
    public string Tag => "v" + this;

    /// <summary>"0.1.0-alpha.2", "v0.1.0", or an assembly's "0.1.0-alpha.2+sha" as a version; null when it isn't one.</summary>
    public static ReleaseVersion? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = text.Trim();
        if (s.StartsWith('v') || s.StartsWith('V')) s = s[1..];
        var plus = s.IndexOf('+');
        if (plus >= 0) s = s[..plus];
        string? stage = null;
        var stageNumber = 0;
        var dash = s.IndexOf('-');
        if (dash >= 0)
        {
            var pre = s[(dash + 1)..].Split('.');
            s = s[..dash];
            if (pre.Length != 2 || !Stages.Contains(pre[0].ToLowerInvariant()) || !TryNumber(pre[1], out stageNumber)) return null;
            stage = pre[0].ToLowerInvariant();
        }
        var parts = s.Split('.');
        if (parts.Length != 3 || !TryNumber(parts[0], out var major) || !TryNumber(parts[1], out var minor) || !TryNumber(parts[2], out var patch))
            return null;
        return new ReleaseVersion(major, minor, patch, stage, stageNumber);
    }

    private static bool TryNumber(string s, out int value) =>
        int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out value) && s.Length is > 0 and <= 6;

    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null) return 1;
        var c = Major.CompareTo(other.Major);
        if (c == 0) c = Minor.CompareTo(other.Minor);
        if (c == 0) c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;
        if (Stage is null || other.Stage is null) return (Stage is null).CompareTo(other.Stage is null);
        c = Array.IndexOf(Stages, Stage).CompareTo(Array.IndexOf(Stages, other.Stage));
        return c != 0 ? c : StageNumber.CompareTo(other.StageNumber);
    }

    public static bool operator <(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) < 0;
    public static bool operator >(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) > 0;
    public static bool operator <=(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) <= 0;
    public static bool operator >=(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) >= 0;
}

/// <summary>
/// The version this build is. The release workflow stamps the tag's version into every assembly
/// (<c>-p:Version=0.1.0-alpha.2</c>) and the SDK appends the commit ("0.1.0-alpha.2+&lt;sha&gt;"). A local build has no
/// version set and reads "1.0.0+&lt;sha&gt;": a development build, which the update check has nothing to compare with.
/// The release workflow also marks its builds (<c>TideReleaseBuild</c>), so the v1.0.0 release, stamped 1.0.0 too, is
/// read as the release it is.
/// </summary>
public static class AppVersionInfo
{
    private static readonly ReleaseVersion Unversioned = new(1, 0, 0);

    /// <summary>The raw informational version, e.g. "0.1.0-alpha.2+a1b2c3d…".</summary>
    public static string Informational { get; } =
        typeof(AppVersionInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "1.0.0";

    /// <summary>Whether the release workflow built this (<c>-p:TideReleaseBuild=true</c>).</summary>
    public static bool IsReleaseBuild { get; } = typeof(AppVersionInfo).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .Any(a => a.Key == "TideReleaseBuild" && a.Value == "true");

    /// <summary>The release version, or null for a development build.</summary>
    public static ReleaseVersion? Current { get; } = ReleaseOf(Informational, IsReleaseBuild);

    /// <summary>For people: "0.1.0-alpha.2 (a1b2c3d)", or "development build (a1b2c3d)".</summary>
    public static string Display => DisplayOf(Informational, IsReleaseBuild);

    /// <summary>The release version an informational version names; null for the unversioned 1.0.0 of a local build,
    /// unless the release workflow built it (<paramref name="releaseBuild"/>).</summary>
    public static ReleaseVersion? ReleaseOf(string informational, bool releaseBuild = false) =>
        ReleaseVersion.Parse(informational) is { } v && (releaseBuild || v != Unversioned) ? v : null;

    public static string? CommitOf(string informational)
    {
        var plus = informational.IndexOf('+');
        if (plus < 0 || plus == informational.Length - 1) return null;
        var sha = informational[(plus + 1)..];
        return sha.Length > 7 ? sha[..7] : sha;
    }

    public static string DisplayOf(string informational, bool releaseBuild = false)
    {
        var name = ReleaseOf(informational, releaseBuild)?.ToString() ?? "development build";
        return CommitOf(informational) is { } c ? $"{name} ({c})" : name;
    }
}
