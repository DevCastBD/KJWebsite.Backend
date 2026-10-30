namespace MembershipService.Data.Entities;

public static class CommitteeKinds
{
    public const string Executive = "executive";
    public const string Advisory = "advisory";
    public const string Project = "project";
    public const string Chapter = "chapter";

    public static readonly IReadOnlyList<string> All = [Executive, Advisory, Project, Chapter];
}

/// <summary>A body of the organisation: an executive or advisory committee, a project committee or a geographic chapter. Nests under a parent.</summary>
public sealed class CommitteeEntity
{
    public string Id { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string Kind { get; set; } = CommitteeKinds.Project;
    public string NameEn { get; set; } = string.Empty;
    public string NameBn { get; set; } = string.Empty;

    /// <summary>True when only one name was supplied and the Bengali one is a copy, so a translator can find it.</summary>
    public bool NameBnIsFallback { get; set; }

    public string? ParentId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Archived committees leave the public chart but keep their history.</summary>
    public DateTimeOffset? ArchivedAt { get; set; }
}

/// <summary>
/// One person's term in one position. Terms are ended, never deleted: `EndsOn` null means open-ended, and a term is current from
/// `StartsOn` through `EndsOn`, both inclusive.
/// </summary>
public sealed class CommitteeMemberEntity
{
    public string Id { get; set; } = string.Empty;
    public string CommitteeId { get; set; } = string.Empty;
    public string MemberId { get; set; } = string.Empty;
    public string PositionKey { get; set; } = "member";
    public string PositionEn { get; set; } = string.Empty;
    public string PositionBn { get; set; } = string.Empty;
    public DateOnly StartsOn { get; set; }
    public DateOnly? EndsOn { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public bool IsCurrent(DateOnly today) => StartsOn <= today && (EndsOn is null || EndsOn >= today);
}
