namespace Tide.UnitTests;

/// <summary>
/// A test that needs the openssl command line (the Release workflow signs with it on its Ubuntu runner). It runs where
/// openssl is on PATH and is reported as skipped elsewhere.
/// </summary>
public sealed class OpenSslFactAttribute : FactAttribute
{
    public OpenSslFactAttribute()
    {
        if (Path() is null) Skip = "openssl isn't on PATH.";
    }

    public static string? Path()
    {
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        var name = OperatingSystem.IsWindows() ? "openssl.exe" : "openssl";
        return dirs.Select(d => System.IO.Path.Combine(d, name)).FirstOrDefault(File.Exists);
    }
}
