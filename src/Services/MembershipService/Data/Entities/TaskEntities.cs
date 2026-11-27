namespace MembershipService.Data.Entities;

public static class TaskStatuses
{
    public const string Todo = "todo";
    public const string InProgress = "in_progress";
    public const string Blocked = "blocked";
    public const string Done = "done";

    public static readonly string[] All = [Todo, InProgress, Blocked, Done];
    public static readonly string[] Open = [Todo, InProgress, Blocked];
}

/// <summary>
/// A lightweight task for a project or a committee: one assignee, an optional due date, a priority and four statuses. Deliberately no
/// custom fields or workflow: this is not a project-management product.
/// </summary>
public sealed class TaskAssignmentEntity
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string AssigneeMemberId { get; set; } = string.Empty;

    /// <summary>Exactly one of `ProjectSlug` and `CommitteeId` is set.</summary>
    public string? ProjectSlug { get; set; }

    public string? CommitteeId { get; set; }
    public DateOnly? DueOn { get; set; }

    /// <summary>`low`, `normal` or `high`.</summary>
    public string Priority { get; set; } = "normal";

    public string Status { get; set; } = TaskStatuses.Todo;
    public string CreatedBy { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Set when the "due soon" reminder was sent, so it is sent once per due date.</summary>
    public DateOnly? ReminderForDueOn { get; set; }

    public DateOnly? OverdueNoticeForDueOn { get; set; }
}

/// <summary>A message in a member's notification centre, written in the same commit as the change that caused it. Delivery by email is the outbox work (#32/#33).</summary>
public sealed class MemberNotificationEntity
{
    public long Id { get; set; }
    public string MemberId { get; set; } = string.Empty;
    public string Template { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
}
