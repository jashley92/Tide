using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Tide.Application.Abstractions;
using Tide.Application.Actions;
using Tide.Application.Articles;
using Tide.Application.Content;
using Tide.Application.Mitre;
using Tide.Application.Org;
using Tide.Application.Security;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.Application.Trends;

/// <summary>
/// One line of a trend table: what it counts (<see cref="Key"/>, e.g. "tag:ransomware"), and the articles or actions it
/// counted in each period, oldest first. Each article or action counts once per period on a line, so a line that sums
/// others (a scorecard category, a tactic) counts an article on two of them once. A roll-up line heads the lines
/// under it.
/// </summary>
public sealed record TrendRow(string Key, string Name, string Group, IReadOnlyList<IReadOnlyList<Guid>> Ids, bool IsRollup = false)
{
    public int Count(int period) => Ids[period].Count;

    public int Current => Ids[^1].Count;

    public int Previous => Ids.Count > 1 ? Ids[^2].Count : 0;

    public int Change => Current - Previous;

    /// <summary>The change as a fraction of the previous period's count; null when that was zero.</summary>
    public double? ChangeRatio => Previous == 0 ? null : (double)Change / Previous;

    public bool Any => Ids.Any(p => p.Count > 0);
}

/// <summary>
/// Tags, ATT&amp;CK and actions over consecutive months or quarters, ending with <see cref="Current"/>. Article lines
/// count articles by <see cref="CountedOn"/>; action lines count actions.
/// </summary>
public sealed record TrendReport(
    IReadOnlyList<Period> Periods,
    bool RollUpSubtechniques,
    IReadOnlyList<TrendRow> Articles,
    IReadOnlyList<TrendRow> Tags,
    IReadOnlyList<TrendRow> Attack,
    IReadOnlyList<TrendRow> TacticTechniques,
    IReadOnlyList<TrendRow> Actions,
    IReadOnlyDictionary<Guid, DateOnly> CountedOn)
{
    /// <summary>The median days to close P1 and P2 actions, this period and the last.</summary>
    public IReadOnlyList<TrendOutcome> Outcomes { get; init; } = [];

    public Period Current => Periods[^1];

    public IEnumerable<TrendRow> AllRows => Articles.Concat(Tags).Concat(Attack).Concat(TacticTechniques).Concat(Actions);

    /// <summary>The techniques seen under one tactic (its <c>tactic:</c> line's key), most counted first.</summary>
    public IEnumerable<TrendRow> TechniquesOf(string tacticKey) => TacticTechniques.Where(r => r.Key.StartsWith(tacticKey + ":", StringComparison.Ordinal));

    public TrendRow? Find(string? key)
    {
        // A link to a kind's line from before a rename names it by its old name ("VendorInquiry" for Outreach), exactly as
        // the line's key was written then.
        if (key?.LastIndexOf(RaisedKind, StringComparison.Ordinal) is >= 0 and var at
            && ActionService.FormerKind(key[(at + RaisedKind.Length)..], StringComparison.Ordinal) is { } kind)
            key = key[..(at + RaisedKind.Length)] + kind;
        return AllRows.FirstOrDefault(r => r.Key == key);
    }

    /// <summary>What an action line's key says before the kind ("actions:raised-kind:Block").</summary>
    internal const string RaisedKind = "raised-kind:";

    /// <summary>Whether a line counts actions (its ids open as actions) rather than articles.</summary>
    public bool IsActionRow(TrendRow row) => Actions.Contains(row);

    /// <summary>
    /// The article list's filter for exactly the articles a line counted in one period (<paramref name="at"/>), or in
    /// all of them; null for an action line, or a line the list can't narrow to.
    /// </summary>
    public ArticleFilter? ArticlesFilter(TrendRow row, Period? at = null) =>
        IsActionRow(row) ? null
        : TrendsService.FilterFor(row.Key, RollUpSubtechniques) is { } f
            ? f with { From = (at ?? Periods[0]).Start, To = (at ?? Current).End }
            : null;

    /// <summary>Done on time as a share of what was done with a due date in the current period (an action with no due
    /// date is neither, as Workload's "on time" reads it); null when none was.</summary>
    public double? OnTimeRatio => Ratio(r => r.Current);

    /// <summary><see cref="OnTimeRatio"/> for the period before.</summary>
    public double? PreviousOnTimeRatio => Ratio(r => r.Previous);

    private double? Ratio(Func<TrendRow, int> at)
    {
        var onTime = Find(TrendsService.OnTimeKey) is { } o ? at(o) : 0;
        var late = Find(TrendsService.LateKey) is { } l ? at(l) : 0;
        return onTime + late == 0 ? null : (double)onTime / (onTime + late);
    }
}

