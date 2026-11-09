using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tide.Application.Abstractions;
using Tide.Application.Actions;
using Tide.Application.Distribution;
using Tide.Application.Org;
using Tide.Application.Security;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.Application.Api;

/// <summary>Keeps a secret TIDE must read back (a webhook's signing secret) encrypted at rest.</summary>
public interface ISecretProtector
{
    string Protect(string secret);

    /// <summary>The secret, or null when it can't be read (the keys changed).</summary>
    string? Unprotect(string protectedSecret);

    /// <summary>The same under a purpose of its own (a secret saved in the app): a value protected for one purpose never
    /// opens under another.</summary>
    string Protect(string purpose, string secret);

    string? Unprotect(string purpose, string protectedSecret);
}

/// <summary>Where nothing has set up Data Protection (a tool, a test that builds the services alone): nothing opens, and
/// protecting refuses, so a secret is never stored unprotected. The web app registers the real one.</summary>
public sealed class NoSecretProtector : ISecretProtector
{
    private const string Why = "Secrets can't be encrypted here: Data Protection isn't set up.";

    public string Protect(string secret) => throw new InvalidOperationException(Why);

    public string? Unprotect(string protectedSecret) => null;

    public string Protect(string purpose, string secret) => throw new InvalidOperationException(Why);

    public string? Unprotect(string purpose, string protectedSecret) => null;
}

/// <summary>Posts a webhook's JSON body; the status code, or why it failed.</summary>
public interface IWebhookPoster
{
    Task<(int? Status, string? Error)> PostAsync(string url, string body, IReadOnlyDictionary<string, string> headers, CancellationToken ct = default);
}

/// <summary>A webhook as the API page lists it: never its secret.</summary>
public sealed record WebhookRow(Guid Id, string Url, IReadOnlyList<string> Events, bool Enabled, DateTimeOffset? LastAtUtc, int? LastStatus, string? LastError);

/// <summary>A webhook just added: the only time its signing secret is shown.</summary>
public sealed record AddedWebhook(Guid Id, string Secret);

