using KJWebsite.BuildingBlocks;
using KJWebsite.BuildingBlocks.Security;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using SponsorshipService.Data;

namespace SponsorshipService.Students;

public sealed record PhotoResult(bool HasPhoto, string ContentType, int Bytes);

public sealed record FormResult(string FileName, string ContentType, int Bytes);

/// <summary>
/// The private files of a student: one photo and the guardian-signed consent form. Both live in a private store and are served only to a
/// staff token holding sponsorship.view; no donor or public route reads them.
/// </summary>
public static class StudentFileEndpoints
{
    private const string Tag = "Admin: Sponsorship";
    private static readonly string[] PhotoTypes = [ImageSanitizer.Jpeg, ImageSanitizer.Png];
    private static readonly string[] FormTypes = [ImageSanitizer.Jpeg, ImageSanitizer.Png, "application/pdf"];

    public static void MapStudentFiles(this IEndpointRouteBuilder app)
    {
        var route = $"{StudentEndpoints.Route}/students/{{id}}";

        app.MapPut($"{route}/photo", async (HttpContext context, string id, SponsorshipDbContext db, IStudentFileStore store, TimeProvider clock) =>
        {
            var student = await db.Students.FirstOrDefaultAsync(s => s.Id == id, context.RequestAborted);
            if (student is null)
            {
                return StudentEndpoints.NotFound(context);
            }

            var latest = await db.Consents.AsNoTracking().Where(c => c.StudentId == id).OrderByDescending(c => c.Id).FirstOrDefaultAsync(context.RequestAborted);
            if (latest?.Granted != true)
            {
                return ApiError.Create(context, StatusCodes.Status409Conflict, "CONSENT_REQUIRED", "A photo can be stored only while the guardian consent is recorded.");
            }

            var upload = await ReadUploadAsync(context, "photo", StudentData.MaxPhotoBytes, PhotoTypes);
            if (upload.Failure is not null)
            {
                return upload.Failure;
            }

            var previous = student.PhotoKey;
            var key = $"pho-{Guid.NewGuid():N}";
            await store.SaveAsync(key, upload.Bytes!, context.RequestAborted);
            student.PhotoKey = key;
            student.PhotoContentType = upload.ContentType;
            student.Version++;
            student.UpdatedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(context.RequestAborted);
            if (previous is not null)
            {
                await store.DeleteAsync(previous, context.RequestAborted);
            }

            context.Response.Headers.ETag = StudentData.ETagOf(student);
            return Results.Ok(new PhotoResult(true, upload.ContentType!, upload.Bytes!.Length));
        })
        .WithName("PutStudentPhoto")
        .WithSummary("Store the student photo")
        .WithDescription("Multipart form field `photo`: JPEG or PNG, up to 2 MB. Location and other embedded metadata are removed before it is stored. 409 CONSENT_REQUIRED without recorded consent, 413 over the limit, 415 for another type or a file that is not a valid image. Replaces any earlier photo.")
        .WithTags(Tag)
        .RequirePermission(Permissions.SponsorshipStudentManage)
        .DisableAntiforgery();

        app.MapGet($"{route}/photo", async (HttpContext context, string id, SponsorshipDbContext db, IStudentFileStore store) =>
        {
            var student = await db.Students.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, context.RequestAborted);
            if (student is null)
            {
                return StudentEndpoints.NotFound(context);
            }

            var stream = student.PhotoKey is null ? null : await store.OpenReadAsync(student.PhotoKey, context.RequestAborted);
            return stream is null
                ? ApiError.Create(context, StatusCodes.Status404NotFound, "NOT_FOUND", "This student has no photo.")
                : Results.Stream(stream, student.PhotoContentType ?? ImageSanitizer.Jpeg);
        })
        .WithName("GetStudentPhoto")
        .WithSummary("The student photo, for staff")
        .WithDescription("Requires a staff token with sponsorship.view; 401 without a token and 403 for any other token, a donor included. Served with `Cache-Control: private, no-store`.")
        .WithTags(Tag)
        .RequirePermission(Permissions.SponsorshipView);

        app.MapDelete($"{route}/photo", async (HttpContext context, string id, SponsorshipDbContext db, IStudentFileStore store, TimeProvider clock) =>
        {
            var student = await db.Students.FirstOrDefaultAsync(s => s.Id == id, context.RequestAborted);
            if (student is null)
            {
                return StudentEndpoints.NotFound(context);
            }

            if (student.PhotoKey is not { } key)
            {
                return ApiError.Create(context, StatusCodes.Status404NotFound, "NOT_FOUND", "This student has no photo.");
            }

            student.PhotoKey = null;
            student.PhotoContentType = null;
            student.Version++;
            student.UpdatedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(context.RequestAborted);
            await store.DeleteAsync(key, context.RequestAborted);
            context.Response.Headers.ETag = StudentData.ETagOf(student);
            return Results.NoContent();
        })
        .WithName("DeleteStudentPhoto")
        .WithSummary("Delete the student photo")
        .WithDescription("204 on success, 404 when there is no photo. The file is removed from the private store.")
        .WithTags(Tag)
        .RequirePermission(Permissions.SponsorshipStudentManage);