/// <summary>
/// An outcome of the period's work that isn't a count: the median days an action of a priority took from being raised
/// to being done, in this period and the one before; null when none was done.
/// </summary>
public sealed record TrendOutcome(string Key, string Name, double? Current, double? Previous, string Unit);

/// <summary>An article a trend line counted, as the drill-down lists it.</summary>
public sealed record TrendArticle(Guid Id, string Title, DateOnly CountedOn, bool Briefed, TlpLevel Tlp, Classification Classification,
    Priority? Priority, string Bluf, string? SourceName, string? SourceUrl, IReadOnlyList<string> Tags, IReadOnlyList<string> Techniques,
    int Actions);

/// <summary>
/// Threat trends for the monthly and quarterly report and the board scorecard: how many articles carried each
/// scorecard tag and each ATT&amp;CK technique and tactic, month on month or quarter on quarter, and the follow-up work
/// the brief generated (actions raised, closed, hunted, still open). Every count opens to the articles or actions
/// behind it.
/// <para>
/// An article counts in the period it was first briefed. One never briefed counts in the period it was written, unless
/// it's still a draft. Each article counts once per period on a line, however many briefs carried it.
/// </para>
/// </summary>
public sealed class TrendsService(IAppDbContextFactory dbs, ICurrentUser user, OrgClock clock)
{
    public const int Months = 6;
    public const int Quarters = 5;

    internal const string DoneKey = "actions:done";
    internal const string OnTimeKey = "actions:on-time";
    internal const string LateKey = "actions:late";
    public const int Weeks = 8;

    /// <summary>How many periods a report shows: half a year of months, or five quarters (so the same quarter a year
    /// back is in it).</summary>
    public static int Span(PeriodKind kind) => kind switch { PeriodKind.Month => Months, PeriodKind.Week => Weeks, _ => Quarters };

    /// <summary>The ranges a report can be drawn over, shortest (the default) first: up to two years of months, or three
    /// years of quarters.</summary>
    public static IReadOnlyList<int> Spans(PeriodKind kind) => kind switch
    {
        PeriodKind.Month => [Months, 12, 24],
        PeriodKind.Week => [Weeks, 13, 26],
        _ => [Quarters, 8, 12],
    };

    /// <summary>A range one of <see cref="Spans"/> offers, else the default.</summary>
    public static int SpanOrDefault(PeriodKind kind, int? span) => span is { } n && Spans(kind).Contains(n) ? n : Span(kind);

    /// <summary>A technique under a tactic, as people read it: "Execution › T1059 Command and Scripting Interpreter".
    /// No tactic for a technique the catalogue doesn't know.</summary>
    public static string TechniqueName(string id, MitreTactic? tactic)
    {
        var name = $"{id} {AttackCatalog.Find(id)?.Name}".TrimEnd();
        return tactic is { } t and not MitreTactic.Unspecified ? $"{AttackMatrix.TacticName(t)} › {name}" : name;
    }

