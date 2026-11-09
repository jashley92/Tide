using System.Globalization;
using Tide.Application.Actions;
using Tide.Application.Files;
using Tide.Application.Intake;
using Tide.Application.Metrics;
using Tide.Application.Operations;
using Tide.Application.Org;

namespace Tide.Application.Settings;

/// <summary>
/// The operational settings an administrator changes in the app, by the rule that operational settings are
/// changed in the app and audited, while security-sensitive and infrastructure settings stay on the server. Each key is
/// the configuration key it overrides, so the server's value is the default until an administrator saves one. Every
/// value is checked here, as it's saved, imported in a configuration bundle, and read back.
/// </summary>
public static class OperationalSettings
{
    public const string TimeZone = "Org:TimeZone";
    public const string MeetingTime = "Org:MeetingTime";
    public const string TeamName = "Org:TeamName";

    public const string ReminderTime = "Notifications:ReminderTime";
    public const string DueSoon = "Notifications:DueSoon:Enabled";
    public const string Presenting = "Notifications:Presenting:Enabled";
    public const string Overdue = "Notifications:Overdue:Enabled";
    public const string Digest = "Notifications:Digest:Enabled";
    public const string DigestDay = "Notifications:Digest:Day";
    public const string DigestTime = "Notifications:Digest:Time";
    public const string BriefNotReady = "Notifications:BriefNotReady:Enabled";
    public const string BriefNotReadyTime = "Notifications:BriefNotReady:Time";
    public const string BriefNotReadyRecipients = "Notifications:BriefNotReady:Recipients";

    /// <summary>Whether people may choose a personal digest in My notifications. On by default.</summary>
    public const string PersonalDigest = "Notifications:PersonalDigest:Enabled";

    /// <summary>The reminder to follow up an Outreach action's unanswered email, and after how many business days.</summary>
    public const string OutreachFollowUp = "Notifications:OutreachFollowUp:Enabled";
    public const string OutreachFollowUpDays = "Notifications:OutreachFollowUp:Days";
    public const string ExternalReminders = "Notifications:ExternalReminders:Enabled";

    /// <summary>Whether a new external team starts with its own reminders on. On by default.</summary>
    public const string ExternalRemindersNewTeams = "Notifications:ExternalReminders:NewTeamsOn";

    /// <summary>A new external team's schedule: business days before the due date, and every how many while overdue (0: once).</summary>
    public const string ExternalRemindersNewBefore = "Notifications:ExternalReminders:NewTeamsBeforeDays";
    public const string ExternalRemindersNewEvery = "Notifications:ExternalReminders:NewTeamsEveryDays";

    /// <summary>The escalation ladder: on or off, which actions it climbs, and its steps (<see cref="EscalationLadder"/>).</summary>
    public const string Escalation = "Notifications:Escalation:Enabled";
    public const string EscalationScope = "Notifications:Escalation:Scope";
    public const string EscalationSteps = "Notifications:Escalation:Steps";

    /// <summary>How many days since a person last signed in they're still reminded (and their roles must still allow what
    /// the email shows): 1 to 365, 30 by default.</summary>
    public const string ActiveWithinDays = "Notifications:ActiveWithinDays";

    public const string IntakeEnabled = "Intake:Enabled";
    public const string RetentionDays = "Intake:RetentionDays";
    public const string MinPollMinutes = "Intake:MinPollMinutes";
    public const string PullNowMinutes = "Intake:PullNowMinutes";
    public const string SkipSponsored = "Intake:SkipSponsored";
    public const string SponsoredMarkers = "Intake:SponsoredMarkers";
    public const string SponsoredExceptions = "Intake:SponsoredExceptions";
    public const string AgeDays = "Intake:AgeDays";
    public const string ReadSeconds = "Intake:ReadSeconds";
    public const string AdSelectors = "Intake:AdSelectors";
    public const string AdLabels = "Intake:AdLabels";

    /// <summary>Triage's suggested groups (EP-84): how far apart two reports of one event can be, in hours, and the
    /// words that don't count besides the built-in ones.</summary>
    public const string GroupWindowHours = "Intake:GroupWindowHours";
    public const string GroupIgnoredWords = "Intake:GroupIgnoredWords";

    public const string CveEnabled = "Cve:Enabled";
    public const string CveRefreshHours = "Cve:RefreshHours";

    public const string DropFolderEnabled = "Metrics:Import:DropFolderEnabled";
    public const string DropFolderPollMinutes = "Metrics:Import:PollMinutes";

    public const string UpdateIntervalHours = "Updates:CheckIntervalHours";

