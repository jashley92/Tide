using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Tide.Application.Abstractions;
using Tide.Application.Org;
using Tide.Application.Security;
using Tide.Domain.Entities;
using Tide.Domain.Enums;

namespace Tide.Application.Metrics;

/// <summary>Metric import settings (config section <c>Metrics:Import</c>).</summary>
public sealed class MetricImportOptions
{
    /// <summary>Whether CSV files left in the drop folder (<c>metrics-inbox</c> under <c>Files:RootPath</c>) are read.
    /// Off by default.</summary>
    public bool DropFolderEnabled { get; set; }

    /// <summary>How often the drop folder is looked at, in minutes (1 to 60).</summary>
    public int PollMinutes { get; set; } = 5;
}

/// <summary>
/// Reads the opening metrics' values from CSV (ADR 0014), from the drop folder or an administrator's upload. Each row
/// sets one metric on one business day, keyed by the metric's key and the date, so reading a file twice changes nothing.
/// An import never overwrites what a person decided: a value an analyst typed (or corrected) is kept, and a day whose
/// brief has been sent is left as it went out. Rows that can't be applied are counted and explained; the rest apply.
/// </summary>
public sealed class MetricImportService(IAppDbContextFactory dbs, ICurrentUser user, OrgClock clock, TimeProvider time)
{
    /// <summary>Problems kept on an import's record.</summary>
    public const int MaxProblems = 50;

    public const string DropFolderName = "Drop folder";

    // Imports run one at a time in the process, so an upload and the drop folder never race over the same values.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>Imports a file an administrator uploaded.</summary>
    public Task<MetricImport> UploadAsync(string fileName, byte[] bytes, CancellationToken ct = default)
    {
        if (!user.Has(Permission.Administer)) throw new ForbiddenException("Only an administrator can import metrics.");
        return ImportAsync(fileName, bytes, MetricImportOrigin.Upload, user.DisplayName, ct);
    }

    /// <summary>Imports a file picked up from the drop folder. Only the background service calls this (no one is signed
    /// in there); a signed-in caller is refused.</summary>
    public Task<MetricImport> ImportDroppedAsync(string fileName, byte[] bytes, CancellationToken ct = default)
    {
        if (user.IsAuthenticated) throw new ForbiddenException("Files in the drop folder are read by TIDE itself.");
        return ImportAsync(fileName, bytes, MetricImportOrigin.DropFolder, DropFolderName, ct);
    }

    /// <summary>The latest imports, newest first.</summary>
    public async Task<IReadOnlyList<MetricImport>> RecentAsync(int take = 25, CancellationToken ct = default)
    {
        if (!user.Has(Permission.Administer)) throw new ForbiddenException("Only an administrator can see metric imports.");
        await using var db = await dbs.CreateAsync(ct);
        return await db.MetricImports.AsNoTracking().OrderByDescending(i => i.ReadAtUtc).Take(Math.Clamp(take, 1, 200)).ToListAsync(ct);
    }

    private async Task<MetricImport> ImportAsync(string fileName, byte[] bytes, MetricImportOrigin origin, string byName, CancellationToken ct)
    {
        var file = MetricCsv.Read(bytes);
        await Gate.WaitAsync(ct);
        try
        {
            // A value an analyst types at the same moment can win the unique (metric, day) index; read again and retry once.
            for (var attempt = 0; ; attempt++)
            {
                try { return await ApplyAsync(Clean(fileName), file, origin, byName, ct); }
                catch (DbUpdateException) when (attempt == 0) { }
            }
        }
        finally { Gate.Release(); }
    }

