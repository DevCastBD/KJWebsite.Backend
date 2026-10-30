namespace MembershipService.Review;

public sealed record StatusChangeRecord(string Status, DateTimeOffset At, string By, bool Notified);

public sealed record ReviewNoteRecord(string Id, DateTimeOffset At, string By, string Text);

/// <summary>Back-office view of an application. Internal: carries notes and score, never served by a public route.</summary>
public sealed record ReviewApplication(
    string Id,
    string Reference,
    string Name,
    string Email,
    string Language,
    DateTimeOffset SubmittedAt,
    string Status,
    string? CampaignId,
    string? ReviewerId,
    int? Score,
    DateTimeOffset? InterviewAt,
    IReadOnlyList<ReviewNoteRecord> Notes,
    IReadOnlyList<StatusChangeRecord> History,
    IReadOnlyList<string> NextStatuses,
    string? MemberCode);

public sealed record ApplicationPage(IReadOnlyList<ReviewApplication> Items, int Total);

public sealed record NoteRequest(string? Text);

public sealed record TransitionRequest(List<string>? Ids, string? To, DateTimeOffset? InterviewAt);

public sealed record TransitionResult(string Id, bool Ok, bool Notified, string? Error, string? MemberCode = null);

public sealed record TransitionResponse(IReadOnlyList<TransitionResult> Results);
