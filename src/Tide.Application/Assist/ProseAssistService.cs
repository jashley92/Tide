using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tide.Application.Abstractions;
using Tide.Application.Briefs;
using Tide.Application.Content;
using Tide.Application.Metrics;
using Tide.Application.Security;
using Tide.Application.Settings;
using Tide.Application.Trends;
using Tide.Domain.Common;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.Application.Assist;

/// <summary>
/// A checked draft: its text, the numbers it states that the material didn't (each flagged), the highest TLP of what it
/// was drafted from (a title slide line takes it), what it was based on, the model that answered (empty for a pasted
/// answer), and the stored draft its decision is counted against.
/// </summary>
public sealed record ProseDraft(Guid DraftId, string Text, IReadOnlyList<string> UnknownNumbers, TlpLevel InputTlp, string BasedOn, string Model = "");

/// <summary>
/// AI help with the two other pieces of prose written on a schedule (ADR 0004): a report section's assessment and a
/// brief's title slide line. Each is drafted from what the person could already see, cut at the lower of
/// <see cref="AiOptions.MaxTlp"/> and the team edition's ceiling, so nothing above either is read: a report's section
/// numbers (counts) and the titles and bottom lines of the articles counted, or the brief's articles' titles and bottom
/// lines. With a provider it's sent and answered; without one, "Copy a prompt" gives the same prompt to run in an approved
/// tool and the answer is pasted back. Either way the answer goes through <see cref="ReportDraftChecks"/>, numbers
/// included, and is stored as an <see cref="AiDraft"/>. Nothing is saved to the report or the brief here: the draft fills
/// the box, and the person edits and saves it. Who may draft is who may write the text today: the report's assessors
/// (<see cref="ReportService.CanAssess"/>), and the brief's writers (analysts, until it's sent).
/// </summary>
public sealed class ProseAssistService(IAppDbContextFactory dbs, ICurrentUser user, IThreatIntelAssistant ai, IOptionsMonitor<AiOptions> options,
    ReportService reports, DistributionSettings editions)
{
    /// <summary>The most articles a draft is told about, newest first.</summary>
    public const int MaxArticles = 40;

    /// <summary>The report sections whose assessment is drafted with the articles counted, not only the numbers.</summary>
    private static readonly HashSet<string> WithArticles = new(StringComparer.Ordinal) { "headlines", "topics" };

    /// <summary>Whether a provider is set up: without one, only "Copy a prompt" is offered.</summary>
    public bool ProviderReady => ai.Configured;

    // --- the report's assessments -----------------------------------------------------------------------------------

    /// <summary>The whole prompt for <paramref name="section"/>'s assessment, for "Copy a prompt".</summary>
    public async Task<string> ReportPromptAsync(Period period, string section, CancellationToken ct = default) =>
        ProsePrompt.ForCopy((await PrepareReportAsync(period, section, ct)).Input);

    /// <summary>Drafts <paramref name="section"/>'s assessment with the provider, checks it and stores the draft.</summary>
    public async Task<ProseDraft> DraftReportAsync(Period period, string section, CancellationToken ct = default) =>
        await AskAsync(await PrepareReportAsync(period, section, ct), ct);

    /// <summary>Checks an answer pasted back from an approved tool, exactly as a provider's is, and stores the draft.</summary>
    public async Task<ProseDraft> ReportFromPastedAsync(Period period, string section, string? answer, CancellationToken ct = default) =>
        await PastedAsync(await PrepareReportAsync(period, section, ct), answer, ct);

    // --- the brief's title slide line -------------------------------------------------------------------------------

    /// <summary>The whole prompt for the brief's title slide line, for "Copy a prompt".</summary>
    public async Task<string> TitleLinePromptAsync(Guid briefId, CancellationToken ct = default) =>
        ProsePrompt.ForCopy((await PrepareTitleLineAsync(briefId, ct)).Input);

    /// <summary>Suggests the brief's title slide line with the provider, checks it and stores the draft.</summary>
    public async Task<ProseDraft> DraftTitleLineAsync(Guid briefId, CancellationToken ct = default) =>
        await AskAsync(await PrepareTitleLineAsync(briefId, ct), ct);

    /// <summary>Checks a title slide line pasted back from an approved tool and stores the draft.</summary>
    public async Task<ProseDraft> TitleLineFromPastedAsync(Guid briefId, string? answer, CancellationToken ct = default) =>
        await PastedAsync(await PrepareTitleLineAsync(briefId, ct), answer, ct);

    /// <summary>Counts the person's decision on a draft: used (put in the box) or rejected.</summary>
    public async Task RecordAsync(Guid draftId, bool accepted, CancellationToken ct = default)
    {
        await using var db = await dbs.CreateAsync(ct);
        var draft = await db.AiDrafts.FirstOrDefaultAsync(d => d.Id == draftId && d.Kind != AiDraftKind.Article, ct);
        if (draft is null) return;
        Require(draft.Kind);
        if (accepted) draft.Accepted++;
        else draft.Rejected++;
        await db.SaveChangesAsync(ct);
    }

    // --- preparing --------------------------------------------------------------------------------------------------

    /// <summary>A prompt's material and how it's recorded: what it's for, the highest TLP read, and what it was based on.</summary>
    private sealed record Prepared(ProseInput Input, string Subject, TlpLevel InputTlp, string BasedOn);

    private async Task<Prepared> PrepareReportAsync(Period period, string section, CancellationToken ct)
    {
        Require(AiDraftKind.ReportAssessment);
        var name = ReportService.Sections.Where(s => s.Key == section).Select(s => s.Name).FirstOrDefault()
            ?? throw new ArgumentException("That isn't a section of the report.");
        var report = await reports.BuildAsync(period, ct);
        var cut = await CutAsync(ct);
        var withArticles = WithArticles.Contains(section);
        var articles = withArticles ? await ArticlesAsync(report.Counted, cut, ct) : [];
        var numbers = Numbers(report, section);
        var heading = $"{ReportService.Name(period)}{(report.Partial ? " (to date)" : "")}, compared with {period.Previous.Label} · {name}";
        var tlp = articles.Count == 0 ? TlpLevel.Clear : articles.Max(a => a.Tlp);
        var basedOn = $"the {name} numbers" + (withArticles ? $" · {Articles(articles.Count)} (at or under {Tlp.Label(cut)})" : "");
        return new(new ProseInput(AiDraftKind.ReportAssessment, heading, numbers, articles.Select(a => a.Article).ToList()), $"{period.Key}/{section}", tlp, basedOn);
    }

    private async Task<Prepared> PrepareTitleLineAsync(Guid briefId, CancellationToken ct)
    {
        Require(AiDraftKind.TitleLine);
        await using var db = await dbs.CreateAsync(ct);
        var brief = await db.Briefs.AsNoTracking().Include(b => b.Items).ThenInclude(i => i.Article).AsSplitQuery()
            .FirstOrDefaultAsync(b => b.Id == briefId, ct) ?? throw new KeyNotFoundException("That brief no longer exists.");
        if (brief.Status == BriefStatus.Sent) throw new InvalidOperationException("This brief has been sent; its title slide line is fixed.");
        var cut = await CutAsync(ct);
        // The team edition at the cut: the same one the emails and slides are made with, so nothing above it is read.
        var shown = BriefEditions.Build(brief, Edition.Team, cut).Articles.Select(a => a.Article).ToList();
        if (shown.Count == 0)
            throw new InvalidOperationException(brief.Items.Count == 0
                ? "The brief has no articles yet to suggest a line from."
                : $"No article on this brief is at or under {Tlp.Label(cut)}, so there's nothing to suggest a line from.");
        var input = new ProseInput(AiDraftKind.TitleLine, $"the daily brief for {brief.BriefDate.ToString("dddd d MMMM yyyy", CultureInfo.InvariantCulture)}", [],
            shown.Select(a => new ProseArticle(a.Title, a.Bluf)).ToList());
        return new(input, brief.Id.ToString(), shown.Max(a => a.Tlp), $"{Articles(shown.Count)} on the brief (at or under {Tlp.Label(cut)})");
    }

    // The lower of the AI limit and the team edition's ceiling.
    private async Task<TlpLevel> CutAsync(CancellationToken ct)
    {
        var max = options.CurrentValue.MaxTlp;
        var team = (await editions.ReadAsync(ct)).Team;
        return max < team ? max : team;
    }

    // The counted articles at or under the cut (an edition leaves out an article above its ceiling, BriefEditions.BuildArticle),
    // newest first: only the title, bottom line and TLP are read.
    private async Task<IReadOnlyList<(ProseArticle Article, TlpLevel Tlp)>> ArticlesAsync(IReadOnlyList<Guid> ids, TlpLevel cut, CancellationToken ct)
    {
        if (ids.Count == 0) return [];
        await using var db = await dbs.CreateAsync(ct);
        var list = ids.ToList();
        var rows = await db.Articles.AsNoTracking().Where(a => list.Contains(a.Id) && a.Tlp <= cut)
            .OrderByDescending(a => a.CreatedAtUtc).Take(MaxArticles).Select(a => new { a.Title, a.Bluf, a.Tlp }).ToListAsync(ct);
        return rows.Select(a => (new ProseArticle(a.Title, a.Bluf), a.Tlp)).ToList();
    }

    /// <summary>
    /// <paramref name="section"/>'s numbers as lines, as the report page shows them: each with the period before and the
    /// change, so a draft can say "up 4" without working it out (which the numbers check would flag).
    /// </summary>
    internal static IReadOnlyList<string> Numbers(PeriodReport r, string section)
    {
        static string Num(double? v, string unit) => v is not { } n ? "none" : unit == " days" ? Words.Days(n) : Words.Num(n) + unit;
        static string Change(double c) => (c > 0 ? "+" : "") + Words.Num(c);
        static string Figure(ReportFigure f) =>
            $"{f.Name}: {Num(f.Current, f.Unit)} (the period before: {Num(f.Previous, f.Unit)}{(f.Change is { } c ? $"; change {Change(c)}" : "")})";
        static string Top(string what, ReportTop t) => $"{what}{t.Name}: {t.Current} (change {Change(t.Change)})";
        static string Value(MetricDefinition m, decimal? v) => v is { } n ? MetricMath.Format(m.Format, n) : "none";

        return section switch
        {
            "headlines" => r.Headlines.Select(Figure).ToList(),
            "outcomes" => r.Outcomes.Select(Figure).ToList(),
            "metrics" => r.Metrics.Select(m => $"{m.Group} · {m.Metric.Name}: latest {Value(m.Metric, m.Latest)}, average {Value(m.Metric, m.Average)}, "
                + $"lowest {Value(m.Metric, m.Lowest)}, highest {Value(m.Metric, m.Highest)}, business days missing {m.Missing}, days past a threshold {m.Breaches}").ToList(),
            "topics" => [.. r.Topics.Select(t => Top("Scorecard topic ", t)), .. r.Techniques.Select(t => Top("ATT&CK technique ", t))],
            "teams" => r.ByTeam.Select(t => Top("Actions raised by ", t)).ToList(),
            _ => [],
        };
    }

    // --- answering and storing --------------------------------------------------------------------------------------

    private async Task<ProseDraft> AskAsync(Prepared p, CancellationToken ct)
    {
        if (!ai.Configured) throw new InvalidOperationException("No AI provider is set up. Copy a prompt to run in an approved tool instead.");
        var request = ProsePrompt.Request(p.Input);
        var answer = await ai.AnswerAsync(request, ct);
        if (answer.Json is null) throw new InvalidOperationException(answer.Error ?? "The AI provider didn't answer.");
        return await StoreAsync(p, ai.Provider, ai.Model, AssistService.Hash(request.Instructions + request.Input), answer.Json, ct);
    }

    private async Task<ProseDraft> PastedAsync(Prepared p, string? answer, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(answer)) throw new ArgumentException("Paste the tool's answer first.");
        return await StoreAsync(p, AssistService.Pasted, "", AssistService.Hash(ProsePrompt.ForCopy(p.Input)), answer, ct);
    }

    private async Task<ProseDraft> StoreAsync(Prepared p, string provider, string model, string hash, string json, CancellationToken ct)
    {
        var spec = ProsePrompt.For(p.Input.Kind);
        var checkedProse = ReportDraftChecks.Check(json, spec, ProsePrompt.Material(p.Input));
        await using var db = await dbs.CreateAsync(ct);
        var draft = new AiDraft
        {
            Kind = p.Input.Kind,
            Subject = p.Subject,
            Provider = provider,
            Model = model.Length > 100 ? model[..100] : model,
            PromptVersion = spec.Version,
            InputHash = hash,
            InputTlp = p.InputTlp,
            BasedOn = Words.Ellipsis(p.BasedOn, AiDraft.MaxBasedOn),
            Suggestions = JsonSerializer.Serialize(checkedProse),
            Offered = 1,
        };
        db.AiDrafts.Add(draft);
        await db.SaveChangesAsync(ct);
        return new(draft.Id, checkedProse.Text, checkedProse.UnknownNumbers, p.InputTlp, p.BasedOn, model);
    }

    private static string Articles(int n) => n == 1 ? "1 article's title and bottom line" : $"{n} articles' titles and bottom lines";

    // Who may write the text today may draft it; nobody else.
    private void Require(AiDraftKind kind)
    {
        if (kind == AiDraftKind.ReportAssessment && !reports.CanAssess) throw new ForbiddenException("Only analysts can write the report's assessments.");
        if (kind == AiDraftKind.TitleLine && !user.Has(Permission.WriteArticles)) throw new ForbiddenException("Only analysts can build briefs.");
    }
}
