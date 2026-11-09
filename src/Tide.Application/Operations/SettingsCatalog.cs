using Tide.Application.Actions;
using Tide.Application.Settings;
using Tide.Domain.Enums;

namespace Tide.Application.Operations;

/// <summary>A setting Administration › System reports on.</summary>
/// <param name="Group">The heading it's listed under ("Database").</param>
/// <param name="EditedIn">For a setting an administrator can change in the app (saved in the database, over the server's
/// value): the page, relative to the app ("admin/teams"). Null for one set on the server only.</param>
public sealed record SettingInfo(string Group, string Label, string Key, SettingKind Kind = SettingKind.Plain, string? EditedIn = null)
{
    public bool InApp => EditedIn is not null;
}

/// <summary>
/// The settings Server configuration and Setting sources list: what the server's configuration
/// sets (appsettings.json, the appsettings.Production.json the installer writes, environment variables), and which of
/// them an administrator can override in the app. Secrets are marked, so they're only ever shown as set or not.
/// </summary>
public static class SettingsCatalog
{
    public const string UpdateCheckKey = "Updates:CheckEnabled";

    private const string OrganizationPage = "admin/organization";
    private const string TeamsPage = "admin/teams";
    private const string SourcesPage = "admin/sources";
    private const string MetricImportsPage = "admin/metric-imports";

    public static readonly IReadOnlyList<SettingInfo> Server =
    [
        new("Hosting", "Sign-in", "Auth:Mode"),
        new("Hosting", "Address readers reach TIDE at", "App:BaseUrl", SettingKind.Url),
        new("Hosting", "Path base (behind a proxy)", "App:PathBase"),
        new("Hosting", "Allowed host names", "AllowedHosts"),
        new("Hosting", "Data Protection key folder", "DataProtection:KeyPath"),
        new("Hosting", "Screenshot folder", "Files:RootPath"),
        new("Hosting", "Largest screenshot (MB)", "Files:MaxImageMB"),
        new("Hosting", "Largest other file on an action (MB)", "Files:MaxEvidenceMB"),

        new("Database", "Provider", "Database:Provider"),
        new("Database", "Applies its own schema", "Database:ApplyMigrations"),
        new("Database", "Connection", "ConnectionStrings:Default", SettingKind.ConnectionString),

        new("Email", "Sending", "Email:Enabled"),
        new("Email", "Relay", "Email:SmtpHost"),
        new("Email", "Port", "Email:SmtpPort"),
        new("Email", "STARTTLS", "Email:EnableSsl"),
        new("Email", "Relay user name", "Email:SmtpUsername"),
        new("Email", "Relay password", "Email:SmtpPassword", SettingKind.Secret),
        new("Email", "Signs in as the app pool", "Email:SmtpUseDefaultCredentials"),
        new("Email", "Time limit (seconds)", "Email:SmtpTimeoutSeconds"),
        new("Email", "From", "Email:From"),
        new("Email", "From name", "Email:FromName"),
        new("Email", "Pickup folder (instead of sending)", "Email:PickupDirectory"),

        new("Intake", "Proxy", "Intake:Proxy", SettingKind.Url),
        new("Intake", "Private networks allowed", "Intake:AllowPrivateNetworks"),
        new("Intake", "Time limit per request (seconds)", "Intake:TimeoutSeconds"),
        new("Intake", "Largest response (KB)", "Intake:MaxResponseKB"),

        new("CVE enrichment", "NVD API key", "Cve:NvdApiKey", SettingKind.Secret),
        new("CVE enrichment", "NVD address", "Cve:NvdUrl", SettingKind.Url),
        new("CVE enrichment", "EPSS address", "Cve:EpssUrl", SettingKind.Url),
        new("CVE enrichment", "KEV address", "Cve:KevUrl", SettingKind.Url),

        new("Updates", "Release repository", "Updates:Repository"),

        new("AI drafting", "Provider", "Ai:Provider"),
        new("AI drafting", "Azure OpenAI resource", "Ai:Endpoint", SettingKind.Url),
        new("AI drafting", "Model deployment", "Ai:Deployment"),
        new("AI drafting", "API key", "Ai:ApiKey", SettingKind.Secret),
        new("AI drafting", "Highest TLP sent", "Ai:MaxTlp"),
        new("AI drafting", "Web search for open sources", "Ai:WebGrounding"),
        new("AI drafting", "Time limit (seconds)", "Ai:TimeoutSeconds"),
    ];

