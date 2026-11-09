using Tide.Domain.Common;

namespace Tide.Domain.Entities;

/// <summary>
/// A secret an administrator saved in the app (the mail relay password, the NVD key, the Azure OpenAI key) in place of
/// the server's configuration. <see cref="Protected"/> is the value encrypted with the server's Data Protection keys under
/// a purpose of its own, so the database holds ciphertext only and one secret never opens as another.
/// </summary>
public class StoredSecret : AuditableEntity
{
    /// <summary>The configuration key it stands in for ("Cve:NvdApiKey").</summary>
    public string Key { get; set; } = string.Empty;

    public string Protected { get; set; } = string.Empty;

    /// <summary>Who saved it last, as the page names them.</summary>
    public string SavedByName { get; set; } = string.Empty;
}
