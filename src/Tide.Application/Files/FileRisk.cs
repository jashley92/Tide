namespace Tide.Application.Files;

/// <summary>When a file on an action downloads inside a password-protected zip by default (<c>Files:ZipDownloads</c>).</summary>
public enum ZipDownloads
{
    /// <summary>Only files that can run, or carry something that runs (the default).</summary>
    RiskyOnly,

    /// <summary>Every file.</summary>
    Always,

    /// <summary>No file: each downloads as it is.</summary>
    Never,
}

/// <summary>What was found about an uploaded file from its name, the type its browser declared and its first bytes.</summary>
/// <param name="ImageType">The raster image type its bytes are (image/png…), or null when they aren't one.</param>
/// <param name="TypeNote">Where the name or declared type and the bytes disagree, said for people; null when they don't.</param>
/// <param name="RiskReason">Why it can run, or carry something that runs, when opened; null when nothing suggests it.</param>
public sealed record FileInspection(string? ImageType, string? TypeNote, string? RiskReason)
{
    public bool IsImage => ImageType is not null;

    /// <summary>Whether it may be shown inline: a real raster image, named and sent as what it is, that can't run.</summary>
    public bool ShowsInline => IsImage && TypeNote is null && RiskReason is null;
}

/// <summary>
/// Judges files people add to actions. A file is risky when it can run on the computer that opens it, or carries
/// something that does: programs, scripts, installers, shortcuts, disk images, Office files with macros, pages that run
/// script from disk. That's judged on all three of its name, its declared type and its first bytes, since any of them
/// can be wrong or chosen to mislead. A risky file downloads by default inside a zip with the password
/// <see cref="ZipPassword"/>, so a double-click can't run it and antivirus doesn't remove it on arrival.
/// </summary>
public static class FileRisk
{
    /// <summary>How many leading bytes are read to judge a file.</summary>
    public const int HeaderLength = 16;

    /// <summary>The password on every zip: the security field's public convention for samples, so anyone receiving one knows it.</summary>
    public const string ZipPassword = "infected"; // NOSONAR S2068: not a credential; a published convention shown to every user

