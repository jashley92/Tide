using Tide.Application.Articles;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.Application.Briefs;

/// <summary>The five steps of a day's brief, in order.</summary>
public enum BriefStepKind { Triage, Write, Order, Present, Send }

/// <summary>Where a step stands. <see cref="Current"/> is the first one still to do (Triage never is: the feed is
/// never finished); <see cref="Attention"/> is one that was passed with something left behind.</summary>
public enum StepState { Todo, Current, Done, Attention }

/// <summary>One step of the brief: what it is, a few words on where it stands ("2 of 5 need work"), and its state.</summary>
public sealed record BriefStep(BriefStepKind Kind, string Label, string Detail, StepState State);

/// <summary>How far the day's brief has got: the step strip at the top of Today (Triage · Write · Order · Present ·
/// Send), built from counts the page already has.</summary>
public static class BriefSteps
{
    /// <summary>
    /// The steps for <paramref name="brief"/> (its items and their articles loaded). <paramref name="newInFeed"/> is
    /// the feed's new items, or null for someone who doesn't triage; <paramref name="missingNumbers"/> the day's
    /// numbers not entered yet; <paramref name="meetsAt"/> the meeting time as people read it ("9:00 AM");
    /// <paramref name="sends"/> the day's send log, when it has been read, for where the Send step stands (<see cref="Send"/>);
    /// <paramref name="nothingToSend"/> the editions with nothing in them to send; <paramref name="sharePointRequired"/>
    /// whether the brief isn't finished until its SharePoint copy is confirmed posted.
    /// </summary>
    public static IReadOnlyList<BriefStep> For(Brief brief, int? newInFeed, int missingNumbers, string meetsAt,
        IReadOnlyList<BriefSend>? sends = null, IReadOnlyCollection<Edition>? nothingToSend = null, bool sharePointRequired = false)
    {
        var articles = brief.Items.Where(i => i.Article is not null).ToList();
        var total = articles.Count;
        var needWork = articles.Count(i => ArticleChecks.For(i.Article!).Any(c => c.Blocking));
        var marked = brief.Status != BriefStatus.Draft;
        var met = brief.Status is BriefStatus.Presented or BriefStatus.Sent;
        var numbers = missingNumbers > 0 ? $" · {Numbers(missingNumbers)} missing" : "";

        var steps = new List<BriefStep>
        {
            newInFeed switch
            {
                null => new(BriefStepKind.Triage, "Triage", "Sorting the feed", StepState.Todo),
                > 0 => new(BriefStepKind.Triage, "Triage", $"{newInFeed} new in the feed", StepState.Attention),
                _ => new(BriefStepKind.Triage, "Triage", "Feed clear", StepState.Done),
            },
            (total, needWork) switch
            {
                (0, _) => new(BriefStepKind.Write, "Write", "No articles yet", StepState.Todo),
                (_, > 0) => new(BriefStepKind.Write, "Write", $"{needWork} of {total} need work", marked ? StepState.Attention : StepState.Todo),
                _ => new(BriefStepKind.Write, "Write", $"{total} of {total} ready", StepState.Done),
            },
            marked
                ? new(BriefStepKind.Order, "Order", "Marked ready" + numbers, missingNumbers > 0 && !met ? StepState.Attention : StepState.Done)
                : new(BriefStepKind.Order, "Order", "Not marked ready" + numbers, StepState.Todo),
            met
                ? new(BriefStepKind.Present, "Present", $"{brief.Items.Count(i => i.Discussed)} of {total} discussed", StepState.Done)
                : new(BriefStepKind.Present, "Present", $"Meets {meetsAt}", StepState.Todo),
            Send(brief, sends, nothingToSend, sharePointRequired),
        };
        var current = steps.FindIndex(s => s.Kind != BriefStepKind.Triage && s.State == StepState.Todo);
        if (current >= 0) steps[current] = steps[current] with { State = StepState.Current };
        return steps;
    }

