using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Tide.Application.Abstractions;
using Tide.Application.Security;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.Application.Api;

/// <summary>
/// What an API token may do (EP-75). Each scope is one or a few endpoints; a token holds only the scopes it was issued
/// with. Each scope also grants the permissions its endpoints' services check (so "actions:write" works an action as an
/// analyst would), but a token reaches nothing beyond <c>/api/v1</c>, and each endpoint checks its own scope first.
/// </summary>
public static class ApiScopes
{
    public const string BriefsRead = "briefs:read";
    public const string ActionsRead = "actions:read";
    public const string ActionsWrite = "actions:write";
    public const string MetricsRead = "metrics:read";
    public const string MetricsWrite = "metrics:write";
    public const string StackRead = "stack:read";
    public const string TriagePropose = "triage:propose";
    public const string DetectionsWrite = "detections:write";

    public sealed record Scope(string Name, string About, Permission[] Grants);

    public static readonly IReadOnlyList<Scope> All =
    [
        new(BriefsRead, "Read briefs as the team edition shows them, cut to the token's TLP", [Permission.ReadTeamEdition]),
        new(ActionsRead, "Read actions", [Permission.ReadTeamEdition]),
        new(ActionsWrite, "Change an action's status and add a note (never delete or reassign)", [Permission.ReadTeamEdition, Permission.WorkActions, Permission.WriteArticles]),
        new(MetricsRead, "Read the opening metrics", [Permission.ReadTeamEdition]),
        new(MetricsWrite, "Enter a day's metric value", [Permission.ReadTeamEdition, Permission.EnterMetrics]),
        new(StackRead, "Read Our stack", [Permission.ReadTeamEdition]),
        new(TriagePropose, "Propose an item for triage (an analyst still decides it)", [Permission.WriteArticles]),
        new(DetectionsWrite, "Push the SOC's detection rules (names and ATT&CK tags) for Coverage", [Permission.WriteArticles]),
    ];

    /// <summary>The scopes in a space-separated list that are real, in the catalog's order.</summary>
    public static IReadOnlyList<string> Parse(string? scopes)
    {
        var named = (scopes ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        return All.Where(s => named.Contains(s.Name)).Select(s => s.Name).ToList();
    }

    public static IReadOnlySet<Permission> Permissions(IEnumerable<string> scopes) =>
        All.Where(s => scopes.Contains(s.Name)).SelectMany(s => s.Grants).ToHashSet();
}

/// <summary>A token as the API page lists it: never the token itself.</summary>
public sealed record ApiTokenRow(Guid Id, string Name, string Hint, IReadOnlyList<string> Scopes, TlpLevel MaxTlp, DateTimeOffset ExpiresAtUtc,
    DateTimeOffset? RevokedAtUtc, DateTimeOffset? LastUsedAtUtc, long Calls, string CreatedBy)
{
    public bool Active(DateTimeOffset now) => RevokedAtUtc is null && ExpiresAtUtc > now;
}

/// <summary>A token just issued: the only time <see cref="Token"/> is shown.</summary>
public sealed record IssuedToken(Guid Id, string Token);

/// <summary>Who a valid token belongs to, for the API's sign-in.</summary>
public sealed record ApiCaller(Guid TokenId, string Name, IReadOnlyList<string> Scopes, TlpLevel MaxTlp);

/// <summary>
/// API tokens (EP-75): administrators issue them, scoped and expiring, and revoke them; the API's sign-in finds the
/// token's caller by its hash. A token is "tide_pat_" and 43 random characters; only its SHA-256 is stored.
/// </summary>
public sealed class ApiTokenService(IAppDbContextFactory dbs, ICurrentUser user, TimeProvider time)
{
    public const string Prefix = "tide_pat_";
    public const int MaxDays = 366;

    public async Task<IReadOnlyList<ApiTokenRow>> ListAsync(CancellationToken ct = default)
    {
        RequireAdmin();
        await using var db = await dbs.CreateAsync(ct);
        var tokens = await db.ApiTokens.AsNoTracking().ToListAsync(ct);
        return tokens.OrderBy(t => t.RevokedAtUtc is not null).ThenBy(t => t.Name)
            .Select(t => new ApiTokenRow(t.Id, t.Name, t.Hint, ApiScopes.Parse(t.Scopes), t.MaxTlp, t.ExpiresAtUtc, t.RevokedAtUtc, t.LastUsedAtUtc, t.Calls, t.CreatedBy))
            .ToList();
    }

    /// <summary>Issues a token for <paramref name="name"/> with <paramref name="scopes"/>, reading up to <paramref name="maxTlp"/>, for <paramref name="days"/>.</summary>
    public async Task<IssuedToken> IssueAsync(string name, IReadOnlyCollection<string> scopes, TlpLevel maxTlp, int days, CancellationToken ct = default)
    {
        RequireAdmin();
        name = (name ?? "").Trim();
        if (name.Length is 0 or > ApiToken.MaxName) throw new ArgumentException($"Name the caller (at most {ApiToken.MaxName} characters), such as “XSOAR playbooks”.");
        var kept = ApiScopes.Parse(string.Join(' ', scopes));
        if (kept.Count == 0) throw new ArgumentException("Choose at least one scope.");
        if (days is < 1 or > MaxDays) throw new ArgumentException($"A token lasts 1 to {MaxDays} days.");
        var token = Prefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        await using var db = await dbs.CreateAsync(ct);
        var row = new ApiToken
        {
            Name = name, Hash = Hash(token), Hint = token[^4..], Scopes = string.Join(' ', kept), MaxTlp = Tlp.Checked(maxTlp),
            ExpiresAtUtc = time.GetUtcNow().AddDays(days),
        };
        db.ApiTokens.Add(row);
        await db.SaveChangesAsync(ct);
        return new IssuedToken(row.Id, token);
    }

    public async Task RevokeAsync(Guid id, CancellationToken ct = default)
    {
        RequireAdmin();
        await using var db = await dbs.CreateAsync(ct);
        var t = await db.ApiTokens.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException("That token no longer exists.");
        t.RevokedAtUtc ??= time.GetUtcNow();
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The caller a presented token belongs to, when it's a live one (not revoked, not expired), counting the call; null
    /// otherwise. No permission check: this is the sign-in.
    /// </summary>
    public async Task<ApiCaller?> AuthenticateAsync(string? token, CancellationToken ct = default)
    {
        if (token is null || !token.StartsWith(Prefix, StringComparison.Ordinal) || token.Length is < 20 or > 100) return null;
        var hash = Hash(token);
        var now = time.GetUtcNow();
        await using var db = await dbs.CreateAsync(ct);
        var t = await db.ApiTokens.FirstOrDefaultAsync(x => x.Hash == hash, ct);
        if (t is null || t.RevokedAtUtc is not null || t.ExpiresAtUtc <= now) return null;
        // Counted without touching the audit columns: a call isn't a change to the token.
        await db.ApiTokens.Where(x => x.Id == t.Id).ExecuteUpdateAsync(u => u.SetProperty(x => x.Calls, x => x.Calls + 1).SetProperty(x => x.LastUsedAtUtc, now), ct);
        return new ApiCaller(t.Id, t.Name, ApiScopes.Parse(t.Scopes), t.MaxTlp);
    }

    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private void RequireAdmin()
    {
        if (!user.Has(Permission.Administer)) throw new ForbiddenException("Only an administrator manages API access.");
    }
}
