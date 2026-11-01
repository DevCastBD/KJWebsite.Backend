using FinanceService.Data.Entities;
using FinanceService.Ledger;
using FluentAssertions;
using KJWebsite.BuildingBlocks.Finance;

namespace KJWebsite.IntegrationTests;

/// <summary>The ledger's pure rules: no database. Properties run over seeded random inputs so a failure reproduces.</summary>
public sealed class LedgerDomainTests
{
    private static readonly IReadOnlyDictionary<string, AccountEntity> Chart = LedgerAccounts.Seed
        .Select(a => new AccountEntity { Id = "acc_" + a.Code, Code = a.Code, Name = a.Name, Type = a.Type })
        .ToDictionary(a => a.Code);

    private static PostingDraft Draft(params LedgerLine[] lines) => new(new DateOnly(2026, 10, 1), "memo", PostingSources.Adjustment, null, lines);

    private static Money Bdt(decimal amount) => Money.Of(amount, "BDT");

    [Theory]
    [InlineData("2026-06-30", 7, "2025-26")]
    [InlineData("2026-07-01", 7, "2026-27")]
    [InlineData("2026-12-31", 7, "2026-27")]
    [InlineData("2027-01-01", 7, "2026-27")]
    [InlineData("2027-06-30", 7, "2026-27")]
    [InlineData("2099-07-15", 7, "2099-00")]
    [InlineData("2026-01-01", 1, "2026")]
    [InlineData("2026-12-31", 1, "2026")]
    [InlineData("2026-03-31", 4, "2025-26")]
    [InlineData("2026-04-01", 4, "2026-27")]
    [InlineData("2026-11-30", 12, "2025-26")]
    [InlineData("2026-12-01", 12, "2026-27")]
    public void FiscalYearLabelsFollowTheConfiguredStartMonth(string date, int startMonth, string expected)
    {
        FiscalCalendar.FiscalYear(DateOnly.Parse(date, System.Globalization.CultureInfo.InvariantCulture), startMonth).Should().Be(expected);
    }

    [Fact]
    public void TheFiscalYearLabelChangesOnlyOnTheFirstDayOfTheStartMonth()
    {
        for (var startMonth = 1; startMonth <= 12; startMonth++)
        {
            var previous = FiscalCalendar.FiscalYear(new DateOnly(2020, 1, 1), startMonth);
            for (var day = new DateOnly(2020, 1, 2); day < new DateOnly(2032, 1, 1); day = day.AddDays(1))
            {
                var label = FiscalCalendar.FiscalYear(day, startMonth);
                (label != previous).Should().Be(day.Month == startMonth && day.Day == 1, $"{day:yyyy-MM-dd} with start month {startMonth}");
                previous = label;
            }
        }
    }