    /// <summary>
    /// The article list's filter (dates aside) for an article line's key: "tag:phishing", "category:fraud",
    /// "tactic:TA0001", "tactic:TA0001:T1566", "technique:T1566", or "articles:threat". A technique takes its
    /// sub-techniques when they're <paramref name="rolledUp"/>. Null for any other key.
    /// </summary>
    public static ArticleFilter? FilterFor(string key, bool rolledUp)
    {
        var parts = key.Split(':');
        return (parts[0], parts.Length) switch
        {
            ("articles", 2) => parts[1] switch
            {
                "all" => new ArticleFilter(),
                "threat" => new ArticleFilter { Assessment = ArticleAssessment.Threat },
                "p1-p2" => new ArticleFilter { Assessment = ArticleAssessment.P1P2 },
                "informational" => new ArticleFilter { Assessment = ArticleAssessment.Informational },
                _ => null,
            },
            ("tag", 2) => new ArticleFilter { Tag = parts[1] },
            ("category", 2) => new ArticleFilter { Category = parts[1] },
            ("technique", 2) when parts[1] == ArticleFilter.None => new ArticleFilter { Technique = ArticleFilter.None },
            ("technique", 2) => new ArticleFilter { Technique = parts[1], WithSubtechniques = rolledUp },
            ("tactic", 2 or 3) when ArticleFilter.ParseTactic(parts[1]) is { } tactic => parts.Length == 2
                ? new ArticleFilter { Tactic = tactic }
                : new ArticleFilter { Tactic = tactic, Technique = parts[2], WithSubtechniques = rolledUp },
            _ => null,
        };
    }

    /// <summary>The last complete month or quarter: the one a report is usually written for.</summary>
    public Period DefaultEnd(PeriodKind kind) => CurrentPeriod(kind).Previous;

    /// <summary>The month or quarter that holds today.</summary>
    public Period CurrentPeriod(PeriodKind kind) => Period.Of(kind, clock.Today);

    /// <summary>Whether a period hasn't ended yet (its counts are to date).</summary>
    public bool IsPartial(Period p) => p.End >= clock.Today;

    /// <param name="span">How many periods, one of <see cref="Spans"/>; the default otherwise.</param>
    public async Task<TrendReport> ReportAsync(Period end, bool rollUpSubtechniques = true, int? span = null, CancellationToken ct = default)
    {
        RequireRead();
        var count = SpanOrDefault(end.Kind, span);
        var periods = Enumerable.Range(0, count).Select(i => end.Shift(i - count + 1)).ToList();
        var first = periods[0].Start;
        var last = end.End;

        await using var db = await dbs.CreateAsync(ct);

        // When each article counts: its first brief, else the day it was written (drafts never briefed don't count).
        var firstBriefed = await ArticleDates.FirstBriefedAsync(db, ct);
        var articles = await db.Articles.AsNoTracking()
            .Select(a => new { a.Id, a.Status, a.CreatedAtUtc, a.Classification, a.Priority }).ToListAsync(ct);
        var countedOn = new Dictionary<Guid, DateOnly>();
        foreach (var a in articles)
        {
            DateOnly day;
            if (firstBriefed.TryGetValue(a.Id, out var briefed)) day = briefed;
            else if (a.Status == ArticleStatus.Draft) continue;
            else day = LocalDay(a.CreatedAtUtc);
            if (day >= first && day <= last) countedOn[a.Id] = day;
        }
        var counted = countedOn.Keys.ToList();

        var tagLinks = await db.ArticleTags.AsNoTracking().Where(t => counted.Contains(t.ArticleId))
            .Select(t => new { t.ArticleId, t.TagId }).ToListAsync(ct);
        var techniqueLinks = await db.ArticleTechniques.AsNoTracking().Where(t => counted.Contains(t.ArticleId))
            .Select(t => new { t.ArticleId, t.TechniqueId }).ToListAsync(ct);
        var categories = await db.ScorecardCategories.AsNoTracking().Include(c => c.Tags)
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Name).ToListAsync(ct);

        // Each line's ids, split by period.
        IReadOnlyList<IReadOnlyList<Guid>> Split(IEnumerable<Guid> ids)
        {
            var set = ids.Distinct().ToList();
            return periods.Select(p => (IReadOnlyList<Guid>)set.Where(id => p.Contains(countedOn[id])).OrderBy(id => countedOn[id]).ToList()).ToList();
        }

