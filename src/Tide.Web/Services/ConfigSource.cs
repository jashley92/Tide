using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration.CommandLine;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Memory;
using Tide.Application.Abstractions;
using Tide.Application.Operations;
using Tide.Application.Security;
using Tide.Domain.Enums;

namespace Tide.Web.Services;

/// <summary>A configuration key's effective value and which provider supplied it.</summary>
public sealed record ConfigResolution(string? Value, string Source);

/// <summary>
/// Where a configuration key's effective value comes from, by walking the live provider chain.
/// Providers are consulted in order and the last one holding the key wins, as <see cref="IConfiguration"/>
/// resolves it, so the answer is the real precedence: environment variables over appsettings.Production.json (which
/// the installer writes) over appsettings.json. A list ("Distribution:TeamRecipients") comes from the last provider
/// that sets any of its items.
/// </summary>
public static class ConfigSource
{
    public const string Unset = "Default (unset)";
    public const string InstallerFile = "appsettings.Production.json";

    public static ConfigResolution Resolve(IConfiguration config, string key)
    {
        if (config is not IConfigurationRoot root)
            return config[key] is { } v ? new(v, "Configuration") : new(null, Unset);

        string? value = null;
        string? source = null;
        foreach (var p in root.Providers)
        {
            if (p.TryGet(key, out var v))
            {
                value = v;
                source = Label(p);
            }
            else if (p.GetChildKeys([], key).Any())
            {
                value = null;
                source = Label(p);
            }
        }
        if (source is null) return new(null, Unset);
        if (value is null)
        {
            var items = config.GetSection(key).GetChildren().Select(c => c.Value).Where(v => !string.IsNullOrEmpty(v)).ToList();
            value = items.Count == 0 ? null : string.Join("; ", items);
        }
        return new(value, source);
    }

    internal static string Label(IConfigurationProvider p) => p switch
    {
        FileConfigurationProvider f when string.Equals(Path.GetFileName(f.Source.Path), InstallerFile, StringComparison.OrdinalIgnoreCase)
            => InstallerFile + " (installer)",
        FileConfigurationProvider f when string.Equals(Path.GetFileName(f.Source.Path), "secrets.json", StringComparison.OrdinalIgnoreCase)
            => "User secrets",
        FileConfigurationProvider f => Path.GetFileName(f.Source.Path) ?? "a file",
        EnvironmentVariablesConfigurationProvider => "Environment variable",
        CommandLineConfigurationProvider => "Command line",
        MemoryConfigurationProvider or ChainedConfigurationProvider => "Host settings",
        _ => p.GetType().Name.Replace("ConfigurationProvider", "", StringComparison.Ordinal),
    };
}

/// <summary>A setting as Server configuration and Setting sources show it: the value masked as <see cref="SecretMask"/>
/// says, and where it came from.</summary>
public sealed record SettingRow(SettingInfo Info, string? Shown, string Source, bool FromDatabase = false, DateTimeOffset? SavedAtUtc = null,
    string? SavedBy = null);

/// <summary>The settings Administration › System lists, each resolved. Administrators only: the pages say so, and so does
/// each method here, as the app's other services do.</summary>
public sealed class SystemSettings(IConfiguration config, IAppDbContextFactory dbs, IUserDirectory users, IWebHostEnvironment env,
    Application.Admin.RoleMappingService roles, Application.Org.OrgClock org, ICurrentUser user, TimeProvider time)
{
    public const string SavedInApp = "Saved in Administration";

    public IReadOnlyList<SettingRow> Server()
    {
        RequireAdmin();
        return [.. SettingsCatalog.Server.Select(Row)];
    }

    public SettingRow Row(SettingInfo s)
    {
        RequireAdmin();
        var r = ConfigSource.Resolve(config, s.Key);
        return new SettingRow(s, SecretMask.Display(s.Key, s.Kind, r.Value), r.Source);
    }

    /// <summary>The in-app settings: the saved value when there is one, else the server's.</summary>
    public async Task<IReadOnlyList<SettingRow>> InAppAsync(CancellationToken ct = default)
    {
        RequireAdmin();
        await using var db = await dbs.CreateAsync(ct);
        var saved = (await db.Settings.AsNoTracking().ToListAsync(ct)).ToDictionary(s => s.Key, StringComparer.OrdinalIgnoreCase);
        var names = (await users.ListAsync(ct)).DistinctBy(u => u.Sid).ToDictionary(u => u.Sid, u => u.DisplayName, StringComparer.Ordinal);
        return [.. SettingsCatalog.InApp.Select(s => saved.TryGetValue(s.Key, out var row)
            ? new SettingRow(s, SecretMask.Display(s.Key, s.Kind, row.Value) ?? "(none)", SavedInApp, true, row.UpdatedAtUtc,
                names.GetValueOrDefault(row.UpdatedBy, row.UpdatedBy))
            : Row(s))];
    }

    /// <summary>What this server knows about its install, for the answers file: its settings, the app pool it runs in, the
    /// folder it runs from, the sub-path it's served under, and the AD groups mapped to each role (by name: a SID can't
    /// go in the answers file).</summary>
    public async Task<InstallFacts> InstallFactsAsync(string? pathBase, CancellationToken ct = default)
    {
        RequireAdmin();
        var facts = InstallFacts.FromSettings(config["Database:Provider"], config.GetConnectionString("Default"),
            config.GetValue("Database:ApplyMigrations", true), config["Files:RootPath"], config["DataProtection:KeyPath"],
            config["App:BaseUrl"], pathBase);
        // By the role as sign-in reads it (ignoring case), so a row in another casing neither fails this nor splits a role.
        var groups = (await roles.ListAsync(ct))
            .Where(m => !m.AdGroup.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase))
            .Select(m => (m.AdGroup, Ok: RoleDefinitions.TryParseRole(m.RoleName, out var role), Role: role))
            .Where(m => m.Ok)
            .GroupBy(m => m.Role)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)[.. g.Select(m => m.AdGroup)]);
        var pool = System.Environment.GetEnvironmentVariable("APP_POOL_ID");
        return facts with
        {
            AppAccount = OperatingSystem.IsWindows() && pool is not null ? $"{System.Environment.UserDomainName}\\{System.Environment.UserName}" : null,
            AppPoolName = pool,
            SitePath = pool is null ? null : env.ContentRootPath.TrimEnd('\\', '/'),
            Groups = groups,
            // The organization settings in force: saved in Administration, else the server's.
            TimeZone = org.Zone.Id,
            MeetingTime = org.MeetingTime.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture),
            TeamName = org.TeamName,
            Origin = $"exported from TIDE {AppVersionInfo.Display} on {time.GetUtcNow():yyyy-MM-dd} by {user.DisplayName}",
        };
    }

    private void RequireAdmin()
    {
        if (!user.Has(Permission.Administer)) throw new ForbiddenException("Only an administrator can see the server's settings.");
    }
}
