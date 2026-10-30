using System.Text.Json;
using KJWebsite.BuildingBlocks;
using KJWebsite.BuildingBlocks.Security;
using MembershipService.Data;
using MembershipService.Data.Entities;
using MembershipService.Intake;
using Microsoft.EntityFrameworkCore;

namespace MembershipService.Review;

public static class ReviewEndpoints
{
    private const string Route = "/api/v1/admin/membership/applications";
    private const int MaxBulk = 200;

    public static void MapReviewEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(Route, async (MembershipDbContext db, IFieldProtector protector, string? status, string? campaignId, int? limit, int? offset) =>
        {
            var query = db.Applications.AsNoTracking().AsQueryable();
            if (!string.IsNullOrWhiteSpace(status))
            {
                query = query.Where(a => a.Status == status);
            }

            if (!string.IsNullOrWhiteSpace(campaignId))
            {
                query = query.Where(a => a.CampaignId == campaignId);
            }

            var total = await query.CountAsync();
            // Oldest first. Sqlite (the test-only provider) cannot order by DateTimeOffset, so it falls back to the id.
            var ordered = db.Database.IsNpgsql() ? query.OrderBy(a => a.CreatedAt).ThenBy(a => a.Id) : query.OrderBy(a => a.Id);
            var page = await ordered
                .Include(a => a.Notes).Include(a => a.StatusEvents)
                .Skip(Math.Max(0, offset ?? 0)).Take(Math.Clamp(limit ?? 200, 1, 500))
                .ToListAsync();
            return Results.Ok(new ApplicationPage([.. page.Select(a => ToRecord(a, protector))], total));
        })
        .WithName("ListMembershipApplications")
        .WithSummary("List applications for review")
        .WithDescription("Internal view with notes, score, reviewer and full status history. Filters: status, campaignId; paging: limit (default 200, max 500), offset.")
        .WithTags("Membership admin")
        .RequirePermission(Permissions.MembershipManage);

        app.MapGet($"{Route}/{{id}}", async (HttpContext context, string id, MembershipDbContext db, IFieldProtector protector) =>
        {
            var application = await Load(db, id, track: false);
            return application is null
                ? ApiError.Create(context, StatusCodes.Status404NotFound, "NOT_FOUND", "Application not found.")
                : Results.Ok(ToRecord(application, protector));
        })
        .WithName("GetMembershipApplication").WithSummary("One application with notes and history").WithTags("Membership admin")
        .RequirePermission(Permissions.MembershipManage);

        app.MapPatch($"{Route}/{{id}}", async (HttpContext context, string id, JsonElement patch, MembershipDbContext db, IFieldProtector protector) =>
        {
            if (patch.ValueKind != JsonValueKind.Object)
            {
                return ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", "Send a JSON object.");
            }

            var application = await Load(db, id, track: true);
            if (application is null)
            {
                return ApiError.Create(context, StatusCodes.Status404NotFound, "NOT_FOUND", "Application not found.");
            }

            // Absent means "leave alone", null means "clear".
            var invalid = new List<string>();
            if (Property(patch, "score") is { } score)
            {
                if (score.ValueKind == JsonValueKind.Null)
                {
                    application.Score = null;
                }
                else if (score.ValueKind == JsonValueKind.Number && score.TryGetInt32(out var value) && value is >= 0 and <= 100)
                {
                    application.Score = value;
                }
                else
                {
                    invalid.Add("score");
                }
            }

            if (Property(patch, "reviewerId") is { } reviewer)
            {
                if (reviewer.ValueKind == JsonValueKind.Null)
                {
                    application.ReviewerId = null;
                }
                else if (reviewer.ValueKind == JsonValueKind.String && reviewer.GetString() is { Length: > 0 and <= 64 } reviewerId)
                {
                    application.ReviewerId = reviewerId;
                }
                else
                {
                    invalid.Add("reviewerId");
                }
            }

            if (Property(patch, "interviewAt") is { } interview)
            {
                if (interview.ValueKind == JsonValueKind.Null)
                {
                    application.InterviewAt = null;
                }
                else if (interview.ValueKind == JsonValueKind.String && interview.TryGetDateTimeOffset(out var at))
                {
                    application.InterviewAt = at.ToUniversalTime();
                }
                else
                {
                    invalid.Add("interviewAt");
                }
            }

            if (invalid.Count > 0)
            {
                return ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", $"Invalid fields: {string.Join(", ", invalid)}.");
            }

            await db.SaveChangesAsync();
            return Results.Ok(ToRecord(application, protector));
        })
        .WithName("UpdateMembershipApplication")
        .WithSummary("Assign a reviewer, set the score or the interview time")
        .WithDescription("Body fields are optional: `reviewerId`, `score` (0-100), `interviewAt`. Send null to clear one.")
        .WithTags("Membership admin")
        .RequirePermission(Permissions.MembershipManage);

