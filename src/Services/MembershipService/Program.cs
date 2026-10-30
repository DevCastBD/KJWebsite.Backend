using System.Security.Cryptography;
using System.Text;
using KJWebsite.BuildingBlocks;
using KJWebsite.BuildingBlocks.Auditing;
using KJWebsite.BuildingBlocks.Idempotency;
using KJWebsite.BuildingBlocks.Security;
using MembershipService.Contracts;
using MembershipService.Data;
using MembershipService.Data.Entities;
using MembershipService.Intake;
using MembershipService.Review;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer((document, context, ct) =>
    {
        document.Info.Title = "Membership Service API";
        document.Info.Description = "Public membership application intake: the full legacy field set, a warn-only duplicate check and CV/photo attachments.";
        document.Info.Version = "v1";
        return Task.CompletedTask;
    });
    options.AddOperationTransformer((operation, context, ct) =>
    {
        if (context.Description.ActionDescriptor.EndpointMetadata.OfType<IdempotentEndpointMetadata>().Any())
        {
            operation.Parameters ??= [];
            operation.Parameters.Add(new Microsoft.OpenApi.OpenApiParameter
            {
                Name = IdempotencyExtensions.HeaderName,
                In = Microsoft.OpenApi.ParameterLocation.Header,
                Required = false,
                Description = "Optional. A repeat with the same key and body returns the original response (header `Idempotency-Replayed: true`) and creates nothing new; the same key with a different body is 422; a repeat while the first is running is 409. Keys are remembered for 24 hours.",
                Schema = new Microsoft.OpenApi.OpenApiSchema { Type = Microsoft.OpenApi.JsonSchemaType.String }
            });
        }

        return Task.CompletedTask;
    });
});

builder.Services.AddDbContext<MembershipDbContext>((services, options) =>
{
    var configuration = services.GetRequiredService<IConfiguration>();
    var connectionString = configuration.GetConnectionString("MembershipDb")
        ?? (configuration.GetValue<bool>("OpenApi:Export") ? "Host=localhost;Database=membership" : throw new InvalidOperationException("ConnectionStrings:MembershipDb is required."));
    options.UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "membership"));
    options.AddInterceptors(services.GetRequiredService<AuditInterceptor>());
});
builder.Services.AddKjAuditing("membership", audit => audit
    .Audit<CampaignEntity>()
    // Personal data stays out of the log; only the review state is recorded.
    .Audit<ApplicationEntity>(
        nameof(ApplicationEntity.FirstName), nameof(ApplicationEntity.LastName), nameof(ApplicationEntity.Gender),
        nameof(ApplicationEntity.AcademicInstitute), nameof(ApplicationEntity.Occupation), nameof(ApplicationEntity.ReasonForJoining),
        nameof(ApplicationEntity.PresentOrganization), nameof(ApplicationEntity.VolunteeringExperience), nameof(ApplicationEntity.HeardAboutUs),
        nameof(ApplicationEntity.CityOfResidence), nameof(ApplicationEntity.CountryOfResidence), nameof(ApplicationEntity.EmailProtected),
        nameof(ApplicationEntity.PhoneProtected), nameof(ApplicationEntity.BloodGroupProtected), nameof(ApplicationEntity.CurrentAddressProtected),
        nameof(ApplicationEntity.DateOfBirthProtected), nameof(ApplicationEntity.EmailHash), nameof(ApplicationEntity.PhoneHash))
    .Audit<ApplicationStatusEventEntity>()
    .Audit<MemberEntity>()
    .Audit<ApplicationNoteEntity>(nameof(ApplicationNoteEntity.Text)));
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.Configure<MembershipOptions>(builder.Configuration.GetSection(MembershipOptions.SectionName));
// Persist the key ring (DataProtection:KeysPath) in any deployment: rows encrypted by one instance must open on the next.
var dataProtection = builder.Services.AddDataProtection(o => o.ApplicationDiscriminator = "KJWebsite.Membership");
if (builder.Configuration["DataProtection:KeysPath"] is { Length: > 0 } keysPath)
{
    dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keysPath));
}