    /// <summary>Whether a brief isn't finished until someone confirms its SharePoint copy was posted. Off by default:
    /// not every team posts one.</summary>
    public const string RequireSharePoint = "Distribution:RequireSharePoint";

    /// <summary>When an action's file downloads inside a password-protected zip by default (<see cref="Files.ZipDownloads"/>):
    /// RiskyOnly (the default), Always or Never.</summary>
    public const string ZipDownloads = "Files:ZipDownloads";

    /// <summary>The most addresses told when a brief isn't ready (a larger audience is a distribution list).</summary>
    public const int MaxNudgeRecipients = 50;

    public const int MaxTeamName = 80;

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    // Each key's check: the value as it's stored, or an ArgumentException saying what's wrong.
    private static readonly Dictionary<string, Func<string, string>> Rules = new(StringComparer.OrdinalIgnoreCase)
    {
        [TimeZone] = Zone,
        [MeetingTime] = Time("The meeting time"),
        [TeamName] = Name,
        [ReminderTime] = Time("The reminder time"),
        [DueSoon] = Switch,
        [Presenting] = Switch,
        [Overdue] = Switch,
        [Digest] = Switch,
        [DigestDay] = EnumValue<DayOfWeek>("The digest day is a day of the week, such as Monday."),
        [DigestTime] = Time("The digest time"),
        [BriefNotReady] = Switch,
        [BriefNotReadyTime] = Time("The brief-not-ready time"),
        [BriefNotReadyRecipients] = Addresses,
        [PersonalDigest] = Switch,
        [OutreachFollowUp] = Switch,
        [OutreachFollowUpDays] = Number("The follow-up wait", ReminderOptions.FollowUpOptions.MinDays, ReminderOptions.FollowUpOptions.MaxDays),
        [ExternalReminders] = Switch,
        [ExternalRemindersNewTeams] = Switch,
        [ExternalRemindersNewBefore] = Number("Days before the due date", 0, Domain.Entities.Team.MaxRemindBefore),
        [ExternalRemindersNewEvery] = v => v == "0" ? v : Number("While overdue, the days between reminders (0: once)", Domain.Entities.Team.MinRemindEvery, Domain.Entities.Team.MaxRemindEvery)(v),
        [Escalation] = Switch,
        [EscalationScope] = EnumValue<Actions.EscalationScope>("The escalation ladder climbs All actions, PartsOnly or ExternalOnly."),
        [EscalationSteps] = EscalationLadder.Normalize,
        [ActiveWithinDays] = Number("Days since someone last signed in", ReminderOptions.MinActiveWithinDays, ReminderOptions.MaxActiveWithinDays),
        [IntakeEnabled] = Switch,
        [RetentionDays] = Number("Days feed items are kept", 7, 3650),
        [MinPollMinutes] = Number("The shortest poll", 5, 24 * 60),
        [PullNowMinutes] = Number("The analyst pull-now window", 1, 24 * 60),
        [SkipSponsored] = Switch,
        [SponsoredMarkers] = v => SponsoredFilter.Normalize("The marker list", v, required: true),
        [SponsoredExceptions] = v => SponsoredFilter.Normalize("The exception list", v, required: false),
        [AgeDays] = Number("Days before an untouched item ages", 1, 90),
        [ReadSeconds] = Number("The time allowed to read an article", 2, 60),
        [AdSelectors] = Assist.ArticleCleanup.NormalizeSelectors,
        [AdLabels] = Assist.ArticleCleanup.NormalizeLabels,
        [GroupWindowHours] = Number("How far apart two reports of one event can be", TriageGroups.MinWindowHours, TriageGroups.MaxWindowHours),
        [GroupIgnoredWords] = TriageGroups.NormalizeWords,
        [CveEnabled] = Switch,
        [CveRefreshHours] = Number("The CVE refresh", 1, 168),
        [DropFolderEnabled] = Switch,
        [DropFolderPollMinutes] = Number("The drop folder's interval", 1, 60),
        [UpdateIntervalHours] = Number("The update check's interval", 6, 168),
        [RequireSharePoint] = Switch,
        [ZipDownloads] = EnumValue<Files.ZipDownloads>("Zipped downloads are RiskyOnly, Always or Never."),
    };

    public static IReadOnlyCollection<string> Keys => Rules.Keys;

    public static bool IsOperational(string key) => Rules.ContainsKey(key);

