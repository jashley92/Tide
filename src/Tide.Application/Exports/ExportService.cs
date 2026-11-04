using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Tide.Application.Abstractions;
using Tide.Application.Content;
using Tide.Application.Security;
using Tide.Domain.Entities;
using Tide.Domain.Enums;
using Tide.Domain.Observables;

namespace Tide.Application.Exports;

/// <summary>What can be exported: one article, a day's brief, or a story's articles.</summary>
public enum ExportScope
{
    Article = 0,
    Brief = 1,
    Story = 2,
}

/// <summary>The articles going into an export, with their indicators, CVEs and techniques, and the briefs each was on.</summary>
public sealed record ExportSet(string FileStem, IReadOnlyList<Article> Articles, IReadOnlyDictionary<Guid, IReadOnlyList<DateOnly>> BriefDates)
{
    /// <summary>An article's indicators, CVEs and techniques in the order every export writes them.</summary>
    public static IEnumerable<ArticleIndicator> Indicators(Article a) =>
        a.Indicators.OrderBy(i => i.Type).ThenBy(i => i.Value, StringComparer.OrdinalIgnoreCase);

    public static IEnumerable<string> Cves(Article a) => a.Cves.Select(c => c.CveId).Order(StringComparer.Ordinal);

    public static IEnumerable<string> Techniques(Article a) => a.Techniques.Select(t => t.TechniqueId).Order(StringComparer.Ordinal);
}

/// <summary>
/// Gathers an article, a brief or a story for export as indicators (CSV, STIX 2.1). An export carries the TLP of the
/// articles in it, so it takes a ceiling: articles marked above it are left out whole, as the editions leave them out.
/// </summary>
public sealed class ExportService(IAppDbContextFactory dbs, ICurrentUser user)
{
    public async Task<ExportSet?> GatherAsync(ExportScope scope, string key, TlpLevel ceiling, CancellationToken ct = default)
    {
        if (!user.Has(Permission.ReadTeamEdition)) throw new ForbiddenException("Exports are open to the team.");
        await using var db = await dbs.CreateAsync(ct);
        var articles = db.Articles.AsNoTracking().Include(a => a.Indicators).Include(a => a.Cves).Include(a => a.Techniques).AsSplitQuery();
        string stem;
        List<Article> rows;
        switch (scope)
        {
            case ExportScope.Article when Guid.TryParse(key, out var id):
                rows = await articles.Where(a => a.Id == id).ToListAsync(ct);
                if (rows.Count == 0) return null;
                stem = "article-" + Slug(rows[0].Title);
                break;
            case ExportScope.Brief when DateOnly.TryParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day):
                var order = await db.Briefs.AsNoTracking().Where(b => b.BriefDate == day)
                    .Select(b => b.Items.OrderBy(i => i.SortOrder).Select(i => i.ArticleId).ToList()).FirstOrDefaultAsync(ct);
                if (order is null) return null;
                var byId = (await articles.Where(a => order.Contains(a.Id)).ToListAsync(ct)).ToDictionary(a => a.Id);
                rows = order.Where(byId.ContainsKey).Select(i => byId[i]).ToList();
                stem = $"brief-{day:yyyy-MM-dd}";
                break;
            case ExportScope.Story when Guid.TryParse(key, out var id):
                var name = await db.Stories.AsNoTracking().Where(s => s.Id == id).Select(s => s.Name).FirstOrDefaultAsync(ct);
                if (name is null) return null;
                rows = await articles.Where(a => a.StoryId == id).OrderBy(a => a.CreatedAtUtc).ToListAsync(ct);
                stem = "story-" + Slug(name);
                break;
            default:
                return null;
        }

        var kept = rows.Where(a => a.Tlp <= ceiling).ToList();
        return new ExportSet(stem, kept, await Search.Lookups.BriefDatesAsync(db, kept.Select(a => a.Id).ToList(), ct));
    }

    /// <summary>A title as a short file-name part: lower-case letters and digits joined by hyphens, at most 40.</summary>
    public static string Slug(string text)
    {
        var sb = new StringBuilder();
        foreach (var c in text.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c)) sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
            if (sb.Length >= 40) break;
        }
        return sb.ToString().Trim('-') is { Length: > 0 } s ? s : "export";
    }
}

/// <summary>Indicators as CSV, one row per indicator per article, with the article's context.</summary>
public static class IndicatorCsv
{
    public static string Write(ExportSet set, string? baseUrl)
    {
        var root = string.IsNullOrWhiteSpace(baseUrl) ? null : baseUrl.TrimEnd('/');
        var sb = new StringBuilder("type,value,defanged,note,tlp,article,article_url,source_url,written_utc,briefed,cves,techniques\r\n");
        foreach (var a in set.Articles)
        {
            var cves = string.Join(' ', ExportSet.Cves(a));
            var techniques = string.Join(' ', ExportSet.Techniques(a));
            var briefed = string.Join(' ', (set.BriefDates.GetValueOrDefault(a.Id) ?? []).Select(d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
            foreach (var i in ExportSet.Indicators(a))
            {
                sb.AppendJoin(',',
                    Csv.Cell(Stix.TypeKey(i.Type)), Csv.Cell(i.Value), Csv.Cell(IocObservable.Defang(i.Type, i.Value)), Csv.Cell(i.Note),
                    Csv.Cell(Tlp.Label(a.Tlp)), Csv.Cell(a.Title), Csv.Cell(root is null ? null : $"{root}/articles/{a.Id}"),
                    Csv.Cell(a.SourceUrl), a.CreatedAtUtc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                    briefed, cves, techniques);
                sb.Append("\r\n");
            }
        }
        return sb.ToString();
    }
}
