using System.Text.RegularExpressions;
using FinanceService.Data.Entities;
using KJWebsite.BuildingBlocks.Finance;

namespace FinanceService.Donations;

/// <summary>A donation request that passed every check, with money already exact and rules applied.</summary>
public sealed record ValidDonation(
    Money Amount,
    Money Fee,
    string? RecurringInterval,
    string DesignationType,
    string? DesignationId,
    string DonorName,
    string DonorEmail,
    string? DonorPhone,
    bool Anonymous,
    string? Dedication,
    string Language);

public static partial class DonationValidator
{
    private const int MaxDedication = 500;

    [GeneratedRegex(@"^[a-z0-9]+(?:-[a-z0-9]+)*\z", RegexOptions.CultureInvariant)]
    private static partial Regex Slug();

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex Email();

    [GeneratedRegex(@"^\+?[0-9 ()-]{6,30}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Phone();

    public static (ValidDonation? Donation, List<string> Invalid) Validate(DonationRequest request, DonationOptions options)
    {
        var invalid = new List<string>();

        Money? amount = null;
        CurrencyRule? rule = null;
        if (!options.Currencies.TryGetValue(request.Currency ?? string.Empty, out rule))
        {
            invalid.Add("currency");
        }
        else
        {
            var parsed = Money.Parse(request.Amount, request.Currency);
            if (!parsed.IsSuccess || parsed.Value.Amount < rule.Min || parsed.Value.Amount > rule.Max)
            {
                invalid.Add("amount");
            }
            else
            {
                amount = parsed.Value;
            }
        }

        string? interval = null;
        switch (request.Frequency)
        {
            case null or "" or "once":
                break;
            case "monthly":
                interval = RecurringIntervals.Monthly;
                break;
            case "yearly":
                interval = RecurringIntervals.Yearly;
                break;
            default:
                invalid.Add("frequency");
                break;
        }

        var designationType = request.Designation?.Type ?? Designations.General;
        var designationId = request.Designation?.Id?.Trim();
        if (!Designations.All.Contains(designationType))
        {
            invalid.Add("designation.type");
        }
        else if (designationType == Designations.General)
        {
            designationId = null;
        }
        else if (designationId is not { Length: > 0 and <= 100 } || !Slug().IsMatch(designationId))
        {
            invalid.Add("designation.id");
        }

        var name = request.Donor?.Name?.Trim();
        if (name is not { Length: > 0 and <= 120 })
        {
            invalid.Add("donor.name");
        }

        var email = request.Donor?.Email?.Trim();
        if (email is not { Length: > 0 and <= 254 } || !Email().IsMatch(email))
        {
            invalid.Add("donor.email");
        }

        var phone = string.IsNullOrWhiteSpace(request.Donor?.Phone) ? null : request.Donor!.Phone!.Trim();
        if (phone is not null && !Phone().IsMatch(phone))
        {
            invalid.Add("donor.phone");
        }

        var dedication = string.IsNullOrWhiteSpace(request.Dedication) ? null : request.Dedication.Trim();
        if (dedication is { Length: > MaxDedication })
        {
            invalid.Add("dedication");
        }

        var language = request.Language ?? "en";
        if (language is not ("en" or "bn"))
        {
            invalid.Add("language");
        }

        if (invalid.Count > 0 || amount is null || rule is null)
        {
            return (null, invalid);
        }

        var fee = request.CoverFees ? amount.Value.GrossUp(rule.FeeRate) - amount.Value : Money.Zero(amount.Value.Currency);
        return (new ValidDonation(amount.Value, fee, interval, designationType, designationId, name!, email!, phone, request.Anonymous, dedication, language), invalid);
    }
}