        var articleRows = new List<TrendRow>
        {
            new("articles:all", "Articles", "Articles", Split(counted), IsRollup: true),
            new("articles:threat", "Threats", "Articles", Split(articles.Where(a => countedOn.ContainsKey(a.Id) && a.Classification == Classification.Threat).Select(a => a.Id))),
            new("articles:p1-p2", "P1 and P2 threats", "Articles", Split(articles.Where(a => countedOn.ContainsKey(a.Id) && a.Priority is Priority.P1 or Priority.P2).Select(a => a.Id))),
            new("articles:informational", "Informational", "Articles", Split(articles.Where(a => countedOn.ContainsKey(a.Id) && a.Classification == Classification.Informational).Select(a => a.Id))),
        };

        // Tags under their category, the category line first. Retired ones only when something counted carries them.
        var byTag = tagLinks.ToLookup(t => t.TagId, t => t.ArticleId);
        var tagRows = new List<TrendRow>();
        foreach (var c in categories)
        {
            var tags = c.Tags.OrderBy(t => t.SortOrder).ThenBy(t => t.Name)
                .Select(t => (Tag: t, Row: new TrendRow("tag:" + t.Key, t.Name, c.Name, Split(byTag[t.Id]))))
                .Where(x => x.Row.Any || !(c.IsRetired || x.Tag.IsRetired)).Select(x => x.Row).ToList();
            if (tags.Count == 0) continue;
            var rollup = new TrendRow("category:" + c.Key, c.Name, c.Name, Split(c.Tags.SelectMany(t => byTag[t.Id])), IsRollup: true);
            tagRows.Add(rollup);
            tagRows.AddRange(tags);
        }
        var tagged = tagLinks.Select(t => t.ArticleId).ToHashSet();
        tagRows.Add(new TrendRow("tag:none", "No scorecard tag", "Not tagged", Split(counted.Where(id => !tagged.Contains(id)))));

        // ATT&CK: every tactic in matrix order (an article counts once per tactic), then the techniques seen, each named
        // under its primary tactic. A technique can sit in several tactics, so under each tactic come the techniques
        // that put an article there: a tactic's lines account for its count, and nothing is summed across tactics.
        string Technique(string id) => rollUpSubtechniques && id.IndexOf('.') is var dot and > 0 ? id[..dot] : id;
        var techniques = techniqueLinks
            .Select(t => (t.ArticleId, Id: Technique(t.TechniqueId.ToUpperInvariant()), Tactics: AttackCatalog.Find(t.TechniqueId)?.Tactics ?? []))
            .ToList();
        var byTactic = techniques.SelectMany(t => t.Tactics.Select(tactic => (Tactic: tactic, t.ArticleId, t.Id))).ToList();
        var tacticArticles = byTactic.ToLookup(x => x.Tactic, x => x.ArticleId);
        var attackRows = AttackMatrix.Tactics
            .Select(t => new TrendRow(TacticKey(t), AttackMatrix.TacticName(t), "Tactics", Split(tacticArticles[t])))
            .ToList();
        attackRows.AddRange(MostCounted(techniques.GroupBy(t => t.Id, StringComparer.Ordinal)
            .Select(g => new TrendRow("technique:" + g.Key, TechniqueName(g.Key, AttackCatalog.Find(g.Key)?.PrimaryTactic), "Techniques", Split(g.Select(x => x.ArticleId))))));
        var withTechnique = techniqueLinks.Select(t => t.ArticleId).ToHashSet();
        attackRows.Add(new TrendRow("technique:none", "No technique", "Not mapped", Split(counted.Where(id => !withTechnique.Contains(id)))));
        var tacticTechniqueRows = AttackMatrix.Tactics.SelectMany(tactic => MostCounted(byTactic.Where(x => x.Tactic == tactic)
                .GroupBy(x => x.Id, StringComparer.Ordinal)
                .Select(g => new TrendRow($"{TacticKey(tactic)}:{g.Key}", TechniqueName(g.Key, tactic), AttackMatrix.TacticName(tactic), Split(g.Select(x => x.ArticleId))))))
            .ToList();

