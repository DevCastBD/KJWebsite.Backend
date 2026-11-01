using System.Globalization;
using System.Security.Cryptography;
using FinanceService.Data;
using FinanceService.Data.Entities;
using FinanceService.Ledger;
using FinanceService.Offline;
using KJWebsite.BuildingBlocks;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FinanceService.Reports;

public sealed record ReportCheck(string Name, string Currency, bool Ok);

/// <summary>What a report request produced: tables to export and the reconciliation checks that were run on them.</summary>
public sealed record ReportResult(string Title, IReadOnlyList<ReportTable> Tables, IReadOnlyList<ReportCheck> Checks);

public sealed record ReportPackView(string Id, string Month, DateTimeOffset GeneratedAt, string Url, string? GeneratedBy, string Sha256, long SizeBytes);

public static class ReportNames
{
    public const string IncomeStatement = "income-statement";
    public const string BalanceSheet = "balance-sheet";
    public const string CashFlow = "cash-flow";
    public const string Donations = "donations";
    public const string ProjectCosts = "project-costs";
    public const string Donors = "donors";
    public const string TrialBalance = "trial-balance";
    public const string GeneralLedger = "general-ledger";
    public const string DonationRegister = "donation-register";
    public const string ExpenseRegister = "expense-register";
    public const string AuditorPack = "auditor-pack";

    public static readonly IReadOnlyList<string> All =
        [IncomeStatement, BalanceSheet, CashFlow, Donations, ProjectCosts, Donors, TrialBalance, GeneralLedger, DonationRegister, ExpenseRegister, AuditorPack];
}

public sealed class ReportService(FinanceDbContext db, ReportBuilder builder, IProofStore store, IDataProtectionProvider protection, TimeProvider clock)
{
    private const string LinkPurpose = "kj.finance.report-link";
    public const string XlsxType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public DateOnly Today => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

