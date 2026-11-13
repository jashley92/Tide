using Microsoft.EntityFrameworkCore;
using Tide.Application.Abstractions;
using Tide.Application.Actions;
using Tide.Application.Security;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.Application.Preferences;

/// <summary>
/// The preferences a person can keep, each with the values it may hold (the first is its default) and the permission
/// that opens the page it belongs to. A new preference is one more entry here; its rows go in the same table.
/// </summary>
public static class UserPreferences
{
    /// <summary>The Actions page as a board (the default) or a list.</summary>
    public const string ActionsLayout = "actions.layout";

    public const string List = "list", Board = "board";

    /// <summary>The Actions page's tab when a link doesn't say which: Mine (the default), My teams or All open.</summary>
    public const string ActionsView = "actions.view";

    public const string Mine = "mine", MyTeams = "myteams", AllOpen = "open";

    /// <summary>The query language a lookup's hunt query is written in on the Search page: the one the person chose
    /// there last (a dialect's key; the first dialect until they choose).</summary>
    public const string HuntDialect = "hunt.dialect";

    /// <summary>Triage's reading pane: open by itself on a wide window (the default, until the person chooses), or as
    /// they last left it, open (V) or closed (Esc or its close button), at every width.</summary>
    public const string TriageReading = "triage.reading";

    public const string ReadingAuto = "auto", ReadingOpen = "open", ReadingClosed = "closed";

    /// <summary>The release whose "What's new" the person last saw ("1.13.0"); none ("") until they've seen one.</summary>
    public const string WhatsNewSeen = "whatsnew.seen";

    /// <summary>The first-sign-in tour: due (the user mirror sets it on a person's first sign-in), done, or none for
    /// someone who used TIDE before the tour existed, who gets What's new instead.</summary>
    public const string TourState = "tour.state";

    public const string TourDue = "due", TourDone = "done";

    // --- My notifications (NotificationPreferenceService) ----------------------------------------------------------

    /// <summary>Due-soon reminders about the person's actions (or their team's, when it has no mailbox): on or off.</summary>
    public const string NotifyDueSoon = "notify.dueSoon";

    /// <summary>Overdue reminders, likewise.</summary>
    public const string NotifyOverdue = "notify.overdue";

    /// <summary>The weekly team digest, when it goes to the team's members because the team has no mailbox.</summary>
    public const string NotifyTeamDigest = "notify.teamDigest";

    /// <summary>The reminder to follow up an Outreach action's email that has had no reply.</summary>
    public const string NotifyOutreachFollowUp = "notify.outreachFollowUp";

    /// <summary>An action handed to a team the person is on, when the team has no mailbox.</summary>
    public const string NotifyHandoffs = "notify.handoffs";

    /// <summary>"You're presenting" and the brief-not-ready nudge.</summary>
    public const string NotifyRotationReminders = "notify.rotationReminders";

    /// <summary>Swap or cover requests to the person, and approved, recorded or declined notices.</summary>
    public const string NotifyRotationRequests = "notify.rotationRequests";

    /// <summary>The personal digest: off (the default), daily or weekly.</summary>
    public const string NotifyDigest = "notify.digest";

    public const string On = "on", Off = "off", Daily = "daily", Weekly = "weekly";

    /// <summary>A preference: one of a fixed list of <paramref name="Values"/> (the first its default), or, made with
    /// <see cref="Matching"/>, any value a rule allows (a release's version).</summary>
    public sealed record Definition(string Name, IReadOnlyList<string> Values, Permission Needs)
    {
        private string? _default;
        private Func<string, bool>? _allows;

        public string Default => _default ?? Values[0];

        /// <summary>Whether the preference may hold <paramref name="value"/>.</summary>
        public bool Allows(string value) => _allows?.Invoke(value) ?? Values.Contains(value);

        /// <summary>A preference that holds any value <paramref name="allows"/> allows, <paramref name="default"/> until set.</summary>
        public static Definition Matching(string name, string @default, Func<string, bool> allows, Permission needs) =>
            new(name, [], needs) { _default = @default, _allows = allows };
    }

    // The notification kinds' values and permissions are theirs (NotificationKinds).
    private static readonly Dictionary<string, Definition> Known =
        new Definition[]
            {
                new(ActionsLayout, [Board, List], Permission.ReadActions), new(ActionsView, [Mine, MyTeams, AllOpen], Permission.ReadActions),
                new(HuntDialect, QueryDialects.All.Select(d => d.Key).ToList(), Permission.ReadTeamEdition),
                new(TriageReading, [ReadingAuto, ReadingOpen, ReadingClosed], Permission.WriteArticles),
                // Everyone who reads release highlights reads the team edition (RoleGroups.For).
                Definition.Matching(WhatsNewSeen, "", Operations.ReleaseVersion.IsVersionOrNone, Permission.ReadTeamEdition),
                new(TourState, ["", TourDue, TourDone], Permission.ReadTeamEdition),
            }
            .Concat(NotificationKinds.All.Select(k => new Definition(k.Name, k.Values, k.Needs)))
            // Each page's tip: the release whose tip the person dismissed there, for whoever can use the page.
            .Concat(Operations.PageTips.All.Select(t => Definition.Matching(t.Preference, "", Operations.ReleaseVersion.IsVersionOrNone, t.Needs)))
            // A page's Show me around link: Help's switch, the first sign-in and the release it's counted from, and each
            // page guide's run or put away (GuideLinkService).
            .Concat([
                new(Operations.GuideLinkService.Links, ["", Operations.GuideLinkService.On, Operations.GuideLinkService.Off], Permission.ReadTeamEdition),
                Definition.Matching(Operations.GuideLinkService.Since, "", Operations.GuideLinkService.IsDayOrNone, Permission.ReadTeamEdition),
                Definition.Matching(Operations.GuideLinkService.Release, "", Operations.GuideLinkService.IsReleaseOrNone, Permission.ReadTeamEdition),
            ])
            .Concat(Operations.Guides.OnPages.Select(g => new Definition(Operations.GuideLinkService.Done(g), ["", Operations.GuideLinkService.DoneValue], g.Needs)))
            .ToDictionary(d => d.Name, StringComparer.Ordinal);

