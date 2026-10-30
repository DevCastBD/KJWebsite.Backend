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
            var pageIds = page.Select(a => a.Id).ToList();
            var codes = await db.Members.AsNoTracking().Where(m => pageIds.Contains(m.ApplicationId)).ToDictionaryAsync(m => m.ApplicationId, m => m.MemberCode);
            return Results.Ok(new ApplicationPage([.. page.Select(a => ToRecord(a, protector, codes.GetValueOrDefault(a.Id)))], total));
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
                : Results.Ok(ToRecord(application, protector, await MemberCodeFor(db, id)));
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
            return Results.Ok(ToRecord(application, protector, await MemberCodeFor(db, id)));
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

        app.MapPost($"{Route}/transition", async (HttpContext context, TransitionRequest payload, MembershipDbContext db, IFieldProtector protector, IAuthProvisioningClient identity, TimeProvider clock) =>
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

                if (payload.To == ApplicationStatuses.Approved)
                {
                    results.Add(await Approve(context, application, db, protector, identity, actor, now));
                    continue;
                }

                if (payload.InterviewAt is not null)
                {
                    application.InterviewAt = interviewAt;
                }

                application.Status = payload.To;
                db.StatusEvents.Add(new ApplicationStatusEventEntity { ApplicationId = id, Status = payload.To, At = now, By = actor, Notified = true });
                // Same commit as the change: a transition and its notification either both exist or neither does.
                db.NotificationOutbox.Add(Notification(application, protector, $"application.status.{payload.To}", new { reference = application.Reference, status = payload.To, interviewAt = application.InterviewAt }, now));
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

    /// <summary>
    /// Approval is the one transition with a side effect outside this service: the login. Order matters. Identity creates the
    /// account first, idempotently and in its own transaction; only then does one local commit write the member, code, history
    /// and welcome message together. If anything fails the application stays un-approved, and a retry reuses the account.
    /// </summary>
    private static async Task<TransitionResult> Approve(HttpContext context, ApplicationEntity application, MembershipDbContext db, IFieldProtector protector, IAuthProvisioningClient identity, string actor, DateTimeOffset now)
    {
        var token = context.Request.Headers.Authorization.ToString() is { Length: > 7 } header && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..] : null;
        var account = await identity.ProvisionAsync(
            new ProvisionAccount(
                application.Id, protector.Unprotect(application.EmailProtected), application.FirstName, application.LastName, application.Gender,
                application.DateOfBirthProtected is null ? null : protector.Unprotect(application.DateOfBirthProtected),
                protector.Unprotect(application.BloodGroupProtected), protector.Unprotect(application.CurrentAddressProtected),
                application.CityOfResidence, application.CountryOfResidence, application.ReasonForJoining, application.PresentOrganization, application.VolunteeringExperience),
            token,
            context.RequestAborted);
        if (account.Outcome is not (ProvisioningOutcome.Created or ProvisioningOutcome.Existing))
        {
            return new TransitionResult(application.Id, false, false, account.Outcome switch
            {
                ProvisioningOutcome.EmailTaken => "email_taken",
                ProvisioningOutcome.Forbidden => "forbidden",
                _ => "provisioning_failed"
            });
        }

        var member = new MemberEntity
        {
            Id = $"mem_{Guid.NewGuid():N}",
            UserId = account.UserId!,
            ApplicationId = application.Id,
            FirstName = application.FirstName,
            LastName = application.LastName,
            JoinedAt = now,
            // The application snapshot becomes the member's own profile; from here the portal is where it is kept current.
            PhoneProtected = application.PhoneProtected,
            AddressProtected = application.CurrentAddressProtected,
            DateOfBirthProtected = application.DateOfBirthProtected,
            BloodGroupProtected = application.BloodGroupProtected
        };
        var welcome = Notification(application, protector, "member.welcome", new { reference = application.Reference, memberCode = string.Empty, userId = account.UserId, setupToken = account.SetupToken }, now);
        var previousStatus = application.Status;
        for (var attempt = 0; ; attempt++)
        {
            member.MemberCode = await MemberCodes.NextAsync(db, now.Year, 0, context.RequestAborted);
            welcome.PayloadJson = protector.Protect(JsonSerializer.Serialize(new { reference = application.Reference, memberCode = member.MemberCode, userId = account.UserId, setupToken = account.SetupToken }));
            application.Status = ApplicationStatuses.Approved;
            var evt = new ApplicationStatusEventEntity { ApplicationId = application.Id, Status = ApplicationStatuses.Approved, At = now, By = actor, Notified = true };
            db.Members.Add(member);
            db.MemberStatusEvents.Add(new MemberStatusEventEntity { MemberId = member.Id, ToStatus = MemberStatuses.Active, EffectiveOn = DateOnly.FromDateTime(now.UtcDateTime), At = now, ActorId = actor });
            db.StatusEvents.Add(evt);
            db.NotificationOutbox.Add(welcome);
            try
            {
                await db.SaveChangesAsync(context.RequestAborted);
                return new TransitionResult(application.Id, true, true, null, member.MemberCode);
            }
            catch (DbUpdateException) when (attempt < 4)
            {
                // A concurrent approval took the number: draw again. Nothing was written, so the whole set is retried.
                db.Entry(member).State = EntityState.Detached;
                foreach (var added in db.ChangeTracker.Entries<MemberStatusEventEntity>().Where(e => e.State == EntityState.Added).ToList())
                {
                    added.State = EntityState.Detached;
                }

                db.Entry(evt).State = EntityState.Detached;
                db.Entry(welcome).State = EntityState.Detached;
                application.Status = previousStatus;
            }
            catch (DbUpdateException)
            {
                DetachPending(db);
                application.Status = previousStatus;
                return new TransitionResult(application.Id, false, false, "provisioning_failed");
            }
        }
    }

    private static void DetachPending(MembershipDbContext db)
    {
        foreach (var entry in db.ChangeTracker.Entries().Where(e => e.State is EntityState.Added).ToList())
        {
            entry.State = EntityState.Detached;
        }
    }

    private static NotificationOutboxEntity Notification(ApplicationEntity application, IFieldProtector protector, string template, object payload, DateTimeOffset now) => new()
    {
        ApplicationId = application.Id,
        Template = template,
        Language = application.Language,
        RecipientProtected = application.EmailProtected,
        PayloadJson = protector.Protect(JsonSerializer.Serialize(payload)),
        CreatedAt = now
    };

    private static JsonElement? Property(JsonElement obj, string name) =>
        obj.EnumerateObject().Where(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).Select(p => (JsonElement?)p.Value).FirstOrDefault();

    private static string Actor(HttpContext context) => context.User.FindFirst("sub")?.Value ?? "unknown";

    private static Task<ApplicationEntity?> Load(MembershipDbContext db, string id, bool track)
    {
        var query = db.Applications.Include(a => a.Notes).Include(a => a.StatusEvents).AsQueryable();
        return (track ? query : query.AsNoTracking()).FirstOrDefaultAsync(a => a.Id == id);
    }

    private static Task<string?> MemberCodeFor(MembershipDbContext db, string applicationId) =>
        db.Members.AsNoTracking().Where(m => m.ApplicationId == applicationId).Select(m => m.MemberCode).FirstOrDefaultAsync();

    private static ReviewApplication ToRecord(ApplicationEntity a, IFieldProtector protector, string? memberCode) => new(
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
        StatusTransitions.NextFrom(a.Status),
        memberCode);
}
