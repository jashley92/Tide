using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Tide.Application.Abstractions;
using Tide.Application.Operations;
using Tide.Domain.Common;
using Tide.Domain.Entities;

namespace Tide.Infrastructure.Persistence;

/// <summary>
/// Stamps who and when on every <see cref="AuditableEntity"/>, and writes an <see cref="AuditLogEntry"/> for each
/// change in the same save (so a change and its record commit or fail together). Each entry holds the changed values,
/// before and after. The sign-in mirror, the audit log itself and machine bookkeeping (feed items, CVE records,
/// detection rules, kept article text) aren't audited.
/// </summary>
public sealed class AuditInterceptor(ICurrentUser user, TimeProvider time) : SaveChangesInterceptor
{
    /// <summary>Longest value kept in an audit entry; longer text is cut and marked.</summary>
    public const int MaxValueLength = 1000;

    // Detection rules and kept article text are bookkeeping, not people's changes: each push or import sets every rule's
    // LastSeenAtUtc (a row per rule each time), and each read or prune of a publisher's text would copy it into the log.
    // An administrator's removal of a pushed set is recorded by DetectionService as one entry.
    // A person's own preferences (List or Board) change nothing anyone else sees.
    private static readonly HashSet<Type> NotAudited =
        [typeof(AuditLogEntry), typeof(AppUser), typeof(UserPreference), typeof(NotificationMark), typeof(FeedItem), typeof(CveRecord), typeof(DetectionRule), typeof(FeedItemText)];

    // Changing on every save, so recording them is noise.
    private static readonly HashSet<string> SkippedProperties =
        [nameof(AuditableEntity.ModifiedAtUtc), nameof(AuditableEntity.ModifiedBy), nameof(Article.EditVersion)];

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (eventData.Context is { } ctx) Record(ctx);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } ctx) Record(ctx);
        return ValueTask.FromResult(result);
    }

    private void Record(DbContext ctx)
    {
        ctx.ChangeTracker.DetectChanges();
        var now = time.GetUtcNow();
        var who = user.UserId;
        var name = user.IsAuthenticated ? user.DisplayName : "system";

        foreach (var entry in ctx.ChangeTracker.Entries<AuditableEntity>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedAtUtc = now;
                entry.Entity.CreatedBy = who;
            }
            else if (entry.State == EntityState.Modified)
            {
                entry.Entity.ModifiedAtUtc = now;
                entry.Entity.ModifiedBy = who;
            }
        }

        var entries = new List<AuditLogEntry>();
        foreach (var entry in ctx.ChangeTracker.Entries().ToList())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted)) continue;
            if (NotAudited.Contains(entry.Entity.GetType())) continue;
            var changes = Changes(entry);
            if (entry.State == EntityState.Modified && changes.Count == 0) continue;
            entries.Add(new AuditLogEntry
            {
                AtUtc = now,
                UserId = who,
                UserName = name,
                Action = entry.State.ToString(),
                EntityType = entry.Metadata.ClrType.Name,
                EntityKey = Key(entry),
                ChangesJson = changes.Count == 0 ? null : JsonSerializer.Serialize(changes),
            });
        }
        ctx.Set<AuditLogEntry>().AddRange(entries);
    }

    private static Dictionary<string, string?[]> Changes(EntityEntry entry)
    {
        var changes = new Dictionary<string, string?[]>();
        foreach (var p in entry.Properties)
        {
            if (p.Metadata.IsPrimaryKey() || SkippedProperties.Contains(p.Metadata.Name)) continue;
            var mask = MaskFor(entry.Entity, p.Metadata.Name);
            switch (entry.State)
            {
                case EntityState.Added when p.CurrentValue is not null:
                    changes[p.Metadata.Name] = [null, Show(p.CurrentValue, mask)];
                    break;
                case EntityState.Deleted when p.OriginalValue is not null:
                    changes[p.Metadata.Name] = [Show(p.OriginalValue, mask), null];
                    break;
                case EntityState.Modified when p.IsModified && !Equals(p.OriginalValue, p.CurrentValue):
                    changes[p.Metadata.Name] = [Show(p.OriginalValue, mask), Show(p.CurrentValue, mask)];
                    break;
            }
        }
        return changes;
    }

    private enum Mask { None, Url, Secret }

    // The properties that can carry a credential, named one by one (a name heuristic over every property hid a team's
    // Key and an action's AssigneeName): a source's address may hold a key or a sign-in, and a setting's value is a secret
    // when the settings catalogue lists it as one or its key names one ("Cve:NvdApiKey"), and a webhook's signing secret is one. Everything else is recorded as it is, so the trail stays readable.
    private static Mask MaskFor(object entity, string property) => entity switch
    {
        Source when property == nameof(Source.Url) => Mask.Url,
        AppSetting s when property == nameof(AppSetting.Value) && SettingsCatalog.IsSecret(s.Key) => Mask.Secret,
        Webhook when property == nameof(Webhook.Secret) => Mask.Secret,
        _ => Mask.None,
    };

    private static string Key(EntityEntry entry)
    {
        var key = entry.Metadata.FindPrimaryKey();
        if (key is null) return "";
        var values = key.Properties.Select(p => Convert.ToString(entry.Property(p.Name).CurrentValue, System.Globalization.CultureInfo.InvariantCulture));
        var joined = string.Join('/', values);
        return joined.Length > 100 ? joined[..100] : joined;
    }

    // A value as the audit trail keeps it: an address without any key or sign-in it carries, a secret only as set.
    private static string? Show(object? value, Mask mask)
    {
        if (value is string text && text.Length > 0)
        {
            if (mask == Mask.Secret) return SecretMask.SetSecret;
            if (mask == Mask.Url && SecretMask.UrlHasSecret(text)) return SecretMask.Url(text);
        }
        var s = value switch
        {
            null => null,
            DateTimeOffset d => d.ToUniversalTime().ToString("o", System.Globalization.CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
            _ => value.ToString(),
        };
        return s is { Length: > MaxValueLength } ? s[..MaxValueLength] + "…" : s;
    }
}
