using System.Security.Claims;
using System.Text.Json;
using CtaSubmissionService.Contracts;
using CtaSubmissionService.Data;
using CtaSubmissionService.Data.Entities;
using FluentValidation;
using KJWebsite.BuildingBlocks;
using KJWebsite.BuildingBlocks.Security;
using KJWebsite.BuildingBlocks.Validation;
using Microsoft.EntityFrameworkCore;

namespace CtaSubmissionService.Admin;

/// <summary>
/// The team's view of submissions: list, read, triage, export. Everything here exposes personal data, so each call needs a
/// permission, leaves an audit row naming who did it, and keeps the data out of logs.
/// </summary>
public static class SubmissionAdminEndpoints
{
    private const string Route = "/api/v1/admin/cta/submissions";
    private const int ExportLimit = 5000;

    public static void MapSubmissionAdmin(this WebApplication app)
    {
        app.MapGet(Route, async (HttpContext context, ClaimsPrincipal user, CtaDbContext db, IValidator<SubmissionListQuery> validator, [AsParameters] SubmissionListQuery query) =>
        {
            var validation = await validator.ValidateAsync(query);
            if (!validation.IsValid)
            {
                return ValidationProblem.Create(context, validation.Errors);
            }

            var limit = query.Limit ?? 20;
            var offset = query.Offset ?? 0;
            var filtered = Filter(db.Submissions.AsNoTracking(), query);
            var total = await filtered.CountAsync();
            var rows = await filtered.OrderByDescending(s => s.CreatedAt).ThenBy(s => s.Id).Skip(offset).Take(limit).ToListAsync();

            Audit(db, user, "list", null, FilterDescription(query));
            await db.SaveChangesAsync();

            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new PagingEnvelope<SubmissionSummary>([.. rows.Select(ToSummary)], total, null, limit, offset));
        })
        .WithName("ListCtaSubmissions")
        .WithSummary("List submissions (paged, filterable)")
        .WithDescription("Filters: status, formName, ctaType, language, assigneeId, q (id or name), from, to. Newest first. Requires `cta.view`; the read is audited.")
        .Produces<PagingEnvelope<SubmissionSummary>>(StatusCodes.Status200OK)
        .WithTags("Admin — CTA")
        .RequirePermission(Permissions.CtaView);

        app.MapGet($"{Route}/export", async (HttpContext context, ClaimsPrincipal user, CtaDbContext db, IValidator<SubmissionListQuery> validator, [AsParameters] SubmissionListQuery query) =>
        {
            var validation = await validator.ValidateAsync(query with { Limit = null, Offset = null });
            if (!validation.IsValid)
            {
                return ValidationProblem.Create(context, validation.Errors);
            }

            var rows = await Filter(db.Submissions.AsNoTracking(), query).OrderByDescending(s => s.CreatedAt).ThenBy(s => s.Id).Take(ExportLimit).ToListAsync();
            Audit(db, user, "export", null, $"{rows.Count} rows; {FilterDescription(query)}");
            await db.SaveChangesAsync();

            var csv = Csv.Build(
                ["id", "formName", "ctaType", "language", "status", "assigneeId", "submittedAt", "createdAt", "firstName", "lastName", "email", "phone", "message", "notes", "values"],
                rows.Select(r =>
                {
                    var values = ParseValues(r.ValuesJson);
                    return (IEnumerable<string?>)
                    [
                        r.Id, r.FormName, r.CtaType, r.Language, r.Status, r.AssigneeId,
                        r.SubmittedAt.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                        r.CreatedAt.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                        r.FirstName ?? Read(values, "firstName"), r.LastName ?? Read(values, "lastName"),
                        Read(values, "email"), Read(values, "phone") ?? Read(values, "phoneNumber"), Read(values, "message"),
                        r.Notes, r.ValuesJson
                    ];
                }));

            context.Response.Headers.CacheControl = "no-store";
            return Results.File(csv, "text/csv; charset=utf-8", $"cta-submissions-{DateTime.UtcNow:yyyyMMdd-HHmm}.csv");
        })
        .WithName("ExportCtaSubmissions")
        .WithSummary("Export submissions as CSV")
        .WithDescription($"UTF-8 with a byte-order mark so Excel shows Bengali correctly; at most {ExportLimit} rows; same filters as the list. Requires `cta.export`; the export is audited.")
        .WithTags("Admin — CTA")
        .Produces<byte[]>(StatusCodes.Status200OK, "text/csv")
        .RequirePermission(Permissions.CtaExport);

