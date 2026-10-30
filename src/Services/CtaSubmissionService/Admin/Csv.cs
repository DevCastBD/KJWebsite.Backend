using System.Text;
using System.Text.RegularExpressions;

namespace CtaSubmissionService.Admin;

/// <summary>
/// RFC 4180 CSV that opens correctly in Excel: UTF-8 with a BOM (without it Bengali shows as mojibake) and CRLF rows.
/// Cells that Excel would run as a formula are neutralised.
/// </summary>
public static partial class Csv
{
    public static readonly byte[] Preamble = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetPreamble();

    [GeneratedRegex(@"^[+-]?[\d\s().\-]+$")]
    private static partial Regex NumberLike();

    public static byte[] Build(IEnumerable<string> header, IEnumerable<IEnumerable<string?>> rows)
    {
        var text = new StringBuilder();
        AppendRow(text, header);
        foreach (var row in rows)
        {
            AppendRow(text, row);
        }

        return [.. Preamble, .. new UTF8Encoding(false).GetBytes(text.ToString())];
    }

    public static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (IsFormula(value))
        {
            value = "'" + value;
        }

        return value.IndexOfAny(['"', ',', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
    }

    // A leading = @ tab or CR always; + or - unless the cell is just a number or phone number.
    private static bool IsFormula(string value) =>
        value[0] is '=' or '@' or '\t' or '\r' || (value[0] is '+' or '-' && !NumberLike().IsMatch(value));

    private static void AppendRow(StringBuilder text, IEnumerable<string?> cells) =>
        text.AppendJoin(',', cells.Select(Escape)).Append("\r\n");
}
