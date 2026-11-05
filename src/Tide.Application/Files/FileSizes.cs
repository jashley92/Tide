using System.Globalization;

namespace Tide.Application.Files;

/// <summary>File sizes as people read them, wherever they're written (the page, an action's history).</summary>
public static class FileSizes
{
    /// <summary>"512 B", "40 KB", "2.5 MB".</summary>
    public static string Text(long bytes) => bytes switch
    {
        < 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes} B"),
        < 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0} KB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0 / 1024.0:0.0} MB"),
    };
}
