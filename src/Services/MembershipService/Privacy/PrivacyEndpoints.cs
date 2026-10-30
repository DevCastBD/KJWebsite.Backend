using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KJWebsite.BuildingBlocks;
using KJWebsite.BuildingBlocks.Auditing;
using KJWebsite.BuildingBlocks.Security;
using MembershipService.Committees;
using MembershipService.Contracts;
using MembershipService.Data;
using MembershipService.Data.Entities;
using MembershipService.Intake;
using MembershipService.Review;
using MembershipService.Self;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MembershipService.Privacy;

public sealed record DataRequestInput(string? Reference, string? Email);

public sealed record RequestView(string Id, string Reference, string SubjectKind, string SubjectId, string? MemberCode, DateTimeOffset RequestedAt, DateTimeOffset? CompletedAt);

/// <summary>Retention, erasure and the applicant's own data rights.</summary>
public static class PrivacyEndpoints
{
    private const string Admin = "/api/v1/admin/membership";

    public static void MapPrivacy(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/membership/privacy", (IOptions<PrivacyOptions> options) =>
        {
            var o = options.Value;
            return Results.Ok(new
            {
                noticeVersion = string.IsNullOrEmpty(o.NoticeVersion) ? null : o.NoticeVersion,
                dataLocation = string.IsNullOrEmpty(o.DataLocation) ? null : o.DataLocation,
                // null means the window is not set: that kind of application is not purged automatically.
                retention = new
                {
                    rejectedApplicationMonths = o.WindowFor(ApplicationStatuses.Rejected),
                    withdrawnApplicationMonths = o.WindowFor(ApplicationStatuses.Withdrawn),
                    postponedApplicationMonths = o.WindowFor(ApplicationStatuses.Postponed),
                    automaticPurge = o.AnyWindow && o.WorkerEnabled
                },
                rights = new { exportForMembers = "/api/v1/members/me/export", exportForApplicants = "/api/v1/membership/applications/export", erasureForMembers = "/api/v1/members/me/deletion-request", erasureForApplicants = "/api/v1/membership/applications/erasure-request" }
            });
        })
        .WithName("GetPrivacyBehaviour")
        .WithSummary("What the platform actually does with personal data")
        .WithDescription("The configured notice version, data location and retention windows, so the published privacy statement can state exactly what runs. A null window means that kind of data is not purged automatically.")
        .WithTags("Privacy").AllowAnonymous();