    /// <summary>One load of the ledger up to `to`, and every report is cut from it.</summary>
    public async Task<ReportResult> BuildAsync(string name, DateOnly from, DateOnly to, DateOnly asOf, string? currency, string? generatedBy, CancellationToken cancellationToken)
    {
        var rows = await builder.LoadAsync(to > asOf ? to : asOf, currency, cancellationToken);
        var cash = await builder.CashCodesAsync(cancellationToken);
        var gifts = ReportBuilder.Gifts(rows);
        var donationIds = gifts.Where(g => g.DonationId is not null).Select(g => g.DonationId!);
        var designations = await builder.DesignationsAsync(donationIds, cancellationToken);
        var donorKeys = await builder.DonorKeysAsync(donationIds, cancellationToken);
        var expenseIds = rows.Where(r => r.SourceType == PostingSources.Expense && r.SourceId is not null).Select(r => r.SourceId!).Distinct(StringComparer.Ordinal).ToList();
        var expenses = await builder.ExpensesAsync(expenseIds, cancellationToken);

        var income = builder.IncomeStatements(rows, from, to);
        var balance = builder.BalanceSheets(rows, asOf);
        var flow = builder.CashFlows(rows, cash, from, to);
        var costs = builder.ProjectCostReports(rows, expenses, from, to);
        var trial = TrialBalance(rows, asOf);
        var checks = new List<ReportCheck>();
        checks.AddRange(balance.Select(b => new ReportCheck("balance-sheet-balances", b.Currency, b.Balanced)));
        checks.AddRange(flow.Select(f => new ReportCheck("cash-flow-agrees-with-ledger", f.Currency, f.Reconciles)));
        checks.AddRange(trial.Select(t => new ReportCheck("trial-balance-balances", t.Currency, t.Balanced)));
        checks.AddRange(costs.Select(c => new ReportCheck("project-costs-equal-expenses", c.Currency, income.FirstOrDefault(i => i.Currency == c.Currency)?.TotalExpenses == c.Total)));

        ReportTable Donations() => ReportExport.DonationTable(builder.DonationSummaries(gifts, designations, from, to));
        ReportTable Donors() => ReportExport.DonorTable(builder.DonorReports(gifts, donorKeys, from, to));
        ReportTable DonationRegister() => ReportExport.DonationRegister(gifts.Where(g => g.Date >= from && g.Date <= to), designations);
        ReportTable ExpenseRegister()
        {
            var expenseOfPosting = rows.Where(r => r.SourceType == PostingSources.Expense && r.SourceId is not null).GroupBy(r => r.PostingId)
                .ToDictionary(g => g.Key, g => g.First().SourceId!.Replace(":payment", string.Empty, StringComparison.Ordinal));
            foreach (var reversal in rows.Where(r => r.Reverses is not null && expenseOfPosting.ContainsKey(r.Reverses.Value)).GroupBy(r => r.PostingId))
            {
                expenseOfPosting[reversal.Key] = expenseOfPosting[reversal.First().Reverses!.Value];
            }

            var lines = rows.Where(r => r.Type == AccountTypes.Expense && r.PostedOn >= from && r.PostedOn <= to)
                .Select(r => new JournalRow(r.PostingId, r.PostedOn, r.Code, r.Name, r.Memo, r.SourceType, r.Debit, r.Credit, r.Currency));
            return ReportExport.ExpenseRegister(lines, expenseOfPosting, expenses);
        }

        ReportTable Ledger() => ReportExport.JournalTable(ReportBuilder.Journal(rows, from, to));
        var trialTable = ReportExport.TrialBalanceTable(trial);
        var incomeTable = ReportExport.IncomeTable(income);
        var balanceTable = ReportExport.BalanceTable(balance);
        var flowTable = ReportExport.CashFlowTable(flow);
        var costTable = ReportExport.ProjectCostTable(costs);

        return name switch
        {
            ReportNames.IncomeStatement => new ReportResult("Income statement", [incomeTable], []),
            ReportNames.BalanceSheet => new ReportResult("Balance sheet", [balanceTable], [.. checks.Where(c => c.Name == "balance-sheet-balances")]),
            ReportNames.CashFlow => new ReportResult("Cash flow", [flowTable], [.. checks.Where(c => c.Name == "cash-flow-agrees-with-ledger")]),
            ReportNames.Donations => new ReportResult("Donations", [Donations()], []),
            ReportNames.ProjectCosts => new ReportResult("Project costs", [costTable], [.. checks.Where(c => c.Name == "project-costs-equal-expenses")]),
            ReportNames.Donors => new ReportResult("Donors", [Donors()], []),
            ReportNames.TrialBalance => new ReportResult("Trial balance", [trialTable], [.. checks.Where(c => c.Name == "trial-balance-balances")]),
            ReportNames.GeneralLedger => new ReportResult("General ledger", [Ledger()], []),
            ReportNames.DonationRegister => new ReportResult("Donation register", [DonationRegister()], []),
            ReportNames.ExpenseRegister => new ReportResult("Expense register", [ExpenseRegister()], []),
            _ => new ReportResult("Auditor pack", [Summary(from, to, asOf, generatedBy, rows, checks), trialTable, incomeTable, balanceTable, flowTable, Donations(), costTable, Donors(), DonationRegister(), ExpenseRegister(), Ledger()], checks)
        };
    }

    private static List<CurrencyTrialBalance> TrialBalance(IReadOnlyList<LedgerRow> rows, DateOnly asOf) =>
    [
        .. rows.Where(r => r.PostedOn <= asOf).GroupBy(r => r.Currency).OrderBy(g => g.Key, StringComparer.Ordinal).Select(group =>
        {
            var lines = group.GroupBy(r => (r.Code, r.Name, r.Type)).OrderBy(g => g.Key.Code, StringComparer.Ordinal)
                .Select(g => new TrialBalanceLine(g.Key.Code, g.Key.Name, g.Key.Type, group.Key, g.Sum(r => r.Debit), g.Sum(r => r.Credit))).ToList();
            return new CurrencyTrialBalance(group.Key, lines, lines.Sum(l => l.Debit), lines.Sum(l => l.Credit));
        })
    ];

