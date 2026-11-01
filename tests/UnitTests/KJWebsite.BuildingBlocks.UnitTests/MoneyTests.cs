using System;
using System.Linq;
using FluentAssertions;
using KJWebsite.BuildingBlocks.Finance;

namespace KJWebsite.BuildingBlocks.UnitTests;

/// <summary>
/// Properties of monetary arithmetic, checked over thousands of seeded random amounts (the seed is fixed so a failure reproduces).
/// Amounts are generated as whole minor units, the way money exists, then divided by 100.
/// </summary>
public sealed class MoneyTests
{
    private const int Iterations = 5_000;
    private static readonly string[] AllCurrencies = [.. Currencies.Supported];

    private static decimal RandomAmount(Random random, long maxMinor = 100_000_000_000L) => random.NextInt64(-maxMinor, maxMinor + 1) / 100m;

    private static Money RandomMoney(Random random, string? currency = null, long maxMinor = 100_000_000_000L) =>
        Money.Of(RandomAmount(random, maxMinor), currency ?? AllCurrencies[random.Next(AllCurrencies.Length)]);

    [Fact]
    public void AdditionIsCommutativeAssociativeAndHasZeroAsIdentity()
    {
        var random = new Random(20261031);
        for (var i = 0; i < Iterations; i++)
        {
            var currency = AllCurrencies[random.Next(AllCurrencies.Length)];
            var a = RandomMoney(random, currency);
            var b = RandomMoney(random, currency);
            var c = RandomMoney(random, currency);

            (a + b).Should().Be(b + a);
            ((a + b) + c).Should().Be(a + (b + c));
            (a + Money.Zero(currency)).Should().Be(a);
        }
    }

    [Fact]
    public void SubtractionUndoesAdditionAndNegationIsItsOwnInverse()
    {
        var random = new Random(7);
        for (var i = 0; i < Iterations; i++)
        {
            var currency = AllCurrencies[random.Next(AllCurrencies.Length)];
            var a = RandomMoney(random, currency);
            var b = RandomMoney(random, currency);

            ((a + b) - b).Should().Be(a);
            (a - a).IsZero.Should().BeTrue();
            (a + a.Negate()).IsZero.Should().BeTrue();
            a.Negate().Negate().Should().Be(a);
        }
    }

    [Fact]
    public void ResultsNeverCarryMoreThanTwoDecimals()
    {
        var random = new Random(11);
        for (var i = 0; i < Iterations; i++)
        {
            var currency = AllCurrencies[random.Next(AllCurrencies.Length)];
            var sum = RandomMoney(random, currency) + RandomMoney(random, currency);
            decimal.Round(sum.Amount, 2).Should().Be(sum.Amount);
        }
    }

    [Fact]
    public void SumOfManyAmountsEqualsTheSumOfTheirMinorUnits()
    {
        var random = new Random(13);
        for (var i = 0; i < 500; i++)
        {
            var minors = Enumerable.Range(0, random.Next(0, 50)).Select(_ => random.NextInt64(1, 1_000_000_000L)).ToList();
            var money = minors.Select(m => Money.Of(m / 100m, Currencies.Bdt));
            Money.Sum(money, Currencies.Bdt).Amount.Should().Be(minors.Sum() / 100m);
        }
    }

    [Fact]
    public void ManyTenthsAddUpExactlyUnlikeFloatingPoint()
    {
        var tenCents = Money.Of(0.10m, Currencies.Usd);
        Money.Sum(Enumerable.Repeat(tenCents, 10), Currencies.Usd).Should().Be(Money.Of(1.00m, Currencies.Usd));
    }

    [Fact]
    public void MixingCurrenciesThrowsInsteadOfConverting()
    {
        var bdt = Money.Of(100m, Currencies.Bdt);
        var usd = Money.Of(1m, Currencies.Usd);

        ((Func<Money>)(() => bdt + usd)).Should().Throw<CurrencyMismatchException>();
        ((Func<Money>)(() => bdt - usd)).Should().Throw<CurrencyMismatchException>();
    }

