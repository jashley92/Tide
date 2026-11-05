using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tide.Application.Abstractions;
using Tide.Application.Security;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.Application.Intake;

/// <summary>CVE enrichment settings (config section <c>Cve</c>). Off by default.</summary>
public sealed class CveOptions
{
    public bool Enabled { get; set; }

    /// <summary>Hours between enrichment runs; EPSS is refreshed this often, NVD weekly.</summary>
    public int RefreshHours { get; set; } = 24;

    /// <summary>An NVD API key raises its rate limit tenfold. Set it as the <c>Cve__NvdApiKey</c> environment variable.</summary>
    public string NvdApiKey { get; set; } = "";

    public string NvdUrl { get; set; } = "https://services.nvd.nist.gov/rest/json/cves/2.0";
    public string EpssUrl { get; set; } = "https://api.first.org/data/v1/epss";
    public string KevUrl { get; set; } = "https://www.cisa.gov/sites/default/files/feeds/known_exploited_vulnerabilities.json";

    /// <summary>The most NVD lookups in one run (NVD allows 5 requests in 30 seconds without a key, 50 with one).</summary>
    public int MaxNvdPerRun { get; set; } = 40;

    /// <summary>Milliseconds between NVD requests; blank uses NVD's published limits (6,500 without a key, 700 with).</summary>
    public int? NvdDelayMs { get; set; }
}

/// <summary>What one enrichment run updated, and the first thing that went wrong.</summary>
public sealed record CveRun(DateTimeOffset AtUtc, int Kev, int Epss, int Nvd, string? Error);

/// <summary>A priority suggested from an item's CVE data, and the reason shown beside it ("on KEV, CVSS 10").</summary>
public sealed record PrioritySuggestion(Priority Priority, string Reason);

/// <summary>The last enrichment run, kept in memory for the administration page (a restart clears it), and whether one is
/// running now. Singleton.</summary>
public sealed class CveRunLog
{
    private int _running;

    public CveRun? Last { get; set; }

    /// <summary>Starts a run unless one is under way: a refresh by hand and the scheduled one at the same moment each
    /// added the same new CVEs, and the second save failed on their keys. One process (ADR 0001), so held in memory.</summary>
    public bool TryStart() => Interlocked.CompareExchange(ref _running, 1, 0) == 0;

    public void Finished() => Volatile.Write(ref _running, 0);
}

