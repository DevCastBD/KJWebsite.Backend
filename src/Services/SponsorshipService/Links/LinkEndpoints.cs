using System.Data;
using System.Globalization;
using KJWebsite.BuildingBlocks;
using KJWebsite.BuildingBlocks.Idempotency;
using KJWebsite.BuildingBlocks.Security;
using Microsoft.EntityFrameworkCore;
using SponsorshipService.Data;
using SponsorshipService.Students;

namespace SponsorshipService.Links;

public sealed record CreateLinkRequest(string? StudentId, string? StartDate);
public sealed record UnlinkRequest(string? Type, string? EndDate, string? Reason);
public sealed record LinkView(string Id, string DonorId, string StudentId, string PreferredName, DateOnly StartDate, DateOnly? EndDate, string? EndType, string? EndReason, string NoticeStatus, DateTimeOffset CreatedAt, int Version);

public static partial class LinkEndpoints
{
    public static void MapLinks(this IEndpointRouteBuilder app)
    {
        var route = $"{StudentEndpoints.Route}/donors/{{donorId}}/students";
        app.MapGet(route, async (HttpContext context, string donorId, SponsorshipDbContext db, string? status, int? limit, int? offset) =>
        {
            if (status is not null and not "all") return StudentEndpoints.Invalid(context, ["status"]);
            var take = Math.Clamp(limit ?? 20, 1, 100);
            var skip = Math.Max(offset ?? 0, 0);
            var rows = await (from l in db.Links.AsNoTracking()
                              join s in db.Students on l.StudentId equals s.Id
                              where l.DonorId == donorId && (status == "all" || l.EndType == null)
                              select new { Link = l, s.PreferredName }).ToListAsync(context.RequestAborted);
            var items = rows.OrderByDescending(x => x.Link.CreatedAt).ThenBy(x => x.Link.Id, StringComparer.Ordinal).Skip(skip).Take(take).Select(x => View(x.Link, x.PreferredName)).ToList();
            return Results.Ok(new PagingEnvelope<LinkView>(items, rows.Count, null, take, skip));
        }).WithName("ListDonorStudentLinks").WithTags("Admin: Sponsorship").RequirePermission(Permissions.SponsorshipView);

        app.MapPost(route, CreateAsync).WithName("CreateDonorStudentLink").WithTags("Admin: Sponsorship").RequirePermission(Permissions.SponsorshipLinkManage).RequireIdempotency();
        app.MapPost($"{route}/{{studentId}}/unlink", UnlinkAsync).WithName("UnlinkDonorStudent").WithTags("Admin: Sponsorship").RequirePermission(Permissions.SponsorshipLinkManage);
    }

    private static async Task<IResult> CreateAsync(HttpContext context, string donorId, CreateLinkRequest payload, SponsorshipDbContext db, IFinanceDonors finance, TimeProvider clock, ILoggerFactory loggers)
    {
        var invalid = new List<string>();
        if (string.IsNullOrWhiteSpace(payload.StudentId) || payload.StudentId.Length > 64) invalid.Add("studentId");
        if (!Date(payload.StartDate, out var start)) invalid.Add("startDate");
        if (invalid.Count > 0) return StudentEndpoints.Invalid(context, invalid);
        var student = await db.Students.AsNoTracking().FirstOrDefaultAsync(s => s.Id == payload.StudentId, context.RequestAborted);
        if (student is null) return StudentEndpoints.NotFound(context);
        if (student.Status != StudentStatuses.Active) return Error(context, "STUDENT_NOT_ACTIVE", "Student is not active.");
        var target = await finance.TargetAsync(donorId, Bearer(context), context.RequestAborted);
        if (target.Outcome == FinanceOutcome.NotFound) return ApiError.Create(context, 404, "NOT_FOUND", "Donor not found.");
        if (target.Outcome != FinanceOutcome.Ok || target.Value is null) return Unavailable(context);
        if (target.Value.Status != "active") return Error(context, "DONOR_INACTIVE", "Donor is not active.");
        var active = await db.Links.AsNoTracking().FirstOrDefaultAsync(l => l.StudentId == student.Id && l.EndType == null, context.RequestAborted);
        if (active is not null) return Conflict(context, active.DonorId == donorId);
        var link = new LinkEntity { DonorId = donorId, DonorSubject = target.Value.AccountSubject, StudentId = student.Id, StartDate = start, CreatedBy = StudentEndpoints.Actor(context), CreatedAt = clock.GetUtcNow() };
        // One SaveChanges is atomic; the filtered unique indexes (one active link per student, per pair) are what make two parallel requests safe,
        // so the loser gets a unique violation (never a serialization failure) and is mapped to the right 409.
        db.Links.Add(link);
        try
        {
            await db.SaveChangesAsync(context.RequestAborted);
        }
        catch (DbUpdateException ex) when (ActiveUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            var same = await db.Links.AnyAsync(l => l.StudentId == student.Id && l.DonorId == donorId && l.EndType == null, context.RequestAborted);
            return Conflict(context, same);
        }
        var notice = await finance.NotifyAsync(donorId, link.Id, Bearer(context), context.RequestAborted);
        var logger = loggers.CreateLogger("SponsorshipLinks");
        if (notice.Outcome == FinanceOutcome.Ok && notice.Value is "queued" or "suppressed")
        {
            await RecordNoticeAsync(db, link, notice.Value!, logger);
        }
        else LogNoticePending(logger, link.Id);
        return Results.Created($"{StudentEndpoints.Route}/donors/{donorId}/students", View(link, student.PreferredName));
    }

