namespace CtaSubmissionService.Data.Entities;

/// <summary>A routing rule: the first active rule (lowest position) that matches an enquiry sets its owner, priority and, optionally, category.</summary>
public sealed class EngagementRuleEntity
{
    public string Id { get; set; } = string.Empty;
    public int Position { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? MatchCategory { get; set; }
    public string? MatchLanguage { get; set; }

    /// <summary>Case-insensitive text looked for in the submitted values.</summary>
    public string? MatchKeyword { get; set; }

    public string? AssignTo { get; set; }
    public string? Priority { get; set; }
    public string? SetCategory { get; set; }
    public bool Active { get; set; } = true;
}

/// <summary>The agreed first-response target for one category, in minutes. No row means no target and no clock.</summary>
public sealed class EngagementSlaTargetEntity
{
    public string Category { get; set; } = string.Empty;
    public int ResponseMinutes { get; set; }
}

/// <summary>Append-only history of what happened to an enquiry.</summary>
public sealed class EngagementEventEntity
{
    public long Id { get; set; }
    public string SubmissionId { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Actor { get; set; } = string.Empty;
    public string? Detail { get; set; }
    public DateTimeOffset At { get; set; }
}
