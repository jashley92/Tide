using System.Globalization;

namespace Tide.Application.Content;

/// <summary>CSV cells for exports a person opens in a spreadsheet.</summary>
public static class Csv
{
    /// <summary>
    /// A cell: quoted when it holds a comma, quote or line break, and with a leading apostrophe when a spreadsheet would
    /// read it as a formula (=, +, -, @, tab, carriage return). With <paramref name="numbersAsIs"/>, a number keeps its
    /// leading minus or plus without one: a spreadsheet reads it as the number it is.
    /// </summary>
    public static string Cell(string? text, bool numbersAsIs = false)
    {
        var t = text ?? "";
        if (t.Length > 0 && IsFormulaStart(t[0])
            && !(numbersAsIs && double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out _))) t = "'" + t;
        return t.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + t.Replace("\"", "\"\"") + "\"" : t;
    }

    /// <summary>Whether a cell starting with <paramref name="c"/> would be read as a formula by a spreadsheet, so
    /// <see cref="Cell"/> puts an apostrophe before it.</summary>
    internal static bool IsFormulaStart(char c) => c is '=' or '+' or '-' or '@' or '\t' or '\r';
}
