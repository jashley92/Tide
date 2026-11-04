using System.Text;
using Tide.Application.Security;

namespace Tide.Web.Api;

/// <summary>What every download endpoint (exports, CSVs, email previews, the SharePoint zip) has in common.</summary>
public static class ExportEndpoints
{
    /// <summary>The downloads' rate limit (its numbers are set with the others in Program.cs).</summary>
    public const string RateLimit = "exports";

    /// <summary>Held to <paramref name="policy"/> and the exports rate limit, and never kept by a cache.</summary>
    public static RouteHandlerBuilder AsExport(this RouteHandlerBuilder endpoint, string policy = Policies.ReadTeamEdition) =>
        endpoint.RequireAuthorization(policy).RequireRateLimiting(RateLimit).AddEndpointFilter(NoStore);

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
