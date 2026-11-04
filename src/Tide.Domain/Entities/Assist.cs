using Tide.Domain.Common;
using Tide.Domain.Enums;

namespace Tide.Domain.Entities;

/// <summary>
/// One AI draft of an article (ADR 0004): who asked, which provider and model, the prompt version, a hash of what was
/// sent and its TLP, what it was based on, and the suggestions that passed the checks. How many the analyst accepted
/// and rejected is counted as they decide. The source text itself is never kept.
/// </summary>
public class AiDraft : AuditableEntity
{
    public const int MaxBasedOn = 1000;
    public const int MaxSuggestions = 60_000;

    public Guid ArticleId { get; set; }

    /// <summary>"AzureOpenAI", or "Pasted" for an answer run in an approved tool and pasted back.</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>The model or deployment that answered; empty for a pasted answer.</summary>
    public string Model { get; set; } = string.Empty;

    public int PromptVersion { get; set; }

    /// <summary>SHA-256 of the prompt as sent, in hex.</summary>
    public string InputHash { get; set; } = string.Empty;

    /// <summary>The highest TLP of what was sent.</summary>
    public TlpLevel InputTlp { get; set; }

    /// <summary>What the draft was based on, as the editor showed it ("article text, 4,120 chars (fetched by TIDE) · …").</summary>
    public string BasedOn { get; set; } = string.Empty;

    /// <summary>The suggestions that passed the checks, as JSON.</summary>
    public string Suggestions { get; set; } = string.Empty;

    public int Offered { get; set; }
    public int Accepted { get; set; }
    public int Rejected { get; set; }
}
