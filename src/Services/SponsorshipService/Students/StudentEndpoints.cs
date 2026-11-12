using System.Globalization;
using KJWebsite.BuildingBlocks;
using KJWebsite.BuildingBlocks.Auditing;
using KJWebsite.BuildingBlocks.Idempotency;
using KJWebsite.BuildingBlocks.Security;
using Microsoft.EntityFrameworkCore;
using SponsorshipService.Data;

namespace SponsorshipService.Students;

public sealed record ProjectChoices(IReadOnlyList<ProgrammeRef> Items);

public sealed record StudentConsentResult(StudentConsentState Consent, StudentConsentRow Row);

/// <summary>Staff endpoints for students, the guardian consent and the donor preview (#213). Nothing here is reachable by a donor token.</summary>
public static class StudentEndpoints
{
    public const string Route = "/api/v1/admin/sponsorship";
    private const string Tag = "Admin: Sponsorship";
    private const int DefaultLimit = 20;
    private const int MaxLimit = 100;

    public static void MapStudents(this IEndpointRouteBuilder app)
    {
        app.MapGet($"{Route}/projects", async (HttpContext context, IProjectCatalogue catalogue, string? lang) =>
        {
            if (!TryLanguage(lang, out var language))
            {
                return Invalid(context, ["lang"]);
            }

            return await catalogue.ListAsync(language, context.RequestAborted) is { } projects
                ? Results.Ok(new ProjectChoices(projects))
                : ProjectsUnavailable(context);
        })
        .WithName("ListSponsorshipProjects")
        .WithSummary("Projects a programme can point to")
        .WithDescription("Project slugs and names from the Content service, in `lang` (en or bn, default en). 503 PROJECTS_UNAVAILABLE when Content cannot be reached.")
        .WithTags(Tag)
        .RequirePermission(Permissions.SponsorshipView);

        app.MapGet($"{Route}/students", async (HttpContext context, SponsorshipDbContext db, IStudentProtector protector, ProgrammeNames names, string? q, string? programme, string? status, string? sponsored, int? limit, int? offset) =>
        {
            var invalid = new List<string>();
            if (limit is < 1 or > MaxLimit)
            {
                invalid.Add("limit");
            }

            if (offset is < 0)
            {
                invalid.Add("offset");
            }

            if (status is not null && !StudentStatuses.All.Contains(status))
            {
                invalid.Add("status");
            }

            if (sponsored is not null && !bool.TryParse(sponsored, out _))
            {
                invalid.Add("sponsored");
            }

            if (q is { Length: > 100 })
            {
                invalid.Add("q");
            }

            if (invalid.Count > 0)
            {
                return Invalid(context, invalid);
            }

            var query = db.Students.AsNoTracking().AsQueryable();
            if (!string.IsNullOrWhiteSpace(programme))
            {
                query = query.Where(s => s.ProgrammeId == programme);
            }

            if (status is not null)
            {
                query = query.Where(s => s.Status == status);
            }

            // No student is linked to a donor until the link endpoints (#214) exist, so only the unsponsored filter can match.
            if (sponsored is not null && bool.Parse(sponsored))
            {
                return Results.Ok(new StudentList([], 0, limit ?? DefaultLimit, offset ?? 0));
            }

            // Ordered here, not in SQL: SQLite (the test provider) cannot order by DateTimeOffset, and a student list is small.
            var rows = (await query.ToListAsync(context.RequestAborted)).OrderByDescending(s => s.CreatedAt).ThenBy(s => s.Id, StringComparer.Ordinal).ToList();

            // The full name is encrypted, so it is matched here after decrypting; the programme is small enough for that.
            if (!string.IsNullOrWhiteSpace(q))
            {
                var needle = q.Trim();
                rows = [.. rows.Where(s => s.Id.Contains(needle, StringComparison.OrdinalIgnoreCase)
                    || s.PreferredName.Contains(needle, StringComparison.OrdinalIgnoreCase)
                    || protector.Unprotect(s.NameProtected).Contains(needle, StringComparison.OrdinalIgnoreCase))];
            }

            var take = limit ?? DefaultLimit;
            var skip = offset ?? 0;
            var page = rows.Skip(skip).Take(take).ToList();
            var ids = page.Select(s => s.Id).ToList();
            var current = await CurrentConsentsAsync(db, ids, context.RequestAborted);
            var programmeNames = await names.NamesAsync("en", context.RequestAborted);
            var items = page.Select(s => StudentData.Summary(
                s, protector, new ProgrammeRef(s.ProgrammeId, programmeNames.GetValueOrDefault(s.ProgrammeId, s.ProgrammeId)),
                current.TryGetValue(s.Id, out var consent) && consent.Granted)).ToList();
            return Results.Ok(new StudentList(items, rows.Count, take, skip));
        })
        .WithName("ListStudents")
        .WithSummary("The staff list of students")
        .WithDescription("Newest first. `q` matches the full name, the preferred name or the id. Filters: `programme` (project slug), `status`, `sponsored` (no student is sponsored until #214). `limit` 1 to 100 (default 20), `offset`.")
        .WithTags(Tag)
        .RequirePermission(Permissions.SponsorshipView);

        app.MapPost($"{Route}/students", async (HttpContext context, StudentRequest payload, SponsorshipDbContext db, IStudentProtector protector, IProjectCatalogue catalogue, ProgrammeNames names, TimeProvider clock) =>
        {
            var now = clock.GetUtcNow();
            var invalid = StudentData.Validate(payload, creating: true, DateOnly.FromDateTime(now.UtcDateTime));
            if (invalid.Count > 0)
            {
                return Invalid(context, invalid);
            }

            if (await CheckProgrammeAsync(context, catalogue, payload.Programme!.Trim()) is { } refused)
            {
                return refused;
            }

            var student = new StudentEntity { Id = StudentData.NewId(), CreatedAt = now, UpdatedAt = now };
            StudentData.Apply(student, payload, protector);
            db.Students.Add(student);
            await db.SaveChangesAsync(context.RequestAborted);

            context.Response.Headers.ETag = StudentData.ETagOf(student);
            var programme = await names.RefAsync(student.ProgrammeId, "en", context.RequestAborted);
            return Results.Created($"{Route}/students/{student.Id}", StudentData.Detail(student, protector, programme, []));
        })
        .WithName("CreateStudent")
        .WithSummary("Add a student")
        .WithDescription("Name, preferredName, dateOfBirth (yyyy-MM-dd), school, class, programme (an existing project slug), district and guardianName are required. Optional: gender (male, female, other), address, guardianPhone (+ and 7 to 15 digits), storyEn, storyBn, status (active, inactive, graduated). 400 VALIDATION_ERROR lists every missing or invalid field; 503 PROJECTS_UNAVAILABLE when Content cannot confirm the programme. Returns the ETag.")
        .WithTags(Tag)
        .RequirePermission(Permissions.SponsorshipStudentManage)
        .RequireIdempotency();

        app.MapGet($"{Route}/students/{{id}}", async (HttpContext context, string id, SponsorshipDbContext db, IStudentProtector protector, ProgrammeNames names) =>
        {
            var student = await db.Students.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, context.RequestAborted);
            if (student is null)
            {
                return NotFound(context);
            }

            // Guardian contact details are read here, so the read is recorded with who made it (D12).
            RecordRead(db, context, student.Id);
            await db.SaveChangesAsync(context.RequestAborted);

            var consents = await db.Consents.AsNoTracking().Where(c => c.StudentId == id).OrderByDescending(c => c.Id).ToListAsync(context.RequestAborted);
            context.Response.Headers.ETag = StudentData.ETagOf(student);
            return Results.Ok(StudentData.Detail(student, protector, await names.RefAsync(student.ProgrammeId, "en", context.RequestAborted), consents));
        })
        .WithName("GetStudent")
        .WithSummary("Every field staff keep for a student")
        .WithDescription("Includes the guardian contact details, the current consent and its history, and the link history (empty until #214). Returns the ETag. Each read is written to the audit log with who made it.")
        .WithTags(Tag)
        .RequirePermission(Permissions.SponsorshipView);

