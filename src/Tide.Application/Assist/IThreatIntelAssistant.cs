using Tide.Domain.Enums;

namespace Tide.Application.Assist;

/// <summary>
/// AI help (config section <c>Ai</c>, ADR 0004). Off until a provider is set: with none, nothing is ever sent, and the
/// editor offers only "Copy a prompt" for a tool the organisation has approved. The key is a server secret (the
/// <c>Ai__ApiKey</c> environment variable or protected configuration), never an in-app setting.
/// </summary>
public sealed class AiOptions
{
    public const string AzureOpenAI = "AzureOpenAI";

    /// <summary>"AzureOpenAI", or blank for none.</summary>
    public string Provider { get; set; } = "";

    /// <summary>The Azure OpenAI resource ("https://contoso-ai.openai.azure.com"); the v1 API is reached under it.</summary>
    public string Endpoint { get; set; } = "";

    /// <summary>The model deployment's name ("gpt-5-mini").</summary>
    public string Deployment { get; set; } = "";

    public string ApiKey { get; set; } = "";

    /// <summary>The highest TLP that may be sent. GREEN by default; above it, nothing is sent and the editor says why.</summary>
    public TlpLevel MaxTlp { get; set; } = TlpLevel.Green;

    /// <summary>
    /// Whether, when TIDE can't fetch an article from a source marked Open, the provider may look it up with its web
    /// search (Azure: Grounding with Bing, billed separately and outside the Azure data boundary). Only the article's
    /// address and title are sent, and only for TLP:CLEAR. Off by default.
    /// </summary>
    public bool WebGrounding { get; set; }

    /// <summary>How long one answer may take, in seconds (15 to 300).</summary>
    public int TimeoutSeconds { get; set; } = 120;

    public bool IsAzure => string.Equals(Provider.Trim(), AzureOpenAI, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a provider is set up enough to call.</summary>
    public bool Configured => IsAzure && Uri.TryCreate(Endpoint.Trim(), UriKind.Absolute, out var u) && u.Scheme == "https"
        && !string.IsNullOrWhiteSpace(Deployment) && !string.IsNullOrWhiteSpace(ApiKey);
}

/// <summary>A request for one structured answer: the instructions, the material, and the JSON schema the answer must fit.</summary>
public sealed record AssistRequest(string Instructions, string Input, string SchemaName, string Schema);

/// <summary>The answer's JSON, or why there isn't one (safe to show).</summary>
public sealed record AssistAnswer(string? Json, string? Error = null);

/// <summary>An article's text the provider found on the web, or why it didn't (safe to show).</summary>
public sealed record FoundArticle(string? Text, string? Error = null);

/// <summary>
/// The AI provider behind the editor's drafting help. Implementations never throw for a failure the provider caused;
/// the result says what went wrong.
/// </summary>
public interface IThreatIntelAssistant
{
    /// <summary>"AzureOpenAI".</summary>
    string Provider { get; }

    /// <summary>The model or deployment that answers.</summary>
    string Model { get; }

    /// <summary>Whether it's set up enough to call (<see cref="AiOptions.Configured"/>).</summary>
    bool Configured { get; }

    /// <summary>One structured answer to <paramref name="request"/>, with no tools: it can't reach the web.</summary>
    Task<AssistAnswer> AnswerAsync(AssistRequest request, CancellationToken ct = default);

    /// <summary>
    /// The text of the article at <paramref name="url"/>, looked up with the provider's web search limited to that site.
    /// It's sent the address and title only, and the text is returned only when the provider cites that same address.
    /// </summary>
    Task<FoundArticle> FindArticleAsync(string url, string? title, CancellationToken ct = default);
}
