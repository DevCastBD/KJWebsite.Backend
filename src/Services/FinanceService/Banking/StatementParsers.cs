using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FinanceService.Banking;

/// <summary>One statement line as read from a file: money in is positive, money out negative.</summary>
public sealed record ParsedLine(int Row, DateOnly Date, string Description, string? Reference, decimal Amount, decimal? Balance);

/// <summary>A row that could not be read, with its position in the file (the header is row 1) and the reason, so the whole list can be fixed in one go.</summary>
public sealed record ImportError(int Row, string Reason);

public sealed record ParsedStatement(IReadOnlyList<ParsedLine> Lines, IReadOnlyList<ImportError> Errors, string? Currency);

/// <summary>
/// Which CSV column is which. Every column is a header name (case-insensitive) or a 1-based position. Amounts come either as one signed `amount` or as separate `debit` (money out)
/// and `credit` (money in) columns. `dateFormat` is a .NET format such as `dd/MM/yyyy`; without one the common formats are tried, day first.
/// </summary>
public sealed record CsvMapping(string? Date, string? Description, string? Reference, string? Amount, string? Debit, string? Credit, string? Balance, string? DateFormat, string? Delimiter);

public static partial class StatementParsers
{
    public const int MaxRows = 20000;

    private static readonly string[] DateFormats =
    [
        "yyyy-MM-dd", "yyyy/MM/dd", "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "d-M-yyyy", "dd.MM.yyyy", "d.M.yyyy",
        "dd-MMM-yyyy", "d-MMM-yyyy", "dd MMM yyyy", "d MMM yyyy", "dd-MMM-yy", "dd/MM/yy", "yyyyMMdd"
    ];

    private static readonly JsonSerializerOptions MappingJson = new(JsonSerializerDefaults.Web);

    private static readonly string[] DateNames = ["date", "transaction date", "txn date", "value date", "posting date", "booking date", "tran date", "তারিখ"];
    private static readonly string[] DescriptionNames = ["description", "details", "narration", "particulars", "transaction details", "memo", "remarks", "narrative", "বিবরণ"];
    private static readonly string[] ReferenceNames = ["reference", "ref", "ref no", "reference no", "reference number", "cheque no", "chq no", "transaction id", "txn id", "fitid", "voucher"];
    private static readonly string[] AmountNames = ["amount", "transaction amount", "txn amount", "net amount"];
    private static readonly string[] DebitNames = ["debit", "withdrawal", "withdrawals", "dr", "paid out", "money out", "debit amount"];
    private static readonly string[] CreditNames = ["credit", "deposit", "deposits", "cr", "paid in", "money in", "credit amount"];
    private static readonly string[] BalanceNames = ["balance", "running balance", "closing balance", "available balance"];

    public static bool LooksLikeOfx(string text)
    {
        var head = text.AsSpan(0, Math.Min(text.Length, 600));
        return head.Contains("OFXHEADER", StringComparison.OrdinalIgnoreCase) || head.Contains("<OFX", StringComparison.OrdinalIgnoreCase);
    }

