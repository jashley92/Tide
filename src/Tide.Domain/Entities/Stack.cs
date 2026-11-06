using Tide.Domain.Common;

namespace Tide.Domain.Entities;

/// <summary>
/// A product the organization runs ("GitLab"), on Our stack: the names it's reported under, the versions in use and
/// notes, the team that owns it, and when that was last checked. Analysts keep it; triage lifts items that name it,
/// and articles can be tagged with it, so past discussion of a product is one click away. Retired, never deleted.
/// </summary>
public class StackProduct : AuditableEntity
{
    public const int MaxName = 100;
    public const int MaxOtherNames = 500;
    public const int MaxVersions = 200;
    public const int MaxNotes = 4000;
    public const int MaxContactName = 100;
    public const int MaxContactEmail = 254;

    public string Name { get; set; } = string.Empty;

    /// <summary>Other names it's reported under, separated by semicolons ("Pulse Secure; ICS").</summary>
    public string? OtherNames { get; set; }

    /// <summary>The versions in use ("17.11.3 self-managed").</summary>
    public string? Versions { get; set; }

    /// <summary>Notes in Markdown: where it runs, who to ask, change windows, past decisions.</summary>
    public string? Notes { get; set; }

    public Guid? TeamId { get; set; }
    public Team? Team { get; set; }

    /// <summary>Who to ask about it at the vendor or its support (a name and an address): an Outreach action's email
    /// about the product is offered to them.</summary>
    public string? ContactName { get; set; }
    public string? ContactEmail { get; set; }

    /// <summary>When the versions and notes were last confirmed.</summary>
    public DateOnly? CheckedOn { get; set; }

    public bool IsRetired { get; set; }
}

/// <summary>An article tagged with a product from Our stack (optional, any number).</summary>
public class ArticleProduct
{
    public Guid ArticleId { get; set; }
    public Guid ProductId { get; set; }
    public StackProduct? Product { get; set; }
}
