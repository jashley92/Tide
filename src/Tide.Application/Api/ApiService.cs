using Microsoft.EntityFrameworkCore;
using Tide.Application.Abstractions;
using Tide.Application.Actions;
using Tide.Application.Briefs;
using Tide.Application.Intake;
using Tide.Application.Metrics;
using Tide.Application.Security;
using Tide.Application.Settings;
using Tide.Application.Stack;
using Tide.Domain.Common;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.Application.Api;

public sealed record ApiBullet(string Text, string Tlp);
public sealed record ApiIndicator(string Type, string Value);
public sealed record ApiEditionAction(int Number, string Title, string Team, DateOnly? DueDate, string Status);

/// <summary>An article as the API gives it: the parts the caller's ceiling lets through, as the team edition cuts it.</summary>
public sealed record ApiArticle(Guid Id, string Title, string Tlp, string Marking, string Classification, string? Priority, string BottomLine,
    IReadOnlyList<ApiBullet> Bullets, string? WhyItMatters, string? RecommendedActions, IReadOnlyList<string> Cves,
    IReadOnlyList<ApiIndicator> Indicators, IReadOnlyList<ApiEditionAction> Actions, string? SourceUrl, int WithheldParts);

public sealed record ApiBrief(DateOnly Day, string Status, string Ceiling, string Marking, string? Intro, IReadOnlyList<ApiArticle> Articles, int WithheldArticles);

public sealed record ApiAction(int Number, string Title, string Kind, string Priority, string Status, string? Outcome, string Team, string? Assignee,
    DateOnly? DueDate, DateOnly? ClosedDate, Guid? ArticleId);

public sealed record ApiProduct(Guid Id, string Name, string? OtherNames, string? Versions, string? Notes, string? Team, int Articles, int OpenActions);

public sealed record ApiMetric(Guid Id, string Key, string Name, string Format);
public sealed record ApiMetricValue(string Key, DateOnly Day, decimal Value, string? Note);
public sealed record ApiMetrics(DateOnly From, DateOnly To, IReadOnlyList<ApiMetric> Metrics, IReadOnlyList<ApiMetricValue> Values);

/// <summary>What a caller asks to change on an action: a status, an outcome when closing it Done, and a note.</summary>
public sealed record ApiActionUpdate(string? Status, string? Outcome, string? Note);

public sealed record ApiMetricEntry(string? Key, DateOnly? Day, decimal? Value, string? Note);

public sealed record ApiProposal(string? Title, string? Url, string? Summary);

/// <summary>A push of the caller's detection rules (EP-79): the full set, replacing its last push.</summary>
public sealed record ApiDetections(IReadOnlyList<ApiDetectionRule>? Rules);

/// <summary>One rule in a push: its id in the caller's system, name, ATT&amp;CK technique ids, platform and whether it's enabled.</summary>
public sealed record ApiDetectionRule(string? Id, string? Name, IReadOnlyList<string>? Techniques, string? Platform, bool? Enabled);