builder.Services.AddSingleton<IFieldProtector, DataProtectionFieldProtector>();
builder.Services.AddSingleton<ContactHasher>();
builder.Services.AddSingleton<IAttachmentStore, LocalDiskAttachmentStore>();
builder.Services.AddHttpClient(AuthProvisioningClient.ClientName, (services, client) =>
{
    client.BaseAddress = new Uri(services.GetRequiredService<IConfiguration>()["Services:Auth:Url"] ?? "http://localhost:7003/");
    client.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddScoped<IAuthProvisioningClient, AuthProvisioningClient>();
builder.Services.AddKjJwtAuthentication();
builder.Services.AddKjIdempotency(builder.Configuration);

var app = builder.Build();

if (!(app.Environment.IsDevelopment() && app.Configuration.GetValue<bool>("OpenApi:Export")))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<MembershipDbContext>();
    // Tests swap in the Sqlite provider, which has no migrations to replay; Postgres keeps its migration history.
    if (db.Database.IsNpgsql())
    {
        db.Database.Migrate();
    }
    else
    {
        db.Database.EnsureCreated();
    }

    // Fail at startup, not on the first application, when the hash key is missing.
    _ = scope.ServiceProvider.GetRequiredService<ContactHasher>();
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();
    app.MapScalarApiReference(options =>
    {
        options.WithTitle("Membership Service API")
               .WithDefaultHttpClient(ScalarTarget.CSharp, ScalarClient.HttpClient);
    }).AllowAnonymous();
}

app.UseAuthentication();
app.UseAuthorization();
app.UseKjIdempotency();

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "MembershipService" }))
   .WithName("MembershipHealth")
   .WithSummary("Membership service health check")
   .WithTags("System")
   .AllowAnonymous();

app.MapPost("/api/v1/membership/applications", async (HttpContext context, ApplicationRequest payload, MembershipDbContext db, IFieldProtector protector, ContactHasher hasher, TimeProvider clock) =>
{
    var now = clock.GetUtcNow();
    var invalid = ApplicationValidator.Validate(payload, DateOnly.FromDateTime(now.UtcDateTime));
    if (invalid.Count > 0)
    {
        return ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", $"Invalid fields: {string.Join(", ", invalid)}.");
    }

    var intake = await CampaignEndpoints.DecideIntakeAsync(db, now);
    if (intake.Intake == "closed")
    {
        return ApiError.Create(context, StatusCodes.Status409Conflict, "INTAKE_CLOSED", "Applications are closed: no recruitment season is open right now.");
    }

    static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    var entity = new ApplicationEntity
    {
        Id = $"app_{Guid.NewGuid():N}",
        CampaignId = intake.Campaign?.Id,
        Language = payload.Language!.ToLowerInvariant(),
        SubmittedAt = (payload.SubmittedAt ?? now).ToUniversalTime(),
        CreatedAt = now,
        FirstName = payload.FirstName!.Trim(),
        LastName = payload.LastName!.Trim(),
        Gender = Clean(payload.Gender),
        AcademicInstitute = payload.AcademicInstitute!.Trim(),
        Occupation = payload.Occupation!.Trim(),
        ReasonForJoining = Clean(payload.ReasonForJoining),
        PresentOrganization = Clean(payload.PresentOrganization),
        VolunteeringExperience = Clean(payload.VolunteeringExperience),
        HeardAboutUs = Clean(payload.HeardAboutUs),
        CityOfResidence = Clean(payload.CityOfResidence),
        CountryOfResidence = Clean(payload.CountryOfResidence),
        EmailProtected = protector.Protect(payload.Email!.Trim()),
        PhoneProtected = protector.Protect(payload.Phone!.Trim()),
        BloodGroupProtected = protector.Protect(payload.BloodGroup!.Trim().ToUpperInvariant()),
        CurrentAddressProtected = protector.Protect(payload.CurrentAddress!.Trim()),
        DateOfBirthProtected = Clean(payload.DateOfBirth) is { } dob ? protector.Protect(dob) : null,
        EmailHash = hasher.HashEmail(payload.Email!),
        PhoneHash = hasher.HashPhone(payload.Phone!),
        StatusEvents = [new ApplicationStatusEventEntity { Status = ApplicationStatuses.Awaiting, At = now }]
    };

    // References are random; the unique index settles the (vanishingly rare) collision and the loser draws again.
    for (var attempt = 1; ; attempt++)
    {
        entity.Reference = ReferenceNumber.Create(now.Year);
        db.Applications.Add(entity);
        try
        {
            await db.SaveChangesAsync();
            break;
        }
        catch (DbUpdateException) when (attempt < 5)
        {
            db.Entry(entity).State = EntityState.Detached;
            foreach (var ev in entity.StatusEvents)
            {
                db.Entry(ev).State = EntityState.Detached;
            }
        }
    }

    return Results.Created($"/api/v1/membership/applications/{entity.Id}", new ApplicationCreated(entity.Id, entity.Reference, entity.Status, entity.CreatedAt));
})
.WithName("CreateMembershipApplication")
.WithSummary("Submit a membership application")
.WithDescription("Accepts the full legacy application field set. Returns 409 INTAKE_CLOSED when recruitment seasons exist but none is open. Returns the application id and its public reference; attach the CV and photo afterwards with PUT /api/v1/membership/applications/{id}/attachments/{kind}.")
.WithTags("Membership")
.RequireIdempotency()
.AllowAnonymous();

