using System.Globalization;

namespace Tide.Application.Abstractions;

/// <summary>
/// One thing that changed: the topic ("article:{id}"), who changed it, and the circuit (or other scope) it came from, so
/// a page can tell a colleague's save from its own.
/// </summary>
public sealed record Change(string Topic, string ActorId, string ActorName, Guid Origin);

/// <summary>
/// Tells open pages that something they show changed elsewhere (a colleague's save, another analyst's triage, a poll that
/// brought items), so a shared page stays current without a reload. One per app (TIDE runs as one process, ADR 0001).
/// Saves are published by the database's change interceptor, so every save path is heard; a page subscribes and reloads,
/// after a short pause so a burst is one reload.
/// </summary>
public sealed class ChangeFeed
{
    /// <summary>The triage inbox: items came in, or someone triaged one.</summary>
    public const string Triage = "triage";

    /// <summary>The brief rotation: a week, a member, a day's override, or a swap or cover asked for or decided.</summary>
    public const string Rotation = "rotation";

    /// <summary>Any article: the articles list, and the brief's Add article count and picker.</summary>
    public const string Articles = "articles";

    /// <summary>Any story: the stories list.</summary>
    public const string Stories = "stories";

    /// <summary>Any action: the actions list and board.</summary>
    public const string Actions = "actions";

    /// <summary>The metrics kept (an administrator added, renamed, moved or retired a metric or a group): every day's numbers.</summary>
    public const string Metrics = "metrics";

    /// <summary>Our stack's products: one added, edited or retired (present mode's "We run" chips and product cards).</summary>
    public const string Stack = "stack";

    /// <summary>The team's saved hunt queries: one saved, edited, retired, restored or deleted.</summary>
    public const string SavedQueries = "saved-queries";

    /// <summary>The team's outreach email templates: one saved, edited, retired, restored or deleted.</summary>
    public const string OutreachTemplates = "outreach-templates";

    /// <summary>The team's playbooks: one saved, edited, moved, retired, restored or deleted.</summary>
    public const string Playbooks = "playbooks";

    public static string Article(Guid id) => $"article:{id}";

    /// <summary>An article's adversary and product tags, which the editor's We run and Threat actors pickers show. Apart from the article's
    /// own topic: a tag doesn't move the article's edit version, so its editor, brief and Present mode needn't hear it.</summary>
    public static string ArticleTags(Guid id) => $"article-tags:{id}";

    public static string Brief(Guid id) => $"brief:{id}";

    /// <summary>A brief's meeting: who attended, and the notes taken.</summary>
    public static string Meeting(Guid briefId) => $"meeting:{briefId}";

    /// <summary>The brief for a day, for a page showing a day whose brief may not exist yet.</summary>
    public static string BriefDay(DateOnly day) => $"brief-day:{day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";

    /// <summary>A day's numbers: a value or its note was entered, changed or cleared.</summary>
    public static string MetricsDay(DateOnly day) => $"metrics-day:{day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";

    /// <summary>Whether <paramref name="change"/> is to the numbers shown for <paramref name="day"/>.</summary>
    public static bool IsMetricsOf(Change change, DateOnly day) =>
        change.Topic == Metrics || change.Topic == MetricsDay(day);

    public static string Action(Guid id) => $"action:{id}";

    /// <summary>Whether <paramref name="change"/> is to an action: any (the lists), or one.</summary>
    public static bool IsAction(Change change) =>
        change.Topic == Actions || change.Topic.StartsWith("action:", StringComparison.Ordinal);

    /// <summary>Raised with what changed. Handlers run on the publisher's thread: marshal to the circuit.</summary>
    public event Action<Change>? Changed;

    /// <summary>Publishes a change no one in particular made (a poll, a page's own announcement).</summary>
    public void Publish(string topic) => Publish(new Change(topic, "system", "", Guid.Empty));

    /// <summary>Opened around each handler call: the host clears what's ambient to the publisher's work (its signed-in
    /// user, for the access log), so a subscriber's continuation doesn't run as the publisher.</summary>
    public static Func<IDisposable?>? AroundHandler { get; set; }

    private long _version;

    /// <summary>How many changes have been published since the app started: unchanged between two reads, nothing that any
    /// page follows was saved in between (a prerendered page's state is then still current, <c>PrerenderHandoff</c>).</summary>
    public long Version => Interlocked.Read(ref _version);

    public void Publish(Change change)
    {
        Interlocked.Increment(ref _version);
        var handlers = Changed;
        if (handlers is null) return;
        // Each subscriber in turn, on the publisher's thread (another circuit, or the poller): one that throws (a page
        // closing at that moment) mustn't fail the publisher's own action or keep the others from hearing.
        foreach (var h in handlers.GetInvocationList().Cast<Action<Change>>())
        {
            try
            {
                using (AroundHandler?.Invoke()) h(change);
            }
            catch (Exception) { /* that subscriber is gone or broken; the rest still hear */ }
        }
    }
}

/// <summary>
/// Where a change came from: one per DI scope, so per Blazor circuit (and per background job). A page ignores changes
/// from its own origin, which its own save already shows.
/// </summary>
public sealed class ChangeOrigin
{
    public Guid Id { get; } = Guid.NewGuid();
}