        app.MapPatch($"{Route}/students/{{id}}", async (HttpContext context, string id, StudentRequest payload, SponsorshipDbContext db, IStudentProtector protector, IProjectCatalogue catalogue, ProgrammeNames names, TimeProvider clock) =>
        {
            var student = await db.Students.FirstOrDefaultAsync(s => s.Id == id, context.RequestAborted);
            if (student is null)
            {
                return NotFound(context);
            }

            if (CheckIfMatch(context, student) is { } precondition)
            {
                return precondition;
            }

            var now = clock.GetUtcNow();
            var invalid = StudentData.Validate(payload, creating: false, DateOnly.FromDateTime(now.UtcDateTime));
            if (invalid.Count > 0)
            {
                return Invalid(context, invalid);
            }

            if (payload.Programme is not null && payload.Programme.Trim() != student.ProgrammeId
                && await CheckProgrammeAsync(context, catalogue, payload.Programme.Trim()) is { } refused)
            {
                return refused;
            }

            StudentData.Apply(student, payload, protector);
            student.Version++;
            student.UpdatedAt = now;
            try
            {
                await db.SaveChangesAsync(context.RequestAborted);
            }
            catch (DbUpdateConcurrencyException)
            {
                return StaleStudent(context);
            }

            var consents = await db.Consents.AsNoTracking().Where(c => c.StudentId == id).OrderByDescending(c => c.Id).ToListAsync(context.RequestAborted);
            context.Response.Headers.ETag = StudentData.ETagOf(student);
            return Results.Ok(StudentData.Detail(student, protector, await names.RefAsync(student.ProgrammeId, "en", context.RequestAborted), consents));
        })
        .WithName("UpdateStudent")
        .WithSummary("Change a student")
        .WithDescription("`If-Match` with the student ETag is required: 428 without it, 412 when the student changed since. Fields left out stay as they are; optional text sent as an empty string is cleared. Returns the new ETag.")
        .WithTags(Tag)
        .RequirePermission(Permissions.SponsorshipStudentManage);

