using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tide.Application.Assist;
using Tide.Application.Intake;

namespace Tide.Infrastructure.Assist;

/// <summary>
/// Azure OpenAI through its v1 Responses API (<c>{endpoint}/openai/v1/responses</c>, the <c>api-key</c> header). A
/// draft is one request with a strict JSON schema (<c>text.format</c>) and no tools, so it can't reach the web. Finding
/// an article is a separate request with only the <c>web_search</c> tool, limited to the article's site, sent the
/// address and title only; its text is returned only when a citation is that same address. Nothing is stored at the
/// provider (<c>store: false</c>). The intake proxy (<c>Intake:Proxy</c>), if set, is used. Never throws for a failure
/// the provider caused.
/// </summary>
public sealed class AzureOpenAiAssistant : IThreatIntelAssistant, IDisposable
{
    private readonly HttpClient _client;
    private readonly IOptionsMonitor<AiOptions> _options;
    private readonly ILogger<AzureOpenAiAssistant> _log;

    public AzureOpenAiAssistant(IOptionsMonitor<AiOptions> options, IOptionsMonitor<IntakeOptions> intake, ILogger<AzureOpenAiAssistant> log)
        : this(options, log, Handler(intake.CurrentValue.Proxy))
    {
    }

    /// <summary>With another handler (tests answer for the provider).</summary>
    internal AzureOpenAiAssistant(IOptionsMonitor<AiOptions> options, ILogger<AzureOpenAiAssistant> log, HttpMessageHandler handler)
    {
        _options = options;
        _log = log;
        _client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private static SocketsHttpHandler Handler(string? proxy)
    {
        var handler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5), AutomaticDecompression = DecompressionMethods.All };
        if (!string.IsNullOrWhiteSpace(proxy) && Uri.TryCreate(proxy, UriKind.Absolute, out var p))
        {
            handler.Proxy = new WebProxy(p) { UseDefaultCredentials = true };
            handler.UseProxy = true;
        }
        return handler;
    }

    public string Provider => AiOptions.AzureOpenAI;
    public string Model => _options.CurrentValue.Deployment.Trim();
    public bool Configured => _options.CurrentValue.Configured;

    public async Task<AssistAnswer> AnswerAsync(AssistRequest request, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["model"] = Model,
            ["instructions"] = request.Instructions,
            ["input"] = request.Input,
            ["store"] = false,
            ["text"] = new JsonObject
            {
                ["format"] = new JsonObject
                {
                    ["type"] = "json_schema",
                    ["name"] = request.SchemaName,
                    ["schema"] = JsonNode.Parse(request.Schema),
                    ["strict"] = true,
                },
            },
        };
        var (response, error) = await SendAsync(body, ct);
        if (response is null) return new(null, error);
        using (response)
        {
            var (text, refusal) = OutputText(response.RootElement);
            if (refusal is not null) return new(null, "The model declined to answer: " + refusal);
            return text is null ? new(null, "The AI provider's answer had no text.") : new(text);
        }
    }

    public async Task<FoundArticle> FindArticleAsync(string url, string? title, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return new(null, "That isn't an address.");
        var body = new JsonObject
        {
            ["model"] = Model,
            ["store"] = false,
            ["tools"] = new JsonArray(new JsonObject
            {
                ["type"] = "web_search",
                ["filters"] = new JsonObject { ["allowed_domains"] = new JsonArray(Site(uri)) },
            }),
            ["tool_choice"] = "auto",
            ["include"] = new JsonArray("web_search_call.action.sources"),
            ["instructions"] = "Find the web page at the address given and return the article's text as published: its headline, then its paragraphs, " +
                               "lists and tables of indicators, in order. Don't summarise, comment or add anything. If you can't find that page, return nothing.",
            ["input"] = $"Address: {uri.AbsoluteUri}" + (string.IsNullOrWhiteSpace(title) ? "" : $"\nTitle: {title.Trim()}"),
        };
        var (response, error) = await SendAsync(body, ct);
        if (response is null) return new(null, error);
        using (response)
        {
            var (text, _) = OutputText(response.RootElement);
            if (string.IsNullOrWhiteSpace(text)) return new(null, "no text came back");
            return Cited(response.RootElement).Any(c => SamePage(c, uri)) ? new(text.Trim()) : new(null, "it didn't cite that page");
        }
    }

    // POSTs to the Responses API; the parsed answer, or why there isn't one.
    private async Task<(JsonDocument?, string?)> SendAsync(JsonObject body, CancellationToken ct)
    {
        var o = _options.CurrentValue;
        if (!o.Configured) return (null, "No AI provider is set up.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(o.TimeoutSeconds, 15, 300)));
        using var request = new HttpRequestMessage(HttpMethod.Post, o.Endpoint.Trim().TrimEnd('/') + "/openai/v1/responses")
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add("api-key", o.ApiKey.Trim());
        try
        {
            using var response = await _client.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                _log.LogWarning("Azure OpenAI answered HTTP {Status}.", (int)response.StatusCode);
                return (null, response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "The AI provider refused the key (Ai:ApiKey).",
                    HttpStatusCode.NotFound => "The AI provider has no deployment by that name (Ai:Deployment).",
                    HttpStatusCode.TooManyRequests => "The AI provider is busy (rate limited). Try again in a minute.",
                    HttpStatusCode.BadRequest => "The AI provider refused the request. The content may have been filtered, or the model may not support this.",
                    _ => $"The AI provider answered HTTP {(int)response.StatusCode}.",
                });
            }
            var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token);
            if (doc.RootElement.TryGetProperty("status", out var s) && s.GetString() is { } status && status != "completed")
            {
                doc.Dispose();
                return (null, status == "incomplete" ? "The AI provider stopped before finishing (the answer was too long or filtered)." : $"The AI provider's answer is {status}.");
            }
            return (doc, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (null, $"The AI provider didn't answer within {o.TimeoutSeconds} seconds.");
        }
        catch (HttpRequestException ex)
        {
            _log.LogWarning("Azure OpenAI couldn't be reached: {Error}.", ex.HttpRequestError);
            return (null, $"The AI provider couldn't be reached ({ex.HttpRequestError}).");
        }
        catch (JsonException)
        {
            return (null, "The AI provider's answer couldn't be read.");
        }
    }

    // The message's output text, joined, or its refusal.
    internal static (string? Text, string? Refusal) OutputText(JsonElement root)
    {
        if (!root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array) return (null, null);
        var texts = new List<string>();
        foreach (var item in output.EnumerateArray())
        {
            if (!item.TryGetProperty("type", out var t) || t.GetString() != "message" || !item.TryGetProperty("content", out var content)) continue;
            foreach (var part in content.EnumerateArray())
            {
                var kind = part.TryGetProperty("type", out var k) ? k.GetString() : null;
                if (kind == "refusal" && part.TryGetProperty("refusal", out var r)) return (null, r.GetString());
                if (kind == "output_text" && part.TryGetProperty("text", out var x) && x.GetString() is { } text) texts.Add(text);
            }
        }
        return texts.Count == 0 ? (null, null) : (string.Concat(texts), null);
    }

    // Every address the answer cites or its searches opened.
    internal static IEnumerable<string> Cited(JsonElement root)
    {
        if (!root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array) yield break;
        foreach (var item in output.EnumerateArray())
        {
            if (item.TryGetProperty("action", out var action) && action.TryGetProperty("sources", out var sources) && sources.ValueKind == JsonValueKind.Array)
                foreach (var s in sources.EnumerateArray())
                    if (s.TryGetProperty("url", out var u) && u.GetString() is { } url) yield return url;
            if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) continue;
            foreach (var part in content.EnumerateArray())
            {
                if (!part.TryGetProperty("annotations", out var annotations) || annotations.ValueKind != JsonValueKind.Array) continue;
                foreach (var a in annotations.EnumerateArray())
                    if (a.TryGetProperty("url", out var u) && u.GetString() is { } url) yield return url;
            }
        }
    }

    // The same page: host (without "www.") and path (without a trailing slash) match, whatever the scheme, query or fragment.
    internal static bool SamePage(string cited, Uri wanted) =>
        Uri.TryCreate(cited, UriKind.Absolute, out var c)
        && string.Equals(Site(c), Site(wanted), StringComparison.OrdinalIgnoreCase)
        && string.Equals(c.AbsolutePath.TrimEnd('/'), wanted.AbsolutePath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    private static string Site(Uri u) => u.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? u.Host[4..] : u.Host;

    public void Dispose() => _client.Dispose();
}