    /// <summary>
    /// The Send step from the send log: "Sent" once both editions have gone (or the brief is Sent and the log isn't
    /// known), "1 of 2 sent" while one is left, and the latest real send's trouble when it failed or may have gone
    /// (Attention) or is still with the relay; one left "sending" past the in-flight time may have gone. An edition in
    /// <paramref name="nothingToSend"/> (every article above its ceiling) can't go, so it isn't waited for. Before any
    /// real send, "Test sent" once a test copy went, else "2 editions". A brief marked distributed without email is
    /// "Distributed", and one finished without sending while email was on "Not sent". Where <paramref name="sharePointRequired"/>, a sent brief whose SharePoint copy isn't confirmed
    /// posted is still to do ("Sent · SharePoint to post").
    /// </summary>
    public static BriefStep Send(Brief brief, IReadOnlyList<BriefSend>? sends, IReadOnlyCollection<Edition>? nothingToSend = null,
        bool sharePointRequired = false)
    {
        // Marked distributed without email, or finished as not sent, then emailed after all: that's sent.
        var emailed = sends?.Any(s => !s.IsTest && s.Status == SendStatus.Sent) == true;
        var done = emailed ? "Sent" : brief.NotSentAtUtc is not null ? "Not sent" : brief.DistributedAtUtc is not null ? "Distributed" : "Sent";
        if (sends is null)
            return brief.Status == BriefStatus.Sent
                ? Finished(brief, done, sharePointRequired)
                : new(BriefStepKind.Send, "Send", "2 editions", StepState.Todo);
        var real = sends.Where(s => !s.IsTest).ToList();
        var editions = real.Where(s => s.Status == SendStatus.Sent).Select(s => s.Edition).Distinct().ToList();
        var went = editions.Count;
        var empty = nothingToSend?.Where(e => !editions.Contains(e)).Distinct().Count() ?? 0;
        if (went >= 2 || (went == 0 && brief.Status == BriefStatus.Sent))
            return Finished(brief, done, sharePointRequired);
        if (went == 1 && empty >= 1)
            return Finished(brief, "Sent · nothing in the other edition", sharePointRequired);
        return (real.MaxBy(s => s.SentAtUtc) is { } latest ? Distribution.SendOutcome.Seen(latest.Status, latest.SentAtUtc) : (SendStatus?)null) switch
        {
            SendStatus.Failed => new(BriefStepKind.Send, "Send", "Last send failed", StepState.Attention),
            SendStatus.Unknown => new(BriefStepKind.Send, "Send", "May have gone: check", StepState.Attention),
            SendStatus.Sending => new(BriefStepKind.Send, "Send", "Sending…", StepState.Todo),
            _ when went == 1 => new(BriefStepKind.Send, "Send", "1 of 2 sent", StepState.Todo),
            _ when sends.Any(s => s.IsTest && s.Status == SendStatus.Sent) => new(BriefStepKind.Send, "Send", "Test sent · 2 editions", StepState.Todo),
            _ => new(BriefStepKind.Send, "Send", "2 editions", StepState.Todo),
        };
    }

    // Gone out: done, unless its SharePoint copy is required and not confirmed posted yet.
    private static BriefStep Finished(Brief brief, string detail, bool sharePointRequired) =>
        SharePointDue(brief, sharePointRequired)
            ? new(BriefStepKind.Send, "Send", detail + " · SharePoint to post", StepState.Todo)
            : new(BriefStepKind.Send, "Send", detail, StepState.Done);

    /// <summary>Whether the brief has gone out but still waits for its SharePoint copy to be confirmed posted.</summary>
    public static bool SharePointDue(Brief brief, bool sharePointRequired) =>
        sharePointRequired && brief.SharePointRequired && brief.Status == BriefStatus.Sent && brief.SharePointPostedAtUtc is null;

    /// <summary>A brief's status as people read it: "Distributed" for one finished without email, "Not sent" for one
    /// finished without sending while email was on (and not emailed since), and "SharePoint to post" for one gone out
    /// whose SharePoint copy is required (now, and when it went out) and not confirmed yet.</summary>
    public static string StatusLabel(BriefStatus status, bool distributed, bool sharePointPosted, bool sharePointRequired, bool notSent = false) => status switch
    {
        BriefStatus.Sent when sharePointRequired && !sharePointPosted => "SharePoint to post",
        BriefStatus.Sent when notSent => "Not sent",
        BriefStatus.Sent when distributed => "Distributed",
        _ => status.ToString(),
    };

    /// <summary>"1 number" or "3 numbers".</summary>
    public static string Numbers(int count) => count == 1 ? "1 number" : $"{count} numbers";

