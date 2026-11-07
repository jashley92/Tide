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
        services.AddSingleton<JobMonitor>();
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
        services.AddSingleton(sp => new OrgClock(sp.GetRequiredService<IOptionsMonitor<OrgOptions>>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IMarkdownService, MarkdownService>();
        services.AddSingleton<BriefRenderer>();
        services.AddSingleton<PublishedContentCache>();

        services.AddScoped<ArticleService>();
        services.AddScoped<AssistService>();
        services.AddScoped<HolidayService>();
        services.AddScoped<Preferences.UserPreferenceService>();
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
        return services;
    }

    private static void Overlay<T>(IServiceCollection services, Action<SavedSettings, T> apply) where T : class
    {
        services.AddSingleton(sp => new OperationalSettingsOverlay<T>(sp.GetRequiredService<SavedSettings>(), apply));
        services.AddSingleton<IPostConfigureOptions<T>>(sp => sp.GetRequiredService<OperationalSettingsOverlay<T>>());
        services.AddSingleton<IOptionsChangeTokenSource<T>>(sp => sp.GetRequiredService<OperationalSettingsOverlay<T>>());
    }
}
