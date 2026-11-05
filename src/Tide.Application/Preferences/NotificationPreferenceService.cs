using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tide.Application.Abstractions;
using Tide.Application.Actions;
using Tide.Application.Distribution;
using Tide.Application.Security;
using Tide.Domain.Enums;
using static Tide.Application.Preferences.UserPreferences;

namespace Tide.Application.Preferences;

/// <summary>One of a person's notification switches, as My notifications shows it.</summary>
/// <param name="On">Whether they get it: their choice, or off when <paramref name="OffForEveryone"/>.</param>
/// <param name="OffForEveryone">An administrator has this kind off on Teams &amp; SLA, so the switch can't change anything.</param>
public sealed record NotificationSwitch(string Name, string Title, string About, string? Note, bool On, bool OffForEveryone);

/// <summary>The signed-in person's notification choices.</summary>
/// <param name="Address">Where their mail goes (AD's address, or the one saved from it), or null when there's none.</param>
/// <param name="Digest">Their personal digest: <see cref="UserPreferences.Off"/>, daily or weekly.</param>
/// <param name="DigestOffered">Whether an administrator offers the personal digest.</param>
public sealed record MyNotificationChoices(string? Address, IReadOnlyList<NotificationSwitch> Switches, string Digest, bool DigestOffered);

/// <summary>Someone who chose something (opted out of a kind, or takes a personal digest), for an administrator's list.</summary>
public sealed record NotificationChooser(string UserId, string Name, string? Email, string Value, DateTimeOffset Since);

/// <summary>
/// People's own choices about the notification emails, kept in the per-user preference store (<see cref="UserPreferences"/>):
/// one on/off switch per kind (on by default) and the personal digest (off by default). An administrator still turns each
/// kind on or off for everyone on Teams &amp; SLA; a person can only turn off what's on, and can't change anyone else's.
/// The reminders and the rotation read the choices where they pick recipients (<see cref="RecipientResolver"/>). The
/// brief's distribution, the test copies people send themselves and corrections never read them.
/// </summary>
public sealed class NotificationPreferenceService(IAppDbContextFactory dbs, ICurrentUser user, UserPreferenceService preferences,
    IOptionsMonitor<ReminderOptions> reminders)
{
    /// <summary>The note under the rotation reminders' switch: they're a duty.</summary>
    public const string DutyNote = "You're expected to present on your rota days; turn this off only if you track them another way.";

    /// <summary>What a switch says when an administrator has its kind off for everyone.</summary>
    public const string OffForEveryoneText = "Off for everyone (set by an administrator)";

    /// <summary>The hint beside a per-item reminder when the person's digest covers the same actions.</summary>
    public const string AlsoInDigest = "You also get these in your digest.";

    /// <summary>The on/off switches, in the page's order.</summary>
    public static readonly IReadOnlyList<string> Switches = [.. NotificationKinds.All.Where(k => k.IsSwitch).Select(k => k.Name)];

    /// <summary>What each switch is called, and what it covers.</summary>
    public static (string Title, string About) Describe(string name)
    {
        var kind = NotificationKinds.For(name);
        return (kind.Title, kind.About);
    }

    /// <summary>Whether an administrator has the kind on for everyone (rotation requests have no switch: always on).</summary>
    public bool IsOnForEveryone(string name) => NotificationKinds.Find(name)?.IsOnForEveryone(reminders.CurrentValue) ?? true;

    /// <summary>The signed-in person's choices, with what an administrator has off and where their mail goes.</summary>
    public async Task<MyNotificationChoices> GetMineAsync(CancellationToken ct = default)
    {
        Require();
        var kinds = NotificationKinds.All.Where(k => k.IsSwitch && user.Has(k.Needs)).ToList();
        var mine = await preferences.GetAsync(kinds.Select(k => k.Name).Append(NotifyDigest), ct);
        var list = kinds.Select(k =>
        {
            var everyone = IsOnForEveryone(k.Name);
            return new NotificationSwitch(k.Name, k.Title, k.About, k.Note, everyone && mine[k.Name] == On, !everyone);
        }).ToList();
        var address = await OwnAddress.ForAsync(user, dbs, ct);
        return new MyNotificationChoices(address, list, mine[NotifyDigest], IsOnForEveryone(NotifyDigest));
    }

    /// <summary>
    /// Keeps one of the signed-in person's choices. Refused for a kind an administrator has off for everyone (there's
    /// nothing to opt out of, or a digest to opt in to), and, as every preference is, for a value it can't hold.
    /// </summary>
    public async Task SetMineAsync(string name, string value, CancellationToken ct = default)
    {
        Require();
        NotificationKinds.For(name);
        if (!IsOnForEveryone(name)) throw new InvalidOperationException($"{Describe(name).Title}: {OffForEveryoneText}.");
        await preferences.SetAsync(name, value, ct);
    }

    // --- an administrator's view: how many chose, and who ------------------------------------------------------

    /// <summary>
    /// How many people opted out of each switch, and how many take a personal digest (under <see cref="NotifyDigest"/>).
    /// Administrators only: it reads everyone's choices, which <see cref="UserPreferenceService"/> never does.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, int>> CountsAsync(CancellationToken ct = default)
    {
        RequireAdmin();
        var names = Switches.Append(NotifyDigest).ToList();
        await using var db = await dbs.CreateAsync(ct);
        var rows = await db.UserPreferences.AsNoTracking().Where(p => names.Contains(p.Name))
            .GroupBy(p => new { p.Name, p.Value }).Select(g => new { g.Key.Name, g.Key.Value, Count = g.Count() }).ToListAsync(ct);
        return names.ToDictionary(n => n, n => rows.Where(r => r.Name == n && Chose(n, r.Value)).Sum(r => r.Count));
    }

    /// <summary>Who opted out of <paramref name="name"/> (or, for <see cref="NotifyDigest"/>, takes a digest), by name, with
    /// their address and when they chose. Administrators only, and only to read: no one changes another's choice.</summary>
    public async Task<IReadOnlyList<NotificationChooser>> PeopleAsync(string name, CancellationToken ct = default)
    {
        RequireAdmin();
        NotificationKinds.For(name);
        await using var db = await dbs.CreateAsync(ct);
        var rows = await db.UserPreferences.AsNoTracking().Where(p => p.Name == name).ToListAsync(ct);
        rows = [.. rows.Where(r => Chose(name, r.Value))];
        var ids = rows.Select(r => r.UserId).ToList();
        var people = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Sid)).ToDictionaryAsync(u => u.Sid, ct);
        return [.. rows.Select(r => people.TryGetValue(r.UserId, out var u)
                ? new NotificationChooser(r.UserId, u.DisplayName, string.IsNullOrWhiteSpace(u.Email) ? null : u.Email, r.Value, r.UpdatedAtUtc)
                : new NotificationChooser(r.UserId, r.UserId, null, r.Value, r.UpdatedAtUtc))
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)];
    }

    // A row that counts: an opt-out of a switch, or a digest taken.
    private static bool Chose(string name, string value) => name == NotifyDigest ? value is Daily or Weekly : value == Off;

    private void Require()
    {
        if (!user.IsAuthenticated) throw new ForbiddenException("Sign in to choose your notifications.");
        if (!user.Has(Permission.ReadActions)) throw new ForbiddenException("You don't get TIDE's notification emails, so there's nothing to choose.");
    }

    private void RequireAdmin()
    {
        if (!user.Has(Permission.Administer)) throw new ForbiddenException("Only an administrator can see who chose what.");
    }
}