    public static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>A line's identity inside one account: the same line in two overlapping statements has the same fingerprint, so it can be recognised and never counted twice.</summary>
    public static string Fingerprint(ParsedLine line)
    {
        var key = string.Join('|', line.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), line.Amount.ToString("0.00", CultureInfo.InvariantCulture),
            Normalise(line.Description), Normalise(line.Reference ?? string.Empty), line.Balance?.ToString("0.00", CultureInfo.InvariantCulture) ?? string.Empty);
        return Hash(key)[..32];
    }

    public static string Normalise(string text) => WhitespaceRun().Replace(text.Trim(), " ").ToUpperInvariant();

    public static CsvMapping? ReadMapping(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<CsvMapping>(json, MappingJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static ParsedStatement ParseCsv(string text, CsvMapping? mapping)
    {
        var errors = new List<ImportError>();
        text = text.TrimStart('﻿');
        var delimiter = mapping?.Delimiter is { Length: 1 } d ? d[0] : DetectDelimiter(text);
        var records = SplitCsv(text, delimiter);
        if (records.Count == 0)
        {
            return new ParsedStatement([], [new ImportError(1, "The file is empty.")], null);
        }

        if (records.Count - 1 > MaxRows)
        {
            return new ParsedStatement([], [new ImportError(1, $"A statement may have at most {MaxRows} rows.")], null);
        }

        var header = records[0].Select(h => h.Trim()).ToList();
        var columns = Resolve(header, mapping, errors);
        if (columns is null)
        {
            return new ParsedStatement([], errors, null);
        }

        var lines = new List<ParsedLine>();
        for (var i = 1; i < records.Count; i++)
        {
            var cells = records[i];
            if (cells.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            var row = i + 1;
            string Cell(int? index) => index is { } n && n < cells.Count ? cells[n].Trim() : string.Empty;

            if (!TryDate(Cell(columns.Date), mapping?.DateFormat, out var date))
            {
                errors.Add(new ImportError(row, $"The date '{Cell(columns.Date)}' is not a recognised date."));
                continue;
            }

            decimal amount;
            if (columns.Amount is not null)
            {
                if (!TryAmount(Cell(columns.Amount), out amount, out var bad))
                {
                    errors.Add(new ImportError(row, bad));
                    continue;
                }
            }
            else
            {
                var debitText = Cell(columns.Debit);
                var creditText = Cell(columns.Credit);
                var hasDebit = !string.IsNullOrWhiteSpace(debitText);
                var hasCredit = !string.IsNullOrWhiteSpace(creditText);
                if (hasDebit == hasCredit)
                {
                    errors.Add(new ImportError(row, hasDebit ? "Both a debit and a credit are filled in." : "Neither a debit nor a credit is filled in."));
                    continue;
                }

                if (!TryAmount(hasDebit ? debitText : creditText, out var magnitude, out var why))
                {
                    errors.Add(new ImportError(row, why));
                    continue;
                }

                amount = hasDebit ? -Math.Abs(magnitude) : Math.Abs(magnitude);
            }

            if (amount == 0m)
            {
                errors.Add(new ImportError(row, "The amount is zero."));
                continue;
            }

            decimal? balance = null;
            if (columns.Balance is not null && !string.IsNullOrWhiteSpace(Cell(columns.Balance)))
            {
                if (!TryAmount(Cell(columns.Balance), out var parsedBalance, out var badBalance))
                {
                    errors.Add(new ImportError(row, "Balance: " + badBalance));
                    continue;
                }

                balance = parsedBalance;
            }

            var description = Cell(columns.Description);
            if (description.Length == 0)
            {
                errors.Add(new ImportError(row, "The description is empty."));
                continue;
            }

            var reference = Cell(columns.Reference);
            lines.Add(new ParsedLine(row, date, Clip(description, 500), reference.Length == 0 ? null : Clip(reference, 100), amount, balance));
        }

        if (lines.Count == 0 && errors.Count == 0)
        {
            errors.Add(new ImportError(1, "The file has a header but no rows."));
        }

        return new ParsedStatement(lines, errors, null);
    }

    public static ParsedStatement ParseOfx(string text)
    {
        var errors = new List<ImportError>();
        var lines = new List<ParsedLine>();
        var currency = Tag(text, "CURDEF");
        var blocks = StatementTransaction().Matches(text);
        if (blocks.Count == 0)
        {
            return new ParsedStatement([], [new ImportError(1, "No transactions were found in the OFX file.")], currency);
        }

        if (blocks.Count > MaxRows)
        {
            return new ParsedStatement([], [new ImportError(1, $"A statement may have at most {MaxRows} rows.")], currency);
        }

        var n = 0;
        foreach (Match block in blocks)
        {
            n++;
            var body = block.Groups[1].Value;
            var posted = Tag(body, "DTPOSTED");
            if (posted is null || posted.Length < 8 || !DateOnly.TryParseExact(posted[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                errors.Add(new ImportError(n, "Transaction " + n.ToString(CultureInfo.InvariantCulture) + ": DTPOSTED is missing or not a date."));
                continue;
            }

            if (!TryAmount(Tag(body, "TRNAMT") ?? string.Empty, out var amount, out var bad) || amount == 0m)
            {
                errors.Add(new ImportError(n, "Transaction " + n.ToString(CultureInfo.InvariantCulture) + ": " + (amount == 0m && bad.Length == 0 ? "the amount is zero." : bad)));
                continue;
            }

            var name = Tag(body, "NAME");
            var memo = Tag(body, "MEMO");
            var description = string.Join(" - ", new[] { name, memo }.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!.Trim()));
            if (description.Length == 0)
            {
                description = Tag(body, "TRNTYPE") ?? string.Empty;
            }

            if (description.Length == 0)
            {
                errors.Add(new ImportError(n, "Transaction " + n.ToString(CultureInfo.InvariantCulture) + ": it has no NAME, MEMO or TRNTYPE."));
                continue;
            }

            var reference = Tag(body, "FITID") ?? Tag(body, "CHECKNUM");
            lines.Add(new ParsedLine(n, date, Clip(System.Net.WebUtility.HtmlDecode(description), 500), string.IsNullOrWhiteSpace(reference) ? null : Clip(reference.Trim(), 100), amount, null));
        }

        return new ParsedStatement(lines, errors, currency);
    }

    /// <summary>Reads a money value the way statements print it: thousands separators, currency marks, a minus sign, (brackets) or a Dr/Cr suffix, and Bengali digits. More than two decimals is refused, never rounded.</summary>
    public static bool TryAmount(string text, out decimal amount, out string error)
    {
        amount = 0m;
        error = string.Empty;
        var raw = text.Trim();
        if (raw.Length == 0)
        {
            error = "The amount is empty.";
            return false;
        }

        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            sb.Append(ch is >= '০' and <= '৯' ? (char)('0' + (ch - '০')) : ch);
        }

        var value = sb.ToString().Replace("৳", string.Empty, StringComparison.Ordinal).Replace("BDT", string.Empty, StringComparison.OrdinalIgnoreCase).Replace("Tk", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
        var negative = false;
        if (value.StartsWith('(') && value.EndsWith(')'))
        {
            negative = true;
            value = value[1..^1].Trim();
        }

        if (value.EndsWith("Dr", StringComparison.OrdinalIgnoreCase))
        {
            negative = true;
            value = value[..^2].Trim();
        }
        else if (value.EndsWith("Cr", StringComparison.OrdinalIgnoreCase))
        {
            value = value[..^2].Trim();
        }

        if (value.StartsWith('-'))
        {
            negative = !negative;
            value = value[1..].Trim();
        }
        else if (value.StartsWith('+'))
        {
            value = value[1..].Trim();
        }

        value = value.Replace(" ", string.Empty, StringComparison.Ordinal);
        if (value.Contains(',', StringComparison.Ordinal))
        {
            // A comma may only group the whole-number part (1,234 or the lakh style 1,23,456), never mark decimals: "1,50" or "1.000,00" is refused rather than misread.
            var whole = value.Split('.', 2)[0];
            if (!ThousandsGrouping().IsMatch(whole))
            {
                error = $"'{raw}' is not an amount: a comma may only separate thousands.";
                return false;
            }

            value = value.Replace(",", string.Empty, StringComparison.Ordinal);
        }

        if (!PlainNumber().IsMatch(value))
        {
            error = $"'{raw}' is not an amount.";
            return false;
        }

        if (!decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number))
        {
            error = $"'{raw}' is not an amount.";
            return false;
        }

        if (decimal.Round(number, 2) != number)
        {
            error = $"'{raw}' has more than two decimal places.";
            return false;
        }

        if (number > 9_999_999_999_999m)
        {
            error = $"'{raw}' is too large.";
            return false;
        }

        amount = negative ? -number : number;
        return true;
    }

    public static bool TryDate(string text, string? format, out DateOnly date)
    {
        date = default;
        var value = text.Trim();
        if (value.Length == 0)
        {
            return false;
        }

        var chars = value.Select(ch => ch is >= '০' and <= '৯' ? (char)('0' + (ch - '০')) : ch).ToArray();
        value = new string(chars);
        if (!string.IsNullOrWhiteSpace(format))
        {
            return DateOnly.TryParseExact(value, format, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out date) && Plausible(date);
        }

        return DateOnly.TryParseExact(value, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out date) && Plausible(date);
    }

    private static bool Plausible(DateOnly date) => date.Year is >= 1990 and <= 2100;

    private sealed record Columns(int? Date, int? Description, int? Reference, int? Amount, int? Debit, int? Credit, int? Balance);

    private static Columns? Resolve(List<string> header, CsvMapping? mapping, List<ImportError> errors)
    {
        int? Find(string? configured, string[] synonyms, string what, bool required)
        {
            if (!string.IsNullOrWhiteSpace(configured))
            {
                if (int.TryParse(configured, NumberStyles.None, CultureInfo.InvariantCulture, out var position) && position >= 1 && position <= header.Count)
                {
                    return position - 1;
                }

                var named = header.FindIndex(h => string.Equals(h, configured.Trim(), StringComparison.OrdinalIgnoreCase));
                if (named >= 0)
                {
                    return named;
                }

                errors.Add(new ImportError(1, $"The mapped {what} column '{configured}' is not in the header."));
                return null;
            }

            var index = header.FindIndex(h => synonyms.Contains(h.ToLowerInvariant()));
            if (index < 0 && required)
            {
                errors.Add(new ImportError(1, $"No {what} column was found in the header ({string.Join(", ", header)}); set the account's CSV mapping."));
            }

            return index < 0 ? null : index;
        }

        var date = Find(mapping?.Date, DateNames, "date", true);
        var description = Find(mapping?.Description, DescriptionNames, "description", true);
        var reference = Find(mapping?.Reference, ReferenceNames, "reference", false);
        var balance = Find(mapping?.Balance, BalanceNames, "balance", false);
        var amount = Find(mapping?.Amount, AmountNames, "amount", false);
        int? debit = null;
        int? credit = null;
        if (amount is null && errors.Count == 0)
        {
            debit = Find(mapping?.Debit, DebitNames, "debit", false);
            credit = Find(mapping?.Credit, CreditNames, "credit", false);
            if (debit is null || credit is null)
            {
                errors.Add(new ImportError(1, "No amount column, or debit and credit columns, were found in the header; set the account's CSV mapping."));
            }
        }

        return errors.Count > 0 ? null : new Columns(date, description, reference, amount, debit, credit, balance);
    }

    private static char DetectDelimiter(string text)
    {
        var firstLine = text.Split('\n', 2)[0];
        var best = ',';
        var most = -1;
        foreach (var candidate in new[] { ',', ';', '\t', '|' })
        {
            var count = firstLine.Count(c => c == candidate);
            if (count > most)
            {
                most = count;
                best = candidate;
            }
        }

        return best;
    }

    private static List<List<string>> SplitCsv(string text, char delimiter)
    {
        var records = new List<List<string>>();
        var cells = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    cell.Append('"');
                    i++;
                }
                else if (ch == '"')
                {
                    quoted = false;
                }
                else
                {
                    cell.Append(ch);
                }

                continue;
            }

            if (ch == '"' && cell.Length == 0)
            {
                quoted = true;
            }
            else if (ch == delimiter)
            {
                cells.Add(cell.ToString());
                cell.Clear();
            }
            else if (ch is '\r' or '\n')
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                cells.Add(cell.ToString());
                cell.Clear();
                records.Add(cells);
                cells = [];
            }
            else
            {
                cell.Append(ch);
            }
        }

        if (cell.Length > 0 || cells.Count > 0)
        {
            cells.Add(cell.ToString());
            records.Add(cells);
        }

        return records;
    }

    private static string? Tag(string text, string name)
    {
        var match = Regex.Match(text, "<" + name + @">\s*([^<\r\n]*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max];

    [GeneratedRegex(@"<STMTTRN>(.*?)(?:</STMTTRN>|(?=<STMTTRN>)|(?=</BANKTRANLIST>)|\z)", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex StatementTransaction();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRun();

    [GeneratedRegex(@"^[0-9]{1,3}(,[0-9]{3})+\z|^[0-9]{1,2}(,[0-9]{2})+,[0-9]{3}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ThousandsGrouping();

    [GeneratedRegex(@"^[0-9]+(\.[0-9]+)?\z|^\.[0-9]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex PlainNumber();
}
