using System.Globalization;
using System.Text.Json;
using KJWebsite.BuildingBlocks;
using KJWebsite.BuildingBlocks.Auditing;
using KJWebsite.BuildingBlocks.Security;
using MembershipService.Contracts;
using MembershipService.Data;
using MembershipService.Data.Entities;
using MembershipService.Intake;
using MembershipService.Review;
using Microsoft.EntityFrameworkCore;

namespace MembershipService.Self;

/// <summary>
/// The signed-in member's own data. No route takes a member id: the member is found from the token's subject, so one member
/// cannot name another. A caller who holds the permission but is not a member gets 404 NOT_A_MEMBER.
/// </summary>
public static class SelfEndpoints
{
    private const string Route = "/api/v1/members/me";
    private const string Tag = "Member portal";

    public static void MapSelfEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet($"{Route}/profile", async (HttpContext context, MembershipDbContext db, IFieldProtector protector) =>
            await WithMember(context, db, async member => Results.Ok(await Profile(db, protector, member))))
        .WithName("GetMyProfile").WithSummary("The member's profile, with moderation status of pending edits").WithTags(Tag).Own();

        app.MapPatch($"{Route}/profile", async (HttpContext context, ProfilePatch payload, MembershipDbContext db, IFieldProtector protector, TimeProvider clock) =>
            await WithMember(context, db, async member =>
            {
                var changes = payload.Changes;
                if (changes is null || changes.Count == 0)
                {
                    return ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", "Invalid fields: changes.");
                }

                var now = clock.GetUtcNow();
                var invalid = changes.Where(c => !ProfileFields.IsKnown(c.Key) || c.Value is null || !ProfileFields.IsValid(c.Key, ProfileFields.Normalize(c.Key, c.Value), DateOnly.FromDateTime(now.UtcDateTime))).Select(c => c.Key).ToList();
                if (invalid.Count > 0)
                {
                    return ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", $"Invalid fields: {string.Join(", ", invalid)}.");
                }

                var open = await db.ProfileChanges.Where(c => c.MemberId == member.Id && c.Status == ProfileChangeStatuses.Pending).ToListAsync();
                foreach (var (field, raw) in changes)
                {
                    var value = ProfileFields.Normalize(field, raw!);
                    if (!ProfileFields.IsSensitive(field))
                    {
                        ProfileFields.Apply(member, protector, field, value);
                        continue;
                    }

                    // A newer request for the same field replaces the one still waiting; a request that changes nothing is ignored.
                    db.ProfileChanges.RemoveRange(open.Where(c => c.Field == field));
                    if (value == ProfileFields.Current(member, protector, field))
                    {
                        continue;
                    }

                    db.ProfileChanges.Add(new ProfileChangeEntity
                    {
                        Id = $"chg_{Guid.NewGuid():N}",
                        MemberId = member.Id,
                        Field = field,
                        ValueProtected = protector.Protect(value),
                        SubmittedAt = now
                    });
                }

                await db.SaveChangesAsync();
                return Results.Ok(await Profile(db, protector, member));
            }))
        .WithName("UpdateMyProfile")
        .WithSummary("Edit the profile: phone, address and expertise apply now; names, date of birth and blood group wait for review")
        .WithTags(Tag).Own();

        app.MapGet($"{Route}/consents", async (HttpContext context, MembershipDbContext db) =>
            await WithMember(context, db, member => Task.FromResult(Results.Ok(ConsentsOf(member)))))
        .WithName("GetMyConsents").WithSummary("What the member has agreed to publish (everything off until they say so)").WithTags(Tag).Own();

        app.MapPut($"{Route}/consents", async (HttpContext context, Consents payload, MembershipDbContext db, TimeProvider clock) =>
            await WithMember(context, db, async member =>
            {
                var now = clock.GetUtcNow();
                void Set(string field, bool was, bool granted, Action<bool> write)
                {
                    if (was == granted)
                    {
                        return;
                    }

                    write(granted);
                    db.ConsentEvents.Add(new MemberConsentEventEntity { MemberId = member.Id, Field = field, Granted = granted, At = now });
                }

                Set("name", member.ConsentName, payload.Name, v => member.ConsentName = v);
                Set("photograph", member.ConsentPhotograph, payload.Photograph, v => member.ConsentPhotograph = v);
                Set("expertise", member.ConsentExpertise, payload.Expertise, v => member.ConsentExpertise = v);
                Set("alumni", member.ConsentAlumni, payload.Alumni ?? member.ConsentAlumni, v => member.ConsentAlumni = v);
                await db.SaveChangesAsync();
                return Results.Ok(ConsentsOf(member));
            }))
        .WithName("SetMyConsents").WithSummary("Give or withdraw publication consent per field").WithTags(Tag).Own();

