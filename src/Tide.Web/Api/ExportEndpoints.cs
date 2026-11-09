using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Http.HttpResults;
using Tide.Application.History;
using Tide.Application.Security;
using Tide.Domain.Enums;
using Tide.Web.Security;

namespace Tide.Web.Api;

/// <summary>When a download is written to the audit log as well as the access log.</summary>
public enum AuditedWhen
{
    /// <summary>The access log only.</summary>
    Never,

    /// <summary>When its TLP ceiling (<see cref="ExportEndpoints.NoteCeiling"/>) is above TLP:GREEN.</summary>
    AboveGreen,

    /// <summary>Every time.</summary>
    Always,
}

/// <summary>
/// What every download endpoint (exports, CSVs, email previews, the SharePoint zip) has in common. Each download that's
/// sent is recorded (<see cref="Recorded"/>): a line in the access log (who, the route, the record it names, the TLP
/// ceiling and the size), and, where the endpoint asks for it (<see cref="Audited"/>), a row in the audit log
/// (<see cref="DownloadAudit"/>).
/// </summary>
public static class ExportEndpoints
{
    /// <summary>The downloads' rate limit (its numbers are set with the others in Program.cs).</summary>
    public const string RateLimit = "exports";

    private const string CeilingItem = "Tide.Download.Ceiling", BytesItem = "Tide.Download.Bytes";

    /// <summary>Held to <paramref name="policy"/> and the exports rate limit, never kept by a cache, and recorded.</summary>
    public static RouteHandlerBuilder AsExport(this RouteHandlerBuilder endpoint, string policy = Policies.ReadTeamEdition) =>
        endpoint.RequireAuthorization(policy).RequireRateLimiting(RateLimit).AddEndpointFilter(NoStore).Recorded();

    /// <summary>Each download sent from <paramref name="endpoint"/> is in the access log, and in the audit log as
    /// <paramref name="when"/> says.</summary>
    public static RouteHandlerBuilder Recorded(this RouteHandlerBuilder endpoint, AuditedWhen when = AuditedWhen.Never) =>
        endpoint.AddEndpointFilter(RecordAsync).Audited(when);

    /// <summary>For an endpoint already recorded (<see cref="AsExport"/>): when its downloads are in the audit log too.</summary>
    public static RouteHandlerBuilder Audited(this RouteHandlerBuilder endpoint, AuditedWhen when) => endpoint.WithMetadata(new DownloadAuditing(when));

    /// <summary>The TLP ceiling this download was held to, for its record.</summary>
    public static void NoteCeiling(HttpContext http, TlpLevel ceiling) => http.Items[CeilingItem] = ceiling;

    /// <summary>The size this download's record gives when its answer doesn't know it (a zip written as it streams: the
    /// file's own size).</summary>
    public static void NoteBytes(HttpContext http, long bytes) => http.Items[BytesItem] = bytes;

    private sealed record DownloadAuditing(AuditedWhen When);

    // After the handler: an answer that sends something (a file, a page, a text) is recorded; a refusal, a redirect or a
    // "not found" isn't (a refusal is logged where it's refused). The audit row is written before the answer is sent, so a
    // download the log can't hold isn't sent.
    private static async ValueTask<object?> RecordAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var result = await next(context);
        if (!Sends(result)) return result;
        var http = context.HttpContext;
        var endpoint = http.GetEndpoint();
        var route = (endpoint as RouteEndpoint)?.RoutePattern.RawText ?? http.Request.Path.Value ?? "";
        var key = string.Join('/', http.Request.RouteValues.Values.Select(v => Convert.ToString(v, CultureInfo.InvariantCulture)));
        TlpLevel? ceiling = http.Items.TryGetValue(CeilingItem, out var c) && c is TlpLevel t ? t : null;
        var (name, bytes) = Describe(result, http);
        http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(AmbientUser.AccessLogCategory).LogInformation(
            "Download by {User}: {Route} {Key}, TLP ceiling {Ceiling}, {Bytes} bytes, as {FileName}",
            http.User.Identity?.Name ?? "anonymous", route, key, ceiling is { } l ? Tlp.Label(l) : "none", bytes?.ToString(CultureInfo.InvariantCulture) ?? "unknown", name ?? "-");
        var when = endpoint?.Metadata.GetMetadata<DownloadAuditing>()?.When ?? AuditedWhen.Never;
        if (when == AuditedWhen.Always || (when == AuditedWhen.AboveGreen && ceiling > TlpLevel.Green))
            await http.RequestServices.GetRequiredService<DownloadAudit>()
                .RecordAsync(new DownloadRecord(route, key.Length > 0 ? key : route, name, ceiling, bytes), http.RequestAborted);
        return result;
    }

    private static bool Sends(object? result) => result switch
    {
        IFileHttpResult => true,
        RedirectHttpResult => false,
        IStatusCodeHttpResult s => s.StatusCode is null or (>= 200 and < 300),
        _ => false,
    };

    private static (string? Name, long? Bytes) Describe(object? result, HttpContext http)
    {
        long? noted = http.Items.TryGetValue(BytesItem, out var b) && b is long n ? n : null;
        return result switch
        {
            FileContentHttpResult f => (f.FileDownloadName, f.FileLength ?? noted),
            FileStreamHttpResult f => (f.FileDownloadName, f.FileLength ?? noted),
            IFileHttpResult f => (f.FileDownloadName, noted),
            ContentHttpResult text => (null, text.ResponseContent is { } s ? Encoding.UTF8.GetByteCount(s) : noted),
            _ => (null, noted),
        };
    }

    // An export is the reader's own answer, built for them now: never kept by a shared cache or on disk after the download.
    private static ValueTask<object?> NoStore(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        context.HttpContext.Response.Headers.CacheControl = "private, no-store";
        return next(context);
    }

    /// <summary>A CSV download with a UTF-8 byte-order mark, so Excel opens it as UTF-8 rather than the local code page.</summary>
    public static IResult CsvFile(string csv, string fileName) => Results.File(Utf8WithBom(csv), "text/csv; charset=utf-8", fileName);

    /// <summary><paramref name="text"/> as UTF-8, after a byte-order mark.</summary>
    public static byte[] Utf8WithBom(string text)
    {
        var preamble = Encoding.UTF8.Preamble;
        var bytes = new byte[preamble.Length + Encoding.UTF8.GetByteCount(text)];
        preamble.CopyTo(bytes);
        Encoding.UTF8.GetBytes(text, bytes.AsSpan(preamble.Length));
        return bytes;
    }
}