    /// <summary>The settings an administrator can change in the app; the server's value is the default until then. The
    /// operational ones (<see cref="OperationalSettings"/>) as well as the edition, SLA, rotation and update ones.</summary>
    public static readonly IReadOnlyList<SettingInfo> InApp =
    [
        new("Distribution", "Company-wide edition ceiling", "Distribution:PublicMaxTlp", EditedIn: "admin/access"),
        new("Distribution", "Team edition ceiling", "Distribution:TeamMaxTlp", EditedIn: "admin/access"),
        new("Distribution", "Team edition recipients", "Distribution:TeamRecipients", EditedIn: "admin/access"),
        new("Distribution", "Company-wide edition recipients", "Distribution:PublicRecipients", EditedIn: "admin/access"),
        new("Distribution", "SharePoint posting required", OperationalSettings.RequireSharePoint, EditedIn: "admin/access"),
        new("Distribution", "Organization's email domains", OperationalSettings.OrganizationDomains, EditedIn: "admin/access"),
        .. Enum.GetValues<Priority>().Select(p =>
            new SettingInfo("Action SLA", $"{p} target (business days)", "Actions:Sla:Priority:" + p, EditedIn: "admin/teams")),
        .. Enum.GetValues<ActionKind>().Select(k =>
            new SettingInfo("Action SLA", $"{ActionService.KindName(k)} target (business days)", "Actions:Sla:Kind:" + k, EditedIn: "admin/teams")),
        new("Brief rotation", "Managers told about swaps", "Rotation:ManagerEmails", EditedIn: "admin/rotation"),
        new("Updates", "Check for new releases", UpdateCheckKey, EditedIn: "admin/system/updates"),
        new("Updates", "Check every (hours)", OperationalSettings.UpdateIntervalHours, EditedIn: "admin/system/updates"),

        new("Organization", "Time zone", OperationalSettings.TimeZone, EditedIn: OrganizationPage),
        new("Organization", "Brief meets at", OperationalSettings.MeetingTime, EditedIn: OrganizationPage),
        new("Organization", "Team name", OperationalSettings.TeamName, EditedIn: OrganizationPage),

        new("Reminders", "Reminder time", OperationalSettings.ReminderTime, EditedIn: TeamsPage),
        new("Reminders", "Due soon", OperationalSettings.DueSoon, EditedIn: TeamsPage),
        new("Reminders", "Presenting tomorrow", OperationalSettings.Presenting, EditedIn: TeamsPage),
        new("Reminders", "Overdue", OperationalSettings.Overdue, EditedIn: TeamsPage),
        new("Reminders", "Weekly digest", OperationalSettings.Digest, EditedIn: TeamsPage),
        new("Reminders", "Digest day", OperationalSettings.DigestDay, EditedIn: TeamsPage),
        new("Reminders", "Digest time", OperationalSettings.DigestTime, EditedIn: TeamsPage),
        new("Reminders", "Brief not ready", OperationalSettings.BriefNotReady, EditedIn: TeamsPage),
        new("Reminders", "Brief-not-ready time", OperationalSettings.BriefNotReadyTime, EditedIn: TeamsPage),
        new("Reminders", "Told when there's no brief", OperationalSettings.BriefNotReadyRecipients, EditedIn: TeamsPage),
        new("Reminders", "Personal digest offered", OperationalSettings.PersonalDigest, EditedIn: TeamsPage),
        new("Reminders", "Outreach follow-up", OperationalSettings.OutreachFollowUp, EditedIn: TeamsPage),
        new("Reminders", "Outreach follow-up after (business days)", OperationalSettings.OutreachFollowUpDays, EditedIn: TeamsPage),
        new("Reminders", "External team reminders", OperationalSettings.ExternalReminders, EditedIn: TeamsPage),
        new("Reminders", "New external teams start with reminders on", OperationalSettings.ExternalRemindersNewTeams, EditedIn: TeamsPage),
        new("Reminders", "New external teams: days before the due date", OperationalSettings.ExternalRemindersNewBefore, EditedIn: TeamsPage),
        new("Reminders", "New external teams: every (days) while overdue", OperationalSettings.ExternalRemindersNewEvery, EditedIn: TeamsPage),
        new("Reminders", "Remind people signed in within (days)", OperationalSettings.ActiveWithinDays, EditedIn: TeamsPage),
        new("Escalation", "Escalation ladder", OperationalSettings.Escalation, EditedIn: TeamsPage),
        new("Escalation", "Actions it climbs", OperationalSettings.EscalationScope, EditedIn: TeamsPage),
        new("Escalation", "Its steps", OperationalSettings.EscalationSteps, EditedIn: TeamsPage),

        new("Intake", "Polling", OperationalSettings.IntakeEnabled, EditedIn: SourcesPage),
        new("Intake", "Feed items kept (days)", OperationalSettings.RetentionDays, EditedIn: SourcesPage),
        new("Intake", "Shortest poll (minutes)", OperationalSettings.MinPollMinutes, EditedIn: SourcesPage),
        new("Intake", "Analyst pull-now window (minutes)", OperationalSettings.PullNowMinutes, EditedIn: SourcesPage),
        new("Intake", "Skip sponsored items", OperationalSettings.SkipSponsored, EditedIn: SourcesPage),
        new("Intake", "Sponsored markers", OperationalSettings.SponsoredMarkers, EditedIn: SourcesPage),
        new("Intake", "Not sponsorship (exceptions)", OperationalSettings.SponsoredExceptions, EditedIn: SourcesPage),
        new("Intake", "Days before an untouched item ages", OperationalSettings.AgeDays, EditedIn: SourcesPage),
        new("Intake", "Time allowed to read an article (seconds)", OperationalSettings.ReadSeconds, EditedIn: SourcesPage),
        new("Intake", "Advert selectors", OperationalSettings.AdSelectors, EditedIn: SourcesPage),
        new("Intake", "Advert labels", OperationalSettings.AdLabels, EditedIn: SourcesPage),
        new("Intake", "How far apart two reports of one event can be (hours)", OperationalSettings.GroupWindowHours, EditedIn: SourcesPage),
        new("Intake", "Extra words triage grouping ignores", OperationalSettings.GroupIgnoredWords, EditedIn: SourcesPage),

        new("CVE enrichment", "Enrichment", OperationalSettings.CveEnabled, EditedIn: SourcesPage),
        new("CVE enrichment", "Refresh (hours)", OperationalSettings.CveRefreshHours, EditedIn: SourcesPage),

        new("Metric imports", "Drop folder", OperationalSettings.DropFolderEnabled, EditedIn: MetricImportsPage),
        new("Metric imports", "Drop folder read every (minutes)", OperationalSettings.DropFolderPollMinutes, EditedIn: MetricImportsPage),

        new("Files", "Downloads in a password-protected zip", OperationalSettings.ZipDownloads, EditedIn: TeamsPage),
    ];

