using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Tide.Application.Content;
using Tide.Application.Metrics;
using Tide.Domain.Entities;

namespace Tide.Application.Detections;

/// <summary>A rule as read from a file or the API, before it's stored.</summary>
public sealed record ParsedRule(string ExternalId, string Name, IReadOnlyList<string> Techniques, string? Platform, bool Enabled);

/// <summary>
/// Reads detection rules from what the SOC already has (EP-79): a Microsoft Sentinel analytics-rules export (ARM
/// template JSON), Sigma rules (YAML), or a CSV. Only the name, id, technique tags, platform and enabled state are read;
/// queries are ignored. A zip of files is read entry by entry, within limits.
/// </summary>
public static partial class DetectionParsers
{
    /// <summary>The largest upload read (8 MB).</summary>
    public const int MaxUploadBytes = 8 * 1024 * 1024;

    /// <summary>The most files read from one zip.</summary>
    public const int MaxZipEntries = 5000;

    /// <summary>The most bytes unpacked from one zip (32 MB), and from one file in it (1 MB).</summary>
    public const long MaxUnzippedBytes = 32L * 1024 * 1024, MaxEntryBytes = 1024 * 1024;

    // An entry that unpacks to more than this many times its packed size is refused (a zip bomb).
    private const int MaxRatio = 100;

    public const string CsvColumnsHelp = "name and techniques (id, platform and enabled are optional)";

    [GeneratedRegex(@"^T\d{4}(\.\d{3})?$", RegexOptions.CultureInvariant)]
    private static partial Regex TechniqueId();

