using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tide.Application.Abstractions;
using Tide.Application.Security;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.Application.Operations;

/// <summary>The update check (config section <c>Updates</c>). <see cref="CheckEnabled"/> is the default an administrator
/// can change in the app (Administration › System › Updates); the rest are set on the server.</summary>
public sealed class UpdateOptions
{
    /// <summary>Off by default: many SOC servers have no outbound internet, and the call is the organization's choice.</summary>
    public bool CheckEnabled { get; set; }

    /// <summary>Hours between checks (6 to 168).</summary>
    public int CheckIntervalHours { get; set; } = 24;

    /// <summary>The GitHub repository whose releases are checked, "owner/name".</summary>
    public string Repository { get; set; } = "jashley92/Tide";
}

/// <summary>Whether the update check is on: the administered value when one is saved, else the server's.</summary>
public sealed class UpdateSettings(IAppDbContextFactory dbs, ICurrentUser user, IOptions<UpdateOptions> defaults)
{
    public async Task<bool> GetEnabledAsync(CancellationToken ct = default)
    {
        await using var db = await dbs.CreateAsync(ct);
        var saved = await db.Settings.AsNoTracking().Where(s => s.Key == SettingsCatalog.UpdateCheckKey).Select(s => s.Value).FirstOrDefaultAsync(ct);
        return bool.TryParse(saved, out var on) ? on : defaults.Value.CheckEnabled;
    }

    public async Task SaveEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        if (!user.Has(Permission.Administer)) throw new ForbiddenException("Only an administrator can turn the update check on or off.");
        await using var db = await dbs.CreateAsync(ct);
        var row = await db.Settings.FirstOrDefaultAsync(s => s.Key == SettingsCatalog.UpdateCheckKey, ct);
        if (row is null) db.Settings.Add(row = new AppSetting { Key = SettingsCatalog.UpdateCheckKey });
        row.Value = enabled ? "true" : "false";
        row.UpdatedAtUtc = DateTimeOffset.UtcNow;
        row.UpdatedBy = user.UserId;
        await db.SaveChangesAsync(ct);
    }
}
