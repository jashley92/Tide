using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tide.Application.Abstractions;
using Tide.Application.Articles;
using Tide.Application.Intake;
using Tide.Application.Security;
using Tide.Application.Stack;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.Application.Assist;

/// <summary>
/// Where drafting stands for an article: whether a provider is set, the TLP of what would be sent against the limit,
/// how its page may be fetched, and whether our internal notes can go with it. <see cref="Blocked"/> says why nothing
/// can be sent, when that's so.
/// </summary>
public sealed record AssistStatus(bool ProviderReady, string? Provider, TlpLevel InputTlp, TlpLevel MaxTlp, ArticleFetch Fetch, string? SourceName,
    bool SendsOurNotes, string? Blocked)
{
    public bool CanDraft => ProviderReady && Blocked is null;
}

/// <summary>The article's text from "Fetch the article", with where it came from ("fetched by TIDE: the page's article
/// markup"), or why there's none (<see cref="Problem"/>, safe to show).</summary>
public sealed record FetchedText(string? Text, string? Origin, string? Problem = null);

/// <summary>Another report of the article (a link added from triage): its address, its outlet, and its text when it was
/// read in triage and is still kept.</summary>
public sealed record OtherSource(string Url, string Name, string? Text, string? Origin);

/// <summary>A draft's checked suggestions, the stored draft they're counted against, what it was based on, the model that
/// answered (empty for a pasted answer), and the sources' names in the order the suggestions number them.</summary>
public sealed record DraftResult(Guid DraftId, Suggestions Suggestions, string BasedOn, string Model = "", IReadOnlyList<string>? SourceNames = null)
{
    public IReadOnlyList<string> SourceNames { get; init; } = SourceNames ?? [];
}