/// <summary>
/// The API's reads and writes (EP-75), each through the service the pages use, so the same permission checks apply. A
/// brief is cut by <see cref="BriefEditions"/> exactly as the team edition is, at the lower of the team edition's
/// ceiling and the caller's. Our stack's notes are internal (AMBER) and go only to a caller whose ceiling allows it. A
/// token's actions are cut at the same ceiling: one raised from an article above it is left out, as the brief leaves the
/// article out with its actions. A person (no token ceiling) sees the actions the Actions page shows them.
/// </summary>
public sealed class ApiService(IAppDbContextFactory dbs, ICurrentUser user, DistributionSettings editions, ActionService actions, StackService stack,
    MetricsService metrics, IntakeService intake)
{
    public async Task<ApiBrief?> BriefAsync(DateOnly day, TlpLevel? callerCeiling, CancellationToken ct = default)
    {
        if (!user.Has(Permission.ReadTeamEdition)) throw new ForbiddenException("You don't have access to the team edition.");
        await using var db = await dbs.CreateAsync(ct);
        var brief = await BriefService.Load(db.Briefs.AsNoTracking()).FirstOrDefaultAsync(b => b.BriefDate == day, ct);
        if (brief is null) return null;
        var (_, teamMax) = await editions.GetAsync(ct);
        var ceiling = callerCeiling is { } c && c < teamMax ? c : teamMax;
        var cut = BriefEditions.Build(brief, Edition.Team, ceiling, await BriefEditions.ActionsAsync(db, brief, ct));
        return new ApiBrief(day, brief.Status.ToString(), Tlp.Label(ceiling), Tlp.Label(cut.Marking), cut.ShowIntro ? brief.Intro : null,
            cut.Articles.Select(Article).ToList(), cut.WithheldArticles);
    }

    private static ApiArticle Article(EditionArticle e)
    {
        var a = e.Article;
        return new ApiArticle(a.Id, a.Title, Tlp.Label(a.Tlp), Tlp.Label(e.Marking), a.Classification.ToString(), a.Priority?.ToString(), a.Bluf,
            e.Bullets.Select(b => new ApiBullet(b.Text, Tlp.Label(a.Effective(b.Tlp)))).ToList(), e.ShowWhy ? a.WhyItMatters : null, a.RecommendedActions,
            a.Cves.Select(c => c.CveId).ToList(), e.Indicators.Select(i => new ApiIndicator(i.Type.ToString(), i.Value)).ToList(),
            e.Actions.Select(x => new ApiEditionAction(x.Number, x.Title, x.TeamName, x.DueDate, x.Status.ToString())).ToList(), a.SourceUrl, e.WithheldParts);
    }

    public async Task<IReadOnlyList<ApiAction>> ActionsAsync(ActionView view, TlpLevel? callerCeiling, CancellationToken ct = default)
    {
        var rows = await actions.ListAsync(new ActionFilter(view), ct);
        var withheld = await WithheldArticlesAsync(rows.Select(r => r.ArticleId), callerCeiling, ct);
        return rows.Where(r => r.ArticleId is not { } a || !withheld.Contains(a)).Select(Action).ToList();
    }

    /// <summary>The action, or null when there's none or it was raised from an article above the caller's ceiling.</summary>
    public async Task<ApiAction?> ActionAsync(int number, TlpLevel? callerCeiling, CancellationToken ct = default) =>
        await VisibleAsync(number, callerCeiling, ct) is { } d ? Action(d.Action) : null;

    /// <summary>Changes an action's status (an outcome is needed to close it Done), noting why. Nothing else about it changes.</summary>
    public async Task<ApiAction?> UpdateActionAsync(int number, ApiActionUpdate update, TlpLevel? callerCeiling, CancellationToken ct = default)
    {
        if (await VisibleAsync(number, callerCeiling, ct) is not { } d) return null;
        if (!EnumNames.TryParse<ActionStatus>(update.Status, out var status, ignoreCase: true))
            throw new ArgumentException("Give a status: " + string.Join(", ", Enum.GetNames<ActionStatus>()) + ".");
        var outcome = ActionOutcome.None;
        if (update.Outcome is { Length: > 0 } o && !EnumNames.TryParse(o, out outcome, ignoreCase: true))
            throw new ArgumentException("Give an outcome: " + string.Join(", ", ActionService.OutcomesFor(d.Action.Kind, status)) + ".");
        var note = string.IsNullOrWhiteSpace(update.Note) ? null : update.Note.Trim();
        if (note is { Length: > 2000 }) throw new ArgumentException("A note is at most 2,000 characters.");
        await actions.SetStatusAsync(d.Action.Id, status, outcome, note, ct);
        return await ActionAsync(number, callerCeiling, ct);
    }

    private async Task<ActionDetail?> VisibleAsync(int number, TlpLevel? callerCeiling, CancellationToken ct)
    {
        if (await actions.GetAsync(number, ct) is not { } d) return null;
        return d.Action.ArticleId is { } a && (await WithheldArticlesAsync([a], callerCeiling, ct)).Contains(a) ? null : d;
    }

    // The articles among these that are above the token's ceiling (the lower of its own and the team edition's, as the brief
    // cuts at). None for a person.
    private async Task<HashSet<Guid>> WithheldArticlesAsync(IEnumerable<Guid?> articleIds, TlpLevel? callerCeiling, CancellationToken ct)
    {
        var ids = articleIds.OfType<Guid>().Distinct().ToList();
        if (callerCeiling is not { } c || ids.Count == 0) return [];
        var (_, teamMax) = await editions.GetAsync(ct);
        var ceiling = c < teamMax ? c : teamMax;
        await using var db = await dbs.CreateAsync(ct);
        var tlps = await db.Articles.AsNoTracking().Where(a => ids.Contains(a.Id)).Select(a => new { a.Id, a.Tlp }).ToListAsync(ct);
        return tlps.Where(a => a.Tlp > ceiling).Select(a => a.Id).ToHashSet();
    }

    private static ApiAction Action(ActionRow r) => new(r.Number, r.Title, r.Kind.ToString(), r.Priority.ToString(), r.Status.ToString(),
        r.Outcome == ActionOutcome.None ? null : r.Outcome.ToString(), r.TeamName, r.AssigneeName, r.DueDate, r.ClosedDate, r.ArticleId);

    private static ApiAction Action(ActionItem a) => new(a.Number, a.Title, a.Kind.ToString(), a.Priority.ToString(), a.Status.ToString(),
        a.Outcome == ActionOutcome.None ? null : a.Outcome.ToString(), a.Team?.Name ?? "", a.AssigneeName, a.DueDate, a.ClosedDate, a.ArticleId);

    public async Task<IReadOnlyList<ApiProduct>> StackAsync(TlpLevel? callerCeiling, CancellationToken ct = default)
    {
        var notes = callerCeiling is not { } c || c >= TlpLevel.Amber;
        return (await stack.ListAsync(ct: ct)).Select(r => new ApiProduct(r.Product.Id, r.Product.Name, r.Product.OtherNames, r.Product.Versions,
            notes ? r.Product.Notes : null, r.TeamName, r.Articles, r.OpenActions)).ToList();
    }

    public async Task<ApiMetrics> MetricsAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var h = await metrics.HistoryAsync(from, to, ct);
        var keys = h.Metrics.ToDictionary(m => m.Id, m => m.Key);
        return new ApiMetrics(h.From, h.To, h.Metrics.Select(m => new ApiMetric(m.Id, m.Key, m.Name, m.Format.ToString())).ToList(),
            h.Values.Where(v => keys.ContainsKey(v.DefinitionId)).Select(v => new ApiMetricValue(keys[v.DefinitionId], v.BriefDate, v.Value, v.Note)).ToList());
    }

    /// <summary>Enters a day's value for the metric with <see cref="ApiMetricEntry.Key"/>, as typing it on the Metrics page would.</summary>
    public async Task SetMetricAsync(ApiMetricEntry entry, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(entry.Key) || entry.Day is not { } day || entry.Value is not { } value)
            throw new ArgumentException("Give the metric's key, the day and the value.");
        var groups = await metrics.ListAsync(ct: ct);
        var metric = groups.SelectMany(g => g.Metrics).FirstOrDefault(m => string.Equals(m.Key, entry.Key.Trim(), StringComparison.OrdinalIgnoreCase))
                     ?? throw new KeyNotFoundException($"There's no metric with the key “{entry.Key.Trim()}”.");
        await metrics.SaveValueAsync(metric.Id, day, value, string.IsNullOrWhiteSpace(entry.Note) ? null : entry.Note.Trim(), ct);
    }

    public Task<Guid?> ProposeAsync(ApiProposal proposal, CancellationToken ct = default) =>
        intake.ProposeAsync(user.DisplayName, proposal.Title ?? "", proposal.Url ?? "", proposal.Summary, ct);
}
