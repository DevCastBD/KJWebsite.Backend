namespace MembershipService.Contracts;

public sealed record CampaignRequest(string? Slug, string? NameEn, string? NameBn, DateTimeOffset? OpensAt, DateTimeOffset? ClosesAt);

public sealed record CampaignRecord(string Id, string Slug, string NameEn, string NameBn, DateTimeOffset OpensAt, DateTimeOffset? ClosesAt, DateTimeOffset? ClosedAt, bool Open);

public sealed record PublicCampaign(string Slug, LocalizedName Name, DateTimeOffset OpensAt, DateTimeOffset? ClosesAt);

public sealed record LocalizedName(string En, string Bn);

/// <summary>`intake` is open, closed, or unrestricted (no campaign is defined, so applications are always accepted).</summary>
public sealed record IntakeState(string Intake, PublicCampaign? Campaign);

public sealed record StatusLookupRequest(string? Reference, string? Email);

public sealed record StatusEvent(string Status, DateTimeOffset At);

public sealed record StatusTimeline(IReadOnlyList<StatusEvent> Timeline);