/// <summary>
/// The editor's drafting help (ADR 0004, EP-70): fetching the article's text, drafting from it with the AI provider or
/// from an answer pasted back from an approved tool, and counting what the analyst accepts. Analysts only. Nothing is
/// sent above <see cref="AiOptions.MaxTlp"/>; our notes on what we run are internal (AMBER) and go only when the limit
/// allows it. Every draft is stored with its provider, model, prompt version and a hash of what was sent; the source
/// text itself isn't kept.
/// </summary>
public sealed class AssistService(IAppDbContextFactory dbs, ICurrentUser user, IThreatIntelAssistant ai, IOutboundHttp http,
    IOptionsMonitor<AiOptions> options, StackService stack, Scorecard.ScorecardService scorecard, IOptionsMonitor<IntakeOptions> intake)
{
    public const string Pasted = "Pasted";

    // Our notes on what we run are internal context: AMBER, whatever the article's marking.
    private const TlpLevel NotesTlp = TlpLevel.Amber;

    public async Task<AssistStatus> StatusAsync(Guid articleId, TlpLevel articleTlp, CancellationToken ct = default)
    {
        RequireWrite();
        var source = await SourceOfAsync(articleId, ct);
        return Status(articleTlp, source);
    }

    /// <summary>
    /// The article's text from its page: fetched by TIDE through intake's guarded client and the body found by
    /// <see cref="ArticleText"/> (with the source's selector, if it has one). When that fails and the source is marked
    /// Open, the article is TLP:CLEAR and web grounding is on, the provider looks it up instead. A source marked Never
    /// isn't fetched.
    /// </summary>
    public async Task<FetchedText> FetchAsync(Guid articleId, string? url, string? title, TlpLevel articleTlp, CancellationToken ct = default)
    {
        RequireWrite();
        var address = ArticleService.SafeUrl(url);
        if (address is null) return new(null, null, "The article has no http or https link to fetch. Paste its text instead.");
        var source = await SourceOfAsync(articleId, ct, address);
        if (source?.ArticleFetch == ArticleFetch.Never)
            return new(null, null, $"Articles from {source.Name} aren't fetched (its sources setting). Paste the text instead.");

        var got = await http.GetAsync(address, ct: ct);
        ExtractedText? extracted = got.Status == FetchStatus.Ok && got.Content is { } bytes ? await ArticleText.ExtractAsync(bytes, source?.ArticleBodySelector, intake.CurrentValue.ReadBudget, ArticleCleanup.For(intake.CurrentValue, source), ct) : null;
        if (extracted is { Usable: true }) return new(extracted.Text, "fetched by TIDE: " + extracted.How);
        var problem = extracted?.Problem ?? got.Error ?? "The page couldn't be read.";

        var o = options.CurrentValue;
        var status = Status(articleTlp, source);
        if (source?.ArticleFetch == ArticleFetch.Open && o.WebGrounding && ai.Configured && status.InputTlp == TlpLevel.Clear)
        {
            var found = await ai.FindArticleAsync(address, title, ct);
            if (found.Text is { Length: >= ArticleText.MinChars } text)
                return new(text.Length > ArticleText.MaxChars ? text[..ArticleText.MaxChars] : text, "found by the AI provider's web search");
            problem += " The AI provider's web search didn't find it either" + (found.Error is { } e ? $" ({e.TrimEnd('.')})." : ".");
        }
        return new(null, null, problem + " Paste the article instead.");
    }

    /// <summary>
    /// The article's text as read in triage (EP-76), kept on the item it was promoted from at its own link, so the editor
    /// starts with it and needn't fetch again. Null when none is kept. Another report's text (an item attached to it, or
    /// one of a group's) is never this: the editor sends this text as the article's own, at its own feed's TLP, and lists
    /// those reports under <see cref="OtherSourcesAsync"/> at theirs.
    /// </summary>
    public async Task<FetchedText?> KeptTextAsync(Guid articleId, CancellationToken ct = default)
    {
        RequireWrite();
        await using var db = await dbs.CreateAsync(ct);
        var url = await db.Articles.AsNoTracking().Where(a => a.Id == articleId).Select(a => a.SourceUrl).FirstOrDefaultAsync(ct);
        if (url is null) return null;
        var canonical = UrlCanonicalizer.Canonicalize(url) ?? url;
        var kept = await (from i in db.FeedItems.AsNoTracking()
                          where i.ArticleId == articleId && (i.Url == url || i.Url == canonical)
                          join t in db.FeedItemTexts.AsNoTracking() on i.Id equals t.FeedItemId
                          orderby t.FetchedAtUtc descending
                          select t).FirstOrDefaultAsync(ct);
        return kept is null ? null : new(kept.Text, "read in triage: " + kept.How);
    }

    /// <summary>
    /// The article's other reports (links added from triage, as "Promote as one" adds them), each with its text when it
    /// was read in triage and is still kept, so a draft can be made from all of them (EP-77).
    /// </summary>
    public async Task<IReadOnlyList<OtherSource>> OtherSourcesAsync(Guid articleId, CancellationToken ct = default)
    {
        RequireWrite();
        await using var db = await dbs.CreateAsync(ct);
        var own = await db.Articles.AsNoTracking().Where(a => a.Id == articleId).Select(a => a.SourceUrl).FirstOrDefaultAsync(ct);
        var links = await db.ArticleLinks.AsNoTracking().Where(l => l.ArticleId == articleId && l.Url != own).OrderBy(l => l.AddedAtUtc).ToListAsync(ct);
        var urls = links.Select(l => l.Url).ToList();
        var kept = await (from i in db.FeedItems.AsNoTracking()
                          where urls.Contains(i.Url)
                          join t in db.FeedItemTexts.AsNoTracking() on i.Id equals t.FeedItemId
                          select new { i.Url, t.Text, t.How }).ToListAsync(ct);
        return links.DistinctBy(l => l.Url).Select(l =>
        {
            var k = kept.FirstOrDefault(x => x.Url == l.Url);
            var name = l.SourceName ?? (Uri.TryCreate(l.Url, UriKind.Absolute, out var u) ? u.Host : l.Url);
            return new OtherSource(l.Url, name, k?.Text, k is null ? null : "read in triage: " + k.How);
        }).ToList();
    }

    /// <summary>
    /// What TIDE's own fetch finds at <paramref name="url"/> with <paramref name="selector"/> (or automatically) and
    /// <paramref name="removeSelector"/> taken out besides the adverts, sent as <paramref name="userAgent"/> (the source's
    /// choice as typed; null for the global one): the Sources page's Test, for an administrator setting a source's
    /// selectors. The provider isn't asked.
    /// </summary>
    public async Task<ExtractedText> TestFetchAsync(string? url, string? selector, string? removeSelector = null, string? userAgent = null, CancellationToken ct = default)
    {
        if (!user.Has(Permission.Administer)) throw new ForbiddenException("Only an administrator can change the sources.");
        var address = ArticleService.SafeUrl(url) ?? throw new ArgumentException("Enter an article's http or https address.");
        foreach (var s in new[] { selector, removeSelector })
            if (!string.IsNullOrWhiteSpace(s) && !ArticleText.IsSelector(s.Trim())) throw new ArgumentException($"“{s.Trim()}” isn't a CSS selector TIDE can use.");
        var got = await http.GetAsync(address, new OutboundRequest(UserAgent: userAgent), ct);
        return got.Status == FetchStatus.Ok && got.Content is { } bytes
            ? await ArticleText.ExtractAsync(bytes, selector, intake.CurrentValue.ReadBudget, ArticleCleanup.For(intake.CurrentValue, removeSelector), ct)
            : new ExtractedText("", "", got.Error ?? "The page couldn't be read.");
    }

    /// <summary>Drafts from the article's own text (pasted or fetched) and its feed summary; see the overload for several sources.</summary>
    public Task<DraftResult> DraftAsync(Guid articleId, ArticleForm form, string? sourceText, string? sourceOrigin, CancellationToken ct = default) =>
        DraftAsync(articleId, form, One(form, sourceText, sourceOrigin), ct);

    /// <summary>Drafts from the article's sources with the AI provider, checks the answer, and stores the draft.</summary>
    public async Task<DraftResult> DraftAsync(Guid articleId, ArticleForm form, IReadOnlyList<DraftSource> sources, CancellationToken ct = default)
    {
        RequireWrite();
        if (!ai.Configured) throw new InvalidOperationException("No AI provider is set up. Copy a prompt to run in an approved tool instead.");
        var (input, context, status, basedOn) = await PrepareAsync(articleId, form, sources, ct);
        var request = DraftPrompt.For(input);
        var answer = await ai.AnswerAsync(request, ct);
        if (answer.Json is null) throw new InvalidOperationException(answer.Error ?? "The AI provider didn't answer.");
        var suggestions = DraftChecks.Check(answer.Json, context);
        var id = await StoreAsync(articleId, ai.Provider, ai.Model, Hash(request.Instructions + request.Input), status, basedOn, suggestions, ct);
        return new(id, suggestions, basedOn, ai.Model, Names(input));
    }

    /// <summary>The whole prompt for "Copy a prompt", to run in a tool the organisation has approved. Refused above the TLP limit.</summary>
    public Task<string> PromptAsync(Guid articleId, ArticleForm form, string? sourceText, string? sourceOrigin, CancellationToken ct = default) =>
        PromptAsync(articleId, form, One(form, sourceText, sourceOrigin), ct);

    public async Task<string> PromptAsync(Guid articleId, ArticleForm form, IReadOnlyList<DraftSource> sources, CancellationToken ct = default)
    {
        RequireWrite();
        var (input, _, _, _) = await PrepareAsync(articleId, form, sources, ct);
        return DraftPrompt.ForCopy(input);
    }

    /// <summary>Checks an answer pasted back from an approved tool, exactly as a provider's is, and stores the draft.</summary>
    public Task<DraftResult> FromPastedAsync(Guid articleId, ArticleForm form, string? sourceText, string? sourceOrigin, string? answer, CancellationToken ct = default) =>
        FromPastedAsync(articleId, form, One(form, sourceText, sourceOrigin), answer, ct);

    public async Task<DraftResult> FromPastedAsync(Guid articleId, ArticleForm form, IReadOnlyList<DraftSource> sources, string? answer, CancellationToken ct = default)
    {
        RequireWrite();
        if (string.IsNullOrWhiteSpace(answer)) throw new ArgumentException("Paste the tool's answer first.");
        var (input, context, status, basedOn) = await PrepareAsync(articleId, form, sources, ct);
        var suggestions = DraftChecks.Check(answer, context);
        var id = await StoreAsync(articleId, Pasted, "", Hash(DraftPrompt.ForCopy(input)), status, basedOn, suggestions, ct);
        return new(id, suggestions, basedOn, "", Names(input));
    }

    /// <summary>Counts a decision on a draft's suggestions: <paramref name="count"/> accepted or rejected.</summary>
    public async Task RecordAsync(Guid draftId, bool accepted, int count = 1, CancellationToken ct = default)
    {
        RequireWrite();
        if (count <= 0) return;
        await using var db = await dbs.CreateAsync(ct);
        var draft = await db.AiDrafts.FirstOrDefaultAsync(d => d.Id == draftId, ct);
        if (draft is null) return;
        if (accepted) draft.Accepted += count;
        else draft.Rejected += count;
        await db.SaveChangesAsync(ct);
    }

    private AssistStatus Status(TlpLevel articleTlp, Source? source)
    {
        var o = options.CurrentValue;
        var input = Tlp.Checked(articleTlp);
        if (source is not null && source.DefaultTlp > input) input = source.DefaultTlp;
        var blocked = input > o.MaxTlp
            ? $"This article is {Tlp.Label(input)} and the AI limit is {Tlp.Label(o.MaxTlp)}, so nothing is sent."
            : null;
        return new(ai.Configured, ai.Configured ? ai.Provider : null, input, o.MaxTlp, source?.ArticleFetch ?? ArticleFetch.TideOnly, source?.Name,
            NotesTlp <= o.MaxTlp, blocked);
    }

    private static IReadOnlyList<string> Names(DraftInput input) => DraftPrompt.Numbered(input).Select(x => x.Name).ToList();

    // The article's own text as its one source, named after its outlet.
    private static IReadOnlyList<DraftSource> One(ArticleForm form, string? text, string? origin) =>
        string.IsNullOrWhiteSpace(text) ? [] : [new DraftSource(form.SourceName is { Length: > 0 } n ? n : "the article", text, origin)];

    // The material for a draft, what its answer is checked against, and the line that says what it was based on.
    private async Task<(DraftInput, DraftContext, AssistStatus, string)> PrepareAsync(Guid articleId, ArticleForm form, IReadOnlyList<DraftSource> sources,
        CancellationToken ct)
    {
        if (form.Id != articleId) throw new ArgumentException("That draft is for another article.");
        await using var db = await dbs.CreateAsync(ct);
        var source = await SourceOfAsync(db, articleId, ct);
        // Every source goes at the TLP of its feed at least: one AMBER report keeps the whole draft at AMBER. A feed item
        // is stored under its canonical address, so a source's address is looked up that way too. The feed summary goes
        // at its own feed's TLP: it can be another report's (one attached, or a group's), not the article's own feed's.
        var urls = sources.Select(x => x.Url).OfType<string>().ToList();
        urls = [.. urls.Concat(urls.Select(UrlCanonicalizer.Canonicalize).OfType<string>()).Distinct()];
        var feedTlps = urls.Count == 0 ? [] : await db.FeedItems.AsNoTracking().Where(i => urls.Contains(i.Url)).Select(i => i.Source!.DefaultTlp).ToListAsync(ct);
        var summary = await ArticleService.ExcerptItems(db, articleId, form.SourceUrl).Select(i => new { i.Excerpt, i.Source!.DefaultTlp }).FirstOrDefaultAsync(ct);
        if (summary is not null) feedTlps.Add(summary.DefaultTlp);
        var status = Status(feedTlps.Append(form.Tlp).Max(), source);
        if (status.Blocked is { } why) throw new InvalidOperationException(why);

        if (sources.Count > MaxSources) throw new ArgumentException($"Draft from at most {MaxSources} sources.");
        var texts = sources.Where(x => !string.IsNullOrWhiteSpace(x.Text))
            .Select(x => x with { Text = x.Text.Trim() is var t && t.Length > ArticleText.MaxChars ? t[..ArticleText.MaxChars] : t }).ToList();
        var excerpt = summary?.Excerpt;
        var cveIds = form.Cves.Select(Indicators.IocExtractor.NormalizeCve).OfType<string>().Distinct().ToList();
        var cves = cveIds.Count == 0 ? [] : await db.CveRecords.AsNoTracking().Where(c => cveIds.Contains(c.CveId)).ToListAsync(ct);

        var products = new List<OurProduct>();
        if (status.SendsOurNotes)
        {
            var ours = await stack.ForArticleAsync(articleId, ct);
            products = ours.Tagged.Concat(ours.Suggested).DistinctBy(p => p.Id).Select(p => new OurProduct(p.Name, p.Versions, p.Notes)).ToList();
        }
        var tags = (await scorecard.ListAsync(ct: ct)).SelectMany(c => c.Tags).Where(t => !t.IsRetired).Select(t => (t.Id, t.Name)).ToList();

        var input = new DraftInput(form.Title, texts, excerpt, cves, products, tags.Select(t => t.Name).ToList());
        var context = new DraftContext(DraftPrompt.Numbered(input).Select(x => x.Text).ToList(), products.Count > 0, tags,
            form.TechniqueIds.ToHashSet(StringComparer.OrdinalIgnoreCase), form.TagIds,
            form.Indicators.Select(i => DraftChecks.IndicatorKey(i.Type, Domain.Observables.IocObservable.Normalize(i.Type, i.Value))).ToHashSet(StringComparer.Ordinal),
            cveIds.ToHashSet(StringComparer.Ordinal));
        if (products.Count > 0 && status.InputTlp < NotesTlp) status = status with { InputTlp = NotesTlp };
        return (input, context, status, BasedOn(texts, excerpt, cves, products, status));
    }

    /// <summary>The most sources one draft is made from.</summary>
    public const int MaxSources = 8;

    // "article text, 4,120 chars (fetched by TIDE: …) · feed summary · CVE-2026-1281 · our notes on GitLab", saying why
    // our notes stayed behind when the limit kept them.
    private static string BasedOn(IReadOnlyList<DraftSource> texts, string? excerpt, IReadOnlyList<CveRecord> cves, IReadOnlyList<OurProduct> products, AssistStatus status)
    {
        var parts = new List<string>();
        foreach (var (t, i) in texts.Select((t, i) => (t, i)))
            parts.Add((texts.Count > 1 ? $"[{i + 1}] {t.Name}, " : "article text, ") + $"{t.Text.Length.ToString("N0", CultureInfo.InvariantCulture)} chars"
                + (t.Origin is { Length: > 0 } o ? $" ({o})" : " (pasted)"));
        if (excerpt is not null) parts.Add("feed summary");
        if (cves.Count > 0) parts.Add(string.Join(", ", cves.Select(c => c.CveId)));
        if (products.Count > 0) parts.Add("our notes on " + string.Join(", ", products.Select(p => p.Name)));
        else if (!status.SendsOurNotes) parts.Add($"not our notes ({Tlp.Label(NotesTlp)}, above the limit)");
        var line = parts.Count == 0 ? "the title only" : string.Join(" · ", parts);
        return line.Length > AiDraft.MaxBasedOn ? line[..(AiDraft.MaxBasedOn - 1)] + "…" : line;
    }

    private async Task<Guid> StoreAsync(Guid articleId, string provider, string model, string hash, AssistStatus status, string basedOn,
        Suggestions suggestions, CancellationToken ct)
    {
        await using var db = await dbs.CreateAsync(ct);
        var json = JsonSerializer.Serialize(suggestions);
        var draft = new AiDraft
        {
            ArticleId = articleId,
            Provider = provider,
            Model = model.Length > 100 ? model[..100] : model,
            PromptVersion = DraftPrompt.Version,
            InputHash = hash,
            InputTlp = status.InputTlp,
            BasedOn = basedOn,
            Suggestions = json.Length > AiDraft.MaxSuggestions ? "{}" : json,
            Offered = suggestions.Count,
        };
        db.AiDrafts.Add(draft);
        await db.SaveChangesAsync(ct);
        return draft.Id;
    }

    private async Task<Source?> SourceOfAsync(Guid articleId, CancellationToken ct, string? url = null)
    {
        await using var db = await dbs.CreateAsync(ct);
        return await SourceOfAsync(db, articleId, ct, url);
    }

    // The source of the item at the address asked about, else of the item the article was made from (its own link
    // first), if it came from triage. A feed item is stored under its canonical address, so the address is looked up that
    // way too: a report link added by hand with its tracking tags is still its feed's (its fetch setting and its TLP).
    private static async Task<Source?> SourceOfAsync(IAppDbContext db, Guid articleId, CancellationToken ct, string? url = null)
    {
        var canonical = url is null ? null : UrlCanonicalizer.Canonicalize(url) ?? url;
        if (url is not null && await db.FeedItems.AsNoTracking().Where(i => i.Url == url || i.Url == canonical).OrderByDescending(i => i.ArticleId == articleId)
                .Select(i => i.Source).FirstOrDefaultAsync(ct) is { } itsOwn)
            return itsOwn;
        var own = await db.Articles.AsNoTracking().Where(a => a.Id == articleId).Select(a => a.SourceUrl).FirstOrDefaultAsync(ct);
        return await db.FeedItems.AsNoTracking().Where(i => i.ArticleId == articleId).OrderByDescending(i => i.Url == own)
            .Select(i => i.Source).FirstOrDefaultAsync(ct);
    }

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private void RequireWrite()
    {
        if (!user.Has(Permission.WriteArticles)) throw new ForbiddenException("Only analysts can draft articles.");
    }
}
