using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Tide.Application.Abstractions;
using Tide.Application.Assist;
using Tide.Application.Distribution;
using Tide.Application.Intake;
using Tide.Infrastructure.Assist;
using Tide.Infrastructure.Notifications;
using Tide.Infrastructure.Persistence;
using Tide.Infrastructure.Security;
using Tide.Infrastructure.Storage;

namespace Tide.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Registers persistence, the role and user directories, file storage and email.</summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<RoleMappingOptions>(config.GetSection("RoleMapping"));

        var provider = DatabaseProvider(config["Database:Provider"]);
        var connectionString = config.GetConnectionString("Default");

        // A context FACTORY, not a shared context: Blazor Server keeps one DI scope for a whole circuit, so a held
        // context would be used by overlapping event handlers. Services take IAppDbContextFactory and make a short-lived
        // context per operation. The factory is scoped so each context gets the scope's audit interceptor (which reads
        // the current user).
        void ConfigureProvider(IServiceProvider sp, DbContextOptionsBuilder options)
        {
            if (provider == SqlServer)
                options.UseSqlServer(
                    connectionString ?? throw new InvalidOperationException("ConnectionStrings:Default is required for SqlServer."),
                    // Production migrations live in their own assembly, so startup applies the SQL Server set.
                    sql => sql.MigrationsAssembly(AppDbContextFactory.SqlServerMigrationsAssembly).EnableRetryOnFailure());
            else
                options.UseSqlite(connectionString ?? "Data Source=App_Data/tide.db");
            options.AddInterceptors(sp.GetRequiredService<AuditInterceptor>(), sp.GetRequiredService<ChangeInterceptor>());
        }

        services.AddScoped<AuditInterceptor>();
        // What a save changed reaches colleagues' open pages (the feed is one per app; the origin one per circuit).
        services.AddSingleton<ChangeFeed>();
        services.AddScoped<ChangeOrigin>();
        services.AddScoped<ChangeInterceptor>();
        services.AddDbContextFactory<AppDbContext>(ConfigureProvider, lifetime: ServiceLifetime.Scoped);
        services.AddScoped<IAppDbContextFactory, AppDbContextFactoryAdapter>();
        // For the few Infrastructure pieces that make their own scope (the directories, start-up).
        services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext());

        services.AddSingleton<IRoleDirectory, RoleDirectory>();
        services.AddSingleton<IUserDirectory, UserDirectory>();
        services.AddSingleton<IFileStore, DiskFileStore>();
        services.AddSingleton<MetricDropFolder>();
        services.Configure<EmailOptions>(config.GetSection("Email"));
        // The relay password saved in the app, when the server's configuration has none.
        Tide.Application.DependencyInjection.SecretOverlay<EmailOptions>(services, Tide.Application.Settings.StoredSecretDefinition.SmtpPassword,
            o => o.SmtpPassword, (o, v) => o.SmtpPassword = v);
        services.AddSingleton<OutboundCircuit>();
        // Every email goes out through the standing copies (a template's CC and BCC, the archive), then the relay.
        services.AddSingleton<EmailSender>();
        services.AddSingleton<IEmailSender>(sp => new CopyingEmailSender(sp.GetRequiredService<EmailSender>(), sp.GetRequiredService<EmailCopyStore>(),
            sp.GetRequiredService<EmailBoundary>(), sp.GetService<Microsoft.Extensions.Logging.ILogger<CopyingEmailSender>>()));
        services.AddSingleton<IOutboundHttp, OutboundHttp>();
        services.AddSingleton<IThreatIntelAssistant, AzureOpenAiAssistant>();
        services.AddScoped<DatabaseInitializer>();
        return services;
    }

    private const string SqlServer = "SqlServer", Sqlite = "Sqlite";

    /// <summary>
    /// <c>Database:Provider</c> as it's used: SqlServer, or Sqlite (blank, in development). Anything else stops start-up
    /// saying so: a mistyped "SQL Server" was taken for SQLite, so start-up failed reading the SQL Server connection
    /// string as a SQLite one ("keyword not supported: 'server'"), or, with none, ran on an empty SQLite file.
    /// </summary>
    public static string DatabaseProvider(string? configured)
    {
        // Not trimmed: the deploy scripts and Diagnostics read the value as written, so " SqlServer" would be SQL Server
        // here and not there.
        var value = configured ?? "";
        if (string.IsNullOrWhiteSpace(value) || value.Equals(Sqlite, StringComparison.OrdinalIgnoreCase)) return Sqlite;
        if (value.Equals(SqlServer, StringComparison.OrdinalIgnoreCase)) return SqlServer;
        throw new InvalidOperationException($"Database:Provider is '{value}'; it must be SqlServer (production) or Sqlite (development).");
    }
}
