namespace KJWebsite.BuildingBlocks.DataQuality;

public sealed record DuplicateDecisionRequest(string? LeftId, string? RightId, string? Decision, string? Note);

public static class DuplicateDecisions
{
    public const string NotDuplicate = "not_duplicate";
    public const string ConfirmedDuplicate = "confirmed_duplicate";
    public static readonly IReadOnlyList<string> All = [NotDuplicate, ConfirmedDuplicate];

    /// <summary>Why: a person who rules on a pair must say what they looked at, so the audit log explains the decision later.</summary>
    public const int MinNoteLength = 5;

    public static string? Validate(DuplicateDecisionRequest request) =>
        string.IsNullOrWhiteSpace(request.LeftId) || string.IsNullOrWhiteSpace(request.RightId) || request.LeftId == request.RightId ? "leftId and rightId must be two different records."
        : request.Decision is null || !All.Contains(request.Decision) ? "decision must be 'not_duplicate' or 'confirmed_duplicate'."
        : request.Note is null || request.Note.Trim().Length < MinNoteLength || request.Note.Length > 500 ? $"note is required ({MinNoteLength}-500 characters): say what you checked."
        : null;
}