    private ReportTable Summary(DateOnly from, DateOnly to, DateOnly asOf, string? generatedBy, IReadOnlyList<LedgerRow> rows, IReadOnlyList<ReportCheck> checks)
    {
        List<object?[]> lines =
        [
            ["Organisation", builder.Options.EffectiveEntityName],
            ["Period from", from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)],
            ["Period to", to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)],
            ["Balances as of", asOf.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)],
            ["Generated at (UTC)", clock.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)],
            ["Generated by", generatedBy ?? "-"],
            ["Currencies", string.Join(", ", rows.Select(r => r.Currency).Distinct(StringComparer.Ordinal).OrderBy(c => c, StringComparer.Ordinal))],
            ["Ledger lines read", rows.Count],
            ["Source", "Every figure is computed from the double-entry ledger; nothing is converted between currencies."]
        ];
        lines.AddRange(checks.Select(c => new object?[] { $"Check: {c.Name} ({c.Currency})", c.Ok ? "OK" : "FAILED" }));
        return new ReportTable("Summary", ["Item", "Value"], lines);
    }

    public static byte[] Render(ReportResult result, string format) => format switch
    {
        "xlsx" => ReportExport.Xlsx(result.Tables),
        _ => result.Tables.Count == 1 ? ReportExport.Csv(result.Tables[0]) : ReportExport.CsvZip(result.Tables)
    };

    // ---- stored packs -----------------------------------------------------------------------------------------------------------------------------------

    public static bool TryMonth(string? month, out DateOnly first, out DateOnly last)
    {
        first = last = default;
        if (month is null || !DateOnly.TryParseExact(month + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out first))
        {
            return false;
        }

        last = first.AddMonths(1).AddDays(-1);
        return first.Year >= 2000;
    }

    public async Task<Result<ReportPackEntity>> GeneratePackAsync(string? month, string? actor, CancellationToken cancellationToken)
    {
        if (!TryMonth(month, out var first, out var last))
        {
            return Result.Failure<ReportPackEntity>("VALIDATION_ERROR", "Invalid fields: month (yyyy-MM).");
        }

        if (first > Today)
        {
            return Result.Failure<ReportPackEntity>("MONTH_IN_FUTURE", "A report cannot be made for a month that has not started.");
        }

        var built = await BuildAsync(ReportNames.AuditorPack, first, last, last, null, actor, cancellationToken);
        var bytes = ReportExport.Xlsx(built.Tables);
        var token = Guid.NewGuid().ToString("N");
        var id = "rpt_" + token;
        var key = "report-" + token;
        using (var content = new MemoryStream(bytes))
        {
            await store.SaveAsync(key, content, cancellationToken);
        }

        var pack = new ReportPackEntity
        {
            Id = id,
            Month = month!,
            EntityName = builder.Options.EffectiveEntityName,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
            SizeBytes = bytes.Length,
            StorageKey = key,
            GeneratedBy = actor,
            GeneratedAt = clock.GetUtcNow()
        };
        db.ReportPacks.Add(pack);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success(pack);
    }

    public string SignedPath(string id)
    {
        var protector = protection.CreateProtector(LinkPurpose).ToTimeLimitedDataProtector();
        var token = protector.Protect(id, TimeSpan.FromMinutes(builder.Options.EffectiveLinkMinutes));
        return $"/api/v1/finance/report-downloads/{id}?token={Uri.EscapeDataString(token)}";
    }

    public bool TokenFits(string id, string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        try
        {
            return protection.CreateProtector(LinkPurpose).ToTimeLimitedDataProtector().Unprotect(token) == id;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>The stored bytes, or null when the pack does not exist; a hash mismatch is reported to the caller as corruption.</summary>
    public async Task<(ReportPackEntity Pack, byte[] Bytes, bool Intact)?> OpenAsync(string id, CancellationToken cancellationToken)
    {
        var pack = await db.ReportPacks.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (pack is null || await store.OpenReadAsync(pack.StorageKey, cancellationToken) is not { } stream)
        {
            return null;
        }

        await using (stream)
        {
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken);
            var bytes = buffer.ToArray();
            return (pack, bytes, string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), pack.Sha256, StringComparison.Ordinal));
        }
    }
}