    [Fact]
    public void GrossUpCoversTheFeeAndOvershootsByLessThanOneCent()
    {
        var random = new Random(17);
        for (var i = 0; i < Iterations; i++)
        {
            var rate = random.Next(0, 100) / 1000m; // 0.000 - 0.099
            var amount = Money.Of(random.NextInt64(1, 100_000_000L) / 100m, AllCurrencies[random.Next(AllCurrencies.Length)]);

            var charged = amount.GrossUp(rate);
            var fee = charged - amount;

            fee.IsNegative.Should().BeFalse();
            // After the gateway takes its share the designated amount is left...
            (charged.Amount * (1m - rate)).Should().BeGreaterThanOrEqualTo(amount.Amount);
            // ...and the donor is not charged a cent more than needed.
            ((charged.Amount - 0.01m) * (1m - rate)).Should().BeLessThan(amount.Amount);
        }
    }

    [Fact]
    public void GrossUpWithNoFeeChargesTheAmountItself()
    {
        var amount = Money.Of(1250.50m, Currencies.Bdt);
        amount.GrossUp(0m).Should().Be(amount);
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1)]
    [InlineData(1.5)]
    public void GrossUpRefusesImpossibleRates(double rate)
    {
        ((Func<Money>)(() => Money.Of(10m, Currencies.Bdt).GrossUp((decimal)rate))).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData("1250.50", 1250.50)]
    [InlineData("0.5", 0.5)]
    [InlineData("100", 100)]
    [InlineData("-5.25", -5.25)]
    public void ParseReadsPlainDecimalStrings(string text, double expected)
    {
        var result = Money.Parse(text, Currencies.Bdt);

        result.IsSuccess.Should().BeTrue();
        result.Value.Amount.Should().Be((decimal)expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1,250.50")]
    [InlineData("1e3")]
    [InlineData("12.345")]
    [InlineData(".5")]
    [InlineData("+5")]
    [InlineData(" 5")]
    [InlineData("5\n")]
    [InlineData("১২৫০")]
    [InlineData("99999999999999999")]
    public void ParseRefusesAnythingButAPlainDecimal(string text)
    {
        Money.Parse(text, Currencies.Bdt).IsSuccess.Should().BeFalse();
    }

    [Fact]
    public void ParseRefusesAThirdDecimalInsteadOfRounding()
    {
        Money.Parse("10.005", Currencies.Usd).IsSuccess.Should().BeFalse();
        Money.TryCreate(10.005m, Currencies.Usd).IsSuccess.Should().BeFalse();
    }

    [Theory]
    [InlineData("XYZ")]
    [InlineData("bdt")]
    [InlineData("")]
    [InlineData(null)]
    public void UnsupportedCurrenciesAreRefused(string? currency)
    {
        Money.TryCreate(10m, currency).IsSuccess.Should().BeFalse();
    }

    [Fact]
    public void TheLargestStorableAmountIsAcceptedAndOneCentMoreIsNot()
    {
        Money.TryCreate(Money.MaxAmount, Currencies.Bdt).IsSuccess.Should().BeTrue();
        Money.TryCreate(Money.MaxAmount + 0.01m, Currencies.Bdt).IsSuccess.Should().BeFalse();
    }

    [Fact]
    public void AdditionThatWouldOverflowStorageThrows()
    {
        var max = Money.Of(Money.MaxAmount, Currencies.Bdt);
        ((Func<Money>)(() => max + Money.Of(0.01m, Currencies.Bdt))).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void DecimalStringAlwaysHasTwoDecimalsAndAPoint()
    {
        var random = new Random(23);
        for (var i = 0; i < 1_000; i++)
        {
            var money = RandomMoney(random);
            var text = money.ToDecimalString();

            text.Should().MatchRegex(@"^-?\d+\.\d{2}$");
            Money.Parse(text, money.Currency).Value.Should().Be(money);
        }
    }
}