        app.MapPost($"{Route}/students/{{id}}/consents", async (HttpContext context, string id, StudentConsentRequest payload, SponsorshipDbContext db, IStudentProtector protector, ProgrammeNames names, TimeProvider clock) =>
        {
            if (payload.Granted is null || payload.Note is { Length: > 500 })
            {
                return Invalid(context, payload.Granted is null ? ["granted"] : ["note"]);
            }

            var student = await db.Students.FirstOrDefaultAsync(s => s.Id == id, context.RequestAborted);
            if (student is null)
            {
                return NotFound(context);
            }

            var latest = await db.Consents.AsNoTracking().Where(c => c.StudentId == id).OrderByDescending(c => c.Id).FirstOrDefaultAsync(context.RequestAborted);
            if (payload.Granted == true && student.FormKey is null)
            {
                return ApiError.Create(context, StatusCodes.Status409Conflict, "CONSENT_FORM_REQUIRED", "Upload the guardian-signed form before recording consent.");
            }

            if ((latest?.Granted ?? false) == payload.Granted)
            {
                return ApiError.Create(context, StatusCodes.Status409Conflict, "CONSENT_UNCHANGED", payload.Granted == true ? "Consent is already recorded." : "There is no consent to withdraw.");
            }

            var now = clock.GetUtcNow();
            var row = new ConsentEntity
            {
                StudentId = id,
                Granted = payload.Granted.Value,
                FormKey = payload.Granted == true ? student.FormKey : null,
                RecordedAt = now,
                ActorId = Actor(context),
                Note = string.IsNullOrWhiteSpace(payload.Note) ? null : payload.Note.Trim()
            };
            db.Consents.Add(row);
            student.Version++;
            student.UpdatedAt = now;
            await db.SaveChangesAsync(context.RequestAborted);

            context.Response.Headers.ETag = StudentData.ETagOf(student);
            var view = new StudentConsentRow(row.Id, row.Granted, row.FormKey is not null, row.RecordedAt, row.ActorId, row.Note);
            return Results.Created($"{Route}/students/{id}", new StudentConsentResult(new StudentConsentState(row.Granted, row.RecordedAt, row.ActorId), view));
        })
        .WithName("RecordStudentConsent")
        .WithSummary("Record the guardian consent or its withdrawal")
        .WithDescription("Body `{ granted, note? }`. One consent covers the photo and the story. Every call appends a dated, attributed row; nothing is rewritten. Granting is refused with 409 CONSENT_FORM_REQUIRED until the signed form is uploaded; repeating the current state is 409 CONSENT_UNCHANGED. A withdrawal removes the photo and story from the donor view at once. Returns 201 and the new ETag.")
        .WithTags(Tag)
        .RequirePermission(Permissions.SponsorshipStudentManage)
        .RequireIdempotency();

