// These ToLower()/Contains() calls are translated to SQL (LOWER / LIKE), not run in-process, so the culture and comparison rules do not apply.
#pragma warning disable CA1304, CA1311, CA1862
using KJWebsite.BuildingBlocks;
using KJWebsite.BuildingBlocks.Security;
using MembershipService.Data;
using MembershipService.Data.Entities;
using MembershipService.Intake;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MembershipService.MemberDirectory;

/// <summary>One public directory row as it leaves the database: a field the member did not approve is NULL in the result set itself.</summary>
public sealed record PublicRow(string Id, string? FirstName, string? LastName, string? NameBn, string? Expertise, bool Photo, bool Alumni);

public sealed record PublicEntry(string Id, string? Name, string? NameBn, string? PhotoUrl, IReadOnlyList<string>? Expertise, bool Alumni = false);

public sealed record PublicPage(IReadOnlyList<PublicEntry> Items, int Total, int PageSize, IReadOnlyList<string> Chapters);

public sealed record StaffEntry(string Id, string MemberCode, string Name, string? NameBn, string Status, DateTimeOffset JoinedAt, string? Expertise, bool HasPhoto, bool ConsentName, bool ConsentPhotograph, bool ConsentExpertise);

public sealed record StaffPage(IReadOnlyList<StaffEntry> Items, int Total);

public sealed record StaffMember(StaffEntry Member, string Email, string Phone, string Address, string DateOfBirth, string BloodGroup);

/// <summary>
/// Internal and public member directories. The public one is built so that publishing cannot be an afterthought: the filter
/// (active, at least one consent, no erasure request), the search and the projection are all in the SQL statement, so a
/// field a member did not approve is never read by the application, let alone mapped away afterwards.
/// </summary>
public static class DirectoryEndpoints
{
    public const int PageSize = 24;
    public const int MaxPage = 50;
    public const int MaxQuery = 100;

    /// <summary>Photos are published only once a scan has marked them clean (the media pipeline, #41, sets it).</summary>
    public const string CleanScan = "clean";

    public static IQueryable<PublicRow> PublicQuery(MembershipDbContext db, string? q, string? chapter = null, DateOnly? today = null)
    {
        var members = db.Members.AsNoTracking()
            // Active members, and former members only if they opted in to being listed as alumni; leave, lapsed and terminated never appear.
            .Where(m => (m.Status == "active" || (m.Status == "alumni" && m.ConsentAlumni)) && (m.ConsentName || m.ConsentPhotograph || m.ConsentExpertise))
            .Where(m => !db.DeletionRequests.Any(d => d.MemberId == m.Id));

        if (!string.IsNullOrEmpty(q))
        {
            // Search sees only what the member has agreed to publish: a hidden name or expertise can never be probed by guessing it.
            members = members.Where(m =>
                (m.ConsentName && ((m.FirstName + " " + m.LastName).ToLower().Contains(q) || (m.NameBn != null && m.NameBn.Contains(q))))
                || (m.ConsentExpertise && m.Expertise != null && m.Expertise.ToLower().Contains(q)));
        }

        if (!string.IsNullOrEmpty(chapter))
        {
            // Chapter membership is as public as the org chart: it counts only for members who agreed to be named.
            var on = today ?? DateOnly.FromDateTime(DateTime.UtcNow);
            members = members.Where(m => m.ConsentName && db.CommitteeMembers.Any(t => t.MemberId == m.Id && t.StartsOn <= on && (t.EndsOn == null || t.EndsOn >= on)
                && db.Committees.Any(c => c.Id == t.CommitteeId && c.Kind == "chapter" && c.ArchivedAt == null && c.NameEn == chapter)));
        }

        // Ordered by the opaque id: ordering by a name or date would reveal how members rank on fields they did not publish.
        return members.OrderBy(m => m.Id).Select(m => new PublicRow(
            m.Id,
            m.ConsentName ? m.FirstName : null,
            m.ConsentName ? m.LastName : null,
            m.ConsentName ? m.NameBn : null,
            m.ConsentExpertise ? m.Expertise : null,
            m.ConsentPhotograph && m.PhotoKey != null && m.PhotoScanStatus == CleanScan,
            m.Status == "alumni"));
    }

    public static string? CleanQuery(string? q) => string.IsNullOrWhiteSpace(q) ? null : (q.Trim().Length > MaxQuery ? q.Trim()[..MaxQuery] : q.Trim()).ToLowerInvariant();