app.MapPost("/api/v1/membership/applications/check", async (HttpContext context, DuplicateCheckRequest payload, MembershipDbContext db, ContactHasher hasher) =>
{
    if (!ApplicationValidator.IsEmail(payload.Email) || payload.Phone is null || ContactHasher.NormalizePhone(payload.Phone).Length < 7)
    {
        return ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", "Invalid fields: email, phone.");
    }

    var emailHash = hasher.HashEmail(payload.Email!);
    var phoneHash = hasher.HashPhone(payload.Phone);
    var duplicate = await db.Applications.AnyAsync(a => a.EmailHash == emailHash || a.PhoneHash == phoneHash);
    return Results.Ok(new DuplicateCheckResult(duplicate));
})
.WithName("CheckMembershipDuplicate")
.WithSummary("Warn when an email or phone already applied")
.WithDescription("Warn-only: re-applying (for example after a rejection) is legitimate, so a duplicate never blocks a submission. Rate limited at the gateway.")
.WithTags("Membership")
.AllowAnonymous();

app.MapPost("/api/v1/membership/applications/status", async (HttpContext context, StatusLookupRequest payload, MembershipDbContext db, ContactHasher hasher) =>
{
    // One answer for "no such reference", "wrong email" and malformed input: nothing here confirms a reference exists.
    var notFound = () => ApiError.Create(context, StatusCodes.Status404NotFound, "NOT_FOUND", "No matching application.");
    if (string.IsNullOrWhiteSpace(payload.Reference) || payload.Reference.Length > 64 || !ApplicationValidator.IsEmail(payload.Email))
    {
        return notFound();
    }

    var reference = ReferenceNumber.Normalize(payload.Reference);
    var application = await db.Applications.AsNoTracking().Include(a => a.StatusEvents).FirstOrDefaultAsync(a => a.Reference == reference);
    // Hash even when nothing was found, so the two failures cost the same.
    var supplied = Encoding.UTF8.GetBytes(hasher.HashEmail(payload.Email!));
    var stored = Encoding.UTF8.GetBytes(application?.EmailHash ?? new string('0', supplied.Length));
    if (application is null || !CryptographicOperations.FixedTimeEquals(supplied, stored))
    {
        return notFound();
    }

    // Status and time only: reviewer notes and scores are never part of this response.
    var timeline = application.StatusEvents.OrderBy(e => e.At).ThenBy(e => e.Id).Select(e => new StatusEvent(e.Status, e.At)).ToList();
    return Results.Ok(new StatusTimeline(timeline));
})
.WithName("LookupMembershipStatus")
.WithSummary("Look up an application's status")
.WithDescription("Needs the reference AND the email used to apply. Any mismatch returns the same 404. Returns status transitions only. Rate limited at the gateway.")
.WithTags("Membership")
.AllowAnonymous();

