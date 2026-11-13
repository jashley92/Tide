using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Tide.Application.Actions;
using Tide.Application.Admin;
using Tide.Application.Articles;
using Tide.Application.Assist;
using Tide.Application.Briefs;
using Tide.Application.Content;
using Tide.Application.Distribution;
using Tide.Application.Exports;
using Tide.Application.Files;
using Tide.Application.History;
using Tide.Application.Intake;
using Tide.Application.Metrics;
using Tide.Application.Operations;
using Tide.Application.Org;
using Tide.Application.Rotation;
using Tide.Application.Scorecard;
using Tide.Application.Search;
using Tide.Application.Settings;
using Tide.Application.Trends;

namespace Tide.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services, IConfiguration config)
    {
        // A feed or page in Shift_JIS, EUC-KR, GB2312, ISO-2022 or a windows-125x code page is read with these encodings,
        // which .NET leaves out until they're registered (for the whole process; registering again changes nothing).
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        services.Configure<OrgOptions>(config.GetSection("Org"));
        services.Configure<DistributionOptions>(config.GetSection("Distribution"));
        services.Configure<FileStoreOptions>(config.GetSection("Files"));
        services.Configure<AppOptions>(config.GetSection("App"));
        services.Configure<SlaOptions>(config.GetSection("Actions:Sla"));
        services.Configure<ReminderOptions>(config.GetSection("Notifications"));
        services.AddSingleton<ReminderLog>();
        services.Configure<IntakeOptions>(config.GetSection("Intake"));
        services.Configure<MetricImportOptions>(config.GetSection("Metrics:Import"));
        services.Configure<CveOptions>(config.GetSection("Cve"));
        services.Configure<UpdateOptions>(config.GetSection("Updates"));
        services.Configure<AiOptions>(config.GetSection("Ai"));
        services.Configure<EmailBoundaryOptions>(config.GetSection("Email"));
        services.AddSingleton<JobMonitor>();
        services.AddSingleton<JobRunLog>();
        // One process runs the background jobs (the lease in the database); the others stand by.
        services.AddSingleton<BackgroundJobLease>();
        services.AddSingleton<IJobLease>(sp => sp.GetRequiredService<BackgroundJobLease>());
        services.AddSingleton<UpdateCheckService>();
        services.AddScoped<UpdateSettings>();
        services.AddScoped<ConfigBundleService>();
        services.AddSingleton<CveRunLog>();
        services.AddSingleton<SponsoredLog>();
        services.AddSingleton<SourcePollGuard>();
        services.AddSingleton(TimeProvider.System);
        // The operational settings saved in Administration, laid over the server's values (OperationalSettings).
        services.AddSingleton<SavedSettings>();
        services.AddSingleton<EmailTemplateStore>();
        services.AddSingleton<EmailCopyStore>();
        services.AddScoped<EmailTemplateService>();
        services.AddScoped<OperationalSettingsService>();
        Overlay<OrgOptions>(services, OperationalSettings.Apply);
        Overlay<ReminderOptions>(services, OperationalSettings.Apply);
        Overlay<IntakeOptions>(services, OperationalSettings.Apply);
        Overlay<CveOptions>(services, OperationalSettings.Apply);
        Overlay<MetricImportOptions>(services, OperationalSettings.Apply);
        Overlay<UpdateOptions>(services, OperationalSettings.Apply);
        Overlay<DistributionOptions>(services, OperationalSettings.Apply);
        Overlay<FileStoreOptions>(services, OperationalSettings.Apply);
        Overlay<EmailBoundaryOptions>(services, OperationalSettings.Apply);
        // Secrets saved in the app fill in what the server's configuration leaves blank.
        services.AddSingleton<StoredSecrets>();
        services.AddScoped<StoredSecretService>();
        SecretOverlay<CveOptions>(services, StoredSecretDefinition.NvdApiKey, o => o.NvdApiKey, (o, v) => o.NvdApiKey = v);
        SecretOverlay<AiOptions>(services, StoredSecretDefinition.AiApiKey, o => o.ApiKey, (o, v) => o.ApiKey = v);
        // The organization's email boundary: every list that sends mail outward checks its addresses against it.
        services.AddSingleton(sp => new EmailBoundary(sp.GetRequiredService<IOptionsMonitor<EmailBoundaryOptions>>()));
        services.AddSingleton(sp => new OrgClock(sp.GetRequiredService<IOptionsMonitor<OrgOptions>>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IMarkdownService, MarkdownService>();
        services.AddSingleton<BriefRenderer>();
        services.AddSingleton<PublishedContentCache>();

        services.AddScoped<ArticleService>();
        services.AddScoped<AssistService>();
        services.AddScoped<HolidayService>();
        services.AddScoped<Preferences.UserPreferenceService>();
        services.AddSingleton(Operations.ReleaseNotesCatalog.Embedded);
        services.AddScoped<Operations.WhatsNewService>();
        services.AddScoped<Operations.PageTipService>();
        services.AddScoped<Preferences.SavedViewService>();
        services.AddScoped<Preferences.NotificationPreferenceService>();
        services.AddScoped<Api.ApiTokenService>();
        services.AddScoped<Api.WebhookService>();
        services.AddScoped<Api.ApiService>();
        services.AddScoped<Adversaries.AdversaryService>();
        services.AddScoped<Detections.DetectionService>();
        services.AddScoped<Reviews.ReviewService>();
        services.AddScoped<ScreenshotService>();
        services.AddScoped<ActionFileService>();
        services.AddScoped<BriefService>();
        services.AddScoped<MeetingService>();
        services.AddScoped<ScorecardService>();
        services.AddScoped<RoleMappingService>();
        services.AddScoped<DistributionSettings>();
        services.AddScoped<MetricsService>();
        services.AddScoped<MetricImportService>();
        services.AddScoped<DistributionService>();
        services.AddScoped<CorrectionService>();
        services.AddScoped<TeamService>();
        services.AddScoped<SlaSettings>();
        services.AddScoped<HuntQueryBuilder>();
        services.AddScoped<ActionService>();
        services.AddScoped<HandoffService>();
        services.AddScoped<HandoffNotices>();
        services.AddScoped<PlaybookService>();
        services.AddScoped<SavedQueryService>();
        services.AddScoped<LibraryUseService>();
        services.AddScoped<OutreachTemplateService>();
        services.AddScoped<OutreachService>();
        services.AddScoped<CardRuleService>();
        services.AddScoped<ReminderService>();
        services.AddScoped<IntakeService>();
        services.AddScoped<ReadingService>();
        services.AddScoped<CveService>();
        services.AddScoped<StoryService>();
        services.AddScoped<RotationService>();
        services.AddScoped<IPresenterLookup>(sp => sp.GetRequiredService<RotationService>());
        services.AddScoped<SearchService>();
        services.AddScoped<RelatedService>();
        services.AddScoped<ExportService>();
        services.AddScoped<TrendsService>();
        services.AddScoped<Trends.ReportService>();
        services.AddScoped<Trends.TeamScorecardService>();
        services.AddScoped<Stack.StackService>();
        services.AddScoped<HistoryService>();
        services.AddScoped<AuditLogService>();
        services.AddScoped<DownloadAudit>();
        return services;
    }

    /// <summary>Fills in <typeparamref name="T"/>'s <paramref name="secret"/> from the one saved in the app, where the
    /// server's configuration leaves it blank (the server's always wins).</summary>
    public static void SecretOverlay<T>(IServiceCollection services, StoredSecretDefinition secret, Func<T, string> get, Action<T, string> set) where T : class =>
        Overlay<StoredSecrets, T>(services, (s, o) =>
        {
            if (string.IsNullOrWhiteSpace(get(o)) && s.TryGet(secret, out var saved)) set(o, saved);
        });

    private static void Overlay<T>(IServiceCollection services, Action<SavedSettings, T> apply) where T : class => Overlay<SavedSettings, T>(services, apply);

    private static void Overlay<TSource, T>(IServiceCollection services, Action<TSource, T> apply) where TSource : IOverlaySource where T : class
    {
        services.AddSingleton(sp => new OptionsOverlay<TSource, T>(sp.GetRequiredService<TSource>(), apply));
        services.AddSingleton<IPostConfigureOptions<T>>(sp => sp.GetRequiredService<OptionsOverlay<TSource, T>>());
        services.AddSingleton<IOptionsChangeTokenSource<T>>(sp => sp.GetRequiredService<OptionsOverlay<TSource, T>>());
    }
}