    private async Task<MetricImport> ApplyAsync(string fileName, MetricCsvFile file, MetricImportOrigin origin, string byName, CancellationToken ct)
    {
        var record = new MetricImport
        {
            FileName = fileName, Origin = origin, ReadAtUtc = time.GetUtcNow(), ImportedByName = Trim(byName, 256), Rows = file.Rows.Count,
        };
        await using var db = await dbs.CreateAsync(ct);
        db.MetricImports.Add(record);
        if (file.Refusal is { } refusal)
        {
            record.Rejected = true;
            record.Problems = refusal;
            await db.SaveChangesAsync(ct);
            return record;
        }

        var metrics = (await db.MetricDefinitions.ToListAsync(ct)).ToDictionary(m => m.Key, StringComparer.OrdinalIgnoreCase);
        var dates = file.Rows.Select(r => ParseDate(r.Date)).OfType<DateOnly>().Distinct().ToList();
        var sent = (await db.Briefs.Where(b => b.Status == BriefStatus.Sent && dates.Contains(b.BriefDate)).Select(b => b.BriefDate).ToListAsync(ct)).ToHashSet();
        var existing = (await db.MetricValues.Where(v => dates.Contains(v.BriefDate)).ToListAsync(ct))
            .ToDictionary(v => (v.DefinitionId, v.BriefDate));
        var today = clock.Today;
        var seen = new Dictionary<(Guid, DateOnly), int>();
        var problems = new List<string>();
        var name = Trim("Import: " + fileName, 256);

        foreach (var row in file.Rows)
        {
            void Skip(string why)
            {
                record.Skipped++;
                problems.Add($"Row {row.Row}: {why}");
            }

            if (ParseDate(row.Date) is not { } day) { Skip($"'{Show(row.Date)}' isn't a date in the form 2026-10-28."); continue; }
            if (!metrics.TryGetValue(row.Key, out var metric)) { Skip(row.Key.Length == 0 ? "no metric_key." : $"no metric has the key '{Show(row.Key)}'."); continue; }
            var label = $"{metric.Name} on {day:yyyy-MM-dd}";
            if (metric.IsRetired) { Skip($"{metric.Name} is retired."); continue; }
            if (row.Value.Length == 0) { Skip($"{label} has no value."); continue; }
            if (!MetricMath.TryParseValue(row.Value, out var parsed)) { Skip($"'{Show(row.Value)}' isn't a number TIDE can keep."); continue; }
            if (WorkingDays.IsWeekend(day)) { Skip($"{day:yyyy-MM-dd} is a weekend; metrics are kept for business days."); continue; }
            if (day > today) { Skip($"{day:yyyy-MM-dd} hasn't happened yet."); continue; }
            if (sent.Contains(day)) { Skip($"the brief for {day:yyyy-MM-dd} has been sent, so its numbers are fixed."); continue; }
            if (seen.TryGetValue((metric.Id, day), out var first)) { Skip($"{label} is also in row {first}, which was used."); continue; }
            seen[(metric.Id, day)] = row.Row;

            var value = Math.Round(parsed, 4);
            // A note too long for its column skips the row like any other bad cell; it mustn't throw and abort the file.
            var note = file.HasNotes ? Articles.ArticleService.TidyOrNull(row.Note) : null;
            if (note is { Length: > 300 }) { Skip($"{label} has a note longer than 300 characters."); continue; }
            if (existing.TryGetValue((metric.Id, day), out var current))
            {
                var sameNote = !file.HasNotes || current.Note == note;
                if (current.Value == value && sameNote) { record.Unchanged++; continue; }
                if (current.Source == MetricSource.Manual)
                {
                    Skip($"{label} was entered by {current.EnteredByName} ({MetricMath.Format(metric.Format, current.Value)}), so it was kept.");
                    continue;
                }
            }
            else db.MetricValues.Add(current = new MetricValue { DefinitionId = metric.Id, BriefDate = day });
            current.Value = value;
            if (file.HasNotes) current.Note = note;
            current.Source = MetricSource.Imported;
            current.ImportId = record.Id;
            current.EnteredById = "import";
            current.EnteredByName = name;
            record.Applied++;
        }

        record.Problems = Summarize(problems);
        await db.SaveChangesAsync(ct);
        return record;
    }

    // The skipped rows, for the import's record: the first MaxProblems, within the column's 4000 characters.
    private static string? Summarize(List<string> problems)
    {
        if (problems.Count == 0) return null;
        var text = string.Join('\n', problems.Take(MaxProblems))
                   + (problems.Count > MaxProblems ? $"\n…and {problems.Count - MaxProblems} more." : "");
        return text.Length > 4000 ? text[..3999] + "…" : text;
    }

    private static DateOnly? ParseDate(string text) =>
        DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    // A cell quoted back in a problem: one line, and short.
    private static string Show(string text) => Trim(Content.MarkdownService.OneLine(text), 40);

    private static string Trim(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    private static string Clean(string fileName)
    {
        var name = Path.GetFileName(fileName ?? "");
        name = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return name.Length == 0 ? "metrics.csv" : Trim(name, 260);
    }
}
