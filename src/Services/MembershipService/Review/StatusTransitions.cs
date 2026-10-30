using MembershipService.Data.Entities;

namespace MembershipService.Review;

/// <summary>
/// The review pipeline's state machine. Forward one step at a time (awaiting, under_review, shortlisted,
/// interview_scheduled, approved); rejected, postponed and withdrawn from any open state; a postponed
/// application can be picked up again. Approved, rejected and withdrawn are final.
/// </summary>
public static class StatusTransitions
{
    public const string Final = "final";
    public const string InvalidTransition = "invalid_transition";
    public const string InterviewDateRequired = "interview_date_required";
    public const string UnknownStatus = "unknown_status";

    private static readonly Dictionary<string, string[]> Allowed = new()
    {
        [ApplicationStatuses.Awaiting] = [ApplicationStatuses.UnderReview, ApplicationStatuses.Rejected, ApplicationStatuses.Postponed, ApplicationStatuses.Withdrawn],
        [ApplicationStatuses.UnderReview] = [ApplicationStatuses.Shortlisted, ApplicationStatuses.Rejected, ApplicationStatuses.Postponed, ApplicationStatuses.Withdrawn],
        [ApplicationStatuses.Shortlisted] = [ApplicationStatuses.InterviewScheduled, ApplicationStatuses.Rejected, ApplicationStatuses.Postponed, ApplicationStatuses.Withdrawn],
        [ApplicationStatuses.InterviewScheduled] = [ApplicationStatuses.Approved, ApplicationStatuses.Rejected, ApplicationStatuses.Postponed, ApplicationStatuses.Withdrawn],
        [ApplicationStatuses.Postponed] = [ApplicationStatuses.UnderReview, ApplicationStatuses.Rejected, ApplicationStatuses.Withdrawn],
        [ApplicationStatuses.Approved] = [],
        [ApplicationStatuses.Rejected] = [],
        [ApplicationStatuses.Withdrawn] = []
    };

    public static bool IsFinal(string status) =>
        status is ApplicationStatuses.Approved or ApplicationStatuses.Rejected or ApplicationStatuses.Withdrawn;

    public static IReadOnlyList<string> NextFrom(string status) => Allowed.TryGetValue(status, out var next) ? next : [];

    /// <summary>Null when the move is allowed, otherwise the error code reported for that item.</summary>
    public static string? Check(string from, string to, DateTimeOffset? interviewAt)
    {
        if (!ApplicationStatuses.All.Contains(to))
        {
            return UnknownStatus;
        }

        if (IsFinal(from))
        {
            return Final;
        }

        if (!NextFrom(from).Contains(to))
        {
            return InvalidTransition;
        }

        return to == ApplicationStatuses.InterviewScheduled && interviewAt is null ? InterviewDateRequired : null;
    }
}