        app.MapGet($"{Route}/notifications", async (HttpContext context, MembershipDbContext db) =>
            await WithMember(context, db, member => Task.FromResult(Results.Ok(PrefsOf(member)))))
        .WithName("GetMyNotifications").WithSummary("Which messages the member wants").WithTags(Tag).Own();

        app.MapPut($"{Route}/notifications", async (HttpContext context, NotificationPrefs payload, MembershipDbContext db) =>
            await WithMember(context, db, async member =>
            {
                member.NotifyNews = payload.News;
                member.NotifyEvents = payload.Events;
                member.NotifyMembership = payload.Membership;
                await db.SaveChangesAsync();
                return Results.Ok(PrefsOf(member));
            }))
        .WithName("SetMyNotifications").WithSummary("Choose which messages to receive").WithTags(Tag).Own();

        app.MapPost($"{Route}/deletion-request", async (HttpContext context, MembershipDbContext db, TimeProvider clock) =>
            await WithMember(context, db, async member =>
            {
                var existing = await db.DeletionRequests.FirstOrDefaultAsync(d => d.MemberId == member.Id);
                if (existing is null)
                {
                    existing = new DeletionRequestEntity
                    {
                        Id = $"del_{Guid.NewGuid():N}",
                        MemberId = member.Id,
                        Reference = $"DEL-{ReferenceNumber.Create(clock.GetUtcNow().Year)[^8..]}",
                        RequestedAt = clock.GetUtcNow()
                    };
                    db.DeletionRequests.Add(existing);
                    await db.SaveChangesAsync();
                }

                return Results.Accepted(null, new DeletionRequestView(existing.Reference, existing.RequestedAt));
            }))
        .WithName("RequestMyDeletion").WithSummary("Ask for the member's data to be erased")
        .WithDescription("Records the request and returns 202 with a reference; repeating it returns the same one. Carrying it out, and what must be kept by law, is decided with the retention work (#58).")
        .WithTags(Tag).Own();

        app.MapPut($"{Route}/photo", async (HttpContext context, MembershipDbContext db, IAttachmentStore store) =>
            await WithMember(context, db, async member =>
            {
                var rule = AttachmentRules.ByKind["photo"];
                var contentType = (context.Request.ContentType ?? string.Empty).Split(';')[0].Trim().ToLowerInvariant();
                if (!rule.ContentTypes.Contains(contentType))
                {
                    return ApiError.Create(context, StatusCodes.Status415UnsupportedMediaType, "UNSUPPORTED_MEDIA_TYPE", "File type is not allowed for a photo.");
                }

                using var buffer = new MemoryStream();
                var chunk = new byte[16 * 1024];
                int read;
                while ((read = await context.Request.Body.ReadAsync(chunk, context.RequestAborted)) > 0)
                {
                    if (buffer.Length + read > rule.MaxBytes)
                    {
                        return ApiError.Create(context, StatusCodes.Status413PayloadTooLarge, "PAYLOAD_TOO_LARGE", "Photo exceeds 2 MB.");
                    }

                    buffer.Write(chunk, 0, read);
                }

                if (buffer.Length == 0 || !AttachmentRules.MatchesSignature(contentType, buffer.GetBuffer().AsSpan(0, (int)Math.Min(buffer.Length, 16))))
                {
                    return ApiError.Create(context, StatusCodes.Status415UnsupportedMediaType, "UNSUPPORTED_MEDIA_TYPE", "File content does not match its declared type.");
                }

                var key = Guid.NewGuid().ToString("N");
                buffer.Position = 0;
                await store.SaveAsync(key, buffer, context.RequestAborted);
                var previous = member.PhotoKey;
                member.PhotoKey = key;
                member.PhotoContentType = contentType;
                member.PhotoBytes = buffer.Length;
                member.PhotoFileName = AttachmentRules.SafeFileName(context.Request.Headers["X-Filename"].ToString());
                member.PhotoScanStatus = AttachmentScanStatuses.Pending;
                await db.SaveChangesAsync();
                if (previous is not null)
                {
                    await store.DeleteAsync(previous, context.RequestAborted);
                }

                return Results.Ok(new AttachmentAccepted("photo", member.PhotoFileName, contentType, buffer.Length, member.PhotoScanStatus));
            }))
        .WithName("UploadMyPhoto").WithSummary("Set the member's photo (jpeg, png or webp, up to 2 MB)").WithTags(Tag).Own();

