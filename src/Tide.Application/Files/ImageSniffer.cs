namespace Tide.Application.Files;

/// <summary>
/// Content-based raster-image detection for the inline image endpoint. The inline path renders
/// bytes <b>in the browser</b> (no attachment disposition), so it must trust the actual bytes, not the
/// client-supplied Content-Type stored at upload. It recognises only genuine raster images (PNG, JPEG,
/// GIF, WEBP) by magic number — an active format such as SVG, or a mislabeled non-image file, is not
/// recognised and is refused, so stored content can never ride this endpoint as script even if the CSP
/// were relaxed.
/// </summary>
public static class ImageSniffer
{
    /// <summary>
    /// Returns the canonical raster-image content-type for <paramref name="header"/> (a file's leading
    /// bytes — 12 are enough for every type below), or <c>null</c> if the bytes are not a recognised
    /// raster image. SVG and non-image content deliberately return <c>null</c>.
    /// </summary>
    public static string? RasterContentType(ReadOnlySpan<byte> header)
    {
        // PNG: 89 50 4E 47 0D 0A 1A 0A
        if (header.Length >= 8 &&
            header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47 &&
            header[4] == 0x0D && header[5] == 0x0A && header[6] == 0x1A && header[7] == 0x0A)
            return "image/png";

        // JPEG: FF D8 FF
        if (header.Length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
            return "image/jpeg";

        // GIF: "GIF87a" or "GIF89a"
        if (header.Length >= 6 &&
            header[0] == (byte)'G' && header[1] == (byte)'I' && header[2] == (byte)'F' && header[3] == (byte)'8' &&
            (header[4] == (byte)'7' || header[4] == (byte)'9') && header[5] == (byte)'a')
            return "image/gif";

        // WEBP: "RIFF" <4-byte size> "WEBP"
        if (header.Length >= 12 &&
            header[0] == (byte)'R' && header[1] == (byte)'I' && header[2] == (byte)'F' && header[3] == (byte)'F' &&
            header[8] == (byte)'W' && header[9] == (byte)'E' && header[10] == (byte)'B' && header[11] == (byte)'P')
            return "image/webp";

        return null;
    }

    /// <summary>
    /// The pixel size of a PNG, GIF, JPEG or WebP from its bytes, or null when it can't be read. Email needs it: Outlook
    /// ignores CSS max-width and draws an image at its width attribute, so the attribute must be right.
    /// </summary>
    public static (int Width, int Height)? Dimensions(ReadOnlySpan<byte> b)
    {
        switch (RasterContentType(b))
        {
            case "image/png" when b.Length >= 24:
                return (BigEndian32(b[16..]), BigEndian32(b[20..]));
            case "image/gif" when b.Length >= 10:
                return (b[6] | b[7] << 8, b[8] | b[9] << 8);
            case "image/jpeg":
                return JpegDimensions(b);
            case "image/webp" when b.Length >= 30:
                var chunk = System.Text.Encoding.ASCII.GetString(b[12..16]);
                return chunk switch
                {
                    "VP8 " => ((b[26] | b[27] << 8) & 0x3FFF, (b[28] | b[29] << 8) & 0x3FFF),
                    "VP8L" => (1 + (b[21] | (b[22] & 0x3F) << 8), 1 + ((b[22] >> 6) | b[23] << 2 | (b[24] & 0x0F) << 10)),
                    "VP8X" => (1 + (b[24] | b[25] << 8 | b[26] << 16), 1 + (b[27] | b[28] << 8 | b[29] << 16)),
                    _ => null,
                };
            default:
                return null;
        }
    }

    private static int BigEndian32(ReadOnlySpan<byte> b) => b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3];

    // Walks the JPEG segments to the first start-of-frame (SOF0-SOF15, but not DHT, JPG or DAC), which holds the size.
    private static (int, int)? JpegDimensions(ReadOnlySpan<byte> b)
    {
        var i = 2;
        while (i + 9 < b.Length)
        {
            if (b[i] != 0xFF) return null;
            var marker = b[i + 1];
            if (marker == 0xFF) { i++; continue; }
            var length = b[i + 2] << 8 | b[i + 3];
            if (marker is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC))
                return (b[i + 7] << 8 | b[i + 8], b[i + 5] << 8 | b[i + 6]);
            if (length < 2) return null;
            i += 2 + length;
        }
        return null;
    }
}