    /// <summary>The value as it's stored ("true", "08:00", "a@x.org; b@x.org"). Throws <see cref="ArgumentException"/>
    /// saying what's wrong with it, or when the key isn't one of these settings.</summary>
    public static string Normalize(string key, string? value)
    {
        if (!Rules.TryGetValue(key, out var rule)) throw new ArgumentException($"{key} isn't a setting changed in the app.");
        return rule((value ?? "").Trim());
    }

    /// <summary>Why a value can't be saved, or null when it can.</summary>
    public static string? Problem(string key, string? value)
    {
        try
        {
            Normalize(key, value);
            return null;
        }
        catch (ArgumentException ex) { return ex.Message; }
    }

    /// <summary>The time zones to choose from, by IANA id, with their offset now: every zone this server knows, and the
    /// one in force if it isn't among them.</summary>
    public static IReadOnlyList<(string Id, string Label)> TimeZoneChoices(string? current = null)
    {
        var now = DateTimeOffset.UtcNow;
        var ids = TimeZoneInfo.GetSystemTimeZones()
            .Select(z => z.HasIanaId ? z.Id : TimeZoneInfo.TryConvertWindowsIdToIanaId(z.Id, out var iana) ? iana : null)
            .OfType<string>()
            .Append("UTC")
            .Concat(current is not null ? [current.Trim()] : [])
            .Where(IsIana)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);
        return [.. ids.Select(id => (id, Label(id, TimeZoneInfo.FindSystemTimeZoneById(id).GetUtcOffset(now))))];

