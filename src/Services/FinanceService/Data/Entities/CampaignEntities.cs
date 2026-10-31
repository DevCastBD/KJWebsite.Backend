namespace FinanceService.Data.Entities;

public static class CampaignStatuses
{
    public const string Draft = "draft";
    public const string Active = "active";
    public const string Ended = "ended";

    public static readonly IReadOnlyList<string> All = [Draft, Active, Ended];
}

/// <summary>
/// A fundraising campaign with a goal in one currency. Donations are designated to it by `Slug`. The slug never changes (it is the public identifier); the other
/// fields can be edited and are audited. What a campaign has raised is not stored here: it is derived from receipts.
/// </summary>
public sealed class CampaignEntity
{
    public string Id { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? TitleBn { get; set; }
    public decimal GoalAmount { get; set; }
    public string Currency { get; set; } = string.Empty;
    public string Status { get; set; } = CampaignStatuses.Draft;
    public DateOnly? StartsOn { get; set; }
    public DateOnly? EndsOn { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Takes new public gifts: active, started and not past its end date.</summary>
    public bool Accepting(DateOnly today) => Status == CampaignStatuses.Active && (StartsOn is null || StartsOn <= today) && (EndsOn is null || EndsOn >= today);
}
