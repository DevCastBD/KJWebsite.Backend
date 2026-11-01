namespace FinanceService.Expenses;

public sealed class ApprovalTier
{
    /// <summary>The tier covers amounts below this; null means no upper limit (the last tier).</summary>
    public decimal? Below { get; set; }

    /// <summary>How many different people must approve an expense in this tier.</summary>
    public int Approvers { get; set; } = 1;
}

/// <summary>
/// Expense rules under `Finance:Expenses`. Approval tiers are per currency and ordered by `Below`; with none configured for a currency the defaults apply. Categories map the
/// words finance uses to ledger expense accounts. Both are configuration, never hard-coded behaviour.
/// </summary>
public sealed class ExpenseOptions
{
    public const string SectionName = "Finance:Expenses";

    public Dictionary<string, ApprovalTier[]> Approvals { get; set; } = new(StringComparer.Ordinal);

    public Dictionary<string, string> Categories { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether at least one receipt must be attached before the first approval.</summary>
    public bool ReceiptRequired { get; set; } = true;

    public static readonly IReadOnlyDictionary<string, ApprovalTier[]> DefaultApprovals = new Dictionary<string, ApprovalTier[]>(StringComparer.Ordinal)
    {
        ["BDT"] = [new() { Below = 10_000m, Approvers = 1 }, new() { Below = 100_000m, Approvers = 2 }, new() { Approvers = 3 }],
        ["USD"] = [new() { Below = 100m, Approvers = 1 }, new() { Below = 1_000m, Approvers = 2 }, new() { Approvers = 3 }],
        ["GBP"] = [new() { Below = 80m, Approvers = 1 }, new() { Below = 800m, Approvers = 2 }, new() { Approvers = 3 }],
        ["EUR"] = [new() { Below = 90m, Approvers = 1 }, new() { Below = 900m, Approvers = 2 }, new() { Approvers = 3 }]
    };

    public static readonly IReadOnlyDictionary<string, string> DefaultCategories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["programme"] = "5010",
        ["fundraising"] = "5020",
        ["administrative"] = "5030",
        ["events"] = "5050",
        ["other"] = "5900"
    };

    public IReadOnlyDictionary<string, string> EffectiveCategories => Categories.Count > 0 ? Categories : DefaultCategories;

    /// <summary>How many different approvers an expense of this amount needs. The first tier whose limit the amount is below; the open-ended tier otherwise.</summary>
    public int RequiredApprovers(string currency, decimal amount)
    {
        var tiers = Approvals.TryGetValue(currency, out var configured) && configured.Length > 0 ? configured : DefaultApprovals.GetValueOrDefault(currency) ?? [new ApprovalTier { Approvers = 3 }];
        var ordered = tiers.OrderBy(t => t.Below ?? decimal.MaxValue).ToList();
        var tier = ordered.FirstOrDefault(t => t.Below is { } limit && amount < limit) ?? ordered[^1];
        return Math.Max(1, tier.Approvers);
    }
}