/// <summary>
/// Webhooks (EP-75): TIDE posts to an administrator's address when an action is raised, when one becomes overdue, and
/// when a brief is sent. Each post is signed: <c>X-Tide-Signature: sha256=</c> the HMAC-SHA256, with the hook's secret,
/// of the <c>X-Tide-Timestamp</c> value, a dot, and the body. The background job sends what happened since the last
/// run, once it's at least <see cref="SettleDelay"/> old; a failed post is tried again on the next, and the page shows the last result. An action is held at its article's
/// TLP, as the team edition holds it: one raised from an article above the team edition's ceiling is never posted, and
/// each action posted carries its article's marking.
/// </summary>
public sealed class WebhookService(IAppDbContextFactory dbs, ICurrentUser user, ISecretProtector secrets, IWebhookPoster poster, OrgClock clock,
    IOptions<AppOptions> app, TimeProvider time, Settings.DistributionSettings editions)
{
    public const string ActionRaised = "action.raised";
    public const string ActionOverdue = "action.overdue";
    public const string BriefSent = "brief.sent";
    public static readonly IReadOnlyList<string> Events = [ActionRaised, ActionOverdue, BriefSent];
    public const int MaxPerRun = 100;

    /// <summary>How old an action raised or a brief sent must be before a run posts it.</summary>
    public static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(30);

    public async Task<IReadOnlyList<WebhookRow>> ListAsync(CancellationToken ct = default)
    {
        RequireAdmin();
        await using var db = await dbs.CreateAsync(ct);
        return (await db.Webhooks.AsNoTracking().OrderBy(w => w.Url).ToListAsync(ct))
            .Select(w => new WebhookRow(w.Id, w.Url, EventsOf(w.Events), w.Enabled, w.LastAtUtc, w.LastStatus, w.LastError)).ToList();
    }

    public async Task<AddedWebhook> AddAsync(string url, IReadOnlyCollection<string> events, CancellationToken ct = default)
    {
        RequireAdmin();
        if (!Uri.TryCreate((url ?? "").Trim(), UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps || u.ToString().Length > 2000)
            throw new ArgumentException("A webhook needs an https address: what it's sent can include action titles.");
        var chosen = Events.Where(events.Contains).ToList();
        if (chosen.Count == 0) throw new ArgumentException("Choose at least one event.");
        var secret = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        await using var db = await dbs.CreateAsync(ct);
        var hook = new Webhook { Url = u.ToString(), Events = string.Join(' ', chosen), Secret = secrets.Protect(secret), SentUpToUtc = time.GetUtcNow() };
        db.Webhooks.Add(hook);
        await db.SaveChangesAsync(ct);
        return new AddedWebhook(hook.Id, secret);
    }

    public async Task SetEnabledAsync(Guid id, bool enabled, CancellationToken ct = default)
    {
        RequireAdmin();
        await using var db = await dbs.CreateAsync(ct);
        var hook = await db.Webhooks.FirstOrDefaultAsync(w => w.Id == id, ct) ?? throw new KeyNotFoundException("That webhook no longer exists.");
        hook.Enabled = enabled;
        // Switched back on, it starts from now: what happened while it was off isn't sent in a burst.
        if (enabled)
        {
            hook.SentUpToUtc = time.GetUtcNow();
            hook.OverdueSentFor = clock.Today.AddDays(-1);
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task RemoveAsync(Guid id, CancellationToken ct = default)
    {
        RequireAdmin();
        await using var db = await dbs.CreateAsync(ct);
        var hook = await db.Webhooks.FirstOrDefaultAsync(w => w.Id == id, ct) ?? throw new KeyNotFoundException("That webhook no longer exists.");
        db.Webhooks.Remove(hook);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Sends a "ping" to the hook now, to check the address and the signature; the result is shown on the page.</summary>
    public async Task<(int? Status, string? Error)> TestAsync(Guid id, CancellationToken ct = default)
    {
        RequireAdmin();
        await using var db = await dbs.CreateAsync(ct);
        var hook = await db.Webhooks.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id, ct) ?? throw new KeyNotFoundException("That webhook no longer exists.");
        var result = await PostAsync(hook, "ping", $"ping:{Guid.NewGuid()}", new { message = "A test from TIDE." }, ct);
        Record(hook, result);
        await SaveAsync(db, hook, null, ct);
        return result;
    }

    /// <summary>
    /// The background job: for each enabled hook, posts what happened since its last run (actions raised, briefs sent)
    /// and, once a day, the actions that became overdue. A hook stops at its first failure and tries again next run.
    /// Returns how many posts were made.
    /// </summary>
    public async Task<int> DeliverDueAsync(CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        var today = clock.Today;
        await using var db = await dbs.CreateAsync(ct);
        var hooks = await db.Webhooks.AsNoTracking().Where(w => w.Enabled).ToListAsync(ct);
        if (hooks.Count == 0) return 0;
        // A receiver is at least as wide as the team edition's list (a SOAR, a ticket queue), so it gets what that edition
        // would: an action raised from an article above its ceiling is left out, as the brief leaves the article out with
        // its actions. Left out of the query, it's passed by the watermark like anything already sent.
        var (_, ceiling) = await editions.ReadAsync(ct);
        var posted = 0;
        foreach (var hook in hooks)
        {
            var read = (hook.SentUpToUtc, hook.OverdueSentFor);
            var events = EventsOf(hook.Events);
            async Task<bool> Post(string name, string id, object data)
            {
                var result = await PostAsync(hook, name, id, data, ct);
                Record(hook, result);
                posted++;
                return result.Status is >= 200 and < 300;
            }

            // Only what's at least SettleDelay old: a save stamped just before the run may not have committed when it reads,
            // and the watermark would pass it. A brief's send is stamped before it goes to the relay and marked Sent once it
            // has, so the watermark also stops short of one still going (unless it's plainly stuck: SendOutcome.InFlight)
            // and it's posted when it's Sent.
            var upTo = now - SettleDelay;
            if (events.Contains(BriefSent))
            {
                var stuck = now - SendOutcome.InFlight;
                var going = await db.BriefSends.AsNoTracking()
                    .Where(s => !s.IsTest && s.Status == SendStatus.Sending && s.SentAtUtc > hook.SentUpToUtc && s.SentAtUtc > stuck)
                    .Select(s => s.SentAtUtc).ToListAsync(ct);
                if (going.Count > 0 && going.Min().AddTicks(-1) < upTo) upTo = going.Min().AddTicks(-1);
            }
            var due = new List<(string Event, string Id, object Data, DateTimeOffset At)>();
            if (events.Contains(ActionRaised))
                due.AddRange((await db.Actions.AsNoTracking().Include(a => a.Team).Include(a => a.Article)
                    .Where(a => a.CreatedAtUtc > hook.SentUpToUtc && a.CreatedAtUtc <= upTo && (a.Article == null || a.Article.Tlp <= ceiling))
                    .ToListAsync(ct)).Select(a => (ActionRaised, $"{ActionRaised}:{a.Id}", Action(a), a.CreatedAtUtc)));
            if (events.Contains(BriefSent))
            {
                due.AddRange((await db.BriefSends.AsNoTracking().Where(s => !s.IsTest && s.Status == SendStatus.Sent && s.SentAtUtc > hook.SentUpToUtc && s.SentAtUtc <= upTo)
                    .ToListAsync(ct)).Select(s => (BriefSent, $"{BriefSent}:{s.Id}", Sent(s), s.SentAtUtc)));
                due.AddRange(await MarkedSentAsync(db, hook.SentUpToUtc, upTo, ct));
            }

            // Oldest first, the watermark following each post, so a backlog drains over the runs. At most MaxPerRun a run,
            // but never stopping inside events stamped at the same moment: the watermark can't tell them apart.
            var ordered = due.OrderBy(d => d.At).ToList();
            var take = Math.Min(MaxPerRun, ordered.Count);
            while (take < ordered.Count && ordered[take].At == ordered[take - 1].At) take++;
            var ok = true;
            for (var i = 0; i < take && ok; i++)
            {
                ok = await Post(ordered[i].Event, ordered[i].Id, ordered[i].Data);
                if (ok && (i + 1 == ordered.Count || ordered[i + 1].At != ordered[i].At)) hook.SentUpToUtc = ordered[i].At;
            }
            if (ok && take == ordered.Count && upTo > hook.SentUpToUtc) hook.SentUpToUtc = upTo;

            // Then the newly overdue, a due date at a time, so a backlog of those drains too.
            if (ok && events.Contains(ActionOverdue) && hook.OverdueSentFor != today.AddDays(-1))
            {
                var overdueFrom = hook.OverdueSentFor?.AddDays(1) ?? today.AddDays(-1);
                var days = (await db.Actions.AsNoTracking().Include(a => a.Team).Include(a => a.Article)
                    .WhereOpen().Where(a => a.DueDate >= overdueFrom && a.DueDate < today && (a.Article == null || a.Article.Tlp <= ceiling))
                    .ToListAsync(ct)).OrderBy(a => a.DueDate).ThenBy(a => a.Number).GroupBy(a => a.DueDate).ToList();
                var left = MaxPerRun;
                var d = 0;
                for (; d < days.Count && ok && left > 0; d++)
                {
                    foreach (var a in days[d])
                        if (!(ok = await Post(ActionOverdue, OverdueId(a), Action(a)))) break;
                    if (ok) hook.OverdueSentFor = days[d].Key;
                    left -= days[d].Count();
                }
                if (ok && d == days.Count) hook.OverdueSentFor = today.AddDays(-1);
            }
            await SaveAsync(db, hook, read, ct);
        }
        return posted;
    }

    // A send marked Sent after the watermark passed its time: settled once someone asked a recipient (it may have gone),
    // or its outcome saved after the in-flight time. It's posted as of when it was marked (its OutcomeAtUtc). Should one
    // already have been posted (a run that stopped between its time and its marking), the repeat keeps its id, so a
    // receiver drops it.
    private static async Task<IEnumerable<(string, string, object, DateTimeOffset)>> MarkedSentAsync(IAppDbContext db, DateTimeOffset after,
        DateTimeOffset upTo, CancellationToken ct)
    {
        var sends = await db.BriefSends.AsNoTracking()
            .Where(s => s.OutcomeAtUtc > after && s.OutcomeAtUtc <= upTo && !s.IsTest && s.Status == SendStatus.Sent && s.SentAtUtc <= after)
            .ToListAsync(ct);
        return sends.Select(s => (BriefSent, $"{BriefSent}:{s.Id}", Sent(s), s.OutcomeAtUtc!.Value));
    }

    /// <summary>The signature a receiver checks: HMAC-SHA256 of "<paramref name="timestamp"/>.<paramref name="body"/>" with the secret.</summary>
    public static string Sign(string secret, string timestamp, string body) =>
        "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(timestamp + "." + body)));

    // The id is the same each time an event is posted again (after a failure), so a receiver can drop a repeat.
    private async Task<(int? Status, string? Error)> PostAsync(Webhook hook, string name, string id, object data, CancellationToken ct)
    {
        if (secrets.Unprotect(hook.Secret) is not { } secret) return (null, "The signing secret can't be read (the server's keys changed). Add the webhook again.");
        var body = JsonSerializer.Serialize(new { id, @event = name, sentAtUtc = time.GetUtcNow(), data }, Json);
        var timestamp = time.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var headers = new Dictionary<string, string>
        {
            ["X-Tide-Event"] = name,
            ["X-Tide-Event-Id"] = id,
            ["X-Tide-Timestamp"] = timestamp,
            ["X-Tide-Signature"] = Sign(secret, timestamp, body),
        };
        try { return await poster.PostAsync(hook.Url, body, headers, ct); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return (null, ex.Message); }
    }

    // Written past the audit trail, as a token's call count is: a delivery isn't a change to the webhook, and a row a
    // minute would bury the real ones. A hook removed meanwhile is simply not updated. A delivery moves the watermarks
    // only while they're still what the run <paramref name="read"/>: a hook switched off and on during the run restarted
    // them from then (SetEnabledAsync), and the run's older ones would bring back the burst that skips. Its last result
    // is recorded either way.
    private static async Task SaveAsync(IAppDbContext db, Webhook hook, (DateTimeOffset SentUpTo, DateOnly? OverdueFor)? read, CancellationToken ct)
    {
        if (read is { } r && await db.Webhooks.Where(w => w.Id == hook.Id && w.SentUpToUtc == r.SentUpTo && w.OverdueSentFor == r.OverdueFor)
                .ExecuteUpdateAsync(u => u.SetProperty(w => w.LastAtUtc, hook.LastAtUtc).SetProperty(w => w.LastStatus, hook.LastStatus)
                    .SetProperty(w => w.LastError, hook.LastError).SetProperty(w => w.SentUpToUtc, hook.SentUpToUtc)
                    .SetProperty(w => w.OverdueSentFor, hook.OverdueSentFor), ct) > 0)
            return;
        await db.Webhooks.Where(w => w.Id == hook.Id).ExecuteUpdateAsync(u =>
            u.SetProperty(w => w.LastAtUtc, hook.LastAtUtc).SetProperty(w => w.LastStatus, hook.LastStatus).SetProperty(w => w.LastError, hook.LastError), ct);
    }

    private static string OverdueId(ActionItem a) =>
        $"{ActionOverdue}:{a.Id}:{a.DueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";

    private void Record(Webhook hook, (int? Status, string? Error) result)
    {
        hook.LastAtUtc = time.GetUtcNow();
        hook.LastStatus = result.Status;
        var error = result.Status is >= 200 and < 300 ? null : result.Error ?? $"The address answered {result.Status}.";
        hook.LastError = error is { Length: > 500 } ? error[..500] : error;
    }

    // marking: the TLP of the article the action was raised from (null when none), as the reminder emails mark their lists.
    private object Action(ActionItem a) => new
    {
        number = a.Number, label = $"A-{a.Number}", a.Title, priority = a.Priority.ToString(), status = a.Status.ToString(), team = a.Team?.Name,
        dueDate = a.DueDate, url = Link($"actions/{a.Number}"), marking = a.Article is { } article ? Tlp.Label(article.Tlp) : null,
    };

    private static object Sent(BriefSend s) => new { briefDate = s.BriefDate, edition = s.Edition.ToString(), s.Subject, marking = Tlp.Label(s.Marking), sentAtUtc = s.SentAtUtc };

    private string Link(string path) => app.Value.BaseUrl.TrimEnd('/') + "/" + path;

    private static IReadOnlyList<string> EventsOf(string events) => Events.Where(e => events.Split(' ').Contains(e)).ToList();

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private void RequireAdmin()
    {
        if (!user.Has(Permission.Administer)) throw new ForbiddenException("Only an administrator manages API access.");
    }
}
