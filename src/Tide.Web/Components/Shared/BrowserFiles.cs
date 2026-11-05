using Microsoft.AspNetCore.Components.Forms;

namespace Tide.Web.Components.Shared;

/// <summary>Files picked, dropped or pasted in the browser, read into the circuit.</summary>
public static class BrowserFiles
{
    /// <summary>
    /// A file's bytes, read up to <paramref name="maxBytes"/> (more throws), and the type its browser declared (null when it
    /// declared none). The buffer is sized to the file up front, and handed over as it is when the file filled it.
    /// </summary>
    public static async Task<(byte[] Bytes, string? DeclaredType)> ReadAsync(IBrowserFile file, long maxBytes)
    {
        await using var stream = file.OpenReadStream(maxBytes);
        using var ms = new MemoryStream((int)Math.Clamp(file.Size, 0, Math.Min(maxBytes, Array.MaxLength)));
        await stream.CopyToAsync(ms);
        var bytes = ms.Length == ms.Capacity ? ms.GetBuffer() : ms.ToArray();
        return (bytes, string.IsNullOrWhiteSpace(file.ContentType) ? null : file.ContentType);
    }
}