        static string Label(string id, TimeSpan offset) =>
            $"{id.Replace('_', ' ')} (UTC{(offset < TimeSpan.Zero ? "−" : "+")}{offset.Duration():hh\\:mm})";
    }

    // --- read back over the server's values (OperationalSettingsOverlay) ------------------------------------------

    internal static void Apply(SavedSettings s, OrgOptions o)
    {
        s.Use(TimeZone, v => o.TimeZone = v);
        s.Use(MeetingTime, v => o.MeetingTime = v);
        s.Use(TeamName, v => o.TeamName = v);
    }

    internal static void Apply(SavedSettings s, ReminderOptions o)
    {
        s.Use(ReminderTime, v => o.ReminderTime = v);
        s.Use(DueSoon, v => o.DueSoon.Enabled = bool.Parse(v));
        s.Use(Presenting, v => o.Presenting.Enabled = bool.Parse(v));
        s.Use(Overdue, v => o.Overdue.Enabled = bool.Parse(v));
        s.Use(Digest, v => o.Digest.Enabled = bool.Parse(v));
        s.Use(DigestDay, v => o.Digest.Day = Enum.Parse<DayOfWeek>(v));
        s.Use(DigestTime, v => o.Digest.Time = v);
        s.Use(BriefNotReady, v => o.BriefNotReady.Enabled = bool.Parse(v));
        s.Use(BriefNotReadyTime, v => o.BriefNotReady.Time = v);
        s.Use(BriefNotReadyRecipients, v => o.BriefNotReady.Recipients = SplitAddresses(v));
        s.Use(PersonalDigest, v => o.PersonalDigest.Enabled = bool.Parse(v));
        s.Use(OutreachFollowUp, v => o.OutreachFollowUp.Enabled = bool.Parse(v));
        s.Use(OutreachFollowUpDays, v => o.OutreachFollowUp.Days = int.Parse(v, Invariant));
        s.Use(ExternalReminders, v => o.ExternalReminders.Enabled = bool.Parse(v));
        s.Use(ExternalRemindersNewTeams, v => o.ExternalReminders.NewTeamsOn = bool.Parse(v));
        s.Use(ExternalRemindersNewBefore, v => o.ExternalReminders.NewTeamsBeforeDays = int.Parse(v, Invariant));
        s.Use(ExternalRemindersNewEvery, v => o.ExternalReminders.NewTeamsEveryDays = int.Parse(v, Invariant));
        s.Use(Escalation, v => o.Escalation.Enabled = bool.Parse(v));
        s.Use(EscalationScope, v => o.Escalation.Scope = Enum.Parse<Actions.EscalationScope>(v));
        s.Use(EscalationSteps, v => o.Escalation.Steps = v);
        s.Use(ActiveWithinDays, v => o.ActiveWithinDays = int.Parse(v, Invariant));
    }

    internal static void Apply(SavedSettings s, IntakeOptions o)
    {
        s.Use(IntakeEnabled, v => o.Enabled = bool.Parse(v));
        s.Use(RetentionDays, v => o.RetentionDays = int.Parse(v, Invariant));
        s.Use(MinPollMinutes, v => o.MinPollMinutes = int.Parse(v, Invariant));
        s.Use(PullNowMinutes, v => o.PullNowMinutes = int.Parse(v, Invariant));
        s.Use(SkipSponsored, v => o.SkipSponsored = bool.Parse(v));
        s.Use(SponsoredMarkers, v => o.SponsoredMarkers = v);
        s.Use(SponsoredExceptions, v => o.SponsoredExceptions = v);
        s.Use(AgeDays, v => o.AgeDays = int.Parse(v, Invariant));
        s.Use(ReadSeconds, v => o.ReadSeconds = int.Parse(v, Invariant));
        s.Use(AdSelectors, v => o.AdSelectors = v);
        s.Use(AdLabels, v => o.AdLabels = v);
        s.Use(GroupWindowHours, v => o.GroupWindowHours = int.Parse(v, Invariant));
        s.Use(GroupIgnoredWords, v => o.GroupIgnoredWords = v);
    }

    internal static void Apply(SavedSettings s, CveOptions o)
    {
        s.Use(CveEnabled, v => o.Enabled = bool.Parse(v));
        s.Use(CveRefreshHours, v => o.RefreshHours = int.Parse(v, Invariant));
    }

    internal static void Apply(SavedSettings s, MetricImportOptions o)
    {
        s.Use(DropFolderEnabled, v => o.DropFolderEnabled = bool.Parse(v));
        s.Use(DropFolderPollMinutes, v => o.PollMinutes = int.Parse(v, Invariant));
    }

    internal static void Apply(SavedSettings s, DistributionOptions o) =>
        s.Use(RequireSharePoint, v => o.RequireSharePoint = bool.Parse(v));

    internal static void Apply(SavedSettings s, FileStoreOptions o) =>
        s.Use(ZipDownloads, v => o.ZipDownloads = Enum.Parse<Files.ZipDownloads>(v));

    internal static void Apply(SavedSettings s, UpdateOptions o) =>
        s.Use(UpdateIntervalHours, v => o.CheckIntervalHours = int.Parse(v, Invariant));

    /// <summary>A stored address list, as the addresses.</summary>
    public static string[] SplitAddresses(string? value) =>
        [.. (value ?? "").Split(DistributionSettings.Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    // --- the checks ------------------------------------------------------------------------------------------------

    private static string Switch(string v) => bool.TryParse(v, out var on)
        ? (on ? "true" : "false")
        : throw new ArgumentException("A switch is on (true) or off (false).");

    private static Func<string, string> Number(string what, int min, int max) => v =>
        int.TryParse(v, NumberStyles.None, Invariant, out var n) && n >= min && n <= max
            ? n.ToString(Invariant)
            : throw new ArgumentException($"{what} is a whole number from {min} to {max}.");

    private static Func<string, string> Time(string what) => v =>
        TimeOnly.TryParseExact(v, ["HH:mm", "H:mm"], Invariant, DateTimeStyles.None, out var t)
            ? t.ToString("HH:mm", Invariant)
            : throw new ArgumentException($"{what} is a 24-hour time such as 08:00.");

    // One of an enum's names, any case, stored as named; never its number, which would read as a name that isn't one.
    private static Func<string, string> EnumValue<T>(string message) where T : struct, Enum => v =>
        !int.TryParse(v, out _) && Enum.TryParse<T>(v, ignoreCase: true, out var value) && Enum.IsDefined(value)
            ? value.ToString()
            : throw new ArgumentException(message);

    private static string Name(string v) => v.Length is > 0 and <= MaxTeamName
        ? v
        : throw new ArgumentException($"The team name is 1 to {MaxTeamName} characters.");

    private static string Zone(string v) => IsIana(v) && TimeZoneInfo.TryFindSystemTimeZoneById(v, out var zone)
        ? zone.Id
        : throw new ArgumentException($"'{v}' isn't a time zone this server knows. Choose one from the list (an IANA id such as America/New_York).");

    private static bool IsIana(string id) =>
        id.Length > 0 && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone) && zone.HasIanaId;

    private static string Addresses(string v)
    {
        var raw = SplitAddresses(v);
        var bad = raw.Where(a => !DistributionSettings.IsAddress(a)).ToList();
        if (bad.Count > 0) throw new ArgumentException($"Not an email address: {string.Join(", ", bad.Take(3))}{(bad.Count > 3 ? "…" : "")}");
        var list = raw.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Count > MaxNudgeRecipients) throw new ArgumentException($"At most {MaxNudgeRecipients} addresses; use a team mailbox for more.");
        return string.Join("; ", list);
    }
}
