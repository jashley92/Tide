namespace Tide.Application.Content;

/// <summary>CSV cells for exports a person opens in a spreadsheet.</summary>
public static class Csv
{
    /// <summary>
    /// A cell: quoted when it holds a comma, quote or line break, and with a leading apostrophe when a spreadsheet would
    /// read it as a formula (=, +, -, @, tab, carriage return).
    /// </summary>
    public static string Cell(string? text)
    {
        var t = text ?? "";
        if (t.Length > 0 && t[0] is '=' or '+' or '-' or '@' or '\t' or '\r') t = "'" + t;
        return t.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + t.Replace("\"", "\"\"") + "\"" : t;
    }
}