/// <summary>
/// CVE context from public sources: the CISA KEV catalog (known exploited, due date, ransomware use), FIRST's EPSS score
/// (how likely exploitation is in the next 30 days), and NVD's CVSS. A scheduled run (off until <c>Cve:Enabled</c>)
/// refreshes the CVEs on articles and on recent feed items; pages read what's stored and never wait on these services.
/// </summary>
public sealed class CveService(IAppDbContextFactory dbs, ICurrentUser user, IOutboundHttp http, IOptionsMonitor<CveOptions> options, TimeProvider time,
    CveRunLog log, Briefs.IMeetingsInProgress? meetings = null)
{
    public const string LastRunKey = "Cve:LastRun";

    /// <summary>EPSS at or above this suggests a threat (roughly the top few percent of all CVEs).</summary>
    public const decimal HighEpss = 0.5m;

    public CveRun? LastRun => log.Last;

    public CveOptions Options => options.CurrentValue;

    /// <summary>What's stored about these CVEs, by id (ids with nothing stored are left out). A Visitor gets them only from
    /// a seat in a live meeting, for its slides' CVE chips (public NVD, KEV and EPSS data).</summary>
    public async Task<IReadOnlyDictionary<string, CveRecord>> GetAsync(IEnumerable<string> ids, CancellationToken ct = default)
    {
        if (!user.Has(Permission.ReadTeamEdition) && !Briefs.MeetingAccess.SeatedAnywhere(user, meetings)) throw new ForbiddenException("You don't have access to CVE details.");
        var list = ids.Select(i => i.ToUpperInvariant()).Distinct().ToList();
        if (list.Count == 0) return new Dictionary<string, CveRecord>();
        await using var db = await dbs.CreateAsync(ct);
        return await db.CveRecords.AsNoTracking().Where(c => list.Contains(c.CveId)).ToDictionaryAsync(c => c.CveId, ct);
    }

    /// <summary>Why these CVEs suggest the article is a threat, if they do: listed on KEV, or a high EPSS.</summary>
    public static IReadOnlyList<string> ThreatHints(IEnumerable<CveRecord> records) => records
        .SelectMany(r => new[]
        {
            r.IsKev ? $"{r.CveId} is on CISA's known exploited list{(r.KevRansomware ? ", used in ransomware" : "")}." : null,
            !r.IsKev && r.EpssScore >= HighEpss ? $"{r.CveId} has a {r.EpssScore:P0} chance of exploitation in the next 30 days (EPSS)." : null,
        })
        .OfType<string>().ToList();

    /// <summary>EPSS at or above this suggests P2 when promoting.</summary>
    public const decimal SuggestEpss = 0.1m;

    /// <summary>CVSS at or above this is critical: P2 when promoting, P1 with "exploit" in the title.</summary>
    public const decimal CriticalCvss = 9m;

    /// <summary>A KEV listing older than this is shown with its date, since roundups cite years-old KEV entries.</summary>
    public const int RecentKevDays = 30;

    // A title saying it's not (yet) exploited doesn't count as saying it is.
    private static readonly string[] NotExploited =
        ["not exploited", "not yet exploited", "not been exploited", "no exploitation", "no evidence of exploitation", "no known exploit"];

    /// <summary>
    /// The priority the promote form suggests for an item with these CVEs and this title, and why ("on KEV, CVSS 10"):
    /// P1 when any is on KEV, or a critical CVSS with "exploit" in the title; P2 for an EPSS of 10% or more, or a critical
    /// CVSS; otherwise null (the form's own default). Only a suggestion: the analyst can always change it.
    /// </summary>
    public static PrioritySuggestion? SuggestedPriority(IEnumerable<CveRecord> records, string title, DateOnly today)
    {
        var list = records.ToList();
        var cvss = list.Max(r => r.CvssScore);
        var epss = list.Max(r => r.EpssScore);
        var critical = cvss >= CriticalCvss;
        var cvssText = critical ? "CVSS " + cvss!.Value.ToString("0.#", CultureInfo.InvariantCulture) : null;
        var kev = list.Where(r => r.IsKev).ToList();
        if (kev.Count > 0)
        {
            // The newest listing; an old one says when, so a roundup citing it reads for what it is.
            var added = kev.Max(r => r.KevAddedDate);
            var kevText = added is { } d && d.AddDays(RecentKevDays) < today
                ? "on KEV since " + d.ToString("MMM yyyy", CultureInfo.InvariantCulture) : "on KEV";
            return new(Priority.P1, Join(kevText, cvssText));
        }
        if (critical && SaysExploited(title)) return new(Priority.P1, Join(cvssText, "\"exploit\" in the title"));
        var epssText = epss >= SuggestEpss ? "EPSS " + epss!.Value.ToString("P0", CultureInfo.InvariantCulture).Replace(" ", "", StringComparison.Ordinal) : null;
        if (epssText is not null || critical) return new(Priority.P2, Join(epssText, cvssText));
        return null;
    }

    private static string Join(params string?[] parts) => string.Join(", ", parts.OfType<string>());

    private static bool SaysExploited(string title) =>
        title.Contains("exploit", StringComparison.OrdinalIgnoreCase)
        && !NotExploited.Any(n => title.Contains(n, StringComparison.OrdinalIgnoreCase));

    /// <summary>Runs enrichment if it's on and due. Returns the run, or null when nothing ran.</summary>
    public async Task<CveRun?> EnrichDueAsync(CancellationToken ct = default)
    {
        var o = options.CurrentValue;
        if (!o.Enabled) return null;
        await using (var db = await dbs.CreateAsync(ct))
        {
            var last = await db.Settings.AsNoTracking().Where(s => s.Key == LastRunKey).Select(s => s.Value).FirstOrDefaultAsync(ct);
            if (DateTimeOffset.TryParse(last, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
                && time.GetUtcNow() - at < TimeSpan.FromHours(Math.Max(1, o.RefreshHours)))
                return null;
        }
        return await EnrichAsync(ct);
    }

    /// <summary>The most NVD lookups a refresh started by hand makes, so the page isn't held for minutes.</summary>
    public const int ManualNvdLookups = 5;

    /// <summary>An administrator refreshes the CVE data now (whether or not the schedule is on). NVD is limited to a few
    /// lookups; the scheduled run does the rest. Null when a refresh is already running.</summary>
    public Task<CveRun?> EnrichNowAsync(CancellationToken ct = default)
    {
        if (!user.Has(Permission.Administer)) throw new ForbiddenException("Only an administrator can refresh the CVE data.");
        return EnrichAsync(ct, ManualNvdLookups);
    }

    // One run at a time: one asked for meanwhile (by hand, or the schedule's) doesn't run, and is null.
    private async Task<CveRun?> EnrichAsync(CancellationToken ct, int? nvdCap = null)
    {
        if (!log.TryStart()) return null;
        try { return await RunAsync(nvdCap, ct); }
        finally { log.Finished(); }
    }

    private async Task<CveRun> RunAsync(int? nvdCap, CancellationToken ct)
    {
        var o = options.CurrentValue;
        var now = time.GetUtcNow();
        string? error = null;
        int kev = 0, epss = 0, nvd = 0;
        await using var db = await dbs.CreateAsync(ct);

        // The CVEs anyone looks at: on articles, and on feed items from the last two weeks.
        var since = now.AddDays(-14);
        var wanted = (await db.ArticleCves.AsNoTracking().Select(c => c.CveId).Distinct().ToListAsync(ct))
            .Concat((await db.FeedItems.AsNoTracking().Where(i => i.Cves != null && i.FetchedAtUtc >= since).Select(i => i.Cves!).ToListAsync(ct))
                .SelectMany(c => c.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
            .Select(c => c.ToUpperInvariant()).Distinct().ToList();
        var records = await db.CveRecords.ToDictionaryAsync(c => c.CveId, ct);
        CveRecord Record(string id)
        {
            if (!records.TryGetValue(id, out var r)) db.CveRecords.Add(records[id] = r = new CveRecord { CveId = id });
            return r;
        }

        // 1. KEV: the whole catalog, so any CVE that's on it says so.
        var kevFetch = await http.GetAsync(o.KevUrl, null, ct);
        if (kevFetch is { Status: FetchStatus.Ok, Content: { } kevBytes })
        {
            try
            {
                using var ms = new MemoryStream(kevBytes, writable: false);
                var entries = FeedParser.ParseKev(ms).DistinctBy(e => e.CveId).ToDictionary(e => e.CveId);
                foreach (var e in entries.Values)
                {
                    var r = Record(e.CveId);
                    r.IsKev = true;
                    r.KevAddedDate = e.Added;
                    r.KevDueDate = e.Due;
                    r.KevRansomware = e.Ransomware;
                    r.Vendor ??= Trim(e.Vendor, 200);
                    r.Product ??= Trim(e.Product, 200);
                    r.Description ??= Trim(e.Description, 2000);
                    r.KevCheckedAtUtc = now;
                    kev++;
                }
                foreach (var r in records.Values.Where(r => r.IsKev && !entries.ContainsKey(r.CveId))) r.IsKev = false;
            }
            catch (FormatException ex) { error ??= "KEV: " + ex.Message; }
        }
        else error ??= "KEV: " + (kevFetch.Error ?? "not fetched");

        // 2. EPSS, a hundred at a time.
        var refresh = TimeSpan.FromHours(Math.Max(1, o.RefreshHours));
        var epssDue = wanted.Where(id => Record(id).EpssCheckedAtUtc is not { } at || now - at >= refresh).ToList();
        foreach (var batch in epssDue.Chunk(100))
        {
            var fetch = await http.GetAsync($"{o.EpssUrl}?cve={string.Join(',', batch)}", null, ct);
            if (fetch is not { Status: FetchStatus.Ok, Content: { } bytes })
            {
                error ??= "EPSS: " + (fetch.Error ?? "not fetched");
                break;
            }
            try
            {
                using var ms = new MemoryStream(bytes, writable: false);
                var scores = CveParsers.ParseEpss(ms).DistinctBy(s => s.CveId).ToDictionary(s => s.CveId);
                foreach (var id in batch)
                {
                    var r = Record(id);
                    if (scores.TryGetValue(id, out var s)) { r.EpssScore = s.Score; r.EpssPercentile = s.Percentile; }
                    r.EpssCheckedAtUtc = now;
                    epss++;
                }
            }
            catch (FormatException ex) { error ??= "EPSS: " + ex.Message; break; }
        }

        // 3. NVD, one CVE per request, paced to its rate limit; missing first, then the stalest.
        var nvdDue = wanted.Where(id => Record(id).NvdCheckedAtUtc is not { } at || now - at >= TimeSpan.FromDays(7))
            .OrderBy(id => Record(id).NvdCheckedAtUtc ?? DateTimeOffset.MinValue).Take(Math.Min(nvdCap ?? int.MaxValue, Math.Clamp(o.MaxNvdPerRun, 0, 500))).ToList();
        var headers = string.IsNullOrWhiteSpace(o.NvdApiKey) ? null : new Dictionary<string, string> { ["apiKey"] = o.NvdApiKey.Trim() };
        var pause = TimeSpan.FromMilliseconds(o.NvdDelayMs ?? (headers is null ? 6500 : 700));
        // An answer about one CVE that can't be read is set aside, and marked checked (tried again in a week like any
        // other) once the next CVE reads fine, so it can't sit first in the queue and hold up the rest. Two in a row, or
        // an answer that isn't JSON at all (a proxy's or a rate limit's page), stop the run with nothing marked.
        string? unreadable = null;
        for (var i = 0; i < nvdDue.Count; i++)
        {
            if (i > 0 && pause > TimeSpan.Zero) await Task.Delay(pause, time, ct);
            var id = nvdDue[i];
            var fetch = await http.GetAsync($"{o.NvdUrl}?cveId={id}", new OutboundRequest(Headers: headers), ct);
            if (fetch is not { Status: FetchStatus.Ok, Content: { } bytes })
            {
                error ??= "NVD: " + (fetch.Error ?? "not fetched");
                break;
            }
            try
            {
                using var ms = new MemoryStream(bytes, writable: false);
                var r = Record(id);
                if (CveParsers.ParseNvd(ms) is { } found)
                {
                    r.Description = Trim(found.Description, 2000) ?? r.Description;
                    r.CvssScore = found.Score;
                    r.CvssSeverity = Trim(found.Severity, 16);
                    r.CvssVector = Trim(found.Vector, 200);
                    r.NvdPublishedAtUtc = found.Published;
                }
                r.NvdCheckedAtUtc = now;
                nvd++;
                if (unreadable is not null) Record(unreadable).NvdCheckedAtUtc = now;
                unreadable = null;
            }
            catch (FormatException ex)
            {
                error ??= $"NVD ({id}): " + ex.Message;
                if (unreadable is not null || !LooksLikeJson(bytes)) break;
                unreadable = id;
            }
        }
        if (unreadable is not null && unreadable == nvdDue[^1]) Record(unreadable).NvdCheckedAtUtc = now;   // the last one, alone

        var key = await db.Settings.FirstOrDefaultAsync(s => s.Key == LastRunKey, ct);
        if (key is null) db.Settings.Add(key = new AppSetting { Key = LastRunKey });
        key.Value = now.ToString("o", CultureInfo.InvariantCulture);
        key.UpdatedAtUtc = now;
        key.UpdatedBy = "system";
        await db.SaveChangesAsync(ct);
        return log.Last = new CveRun(now, kev, epss, nvd, error is null ? null : Trim(error, 300));
    }

    private static bool LooksLikeJson(byte[] body) =>
        body.SkipWhile(b => b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or 0xEF or 0xBB or 0xBF).FirstOrDefault() == (byte)'{';

    private static string? Trim(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Length <= max ? s.Trim() : s[..max].Trim();
}

/// <summary>Readers for the EPSS and NVD responses.</summary>
public static class CveParsers
{
    public sealed record EpssScore(string CveId, decimal Score, decimal Percentile);

    public sealed record NvdCve(string? Description, decimal? Score, string? Severity, string? Vector, DateTimeOffset? Published);

    /// <summary>FIRST's EPSS API: <c>{"data":[{"cve":"CVE-…","epss":"0.97","percentile":"0.99"}]}</c>.</summary>
    public static IReadOnlyList<EpssScore> ParseEpss(Stream content)
    {
        try
        {
            using var doc = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = 16 });
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) throw new FormatException("There's no data list.");
            return data.EnumerateArray()
                .Select(d => (Cve: Str(d, "cve"), Epss: Dec(d, "epss"), Pct: Dec(d, "percentile")))
                .Where(x => x.Cve is not null && x.Epss is not null)
                .Select(x => new EpssScore(x.Cve!.ToUpperInvariant(), x.Epss!.Value, x.Pct ?? 0)).ToList();
        }
        catch (JsonException ex) { throw new FormatException("The EPSS answer isn't valid JSON.", ex); }
    }

    /// <summary>NVD's CVE API 2.0 for one CVE: the English description and the newest CVSS (v4.0, then 3.1, 3.0, 2).</summary>
    public static NvdCve? ParseNvd(Stream content)
    {
        try
        {
            using var doc = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = 32 });
            if (!doc.RootElement.TryGetProperty("vulnerabilities", out var list) || list.ValueKind != JsonValueKind.Array) throw new FormatException("There's no vulnerabilities list.");
            if (list.GetArrayLength() == 0) return null;
            var cve = list[0].GetProperty("cve");
            var description = cve.TryGetProperty("descriptions", out var ds) && ds.ValueKind == JsonValueKind.Array
                ? ds.EnumerateArray().Where(d => Str(d, "lang") == "en").Select(d => Str(d, "value")).FirstOrDefault()
                : null;
            DateTimeOffset? published = DateTimeOffset.TryParse(Str(cve, "published"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var p) ? p : null;
            decimal? score = null;
            string? severity = null, vector = null;
            if (cve.TryGetProperty("metrics", out var metrics))
                foreach (var name in new[] { "cvssMetricV40", "cvssMetricV31", "cvssMetricV30", "cvssMetricV2" })
                {
                    if (!metrics.TryGetProperty(name, out var m) || m.ValueKind != JsonValueKind.Array || m.GetArrayLength() == 0) continue;
                    var chosen = m.EnumerateArray().FirstOrDefault(x => Str(x, "type") == "Primary");
                    if (chosen.ValueKind == JsonValueKind.Undefined) chosen = m[0];
                    if (!chosen.TryGetProperty("cvssData", out var data)) continue;
                    score = data.TryGetProperty("baseScore", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetDecimal() : null;
                    severity = Str(data, "baseSeverity") ?? Str(chosen, "baseSeverity");
                    vector = Str(data, "vectorString");
                    break;
                }
            return new NvdCve(description, score, severity, vector, published);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new FormatException("The NVD answer isn't in the expected shape.", ex);
        }
    }

    // An entry that isn't an object (a stray number in the list) reads as having nothing, not as a crash.
    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static decimal? Dec(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) ? p.ValueKind switch
    {
        JsonValueKind.Number => p.GetDecimal(),
        JsonValueKind.String when decimal.TryParse(p.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
        _ => null,
    } : null;
}