        var (actionRows, outcomes) = await ActionRowsAsync(db, periods, ct);
        return new TrendReport(periods, rollUpSubtechniques, articleRows, tagRows, attackRows, tacticTechniqueRows, actionRows, countedOn) { Outcomes = outcomes };
    }

    private static string TacticKey(MitreTactic t) => "tactic:" + AttackMatrix.TacticId(t);

    private static IEnumerable<TrendRow> MostCounted(IEnumerable<TrendRow> rows) =>
        rows.OrderByDescending(r => r.Current).ThenByDescending(r => r.Ids.Sum(p => p.Count)).ThenBy(r => r.Key, StringComparer.Ordinal);

    /// <summary>
    /// The work generated: actions raised (by kind and team), closed (done on time or late, by outcome; or cancelled),
    /// hunted (a hunt result recorded), and open or overdue when each period ended.
    /// </summary>
    private async Task<(List<TrendRow> Rows, List<TrendOutcome> Outcomes)> ActionRowsAsync(IAppDbContext db, IReadOnlyList<Period> periods, CancellationToken ct)
    {
        var actions = await db.Actions.AsNoTracking()
            .Select(a => new { a.Id, a.CreatedAtUtc, a.RaisedOn, a.ClosedDate, a.DueDate, a.Kind, a.TeamId, a.Status, a.Outcome, a.Priority }).ToListAsync(ct);
        var hunts = await db.HuntQueries.AsNoTracking().Where(h => h.Result != HuntResult.NotRun && h.RunAtUtc != null)
            .Select(h => new { h.ActionId, h.RunAtUtc, h.Result }).ToListAsync(ct);
        var teams = await db.Teams.AsNoTracking().OrderBy(t => t.SortOrder).ThenBy(t => t.Name).ToListAsync(ct);
        // An action raised for a past day's brief counts on that day, like the brief's articles (ADR 0013).
        var facts = actions.Select(a => new ActionFact(a.Id, a.RaisedOn ?? LocalDay(a.CreatedAtUtc), a.ClosedDate, a.DueDate, a.Kind, a.TeamId, a.Status, a.Outcome, a.Priority)).ToList();
        var huntFacts = hunts.Select(h => (h.ActionId, Day: LocalDay(h.RunAtUtc!.Value), h.Result)).ToList();

        TrendRow Row(string key, string name, string group, Func<ActionFact, Period, bool> counts, bool rollup = false) =>
            new("actions:" + key, name, group,
                periods.Select(p => (IReadOnlyList<Guid>)facts.Where(f => counts(f, p)).Select(f => f.Id).ToList()).ToList(), rollup);

        TrendRow HuntRow(string key, string name, Func<HuntResult, bool> counts, bool rollup = false) =>
            new("actions:" + key, name, "Hunted",
                periods.Select(p => (IReadOnlyList<Guid>)huntFacts.Where(h => p.Contains(h.Day) && counts(h.Result)).Select(h => h.ActionId).Distinct().ToList()).ToList(), rollup);

        static bool Raised(ActionFact f, Period p) => p.Contains(f.Raised);
        static bool Closed(ActionFact f, Period p) => f.Closed is { } d && p.Contains(d);
        static bool Done(ActionFact f, Period p) => f.Status == ActionStatus.Done && Closed(f, p);
        // On time (Met) or late (Missed) as ActionSla.StateOf reads a Done action, as Workload and Team commitments do: one
        // with no due date is neither.
        SlaState Sla(ActionFact f) => ActionSla.StateOf(f.Status, f.Due, f.Closed, clock.Today, clock.Days);
        static bool OpenAt(ActionFact f, DateOnly day) => f.Raised <= day && (f.Closed is not { } c || c > day);

        var rows = new List<TrendRow> { Row("raised", "Raised", "Raised", Raised, rollup: true) };
        rows.AddRange(Enum.GetValues<ActionKind>().Select(k => Row(TrendReport.RaisedKind + k, ActionService.KindName(k), "Raised by kind", (f, p) => f.Kind == k && Raised(f, p))).Where(r => r.Any));
        rows.AddRange(teams.Select(t => (Team: t, Row: Row("raised-team:" + t.Key, t.Name, "Raised by team", (f, p) => f.Team == t.Id && Raised(f, p))))
            .Where(x => x.Row.Any || !x.Team.IsRetired).Select(x => x.Row));

        rows.Add(Row("closed", "Closed", "Closed", Closed, rollup: true));
        rows.Add(Row("done", "Done", "Closed", Done));
        rows.Add(Row("on-time", "Done on time", "Closed", (f, p) => Done(f, p) && Sla(f) == SlaState.Met));
        rows.Add(Row("late", "Done late", "Closed", (f, p) => Done(f, p) && Sla(f) == SlaState.Missed));
        rows.Add(Row("no-due", "Done, no due date", "Closed", (f, p) => Done(f, p) && f.Due is null));
        rows.Add(Row("cancelled", "Cancelled or not applicable", "Closed", (f, p) => f.Status != ActionStatus.Done && Closed(f, p)));
        rows.AddRange(Enum.GetValues<ActionOutcome>().Where(o => o != ActionOutcome.None)
            .Select(o => Row("outcome:" + o, ActionService.OutcomeName(o), "Outcome", (f, p) => f.Outcome == o && Closed(f, p))).Where(r => r.Any));

        rows.Add(HuntRow("hunted", "Hunted", _ => true, rollup: true));
        rows.Add(HuntRow("hits", "With hits", r => r == HuntResult.Hits));
        rows.Add(HuntRow("no-hits", "Hunt: no hits", r => r == HuntResult.NoHits));

        // A period still under way is read as of today: an action due later this month isn't overdue yet. One that hasn't
        // started has no end to read yet, so nothing is open or overdue at it (as of today would count today's backlog
        // in a period it isn't in).
        var today = clock.Today;
        DateOnly AsOf(Period p) => p.End < today ? p.End : today;
        bool Begun(Period p) => p.Start <= today;
        rows.Add(Row("open-at-end", "Open at period end", "Backlog", (f, p) => Begun(p) && OpenAt(f, AsOf(p)), rollup: true));
        rows.Add(Row("overdue-at-end", "Overdue at period end", "Backlog", (f, p) => Begun(p) && OpenAt(f, AsOf(p)) && f.Due < AsOf(p)));

        // How long the urgent work took: the median days from raised to done, by priority, this period and the last.
        double? Median(Priority priority, Period p)
        {
            var days = facts.Where(f => f.Priority == priority && Done(f, p)).Select(f => (double)(f.Closed!.Value.DayNumber - f.Raised.DayNumber)).Order().ToList();
            if (days.Count == 0) return null;
            return days.Count % 2 == 1 ? days[days.Count / 2] : (days[days.Count / 2 - 1] + days[days.Count / 2]) / 2;
        }
        var (current, previous) = (periods[^1], periods.Count > 1 ? periods[^2] : periods[^1].Previous);
        var outcomes = new[] { Priority.P1, Priority.P2 }
            .Select(pr => new TrendOutcome($"median-days:{pr}", $"Median days to close, {pr}", Median(pr, current), Median(pr, previous), "days")).ToList();
        return (rows, outcomes);
    }

    private sealed record ActionFact(Guid Id, DateOnly Raised, DateOnly? Closed, DateOnly? Due, ActionKind Kind, Guid Team, ActionStatus Status, ActionOutcome Outcome,
        Priority Priority);

    /// <summary>The articles behind a line, newest first, with their tags and techniques.</summary>
    public async Task<IReadOnlyList<TrendArticle>> ArticlesAsync(IReadOnlyCollection<Guid> ids, IReadOnlyDictionary<Guid, DateOnly> countedOn, CancellationToken ct = default)
    {
        RequireRead();
        if (ids.Count == 0) return [];
        await using var db = await dbs.CreateAsync(ct);
        var list = ids.ToList();
        var articles = await db.Articles.AsNoTracking().Where(a => list.Contains(a.Id))
            .Include(a => a.Tags).ThenInclude(t => t.Tag).Include(a => a.Techniques).AsSplitQuery().ToListAsync(ct);
        var briefed = (await db.BriefItems.AsNoTracking().Where(i => list.Contains(i.ArticleId)).Select(i => i.ArticleId).ToListAsync(ct)).ToHashSet();
        var actions = (await db.Actions.AsNoTracking().Where(a => a.ArticleId != null && list.Contains(a.ArticleId.Value)).Select(a => a.ArticleId!.Value).ToListAsync(ct))
            .CountBy(id => id).ToDictionary();
        return articles
            .Select(a => new TrendArticle(a.Id, a.Title, countedOn.GetValueOrDefault(a.Id, LocalDay(a.CreatedAtUtc)), briefed.Contains(a.Id), a.Tlp,
                a.Classification, a.Priority, a.Bluf, a.SourceName, a.SourceUrl,
                a.Tags.Where(t => t.Tag is not null).OrderBy(t => t.Tag!.SortOrder).Select(t => t.Tag!.Name).ToList(),
                a.Techniques.Select(t => t.TechniqueId).Order(StringComparer.Ordinal).ToList(), actions.GetValueOrDefault(a.Id)))
            .OrderByDescending(a => a.CountedOn).ThenBy(a => a.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>The report as one table: a line per row with its count in each period, oldest first, and the change.</summary>
    public static string ToCsv(TrendReport report)
    {
        var sb = new StringBuilder();
        sb.Append("section,group,line,key");
        foreach (var p in report.Periods) sb.Append(',').Append(p.Key);
        sb.Append(",change\n");
        foreach (var (section, rows) in new[] { ("Articles", report.Articles), ("Scorecard tags", report.Tags), ("ATT&CK", report.Attack), ("ATT&CK by tactic", report.TacticTechniques), ("Actions", report.Actions) })
            foreach (var r in rows)
            {
                sb.Append(Csv.Cell(section)).Append(',').Append(Csv.Cell(r.Group)).Append(',').Append(Csv.Cell(r.Name)).Append(',').Append(Csv.Cell(r.Key));
                for (var i = 0; i < report.Periods.Count; i++) sb.Append(',').Append(r.Count(i).ToString(CultureInfo.InvariantCulture));
                sb.Append(',').Append(r.Change.ToString(CultureInfo.InvariantCulture)).Append('\n');
            }
        return sb.ToString();
    }

    /// <summary>The articles counted in a period, for writing the report: what each was, how it was tagged, and where.</summary>
    public static string ArticlesCsv(IReadOnlyList<TrendArticle> articles, string baseUrl)
    {
        var root = baseUrl.TrimEnd('/');
        var sb = new StringBuilder("counted_on,briefed,title,article_url,tlp,classification,priority,source,source_url,bluf,tags,techniques,tactics,actions\n");
        foreach (var a in articles)
        {
            var tactics = a.Techniques.SelectMany(t => AttackCatalog.Find(t)?.Tactics ?? []).Distinct()
                .OrderBy(AttackMatrix.TacticRank).Select(AttackMatrix.TacticName);
            string[] cells =
            [
                a.CountedOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), a.Briefed ? "yes" : "no", a.Title, $"{root}/articles/{a.Id}",
                Tlp.Label(a.Tlp), a.Classification.ToString(), a.Priority?.ToString() ?? "", a.SourceName ?? "", a.SourceUrl ?? "", a.Bluf,
                string.Join("; ", a.Tags), string.Join(' ', a.Techniques), string.Join("; ", tactics), a.Actions.ToString(CultureInfo.InvariantCulture),
            ];
            sb.AppendJoin(',', cells.Select(Csv.Cell)).Append('\n');
        }
        return sb.ToString();
    }

    private DateOnly LocalDay(DateTimeOffset utc) => clock.DayOf(utc);

    private void RequireRead()
    {
        if (!user.Has(Permission.ReadTeamEdition)) throw new ForbiddenException("Trends are open to the team.");
    }
}