        app.MapPost("/api/v1/membership/applications/export", async (HttpContext context, DataRequestInput payload, MembershipDbContext db, ContactHasher hasher, IFieldProtector protector, TimeProvider clock) =>
        {
            var application = await FindVerified(db, hasher, payload);
            if (application is null)
            {
                return NotFound(context);
            }

            await db.Entry(application).Collection(a => a.StatusEvents).LoadAsync();
            await db.Entry(application).Collection(a => a.Notes).LoadAsync();
            await db.Entry(application).Collection(a => a.Attachments).LoadAsync();
            var consents = (await db.ConsentRecords.AsNoTracking().Where(c => c.SubjectId == application.Id).ToListAsync()).OrderBy(c => c.Id).ToList();
            var messages = (await db.NotificationOutbox.AsNoTracking().Where(o => o.ApplicationId == application.Id).ToListAsync()).OrderBy(o => o.Id).ToList();
            context.Response.Headers.CacheControl = "no-store";
            return Results.Json(new
            {
                exportedAt = clock.GetUtcNow(),
                application = new
                {
                    application.Reference,
                    application.Language,
                    application.Status,
                    application.SubmittedAt,
                    application.FirstName,
                    application.LastName,
                    application.Gender,
                    application.AcademicInstitute,
                    application.Occupation,
                    application.ReasonForJoining,
                    application.PresentOrganization,
                    application.VolunteeringExperience,
                    application.HeardAboutUs,
                    application.CityOfResidence,
                    application.CountryOfResidence,
                    email = protector.Unprotect(application.EmailProtected),
                    phone = protector.Unprotect(application.PhoneProtected),
                    bloodGroup = protector.Unprotect(application.BloodGroupProtected),
                    currentAddress = protector.Unprotect(application.CurrentAddressProtected),
                    dateOfBirth = application.DateOfBirthProtected is null ? null : protector.Unprotect(application.DateOfBirthProtected),
                    attachments = application.Attachments.Select(a => new { a.Kind, a.FileName, a.ContentType, a.Bytes, a.UploadedAt }),
                    history = application.StatusEvents.OrderBy(e => e.At).ThenBy(e => e.Id).Select(e => new { e.Status, e.At }),
                    reviewerNotes = application.Notes.OrderBy(n => n.At).Select(n => new { n.At, n.Text }),
                    review = new { score = application.Score, interviewAt = application.InterviewAt }
                },
                consentRecords = consents.Select(c => new { c.Purpose, c.Granted, c.At, c.Source, c.NoticeVersion }),
                messagesSent = messages.Select(m => new { m.Template, m.Language, m.CreatedAt, m.SentAt })
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        })
        .WithName("ExportApplicationData")
        .WithSummary("An applicant's own data (reference and email required)")
        .WithDescription("Same two factors as the status lookup; any mismatch is the same 404. Rate limited at the gateway.")
        .WithTags("Privacy").AllowAnonymous();

        app.MapPost("/api/v1/membership/applications/erasure-request", async (HttpContext context, DataRequestInput payload, MembershipDbContext db, ContactHasher hasher, TimeProvider clock) =>
        {
            var application = await FindVerified(db, hasher, payload);
            if (application is null)
            {
                return NotFound(context);
            }

            // An applicant who became a member asks as that member: their record is the member's, and the request covers both.
            var memberId = await db.Members.AsNoTracking().Where(m => m.ApplicationId == application.Id).Select(m => m.Id).FirstOrDefaultAsync();
            var existing = await db.DeletionRequests.FirstOrDefaultAsync(d => (memberId != null && d.MemberId == memberId) || d.ApplicationId == application.Id);
            if (existing is null)
            {
                existing = new DeletionRequestEntity
                {
                    Id = $"del_{Guid.NewGuid():N}",
                    MemberId = memberId,
                    ApplicationId = memberId is null ? application.Id : null,
                    Reference = $"DEL-{ReferenceNumber.Create(clock.GetUtcNow().Year)[^8..]}",
                    RequestedAt = clock.GetUtcNow()
                };
                db.DeletionRequests.Add(existing);
                await db.SaveChangesAsync();
            }

            return Results.Accepted(null, new DeletionRequestView(existing.Reference, existing.RequestedAt));
        })
        .WithName("RequestApplicationErasure")
        .WithSummary("Ask for an application to be erased (reference and email required)")
        .WithDescription("Records the request and returns 202 with a reference; staff carry it out, so nobody can delete someone else's application by knowing an email. Repeating returns the same reference.")
        .WithTags("Privacy").AllowAnonymous();

        app.MapPost($"{Admin}/retention/run", async (MembershipDbContext db, IAttachmentStore store, IOptions<PrivacyOptions> options, TimeProvider clock, HttpContext context, bool? dryRun) =>
        {
            // A dry run unless asked otherwise: deleting is never the accident.
            var result = await RetentionPurger.RunAsync(db, store, options.Value, clock, context.User.FindFirst(KjClaims.Subject)?.Value ?? "unknown", dryRun ?? true, context.RequestAborted);
            return Results.Ok(result);
        })
        .WithName("RunRetention")
        .WithSummary("Find (dryRun=true, the default) or purge (dryRun=false) applications past their retention window")
        .WithDescription("Windows come from `Privacy:*ApplicationMonths`; a status with no window is never touched. Every run, dry or not, is recorded with the policy used; a purge also records the opaque ids it removed, never anything personal.")
        .WithTags("Privacy admin").RequirePermission(Permissions.MembershipPrivacy);

        app.MapGet($"{Admin}/retention/runs", async (MembershipDbContext db) =>
        {
            var runs = (await db.RetentionRuns.AsNoTracking().ToListAsync()).OrderByDescending(r => r.StartedAt).Take(100);
            return Results.Ok(new { items = runs.Select(r => new { r.Id, r.StartedAt, r.FinishedAt, r.DryRun, r.StartedBy, policy = JsonDocument.Parse(r.PolicyJson).RootElement, r.Purged }) });
        })
        .WithName("ListRetentionRuns").WithSummary("Past retention runs").WithTags("Privacy admin").RequirePermission(Permissions.MembershipPrivacy);

        app.MapGet($"{Admin}/retention/runs/{{id}}/items", async (MembershipDbContext db, string id) =>
            Results.Ok(new { items = await db.RetentionItems.AsNoTracking().Where(i => i.RunId == id).Select(i => new { i.ApplicationId, i.Status, i.StatusSince }).ToListAsync() }))
        .WithName("ListRetentionRunItems").WithSummary("What a run purged: opaque ids, status and the day the status began").WithTags("Privacy admin").RequirePermission(Permissions.MembershipPrivacy);

        app.MapGet($"{Admin}/erasure-requests", async (MembershipDbContext db, string? status) =>
        {
            var completed = status == "completed";
            var requests = (await db.DeletionRequests.AsNoTracking().Where(d => completed ? d.CompletedAt != null : d.CompletedAt == null).ToListAsync()).OrderBy(d => d.RequestedAt).ToList();
            var memberIds = requests.Where(r => r.MemberId != null).Select(r => r.MemberId!).ToList();
            var codes = await db.Members.AsNoTracking().Where(m => memberIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id, m => m.MemberCode);
            return Results.Ok(new
            {
                items = requests.Select(r => new RequestView(r.Id, r.Reference, r.MemberId is null ? "application" : "member", r.MemberId ?? r.ApplicationId ?? string.Empty, r.MemberId is null ? null : codes.GetValueOrDefault(r.MemberId), r.RequestedAt, r.CompletedAt))
            });
        })
        .WithName("ListErasureRequests").WithSummary("Erasure requests (status=pending, the default, or completed)").WithTags("Privacy admin").RequirePermission(Permissions.MembershipPrivacy);

        app.MapPost($"{Admin}/erasure-requests/{{id}}/execute", async (HttpContext context, string id, MembershipDbContext db, IAttachmentStore store, IAuthProvisioningClient identity, IOptions<PrivacyOptions> privacy, TimeProvider clock) =>
            await Execute(context, id, db, store, identity, privacy.Value, clock))
        .WithName("ExecuteErasure")
        .WithSummary("Carry out an erasure request")
        .WithDescription("Members: the Identity account is erased first, then in one transaction the application (with files, notes and queued messages), edit requests and profile are removed or blanked, committee terms end, consents are withdrawn and a non-identifying erasure record is written; the actor's email/IP on their audit rows is nulled. Applicants: the application and its files are deleted. Idempotent on Identity, so a failed run can be repeated; a completed request is 409.")
        .WithTags("Privacy admin").RequirePermission(Permissions.MembershipPrivacy);
    }

    private static IResult NotFound(HttpContext context) => ApiError.Create(context, StatusCodes.Status404NotFound, "NOT_FOUND", "No matching application.");

    /// <summary>Reference plus the email it was filed with; a wrong guess of either is indistinguishable from the other.</summary>
    private static async Task<ApplicationEntity?> FindVerified(MembershipDbContext db, ContactHasher hasher, DataRequestInput payload)
    {
        if (string.IsNullOrWhiteSpace(payload.Reference) || payload.Reference.Length > 64 || !ApplicationValidator.IsEmail(payload.Email))
        {
            return null;
        }

        var reference = ReferenceNumber.Normalize(payload.Reference);
        var application = await db.Applications.FirstOrDefaultAsync(a => a.Reference == reference);
        var supplied = Encoding.UTF8.GetBytes(hasher.HashEmail(payload.Email!));
        var stored = Encoding.UTF8.GetBytes(application?.EmailHash ?? new string('0', supplied.Length));
        return application is not null && CryptographicOperations.FixedTimeEquals(supplied, stored) ? application : null;
    }

    private static async Task<IResult> Execute(HttpContext context, string id, MembershipDbContext db, IAttachmentStore store, IAuthProvisioningClient identity, PrivacyOptions privacy, TimeProvider clock)
    {
        var request = await db.DeletionRequests.FirstOrDefaultAsync(d => d.Id == id);
        if (request is null)
        {
            return ApiError.Create(context, StatusCodes.Status404NotFound, "NOT_FOUND", "Request not found.");
        }

        if (request.CompletedAt is not null)
        {
            return ApiError.Create(context, StatusCodes.Status409Conflict, "ALREADY_COMPLETED", "This request has already been carried out.");
        }

        var now = clock.GetUtcNow();
        var actor = context.User.FindFirst(KjClaims.Subject)?.Value ?? "unknown";
        var token = context.Request.Headers.Authorization.ToString() is { Length: > 7 } h && h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? h[7..] : null;
        ErasureSummary summary;
        string subjectKind;
        string subjectId;
        string? photoKey = null;
        string? userId = null;

        if (request.MemberId is { } memberId)
        {
            var member = await db.Members.FirstOrDefaultAsync(m => m.Id == memberId);
            if (member is null)
            {
                return ApiError.Create(context, StatusCodes.Status404NotFound, "NOT_FOUND", "Member not found.");
            }

            // Identity goes first and is safe to repeat. If anything later fails, a retry finishes the job.
            if (!await identity.EraseAccountAsync(member.UserId, token, context.RequestAborted))
            {
                return ApiError.Create(context, StatusCodes.Status502BadGateway, "IDENTITY_UNAVAILABLE", "The login could not be erased, so nothing was changed. Try again.");
            }

            subjectKind = "member";
            subjectId = member.Id;
            userId = member.UserId;
            photoKey = member.PhotoKey;
            await using var transaction = await db.Database.BeginTransactionAsync(context.RequestAborted);
            var applicationSummary = await Erasure.DeleteApplicationAsync(db, store, member.ApplicationId, context.RequestAborted);
            db.ProfileChanges.RemoveRange(await db.ProfileChanges.Where(c => c.MemberId == memberId).ToListAsync(context.RequestAborted));

            foreach (var term in await db.CommitteeMembers.Where(t => t.MemberId == memberId && (t.EndsOn == null || t.EndsOn > CommitteeEndpoints.Today(clock))).ToListAsync(context.RequestAborted))
            {
                term.EndsOn = term.StartsOn > CommitteeEndpoints.Today(clock) ? term.StartsOn : CommitteeEndpoints.Today(clock);
            }

            void Withdraw(string purpose, bool was)
            {
                if (was)
                {
                    ConsentLedger.Record(db, "member", memberId, purpose, false, now, "staff", actor, privacy.NoticeVersion);
                }
            }

            Withdraw(ConsentPurposes.PublishName, member.ConsentName);
            Withdraw(ConsentPurposes.PublishPhotograph, member.ConsentPhotograph);
            Withdraw(ConsentPurposes.PublishExpertise, member.ConsentExpertise);
            Withdraw(ConsentPurposes.PublishAlumni, member.ConsentAlumni);

            var previous = member.Status;
            member.FirstName = "Erased";
            member.LastName = "Member";
            member.NameBn = member.Expertise = member.PhoneProtected = member.AddressProtected = member.DateOfBirthProtected = member.BloodGroupProtected = null;
            member.PhotoKey = member.PhotoContentType = member.PhotoFileName = member.PhotoScanStatus = null;
            member.PhotoBytes = null;
            member.ConsentName = member.ConsentPhotograph = member.ConsentExpertise = member.ConsentAlumni = false;
            member.Status = MemberStatuses.Erased;
            foreach (var e in await db.MemberStatusEvents.Where(e => e.MemberId == memberId).ToListAsync(context.RequestAborted))
            {
                e.Note = null;
                e.ReasonProtected = null;
            }

            db.MemberStatusEvents.Add(new MemberStatusEventEntity { MemberId = memberId, FromStatus = previous, ToStatus = MemberStatuses.Erased, EffectiveOn = CommitteeEndpoints.Today(clock), At = now, ActorId = actor });
            summary = applicationSummary with { MemberAnonymised = true };
            request.CompletedAt = now;
            request.CompletedBy = actor;
            db.ErasureRecords.Add(new ErasureRecordEntity { Id = $"ers_{Guid.NewGuid():N}", SubjectKind = subjectKind, SubjectId = subjectId, RequestId = request.Id, ExecutedAt = now, ExecutedBy = actor, SummaryJson = Erasure.Describe(summary) });
            await db.SaveChangesAsync(context.RequestAborted);
            await transaction.CommitAsync(context.RequestAborted);
        }
        else
        {
            subjectKind = "application";
            subjectId = request.ApplicationId ?? string.Empty;
            summary = await Erasure.DeleteApplicationAsync(db, store, subjectId, context.RequestAborted);
            request.CompletedAt = now;
            request.CompletedBy = actor;
            db.ErasureRecords.Add(new ErasureRecordEntity { Id = $"ers_{Guid.NewGuid():N}", SubjectKind = subjectKind, SubjectId = subjectId, RequestId = request.Id, ExecutedAt = now, ExecutedBy = actor, SummaryJson = Erasure.Describe(summary) });
            await db.SaveChangesAsync(context.RequestAborted);
        }

        if (photoKey is not null)
        {
            await store.DeleteAsync(photoKey, context.RequestAborted);
        }

        if (userId is not null)
        {
            await Erasure.RedactAuditActorAsync(db, userId, context.RequestAborted);
        }

        return Results.Ok(new { id = request.Id, status = "completed", subjectKind, subjectId, summary });
    }
}
