using System.Collections.Concurrent;
using Microsoft.AspNetCore.DataProtection;
using Tide.Application.Abstractions;

namespace Tide.Web.Security;

/// <summary>Secrets TIDE reads back (a webhook's signing secret, those saved in Administration › Secrets), encrypted with
/// the server's Data Protection keys (DataProtection:KeyPath), each under its own purpose. One protector per purpose.</summary>
public sealed class DataProtectionSecrets(IDataProtectionProvider provider) : ISecretProtector
{
    private readonly ConcurrentDictionary<string, IDataProtector> _protectors = new(StringComparer.Ordinal);

    private IDataProtector For(string purpose) => _protectors.GetOrAdd(purpose, provider.CreateProtector);

    public string Protect(string purpose, string secret) => For(purpose).Protect(secret);

    public string? Unprotect(string purpose, string protectedSecret)
    {
        try { return For(purpose).Unprotect(protectedSecret); }
        catch (System.Security.Cryptography.CryptographicException) { return null; }
    }
}
