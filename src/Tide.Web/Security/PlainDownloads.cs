using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace Tide.Web.Security;

/// <summary>
/// The pass that lets a file whose main download is the zip be sent as it is: minted only for the "as is" link on an
/// action's Files list, for one file and one person, and good for <see cref="Lifetime"/>. A link written by hand (in a
/// comment's Markdown, say) can't carry a valid one, so it gets the zip. Data Protection signs and encrypts it, under
/// its own purpose, with the expiry inside.
/// </summary>
public sealed class PlainDownloads(IDataProtectionProvider protection)
{
    /// <summary>How long a minted pass is good for.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private readonly ITimeLimitedDataProtector _protector = protection.CreateProtector("Tide.Files.PlainDownload").ToTimeLimitedDataProtector();

    /// <summary>A pass for <paramref name="userId"/> to download file <paramref name="fileId"/> as it is, for <see cref="Lifetime"/>.</summary>
    public string Mint(Guid fileId, string userId) => Mint(fileId, userId, DateTimeOffset.UtcNow.Add(Lifetime));

    /// <summary>As <see cref="Mint(Guid, string)"/>, ending at <paramref name="expires"/>.</summary>
    internal string Mint(Guid fileId, string userId, DateTimeOffset expires) => _protector.Protect(Bound(fileId, userId), expires);

    /// <summary>Whether <paramref name="pass"/> was minted for this file and this person and hasn't expired.</summary>
    public bool Allows(string? pass, Guid fileId, string userId)
    {
        if (string.IsNullOrEmpty(pass)) return false;
        try { return _protector.Unprotect(pass, out _) == Bound(fileId, userId); }
        catch (CryptographicException) { return false; }
    }

    private static string Bound(Guid fileId, string userId) => fileId.ToString("N") + "|" + userId;
}