        app.MapGet($"{Route}/photo", async (HttpContext context, MembershipDbContext db, IAttachmentStore store) =>
            await WithMember(context, db, async member =>
            {
                var stream = member.PhotoKey is null ? null : await store.OpenReadAsync(member.PhotoKey, context.RequestAborted);
                return stream is null
                    ? ApiError.Create(context, StatusCodes.Status404NotFound, "NOT_FOUND", "No photo.")
                    : Results.Stream(stream, member.PhotoContentType, enableRangeProcessing: false);
            }))
        .WithName("GetMyPhoto").WithSummary("The member's own photo").WithTags(Tag).Own();

        app.MapDelete($"{Route}/photo", async (HttpContext context, MembershipDbContext db, IAttachmentStore store) =>
            await WithMember(context, db, async member =>
            {
                if (member.PhotoKey is { } key)
                {
                    member.PhotoKey = member.PhotoContentType = member.PhotoFileName = member.PhotoScanStatus = null;
                    member.PhotoBytes = null;
                    await db.SaveChangesAsync();
                    await store.DeleteAsync(key, context.RequestAborted);
                }

                return Results.NoContent();
            }))
        .WithName("DeleteMyPhoto").WithSummary("Remove the member's photo").WithTags(Tag).Own();

        app.MapMemberExport();
        app.MapMemberCard();
    }

    internal static TBuilder Own<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.RequirePermission(Permissions.MemberProfileEdit);

    internal static string? Subject(HttpContext context) => context.User.FindFirst(KjClaims.Subject)?.Value;

    internal static async Task<IResult> WithMember(HttpContext context, MembershipDbContext db, Func<MemberEntity, Task<IResult>> action)
    {
        var subject = Subject(context);
        var member = subject is null ? null : await db.Members.FirstOrDefaultAsync(m => m.UserId == subject);
        return member is null
            ? ApiError.Create(context, StatusCodes.Status404NotFound, "NOT_A_MEMBER", "The signed-in account has no member record.")
            : await action(member);
    }

    internal static Consents ConsentsOf(MemberEntity m) => new(m.ConsentName, m.ConsentPhotograph, m.ConsentExpertise, m.ConsentAlumni);

    internal static NotificationPrefs PrefsOf(MemberEntity m) => new(m.NotifyNews, m.NotifyEvents, m.NotifyMembership);

    internal static async Task<PortalProfile> Profile(MembershipDbContext db, IFieldProtector protector, MemberEntity member)
    {
        var email = await db.Applications.AsNoTracking().Where(a => a.Id == member.ApplicationId).Select(a => a.EmailProtected).FirstOrDefaultAsync();
        var changes = (await db.ProfileChanges.AsNoTracking().Where(c => c.MemberId == member.Id).ToListAsync()).OrderBy(c => c.SubmittedAt).ToList();
        // The latest request per field, shown while it waits or after it was refused; an approved one is simply the current value.
        var pending = changes.GroupBy(c => c.Field).Select(g => g.Last()).Where(c => c.Status != ProfileChangeStatuses.Approved)
            .ToDictionary(c => c.Field, c => new PendingChange(protector.Unprotect(c.ValueProtected), c.Status, c.Reason, c.SubmittedAt));
        var deletion = await db.DeletionRequests.AsNoTracking().Where(d => d.MemberId == member.Id).FirstOrDefaultAsync();

        return new PortalProfile(
            member.MemberCode,
            member.JoinedAt.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            email is null ? string.Empty : protector.Unprotect(email),
            member.FirstName,
            member.LastName,
            member.NameBn ?? string.Empty,
            ProfileFields.Current(member, protector, "dateOfBirth"),
            ProfileFields.Current(member, protector, "bloodGroup"),
            ProfileFields.Current(member, protector, "phone"),
            ProfileFields.Current(member, protector, "address"),
            member.Expertise ?? string.Empty,
            pending,
            deletion is null ? null : new DeletionRequestView(deletion.Reference, deletion.RequestedAt),
            member.PhotoKey is not null,
            member.Status);
    }
}
