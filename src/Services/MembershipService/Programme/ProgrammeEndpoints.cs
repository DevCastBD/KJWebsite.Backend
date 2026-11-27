using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using KJWebsite.BuildingBlocks;
using KJWebsite.BuildingBlocks.Security;
using MembershipService.Data;
using MembershipService.Data.Entities;
using MembershipService.Intake;
using MembershipService.Self;
using Microsoft.EntityFrameworkCore;

namespace MembershipService.Programme;

public sealed record BeneficiaryRequest(string? Slug, string? Kind, string? NameEn, string? NameBn, string? Address, string? District, bool? IsArchived);

public sealed record EnrolmentRequest(int? Students);

public sealed record MouRequest(DateOnly? SignedOn, DateOnly? ValidUntil, string? Signatory, string? SignatoryRole);

public sealed record SessionInput(string? ClientId, string? ProjectSlug, string? BeneficiarySlug, DateOnly? HeldOn, string? Topic, int? Attendance, int? DurationMinutes);

public sealed record SessionBatch(List<SessionInput>? Sessions);

public sealed record SessionResult(string? ClientId, string Result, string? Id, string? Error);

public sealed record OutcomeRequest(string? ProjectSlug, string? MetricKey, DateOnly? PeriodStart, DateOnly? PeriodEnd, double? Value, string? Unit, string? Note);

/// <summary>
/// Programme delivery: beneficiary institutions, MoUs, sessions delivered and outcome metrics. Institution data is sensitive (MoUs name
/// signatories; counts plus addresses identify where children are), so institutions, MoUs and their documents need
/// `programme.manage`; logging a session needs only `programme.log` and sees neither address nor MoU. Only aggregate figures are meant to
/// leave this module, and each carries a fingerprint of the logged sessions and enrolments behind it.
/// </summary>
public static partial class ProgrammeEndpoints
{
    private const string Root = "/api/v1/admin/membership/programme";
    private const string Tag = "Programme delivery";
    private const long MaxDocumentBytes = 10 * 1024 * 1024;
    private const int MaxBatch = 200;
    private const int MaxLateDays = 366;
    private static readonly string[] Kinds = ["school", "madrasa", "mission", "other"];
    private static readonly string[] DocumentTypes = ["application/pdf", "image/jpeg", "image/png"];

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex SlugPattern();

    public static void MapProgramme(this IEndpointRouteBuilder app)
    {
        MapInstitutions(app);
        MapMous(app);
        MapSessions(app);
        MapOutcomesAndFigures(app);
    }

    // ---- institutions