        app.MapPost($"{Route}/{{id}}/notes", async (HttpContext context, string id, NoteRequest payload, MembershipDbContext db, TimeProvider clock) =>
        {
            if (string.IsNullOrWhiteSpace(payload.Text) || payload.Text.Trim().Length > 4000)
            {
                return ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", "Invalid fields: text.");
            }

            if (!await db.Applications.AnyAsync(a => a.Id == id))
            {
                return ApiError.Create(context, StatusCodes.Status404NotFound, "NOT_FOUND", "Application not found.");
            }

            var note = new ApplicationNoteEntity
            {
                Id = $"note_{Guid.NewGuid():N}",
                ApplicationId = id,
                At = clock.GetUtcNow(),
                By = Actor(context),
                Text = payload.Text.Trim()
            };
            db.Notes.Add(note);
            await db.SaveChangesAsync();
            return Results.Created($"{Route}/{id}", new ReviewNoteRecord(note.Id, note.At, note.By, note.Text));
        })
        .WithName("AddMembershipNote").WithSummary("Add an internal reviewer note").WithTags("Membership admin")
        .RequirePermission(Permissions.MembershipManage);

        app.MapPost($"{Route}/transition", async (HttpContext context, TransitionRequest payload, MembershipDbContext db, IFieldProtector protector, TimeProvider clock) =>
        {
            var ids = payload.Ids?.Where(i => !string.IsNullOrWhiteSpace(i)).Distinct().ToList() ?? [];
            if (ids.Count == 0 || ids.Count > MaxBulk || payload.To is null || !ApplicationStatuses.All.Contains(payload.To))
            {
                return ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", $"Invalid fields: ids (1-{MaxBulk}), to.");
            }

            var now = clock.GetUtcNow();
            var actor = Actor(context);
            var applications = await db.Applications.Where(a => ids.Contains(a.Id)).ToDictionaryAsync(a => a.Id);
            var results = new List<TransitionResult>(ids.Count);
            foreach (var id in ids)
            {
                if (!applications.TryGetValue(id, out var application))
                {
                    results.Add(new TransitionResult(id, false, false, "not_found"));
                    continue;
                }

                var interviewAt = payload.InterviewAt?.ToUniversalTime() ?? application.InterviewAt;
                if (StatusTransitions.Check(application.Status, payload.To, interviewAt) is { } error)
                {
                    results.Add(new TransitionResult(id, false, false, error));
                    continue;
                }

                if (payload.InterviewAt is not null)
                {
                    application.InterviewAt = interviewAt;
                }

                application.Status = payload.To;
                db.StatusEvents.Add(new ApplicationStatusEventEntity { ApplicationId = id, Status = payload.To, At = now, By = actor, Notified = true });
                // Same commit as the change: a transition and its notification either both exist or neither does.
                db.NotificationOutbox.Add(new NotificationOutboxEntity
                {
                    ApplicationId = id,
                    Template = $"application.status.{payload.To}",
                    Language = application.Language,
                    RecipientProtected = application.EmailProtected,
                    PayloadJson = JsonSerializer.Serialize(new { reference = application.Reference, status = payload.To, interviewAt = application.InterviewAt }),
                    CreatedAt = now
                });
                results.Add(new TransitionResult(id, true, true, null));
            }

            await db.SaveChangesAsync();
            return Results.Ok(new TransitionResponse(results));
        })
        .WithName("TransitionMembershipApplications")
        .WithSummary("Move applications to a new status, in bulk")
        .WithDescription($"Up to {MaxBulk} ids. Always 200 with one result per id; a failed item carries `error` (`not_found`, `final`, `invalid_transition`, `interview_date_required`) and changes nothing. Each success is audited, appended to the history and queues the applicant's notification in their language. `interview_scheduled` needs `interviewAt`.")
        .WithTags("Membership admin")
        .RequirePermission(Permissions.MembershipManage);
    }

    private static JsonElement? Property(JsonElement obj, string name) =>
        obj.EnumerateObject().Where(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).Select(p => (JsonElement?)p.Value).FirstOrDefault();

    private static string Actor(HttpContext context) => context.User.FindFirst("sub")?.Value ?? "unknown";

    private static Task<ApplicationEntity?> Load(MembershipDbContext db, string id, bool track)
    {
        var query = db.Applications.Include(a => a.Notes).Include(a => a.StatusEvents).AsQueryable();
        return (track ? query : query.AsNoTracking()).FirstOrDefaultAsync(a => a.Id == id);
    }

    private static ReviewApplication ToRecord(ApplicationEntity a, IFieldProtector protector) => new(
        a.Id,
        a.Reference,
        $"{a.FirstName} {a.LastName}",
        protector.Unprotect(a.EmailProtected),
        a.Language,
        a.SubmittedAt,
        a.Status,
        a.CampaignId,
        a.ReviewerId,
        a.Score,
        a.InterviewAt,
        [.. a.Notes.OrderBy(n => n.At).ThenBy(n => n.Id).Select(n => new ReviewNoteRecord(n.Id, n.At, n.By, n.Text))],
        [.. a.StatusEvents.OrderBy(e => e.At).ThenBy(e => e.Id).Select(e => new StatusChangeRecord(e.Status, e.At, e.By, e.Notified))],
        StatusTransitions.NextFrom(a.Status));
}
