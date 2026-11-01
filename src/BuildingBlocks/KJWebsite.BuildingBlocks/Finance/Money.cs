using System.Globalization;

namespace KJWebsite.BuildingBlocks.Finance;

/// <summary>The currencies the organisation accepts (ISO 4217): BDT primary, the rest for diaspora donors.</summary>
public static class Currencies
{
    public const string Bdt = "BDT";
    public const string Usd = "USD";
    public const string Gbp = "GBP";
    public const string Eur = "EUR";

    public static readonly IReadOnlyList<string> Supported = [Bdt, Usd, Gbp, Eur];

    public static bool IsSupported(string? code) => code is not null && Supported.Contains(code, StringComparer.Ordinal);
}

public sealed class CurrencyMismatchException(string left, string right)
    : InvalidOperationException($"Cannot combine {left} with {right}: amounts in different currencies are never added or compared.");

/// <summary>
/// An exact amount of money: a `decimal` with two decimal places and an explicit ISO-4217 currency, never a float. Nothing here converts
/// between currencies or rounds silently: construction refuses a third decimal place, and combining two currencies throws.
/// Matches the `numeric(18,2)` database columns, so a value always survives a round trip.
/// </summary>
public readonly record struct Money
{
    /// <summary>The largest value `numeric(18,2)` holds.</summary>
    public const decimal MaxAmount = 9_999_999_999_999_999.99m;

    private static readonly System.Text.RegularExpressions.Regex DecimalText = new(@"^-?[0-9]{1,16}(\.[0-9]{1,2})?\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    public decimal Amount { get; }

    public string Currency { get; }

    private Money(decimal amount, string currency)
    {
        Amount = amount;
        Currency = currency;
    }

    /// <summary>Throws when the currency is unsupported, the amount has more than two decimals or it is outside `numeric(18,2)`.</summary>
    public static Money Of(decimal amount, string currency)
    {
        var result = TryCreate(amount, currency);
        return result.IsSuccess ? result.Value : throw new ArgumentException(result.Error!.Message, nameof(amount));
    }

    public static Result<Money> TryCreate(decimal amount, string? currency)
    {
        if (!Currencies.IsSupported(currency))
        {
            return Result.Failure<Money>("INVALID_CURRENCY", $"Currency must be one of {string.Join(", ", Currencies.Supported)}.");
        }

        if (decimal.Round(amount, 2) != amount)
        {
            return Result.Failure<Money>("INVALID_AMOUNT", "An amount has at most two decimal places.");
        }

        if (Math.Abs(amount) > MaxAmount)
        {
            return Result.Failure<Money>("INVALID_AMOUNT", "The amount is too large.");
        }

        return Result.Success(new Money(decimal.Round(amount, 2), currency!));
    }

    /// <summary>Reads a decimal string such as "1250.50": plain digits, an optional point and at most two decimals. No separators, exponent or sign other than a leading minus.</summary>
    public static Result<Money> Parse(string? text, string? currency)
    {
        if (text is null || !DecimalText.IsMatch(text))
        {
            return Result.Failure<Money>("INVALID_AMOUNT", "An amount is a plain decimal such as 1250.50 with at most two decimal places.");
        }

        return TryCreate(decimal.Parse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture), currency);
    }

    public static Money Zero(string currency) => Of(0m, currency);

    public bool IsZero => Amount == 0m;

    public bool IsPositive => Amount > 0m;

    public bool IsNegative => Amount < 0m;

    public Money Negate() => new(-Amount, Currency);

    public Money Add(Money other) => Combine(other, Amount + other.Amount);

    public Money Subtract(Money other) => Combine(other, Amount - other.Amount);

    public static Money operator +(Money left, Money right) => left.Add(right);

    public static Money operator -(Money left, Money right) => left.Subtract(right);

    public static Money operator -(Money value) => value.Negate();

    /// <summary>Sums amounts of one currency; an empty sequence is zero in <paramref name="currency"/>.</summary>
    public static Money Sum(IEnumerable<Money> values, string currency) => values.Aggregate(Zero(currency), (total, value) => total + value);

    /// <summary>
    /// The charge that leaves <see cref="Amount"/> after a gateway takes <paramref name="feeRate"/> of it, rounded up to the next cent so the
    /// donor never covers less than the fee. The fee is `GrossUp(rate) - this`; reconciliation sees both numbers.
    /// </summary>
    public Money GrossUp(decimal feeRate)
    {
        if (feeRate is < 0m or >= 1m)
        {
            throw new ArgumentOutOfRangeException(nameof(feeRate), "A fee rate is at least 0 and below 1.");
        }

        var gross = Math.Ceiling(Amount / (1m - feeRate) * 100m) / 100m;
        return Of(gross, Currency);
    }

    /// <summary>A decimal string with exactly two decimals and a point, never localised: "1250.50".</summary>
    public string ToDecimalString() => Amount.ToString("0.00", CultureInfo.InvariantCulture);

    public override string ToString() => $"{ToDecimalString()} {Currency}";

    private Money Combine(Money other, decimal amount)
    {
        if (!string.Equals(Currency, other.Currency, StringComparison.Ordinal))
        {
            throw new CurrencyMismatchException(Currency, other.Currency);
        }

        return Of(amount, Currency);
    }
}