    private static void MapInstitutions(IEndpointRouteBuilder app)
    {
        app.MapGet($"{Root}/beneficiaries", async (MembershipDbContext db, IFieldProtector protector, bool? includeArchived) =>
        {
            var rows = (await db.Beneficiaries.AsNoTracking().ToListAsync()).Where(b => includeArchived == true || !b.IsArchived).OrderBy(b => b.Slug, StringComparer.Ordinal).ToList();
            var enrolments = await db.ProgrammeEnrolments.AsNoTracking().ToListAsync();
            return Results.Ok(new { items = rows.Select(b => ToView(b, protector, enrolments)) });
        })
        .WithName("ListBeneficiaries").WithSummary("Beneficiary institutions with the projects they take part in")
        .WithTags(Tag).RequirePermission(Permissions.ProgrammeManage);

        app.MapPost($"{Root}/beneficiaries", async (HttpContext context, BeneficiaryRequest payload, MembershipDbContext db, IFieldProtector protector, TimeProvider clock) =>
        {
            var entity = new BeneficiaryEntity { Id = $"ben_{Guid.NewGuid():N}", CreatedAt = clock.GetUtcNow() };
            if (Apply(entity, payload, protector, creating: true) is { } error)
            {
                return Invalid(context, error);
            }

            if (await db.Beneficiaries.AnyAsync(b => b.Slug == entity.Slug))
            {
                return Conflict(context, "SLUG_TAKEN", $"The slug '{entity.Slug}' is already used.");
            }

            entity.UpdatedAt = entity.CreatedAt;
            db.Beneficiaries.Add(entity);
            await db.SaveChangesAsync();
            return Results.Created($"{Root}/beneficiaries/{entity.Id}", ToView(entity, protector, []));
        })
        .WithName("CreateBeneficiary").WithSummary("Add a beneficiary institution")
        .WithTags(Tag).RequirePermission(Permissions.ProgrammeManage);

        app.MapPut($"{Root}/beneficiaries/{{id}}", async (HttpContext context, string id, BeneficiaryRequest payload, MembershipDbContext db, IFieldProtector protector, TimeProvider clock) =>
        {
            var entity = await db.Beneficiaries.FirstOrDefaultAsync(b => b.Id == id);
            if (entity is null)
            {
                return NotFound(context, "Institution not found.");
            }

            if (Apply(entity, payload, protector, creating: false) is { } error)
            {
                return Invalid(context, error);
            }

            entity.UpdatedAt = clock.GetUtcNow();
            await db.SaveChangesAsync();
            return Results.Ok(ToView(entity, protector, await db.ProgrammeEnrolments.AsNoTracking().ToListAsync()));
        })
        .WithName("UpdateBeneficiary").WithSummary("Edit or archive an institution; fields left out stay")
        .WithTags(Tag).RequirePermission(Permissions.ProgrammeManage);

        app.MapPut($"{Root}/beneficiaries/{{id}}/enrolments/{{projectSlug}}", async (HttpContext context, string id, string projectSlug, EnrolmentRequest payload, MembershipDbContext db, TimeProvider clock) =>
        {
            if (!SlugPattern().IsMatch(projectSlug) || payload.Students is null or < 0 or > 1_000_000)
            {
                return Invalid(context, "A project slug and a students count (0 to 1,000,000) are required.");
            }

            if (!await db.Beneficiaries.AnyAsync(b => b.Id == id))
            {
                return NotFound(context, "Institution not found.");
            }

            var enrolment = await db.ProgrammeEnrolments.FirstOrDefaultAsync(e => e.BeneficiaryId == id && e.ProjectSlug == projectSlug);
            if (enrolment is null)
            {
                enrolment = new ProgrammeEnrolmentEntity { Id = $"enr_{Guid.NewGuid():N}", BeneficiaryId = id, ProjectSlug = projectSlug };
                db.ProgrammeEnrolments.Add(enrolment);
            }

            enrolment.Students = payload.Students.Value;
            enrolment.UpdatedAt = clock.GetUtcNow();
            await db.SaveChangesAsync();
            return Results.Ok(new { beneficiaryId = id, projectSlug, students = enrolment.Students });
        })
        .WithName("SetBeneficiaryEnrolment").WithSummary("Set how many of an institution's students a project reaches")
        .WithTags(Tag).RequirePermission(Permissions.ProgrammeManage);

        app.MapDelete($"{Root}/beneficiaries/{{id}}/enrolments/{{projectSlug}}", async (HttpContext context, string id, string projectSlug, MembershipDbContext db) =>
        {
            var enrolment = await db.ProgrammeEnrolments.FirstOrDefaultAsync(e => e.BeneficiaryId == id && e.ProjectSlug == projectSlug);
            if (enrolment is null)
            {
                return NotFound(context, "Enrolment not found.");
            }

            db.ProgrammeEnrolments.Remove(enrolment);
            await db.SaveChangesAsync();
            return Results.NoContent();
        })
        .WithName("RemoveBeneficiaryEnrolment").WithSummary("Take an institution out of a project")
        .WithTags(Tag).RequirePermission(Permissions.ProgrammeManage);
    }

    // ---- MoUs

