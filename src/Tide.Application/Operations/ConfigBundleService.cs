using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tide.Application.Abstractions;
using Tide.Application.Actions;
using Tide.Application.Articles;
using Tide.Application.Distribution;
using Tide.Application.Security;
using Tide.Application.Settings;
using Tide.Domain.Common;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.Application.Operations;

/// <summary>
/// Exports and imports the <see cref="ConfigBundle"/>. Import is additive: an item is matched by its name
/// or key, updated when it differs and added when it's new; nothing is removed. Every item is checked as the app's own
/// editors check it, one that isn't valid is skipped with the reason, and the preview shows all of it before anything is
/// written. The changes are saved together (all or nothing) and recorded in the audit trail like any other change.
/// Administrators only.
/// </summary>
public sealed class ConfigBundleService(IAppDbContextFactory dbs, ICurrentUser user, IRoleDirectory directory, TimeProvider time,
    SavedSettings? saved = null, Distribution.EmailTemplateStore? emailTemplates = null, Org.OrgClock? clock = null,
    IOptions<DistributionOptions>? distribution = null, IOptions<ReminderOptions>? reminders = null,
    Distribution.EmailBoundary? boundary = null)
{
    // The address lists a bundle can carry: an address outside the organization in one is flagged in the preview, as the
    // page that edits it would ask before saving it.
    private static readonly HashSet<string> AddressLists = new(StringComparer.OrdinalIgnoreCase)
    {
        Settings.DistributionSettings.TeamRecipientsKey, Settings.DistributionSettings.PublicRecipientsKey,
        Rotation.RotationService.ManagerEmailsKey, OperationalSettings.BriefNotReadyRecipients,
    };

    private const string SettingsArea = "Settings";
    private const string GroupsArea = "AD groups";
    private const string SourcesArea = "Sources";
    private const string ScorecardArea = "Scorecard";
    private const string QueriesArea = "Query templates";
    private const string HolidaysArea = "Holidays";
    private const int MinPollMinutes = 5;
    private const int MaxPollMinutes = 7 * 24 * 60;

    public async Task<ConfigBundle> ExportAsync(CancellationToken ct = default)
    {
        RequireAdmin();
        await using var db = await dbs.CreateAsync(ct);
        var settings = (await db.Settings.AsNoTracking().ToListAsync(ct))
            .Where(s => SettingsCatalog.IsInApp(s.Key) || Distribution.EmailTemplateCatalog.IsKey(s.Key)).OrderBy(s => s.Key, StringComparer.Ordinal)
            .Select(s => new BundleSetting(s.Key, s.Value)).ToList();
        var mappings = (await db.RoleMappings.AsNoTracking().ToListAsync(ct))
            .OrderBy(m => m.RoleName, StringComparer.Ordinal).ThenBy(m => m.AdGroup, StringComparer.OrdinalIgnoreCase)
            .Select(m => new BundleRoleMapping(m.AdGroup, m.RoleName)).ToList();
        var sources = (await db.Sources.AsNoTracking().ToListAsync(ct)).OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).Select(s =>
        {
            var hidden = SecretMask.UrlHasSecret(s.Url);
            return new BundleSource(s.Name, s.Kind.ToString(), hidden ? SecretMask.Url(s.Url) : s.Url, s.DefaultTlp.ToString(), s.Reliability.ToString(),
                s.LicenseNote, s.PollMinutes, s.Enabled, s.IsRetired, s.OnlyCategories, s.SkipCategories, s.OnlyTitleWords, s.SkipTitleWords, hidden,
                s.ArticleFetch.ToString(), s.ArticleBodySelector, s.ArticleRemoveSelector);
        }).ToList();
        var categories = (await db.ScorecardCategories.AsNoTracking().Include(c => c.Tags).ToListAsync(ct))
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Key, StringComparer.Ordinal)
            .Select(c => new BundleScorecardCategory(c.Key, c.Name, c.Description, c.SortOrder, c.IsRetired,
                [.. c.Tags.OrderBy(t => t.SortOrder).ThenBy(t => t.Key, StringComparer.Ordinal)
                    .Select(t => new BundleScorecardTag(t.Key, t.Name, t.Description, t.SortOrder, t.IsRetired))]))
            .ToList();
        var templates = (await db.QueryTemplates.AsNoTracking().ToListAsync(ct))
            .OrderBy(q => q.Dialect, StringComparer.Ordinal).ThenBy(q => q.Kind)
            .Select(q => new BundleQueryTemplate(q.Dialect, q.Kind.ToString(), q.Template)).ToList();
        var holidays = (await db.Holidays.AsNoTracking().ToListAsync(ct)).OrderBy(h => h.Date).Select(h => new BundleHoliday(h.Date, h.Name)).ToList();
        return new ConfigBundle(ConfigBundle.FormatName, ConfigBundle.CurrentVersion, time.GetUtcNow(), user.DisplayName, AppVersionInfo.Display,
            settings, mappings, sources, categories, templates, holidays);
    }

    /// <summary>What importing the bundle would do; nothing is written.</summary>
    public async Task<BundlePreview> PreviewAsync(ConfigBundle bundle, CancellationToken ct = default)
    {
        RequireAdmin();
        await using var db = await dbs.CreateAsync(ct);
        return new BundlePreview(bundle, await PlanAsync(db, bundle, apply: false, ct));
    }

    /// <summary>Imports the bundle and says what it did. <paramref name="fingerprint"/> is the
    /// <see cref="BundlePreview.Fingerprint"/> of the preview the administrator read: when the plan worked out now differs
    /// (the configuration here changed since, or another bundle), nothing is written and they're asked to preview again.
    /// A plan with a warning is imported only when <paramref name="acknowledged"/> says the warnings were read.</summary>
    public async Task<BundlePreview> ImportAsync(ConfigBundle bundle, string fingerprint, bool acknowledged, CancellationToken ct = default)
    {
        RequireAdmin();
        await using var db = await dbs.CreateAsync(ct);
        var changes = await PlanAsync(db, bundle, apply: true, ct);
        // Refused before anything is saved: the context's pending changes go with it.
        var planned = new BundlePreview(bundle, changes);
        if (!string.Equals(planned.Fingerprint(), fingerprint, StringComparison.Ordinal))
            throw new StaleEditException("The configuration changed since you previewed it: preview again.");
        if (planned.Warnings > 0 && !acknowledged)
            throw new InvalidOperationException(
                $"{planned.NeedALook} a careful look: confirm you've read the warnings, then import.");
        await db.SaveChangesAsync(ct);
        if (changes.Any(c => c.Area == GroupsArea && c.Kind == BundleChangeKind.Add)) directory.Invalidate();
        // An operational setting imported takes effect at once, as one saved on its page does.
        if (saved is not null && changes.Any(c => c.Area == SettingsArea && c.Kind is BundleChangeKind.Add or BundleChangeKind.Update))
            await saved.RefreshAsync(dbs, ct);
        // So does an email template's wording.
        if (emailTemplates is not null && changes.Any(c => c.Area == SettingsArea && Distribution.EmailTemplateCatalog.IsKey(c.Item)
                                                           && c.Kind is BundleChangeKind.Add or BundleChangeKind.Update))
            await emailTemplates.RefreshAsync(dbs, ct);
        // And the holidays: the rota, deadlines and reminders follow them at once.
        if (clock is not null && changes.Any(c => c.Area == HolidaysArea && c.Kind is BundleChangeKind.Add or BundleChangeKind.Update))
            await Org.HolidayService.LoadAsync(db, clock, ct);
        return planned;
    }

    private async Task<List<BundleChange>> PlanAsync(IAppDbContext db, ConfigBundle bundle, bool apply, CancellationToken ct)
    {
        var changes = new List<BundleChange>();
        await PlanSettingsAsync(db, bundle, apply, changes, ct);
        await PlanRoleMappingsAsync(db, bundle, apply, changes, ct);
        await PlanSourcesAsync(db, bundle, apply, changes, ct);
        await PlanScorecardAsync(db, bundle, apply, changes, ct);
        await PlanQueryTemplatesAsync(db, bundle, apply, changes, ct);
        await PlanHolidaysAsync(db, bundle, apply, changes, ct);
        return changes;
    }

    private async Task PlanSettingsAsync(IAppDbContext db, ConfigBundle bundle, bool apply, List<BundleChange> changes, CancellationToken ct)
    {
        // By key ignoring case, the row written as the app writes it first (an earlier import may have left another beside it).
        var rows = new Dictionary<string, AppSetting>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in (await db.Settings.ToListAsync(ct)).OrderBy(r => r.Key == CanonicalKey(r.Key) ? 0 : 1))
            rows.TryAdd(r.Key, r);
        // Each key as the app writes it: a hand-edited "org:timezone" was matched to the catalogue ignoring case but stored
        // as typed, a second row that SQL Server's key refused (the whole import failed) and SQLite kept beside the real one,
        // unread, while the preview said it was added. Its checks by key were missed too.
        foreach (var s in bundle.Settings.Select(s => s with { Key = CanonicalKey(s.Key) }).DistinctBy(s => s.Key))
        {
            if (SettingProblem(s) is { } problem)
            {
                changes.Add(new(SettingsArea, s.Key, BundleChangeKind.Skipped, problem));
                continue;
            }
            var value = OperationalSettings.IsOperational(s.Key) ? OperationalSettings.Normalize(s.Key, s.Value)
                : Distribution.EmailTemplateCatalog.Parse(s.Key) is { } template ? Distribution.EmailTemplateCatalog.Normalize(template.Part, s.Value)
                : s.Value.Trim();
            if (rows.TryGetValue(s.Key, out var row) && row.Value == value)
            {
                changes.Add(new(SettingsArea, s.Key, BundleChangeKind.Unchanged));
                continue;
            }
            var kind = row is null ? BundleChangeKind.Add : BundleChangeKind.Update;
            // What widens what leaves (Widens), and an address outside the organization added to a list: either is warned, both said.
            var outside = AddressLists.Contains(s.Key)
                ? boundary.CurrentOrUnknown().Outside(OperationalSettings.SplitAddresses(value)
                    .Except(OperationalSettings.SplitAddresses(row?.Value ?? ServerValue(s.Key) ?? ""), StringComparer.OrdinalIgnoreCase))
                : [];
            var risks = new[] { Widens(s.Key, row?.Value ?? ServerValue(s.Key), value), outside.Count > 0 ? $"Outside the organization: {string.Join("; ", outside)}." : null }
                .OfType<string>().ToList();
            changes.Add(new(SettingsArea, s.Key, kind, risks.Count > 0 ? string.Join(" ", risks) : Clip(value), Warning: risks.Count > 0));
            if (!apply) continue;
            if (row is null) db.Settings.Add(rows[s.Key] = row = new AppSetting { Key = s.Key });
            row.Value = value;
            row.UpdatedAtUtc = time.GetUtcNow();
            row.UpdatedBy = user.UserId;
        }
    }

    // What a setting is when no row is saved for it: the server's value (or the built-in default), written as a row
    // would be. Only for the settings Widens looks at; null for the rest.
    private string? ServerValue(string key)
    {
        var d = distribution?.Value ?? new DistributionOptions();
        var r = reminders?.Value ?? new ReminderOptions();
        return key switch
        {
            DistributionSettings.TeamMaxTlpKey => d.TeamMaxTlp.ToString(),
            DistributionSettings.PublicMaxTlpKey => d.PublicMaxTlp.ToString(),
            DistributionSettings.TeamRecipientsKey => string.Join("; ", d.TeamRecipients ?? []),
            DistributionSettings.PublicRecipientsKey => string.Join("; ", d.PublicRecipients ?? []),
            OperationalSettings.BriefNotReadyRecipients => string.Join("; ", r.BriefNotReady.Recipients ?? []),
            OperationalSettings.ExternalReminders => r.ExternalReminders.Enabled ? "true" : "false",
            OperationalSettings.Escalation => r.Escalation.Enabled ? "true" : "false",
            OperationalSettings.EscalationSteps => r.Escalation.Steps,
            _ => Distribution.EmailTemplateCatalog.Parse(key) is { } t ? t.Template.Default(t.Part) : null,
        };
    }

    // The address lists whose recipients get mail from TIDE, and who they are, for the warning.
    private static readonly Dictionary<string, string> RecipientLists = new(StringComparer.OrdinalIgnoreCase)
    {
        [DistributionSettings.TeamRecipientsKey] = "the team edition",
        [DistributionSettings.PublicRecipientsKey] = "the company-wide edition",
        [OperationalSettings.BriefNotReadyRecipients] = "those told when there's no brief",
        [Rotation.RotationService.ManagerEmailsKey] = "the managers told about rotation swaps",
    };

    /// <summary>Why an incoming setting widens what leaves the organization, or null when it doesn't: it raises an
    /// edition's ceiling, adds recipients to a list TIDE mails, turns on email to people outside TIDE, or rewords an email
    /// that goes to them. <paramref name="current"/> is the value in force (saved, or the server's); <paramref name="value"/>
    /// the incoming one, as it would be stored.</summary>
    internal static string? Widens(string key, string? current, string value)
    {
        if (key is DistributionSettings.TeamMaxTlpKey or DistributionSettings.PublicMaxTlpKey)
        {
            var edition = key == DistributionSettings.TeamMaxTlpKey ? "team edition" : "company-wide edition";
            var to = Tlp.Parse(value);
            var from = Tlp.Parse(current);
            if (to is not { } raised || raised <= from) return null;   // no ceiling in force reads: any is warned
            return $"Raises the {edition}'s ceiling {(from is { } f ? $"from {Tlp.Label(f)} " : "")}to {Tlp.Label(raised)}.";
        }
        if (RecipientLists.TryGetValue(key, out var list))
        {
            var have = OperationalSettings.SplitAddresses(current).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var added = OperationalSettings.SplitAddresses(value).Distinct(StringComparer.OrdinalIgnoreCase).Where(a => !have.Contains(a)).ToList();
            if (added.Count == 0) return null;
            var named = string.Join(", ", added.Take(5)) + (added.Count > 5 ? $" and {added.Count - 5} more" : "");
            return $"Adds {Content.Words.Plural(added.Count, "recipient")} to {list}: {named}.";
        }
        if (key is OperationalSettings.ExternalReminders or OperationalSettings.Escalation)
        {
            var on = bool.TryParse(value, out var v) && v;
            var was = bool.TryParse(current, out var c) && c;
            if (!on || was) return null;
            return key == OperationalSettings.ExternalReminders
                ? "Turns on reminders to external teams, emailed to their contacts outside TIDE."
                : "Turns on the escalation ladder, which emails escalation contacts, outside TIDE included.";
        }
        if (key == OperationalSettings.EscalationSteps)
        {
            // A step that copies the escalation contact emails them, and one that reminds emails an external team's contacts:
            // both can reach people outside TIDE. One that wasn't there at that day, or that does more of it, is warned.
            const EscalationDoes mails = EscalationDoes.CopyContact | EscalationDoes.Remind;
            var had = new Dictionary<int, EscalationDoes>();
            foreach (var s in EscalationLadder.ParseOrNone(current)) had[s.Offset] = had.GetValueOrDefault(s.Offset) | (s.Does & mails);
            var added = EscalationLadder.ParseOrNone(value)
                .Where(s => (s.Does & mails & ~had.GetValueOrDefault(s.Offset)) != 0).Select(s => s.Text).ToList();
            return added.Count == 0 ? null
                : $"Adds escalation steps that email people outside TIDE (escalation contacts, external teams): {string.Join("; ", added)}.";
        }
        if (Distribution.EmailTemplateCatalog.Parse(key) is { } t && Distribution.EmailCopyRules.GoesOutside(t.Template)
            && Distribution.EmailTemplateCatalog.Normalize(t.Part, current) != value)
            return $"Changes the {t.Part.ToString().ToLowerInvariant()} of the {t.Template.Title} email, which goes to people outside TIDE: {Clip(value)}";
        return null;
    }

    // A setting's key as the catalogue (or the email templates) names it; any other as it came.
    private static string CanonicalKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return key ?? "";
        var k = key.Trim();
        k = SettingsCatalog.Renamed(k) ?? k;
        if (Distribution.EmailTemplateCatalog.Parse(k) is { } template) return Distribution.EmailTemplateCatalog.Key(template.Template.Id, template.Part);
        return SettingsCatalog.IsInApp(k) && SettingsCatalog.Find(k) is { } info ? info.Key : k;
    }

    /// <summary>Why a setting can't be imported, or null when it can: only the settings changed in the app, each with a
    /// value the app's own page would accept.</summary>
    internal static string? SettingProblem(BundleSetting s)
    {
        if (string.IsNullOrWhiteSpace(s.Key)) return "not a setting an administrator changes in the app";
        // A hand-edited file's null: not an empty value, which would clear a recipient list.
        if (s.Value is null) return "it has no value";
        if (Distribution.EmailTemplateCatalog.IsKey(s.Key)) return Distribution.EmailTemplateCatalog.Problem(s.Key, s.Value);
        if (string.IsNullOrWhiteSpace(s.Key) || !SettingsCatalog.IsInApp(s.Key)) return "not a setting an administrator changes in the app";
        var value = (s.Value ?? "").Trim();
        if (value.Length > 4000) return "the value is too long";
        if (s.Key.EndsWith("MaxTlp", StringComparison.Ordinal)) return Tlp.Parse(value) is null ? "not a TLP level" : null;
        if (s.Key.StartsWith("Actions:Sla:", StringComparison.Ordinal))
            return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var days) && days <= ActionSla.MaxDays
                ? null : $"not a number of business days (0 to {ActionSla.MaxDays})";
        if (s.Key == SettingsCatalog.UpdateCheckKey) return bool.TryParse(value, out _) ? null : "not true or false";
        if (OperationalSettings.IsOperational(s.Key)) return OperationalSettings.Problem(s.Key, value);
        // The rest are address lists, held to what the settings page accepts.
        var addresses = value.Split([';', ',', ' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries);
        // As the pages check them: the whole text an address ("<a@x.org>" parses, but isn't one, and a recipient list read
        // back drops it, so the preview said it was added while no mail went to it).
        var bad = addresses.FirstOrDefault(a => !Settings.DistributionSettings.IsAddress(a));
        if (bad is not null) return $"not an email address: {bad}";
        var count = addresses.Distinct(StringComparer.OrdinalIgnoreCase).Count();
        return count > Settings.DistributionSettings.MaxRecipients
            ? $"{count} addresses; a list holds at most {Settings.DistributionSettings.MaxRecipients} (use a distribution list)"
            : null;
    }

    private async Task PlanRoleMappingsAsync(IAppDbContext db, ConfigBundle bundle, bool apply, List<BundleChange> changes, CancellationToken ct)
    {
        var existing = await db.RoleMappings.AsNoTracking().Select(m => new { m.AdGroup, m.RoleName }).ToListAsync(ct);
        var have = existing.Select(m => (m.AdGroup.ToUpperInvariant(), m.RoleName)).ToHashSet();
        foreach (var m in bundle.RoleMappings)
        {
            var group = (m.AdGroup ?? "").Trim();
            var item = $"{group} → {m.Role}";
            if (group.Length is 0 or > 256 || !RoleDefinitions.TryParseRole(m.Role, out var role))
            {
                changes.Add(new(GroupsArea, item, BundleChangeKind.Skipped, "not a group name and a TIDE role"));
                continue;
            }
            if (!have.Add((group.ToUpperInvariant(), role.ToString())))
            {
                changes.Add(new(GroupsArea, item, BundleChangeKind.Unchanged));
                continue;
            }
            changes.Add(RiskOf(group, role) is { } risk ? new(GroupsArea, item, BundleChangeKind.Add, risk, Warning: true) : new(GroupsArea, item, BundleChangeKind.Add));
            if (apply) db.RoleMappings.Add(new AdGroupRoleMapping { AdGroup = group, RoleName = role.ToString(), UpdatedAtUtc = time.GetUtcNow(), UpdatedBy = user.UserId });
        }
    }

    // Groups every signed-in person (or everyone at all) is in, by name or SID: a role mapped to one is given to all.
    private static readonly HashSet<string> EveryoneGroups = new(StringComparer.OrdinalIgnoreCase)
    {
        "Everyone", "Authenticated Users", @"NT AUTHORITY\Authenticated Users", "Domain Users", "Users", @"BUILTIN\Users",
        "S-1-1-0", "S-1-5-11", "S-1-5-32-545",
        // The other identities a Windows sign-in token carries for everyone: the network logon (every Negotiate sign-in),
        // an interactive one, the organization, an identity the authentication authority asserted, and pre-2000 access.
        "NETWORK", @"NT AUTHORITY\NETWORK", "S-1-5-2",
        "INTERACTIVE", @"NT AUTHORITY\INTERACTIVE", "S-1-5-4",
        "This Organization", @"NT AUTHORITY\This Organization", "S-1-5-15",
        "Authentication authority asserted identity", "S-1-18-1",
        "Pre-Windows 2000 Compatible Access", @"BUILTIN\Pre-Windows 2000 Compatible Access", "S-1-5-32-554",
    };

    /// <summary>Why an incoming group mapping needs a careful look, or null: it gives Admin, or it maps a group everyone is
    /// in (Everyone, Authenticated Users, Domain Users in any domain, BUILTIN\Users, or their SIDs).</summary>
    internal static string? RiskOf(string group, AppRole role)
    {
        var g = group.Trim();
        var bare = g[(g.LastIndexOf('\\') + 1)..];
        var everyone = EveryoneGroups.Contains(g) || bare.Equals("Domain Users", StringComparison.OrdinalIgnoreCase)
            || (g.StartsWith("S-1-5-21-", StringComparison.OrdinalIgnoreCase) && g.EndsWith("-513", StringComparison.Ordinal));
        if (everyone)
            return $"Check this: {g} holds everyone who signs in, so all of them would get {role}" + (role == AppRole.Admin ? ", administration included." : ".");
        return role == AppRole.Admin ? $"Check this: members of {g} would administer this TIDE." : null;
    }

    private static async Task PlanSourcesAsync(IAppDbContext db, ConfigBundle bundle, bool apply, List<BundleChange> changes, CancellationToken ct)
    {
        var rows = (await db.Sources.ToListAsync(ct)).ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var b in bundle.Sources.DistinctBy(s => (s.Name ?? "").Trim(), StringComparer.OrdinalIgnoreCase))
        {
            var name = (b.Name ?? "").Trim();
            if (SourceProblem(b, out var kind, out var tlp, out var reliability, out var fetch) is { } problem)
            {
                changes.Add(new(SourcesArea, name.Length == 0 ? "(no name)" : name, BundleChangeKind.Skipped, problem));
                continue;
            }
            rows.TryGetValue(name, out var row);
            // An address the bundle hides a key in can't be used: an existing source keeps its own, a new one comes in off.
            var url = b.UrlHidden && row is not null ? row.Url : b.Url.Trim();
            var enabled = b.Enabled && !(b.UrlHidden && row is null);
            if (row is not null && row.Kind == kind && row.Url == url && row.DefaultTlp == tlp && row.Reliability == reliability
                && row.LicenseNote == b.LicenseNote && row.PollMinutes == b.PollMinutes && row.Enabled == enabled && row.IsRetired == b.IsRetired
                && row.OnlyCategories == b.OnlyCategories && row.SkipCategories == b.SkipCategories
                && row.OnlyTitleWords == b.OnlyTitleWords && row.SkipTitleWords == b.SkipTitleWords
                && row.ArticleFetch == fetch && row.ArticleBodySelector == b.ArticleBodySelector && row.ArticleRemoveSelector == b.ArticleRemoveSelector)
            {
                changes.Add(new(SourcesArea, name, BundleChangeKind.Unchanged));
                continue;
            }
            var detail = !b.UrlHidden ? null : row is null
                ? "comes in turned off: enter its address again (the bundle hides a key in it)"
                : "keeps its own address (the bundle hides a key in it)";
            // A lower default TLP lets the source's items into a wider edition, and to the AI provider (its limit is checked
            // against the source's TLP).
            var risk = row is not null && tlp < row.DefaultTlp
                ? $"Lowers its default TLP from {Tlp.Label(row.DefaultTlp)} to {Tlp.Label(tlp)}: its items can go in a wider edition, and to the AI provider."
                : null;
            changes.Add(new(SourcesArea, name, row is null ? BundleChangeKind.Add : BundleChangeKind.Update,
                risk is null ? detail : detail is null ? risk : $"{risk} It {detail}.", Warning: risk is not null));
            if (!apply) continue;
            if (row is null) db.Sources.Add(row = new Source { Name = name });
            if (row.Url != url) { row.ETag = null; row.LastModified = null; }
            row.Kind = kind;
            row.Url = url;
            row.DefaultTlp = tlp;
            row.Reliability = reliability;
            row.LicenseNote = b.LicenseNote;
            row.PollMinutes = b.PollMinutes;
            row.Enabled = enabled;
            row.IsRetired = b.IsRetired;
            row.OnlyCategories = b.OnlyCategories;
            row.SkipCategories = b.SkipCategories;
            row.OnlyTitleWords = b.OnlyTitleWords;
            row.SkipTitleWords = b.SkipTitleWords;
            row.ArticleFetch = fetch;
            row.ArticleBodySelector = b.ArticleBodySelector;
            row.ArticleRemoveSelector = b.ArticleRemoveSelector;
            row.EditVersion = Guid.NewGuid();   // a source form opened before the import can't save its old settings back
        }
    }

    private static string? SourceProblem(BundleSource b, out SourceKind kind, out TlpLevel tlp, out SourceReliability reliability, out ArticleFetch fetch)
    {
        tlp = default;
        reliability = default;
        fetch = ArticleFetch.TideOnly;
        if (!Enum.TryParse(b.Kind, true, out kind) || !Enum.IsDefined(kind)) return "not a kind of source";
        if (b.ArticleFetch is not null && !EnumNames.TryParse(b.ArticleFetch, out fetch, ignoreCase: true)) return "not a way of fetching articles";
        if ((b.ArticleBodySelector?.Length ?? 0) > Source.MaxBodySelector) return "its article body selector is too long";
        if ((b.ArticleRemoveSelector?.Length ?? 0) > Source.MaxRemoveSelector) return "its remove selector is too long";
        if (!Enum.TryParse(b.DefaultTlp, true, out tlp) || !Enum.IsDefined(tlp)) return "not a TLP level";
        if (!Enum.TryParse(b.Reliability, true, out reliability) || !Enum.IsDefined(reliability)) return "not a reliability (A to F)";
        var name = (b.Name ?? "").Trim();
        if (name.Length is 0 or > 100) return "a source's name is 1 to 100 characters";
        try { if (ArticleService.CheckUrl(b.Url, "Its address") is null) return "it has no address"; }
        catch (ArgumentException ex) { return ex.Message; }
        if (b.PollMinutes is < MinPollMinutes or > MaxPollMinutes) return $"polled every {b.PollMinutes} minutes (5 minutes to once a week)";
        if ((b.LicenseNote?.Length ?? 0) > 500) return "its terms note is too long";
        if (new[] { b.OnlyCategories, b.SkipCategories, b.OnlyTitleWords, b.SkipTitleWords }.Any(f => (f?.Length ?? 0) > 2000)) return "a filter is too long";
        return null;
    }

    private static async Task PlanScorecardAsync(IAppDbContext db, ConfigBundle bundle, bool apply, List<BundleChange> changes, CancellationToken ct)
    {
        // By key ignoring case, as SQL Server's unique key compares them: a hand-edited "Phishing" for the app's "phishing"
        // was a new row there, which its key refused (the whole import failed), and on SQLite a second category or tag.
        var categories = (await db.ScorecardCategories.ToListAsync(ct)).DistinctBy(c => c.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(c => c.Key, StringComparer.OrdinalIgnoreCase);
        var tags = (await db.ScorecardTags.ToListAsync(ct)).DistinctBy(t => t.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(t => t.Key, StringComparer.OrdinalIgnoreCase);
        foreach (var c in bundle.Scorecard.DistinctBy(c => c.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!ValidKeyName(c.Key, c.Name, c.Description))
            {
                changes.Add(new(ScorecardArea, c.Name ?? c.Key ?? "(no key)", BundleChangeKind.Skipped, "a key of up to 64 characters and a name of up to 100 are needed"));
                continue;
            }
            categories.TryGetValue(c.Key, out var cat);
            var same = cat is not null && cat.Name == c.Name && cat.Description == c.Description && cat.SortOrder == c.SortOrder && cat.IsRetired == c.IsRetired;
            changes.Add(new(ScorecardArea, c.Name, same ? BundleChangeKind.Unchanged : cat is null ? BundleChangeKind.Add : BundleChangeKind.Update));
            if (apply && !same)
            {
                if (cat is null) db.ScorecardCategories.Add(categories[c.Key] = cat = new ScorecardCategory { Key = c.Key });
                cat.Name = c.Name;
                cat.Description = c.Description;
                cat.SortOrder = c.SortOrder;
                cat.IsRetired = c.IsRetired;
            }
            foreach (var t in (c.Tags ?? []).DistinctBy(t => t.Key, StringComparer.OrdinalIgnoreCase))
                PlanTag(db, apply, changes, c, cat, tags, t);
        }
    }

    private static void PlanTag(IAppDbContext db, bool apply, List<BundleChange> changes, BundleScorecardCategory c, ScorecardCategory? cat,
        Dictionary<string, ScorecardTag> tags, BundleScorecardTag t)
    {
        var item = $"{c.Name} › {t.Name}";
        if (!ValidKeyName(t.Key, t.Name, t.Description))
        {
            changes.Add(new(ScorecardArea, item, BundleChangeKind.Skipped, "a key of up to 64 characters and a name of up to 100 are needed"));
            return;
        }
        tags.TryGetValue(t.Key, out var tag);
        var same = tag is not null && cat is not null && tag.CategoryId == cat.Id && tag.Name == t.Name && tag.Description == t.Description
            && tag.SortOrder == t.SortOrder && tag.IsRetired == t.IsRetired;
        changes.Add(new(ScorecardArea, item, same ? BundleChangeKind.Unchanged : tag is null ? BundleChangeKind.Add : BundleChangeKind.Update));
        if (!apply || same || cat is null) return;
        if (tag is null) db.ScorecardTags.Add(tags[t.Key] = tag = new ScorecardTag { Key = t.Key });
        tag.Category = cat;
        tag.CategoryId = cat.Id;
        tag.Name = t.Name;
        tag.Description = t.Description;
        tag.SortOrder = t.SortOrder;
        tag.IsRetired = t.IsRetired;
    }

    private static bool ValidKeyName([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? key,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? name, string? description) =>
        !string.IsNullOrWhiteSpace(key) && key.Length <= 64 && !string.IsNullOrWhiteSpace(name) && name.Length <= 100 && (description?.Length ?? 0) <= 500;

    private async Task PlanQueryTemplatesAsync(IAppDbContext db, ConfigBundle bundle, bool apply, List<BundleChange> changes, CancellationToken ct)
    {
        var rows = await db.QueryTemplates.ToListAsync(ct);
        var seen = new HashSet<(string, HuntValueKind)>();
        foreach (var q in bundle.QueryTemplates)
        {
            var item = $"{q.Dialect} · {q.Kind}";
            if (QueryDialects.Find(q.Dialect) is not { } d || !Enum.TryParse<HuntValueKind>(q.Kind, true, out var kind)
                || !Enum.IsDefined(kind) || string.IsNullOrWhiteSpace(q.Template) || q.Template.Length > 4000)
            {
                changes.Add(new(QueriesArea, item, BundleChangeKind.Skipped, "not a dialect, a kind of value and a template of up to 4,000 characters"));
                continue;
            }
            // The dialect as the app keys it ("kql"): a hand-edited "KQL" was found ignoring case but stored as typed, a
            // row SQL Server's key refused beside the app's (the whole import failed) and SQLite kept, never read.
            if (!seen.Add((d.Key, kind))) continue;
            item = $"{d.Key} · {kind}";
            var row = rows.FirstOrDefault(r => r.Dialect == d.Key && r.Kind == kind);
            if (row?.Template == q.Template)
            {
                changes.Add(new(QueriesArea, item, BundleChangeKind.Unchanged));
                continue;
            }
            changes.Add(new(QueriesArea, item, row is null ? BundleChangeKind.Add : BundleChangeKind.Update));
            if (!apply) continue;
            if (row is null)
            {
                db.QueryTemplates.Add(row = new QueryTemplate { Dialect = d.Key, Kind = kind });
                rows.Add(row);
            }
            row.Template = q.Template;
            row.UpdatedAtUtc = time.GetUtcNow();
            row.UpdatedBy = user.UserId;
        }
    }

    // A holiday is matched by its date, and held to what the page refuses (HolidayService): a weekend, no name or a long
    // one, a year outside 2000 to 2100, or a day with a brief (it would hide the brief and strand what was carried to it).
    private static async Task PlanHolidaysAsync(IAppDbContext db, ConfigBundle bundle, bool apply, List<BundleChange> changes, CancellationToken ct)
    {
        var rows = await db.Holidays.ToDictionaryAsync(h => h.Date, ct);
        var incoming = (bundle.Holidays ?? []).Where(h => h is not null).DistinctBy(h => h.Date).ToList();
        var dates = incoming.Select(h => h.Date).ToList();
        var briefed = (await db.Briefs.AsNoTracking().Where(b => dates.Contains(b.BriefDate)).Select(b => b.BriefDate).ToListAsync(ct)).ToHashSet();
        foreach (var h in incoming)
        {
            var name = (h.Name ?? "").Trim();
            var item = $"{h.Date:yyyy-MM-dd} {name}";
            if (name.Length is 0 or > Holiday.MaxName || Org.WorkingDays.IsWeekend(h.Date) || h.Date.Year is < 2000 or > 2100)
            {
                changes.Add(new(HolidaysArea, item, BundleChangeKind.Skipped, $"not a weekday from 2000 to 2100 with a name of up to {Holiday.MaxName} characters"));
                continue;
            }
            if (!rows.ContainsKey(h.Date) && briefed.Contains(h.Date))
            {
                changes.Add(new(HolidaysArea, item, BundleChangeKind.Skipped, "that day has a brief already"));
                continue;
            }
            if (rows.TryGetValue(h.Date, out var row) && row.Name == name)
            {
                changes.Add(new(HolidaysArea, item, BundleChangeKind.Unchanged));
                continue;
            }
            changes.Add(new(HolidaysArea, item, row is null ? BundleChangeKind.Add : BundleChangeKind.Update));
            if (!apply) continue;
            if (row is null) db.Holidays.Add(rows[h.Date] = row = new Holiday { Date = h.Date });
            row.Name = name;
        }
    }

    private static string Clip(string value) => value.Length <= 80 ? value : value[..79] + "…";

    private void RequireAdmin()
    {
        if (!user.Has(Permission.Administer)) throw new ForbiddenException("Only an administrator can export or import the configuration.");
    }
}
