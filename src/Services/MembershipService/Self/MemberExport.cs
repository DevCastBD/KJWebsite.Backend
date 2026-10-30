using System.Text.Json;
using KJWebsite.BuildingBlocks.Auditing;
using MembershipService.Data;
using MembershipService.Data.Entities;
using MembershipService.Intake;
using MembershipService.Lifecycle;
using MembershipService.Review;
using Microsoft.EntityFrameworkCore;

namespace MembershipService.Self;

/// <summary>Download-my-data: everything this service holds about the member, plus what Identity holds, in one document.</summary>
public static class MemberExport
{
    public static void MapMemberExport(this IEndpointRouteBuilder app) =>
        app.MapGet("/api/v1/members/me/export", async (HttpContext context, MembershipDbContext db, IFieldProtector protector, IAuthProvisioningClient identity, TimeProvider clock) =>
            await SelfEndpoints.WithMember(context, db, async member =>
            {
                var profile = await SelfEndpoints.Profile(db, protector, member);
                var application = await db.Applications.AsNoTracking().Include(a => a.Notes).Include(a => a.StatusEvents).Include(a => a.Attachments).FirstOrDefaultAsync(a => a.Id == member.ApplicationId);
                var changes = (await db.ProfileChanges.AsNoTracking().Where(c => c.MemberId == member.Id).ToListAsync()).OrderBy(c => c.SubmittedAt).ToList();
                var consents = (await db.ConsentEvents.AsNoTracking().Where(c => c.MemberId == member.Id).ToListAsync()).OrderBy(c => c.Id).ToList();
                var notices = (await db.NotificationOutbox.AsNoTracking().Where(o => o.ApplicationId == member.ApplicationId).ToListAsync()).OrderBy(o => o.Id).ToList();

                // Audit rows are about this member when their entity is one of the member's own records.
                var ids = new List<string> { member.Id, member.ApplicationId };
                ids.AddRange(changes.Select(c => c.Id));
                var audit = (await db.Set<AuditEvent>().AsNoTracking().Where(a => ids.Contains(a.EntityId)).ToListAsync()).OrderBy(a => a.Id).ToList();

                var token = context.Request.Headers.Authorization.ToString() is { Length: > 7 } h && h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? h[7..] : null;
                var account = await identity.GetAccountExportAsync(token, context.RequestAborted);

                var snapshot = application is null ? null : new
                {
                    application.Reference,
                    application.Language,
                    application.Status,
                    application.SubmittedAt,
                    application.CampaignId,
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
                    attachments = application.Attachments.Select(a => new { a.Kind, a.FileName, a.ContentType, a.Bytes, a.ScanStatus, a.UploadedAt }),
                    history = application.StatusEvents.OrderBy(e => e.At).ThenBy(e => e.Id).Select(e => new { e.Status, e.At, e.By, e.Notified }),
                    reviewerNotes = application.Notes.OrderBy(n => n.At).Select(n => new { n.At, n.By, n.Text }),
                    review = new { reviewerId = application.ReviewerId, score = application.Score, interviewAt = application.InterviewAt }
                };

                return Results.Json(new
                {
                    exportedAt = clock.GetUtcNow(),
                    profile,
                    consents = SelfEndpoints.ConsentsOf(member),
                    notifications = SelfEndpoints.PrefsOf(member),
                    sessions = account is { } a && a.TryGetProperty("sessions", out var s) ? s : (JsonElement?)null,
                    member = new
                    {
                        member.Id,
                        member.MemberCode,
                        member.Status,
                        member.JoinedAt,
                        photo = member.PhotoKey is null ? null : new { fileName = member.PhotoFileName, contentType = member.PhotoContentType, bytes = member.PhotoBytes, scanStatus = member.PhotoScanStatus }
                    },
                    // The member's own right of access covers the whole record, termination reason included.
                    lifecycle = await LifecycleEndpoints.View(db, protector, member, showReasons: true, clock),
                    consentHistory = consents.Select(c => new { c.Field, c.Granted, c.At }),
                    profileChanges = changes.Select(c => new { c.Field, value = protector.Unprotect(c.ValueProtected), c.Status, c.Reason, c.SubmittedAt, c.DecidedAt }),
                    application = snapshot,
                    messagesSent = notices.Select(o => new { o.Template, o.Language, o.CreatedAt, o.SentAt }),
                    audit = audit.Select(a => new { a.At, a.Service, a.Action, a.Entity, a.EntityId, a.Before, a.After }),
                    identity = account,
                    identityAvailable = account is not null
                }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
            }))
        .WithName("ExportMyData")
        .WithSummary("Download everything held about the member")
        .WithDescription("Profile, consents and their history, edit requests, the original application with its history and reviewer notes, messages sent, audit entries about the member's records, and the Identity account with sessions. Secrets (password hashes, token values) are never included. `identityAvailable` is false when Identity could not be reached; retry for the full set.")
        .WithTags("Member portal").Own();
}