        app.MapGet($"{Route}/students/{{id}}/donor-view", async (HttpContext context, string id, SponsorshipDbContext db, ProgrammeNames names, string? lang) =>
        {
            if (!TryLanguage(lang, out var language))
            {
                return Invalid(context, ["lang"]);
            }

            var student = await db.Students.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, context.RequestAborted);
            if (student is null)
            {
                return NotFound(context);
            }

            var latest = await db.Consents.AsNoTracking().Where(c => c.StudentId == id).OrderByDescending(c => c.Id).FirstOrDefaultAsync(context.RequestAborted);
            return Results.Ok(DonorProjection.Of(student, latest?.Granted == true, await names.RefAsync(student.ProgrammeId, language, context.RequestAborted), language));
        })
        .WithName("GetStudentDonorView")
        .WithSummary("Exactly what a linked donor would receive")
        .WithDescription("The donor-visible fields only: preferredName, class, school, programme, district, status, and hasPhoto and story while consent is granted. `lang` (en or bn, default en) picks the story, falling back to the other language.")
        .WithTags(Tag)
        .RequirePermission(Permissions.SponsorshipView);
    }

    internal static string Actor(HttpContext context) => context.User.FindFirst(KjClaims.Subject)?.Value ?? "unknown";

    internal static IResult Invalid(HttpContext context, IEnumerable<string> fields) =>
        ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", $"Invalid fields: {string.Join(", ", fields)}.");

    internal static IResult NotFound(HttpContext context) => ApiError.Create(context, StatusCodes.Status404NotFound, "NOT_FOUND", "Student not found.");

    private static IResult ProjectsUnavailable(HttpContext context) =>
        ApiError.Create(context, StatusCodes.Status503ServiceUnavailable, "PROJECTS_UNAVAILABLE", "The project list could not be reached. Nothing was saved; try again.");

    private static IResult StaleStudent(HttpContext context) =>
        ApiError.Create(context, StatusCodes.Status412PreconditionFailed, "PRECONDITION_FAILED", "The student was changed by someone else. Reload it and apply your edit again.");

    internal static bool TryLanguage(string? lang, out string language)
    {
        language = string.IsNullOrEmpty(lang) ? "en" : lang;
        return language is "en" or "bn";
    }

    private static async Task<IResult?> CheckProgrammeAsync(HttpContext context, IProjectCatalogue catalogue, string programme) =>
        await catalogue.ExistsAsync(programme, context.RequestAborted) switch
        {
            true => null,
            false => Invalid(context, ["programme"]),
            null => ProjectsUnavailable(context)
        };

    internal static IResult? CheckIfMatch(HttpContext context, StudentEntity student)
    {
        var header = context.Request.Headers.IfMatch.ToString().Trim();
        if (header.Length == 0)
        {
            return ApiError.Create(context, StatusCodes.Status428PreconditionRequired, "PRECONDITION_REQUIRED", "Send If-Match with the student ETag.");
        }

        var presented = (header.StartsWith("W/", StringComparison.Ordinal) ? header[2..] : header).Trim('"');
        return presented == student.Version.ToString(CultureInfo.InvariantCulture) ? null : StaleStudent(context);
    }

    /// <summary>The newest consent row of each student, so a list reads one query instead of one per row.</summary>
    private static async Task<Dictionary<string, ConsentEntity>> CurrentConsentsAsync(SponsorshipDbContext db, List<string> studentIds, CancellationToken cancellationToken) =>
        (await db.Consents.AsNoTracking()
            .Where(c => studentIds.Contains(c.StudentId) && !db.Consents.Any(n => n.StudentId == c.StudentId && n.Id > c.Id))
            .ToListAsync(cancellationToken)).ToDictionary(c => c.StudentId);

    /// <summary>Writes a `read` row to the audit log, in the same save as the caller's next SaveChanges.</summary>
    private static void RecordRead(SponsorshipDbContext db, HttpContext context, string studentId) => db.Set<AuditEvent>().Add(new AuditEvent
    {
        At = DateTime.UtcNow,
        Service = "sponsorship",
        ActorId = Actor(context),
        ActorEmail = context.User.FindFirst(KjClaims.Email)?.Value,
        Action = "read",
        Entity = "Student",
        EntityId = studentId,
        Operation = $"{context.Request.Method} {Route}/students/{{id}} (guardian contact details)",
        RequestId = context.TraceIdentifier,
        Ip = context.Connection.RemoteIpAddress?.ToString()
    });
}