    private static void MapMous(IEndpointRouteBuilder app)
    {
        app.MapGet($"{Root}/beneficiaries/{{id}}/mous", async (HttpContext context, string id, MembershipDbContext db, IFieldProtector protector) =>
            await db.Beneficiaries.AnyAsync(b => b.Id == id)
                ? Results.Ok(new { items = (await db.Mous.AsNoTracking().Where(m => m.BeneficiaryId == id).ToListAsync()).OrderByDescending(m => m.SignedOn).Select(m => ToView(m, protector)) })
                : NotFound(context, "Institution not found."))
        .WithName("ListMous").WithSummary("MoUs of an institution")
        .WithTags(Tag).RequirePermission(Permissions.ProgrammeManage);

        app.MapPost($"{Root}/beneficiaries/{{id}}/mous", async (HttpContext context, string id, MouRequest payload, MembershipDbContext db, IFieldProtector protector, TimeProvider clock) =>
        {
            if (!await db.Beneficiaries.AnyAsync(b => b.Id == id))
            {
                return NotFound(context, "Institution not found.");
            }

            if (payload.SignedOn is not { } signed || string.IsNullOrWhiteSpace(payload.Signatory) || payload.Signatory.Length > 200 || payload.ValidUntil is { } until && until < signed)
            {
                return Invalid(context, "signedOn and signatory are required; validUntil cannot be before signedOn.");
            }

            var mou = new MouEntity
            {
                Id = $"mou_{Guid.NewGuid():N}",
                BeneficiaryId = id,
                SignedOn = signed,
                ValidUntil = payload.ValidUntil,
                SignatoryProtected = protector.Protect(payload.Signatory.Trim()),
                SignatoryRole = string.IsNullOrWhiteSpace(payload.SignatoryRole) ? null : payload.SignatoryRole.Trim(),
                CreatedAt = clock.GetUtcNow()
            };
            db.Mous.Add(mou);
            await db.SaveChangesAsync();
            return Results.Created($"{Root}/mous/{mou.Id}", ToView(mou, protector));
        })
        .WithName("CreateMou").WithSummary("Record an MoU")
        .WithTags(Tag).RequirePermission(Permissions.ProgrammeManage);

        app.MapPut($"{Root}/mous/{{mouId}}/document", async (HttpContext context, string mouId, MembershipDbContext db, IAttachmentStore store) =>
        {
            var mou = await db.Mous.FirstOrDefaultAsync(m => m.Id == mouId);
            if (mou is null)
            {
                return NotFound(context, "MoU not found.");
            }

            var contentType = context.Request.ContentType?.Split(';')[0].Trim().ToLowerInvariant() ?? string.Empty;
            if (!DocumentTypes.Contains(contentType))
            {
                return ApiError.Create(context, StatusCodes.Status415UnsupportedMediaType, "UNSUPPORTED_MEDIA_TYPE", "An MoU document must be a PDF, JPEG or PNG.");
            }

            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await context.Request.Body.ReadAsync(chunk, context.RequestAborted)) > 0)
            {
                if (buffer.Length + read > MaxDocumentBytes)
                {
                    return ApiError.Create(context, StatusCodes.Status413PayloadTooLarge, "PAYLOAD_TOO_LARGE", "The document is larger than 10 MB.");
                }

                buffer.Write(chunk, 0, read);
            }

            if (buffer.Length == 0 || !AttachmentRules.MatchesSignature(contentType, buffer.GetBuffer().AsSpan(0, (int)Math.Min(buffer.Length, 16))))
            {
                return Invalid(context, "The file's contents do not match its declared type.");
            }

            var previous = mou.DocumentKey;
            var key = $"mou-{Guid.NewGuid():N}";
            buffer.Position = 0;
            await store.SaveAsync(key, buffer, context.RequestAborted);
            mou.DocumentKey = key;
            mou.DocumentContentType = contentType;
            mou.DocumentBytes = buffer.Length;
            mou.DocumentFileName = AttachmentRules.SafeFileName(context.Request.Headers["X-Filename"].ToString());
            await db.SaveChangesAsync();
            if (previous is not null)
            {
                await store.DeleteAsync(previous, context.RequestAborted);
            }

            return Results.Ok(new { id = mou.Id, hasDocument = true, bytes = mou.DocumentBytes });
        })
        .WithName("UploadMouDocument").WithSummary("Attach the signed MoU (PDF, JPEG or PNG, up to 10 MB; raw body, `X-Filename` header)")
        .WithTags(Tag).RequirePermission(Permissions.ProgrammeManage);

        app.MapGet($"{Root}/mous/{{mouId}}/document", async (HttpContext context, string mouId, MembershipDbContext db, IAttachmentStore store) =>
        {
            var mou = await db.Mous.AsNoTracking().FirstOrDefaultAsync(m => m.Id == mouId);
            var stream = mou?.DocumentKey is null ? null : await store.OpenReadAsync(mou.DocumentKey, context.RequestAborted);
            if (mou is null || stream is null)
            {
                return NotFound(context, "No document for this MoU.");
            }

            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            return Results.File(stream, mou.DocumentContentType, mou.DocumentFileName, enableRangeProcessing: false);
        })
        .WithName("DownloadMouDocument").WithSummary("Download an MoU document (permission-gated, never cached)")
        .WithTags(Tag).RequirePermission(Permissions.ProgrammeManage);
    }

    // ---- sessions

    private static void MapSessions(IEndpointRouteBuilder app)
    {
        app.MapPost($"{Root}/sessions", async (HttpContext context, SessionInput payload, MembershipDbContext db, TimeProvider clock) =>
        {
            var results = await LogAsync(db, [payload], SelfEndpoints.Subject(context) ?? "unknown", clock);
            var only = results[0];
            return only.Result switch
            {
                "invalid" => Invalid(context, only.Error ?? "Invalid session."),
                "created" => Results.Created($"{Root}/sessions/{only.Id}", only),
                _ => Results.Ok(only)
            };
        })
        .WithName("LogProgrammeSession").WithSummary("Log one delivered session")
        .WithDescription("Idempotent on `clientId` (the field app's own id): sending it again returns `duplicate` and changes nothing.")
        .WithTags(Tag).RequirePermission(Permissions.ProgrammeLog);

        app.MapPost($"{Root}/sessions/batch", async (HttpContext context, SessionBatch payload, MembershipDbContext db, TimeProvider clock) =>
        {
            if (payload.Sessions is null or { Count: 0 } || payload.Sessions.Count > MaxBatch)
            {
                return Invalid(context, $"Send between 1 and {MaxBatch} sessions.");
            }

            return Results.Ok(new { items = await LogAsync(db, payload.Sessions, SelfEndpoints.Subject(context) ?? "unknown", clock) });
        })
        .WithName("LogProgrammeSessionsBatch").WithSummary("Log sessions in a batch, late and in any order")
        .WithDescription("For field staff on poor connections. Each item is answered `created`, `duplicate` (same `clientId` seen before) or `invalid` with the reason; one bad item does not stop the rest. A session may be up to a year old; one in the future is invalid.")
        .WithTags(Tag).RequirePermission(Permissions.ProgrammeLog);

        app.MapGet($"{Root}/sessions", async (MembershipDbContext db, string? project, string? beneficiaryId, int? limit, int? offset) =>
        {
            var rows = (await db.SessionLogs.AsNoTracking().ToListAsync()).Where(s => (project is null || s.ProjectSlug == project) && (beneficiaryId is null || s.BeneficiaryId == beneficiaryId))
                .OrderByDescending(s => s.HeldOn).ThenBy(s => s.Id, StringComparer.Ordinal).ToList();
            var take = Math.Clamp(limit ?? 50, 1, 200);
            var skip = Math.Max(offset ?? 0, 0);
            return Results.Ok(new { items = rows.Skip(skip).Take(take), total = rows.Count, limit = take, offset = skip });
        })
        .WithName("ListProgrammeSessions").WithSummary("Logged sessions")
        .WithTags(Tag).RequirePermission(Permissions.ProgrammeManage);
    }

    private static async Task<List<SessionResult>> LogAsync(MembershipDbContext db, List<SessionInput> inputs, string actor, TimeProvider clock)
    {
        var now = clock.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var clientIds = inputs.Select(i => i.ClientId?.Trim() ?? string.Empty).Where(c => c.Length > 0).Distinct().ToList();
        var known = (await db.SessionLogs.AsNoTracking().Where(s => clientIds.Contains(s.ClientId)).ToListAsync()).ToDictionary(s => s.ClientId);
        var beneficiaries = await db.Beneficiaries.AsNoTracking().Where(b => !b.IsArchived).ToDictionaryAsync(b => b.Slug);
        var results = new List<SessionResult>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var input in inputs)
        {
            var clientId = input.ClientId?.Trim();
            if (string.IsNullOrEmpty(clientId) || clientId.Length > 64)
            {
                results.Add(new(input.ClientId, "invalid", null, "clientId is required (up to 64 characters)."));
                continue;
            }

            if (known.TryGetValue(clientId, out var existing))
            {
                results.Add(new(clientId, "duplicate", existing.Id, null));
                continue;
            }

            if (!seen.Add(clientId))
            {
                results.Add(new(clientId, "duplicate", null, null));
                continue;
            }

            if (Validate(input, today, beneficiaries) is { } error)
            {
                results.Add(new(clientId, "invalid", null, error));
                continue;
            }

            var log = new SessionLogEntity
            {
                Id = $"ses_{Guid.NewGuid():N}",
                ClientId = clientId,
                ProjectSlug = input.ProjectSlug!.Trim(),
                BeneficiaryId = beneficiaries[input.BeneficiarySlug!.Trim()].Id,
                HeldOn = input.HeldOn!.Value,
                Topic = input.Topic!.Trim(),
                Attendance = input.Attendance!.Value,
                DurationMinutes = input.DurationMinutes!.Value,
                LoggedBy = actor,
                LoggedAt = now
            };
            db.SessionLogs.Add(log);
            results.Add(new(clientId, "created", log.Id, null));
        }

        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // A concurrent replay of the same client id won the unique index: report what is stored instead.
            db.ChangeTracker.Clear();
            var stored = (await db.SessionLogs.AsNoTracking().Where(s => clientIds.Contains(s.ClientId)).ToListAsync()).ToDictionary(s => s.ClientId);
            return [.. results.Select(r => r.Result == "created" && r.ClientId is not null && stored.TryGetValue(r.ClientId, out var s) ? r with { Result = s.Id == r.Id ? "created" : "duplicate", Id = s.Id } : r)];
        }

        return results;
    }

    private static string? Validate(SessionInput input, DateOnly today, Dictionary<string, BeneficiaryEntity> beneficiaries)
    {
        if (string.IsNullOrWhiteSpace(input.ProjectSlug) || !SlugPattern().IsMatch(input.ProjectSlug.Trim()))
        {
            return "projectSlug is required.";
        }

        if (string.IsNullOrWhiteSpace(input.BeneficiarySlug) || !beneficiaries.ContainsKey(input.BeneficiarySlug.Trim()))
        {
            return "beneficiarySlug must name an active institution.";
        }

        if (input.HeldOn is not { } held || held > today.AddDays(1) || held < today.AddDays(-MaxLateDays))
        {
            return $"heldOn is required, not in the future and at most {MaxLateDays} days old.";
        }

        if (string.IsNullOrWhiteSpace(input.Topic) || input.Topic.Trim().Length > 200)
        {
            return "topic is required (up to 200 characters).";
        }

        if (input.Attendance is null or < 0 or > 10_000 || input.DurationMinutes is null or < 1 or > 600)
        {
            return "attendance (0 to 10000) and durationMinutes (1 to 600) are required.";
        }

        return null;
    }

    // ---- outcomes, project summary and published figures

    private static void MapOutcomesAndFigures(IEndpointRouteBuilder app)
    {
        app.MapPut($"{Root}/outcomes", async (HttpContext context, OutcomeRequest payload, MembershipDbContext db, TimeProvider clock) =>
        {
            if (string.IsNullOrWhiteSpace(payload.ProjectSlug) || !SlugPattern().IsMatch(payload.ProjectSlug.Trim()) || string.IsNullOrWhiteSpace(payload.MetricKey) || payload.MetricKey.Trim().Length > 80
                || payload.PeriodStart is not { } start || payload.PeriodEnd is not { } end || end < start || payload.Value is not { } value || double.IsNaN(value) || double.IsInfinity(value) || string.IsNullOrWhiteSpace(payload.Unit) || payload.Unit.Length > 40)
            {
                return Invalid(context, "projectSlug, metricKey, periodStart, periodEnd (not before the start), value and unit are required.");
            }

            var project = payload.ProjectSlug.Trim();
            var key = payload.MetricKey.Trim();
            var outcome = await db.ProgrammeOutcomes.FirstOrDefaultAsync(o => o.ProjectSlug == project && o.MetricKey == key && o.PeriodStart == start && o.PeriodEnd == end);
            if (outcome is null)
            {
                outcome = new ProgrammeOutcomeEntity { Id = $"out_{Guid.NewGuid():N}", ProjectSlug = project, MetricKey = key, PeriodStart = start, PeriodEnd = end };
                db.ProgrammeOutcomes.Add(outcome);
            }

            outcome.Value = value;
            outcome.Unit = payload.Unit.Trim();
            outcome.Note = string.IsNullOrWhiteSpace(payload.Note) ? null : payload.Note.Trim();
            outcome.RecordedBy = SelfEndpoints.Subject(context) ?? "unknown";
            outcome.RecordedAt = clock.GetUtcNow();
            await db.SaveChangesAsync();
            return Results.Ok(outcome);
        })
        .WithName("RecordProgrammeOutcome").WithSummary("Record an outcome metric for a project and period (replaces the same key and period)")
        .WithTags(Tag).RequirePermission(Permissions.ProgrammeManage);

        app.MapGet($"{Root}/projects/{{projectSlug}}/summary", async (string projectSlug, MembershipDbContext db, IFieldProtector protector) =>
        {
            var enrolments = (await db.ProgrammeEnrolments.AsNoTracking().ToListAsync()).Where(e => e.ProjectSlug == projectSlug).ToList();
            var ids = enrolments.Select(e => e.BeneficiaryId).ToList();
            var institutions = (await db.Beneficiaries.AsNoTracking().ToListAsync()).Where(b => ids.Contains(b.Id)).OrderBy(b => b.Slug, StringComparer.Ordinal).ToList();
            var mous = (await db.Mous.AsNoTracking().ToListAsync()).Where(m => ids.Contains(m.BeneficiaryId)).OrderBy(m => m.SignedOn).ToList();
            var sessions = (await db.SessionLogs.AsNoTracking().ToListAsync()).Where(s => s.ProjectSlug == projectSlug).ToList();
            var outcomes = (await db.ProgrammeOutcomes.AsNoTracking().ToListAsync()).Where(o => o.ProjectSlug == projectSlug).OrderBy(o => o.MetricKey, StringComparer.Ordinal).ThenBy(o => o.PeriodStart).ToList();
            return Results.Ok(new
            {
                projectSlug,
                institutions = institutions.Select(b => ToView(b, protector, enrolments)),
                mous = mous.Select(m => ToView(m, protector)),
                sessionsDelivered = sessions.Count,
                studentsReached = enrolments.Sum(e => e.Students),
                attendanceTotal = sessions.Sum(s => s.Attendance),
                outcomes,
                fingerprint = Fingerprint(sessions, enrolments)
            });
        })
        .WithName("ProjectProgrammeSummary").WithSummary("A project's institutions, MoUs, sessions delivered, students reached and outcomes")
        .WithTags(Tag).RequirePermission(Permissions.ProgrammeManage);

        app.MapGet($"{Root}/figures", async (MembershipDbContext db) =>
        {
            var beneficiaries = (await db.Beneficiaries.AsNoTracking().ToListAsync()).Where(b => !b.IsArchived).ToList();
            var activeIds = beneficiaries.Select(b => b.Id).ToHashSet();
            var enrolments = (await db.ProgrammeEnrolments.AsNoTracking().ToListAsync()).Where(e => activeIds.Contains(e.BeneficiaryId)).ToList();
            var sessions = await db.SessionLogs.AsNoTracking().ToListAsync();
            var projects = enrolments.Select(e => e.ProjectSlug).Union(sessions.Select(s => s.ProjectSlug)).Distinct().Order(StringComparer.Ordinal)
                .Select(p => new
                {
                    projectSlug = p,
                    institutions = enrolments.Count(e => e.ProjectSlug == p),
                    studentsReached = enrolments.Where(e => e.ProjectSlug == p).Sum(e => e.Students),
                    sessionsDelivered = sessions.Count(s => s.ProjectSlug == p),
                    fingerprint = Fingerprint(sessions.Where(s => s.ProjectSlug == p), enrolments.Where(e => e.ProjectSlug == p))
                }).ToList();
            return Results.Ok(new
            {
                institutions = beneficiaries.Count(b => enrolments.Any(e => e.BeneficiaryId == b.Id)),
                studentsReached = enrolments.Sum(e => e.Students),
                sessionsDelivered = sessions.Count,
                projects,
                fingerprint = Fingerprint(sessions, enrolments)
            });
        })
        .WithName("ProgrammeFigures").WithSummary("Aggregate figures only: the numbers that may be published")
        .WithDescription("Counts of institutions, students reached and sessions delivered, per project and in total, each with a fingerprint of the logged sessions and enrolments behind it. No names, addresses, MoUs or districts are included. Impact metrics (BE-085) read this, not the institution endpoints.")
        .WithTags(Tag).RequirePermission(Permissions.ProgrammeManage);
    }

    // ---- helpers

    private static string Fingerprint(IEnumerable<SessionLogEntity> sessions, IEnumerable<ProgrammeEnrolmentEntity> enrolments)
    {
        var text = string.Join('|', sessions.OrderBy(s => s.Id, StringComparer.Ordinal).Select(s => $"s:{s.Id}")
            .Concat(enrolments.OrderBy(e => e.Id, StringComparer.Ordinal).Select(e => $"e:{e.Id}:{e.Students}")));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant()[..16];
    }

    private static string? Apply(BeneficiaryEntity entity, BeneficiaryRequest request, IFieldProtector protector, bool creating)
    {
        if (creating && (string.IsNullOrWhiteSpace(request.Slug) || !SlugPattern().IsMatch(request.Slug.Trim())))
        {
            return "slug is required (lower-case letters, digits and hyphens).";
        }

        if (creating && (string.IsNullOrWhiteSpace(request.NameEn) || string.IsNullOrWhiteSpace(request.NameBn) || string.IsNullOrWhiteSpace(request.District)))
        {
            return "nameEn, nameBn and district are required.";
        }

        if (request.Kind is not null && !Kinds.Contains(request.Kind))
        {
            return "kind must be school, madrasa, mission or other.";
        }

        if (request.NameEn?.Length > 200 || request.NameBn?.Length > 200 || request.District?.Length > 100 || request.Address?.Length > 500)
        {
            return "A field is too long.";
        }

        if (creating)
        {
            entity.Slug = request.Slug!.Trim();
        }

        entity.Kind = request.Kind ?? entity.Kind;
        entity.NameEn = request.NameEn?.Trim() ?? entity.NameEn;
        entity.NameBn = request.NameBn?.Trim() ?? entity.NameBn;
        entity.District = request.District?.Trim() ?? entity.District;
        entity.IsArchived = request.IsArchived ?? entity.IsArchived;
        if (request.Address is not null)
        {
            entity.AddressProtected = string.IsNullOrWhiteSpace(request.Address) ? null : protector.Protect(request.Address.Trim());
        }

        return null;
    }

    private static object ToView(BeneficiaryEntity b, IFieldProtector protector, List<ProgrammeEnrolmentEntity> enrolments) => new
    {
        b.Id,
        b.Slug,
        b.Kind,
        b.NameEn,
        b.NameBn,
        address = b.AddressProtected is null ? null : protector.Unprotect(b.AddressProtected),
        b.District,
        b.IsArchived,
        projects = enrolments.Where(e => e.BeneficiaryId == b.Id).OrderBy(e => e.ProjectSlug, StringComparer.Ordinal).Select(e => new { e.ProjectSlug, e.Students })
    };

    private static object ToView(MouEntity m, IFieldProtector protector) => new
    {
        m.Id,
        m.BeneficiaryId,
        m.SignedOn,
        m.ValidUntil,
        signatory = protector.Unprotect(m.SignatoryProtected),
        m.SignatoryRole,
        hasDocument = m.DocumentKey is not null,
        m.DocumentFileName,
        m.DocumentBytes
    };

    private static IResult Invalid(HttpContext context, string message) => ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", message);

    private static IResult NotFound(HttpContext context, string message) => ApiError.Create(context, StatusCodes.Status404NotFound, "NOT_FOUND", message);

    private static IResult Conflict(HttpContext context, string code, string message) => ApiError.Create(context, StatusCodes.Status409Conflict, code, message);
}