    /// <summary>
    /// The link exists and Finance already has the notice, so failing to record its status must not fail the request, and the write must not be
    /// cut short because the caller went away. A status that cannot be saved stays `pending`.
    /// </summary>
    private static async Task RecordNoticeAsync(SponsorshipDbContext db, LinkEntity link, string outcome, ILogger logger)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                link.NoticeStatus = outcome;
                link.Version++;
                await db.SaveChangesAsync(CancellationToken.None);
                return;
            }
            catch (DbUpdateConcurrencyException) when (attempt == 0)
            {
                await db.Entry(link).ReloadAsync(CancellationToken.None);
            }
            catch (DbUpdateException)
            {
                link.NoticeStatus = "pending";
                LogNoticeStatusNotSaved(logger, link.Id);
                return;
            }
        }
    }

    private static async Task<IResult> UnlinkAsync(HttpContext context, string donorId, string studentId, UnlinkRequest payload, SponsorshipDbContext db, ProgrammeNames names, FoundationDay day, TimeProvider clock)
    {
        var today = day.Today;
        var invalid = new List<string>();
        var end = default(DateOnly);
        var hasEnd = !string.IsNullOrWhiteSpace(payload.EndDate);
        if (payload.Type is not LinkEndTypes.Ended and not LinkEndTypes.Mistaken) invalid.Add("type");
        if (payload.Reason is { Length: > 500 } || (payload.Type == LinkEndTypes.Mistaken && string.IsNullOrWhiteSpace(payload.Reason))) invalid.Add("reason");

        // An end date is read here only to refuse a malformed or future one. Whether it is required depends on whether the link has started (below).
        if (payload.Type == LinkEndTypes.Ended && hasEnd && (!Date(payload.EndDate, out end) || end > today)) invalid.Add("endDate");
        if (invalid.Count > 0) return StudentEndpoints.Invalid(context, invalid);
        if (payload.Type == LinkEndTypes.Mistaken && !context.User.HasClaim(KjClaims.Permission, Permissions.SponsorshipLinkVoid))
            return ApiError.Create(context, 403, "FORBIDDEN", "You do not have permission to do this.");

        // Content may be slow, and a transaction must never wait on another service, so the programme names are read before it opens (and only
        // when a frozen copy will be made: the link is active and has started).
        string? namedProgramme = null;
        ProgrammeRef? programmeEn = null, programmeBn = null;
        if (payload.Type == LinkEndTypes.Ended)
        {
            var started = await db.Links.AsNoTracking().Where(l => l.DonorId == donorId && l.StudentId == studentId && l.EndType == null).Select(l => (DateOnly?)l.StartDate).FirstOrDefaultAsync(context.RequestAborted);
            if (started is null) return Error(context, "LINK_NOT_ACTIVE", "Link is not active.");
            if (started <= today)
            {
                namedProgramme = await db.Students.AsNoTracking().Where(s => s.Id == studentId).Select(s => s.ProgrammeId).FirstOrDefaultAsync(context.RequestAborted);
                if (namedProgramme is not null)
                {
                    programmeEn = await names.RefAsync(namedProgramme, "en", context.RequestAborted);
                    programmeBn = await names.RefAsync(namedProgramme, "bn", context.RequestAborted);
                }
            }
        }

        await using var transaction = await db.Database.BeginTransactionAsync(context.RequestAborted);
        var link = await db.Links.FirstOrDefaultAsync(l => l.DonorId == donorId && l.StudentId == studentId && l.EndType == null, context.RequestAborted);
        if (link is null) return Error(context, "LINK_NOT_ACTIVE", "Link is not active.");

        // A link that has not started can be ended by staff, but nothing was ever shared: no frozen copy and no end date, so the donor never sees it.
        var cancelled = payload.Type == LinkEndTypes.Ended && link.StartDate > today;
        var freeze = payload.Type == LinkEndTypes.Ended && !cancelled;
        if (freeze && (!hasEnd || end < link.StartDate)) return StudentEndpoints.Invalid(context, ["endDate"]);
        var student = await db.Students.SingleAsync(s => s.Id == studentId, context.RequestAborted);
        var now = clock.GetUtcNow();
        link.EndType = payload.Type;
        link.EndDate = freeze ? end : null;
        link.EndReason = string.IsNullOrWhiteSpace(payload.Reason) ? null : payload.Reason.Trim();
        link.EndedBy = StudentEndpoints.Actor(context);
        link.EndedAt = now;
        link.Version++;
        if (freeze)
        {
            var consent = await db.Consents.AsNoTracking().Where(c => c.StudentId == studentId).OrderByDescending(c => c.Id).FirstOrDefaultAsync(context.RequestAborted);

            // Names are only used if the programme is the one that was looked up; a programme changed in the meantime keeps its slug as the name.
            var sameProgramme = namedProgramme == student.ProgrammeId && programmeEn is not null && programmeBn is not null;
            var en = sameProgramme ? programmeEn! : new ProgrammeRef(student.ProgrammeId, student.ProgrammeId);
            var bnName = sameProgramme ? programmeBn!.Name : student.ProgrammeId;
            var view = DonorProjection.Of(student, consent?.Granted == true, en, "en");
            db.FrozenCopies.Add(new FrozenCopyEntity
            {
                LinkId = link.Id,
                StudentId = view.Id,
                PreferredName = view.PreferredName,
                Class = view.Class,
                School = view.School,
                ProgrammeId = en.Id,
                ProgrammeNameEn = en.Name,
                ProgrammeNameBn = bnName,
                District = view.District,
                Status = view.Status,
                StoryEn = consent?.Granted == true ? student.StoryEn : null,
                StoryBn = consent?.Granted == true ? student.StoryBn : null,
                PhotoKey = view.HasPhoto ? student.PhotoKey : null,
                PhotoContentType = view.HasPhoto ? student.PhotoContentType : null,
                FrozenAt = now
            });

            // The copy keeps a reference to the live photo file. Bumping the student's version makes a photo replace or delete that read the student
            // before this commit fail its own save (409) instead of deleting the file the copy now points at.
            student.Version++;
        }

        var linkId = link.Id;
        try { await db.SaveChangesAsync(context.RequestAborted); await transaction.CommitAsync(context.RequestAborted); }
        catch (DbUpdateException ex)
        {
            // Roll back first: on Postgres a failed statement leaves the transaction unusable. Then tell the two losses apart: another request ended
            // this link first (its concurrency token, or the copy's key, clashed), or the student changed at the same moment.
            await transaction.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            if (!await db.Links.AsNoTracking().AnyAsync(l => l.Id == linkId && l.EndType == null, context.RequestAborted))
                return Error(context, "LINK_NOT_ACTIVE", "Link is not active.");
            if (ex is DbUpdateConcurrencyException)
                return Error(context, "STUDENT_CHANGED", "The student was changed at the same moment. Check the student and try again.");
            throw;
        }

        return Results.Ok(View(link, student.PreferredName));
    }

    /// <summary>True when the exception is one of the two "active link" unique indexes being violated, in SQLite's or Npgsql's wording (public so a test can feed it each provider's message).</summary>
    public static bool ActiveUniqueViolation(DbUpdateException ex)
    {
        var message = ex.InnerException?.Message ?? ex.Message;
        return message.Contains("UX_links_active_", StringComparison.Ordinal) ||
            (message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase) && message.Contains("links.StudentId", StringComparison.Ordinal));
    }
    internal static bool Date(string? value, out DateOnly date) => DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    internal static string Bearer(HttpContext context) => context.Request.Headers.Authorization.ToString() is { } header && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..] : string.Empty;
    internal static IResult Unavailable(HttpContext context) => ApiError.Create(context, 503, "FINANCE_UNAVAILABLE", "Finance could not be reached.");
    private static IResult Error(HttpContext context, string code, string message) => ApiError.Create(context, 409, code, message);
    private static IResult Conflict(HttpContext context, bool same) => Error(context, same ? "LINK_EXISTS" : "STUDENT_ALREADY_SPONSORED", same ? "This link already exists." : "Student already has an active sponsor.");
    private static LinkView View(LinkEntity link, string name) => new(link.Id, link.DonorId, link.StudentId, name, link.StartDate, link.EndDate, link.EndType, link.EndReason, link.NoticeStatus, link.CreatedAt, link.Version);
    [LoggerMessage(Level = LogLevel.Warning, Message = "The notice for link {LinkId} is pending.")]
    private static partial void LogNoticePending(ILogger logger, string linkId);
    [LoggerMessage(Level = LogLevel.Warning, Message = "The notice status of link {LinkId} could not be saved; it stays pending.")]
    private static partial void LogNoticeStatusNotSaved(ILogger logger, string linkId);
}