    private static readonly HashSet<string> RunnableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        // Windows programs, libraries, installers and control-panel items
        ".exe", ".dll", ".scr", ".com", ".pif", ".cpl", ".sys", ".drv", ".ocx", ".msi", ".msp", ".msc", ".mst", ".appx",
        ".appxbundle", ".msix", ".msixbundle", ".application", ".gadget", ".xll",
        // scripts
        ".bat", ".cmd", ".ps1", ".psm1", ".psd1", ".vbs", ".vbe", ".js", ".jse", ".mjs", ".wsf", ".wsh", ".wsc", ".sct",
        ".sh", ".bash", ".zsh", ".py", ".pyw", ".pl", ".rb", ".php", ".jar", ".class",
        // shortcuts, settings and help files that run things
        ".hta", ".lnk", ".url", ".scf", ".reg", ".inf", ".chm", ".settingcontent-ms", ".library-ms",
        // disk images that mount and can autorun
        ".iso", ".img", ".vhd", ".vhdx",
        // other platforms' programs and packages
        ".apk", ".app", ".dmg", ".pkg", ".deb", ".rpm", ".elf", ".so", ".dylib", ".run",
        // Office files with macros, and OneNote (which carries attachments that run)
        ".docm", ".dotm", ".xlsm", ".xltm", ".xlsb", ".xlam", ".pptm", ".potm", ".ppam", ".ppsm", ".sldm", ".one",
        // pages that run script when opened from disk
        ".html", ".htm", ".xhtml", ".svg", ".mht", ".mhtml", ".xht",
    };

    private static readonly HashSet<string> RunnableTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/x-msdownload", "application/x-dosexec", "application/x-msdos-program", "application/x-ms-installer",
        "application/x-msi", "application/vnd.microsoft.portable-executable", "application/x-executable", "application/x-elf",
        "application/x-mach-binary", "application/java-archive", "application/vnd.android.package-archive",
        "application/x-ms-shortcut", "application/hta", "application/x-sh", "application/x-bat", "application/x-csh",
        "application/javascript", "text/javascript", "application/x-javascript", "application/x-powershell",
        "text/html", "application/xhtml+xml", "image/svg+xml", "application/x-iso9660-image", "application/x-apple-diskimage",
    };

    private static readonly byte[] CompoundFileMagic = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    private static readonly Dictionary<string, string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".jfif"] = "image/jpeg",
        [".gif"] = "image/gif", [".webp"] = "image/webp",
    };

    /// <summary>Why the file can run, or carry something that runs, as a phrase ("it's a Windows program"); null when
    /// nothing about it suggests so. The bytes are judged first, as they can't be renamed.</summary>
    public static string? Reason(string? fileName, string? declaredType, ReadOnlySpan<byte> header = default)
    {
        if (ByBytes(header) is { } bytes) return bytes;
        var ext = Extension(fileName);
        if (ext.Length > 0 && RunnableExtensions.Contains(ext)) return $"a {ext.ToLowerInvariant()} file can run on the computer that opens it";
        var type = BareType(declaredType);
        if (type.Contains("macroenabled", StringComparison.OrdinalIgnoreCase)) return "an Office file with macros can run code";
        if (RunnableTypes.Contains(type)) return $"it was sent as {type}, which can run on the computer that opens it";
        return null;
    }

    /// <summary>The reason as a sentence, for a note or a title: "It's a Windows program."</summary>
    public static string? Sentence(string? reason) =>
        string.IsNullOrEmpty(reason) ? null : char.ToUpperInvariant(reason[0]) + reason[1..] + ".";

    /// <summary>Judges a file from its name, declared type and bytes (only the first <see cref="HeaderLength"/> are read).</summary>
    public static FileInspection Inspect(string? fileName, string? declaredType, ReadOnlySpan<byte> bytes)
    {
        var header = bytes.Length > HeaderLength ? bytes[..HeaderLength] : bytes;
        var image = ImageSniffer.RasterContentType(header);
        return new FileInspection(image, TypeNote(fileName, declaredType, image), Reason(fileName, declaredType, header));
    }

    /// <summary>Whether the zip is the main download for a file under <paramref name="mode"/>.</summary>
    public static bool ZipFirst(ZipDownloads mode, string? riskReason) => mode switch
    {
        ZipDownloads.Always => true,
        ZipDownloads.Never => false,
        _ => riskReason is not null,
    };

    /// <summary>The name of the file inside its zip: no folder, nothing an unzip tool could be steered by, never empty.</summary>
    public static string EntryName(string? fileName)
    {
        var name = (fileName ?? "").Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        var clean = new string(name.Where(c => c >= 0x20 && c != 0x7F && "<>:\"|?*".IndexOf(c) < 0).ToArray()).Trim(' ', '.');
        if (clean.Length == 0) return "file.bin";
        return clean.Length > 200 ? clean[^200..] : clean;
    }

    // Where the name or declared type and the bytes disagree about being an image.
    private static string? TypeNote(string? fileName, string? declaredType, string? image)
    {
        var byName = ImageExtensions.GetValueOrDefault(Extension(fileName));
        var declared = BareType(declaredType);
        // An SVG is a drawing, not a raster image: it's judged by its risk (it can carry script), not here.
        var byType = declared.StartsWith("image/", StringComparison.OrdinalIgnoreCase) && !declared.StartsWith("image/svg", StringComparison.OrdinalIgnoreCase)
            ? declared.ToLowerInvariant() : null;
        if (byType is "image/jpg" or "image/pjpeg") byType = "image/jpeg";
        var claimed = byName ?? byType;
        if (claimed is null)
            return image is not null && Extension(fileName).Length > 0 ? $"Its contents are {Article(image)} image, not what its name says." : null;
        if (image is null)
            return $"It's {(byName is not null ? "named" : "sent")} as {Article(claimed)} image, but its contents aren't one.";
        if (byName is not null && byName != image) return $"It's named as {Article(byName)} image, but its contents are {Article(image)} image.";
        if (byType is not null && byType != image) return $"It was sent as {Article(byType)} image, but its contents are {Article(image)} image.";
        return null;
    }

    private static string? ByBytes(ReadOnlySpan<byte> h)
    {
        if (h.Length >= 2 && h[0] == (byte)'M' && h[1] == (byte)'Z') return "it's a Windows program";
        if (h.Length >= 4 && h[0] == 0x7F && h[1] == (byte)'E' && h[2] == (byte)'L' && h[3] == (byte)'F') return "it's a Linux program";
        if (h.Length >= 4)
        {
            var magic = (uint)(h[0] << 24 | h[1] << 16 | h[2] << 8 | h[3]);
            if (magic is 0xFEEDFACE or 0xFEEDFACF or 0xCEFAEDFE or 0xCFFAEDFE or 0xCAFEBABE) return "it's a macOS or Java program";
        }
        if (h.Length >= 2 && h[0] == (byte)'#' && h[1] == (byte)'!') return "it's a script";
        if (h.StartsWith(CompoundFileMagic)) return "it's a legacy Office or Outlook file, which can carry macros or attachments";
        return null;
    }

    private static string Extension(string? fileName)
    {
        var name = (fileName ?? "").TrimEnd('.', ' ');
        var dot = name.LastIndexOf('.');
        return dot < 0 || dot < name.LastIndexOfAny(['/', '\\']) ? "" : name[dot..];
    }

    private static string BareType(string? type) => (type ?? "").Split(';')[0].Trim();

    private static string Name(string imageType) => imageType switch
    {
        "image/png" => "PNG",
        "image/jpeg" => "JPEG",
        "image/gif" => "GIF",
        "image/webp" => "WebP",
        _ => imageType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ? imageType[6..].ToUpperInvariant() : imageType,
    };

    // Every image type named here (PNG, JPEG, GIF, WebP) takes "a".
    private static string Article(string imageType) => "a " + Name(imageType);
}
