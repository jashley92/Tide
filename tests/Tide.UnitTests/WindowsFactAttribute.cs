namespace Tide.UnitTests;

/// <summary>
/// A test of something that exists only on Windows (robocopy, Task Scheduler, Windows paths). It runs on Windows and is
/// reported as skipped elsewhere, rather than returning early and counting as a pass on CI's Linux runner.
/// </summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Windows only.";
    }
}
