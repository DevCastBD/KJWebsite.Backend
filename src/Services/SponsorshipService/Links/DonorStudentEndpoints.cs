using KJWebsite.BuildingBlocks;
using KJWebsite.BuildingBlocks.Security;
using Microsoft.EntityFrameworkCore;
using SponsorshipService.Data;
using SponsorshipService.Students;

namespace SponsorshipService.Links;

public sealed record DonorLinkedStudent(string Id, string PreferredName, string Class, string School, ProgrammeRef Programme, string District, string Status, bool HasPhoto, string? Story, string? StoryLanguage, DateOnly Since, string Sponsorship, DateOnly? EndedOn);

public static class DonorStudentEndpoints
{
    public const string Route = "/api/v1/donors/me/students";
    public static void MapDonorStudents(this IEndpointRouteBuilder app)
    {
        app.MapGet(Route, async (HttpContext context, SponsorshipDbContext db, IFinanceDonors finance, IProjectCatalogue catalogue, FoundationDay clock) =>
        {
            var me = await finance.MeAsync(LinkEndpoints.Bearer(context), context.RequestAborted);
            if (Refusal(context, me) is { } refusal) return refusal;
            var invalid = new List<string>();
            var take = 20;
            var skip = 0;
            if (context.Request.Query.TryGetValue("limit", out var limit) && (!int.TryParse(limit, out take) || take is < 1 or > 100)) invalid.Add("limit");
            if (context.Request.Query.TryGetValue("offset", out var offset) && (!int.TryParse(offset, out skip) || skip < 0)) invalid.Add("offset");
            if (invalid.Count > 0) return StudentEndpoints.Invalid(context, invalid);
            var links = await SelectedAsync(db, me.Value!.Id, null, clock, context.RequestAborted);
            var page = links.Skip(skip).Take(take).ToList();
            var items = await ProjectAsync(db, catalogue, page, me.Value.Language, context.RequestAborted);
            return Results.Ok(new PagingEnvelope<DonorLinkedStudent>(items, links.Count, null, take, skip));
        }).WithName("ListMyStudents").WithTags("Donor: Students").RequireDonorSelf();

        app.MapGet($"{Route}/names", NamesAsync)
            .WithName("GetMyStudentPreferredNames").WithTags("Donor: Students")
            .WithDescription("Returns preferred names for up to 100 visible students, ordered by ID.").RequireDonorSelf();

        app.MapGet($"{Route}/{{studentId}}", async (HttpContext context, string studentId, SponsorshipDbContext db, IFinanceDonors finance, IProjectCatalogue catalogue, FoundationDay clock) =>
        {
            var me = await finance.MeAsync(LinkEndpoints.Bearer(context), context.RequestAborted);
            if (Refusal(context, me) is { } refusal) return refusal;
            var links = await SelectedAsync(db, me.Value!.Id, studentId, clock, context.RequestAborted);
            var items = await ProjectAsync(db, catalogue, links, me.Value.Language, context.RequestAborted);
            return items.Count == 0 ? NotFound(context) : Results.Ok(items[0]);
        }).WithName("GetMyStudent").WithTags("Donor: Students").RequireDonorSelf();

        app.MapGet($"{Route}/{{studentId}}/photo", async (HttpContext context, string studentId, SponsorshipDbContext db, IFinanceDonors finance, IStudentFileStore store, FoundationDay clock) =>
        {
            var me = await finance.MeAsync(LinkEndpoints.Bearer(context), context.RequestAborted);
            if (Refusal(context, me) is { } refusal) return refusal;
            var link = (await SelectedAsync(db, me.Value!.Id, studentId, clock, context.RequestAborted)).FirstOrDefault();
            if (link is null) return NotFound(context);
            string? key;
            string? type;
            if (link.EndType == LinkEndTypes.Ended)
            {
                var copy = await db.FrozenCopies.AsNoTracking().FirstOrDefaultAsync(f => f.LinkId == link.Id, context.RequestAborted);
                key = copy?.PhotoKey;
                type = copy?.PhotoContentType;
            }
            else
            {
                var student = await db.Students.AsNoTracking().FirstOrDefaultAsync(s => s.Id == studentId, context.RequestAborted);
                var consents = await StudentEndpoints.CurrentConsentsAsync(db, [studentId], context.RequestAborted);
                key = consents.GetValueOrDefault(studentId)?.Granted == true ? student?.PhotoKey : null;
                type = student?.PhotoContentType;
            }
            var stream = key is null ? null : await store.OpenReadAsync(key, context.RequestAborted);
            return stream is null ? NotFound(context) : Results.Stream(stream, type ?? "application/octet-stream");
        }).WithName("GetMyStudentPhoto").WithTags("Donor: Students").RequireDonorSelf();

        string[] writes = ["POST", "PUT", "PATCH", "DELETE"];
        app.MapMethods(Route, writes, ReadOnly).AllowAnonymous().ExcludeFromDescription();
        app.MapMethods($"{Route}/{{**rest}}", writes, ReadOnly).AllowAnonymous().ExcludeFromDescription();
    }

