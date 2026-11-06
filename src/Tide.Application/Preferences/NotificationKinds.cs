using Tide.Application.Actions;
using Tide.Domain.Enums;
using static Tide.Application.Preferences.UserPreferences;

namespace Tide.Application.Preferences;

/// <summary>A kind of notification email a person can choose about in My notifications.</summary>
/// <param name="Name">The preference it's kept under (stored in each person's preference rows: never rename one).</param>
/// <param name="Title">What My notifications calls it.</param>
/// <param name="About">What it covers.</param>
/// <param name="Values">The values its preference may hold, the default first (stored too).</param>
/// <param name="Needs">The permission a person needs to be sent it, so to choose about it.</param>
/// <param name="Scans">The reminder scans that send it. An administrator has it on for everyone when any of them is on;
/// one with none (the rotation's requests and notices) is always on.</param>
/// <param name="Note">The note under its switch, if any.</param>
/// <param name="InDigest">Whether the personal digest lists the same actions.</param>
public sealed record NotificationKind(string Name, string Title, string About, IReadOnlyList<string> Values, Permission Needs,
    IReadOnlyList<ReminderKind> Scans, string? Note = null, bool InDigest = false)
{
    /// <summary>An on/off switch (on by default), rather than the personal digest's cadence.</summary>
    public bool IsSwitch => Values[0] == On;

    /// <summary>Whether an administrator has it on for everyone.</summary>
    public bool IsOnForEveryone(ReminderOptions options) => Scans.Count == 0 || Scans.Any(options.IsEnabled);
}

/// <summary>
/// Every notification kind a person can choose about, in My notifications' order: the one place each is named, described,
/// given its values and permission, and tied to the scans that send it. The preference definitions
/// (<see cref="UserPreferences"/>), My notifications, Teams &amp; SLA and the senders all read it.
/// </summary>
public static class NotificationKinds
{
    // The action emails reach whoever is given an action or sits on a team without a mailbox, Viewers included.
    public static readonly NotificationKind DueSoon = new(NotifyDueSoon, "Due-soon reminders",
        "Actions due today or the next business day: yours, or your team's when it has no mailbox.",
        [On, Off], Permission.ReadActions, [ReminderKind.DueSoon], InDigest: true);

    public static readonly NotificationKind Overdue = new(NotifyOverdue, "Overdue reminders",
        "Actions past their due date: yours, or your team's when it has no mailbox.",
        [On, Off], Permission.ReadActions, [ReminderKind.Overdue], InDigest: true);

    public static readonly NotificationKind TeamDigest = new(NotifyTeamDigest, "Weekly team digest",
        "Your team's week, when it comes to its members because the team has no mailbox. A team mailbox still gets it.",
        [On, Off], Permission.ReadActions, [ReminderKind.Digest]);

    // Whoever works actions marks an Outreach action's email sent, so is the one reminded to follow it up.
    public static readonly NotificationKind OutreachFollowUp = new(NotifyOutreachFollowUp, "Outreach follow-ups",
        "A reminder when an email you marked sent on an Outreach action has had no reply recorded for a few business days.",
        [On, Off], Permission.WorkActions, [ReminderKind.OutreachFollowUp]);

    // The rotation's are for analysts in it.
    public static readonly NotificationKind RotationReminders = new(NotifyRotationReminders, "Rotation reminders",
        "\"You're presenting\" before your brief days, and the nudge when today's brief isn't ready.",
        [On, Off], Permission.WriteArticles, [ReminderKind.Presenting, ReminderKind.BriefNotReady], NotificationPreferenceService.DutyNote);

    public static readonly NotificationKind RotationRequests = new(NotifyRotationRequests, "Rotation requests and notices",
        "Swap or cover requests to you, and notices when one is approved, recorded or declined.",
        [On, Off], Permission.WriteArticles, []);

    public static readonly NotificationKind Digest = new(NotifyDigest, "Personal digest",
        "Your actions that are due soon or overdue, and those newly given to you.",
        [Off, Daily, Weekly], Permission.ReadActions, [ReminderKind.PersonalDigest]);

    /// <summary>All of them: the switches in the page's order, then the personal digest.</summary>
    public static readonly IReadOnlyList<NotificationKind> All = [DueSoon, Overdue, TeamDigest, OutreachFollowUp, RotationReminders, RotationRequests, Digest];

    /// <summary>The kind named <paramref name="name"/>, or null.</summary>
    public static NotificationKind? Find(string name) => All.FirstOrDefault(k => k.Name == name);

    /// <summary>The kind named <paramref name="name"/>; throws <see cref="ArgumentException"/> for any other.</summary>
    public static NotificationKind For(string name) =>
        Find(name) ?? throw new ArgumentException($"There's no notification named \"{name}\".", nameof(name));

    /// <summary>The kind a reminder scan sends, whose choice leaves people out of it (or, for the personal digest, takes it).</summary>
    public static NotificationKind For(ReminderKind scan) => All.First(k => k.Scans.Contains(scan));
}