    /// <summary>The preference named <paramref name="name"/>; throws <see cref="ArgumentException"/> for any other.</summary>
    public static Definition For(string name) =>
        Known.TryGetValue(name, out var d) ? d : throw new ArgumentException($"There's no preference named \"{name}\".", nameof(name));
}

/// <summary>
/// The signed-in person's own preferences (<see cref="UserPreferences"/>), kept on the server so they follow them from
/// computer to computer. A preference only says how a page is shown to the person who chose it, so anyone who can open
/// that page may keep one, Viewers too: it's not a write to shared data, and no one can read or change another's.
/// </summary>
public sealed class UserPreferenceService(IAppDbContextFactory dbs, ICurrentUser user, TimeProvider time)
{
    /// <summary>The person's value for <paramref name="name"/>, or the preference's default when they haven't chosen
    /// (or what's stored is no longer one of its values).</summary>
    public async Task<string> GetAsync(string name, CancellationToken ct = default)
    {
        var d = Require(name);
        await using var db = await dbs.CreateAsync(ct);
        var stored = await db.UserPreferences.AsNoTracking()
            .Where(p => p.UserId == user.UserId && p.Name == d.Name).Select(p => p.Value).FirstOrDefaultAsync(ct);
        return stored is not null && d.Allows(stored) ? stored : d.Default;
    }

    /// <summary>The person's values for each of <paramref name="names"/> (as <see cref="GetAsync(string, CancellationToken)"/>
    /// has them), read in one query.</summary>
    public async Task<IReadOnlyDictionary<string, string>> GetAsync(IEnumerable<string> names, CancellationToken ct = default)
    {
        var defs = names.Distinct().Select(Require).ToList();
        var wanted = defs.Select(d => d.Name).ToList();
        await using var db = await dbs.CreateAsync(ct);
        var rows = await db.UserPreferences.AsNoTracking()
            .Where(p => p.UserId == user.UserId && wanted.Contains(p.Name)).Select(p => new { p.Name, p.Value }).ToListAsync(ct);
        var stored = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var r in rows) stored.TryAdd(r.Name, r.Value);
        return defs.ToDictionary(d => d.Name, d => stored.TryGetValue(d.Name, out var v) && d.Allows(v) ? v : d.Default, StringComparer.Ordinal);
    }

    /// <summary>Keeps <paramref name="value"/> as the person's <paramref name="name"/>. Throws
    /// <see cref="ArgumentException"/> for a value the preference can't hold.</summary>
    public Task SetAsync(string name, string value, CancellationToken ct = default) =>
        SetAsync(new Dictionary<string, string>(StringComparer.Ordinal) { [name] = value }, ct);

    /// <summary>Keeps each of <paramref name="values"/> as the person's, in one save: all or none. Throws as
    /// <see cref="SetAsync(string, string, CancellationToken)"/> does for any one of them, before saving anything.</summary>
    public async Task SetAsync(IReadOnlyDictionary<string, string> values, CancellationToken ct = default)
    {
        foreach (var (name, value) in values)
        {
            var d = Require(name);
            if (!d.Allows(value))
                throw new ArgumentException(d.Values.Count > 0 ? $"\"{value}\" isn't one of {string.Join(", ", d.Values)}." : $"\"{value}\" isn't a value {d.Name} can hold.", nameof(values));
        }
        var names = values.Keys.ToList();
        for (var attempt = 0; ; attempt++)
        {
            await using var db = await dbs.CreateAsync(ct);
            var rows = await db.UserPreferences.Where(p => p.UserId == user.UserId && names.Contains(p.Name)).ToDictionaryAsync(p => p.Name, StringComparer.Ordinal, ct);
            foreach (var (name, value) in values)
            {
                if (!rows.TryGetValue(name, out var row)) db.UserPreferences.Add(row = new UserPreference { UserId = user.UserId, Name = name });
                row.Value = value;
                row.UpdatedAtUtc = time.GetUtcNow();
            }
            try
            {
                await db.SaveChangesAsync(ct);
                return;
            }
            // The same person's other tab added a row first: change that one instead.
            catch (DbUpdateException) when (attempt == 0) { }
        }
    }

    private UserPreferences.Definition Require(string name)
    {
        var d = UserPreferences.For(name);
        if (!user.IsAuthenticated) throw new ForbiddenException("Sign in to keep a preference.");
        if (!user.Has(d.Needs)) throw new ForbiddenException($"You don't have permission to {Policies.Describe(d.Needs)}.");
        return d;
    }
}
