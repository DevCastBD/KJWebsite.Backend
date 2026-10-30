using System.Text.Json.Serialization;

namespace CtaSubmissionService.Contracts;

/// <summary>Query string of the admin list and export endpoints.</summary>
public sealed record SubmissionListQuery(
    string? Status = null,
    string? FormName = null,
    string? CtaType = null,
    string? Language = null,
    string? AssigneeId = null,
    string? Q = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    int? Limit = null,
    int? Offset = null);

public sealed record SubmissionSummary(
    string Id,
    string FormName,
    string CtaType,
    string Language,
    string Status,
    string? AssigneeId,
    string? Name,
    string? Email,
    DateTimeOffset SubmittedAt,
    DateTimeOffset CreatedAt);

public sealed record SubmissionDetail(
    string Id,
    string FormName,
    string CtaType,
    string Language,
    string SourcePath,
    string Status,
    string? AssigneeId,
    string? Notes,
    Dictionary<string, object?> Values,
    DateTimeOffset SubmittedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    IReadOnlyList<SubmissionAuditView> History);

public sealed record SubmissionAuditView(string Actor, string Action, string? Detail, DateTimeOffset At);

/// <summary>Omitted fields stay as they are; an empty <c>assigneeId</c> or <c>notes</c> clears it.</summary>
public sealed record SubmissionPatchRequest(string? Status, string? AssigneeId, string? Notes);

public sealed record SubmissionPatchResult([property: JsonPropertyName("id")] string Id, string Status, string? AssigneeId, string? Notes, DateTimeOffset UpdatedAt);