        app.MapPut($"{route}/consent-form", async (HttpContext context, string id, SponsorshipDbContext db, IStudentFileStore store, TimeProvider clock) =>
        {
            var student = await db.Students.FirstOrDefaultAsync(s => s.Id == id, context.RequestAborted);
            if (student is null)
            {
                return StudentEndpoints.NotFound(context);
            }

            var upload = await ReadUploadAsync(context, "form", StudentData.MaxFormBytes, FormTypes);
            if (upload.Failure is not null)
            {
                return upload.Failure;
            }

            // An earlier form stays in the store: a consent row may point at it, and it is the evidence for that consent.
            var key = $"frm-{Guid.NewGuid():N}";
            await store.SaveAsync(key, upload.Bytes!, context.RequestAborted);
            student.FormKey = key;
            student.FormContentType = upload.ContentType;
            student.FormFileName = SafeFileName(upload.FileName);
            student.Version++;
            student.UpdatedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(context.RequestAborted);
            context.Response.Headers.ETag = StudentData.ETagOf(student);
            return Results.Ok(new FormResult(student.FormFileName, upload.ContentType!, upload.Bytes!.Length));
        })
        .WithName("PutStudentConsentForm")
        .WithSummary("Store the guardian-signed consent form")
        .WithDescription("Multipart form field `form`: PDF, JPEG or PNG, up to 5 MB, kept in the private store (image metadata is removed). 413 over the limit, 415 for another type. Consent can be recorded only after this.")
        .WithTags(Tag)
        .RequirePermission(Permissions.SponsorshipStudentManage)
        .DisableAntiforgery();

        app.MapGet($"{route}/consent-form", async (HttpContext context, string id, SponsorshipDbContext db, IStudentFileStore store) =>
        {
            var student = await db.Students.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, context.RequestAborted);
            if (student is null)
            {
                return StudentEndpoints.NotFound(context);
            }

            var stream = student.FormKey is null ? null : await store.OpenReadAsync(student.FormKey, context.RequestAborted);
            return stream is null
                ? ApiError.Create(context, StatusCodes.Status404NotFound, "NOT_FOUND", "No signed form has been uploaded for this student.")
                : Results.File(stream, student.FormContentType ?? "application/octet-stream", student.FormFileName ?? "consent-form");
        })
        .WithName("GetStudentConsentForm")
        .WithSummary("The signed consent form, for staff")
        .WithDescription("Requires a staff token with sponsorship.view; 401 without a token and 403 for any other token, a donor included. Never reachable by a donor or the public.")
        .WithTags(Tag)
        .RequirePermission(Permissions.SponsorshipView);
    }

    private sealed record Upload(byte[]? Bytes, string? ContentType, string? FileName, IResult? Failure);

    /// <summary>
    /// Reads one multipart file, checks its size, declared type and real signature, and removes image metadata. The body is capped while
    /// it is read, so an oversized upload is refused without being buffered whole.
    /// </summary>
    private static async Task<Upload> ReadUploadAsync(HttpContext context, string field, long maxBytes, string[] allowed)
    {
        static Upload Fail(IResult result) => new(null, null, null, result);

        if (!context.Request.HasFormContentType)
        {
            return Fail(ApiError.Create(context, StatusCodes.Status415UnsupportedMediaType, "UNSUPPORTED_MEDIA_TYPE", $"Send the file as multipart/form-data in the field `{field}`."));
        }

        var limit = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (limit is { IsReadOnly: false })
        {
            limit.MaxRequestBodySize = maxBytes + 64 * 1024;
        }

        IFormCollection form;
        try
        {
            form = await context.Request.ReadFormAsync(context.RequestAborted);
        }
        catch (BadHttpRequestException)
        {
            return Fail(TooLarge(context, maxBytes));
        }
        catch (InvalidDataException)
        {
            return Fail(TooLarge(context, maxBytes));
        }

        var file = form.Files.GetFile(field);
        if (file is null || file.Length == 0)
        {
            return Fail(StudentEndpoints.Invalid(context, [field]));
        }

        if (file.Length > maxBytes)
        {
            return Fail(TooLarge(context, maxBytes));
        }

        var contentType = file.ContentType?.Split(';')[0].Trim().ToLowerInvariant() ?? string.Empty;
        if (!allowed.Contains(contentType))
        {
            return Fail(ApiError.Create(context, StatusCodes.Status415UnsupportedMediaType, "UNSUPPORTED_MEDIA_TYPE", $"Allowed types: {string.Join(", ", allowed)}."));
        }

        await using var stream = file.OpenReadStream();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, context.RequestAborted);
        var bytes = buffer.ToArray();
        if (!ImageSanitizer.MatchesSignature(contentType, bytes))
        {
            return Fail(ApiError.Create(context, StatusCodes.Status415UnsupportedMediaType, "UNSUPPORTED_MEDIA_TYPE", "The file content does not match its type."));
        }

        if (contentType != "application/pdf")
        {
            var cleaned = ImageSanitizer.Strip(contentType, bytes);
            if (cleaned is null)
            {
                return Fail(ApiError.Create(context, StatusCodes.Status415UnsupportedMediaType, "UNSUPPORTED_MEDIA_TYPE", "The file is not a valid image."));
            }

            bytes = cleaned;
        }

        return new Upload(bytes, contentType, file.FileName, null);
    }

    private static IResult TooLarge(HttpContext context, long maxBytes) =>
        ApiError.Create(context, StatusCodes.Status413PayloadTooLarge, "PAYLOAD_TOO_LARGE", $"The file is larger than {maxBytes / (1024 * 1024)} MB.");

    /// <summary>The name is shown to staff only: strip paths and control characters, cap the length.</summary>
    private static string SafeFileName(string? raw)
    {
        var name = Path.GetFileName((raw ?? string.Empty).Replace('\\', '/'));
        name = new string([.. name.Where(c => !char.IsControl(c))]).Trim();
        if (name.Length > 120)
        {
            name = name[^120..];
        }

        return name.Length == 0 ? "consent-form" : name;
    }
}
