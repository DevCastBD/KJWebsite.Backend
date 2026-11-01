using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;
using FinanceService.Ledger;

namespace FinanceService.Reports;

/// <summary>A named grid: the unit every report is exported as. Cells are strings, decimals (money, written as numbers in XLSX and two-decimal text in CSV), ints or null.</summary>
public sealed record ReportTable(string Name, IReadOnlyList<string> Headers, IReadOnlyList<object?[]> Rows);

public static class ReportExport
{
    private static string Money(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private static string Day(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // ---- tables from the models ----------------------------------------------------------------------------------------------------------------------------

    public static ReportTable IncomeTable(IEnumerable<IncomeStatement> statements)
    {
        List<object?[]> rows = [];
        foreach (var s in statements)
        {
            rows.AddRange(s.Income.Select(l => new object?[] { s.Currency, "Income", l.Code, l.Name, l.Amount }));
            rows.Add([s.Currency, "Income", null, "Total income", s.TotalIncome]);
            rows.AddRange(s.Expenses.Select(l => new object?[] { s.Currency, "Expenses", l.Code, l.Name, l.Amount }));
            rows.Add([s.Currency, "Expenses", null, "Total expenses", s.TotalExpenses]);
            rows.Add([s.Currency, "Result", null, "Surplus (deficit)", s.Surplus]);
        }

        return new ReportTable("Income statement", ["Currency", "Section", "Account", "Name", "Amount"], rows);
    }

    public static ReportTable BalanceTable(IEnumerable<BalanceSheet> sheets)
    {
        List<object?[]> rows = [];
        foreach (var s in sheets)
        {
            rows.AddRange(s.Assets.Select(l => new object?[] { s.Currency, "Assets", l.Code, l.Name, l.Amount }));
            rows.Add([s.Currency, "Assets", null, "Total assets", s.TotalAssets]);
            rows.AddRange(s.Liabilities.Select(l => new object?[] { s.Currency, "Liabilities", l.Code, l.Name, l.Amount }));
            rows.Add([s.Currency, "Liabilities", null, "Total liabilities", s.TotalLiabilities]);
            rows.AddRange(s.Equity.Select(l => new object?[] { s.Currency, "Equity", l.Code, l.Name, l.Amount }));
            rows.Add([s.Currency, "Equity", null, "Accumulated surplus (deficit)", s.AccumulatedSurplus]);
            rows.Add([s.Currency, "Equity", null, "Total liabilities and equity", s.TotalLiabilitiesAndEquity]);
        }

        return new ReportTable("Balance sheet", ["Currency", "Section", "Account", "Name", "Amount"], rows);
    }

    public static ReportTable CashFlowTable(IEnumerable<CashFlow> flows)
    {
        List<object?[]> rows = [];
        foreach (var f in flows)
        {
            rows.Add([f.Currency, "Opening balance", null, null, f.Opening]);
            rows.AddRange(f.Lines.Select(l => new object?[] { f.Currency, l.Label, l.In, l.Out, l.Net }));
            rows.Add([f.Currency, "Net movement", f.TotalIn, f.TotalOut, f.Net]);
            rows.Add([f.Currency, "Closing balance", null, null, f.Closing]);
            rows.Add([f.Currency, f.Reconciles ? "Agrees with the ledger" : "DOES NOT agree with the ledger", null, null, f.LedgerClosing]);
        }

        return new ReportTable("Cash flow", ["Currency", "Line", "Cash in", "Cash out", "Net"], rows);
    }

    public static ReportTable DonationTable(IEnumerable<DonationSummary> summaries)
    {
        List<object?[]> rows = [];
        foreach (var s in summaries)
        {
            rows.Add([s.Currency, "Total", "all", "All gifts", s.Total, s.Gifts]);
            rows.AddRange(s.ByChannel.Select(l => new object?[] { s.Currency, "Channel", l.Key, l.Label, l.Amount, l.Gifts }));
            rows.AddRange(s.ByDesignation.Select(l => new object?[] { s.Currency, "Designation", l.Key, l.Label, l.Amount, l.Gifts }));
            rows.AddRange(s.ByMonth.Select(l => new object?[] { s.Currency, "Month", l.Key, l.Label, l.Amount, l.Gifts }));
        }

        return new ReportTable("Donations", ["Currency", "Grouping", "Key", "Label", "Amount", "Gifts"], rows);
    }

    public static ReportTable ProjectCostTable(IEnumerable<ProjectCosts> reports)
    {
        List<object?[]> rows = [];
        foreach (var r in reports)
        {
            rows.AddRange(r.Lines.Select(l => new object?[] { r.Currency, l.Project, l.Category, l.Amount }));
            rows.Add([r.Currency, "Total", null, r.Total]);
        }

        return new ReportTable("Project costs", ["Currency", "Project", "Category", "Amount"], rows);
    }

    public static ReportTable DonorTable(IEnumerable<DonorReport> reports) =>
        new("Donors", ["Currency", "Donors", "New donors", "Returning donors", "Gifts", "Total", "Average gift", "Largest gift", "Gifts without a donor record", "Total without a donor record"],
            [.. reports.Select(r => new object?[] { r.Currency, r.Donors, r.NewDonors, r.ReturningDonors, r.Gifts, r.Total, r.Average, r.Largest, r.UnidentifiedGifts, r.UnidentifiedTotal })]);

    public static ReportTable JournalTable(IEnumerable<JournalRow> rows) =>
        new("General ledger", ["Posting", "Date", "Account", "Account name", "Memo", "Source", "Debit", "Credit", "Currency"],
            [.. rows.Select(r => new object?[] { r.PostingId, Day(r.Date), r.Code, r.Account, r.Memo, r.SourceType, r.Debit, r.Credit, r.Currency })]);

    public static ReportTable TrialBalanceTable(IEnumerable<CurrencyTrialBalance> sections)
    {
        List<object?[]> rows = [];
        foreach (var s in sections)
        {
            rows.AddRange(s.Lines.Select(l => new object?[] { s.Currency, l.Code, l.Name, l.Type, l.Debit, l.Credit, l.Balance }));
            rows.Add([s.Currency, null, "Total", null, s.TotalDebit, s.TotalCredit, null]);
        }

        return new ReportTable("Trial balance", ["Currency", "Account", "Name", "Type", "Debit", "Credit", "Balance"], rows);
    }

    public static ReportTable DonationRegister(IEnumerable<GiftFact> gifts, IReadOnlyDictionary<string, string> designationOf) =>
        new("Donation register", ["Posting", "Date", "Donation", "Channel", "Designation", "Amount", "Currency"],
            [.. gifts.Select(g => new object?[] { g.PostingId, Day(g.Date), g.DonationId, g.ChannelName, g.DonationId is not null && designationOf.TryGetValue(g.DonationId, out var d) ? d : null, g.Amount, g.Currency })]);

    public static ReportTable ExpenseRegister(IEnumerable<JournalRow> expenseLines, IReadOnlyDictionary<long, string> expenseOfPosting, IReadOnlyDictionary<string, (string? Project, string Category)> expenses) =>
        new("Expense register", ["Posting", "Date", "Expense", "Account", "Memo", "Project", "Category", "Amount", "Currency"],
            [.. expenseLines.Select(r =>
            {
                var id = expenseOfPosting.GetValueOrDefault(r.PostingId);
                var info = id is not null && expenses.TryGetValue(id, out var e) ? e : default;
                return new object?[] { r.PostingId, Day(r.Date), id, r.Code + " " + r.Account, r.Memo, id is null ? null : info.Project ?? "(organisation)", id is null ? null : info.Category, r.Debit - r.Credit, r.Currency };
            })]);

    // ---- CSV -----------------------------------------------------------------------------------------------------------------------------------------------

    /// <summary>UTF-8 with a byte-order mark (Excel needs it to read Bengali), CRLF line ends, quoted where needed. A text cell that starts with = + - @ is prefixed with an apostrophe so a spreadsheet never runs it as a formula.</summary>
    public static byte[] Csv(ReportTable table)
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
    public static byte[] CsvZip(IEnumerable<ReportTable> tables)
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
    public static byte[] Xlsx(IReadOnlyList<ReportTable> tables)
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

    private static List<string> SheetNames(IReadOnlyList<ReportTable> tables)
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

    private static string Sheet(ReportTable table)
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
