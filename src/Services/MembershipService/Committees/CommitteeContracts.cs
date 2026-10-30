namespace MembershipService.Committees;

/// <summary>`Name` is the English name (what the back office sends today); `NameEn`/`NameBn` are the bilingual pair.</summary>
public sealed record CommitteeRequest(string? Slug, string? Kind, string? Name, string? NameEn, string? NameBn, string? ParentId, string? Chair = null);

public sealed record CommitteeView(
    string Id, string Slug, string Name, string NameEn, string NameBn, bool NameBnIsFallback, string Kind, string? ParentId, string Chair, bool Archived, int CurrentMembers);

public sealed record TermRequest(string? MemberId, string? PositionKey, string? PositionEn, string? PositionBn, DateOnly? StartsOn, DateOnly? EndsOn);

public sealed record EndTermRequest(DateOnly? EndsOn);

public sealed record TermView(string Id, string CommitteeId, string MemberId, string MemberCode, string MemberName, string PositionKey, string PositionEn, string PositionBn, DateOnly StartsOn, DateOnly? EndsOn, bool Current);

public sealed record PublicName(string En, string Bn);

public sealed record PublicPosition(string Key, string En, string Bn);

public sealed record PublicSeat(string MemberId, string Name, string? NameBn, PublicPosition Position, DateOnly StartsOn, DateOnly? EndsOn);

public sealed record OrgNode(string Id, string Slug, PublicName Name, string Kind, IReadOnlyList<PublicSeat> Members, IReadOnlyList<OrgNode> Children);

public sealed record OrgChart(DateOnly AsOf, IReadOnlyList<OrgNode> Items);

public sealed record CommitteeDetail(string Id, string Slug, PublicName Name, string Kind, string? ParentSlug, IReadOnlyList<PublicSeat> Current, IReadOnlyList<PublicSeat> Past);
