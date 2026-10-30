namespace MembershipService.Contracts;

public sealed record CampaignRequest(string? Slug, string? NameEn, string? NameBn, DateTimeOffset? OpensAt, DateTimeOffset? ClosesAt);

public sealed record CampaignRecord(string Id, string Slug, string NameEn, string NameBn, DateTimeOffset OpensAt, DateTimeOffset? ClosesAt, DateTimeOffset? ClosedAt, bool Open);

public sealed record PublicCampaign(string Slug, LocalizedName Name, DateTimeOffset OpensAt, DateTimeOffset? ClosesAt);

public sealed record LocalizedName(string En, string Bn);

/// <summary>
/// `intake` is open, closed, or unrestricted (no campaign is defined, so applications are always accepted). The remaining
/// fields are the shape the public site reads: `state` is open, upcoming or closed (null when unrestricted, which the site
/// treats as "no information", leaving its static form untouched), with dates as yyyy-MM-dd.
/// </summary>
public sealed record IntakeState(
    string Intake,
    PublicCampaign? Campaign,
    string? State = null,
    string? Id = null,
    LocalizedName? Name = null,
    string? OpensOn = null,
    string? ClosesOn = null,
    string? NextOpensOn = null);

public sealed record StatusLookupRequest(string? Reference, string? Email);

public sealed record StatusEvent(string Status, DateTimeOffset At);

public sealed record StatusTimeline(IReadOnlyList<StatusEvent> Timeline);