    [GeneratedRegex(@"^attack\.(t\d{4}(?:\.\d{3})?)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SigmaTag();

    [GeneratedRegex(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", RegexOptions.CultureInvariant)]
    private static partial Regex Guid();

    /// <summary>A technique id in its canonical form ("t1059.001" → "T1059.001"), or null when it isn't one.</summary>
    public static string? Technique(string? text)
    {
        var t = (text ?? "").Trim().ToUpperInvariant();
        return TechniqueId().IsMatch(t) ? t : null;
    }

    /// <summary>The files to read: the upload itself, or each entry of a zip with one of <paramref name="extensions"/>.</summary>
    public static IReadOnlyList<(string Name, string Text)> Files(string fileName, byte[] bytes, IReadOnlyCollection<string> extensions, List<string> problems)
    {
        if (bytes.Length > MaxUploadBytes) throw new ArgumentException($"The file is larger than {MaxUploadBytes / 1024 / 1024} MB.");
        if (!fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return [(fileName, Text(bytes))];
        var files = new List<(string, string)>();
        long total = 0;
        try
        {
            using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            if (zip.Entries.Count > MaxZipEntries) throw new ArgumentException($"The zip has more than {MaxZipEntries:N0} files.");
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.EndsWith('/') || !extensions.Any(e => entry.FullName.EndsWith(e, StringComparison.OrdinalIgnoreCase))) continue;
                if (entry.Length > MaxEntryBytes || (entry.CompressedLength > 0 && entry.Length / entry.CompressedLength > MaxRatio))
                {
                    problems.Add($"{entry.FullName}: skipped (too large).");
                    continue;
                }
                total += entry.Length;
                if (total > MaxUnzippedBytes) throw new ArgumentException($"The zip unpacks to more than {MaxUnzippedBytes / 1024 / 1024} MB.");
                using var stream = entry.Open();
                using var copy = new MemoryStream();
                stream.CopyTo(copy, 81920);
                files.Add((entry.FullName, Text(copy.ToArray())));
            }
        }
        catch (InvalidDataException) { throw new ArgumentException("The zip can't be read."); }
        return files;
    }

    private static string Text(byte[] bytes)
    {
        string text;
        try { text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes); }
        catch (DecoderFallbackException) { text = Encoding.Latin1.GetString(bytes); }
        return text.Length > 0 && text[0] == '﻿' ? text[1..] : text;
    }

    // --- Sentinel ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// The analytics rules in a Sentinel export: an ARM template's <c>resources</c>, an array of them, or one rule. A rule's
    /// techniques are its <c>techniques</c> and <c>subTechniques</c>.
    /// </summary>
    public static IReadOnlyList<ParsedRule> Sentinel(string fileName, string json, List<string> problems)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }); }
        catch (JsonException)
        {
            problems.Add($"{fileName}: not JSON.");
            return [];
        }
        using (doc)
        {
            var root = doc.RootElement;
            IEnumerable<JsonElement> resources = root.ValueKind switch
            {
                JsonValueKind.Array => root.EnumerateArray(),
                JsonValueKind.Object when root.TryGetProperty("resources", out var r) && r.ValueKind == JsonValueKind.Array => r.EnumerateArray(),
                JsonValueKind.Object => [root],
                _ => [],
            };
            var rules = new List<ParsedRule>();
            foreach (var res in resources)
            {
                if (res.ValueKind != JsonValueKind.Object || !res.TryGetProperty("properties", out var p) || p.ValueKind != JsonValueKind.Object) continue;
                var type = Str(res, "type") ?? "";
                if (type.Length > 0 && !type.Contains("alertRules", StringComparison.OrdinalIgnoreCase)) continue;
                var name = Str(p, "displayName");
                if (string.IsNullOrWhiteSpace(name)) continue;
                var techniques = Strings(p, "techniques").Concat(Strings(p, "subTechniques")).ToList();
                var resourceName = Str(res, "name") ?? "";
                var id = resourceName.StartsWith('[') ? Guid().Match(resourceName) is { Success: true } g ? g.Value : name : resourceName.Length > 0 ? resourceName : name;
                var kind = Str(res, "kind");
                rules.Add(Rule(id, name, techniques, kind is null ? "Sentinel" : $"Sentinel {kind}", p.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.False ? false : true, problems));
            }
            if (rules.Count == 0) problems.Add($"{fileName}: no analytics rules found.");
            return rules;
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static IEnumerable<string> Strings(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!)
            : [];

    // --- Sigma ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The Sigma rules in a YAML file (one or more documents): the title, id, status, <c>attack.tNNNN</c> tags and the
    /// log source's product and service. A deprecated or unsupported rule counts as disabled. Only these top-level keys
    /// are read, so this isn't a general YAML reader.
    /// </summary>
    public static IReadOnlyList<ParsedRule> Sigma(string fileName, string yaml, List<string> problems)
    {
        var rules = new List<ParsedRule>();
        foreach (var document in Documents(yaml))
        {
            string? title = null, id = null, status = null, product = null, service = null;
            var tags = new List<string>();
            string? block = null;
            foreach (var raw in document)
            {
                var line = raw.TrimEnd();
                if (line.Length == 0 || line.TrimStart().StartsWith('#')) continue;
                var indented = char.IsWhiteSpace(line[0]);
                var text = line.Trim();
                if (!indented)
                {
                    var (key, value) = KeyValue(text);
                    block = key;
                    switch (key)
                    {
                        case "title": title = value; break;
                        case "id": id = value; break;
                        case "status": status = value; break;
                        case "tags" when value is { Length: > 0 } inline:
                            tags.AddRange(inline.Trim('[', ']').Split(',').Select(Unquote));
                            break;
                    }
                    continue;
                }
                if (block == "tags" && text.StartsWith("- ", StringComparison.Ordinal)) tags.Add(Unquote(text[2..]));
                else if (block == "logsource")
                {
                    var (key, value) = KeyValue(text);
                    if (key == "product") product = value;
                    else if (key == "service") service = value;
                }
            }
            if (string.IsNullOrWhiteSpace(title)) continue;
            var techniques = tags.Select(t => SigmaTag().Match(t.Trim())).Where(m => m.Success).Select(m => m.Groups[1].Value).ToList();
            var platform = string.Join(" / ", new[] { product, service }.Where(x => !string.IsNullOrWhiteSpace(x)));
            var enabled = status is null || !(status.Equals("deprecated", StringComparison.OrdinalIgnoreCase) || status.Equals("unsupported", StringComparison.OrdinalIgnoreCase));
            rules.Add(Rule(string.IsNullOrWhiteSpace(id) ? title : id, title, techniques, platform.Length == 0 ? "Sigma" : platform, enabled, problems));
        }
        if (rules.Count == 0) problems.Add($"{fileName}: no Sigma rule (a title) found.");
        return rules;
    }

    private static IEnumerable<List<string>> Documents(string yaml)
    {
        var current = new List<string>();
        foreach (var line in yaml.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.TrimEnd() == "---")
            {
                if (current.Count > 0) yield return current;
                current = [];
                continue;
            }
            current.Add(line);
        }
        if (current.Count > 0) yield return current;
    }

    private static (string Key, string? Value) KeyValue(string text)
    {
        var colon = text.IndexOf(':');
        if (colon < 0) return (text, null);
        var value = text[(colon + 1)..].Trim();
        return (text[..colon].Trim(), value.Length == 0 ? null : Unquote(value));
    }

    private static string Unquote(string s)
    {
        s = s.Trim();
        return s.Length >= 2 && (s[0] == '\'' || s[0] == '"') && s[^1] == s[0] ? s[1..^1] : s;
    }

    // --- CSV --------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Rules from a CSV with a header row naming <see cref="CsvColumnsHelp"/>. Techniques in a cell are separated by
    /// semicolons, commas or spaces; enabled is yes/no, true/false or 1/0 (yes when blank).
    /// </summary>
    public static IReadOnlyList<ParsedRule> Csv(string fileName, string text, List<string> problems)
    {
        var records = new List<(int Row, List<string> Cells)>();
        if (MetricCsv.Split(text, records) is { } problem) throw new ArgumentException(problem);
        if (records.Count == 0) throw new ArgumentException("The file is empty.");
        // Split stops reading past MetricCsv.MaxRows: a longer file is refused, not stored in part (the rules it didn't
        // read would stop counting, and Coverage would show gaps for what they cover).
        if (records.Count - 1 > MetricCsv.MaxRows) throw new ArgumentException($"The file has more than {MetricCsv.MaxRows:N0} rows; split it, or zip several files.");
        var header = records[0].Cells.Select(h => h.Trim().ToLowerInvariant().Replace(' ', '_')).ToList();
        int name = header.IndexOf("name"), techniques = header.IndexOf("techniques"), id = header.IndexOf("id"),
            platform = header.IndexOf("platform"), enabled = header.IndexOf("enabled");
        if (name < 0 || techniques < 0) throw new ArgumentException($"The first row must name the columns {CsvColumnsHelp}.");
        string Cell(List<string> cells, int i) => i >= 0 && i < cells.Count ? cells[i].Trim() : "";
        var rules = new List<ParsedRule>();
        foreach (var (row, cells) in records.Skip(1))
        {
            var n = Cell(cells, name);
            if (n.Length == 0)
            {
                if (cells.Any(c => c.Trim().Length > 0)) problems.Add($"{fileName} row {row}: no name.");
                continue;
            }
            var on = Cell(cells, enabled).ToLowerInvariant();
            if (on is not ("" or "yes" or "y" or "true" or "1" or "no" or "n" or "false" or "0"))
            {
                problems.Add($"{fileName} row {row}: enabled “{Cell(cells, enabled)}” isn't yes or no; read as yes.");
                on = "";
            }
            var platformCell = Cell(cells, platform);
            rules.Add(Rule(Cell(cells, id) is { Length: > 0 } i ? i : n, n, Cell(cells, techniques).Split([';', ',', ' '], StringSplitOptions.RemoveEmptyEntries),
                platformCell.Length == 0 ? null : platformCell, on is not ("no" or "n" or "false" or "0"), problems));
        }
        return rules;
    }

    // --- shared -----------------------------------------------------------------------------------------------------

    /// <summary>A rule with its names cut to size and its technique ids checked (ones that aren't are noted and left out).</summary>
    public static ParsedRule Rule(string id, string name, IEnumerable<string> techniques, string? platform, bool enabled, List<string> problems)
    {
        var good = new List<string>();
        foreach (var t in techniques)
        {
            if (Technique(t) is { } ok) { if (!good.Contains(ok)) good.Add(ok); }
            else if (!string.IsNullOrWhiteSpace(t)) problems.Add($"{Words.Clip(name, 60)}: “{Words.Clip(t.Trim(), 20)}” isn't a technique id.");
        }
        return new ParsedRule(Words.Clip(id.Trim(), DetectionRule.MaxExternalId), Words.Clip(name.Trim(), DetectionRule.MaxName), good,
            platform is null ? null : Words.Clip(platform.Trim(), DetectionRule.MaxPlatform), enabled);
    }
}
