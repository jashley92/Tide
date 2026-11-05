using ICSharpCode.SharpZipLib.Zip;
using Microsoft.AspNetCore.Http.Features;
using Tide.Application.Actions;
using Tide.Application.Files;

namespace Tide.Web.Security;

/// <summary>
/// The answers a file download sends: the file as it was added, or the file inside a password-protected zip (the
/// password <see cref="FileRisk.ZipPassword"/>). Either way it's an attachment of no particular type, never sniffed into
/// one, never cached, and sandboxed with nothing allowed, so whatever the browser makes of it can't run in TIDE's origin.
/// Who may download which file is the services' call (<see cref="ActionFileService.OpenAsync"/>).
/// </summary>
public static class FileDownloads
{
    /// <summary>The file as it was added.</summary>
    public static IResult Plain(HttpContext http, Stream content, string fileName)
    {
        Harden(http);
        return Results.File(content, "application/octet-stream", fileName);
    }

    /// <summary>The file inside a zip with the password "infected", named after it (report.exe.zip). The zip is written
    /// as it streams from the store: no plain copy is made on the server's disk.</summary>
    public static IResult Zipped(HttpContext http, FileDownload file)
    {
        Harden(http);
        // SharpZipLib writes synchronously.
        if (http.Features.Get<IHttpBodyControlFeature>() is { } body) body.AllowSynchronousIO = true;
        var entry = FileRisk.EntryName(file.FileName);
        return Results.Stream(output => WriteZipAsync(output, file.Content, entry, file.SizeBytes, file.AddedAtUtc.UtcDateTime),
            "application/zip", entry + ".zip");
    }

    /// <summary>Writes <paramref name="content"/> to <paramref name="output"/> as the one entry of a zip with the password
    /// "infected". Disposes <paramref name="content"/>; leaves <paramref name="output"/> open.</summary>
    public static async Task WriteZipAsync(Stream output, Stream content, string entryName, long size, DateTime modifiedUtc)
    {
        await using var source = content;
        using var zip = new ZipOutputStream(output) { IsStreamOwner = false, Password = FileRisk.ZipPassword };
        zip.UseZip64 = size >= uint.MaxValue - 1024 ? UseZip64.On : UseZip64.Off;
        zip.SetLevel(5);
        // Classic PKWARE encryption (ZipCrypto), not AES, so Windows File Explorer opens it with the password as well as any
        // zip tool. It's deliberately weak: its job is to stop a double-click running the file and antivirus deleting it on
        // arrival, not to keep it secret (the password is the field's public convention).
        zip.PutNextEntry(new ZipEntry(entryName) { DateTime = modifiedUtc, Size = size, AESKeySize = 0 });
        await source.CopyToAsync(zip, 1 << 16);
        zip.CloseEntry();
        zip.Finish();
    }

    // Nothing in the response runs, is sniffed or is kept.
    private static void Harden(HttpContext http)
    {
        var headers = http.Response.Headers;
        headers.CacheControl = "private, no-store";
        headers.XContentTypeOptions = "nosniff";
        headers.ContentSecurityPolicy = "sandbox; default-src 'none'";
    }
}