    /// <summary>
    /// The one thing to do next with a brief, the page header's primary button: mark it ready while it's a draft with
    /// articles on it, present it once ready, send it once presented, and read it as sent after. A draft with nothing on
    /// it has none. Sent with its SharePoint copy still to confirm (<paramref name="sharePointDue"/>), it's posting it.
    /// </summary>
    public static BriefNext Next(BriefStatus status, int articles, bool sharePointDue = false) => status switch
    {
        BriefStatus.Draft => articles > 0 ? BriefNext.MarkReady : BriefNext.None,
        BriefStatus.Ready => BriefNext.Present,
        BriefStatus.Presented => BriefNext.Send,
        BriefStatus.Sent => sharePointDue ? BriefNext.PostToSharePoint : BriefNext.ViewAsSent,
        _ => BriefNext.None,
    };
}

/// <summary>A brief's next step, as the header's primary button offers it (<see cref="BriefSteps.Next"/>).</summary>
public enum BriefNext { None, MarkReady, Present, Send, ViewAsSent, PostToSharePoint }

/// <summary>How much of something an edition carries.</summary>
public enum EditionFit { Included, Part, LeftOut }

/// <summary>One line of the edition check: an article (or the opening note, or the numbers), how much of it the edition
/// carries, and why when not all of it. <see cref="ArticleId"/> is null for the note and the numbers. <see cref="Warning"/>
/// is a readiness warning about what the edition carries (<see cref="ArticleChecks.WhyReachesCompany"/>).</summary>
public sealed record EditionCheckRow(Guid? ArticleId, string Title, EditionFit Fit, string? Why = null, string? Warning = null);

/// <summary>
/// What each edition will carry of the brief, as it stands: for each article, whether it goes in whole, in part, or not
/// at all. It is the edition's own cut (<see cref="BriefEditions.BuildArticle"/>), so it can't disagree with what's sent.
/// </summary>
public static class EditionChecks
{
    /// <summary>
    /// The rows for <paramref name="edition"/> under <paramref name="ceiling"/>: the opening note (when there is one),
    /// the day's numbers (when any are kept; <paramref name="missingNumbers"/> of them not entered), then each article in
    /// the running order. On the company-wide edition, an article whose "Why it matters" it carries is warned about.
    /// </summary>
    public static IReadOnlyList<EditionCheckRow> Rows(Brief brief, Edition edition, TlpLevel ceiling, bool hasNumbers = false, int missingNumbers = 0)
    {
        var rows = new List<EditionCheckRow>();
        if (!string.IsNullOrWhiteSpace(brief.Intro))
            rows.Add(brief.IntroTlp <= ceiling
                ? new(null, "Opening note", EditionFit.Included)
                : new(null, "Opening note", EditionFit.LeftOut, $"The note is {Tlp.Label(brief.IntroTlp)}"));
        if (hasNumbers)
            rows.Add((edition, missingNumbers) switch
            {
                (not Edition.Team, _) => new(null, "Today's numbers", EditionFit.LeftOut, "Never in this edition"),
                (_, > 0) => new(null, "Today's numbers", EditionFit.Part, $"{BriefSteps.Numbers(missingNumbers)} not entered"),
                _ => new(null, "Today's numbers", EditionFit.Included),
            });
        foreach (var item in brief.Items.OrderBy(i => i.SortOrder))
        {
            if (item.Article is not { } a) continue;
            var warning = edition == Edition.Public ? ArticleChecks.WhyReachesCompany(a, ceiling)?.Message : null;
            rows.Add(BriefEditions.BuildArticle(a, edition, ceiling) switch
            {
                null => new(a.Id, a.Title, EditionFit.LeftOut, $"The article is {Tlp.Label(a.Tlp)}"),
                { WithheldParts: > 0 } cut => new(a.Id, a.Title, EditionFit.Part,
                    $"{cut.WithheldParts} part{(cut.WithheldParts == 1 ? "" : "s")} above {Tlp.Label(ceiling)} left out", warning),
                _ => new(a.Id, a.Title, EditionFit.Included, Warning: warning),
            });
        }
        return rows;
    }
}

/// <summary>The running order's moves that aren't one step up or down.</summary>
public static class BriefOrder
{
    /// <summary>
    /// <paramref name="ids"/> with <paramref name="id"/> moved next to <paramref name="target"/>: just after it when
    /// <paramref name="after"/>, otherwise just before. Unchanged when either isn't in the list or they're the same.
    /// </summary>
    public static IReadOnlyList<Guid> Dropped(IReadOnlyList<Guid> ids, Guid id, Guid target, bool after)
    {
        if (id == target || !ids.Contains(id) || !ids.Contains(target)) return ids;
        var list = ids.Where(x => x != id).ToList();
        list.Insert(list.IndexOf(target) + (after ? 1 : 0), id);
        return list;
    }
}