    [Theory]
    [InlineData("2026-10", true)]
    [InlineData("2026-01", true)]
    [InlineData("2026-13", false)]
    [InlineData("2026-1", false)]
    [InlineData("2026-00", false)]
    [InlineData("abcd-ef", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void PeriodKeysAreYearAndMonth(string? key, bool valid)
    {
        FiscalCalendar.TryParsePeriod(key, out var first).Should().Be(valid);
        if (valid)
        {
            first.Day.Should().Be(1);
            FiscalCalendar.PeriodKey(first).Should().Be(key);
        }
    }

    [Fact]
    public void OnlyTheOptionRangeForTheStartMonthIsAccepted()
    {
        new LedgerOptions { FiscalYearStartMonth = 7 }.Invoking(o => o.Validate()).Should().NotThrow();
        new LedgerOptions { FiscalYearStartMonth = 0 }.Invoking(o => o.Validate()).Should().Throw<InvalidOperationException>();
        new LedgerOptions { FiscalYearStartMonth = 13 }.Invoking(o => o.Validate()).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void ABalancedPostingIsAccepted()
    {
        PostingRules.Validate(Draft(LedgerLine.Debit("1010", Bdt(100m)), LedgerLine.Credit("4010", Bdt(100m))), Chart).IsSuccess.Should().BeTrue();
        PostingRules.Validate(Draft(LedgerLine.Debit("1010", Bdt(100m)), LedgerLine.Credit("4010", Bdt(60m)), LedgerLine.Credit("4020", Bdt(40m))), Chart).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void AnUnbalancedPostingIsRefused()
    {
        var result = PostingRules.Validate(Draft(LedgerLine.Debit("1010", Bdt(100m)), LedgerLine.Credit("4010", Bdt(99.99m))), Chart);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("POSTING_UNBALANCED");
    }

    [Fact]
    public void DebitsMustEqualCreditsInEveryCurrencySeparately()
    {
        var bdt = Money.Of(100m, "BDT");
        var usd = Money.Of(100m, "USD");

        // Balanced overall in digits, but BDT and USD are different currencies and nothing is converted.
        PostingRules.Validate(Draft(LedgerLine.Debit("1010", bdt), LedgerLine.Credit("4010", usd)), Chart).Error!.Code.Should().Be("POSTING_UNBALANCED");
        PostingRules.Validate(Draft(LedgerLine.Debit("1010", bdt), LedgerLine.Credit("4010", bdt), LedgerLine.Debit("1020", usd), LedgerLine.Credit("4020", usd)), Chart).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void ShapeProblemsAreNamed()
    {
        PostingRules.Validate(Draft(LedgerLine.Debit("1010", Bdt(1m))), Chart).Error!.Code.Should().Be("POSTING_TOO_FEW_LINES");
        PostingRules.Validate(Draft(), Chart).Error!.Code.Should().Be("POSTING_TOO_FEW_LINES");
        PostingRules.Validate(Draft(LedgerLine.Debit("1010", Bdt(0m)), LedgerLine.Credit("4010", Bdt(0m))), Chart).Error!.Code.Should().Be("POSTING_AMOUNT_INVALID");
        PostingRules.Validate(Draft(LedgerLine.Debit("1010", Bdt(-5m)), LedgerLine.Credit("4010", Bdt(-5m))), Chart).Error!.Code.Should().Be("POSTING_AMOUNT_INVALID");
        PostingRules.Validate(Draft(LedgerLine.Debit("9999", Bdt(5m)), LedgerLine.Credit("4010", Bdt(5m))), Chart).Error!.Code.Should().Be("POSTING_UNKNOWN_ACCOUNT");
        PostingRules.Validate(Draft(LedgerLine.Debit("1010", Bdt(5m)), LedgerLine.Credit("4010", Bdt(5m))) with { Memo = " " }, Chart).Error!.Code.Should().Be("POSTING_MEMO_INVALID");
        PostingRules.Validate(Draft(LedgerLine.Debit("1010", Bdt(5m)), LedgerLine.Credit("4010", Bdt(5m))) with { Memo = new string('x', 501) }, Chart).Error!.Code.Should().Be("POSTING_MEMO_INVALID");
    }

    [Fact]
    public void AnArchivedAccountTakesNoNewLines()
    {
        var archived = new Dictionary<string, AccountEntity>(Chart) { ["4010"] = new() { Id = "x", Code = "4010", Type = AccountTypes.Income, ArchivedAt = DateTimeOffset.UtcNow } };

        PostingRules.Validate(Draft(LedgerLine.Debit("1010", Bdt(5m)), LedgerLine.Credit("4010", Bdt(5m))), archived).Error!.Code.Should().Be("POSTING_ACCOUNT_ARCHIVED");
    }

    [Fact]
    public void RandomBalancedPostingsPassAndOneCentOffNeverDoes()
    {
        var random = new Random(37);
        var codes = Chart.Keys.ToArray();
        var currencies = Currencies.Supported.ToArray();
        for (var i = 0; i < 2_000; i++)
        {
            var lines = new List<LedgerLine>();
            foreach (var currency in currencies.OrderBy(_ => random.Next()).Take(random.Next(1, 3)))
            {
                var parts = Enumerable.Range(0, random.Next(1, 5)).Select(_ => random.NextInt64(1, 5_000_000L)).ToList();
                var total = parts.Sum();
                lines.AddRange(parts.Select(p => LedgerLine.Debit(codes[random.Next(codes.Length)], Money.Of(p / 100m, currency))));
                // Split the credit side differently from the debit side, to the same total.
                var cut = random.NextInt64(1, Math.Max(2, total));
                lines.Add(LedgerLine.Credit(codes[random.Next(codes.Length)], Money.Of(cut / 100m, currency)));
                lines.Add(LedgerLine.Credit(codes[random.Next(codes.Length)], Money.Of((total - cut) / 100m == 0m ? total / 100m : (total - cut) / 100m, currency)));
                if (total - cut == 0)
                {
                    lines.RemoveRange(lines.Count - 2, 2);
                    lines.Add(LedgerLine.Credit(codes[random.Next(codes.Length)], Money.Of(total / 100m, currency)));
                }
            }

            PostingRules.Validate(Draft([.. lines]), Chart).IsSuccess.Should().BeTrue($"iteration {i}");

            // Nudge one line by a cent: now a currency is out of balance.
            var index = random.Next(lines.Count);
            var nudged = lines[index] with { Amount = Money.Of(lines[index].Amount.Amount + 0.01m, lines[index].Amount.Currency) };
            lines[index] = nudged;
            PostingRules.Validate(Draft([.. lines]), Chart).Error!.Code.Should().Be("POSTING_UNBALANCED", $"iteration {i}");
        }
    }

    [Fact]
    public void BalancedHelperWorksPerCurrency()
    {
        PostingRules.Balanced([("BDT", 10m, 0m), ("BDT", 0m, 10m)]).Should().BeTrue();
        PostingRules.Balanced([("BDT", 10m, 0m), ("USD", 0m, 10m)]).Should().BeFalse();
        PostingRules.Balanced([]).Should().BeTrue();
    }

    [Fact]
    public void TheSeededChartIsWellFormed()
    {
        LedgerAccounts.Seed.Select(a => a.Code).Should().OnlyHaveUniqueItems();
        LedgerAccounts.Seed.Select(a => a.Type).Should().OnlyContain(t => AccountTypes.All.Contains(t));
        foreach (var code in new[] { LedgerAccounts.Cash, LedgerAccounts.Bank, LedgerAccounts.SslCommerzClearing, LedgerAccounts.BkashClearing, LedgerAccounts.NagadClearing,
                     LedgerAccounts.StripeClearing, LedgerAccounts.PayPalClearing, LedgerAccounts.OtherClearing, LedgerAccounts.DonationsGeneral, LedgerAccounts.DonationsDesignated, LedgerAccounts.FeeContributions })
        {
            LedgerAccounts.Seed.Select(a => a.Code).Should().Contain(code);
        }

        AccountTypes.IsDebitNormal(AccountTypes.Asset).Should().BeTrue();
        AccountTypes.IsDebitNormal(AccountTypes.Expense).Should().BeTrue();
        AccountTypes.IsDebitNormal(AccountTypes.Income).Should().BeFalse();
        AccountTypes.IsDebitNormal(AccountTypes.Liability).Should().BeFalse();
        AccountTypes.IsDebitNormal(AccountTypes.Equity).Should().BeFalse();
    }

    [Theory]
    [InlineData("sslcommerz", "1110")]
    [InlineData("bkash", "1120")]
    [InlineData("nagad", "1130")]
    [InlineData("stripe", "1140")]
    [InlineData("paypal", "1150")]
    [InlineData("cash", "1010")]
    [InlineData("bank", "1020")]
    [InlineData("cheque", "1020")]
    [InlineData("somethingnew", "1190")]
    public void TheMethodPicksWhereTheMoneySits(string method, string account)
    {
        LedgerAccounts.ClearingFor(method).Should().Be(account);
    }

    [Fact]
    public void ADonationPostingAlwaysBalancesAndSplitsTheFeeOut()
    {
        var random = new Random(41);
        for (var i = 0; i < 2_000; i++)
        {
            var currency = Currencies.Supported[random.Next(Currencies.Supported.Count)];
            var amount = Money.Of(random.NextInt64(100, 100_000_000L) / 100m, currency);
            var fee = random.Next(2) == 0 ? Money.Zero(currency) : amount.GrossUp(random.Next(1, 100) / 1000m) - amount;
            var donation = new DonationEntity
            {
                Id = "don_" + i,
                Reference = "KJD-0000000000",
                Amount = amount.Amount,
                FeeAmount = fee.Amount,
                Currency = currency,
                DesignationType = random.Next(2) == 0 ? Designations.General : Designations.Campaign
            };

            var draft = DonationPostings.ForConfirmation(donation, "bkash", new DateOnly(2026, 10, 1));

            PostingRules.Validate(draft, Chart).IsSuccess.Should().BeTrue($"iteration {i}");
            draft.SourceType.Should().Be("donation");
            draft.SourceId.Should().Be(donation.Id);
            var debit = draft.Lines.Single(l => l.Side == LedgerSide.Debit);
            (debit.AccountCode, debit.Amount).Should().Be(("1120", amount + fee));
            draft.Lines.Where(l => l.AccountCode == LedgerAccounts.FeeContributions).Select(l => l.Amount).Should().Equal(fee.IsZero ? [] : [fee]);
            draft.Lines.Single(l => l.AccountCode is LedgerAccounts.DonationsGeneral or LedgerAccounts.DonationsDesignated).AccountCode
                .Should().Be(donation.DesignationType == Designations.General ? LedgerAccounts.DonationsGeneral : LedgerAccounts.DonationsDesignated);
        }
    }

    [Fact]
    public void TrialBalanceLinesReadPositiveInTheirNormalDirection()
    {
        new TrialBalanceLine("1010", "Cash", AccountTypes.Asset, "BDT", 100m, 30m).Balance.Should().Be(70m);
        new TrialBalanceLine("4010", "Income", AccountTypes.Income, "BDT", 30m, 100m).Balance.Should().Be(70m);
        new CurrencyTrialBalance("BDT", [], 5m, 5m).Balanced.Should().BeTrue();
        new CurrencyTrialBalance("BDT", [], 5m, 6m).Balanced.Should().BeFalse();
    }
}
