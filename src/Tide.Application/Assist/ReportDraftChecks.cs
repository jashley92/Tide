using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Tide.Application.Assist;

/// <summary>
/// A drafted piece of prose (a report section's assessment, a title slide line) that passed
/// <see cref="ReportDraftChecks"/>, with the numbers it states that the material didn't give, as written. Each of those
/// is flagged for the person to check before using the draft.
/// </summary>
public sealed record CheckedProse(string Text, IReadOnlyList<string> UnknownNumbers);

/// <summary>
/// The checks on a drafted assessment or title slide line, the same for a provider's answer and a pasted one (ADR 0004).
/// The text is trimmed, cut to its field's limit, and Markdown links are reduced to their text, as an article draft's are.
/// Every number it states must appear in the material it was drafted from: a model that adds up, rounds or invents a
/// figure is caught here, since the report's whole point is its numbers.
/// </summary>
public static partial class ReportDraftChecks
{
    /// <summary>
    /// The answer's text in <paramref name="field"/> ("assessment", "line"), checked against <paramref name="input"/>, the
    /// material as sent. <paramref name="oneLine"/> joins its lines. An answer that isn't the JSON asked for, or says
    /// nothing, is refused.
    /// </summary>
    public static CheckedProse Check(string json, string field, int max, string input, bool oneLine = false)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(DraftChecks.Unwrapped(json), new JsonDocumentOptions { AllowTrailingCommas = true }); }
        catch (JsonException) { throw new ArgumentException("The answer isn't the JSON TIDE asked for. Run the prompt again, or check it was pasted whole."); }
        using (doc)
        {
            var root = doc.RootElement;
            var raw = root.ValueKind == JsonValueKind.Object && root.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            if (oneLine && raw is not null) raw = string.Join(' ', raw.Split((char[])['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
            var text = DraftChecks.Text(raw, max) ?? throw new ArgumentException("The answer has no text to use. Run the prompt again.");
            return new CheckedProse(text, Numbers(text, input));
        }
    }

    /// <summary>
    /// The numbers <paramref name="answer"/> states that <paramref name="input"/> doesn't, as the answer writes them, each
    /// once, in order. Numbers compare by value: "1,204" matches "1204" and "12.0" matches "12". Only figures written in
    /// digits are read; a number spelled out ("three") isn't. A number inside an identifier (CVE-2026-1281, T1190) counts
    /// too, so an identifier the material doesn't have is flagged as well.
    /// </summary>
    public static IReadOnlyList<string> Numbers(string answer, string input)
    {
        var known = Values(input).Select(x => x.Value).ToHashSet();
        return Values(answer).Where(x => !known.Contains(x.Value)).Select(x => x.Written).Distinct(StringComparer.Ordinal).ToList();
    }

    private static IEnumerable<(string Written, decimal Value)> Values(string text)
    {
        foreach (Match m in Number().Matches(text))
            if (decimal.TryParse(m.Value.Replace(",", "", StringComparison.Ordinal), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
                yield return (m.Value, value);
    }

    // Digits with thousands separators ("1,204"), or plain, either with a decimal part ("12.5"). A comma not followed by
    // exactly three digits ends the number, so a list ("3,4") reads as two.
    [GeneratedRegex(@"\d{1,3}(?:,\d{3})+(?:\.\d+)?(?!\d)|\d+(?:\.\d+)?", RegexOptions.CultureInvariant)]
    private static partial Regex Number();
}