    public static IReadOnlyList<string>? SplitExpertise(string? text) => string.IsNullOrWhiteSpace(text)
        ? null
        : [.. text.Split([',', ';', '\n', '،'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length > 0).Select(t => t.Length > 60 ? t[..60] : t).Distinct(StringComparer.OrdinalIgnoreCase).Take(10)];

    public static void MapDirectory(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/members/directory", async (HttpContext context, MembershipDbContext db, IOptions<MembershipOptions> options, TimeProvider clock, string? q, string? chapter, int? page) =>
        {
            var number = page ?? 1;
            if (number is < 1 or > MaxPage)
            {
                return ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", $"Invalid fields: page (1-{MaxPage}).");
            }

            // Read live on every request and never cached: withdrawing consent must show on the very next call.
            context.Response.Headers.CacheControl = "no-store";
            var query = PublicQuery(db, CleanQuery(q), string.IsNullOrWhiteSpace(chapter) ? null : chapter.Trim(), DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime));
            var total = await query.CountAsync();
            var rows = await query.Skip((number - 1) * PageSize).Take(PageSize).ToListAsync();
            var baseUrl = options.Value.PublicApiBaseUrl.TrimEnd('/');
            var items = rows.Select(r => new PublicEntry(
                r.Id,
                r.FirstName is null ? null : $"{r.FirstName} {r.LastName}".Trim(),
                string.IsNullOrWhiteSpace(r.NameBn) ? null : r.NameBn,
                r.Photo ? $"{baseUrl}/api/v1/members/directory/{r.Id}/photo" : null,
                SplitExpertise(r.Expertise),
                r.Alumni)).ToList();
            var chapters = await db.Committees.AsNoTracking().Where(c => c.Kind == "chapter" && c.ArchivedAt == null).Select(c => c.NameEn).OrderBy(n => n).ToListAsync();
            return Results.Ok(new PublicPage(items, total, PageSize, chapters));
        })
        .WithName("SearchMemberDirectory")
        .WithSummary("Public member directory")
        .WithDescription("Only members who opted in appear. `chapter` filters to current members of a chapter committee (by its English name) who agreed to be named; `chapters` lists the chapters, each with only the fields they approved (name, photo, expertise). Contact details are never offered. 24 per page, page 1-50, `q` matches only published fields. Never cached; rate limited at the gateway.")
        .WithTags("Directory")
        .AllowAnonymous();

        app.MapGet("/api/v1/members/directory/{id}/photo", async (HttpContext context, string id, MembershipDbContext db, IAttachmentStore store) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var photo = await db.Members.AsNoTracking()
                .Where(m => m.Id == id && (m.Status == "active" || (m.Status == "alumni" && m.ConsentAlumni)) && m.ConsentPhotograph && m.PhotoKey != null && m.PhotoScanStatus == CleanScan && !db.DeletionRequests.Any(d => d.MemberId == m.Id))
                .Select(m => new { m.PhotoKey, m.PhotoContentType })
                .FirstOrDefaultAsync();
            var stream = photo is null ? null : await store.OpenReadAsync(photo.PhotoKey!, context.RequestAborted);
            // One answer for "no such member", "no consent" and "not yet scanned".
            return stream is null
                ? ApiError.Create(context, StatusCodes.Status404NotFound, "NOT_FOUND", "No photo.")
                : Results.Stream(stream, photo!.PhotoContentType);
        })
        .WithName("GetDirectoryPhoto").WithSummary("A consented, scanned member photo").WithTags("Directory")
        .AllowAnonymous();

        app.MapGet("/api/v1/admin/membership/members", async (MembershipDbContext db, string? q, string? status, int? limit, int? offset) =>
        {
            var query = db.Members.AsNoTracking().AsQueryable();
            if (!string.IsNullOrWhiteSpace(status))
            {
                query = query.Where(m => m.Status == status);
            }

            if (CleanQuery(q) is { } term)
            {
                query = query.Where(m => (m.FirstName + " " + m.LastName).ToLower().Contains(term) || m.MemberCode.ToLower().Contains(term)
                    || (m.NameBn != null && m.NameBn.Contains(term)) || (m.Expertise != null && m.Expertise.ToLower().Contains(term)));
            }

            var total = await query.CountAsync();
            var page = await query.OrderBy(m => m.MemberCode).Skip(Math.Max(0, offset ?? 0)).Take(Math.Clamp(limit ?? 50, 1, 100)).ToListAsync();
            return Results.Ok(new StaffPage([.. page.Select(ToStaff)], total));
        })
        .WithName("ListMembers")
        .WithSummary("Search members (staff)")
        .WithDescription("Name, member code and expertise search with status filter; paged (limit max 100). Contact details are not part of the list: open one member for those.")
        .WithTags("Membership admin")
        .RequirePermission(Permissions.MemberProfileView);

        app.MapGet("/api/v1/admin/membership/members/{id}", async (HttpContext context, string id, MembershipDbContext db, IFieldProtector protector) =>
        {
            var member = await db.Members.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id);
            if (member is null)
            {
                return ApiError.Create(context, StatusCodes.Status404NotFound, "NOT_FOUND", "Member not found.");
            }

            var email = await db.Applications.AsNoTracking().Where(a => a.Id == member.ApplicationId).Select(a => a.EmailProtected).FirstOrDefaultAsync();
            string Open(string? value) => value is null ? string.Empty : protector.Unprotect(value);
            return Results.Ok(new StaffMember(ToStaff(member), Open(email), Open(member.PhoneProtected), Open(member.AddressProtected), Open(member.DateOfBirthProtected), Open(member.BloodGroupProtected)));
        })
        .WithName("GetMember").WithSummary("One member with contact details (staff)").WithTags("Membership admin")
        .RequirePermission(Permissions.MemberProfileView);
    }

    private static StaffEntry ToStaff(MemberEntity m) => new(
        m.Id, m.MemberCode, $"{m.FirstName} {m.LastName}".Trim(), m.NameBn, m.Status, m.JoinedAt, m.Expertise,
        m.PhotoKey is not null, m.ConsentName, m.ConsentPhotograph, m.ConsentExpertise);
}
