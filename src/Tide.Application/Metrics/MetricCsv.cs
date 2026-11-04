using System.Text;

namespace Tide.Application.Metrics;

/// <summary>One data row of a metrics CSV, as text. <see cref="Row"/> is its row number as a spreadsheet shows it (the
/// header is row 1). <see cref="Note"/> is null when the file has no note column.</summary>
public sealed record MetricCsvRow(int Row, string Date, string Key, string Value, string? Note);

/// <summary>A metrics CSV read: its rows, or why the whole file was refused.</summary>
public sealed record MetricCsvFile(IReadOnlyList<MetricCsvRow> Rows, bool HasNotes, string? Refusal)
{
    public static MetricCsvFile Refused(string why) => new([], false, why);
}

/// <summary>
/// Reads a metrics CSV (RFC 4180: comma-separated, cells quoted with double quotes when they hold a comma, quote or line
/// break). The first row names the columns: <c>date</c>, <c>metric_key</c> and <c>value</c> are needed, <c>note</c> is
/// optional and any other column is ignored, so the metrics page's own export reads back in. Text is UTF-8 (with or
/// without a byte-order mark); a file that isn't valid UTF-8 is read as Latin-1, which is what a spreadsheet's plain
/// "CSV" usually is.
/// </summary>
public static class MetricCsv
{
    /// <summary>The largest file read (1 MB).</summary>
    public const int MaxBytes = 1024 * 1024;

    /// <summary>The most data rows read.</summary>
    public const int MaxRows = 10_000;

    public const string ColumnsHelp = "date, metric_key and value (note is optional)";

    public static MetricCsvFile Read(byte[] bytes)
    {
        if (bytes.Length > MaxBytes) return MetricCsvFile.Refused($"The file is larger than {MaxBytes / 1024 / 1024} MB.");
        if (Array.IndexOf(bytes, (byte)0) >= 0) return MetricCsvFile.Refused("The file isn't text (it looks like a spreadsheet or other binary file; save it as CSV).");
        string text;
        try { text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes); }
        catch (DecoderFallbackException) { text = Encoding.Latin1.GetString(bytes); }
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];

        var records = new List<(int Row, List<string> Cells)>();
        if (Split(text, records) is { } problem) return MetricCsvFile.Refused(problem);
        if (records.Count == 0) return MetricCsvFile.Refused("The file is empty.");

        var header = records[0].Cells.Select(h => h.Trim().ToLowerInvariant().Replace(' ', '_')).ToList();
        int date = header.IndexOf("date"), key = header.IndexOf("metric_key"), value = header.IndexOf("value"), note = header.IndexOf("note");
        var missing = new[] { ("date", date), ("metric_key", key), ("value", value) }.Where(c => c.Item2 < 0).Select(c => c.Item1).ToList();
        if (missing.Count > 0)
            return MetricCsvFile.Refused($"The first row must name the columns {ColumnsHelp}; it has no {string.Join(" or ", missing)} column.");
        if (records.Count - 1 > MaxRows) return MetricCsvFile.Refused($"The file has more than {MaxRows:N0} rows; split it.");

        string Cell(List<string> cells, int i) => i < cells.Count ? cells[i].Trim() : "";
        var rows = records.Skip(1)
            .Where(r => r.Cells.Any(c => c.Trim().Length > 0))
            .Select(r => new MetricCsvRow(r.Row, Cell(r.Cells, date), Cell(r.Cells, key), Cell(r.Cells, value),
                note < 0 ? null : Unguard(Cell(r.Cells, note))))
            .ToList();
        return new MetricCsvFile(rows, note >= 0, null);
    }

    // The export puts an apostrophe before a cell a spreadsheet would read as a formula (Csv.Cell); take it off again.
    private static string Unguard(string cell) =>
        cell.Length > 1 && cell[0] == '\'' && cell[1] is '=' or '+' or '-' or '@' or '\t' or '\r' ? cell[1..] : cell;

    /// <summary>Splits the text into records of cells; returns why it can't, or null.</summary>
    internal static string? Split(string text, List<(int Row, List<string> Cells)> records)
    {
        var cells = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        var row = 1;
        var any = false;   // whether the current record has begun
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c != '"') { cell.Append(c); continue; }
                if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; continue; }
                quoted = false;
                continue;
            }
            switch (c)
            {
                case '"' when cell.Length == 0:
                    quoted = true;
                    any = true;
                    break;
                case ',':
                    cells.Add(cell.ToString());
                    cell.Clear();
                    any = true;
                    break;
                case '\r' or '\n':
                    if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                    if (any || cell.Length > 0) { cells.Add(cell.ToString()); records.Add((row, cells)); }
                    cells = [];
                    cell.Clear();
                    any = false;
                    row++;
                    if (records.Count > MaxRows + 1) return null;   // the caller refuses it by count
                    break;
                default:
                    cell.Append(c);
                    any = true;
                    break;
            }
        }
        if (quoted) return $"A quoted cell that starts in row {row} isn't closed.";
        if (any || cell.Length > 0) { cells.Add(cell.ToString()); records.Add((row, cells)); }
        return null;
    }
}
