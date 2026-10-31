using Iso = KJWebsite.BuildingBlocks.Finance.Currencies;

namespace FinanceService.Donations;

/// <summary>
/// What a donor may give, per currency. `FeeRate` is the share a gateway takes, used only to size the cover-the-fee surcharge; it is a
/// placeholder until the merchant accounts and their real fees are settled (ADR-008, #111) and is overridden under `Finance:Donations`.
/// </summary>
public sealed class DonationOptions
{
    public const string SectionName = "Finance:Donations";

    public string DefaultCurrency { get; set; } = Iso.Bdt;

    public Dictionary<string, CurrencyRule> Currencies { get; set; } = new(StringComparer.Ordinal)
    {
        [Iso.Bdt] = new() { Presets = [500m, 1000m, 2500m, 5000m], Min = 50m, Max = 5_000_000m, FeeRate = 0.025m },
        [Iso.Usd] = new() { Presets = [10m, 25m, 50m, 100m], Min = 1m, Max = 50_000m, FeeRate = 0.039m },
        [Iso.Gbp] = new() { Presets = [10m, 25m, 50m, 100m], Min = 1m, Max = 40_000m, FeeRate = 0.039m },
        [Iso.Eur] = new() { Presets = [10m, 25m, 50m, 100m], Min = 1m, Max = 45_000m, FeeRate = 0.039m }
    };
}

public sealed class CurrencyRule
{
    public decimal[] Presets { get; set; } = [];
    public decimal Min { get; set; } = 1m;
    public decimal Max { get; set; } = 1_000_000m;
    public decimal FeeRate { get; set; }
}