    private static readonly HashSet<string> InAppKeys = InApp.Select(s => s.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, SettingInfo> ByKey =
        Server.Concat(InApp).GroupBy(s => s.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);

    /// <summary>The setting listed under <paramref name="key"/> (an in-app one over the server's), or null.</summary>
    public static SettingInfo? Find(string key) => ByKey.GetValueOrDefault(key);

    /// <summary>Whether a setting's value is a secret, shown only as set: listed as one, or named like one
    /// (<see cref="SecretMask.LooksSecret"/>), as a guard against a secret listed as plain by mistake.</summary>
    public static bool IsSecret(string key) => Find(key)?.Kind == SettingKind.Secret || SecretMask.LooksSecret(key);

    /// <summary>
    /// A key an earlier release wrote, as this one writes it, or null: a kind's SLA target was saved under its name before
    /// a rename (<see cref="ActionService.FormerKind"/>; ADR 0019: "Actions:Sla:Kind:VendorInquiry"). The upgrade's
    /// migration moves saved rows; this reads a bundle exported before it.
    /// </summary>
    public static string? Renamed(string key) =>
        key.StartsWith(SlaKindPrefix, StringComparison.OrdinalIgnoreCase) && ActionService.FormerKind(key[SlaKindPrefix.Length..]) is { } kind
            ? SlaKindPrefix + kind
            : null;

    private const string SlaKindPrefix = "Actions:Sla:Kind:";

    /// <summary>Whether a saved setting row is one an administrator sets (and so travels in a configuration bundle), as
    /// opposed to the app's own bookkeeping (when CVE data was last refreshed).</summary>
    public static bool IsInApp(string key) => InAppKeys.Contains(key);
}
