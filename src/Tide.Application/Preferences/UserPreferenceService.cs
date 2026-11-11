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

    public sealed record Definition(string Name, IReadOnlyList<string> Values, Permission Needs)
    {
        public string Default => Values[0];
    }

    // The notification kinds' values and permissions are theirs (NotificationKinds).
    private static readonly Dictionary<string, Definition> Known =
        new Definition[]
            {
                new(ActionsLayout, [Board, List], Permission.ReadActions), new(ActionsView, [Mine, MyTeams, AllOpen], Permission.ReadActions),
                new(HuntDialect, QueryDialects.All.Select(d => d.Key).ToList(), Permission.ReadTeamEdition),
                new(TriageReading, [ReadingAuto, ReadingOpen, ReadingClosed], Permission.WriteArticles),
            }
            .Concat(NotificationKinds.All.Select(k => new Definition(k.Name, k.Values, k.Needs)))
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
        return stored is not null && d.Values.Contains(stored) ? stored : d.Default;
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
        return defs.ToDictionary(d => d.Name, d => stored.TryGetValue(d.Name, out var v) && d.Values.Contains(v) ? v : d.Default, StringComparer.Ordinal);
    }

    /// <summary>Keeps <paramref name="value"/> as the person's <paramref name="name"/>. Throws
    /// <see cref="ArgumentException"/> for a value the preference can't hold.</summary>
    public async Task SetAsync(string name, string value, CancellationToken ct = default)
    {
        var d = Require(name);
        if (!d.Values.Contains(value)) throw new ArgumentException($"\"{value}\" isn't one of {string.Join(", ", d.Values)}.", nameof(value));
        for (var attempt = 0; ; attempt++)
        {
            await using var db = await dbs.CreateAsync(ct);
            var row = await db.UserPreferences.FirstOrDefaultAsync(p => p.UserId == user.UserId && p.Name == d.Name, ct);
            if (row is null) db.UserPreferences.Add(row = new UserPreference { UserId = user.UserId, Name = d.Name });
            row.Value = value;
            row.UpdatedAtUtc = time.GetUtcNow();
            try
            {
                await db.SaveChangesAsync(ct);
                return;
            }
            // The same person's other tab added the row first: change that one instead.
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