    private static async Task<IResult> NamesAsync(HttpContext context, string? ids, SponsorshipDbContext db, IFinanceDonors finance, FoundationDay clock)
    {
        var ct = context.RequestAborted;
        var me = await finance.MeAsync(LinkEndpoints.Bearer(context), ct);
        if (Refusal(context, me) is { } refusal) return refusal;
        var wanted = (ids ?? string.Empty).Split(',').Select(id => id.Trim()).Distinct(StringComparer.Ordinal).ToArray();
        if (wanted.Length > 100 || wanted.Any(id => !DesignationEndpoints.StudentIdPattern().IsMatch(id))) return StudentEndpoints.Invalid(context, ["ids"]);

        var links = (await SelectedAsync(db, me.Value!.Id, null, clock, ct)).Where(l => wanted.Contains(l.StudentId)).ToList();
        var liveIds = links.Where(l => l.EndType is null).Select(l => l.StudentId).ToArray();
        var live = await db.Students.AsNoTracking().Where(s => liveIds.Contains(s.Id))
            .Select(s => new { s.Id, s.PreferredName }).ToDictionaryAsync(s => s.Id, s => s.PreferredName, ct);
        var endedIds = links.Where(l => l.EndType == LinkEndTypes.Ended).Select(l => l.Id).ToArray();
        var frozen = await db.FrozenCopies.AsNoTracking().Where(f => endedIds.Contains(f.LinkId))
            .Select(f => new { f.LinkId, f.PreferredName }).ToDictionaryAsync(f => f.LinkId, f => f.PreferredName, ct);
        var items = new List<object>();
        foreach (var link in links.OrderBy(l => l.StudentId, StringComparer.Ordinal))
        {
            var names = link.EndType is null ? live : frozen;
            if (names.TryGetValue(link.EndType is null ? link.StudentId : link.Id, out var name))
                items.Add(new { id = link.StudentId, preferredName = name });
        }
        return Results.Ok(new { items });
    }

    private static async Task<List<LinkEntity>> SelectedAsync(SponsorshipDbContext db, string donorId, string? studentId, FoundationDay clock, CancellationToken ct)
    {
        var today = clock.Today;

        // An `ended` link with no end date was cancelled before it started: nothing was ever shown, so it has no frozen copy and is never listed.
        var links = await db.Links.AsNoTracking().Where(l => l.DonorId == donorId && (studentId == null || l.StudentId == studentId) &&
            ((l.EndType == null && l.StartDate <= today) || (l.EndType == LinkEndTypes.Ended && l.EndDate != null))).ToListAsync(ct);
        return links.GroupBy(l => l.StudentId).Select(g => g.OrderBy(l => l.EndType is null ? 0 : 1).ThenByDescending(l => l.EndDate).ThenByDescending(l => l.EndedAt).ThenBy(l => l.Id, StringComparer.Ordinal).First())
            .OrderByDescending(l => l.StartDate).ThenBy(l => l.StudentId, StringComparer.Ordinal).ToList();
    }

    private static async Task<List<DonorLinkedStudent>> ProjectAsync(SponsorshipDbContext db, IProjectCatalogue catalogue, List<LinkEntity> links, string language, CancellationToken ct)
    {
        var ids = links.Where(l => l.EndType is null).Select(l => l.StudentId).ToList();
        var students = await db.Students.AsNoTracking().Where(s => ids.Contains(s.Id)).ToDictionaryAsync(s => s.Id, ct);
        var consents = await StudentEndpoints.CurrentConsentsAsync(db, ids, ct);
        var linkIds = links.Where(l => l.EndType == LinkEndTypes.Ended).Select(l => l.Id).ToList();
        var copies = await db.FrozenCopies.AsNoTracking().Where(f => linkIds.Contains(f.LinkId)).ToDictionaryAsync(f => f.LinkId, ct);
        var programmes = ids.Count == 0 ? [] : await catalogue.ListAsync(language == "bn" ? "bn" : "en", ct);
        var names = programmes?.ToDictionary(p => p.Id, p => p.Name) ?? [];
        var items = new List<DonorLinkedStudent>();
        foreach (var link in links)
        {
            DonorStudentView view;
            if (link.EndType is null)
            {
                if (!students.TryGetValue(link.StudentId, out var student)) continue;
                view = DonorProjection.Of(student, consents.GetValueOrDefault(student.Id)?.Granted == true, new ProgrammeRef(student.ProgrammeId, names.GetValueOrDefault(student.ProgrammeId, student.ProgrammeId)), language);
            }
            else
            {
                if (!copies.TryGetValue(link.Id, out var copy)) continue;
                var frozen = new StudentEntity { Id = copy.StudentId, PreferredName = copy.PreferredName, ClassName = copy.Class, School = copy.School, ProgrammeId = copy.ProgrammeId, District = copy.District, Status = copy.Status, StoryEn = copy.StoryEn, StoryBn = copy.StoryBn, PhotoKey = copy.PhotoKey };
                view = DonorProjection.Of(frozen, true, new ProgrammeRef(copy.ProgrammeId, language == "bn" ? copy.ProgrammeNameBn : copy.ProgrammeNameEn), language);
            }
            items.Add(new(view.Id, view.PreferredName, view.Class, view.School, view.Programme, view.District, view.Status, view.HasPhoto, view.Story, view.StoryLanguage, link.StartDate, link.EndType is null ? "active" : "ended", link.EndDate));
        }
        return items;
    }
    private static IResult? Refusal(HttpContext context, FinanceResult<FinanceMe> result) => result.Outcome switch
    {
        FinanceOutcome.Ok when result.Value is { Id.Length: > 0 } => null,
        FinanceOutcome.NotFound => ApiError.Create(context, 404, result.Code ?? "NOT_A_DONOR", result.Message ?? "The signed-in account has no donor record."),
        FinanceOutcome.Forbidden => ApiError.Create(context, 403, result.Code ?? "FORBIDDEN", result.Message ?? "Donor access refused."),
        _ => LinkEndpoints.Unavailable(context)
    };
    private static IResult NotFound(HttpContext context) => StudentEndpoints.NotFound(context);
    private static IResult ReadOnly(HttpContext context)
    {
        context.Response.Headers.Allow = "GET";
        return ApiError.Create(context, 405, "METHOD_NOT_ALLOWED", "Donor self-service is read-only.");
    }
}
