using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tide.Application.Abstractions;
using Tide.Application.Distribution;
using Tide.Application.Security;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.Application.Settings;

/// <summary>The defaults for how briefs are cut (config section <c>Distribution</c>); admins can override them in the app.</summary>
public sealed class DistributionOptions
{
    /// <summary>The highest TLP the company-wide (public) edition carries.</summary>
    public TlpLevel PublicMaxTlp { get; set; } = TlpLevel.Green;

    /// <summary>The highest TLP the team edition (and the meeting) carries. RED is for named recipients, so not by
    /// default.</summary>
    public TlpLevel TeamMaxTlp { get; set; } = TlpLevel.AmberStrict;

    /// <summary>Who the team edition is emailed to, until an administrator sets the list in the app.</summary>
    public string[] TeamRecipients { get; set; } = [];

    /// <summary>Who the company-wide edition is emailed to (often one distribution list), until set in the app.</summary>
    public string[] PublicRecipients { get; set; } = [];

    /// <summary>Whether a brief isn't finished until someone confirms its SharePoint copy was posted.</summary>
    public bool RequireSharePoint { get; set; }
}

/// <summary>The edition ceilings and recipient lists in force: the administered value when one is saved, else the
/// configured default.</summary>
public sealed class DistributionSettings(IAppDbContextFactory dbs, ICurrentUser user, IOptions<DistributionOptions> defaults,
    Briefs.IMeetingsInProgress? meetings = null, Distribution.EmailBoundary? boundary = null)
{
    public const string PublicMaxTlpKey = "Distribution:PublicMaxTlp";
    public const string TeamMaxTlpKey = "Distribution:TeamMaxTlp";
    public const string TeamRecipientsKey = "Distribution:TeamRecipients";
    public const string PublicRecipientsKey = "Distribution:PublicRecipients";

    /// <summary>The most addresses one edition's list may hold (a large audience is a distribution list).</summary>
    public const int MaxRecipients = 200;

    internal static readonly char[] Separators = [';', ',', ' ', '\t', '\r', '\n'];

    /// <summary>The ceilings in force. Read wherever a brief is cut (the brief page, present mode, the editor's preview,
    /// the sends): the team edition's readers, and a Visitor in a live meeting (its slides are cut by them).</summary>
    public async Task<(TlpLevel Public, TlpLevel Team)> GetAsync(CancellationToken ct = default)
    {
        if (!user.Has(Permission.ReadTeamEdition) && !Briefs.MeetingAccess.SeatedAnywhere(user, meetings)) throw new ForbiddenException("You don't have access to the edition settings.");
        return await ReadAsync(ct);
    }

    /// <summary>The ceilings in force, for a background job that has no user to check (the webhooks hold to the team
    /// edition's).</summary>
    internal async Task<(TlpLevel Public, TlpLevel Team)> ReadAsync(CancellationToken ct = default)
    {
        await using var db = await dbs.CreateAsync(ct);
        var saved = await db.Settings.AsNoTracking()
            .Where(s => s.Key == PublicMaxTlpKey || s.Key == TeamMaxTlpKey)
            .ToDictionaryAsync(s => s.Key, s => s.Value, ct);
        var pub = saved.TryGetValue(PublicMaxTlpKey, out var p) && Tlp.Parse(p) is { } pv ? pv : defaults.Value.PublicMaxTlp;
        var team = saved.TryGetValue(TeamMaxTlpKey, out var t) && Tlp.Parse(t) is { } tv ? tv : defaults.Value.TeamMaxTlp;
        // The public edition never carries more than the team one.
        return (pub > team ? team : pub, team);
    }

    public async Task SaveAsync(TlpLevel publicMax, TlpLevel teamMax, CancellationToken ct = default)
    {
        if (!user.Has(Permission.Administer)) throw new ForbiddenException("Only an administrator can change the edition ceilings.");
        Tlp.Checked(publicMax);
        Tlp.Checked(teamMax);
        if (publicMax > teamMax) throw new ArgumentException("The public edition can't carry more than the team edition.");
        await using var db = await dbs.CreateAsync(ct);
        await Upsert(db, PublicMaxTlpKey, publicMax.ToString(), ct);
        await Upsert(db, TeamMaxTlpKey, teamMax.ToString(), ct);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The addresses an edition is emailed to: read by whoever may send it (the plan, the send, the send dialog)
    /// and by administrators (who hold every permission).</summary>
    public async Task<IReadOnlyList<string>> GetRecipientsAsync(Edition edition, CancellationToken ct = default)
    {
        if (!user.Has(Permission.PresentBriefs)) throw new ForbiddenException("Only analysts can see who the brief is sent to.");
        var key = edition == Edition.Public ? PublicRecipientsKey : TeamRecipientsKey;
        await using var db = await dbs.CreateAsync(ct);
        var saved = await db.Settings.AsNoTracking().Where(s => s.Key == key).Select(s => s.Value).FirstOrDefaultAsync(ct);
        if (saved is not null) return Split(saved);
        var configured = edition == Edition.Public ? defaults.Value.PublicRecipients : defaults.Value.TeamRecipients;
        return Split(string.Join(';', configured ?? []));
    }

    /// <summary>Saves an edition's recipient list (addresses separated by commas, semicolons or lines). Every address must
    /// be a valid one; an empty list means the edition isn't emailed. An address outside the organization that the list
    /// didn't have is refused (<see cref="Distribution.OutsideAddressesException"/>, naming it) unless
    /// <paramref name="confirmOutside"/>.</summary>
    public async Task SaveRecipientsAsync(Edition edition, string? addresses, bool confirmOutside = false, CancellationToken ct = default)
    {
        if (!user.Has(Permission.Administer)) throw new ForbiddenException("Only an administrator can change who the brief is sent to.");
        var value = CheckedRecipients(addresses);
        await using var db = await dbs.CreateAsync(ct);
        if (!confirmOutside) await CheckOutsideAsync([(edition, value)], ct);
        await Upsert(db, edition == Edition.Public ? PublicRecipientsKey : TeamRecipientsKey, value, ct);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Saves both editions' recipient lists, both or neither: each is checked before either is saved, so a bad
    /// address in one list, or an outside one not confirmed, leaves the other as it was too.</summary>
    public async Task SaveRecipientsAsync(string? teamAddresses, string? publicAddresses, bool confirmOutside = false, CancellationToken ct = default)
    {
        if (!user.Has(Permission.Administer)) throw new ForbiddenException("Only an administrator can change who the brief is sent to.");
        var team = CheckedRecipients(teamAddresses);
        var pub = CheckedRecipients(publicAddresses);
        await using var db = await dbs.CreateAsync(ct);
        if (!confirmOutside) await CheckOutsideAsync([(Edition.Team, team), (Edition.Public, pub)], ct);
        await Upsert(db, TeamRecipientsKey, team, ct);
        await Upsert(db, PublicRecipientsKey, pub, ct);
        await db.SaveChangesAsync(ct);
    }

    // An edition goes to everyone on its list with all it carries: the team edition up to its ceiling (AMBER by default),
    // so an address outside the organization is one more way for it to leave. Each one the list didn't already have is
    // confirmed, naming it and what the edition carries; both lists' at once, so confirming one never passes the other's.
    private async Task CheckOutsideAsync(IReadOnlyList<(Edition Edition, string Value)> lists, CancellationToken ct)
    {
        var inside = boundary.CurrentOrUnknown();
        var added = new List<(Edition Edition, string Address)>();
        foreach (var (edition, value) in lists)
        {
            var had = await GetRecipientsAsync(edition, ct);
            added.AddRange(inside.Outside(Split(value).Except(had, StringComparer.OrdinalIgnoreCase)).Select(a => (edition, a)));
        }
        if (added.Count == 0) return;
        var (pub, team) = await ReadAsync(ct);
        string Says(Edition e) => e == Edition.Public
            ? $"The company-wide edition goes there with everything marked up to {Tlp.Label(pub)}."
            : $"The team edition goes there with everything marked up to {Tlp.Label(team)}.";
        inside.Check(added.Select(a => ((string?)a.Address, Says(a.Edition))), confirmed: false);
    }

    // A recipient list as it's stored, or an ArgumentException saying what's wrong.
    private static string CheckedRecipients(string? addresses) => CheckedAddresses(addresses,
        MaxRecipients, $"A list can hold at most {MaxRecipients} addresses; use a distribution list for more.",
        4000, "That list is too long; use a distribution list.");

    /// <summary>
    /// Addresses as they're kept ("a@x.org; b@x.org"), the brief's recipients or an outreach email's: separated by
    /// semicolons, commas, spaces or lines, each a valid address (the first few that aren't are named), repeats dropped ignoring
    /// case. More than <paramref name="maxCount"/>, or longer than <paramref name="maxLength"/> written out, is refused
    /// in the caller's words. None at all is "".
    /// </summary>
    internal static string CheckedAddresses(string? addresses, int maxCount, string tooMany, int maxLength, string tooLong)
    {
        var raw = (addresses ?? "").Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var bad = raw.Where(a => !IsAddress(a)).ToList();
        if (bad.Count > 0) throw new ArgumentException($"Not an email address: {string.Join(", ", bad.Take(3))}{(bad.Count > 3 ? "…" : "")}");
        var kept = raw.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (kept.Count > maxCount) throw new ArgumentException(tooMany);
        var value = string.Join("; ", kept);
        if (value.Length > maxLength) throw new ArgumentException(tooLong);
        return value;
    }

    private static List<string> Split(string value) => value
        .Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(IsAddress).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    internal static bool IsAddress(string a) =>
        System.Net.Mail.MailAddress.TryCreate(a, out var parsed) && parsed.Address.Equals(a, StringComparison.OrdinalIgnoreCase);

    private async Task Upsert(IAppDbContext db, string key, string value, CancellationToken ct)
    {
        var row = await db.Settings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (row is null) db.Settings.Add(row = new AppSetting { Key = key });
        row.Value = value;
        row.UpdatedAtUtc = DateTimeOffset.UtcNow;
        row.UpdatedBy = user.UserId;
    }
}