        app.MapGet($"{Route}/{{id}}", async (HttpContext context, ClaimsPrincipal user, CtaDbContext db, string id) =>
        {
            var row = await db.Submissions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id);
            if (row is null)
            {
                return ApiError.Create(context, StatusCodes.Status404NotFound, "NOT_FOUND", "Submission not found.");
            }

            Audit(db, user, "view", id, null);
            await db.SaveChangesAsync();

            var history = await db.SubmissionAudit.AsNoTracking().Where(a => a.SubmissionId == id && a.Action == "update")
                .OrderByDescending(a => a.At).Take(50).ToListAsync();
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new SubmissionDetail(row.Id, row.FormName, row.CtaType, row.Language, row.SourcePath, row.Status, row.AssigneeId,
                row.Notes, ParseValues(row.ValuesJson), row.SubmittedAt, row.CreatedAt, row.UpdatedAt,
                [.. history.Select(h => new SubmissionAuditView(h.Actor, h.Action, h.Detail, h.At))]));
        })
        .WithName("GetCtaSubmission")
        .WithSummary("Get one submission")
        .WithDescription("Full submission with its update history. Requires `cta.view`; the read is audited.")
        .Produces<SubmissionDetail>(StatusCodes.Status200OK)
        .WithTags("Admin — CTA")
        .RequirePermission(Permissions.CtaView);

        app.MapPatch($"{Route}/{{id}}", async (HttpContext context, ClaimsPrincipal user, CtaDbContext db, string id, SubmissionPatchRequest patch) =>
        {
            var row = await db.Submissions.FirstOrDefaultAsync(s => s.Id == id);
            if (row is null)
            {
                return ApiError.Create(context, StatusCodes.Status404NotFound, "NOT_FOUND", "Submission not found.");
            }

            var changes = new List<string>();
            if (patch.Status is not null && patch.Status != row.Status)
            {
                changes.Add($"status: {row.Status} -> {patch.Status}");
                row.Status = patch.Status;
            }

            if (patch.AssigneeId is not null)
            {
                var assignee = patch.AssigneeId.Length == 0 ? null : patch.AssigneeId;
                if (assignee != row.AssigneeId)
                {
                    changes.Add($"assignee: {row.AssigneeId ?? "none"} -> {assignee ?? "none"}");
                    row.AssigneeId = assignee;
                }
            }

            if (patch.Notes is not null)
            {
                var notes = patch.Notes.Length == 0 ? null : patch.Notes;
                if (notes != row.Notes)
                {
                    // The audit row records that notes changed, never their (personal) content.
                    changes.Add("notes changed");
                    row.Notes = notes;
                }
            }

            if (changes.Count > 0)
            {
                row.UpdatedAt = DateTimeOffset.UtcNow;
                Audit(db, user, "update", id, string.Join("; ", changes));
                await db.SaveChangesAsync();
            }

            return Results.Ok(new SubmissionPatchResult(row.Id, row.Status, row.AssigneeId, row.Notes, row.UpdatedAt ?? row.CreatedAt));
        })
        .WithName("UpdateCtaSubmission")
        .WithSummary("Update status, assignee or notes")
        .WithDescription("Omitted fields are unchanged; an empty assigneeId or notes clears it. Requires `cta.manage`; every change is audited.")
        .Produces<SubmissionPatchResult>(StatusCodes.Status200OK)
        .WithTags("Admin — CTA")
        .WithValidation<SubmissionPatchRequest>()
        .RequirePermission(Permissions.CtaManage);
    }

    private static IQueryable<CtaSubmissionEntity> Filter(IQueryable<CtaSubmissionEntity> source, SubmissionListQuery q)
    {
        if (q.Status is not null)
        {
            source = source.Where(s => s.Status == q.Status);
        }

        if (!string.IsNullOrWhiteSpace(q.FormName))
        {
            source = source.Where(s => s.FormName == q.FormName);
        }

        if (!string.IsNullOrWhiteSpace(q.CtaType))
        {
            source = source.Where(s => s.CtaType == q.CtaType);
        }

        if (!string.IsNullOrWhiteSpace(q.Language))
        {
            source = source.Where(s => s.Language == q.Language);
        }

        if (!string.IsNullOrWhiteSpace(q.AssigneeId))
        {
            source = source.Where(s => s.AssigneeId == q.AssigneeId);
        }

        if (!string.IsNullOrWhiteSpace(q.Q))
        {
            var pattern = "%" + q.Q.Trim().ToLowerInvariant().Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";
            // EF translates ToLower() to the database's lower(); ToLowerInvariant() is not translatable. Culture is the database's, not the process's.
#pragma warning disable CA1304, CA1311
            source = source.Where(s => EF.Functions.Like(s.Id.ToLower(), pattern, "\\")
                || (s.FirstName != null && EF.Functions.Like(s.FirstName.ToLower(), pattern, "\\"))
                || (s.LastName != null && EF.Functions.Like(s.LastName.ToLower(), pattern, "\\"))
                || EF.Functions.Like(s.ValuesJson.ToLower(), pattern, "\\"));
#pragma warning restore CA1304, CA1311
        }

        if (q.From is { } from)
        {
            source = source.Where(s => s.CreatedAt >= from);
        }

        if (q.To is { } to)
        {
            source = source.Where(s => s.CreatedAt <= to);
        }

        return source;
    }

    // Filter values only: free-text search terms can be personal data, so they are described, not recorded.
    private static string FilterDescription(SubmissionListQuery q) =>
        string.Join(", ", new[]
        {
            q.Status is null ? null : $"status={q.Status}", q.FormName is null ? null : $"form={q.FormName}", q.CtaType is null ? null : $"type={q.CtaType}",
            q.Language is null ? null : $"lang={q.Language}", q.AssigneeId is null ? null : $"assignee={q.AssigneeId}",
            q.Q is null ? null : "q=<text>", q.From is null ? null : "from", q.To is null ? null : "to"
        }.Where(f => f is not null)) is { Length: > 0 } text ? text : "no filters";

    private static void Audit(CtaDbContext db, ClaimsPrincipal user, string action, string? submissionId, string? detail) =>
        db.SubmissionAudit.Add(new SubmissionAuditEntity
        {
            Id = Guid.NewGuid(),
            SubmissionId = submissionId,
            Actor = user.FindFirstValue(KjClaims.Subject) ?? "unknown",
            Action = action,
            Detail = detail,
            At = DateTimeOffset.UtcNow
        });

    private static SubmissionSummary ToSummary(CtaSubmissionEntity row)
    {
        var values = ParseValues(row.ValuesJson);
        var name = string.Join(' ', new[] { row.FirstName ?? Read(values, "firstName"), row.LastName ?? Read(values, "lastName") }.Where(n => !string.IsNullOrWhiteSpace(n)));
        return new SubmissionSummary(row.Id, row.FormName, row.CtaType, row.Language, row.Status, row.AssigneeId,
            name.Length > 0 ? name : Read(values, "name"), Read(values, "email"), row.SubmittedAt, row.CreatedAt);
    }

    private static Dictionary<string, object?> ParseValues(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? Read(Dictionary<string, object?> values, string key) =>
        values.TryGetValue(key, out var raw) && raw?.ToString()?.Trim() is { Length: > 0 } text ? text : null;
}
