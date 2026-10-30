using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;

namespace CtaSubmissionService.Contracts;

/// <summary>Query string of the admin list and export endpoints.</summary>
public sealed record SubmissionListQuery(
    [FromQuery(Name = "status")] string? Status = null,
    [FromQuery(Name = "formName")] string? FormName = null,
    [FromQuery(Name = "ctaType")] string? CtaType = null,
    [FromQuery(Name = "language")] string? Language = null,
    [FromQuery(Name = "assigneeId")] string? AssigneeId = null,
    [FromQuery(Name = "q")] string? Q = null,
    [FromQuery(Name = "from")] DateTimeOffset? From = null,
    [FromQuery(Name = "to")] DateTimeOffset? To = null,
    [FromQuery(Name = "limit")] int? Limit = null,
    [FromQuery(Name = "offset")] int? Offset = null);

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

/// <summary>Response to a stored submission. <c>created_at</c> keeps the snake_case name the frontend already reads.</summary>
public sealed record SubmissionCreated(string Id, string Status, [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt);
