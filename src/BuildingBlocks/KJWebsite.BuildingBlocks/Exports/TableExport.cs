using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;

namespace KJWebsite.BuildingBlocks.Exports;

/// <summary>A named grid, the unit every report is exported as. Cells are strings, decimals (money: numbers in XLSX, two-decimal text in CSV), ints or null.</summary>
public sealed record ExportTable(string Name, IReadOnlyList<string> Headers, IReadOnlyList<object?[]> Rows);

/// <summary>
/// CSV and XLSX writers shared by every service that exports tables. CSV is UTF-8 with a byte-order mark so Excel reads Bengali; XLSX stores text as inline UTF-8 strings, so Bengali
/// is kept as is. (Finance has its own copy of this logic from before the move; behaviour is identical.)
/// </summary>
public static class TableExport
{
    private static string Money(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    // ---- CSV -----------------------------------------------------------------------------------------------------------------------------------------------

    /// <summary>UTF-8 with a byte-order mark (Excel needs it to read Bengali), CRLF line ends, quoted where needed. A text cell that starts with = + - @ is prefixed with an apostrophe so a spreadsheet never runs it as a formula.</summary>
    public static byte[] Csv(ExportTable table)
    {
        var sb = new StringBuilder();
        sb.AppendJoin(',', table.Headers.Select(CsvText)).Append("\r\n");
        foreach (var row in table.Rows)
        {
            sb.AppendJoin(',', row.Select(CsvCell)).Append("\r\n");
        }

        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(sb.ToString())];
    }

    private static string CsvCell(object? cell) => cell switch
    {
        null => string.Empty,
        decimal d => Money(d),
        int or long => Convert.ToString(cell, CultureInfo.InvariantCulture)!,
        _ => CsvText(cell.ToString() ?? string.Empty, guard: cell is string)
    };

    private static string CsvText(string value) => CsvText(value, guard: false);

    private static string CsvText(string value, bool guard)
    {
        if (guard && value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            value = "'" + value;
        }

        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
    }

    /// <summary>Every table as its own CSV file in one zip.</summary>
    public static byte[] CsvZip(IEnumerable<ExportTable> tables)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var table in tables)
            {
                AddEntry(zip, SafeFileName(table.Name) + ".csv", Csv(table));
            }
        }

        return stream.ToArray();
    }

    private static string SafeFileName(string name) => new([.. name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-')]);

    private static void AddEntry(ZipArchive zip, string path, byte[] content)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var target = entry.Open();
        target.Write(content);
    }

    // ---- XLSX ----------------------------------------------------------------------------------------------------------------------------------------------

    /// <summary>A workbook written straight to the Office Open XML parts: text as inline UTF-8 strings (Bengali is stored as is), money as numbers formatted to two decimals, a bold header row. The bytes depend only on the content.</summary>
    public static byte[] Xlsx(IReadOnlyList<ExportTable> tables)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var names = SheetNames(tables);
            AddEntry(zip, "[Content_Types].xml", Utf8("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>"
                + string.Concat(names.Select((_, i) => $"<Override PartName=\"/xl/worksheets/sheet{i + 1}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>")) + "</Types>"));
            AddEntry(zip, "_rels/.rels", Utf8("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>"));
            AddEntry(zip, "xl/workbook.xml", Utf8("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>"
                + string.Concat(names.Select((n, i) => $"<sheet name=\"{Xml(n)}\" sheetId=\"{i + 1}\" r:id=\"rId{i + 1}\"/>")) + "</sheets></workbook>"));
            AddEntry(zip, "xl/_rels/workbook.xml.rels", Utf8("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
                + string.Concat(names.Select((_, i) => $"<Relationship Id=\"rId{i + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{i + 1}.xml\"/>"))
                + $"<Relationship Id=\"rId{names.Count + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>"));
            AddEntry(zip, "xl/styles.xml", Utf8("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts><fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills><borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders><cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs><cellXfs count=\"3\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/><xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/><xf numFmtId=\"4\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/></cellXfs></styleSheet>"));
            for (var i = 0; i < tables.Count; i++)
            {
                AddEntry(zip, $"xl/worksheets/sheet{i + 1}.xml", Utf8(Sheet(tables[i])));
            }
        }

        return stream.ToArray();
    }

    private static byte[] Utf8(string xml) => new UTF8Encoding(false).GetBytes(xml);

    private static List<string> SheetNames(IReadOnlyList<ExportTable> tables)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>();
        foreach (var table in tables)
        {
            var clean = new string([.. table.Name.Where(c => c is not ('\\' or '/' or '?' or '*' or '[' or ']' or ':'))]);
            var name = clean.Length == 0 ? "Sheet" : clean[..Math.Min(clean.Length, 31)];
            var n = 2;
            while (!used.Add(name))
            {
                var suffix = " " + n++.ToString(CultureInfo.InvariantCulture);
                name = clean[..Math.Min(clean.Length, 31 - suffix.Length)] + suffix;
            }

            names.Add(name);
        }

        return names;
    }

    private static string Sheet(ExportTable table)
    {
        var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews><cols>");
        for (var c = 0; c < table.Headers.Count; c++)
        {
            sb.Append(CultureInfo.InvariantCulture, $"<col min=\"{c + 1}\" max=\"{c + 1}\" width=\"{Math.Clamp(Math.Max(table.Headers[c].Length, 12) + 4, 14, 60)}\" customWidth=\"1\"/>");
        }

        sb.Append("</cols><sheetData>");
        AppendRow(sb, 1, table.Headers.Cast<object?>().ToArray(), header: true);
        for (var r = 0; r < table.Rows.Count; r++)
        {
            AppendRow(sb, r + 2, table.Rows[r], header: false);
        }

        return sb.Append("</sheetData></worksheet>").ToString();
    }

    private static void AppendRow(StringBuilder sb, int rowNumber, object?[] cells, bool header)
    {
        sb.Append(CultureInfo.InvariantCulture, $"<row r=\"{rowNumber}\">");
        for (var c = 0; c < cells.Length; c++)
        {
            var reference = Column(c) + rowNumber.ToString(CultureInfo.InvariantCulture);
            switch (cells[c])
            {
                case null:
                    break;
                case decimal d:
                    sb.Append(CultureInfo.InvariantCulture, $"<c r=\"{reference}\" s=\"2\"><v>{Money(d)}</v></c>");
                    break;
                case int or long:
                    sb.Append(CultureInfo.InvariantCulture, $"<c r=\"{reference}\"><v>{Convert.ToString(cells[c], CultureInfo.InvariantCulture)}</v></c>");
                    break;
                default:
                    sb.Append(CultureInfo.InvariantCulture, $"<c r=\"{reference}\" t=\"inlineStr\"{(header ? " s=\"1\"" : string.Empty)}><is><t xml:space=\"preserve\">{Xml(cells[c]!.ToString() ?? string.Empty)}</t></is></c>");
                    break;
            }
        }

        sb.Append("</row>");
    }

    private static string Column(int index)
    {
        var name = string.Empty;
        for (var n = index + 1; n > 0; n = (n - 1) / 26)
        {
            name = (char)('A' + ((n - 1) % 26)) + name;
        }

        return name;
    }

    /// <summary>XML-escapes text and drops the control characters XML 1.0 cannot carry, so a stray byte in a memo cannot corrupt the workbook.</summary>
    private static string Xml(string text) => SecurityElement.Escape(new string([.. text.Where(c => c is '\t' or '\n' or '\r' || (c >= ' ' && c != '￾' && c != '￿' && !char.IsSurrogate(c)) || (char.IsSurrogate(c)))])) ?? string.Empty;
}
