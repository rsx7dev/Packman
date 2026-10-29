using System.Text;

namespace Packman.Helpers;

/// <summary>
/// RFC 4180 CSV for spreadsheets: CRLF rows, quotes doubled, fields with a comma, quote or
/// line break quoted. Text that Excel would run as a formula gets a leading apostrophe.
/// </summary>
public static class CsvWriter
{
    /// <summary>UTF-8 with a BOM, so Excel reads accented names correctly.</summary>
    public static readonly Encoding Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    public static string Field(string? value)
    {
        var text = value ?? "";
        if (text.Length > 0 && text[0] is '=' or '+' or '-' or '@' or '\t')
            text = "'" + text;
        return text.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? "\"" + text.Replace("\"", "\"\"") + "\""
            : text;
    }

    public static string Build(IEnumerable<IEnumerable<string?>> rows)
    {
        var sb = new StringBuilder();
        foreach (var row in rows)
            sb.Append(string.Join(",", row.Select(Field))).Append("\r\n");
        return sb.ToString();
    }
}