app.MapCampaignEndpoints();
app.MapReviewEndpoints();
app.MapAuditLog<MembershipDbContext>("/api/v1/admin/membership/audit", "Membership");

app.MapPut("/api/v1/membership/applications/{id}/attachments/{kind}", async (HttpContext context, string id, string kind, MembershipDbContext db, IAttachmentStore store) =>
{
    if (!AttachmentRules.ByKind.TryGetValue(kind, out var rule))
    {
        return ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", "Attachment kind must be cv or photo.");
    }

    var contentType = (context.Request.ContentType ?? string.Empty).Split(';')[0].Trim().ToLowerInvariant();
    if (!rule.ContentTypes.Contains(contentType))
    {
        return ApiError.Create(context, StatusCodes.Status415UnsupportedMediaType, "UNSUPPORTED_MEDIA_TYPE", "File type is not allowed for this attachment.");
    }

    if (context.Request.ContentLength > rule.MaxBytes)
    {
        return ApiError.Create(context, StatusCodes.Status413PayloadTooLarge, "PAYLOAD_TOO_LARGE", $"File exceeds {rule.MaxBytes / (1024 * 1024)} MB.");
    }

    var application = await db.Applications.Include(a => a.Attachments).FirstOrDefaultAsync(a => a.Id == id);
    if (application is null)
    {
        return ApiError.Create(context, StatusCodes.Status404NotFound, "NOT_FOUND", "Application not found.");
    }

    // Read one byte past the cap: a missing or lying Content-Length cannot smuggle in a bigger file.
    context.Features.Get<IHttpMaxRequestBodySizeFeature>()?.MaxRequestBodySize = rule.MaxBytes + 1;
    using var buffer = new MemoryStream();
    var chunk = new byte[16 * 1024];
    int read;
    while ((read = await context.Request.Body.ReadAsync(chunk, context.RequestAborted)) > 0)
    {
        if (buffer.Length + read > rule.MaxBytes)
        {
            return ApiError.Create(context, StatusCodes.Status413PayloadTooLarge, "PAYLOAD_TOO_LARGE", $"File exceeds {rule.MaxBytes / (1024 * 1024)} MB.");
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

    var normalizedKind = kind.ToLowerInvariant();
    var attachment = application.Attachments.FirstOrDefault(a => a.Kind == normalizedKind);
    var previousKey = attachment?.StorageKey;
    if (attachment is null)
    {
        attachment = new AttachmentEntity { ApplicationId = application.Id, Kind = normalizedKind };
        db.Attachments.Add(attachment);
    }

    attachment.StorageKey = key;
    attachment.FileName = AttachmentRules.SafeFileName(context.Request.Headers["X-Filename"].ToString());
    attachment.ContentType = contentType;
    attachment.Bytes = buffer.Length;
    attachment.ScanStatus = AttachmentScanStatuses.Pending;
    attachment.UploadedAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync(context.RequestAborted);
    if (previousKey is not null)
    {
        await store.DeleteAsync(previousKey, context.RequestAborted);
    }

    return Results.Ok(new AttachmentAccepted(attachment.Kind, attachment.FileName, attachment.ContentType, attachment.Bytes, attachment.ScanStatus));
})
.WithName("UploadMembershipAttachment")
.WithSummary("Attach a CV or photo to an application")
.WithDescription("Raw file body with the file's Content-Type and an optional URL-encoded X-Filename header. cv: pdf/doc/docx up to 5 MB; photo: jpeg/png/webp up to 2 MB. The content is checked against the declared type. A second upload replaces the first. Files are stored with scanStatus `pending` until the media pipeline scans them.")
.WithTags("Membership")
.AllowAnonymous();

app.Run();

namespace MembershipService
{
    public sealed class MembershipServiceTestEntryPoint;
}
