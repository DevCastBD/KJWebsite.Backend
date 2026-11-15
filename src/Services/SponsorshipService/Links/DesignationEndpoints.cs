using System.Globalization;
using System.Text.RegularExpressions;
using KJWebsite.BuildingBlocks.Security;
using Microsoft.EntityFrameworkCore;
using SponsorshipService.Data;
using SponsorshipService.Students;

namespace SponsorshipService.Links;

public static partial class DesignationEndpoints
{
    public static void MapDesignation(this IEndpointRouteBuilder app)
    {
        app.MapGet($"{StudentEndpoints.Route}/donors/{{donorId}}/students/{{studentId}}/designation", DesignationAsync)
            .WithName("GetStudentDesignation").WithSummary("Check a student's donation designation")
            .WithDescription("Checks whether the donor's link was active on the required calendar date, returning only the preferred name when active.")
            .WithTags("Admin: Sponsorship").RequirePermission(Permissions.FinanceDonationRecord);
        app.MapGet($"{StudentEndpoints.Route}/donors/{{donorId}}/designatable-students", DesignatableAsync)
            .WithName("ListDesignatableStudents").WithSummary("List students eligible for donation designation")
            .WithDescription("Returns up to 200 students linked on the calendar date, defaulting to today in the foundation's time zone.")
            .WithTags("Admin: Sponsorship").RequirePermission(Permissions.FinanceDonationRecord);
        app.MapGet($"{StudentEndpoints.Route}/students/names", NamesAsync)
            .WithName("GetStudentPreferredNames").WithSummary("Look up student preferred names")
            .WithDescription("Returns only IDs and preferred names for up to 100 distinct student IDs, in request order, omitting unknown IDs.")
            .WithTags("Admin: Sponsorship").RequirePermission(Permissions.FinanceView);
    }

    private static IQueryable<LinkEntity> ActiveLinks(SponsorshipDbContext db, string donorId, DateOnly date) =>
        db.Links.AsNoTracking().Where(link => link.DonorId == donorId && link.StartDate <= date &&
            (link.EndType == null || (link.EndType == LinkEndTypes.Ended && link.EndDate != null && date <= link.EndDate)));

    private static async Task<IResult> DesignationAsync(HttpContext context, string donorId, string studentId, string? date, SponsorshipDbContext db)
    {
        if (!LinkEndpoints.Date(date, out var day)) return StudentEndpoints.Invalid(context, ["date"]);
        var match = await (from link in ActiveLinks(db, donorId, day)
                           join student in db.Students.AsNoTracking() on link.StudentId equals student.Id
                           where link.StudentId == studentId
                           select new { student.PreferredName }).FirstOrDefaultAsync(context.RequestAborted);
        return Results.Ok(new { donorId, studentId, date = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), active = match is not null, studentName = match?.PreferredName });
    }

    private static async Task<IResult> DesignatableAsync(HttpContext context, string donorId, string? date, SponsorshipDbContext db, FoundationDay today)
    {
        var asOf = today.Today;
        if (date is not null && !LinkEndpoints.Date(date, out asOf))
            return StudentEndpoints.Invalid(context, ["date"]);
        var rows = await (from link in ActiveLinks(db, donorId, asOf)
                          join student in db.Students.AsNoTracking() on link.StudentId equals student.Id
                          select new { StudentId = student.Id, student.PreferredName, link.StartDate, EndDate = link.EndType == null ? null : link.EndDate, link.Id })
            .ToListAsync(context.RequestAborted);
        var items = rows.GroupBy(row => row.StudentId)
            .Select(group => group.OrderByDescending(row => row.StartDate).ThenBy(row => row.Id, StringComparer.Ordinal).First())
            .OrderBy(row => row.PreferredName, StringComparer.OrdinalIgnoreCase).ThenBy(row => row.StudentId, StringComparer.Ordinal)
            .Take(200).Select(row => new { row.StudentId, row.PreferredName, row.StartDate, row.EndDate });
        return Results.Ok(new { items });
    }

    private static async Task<IResult> NamesAsync(HttpContext context, string? ids, SponsorshipDbContext db)
    {
        var wanted = (ids ?? string.Empty).Split(',').Select(id => id.Trim()).Distinct(StringComparer.Ordinal).ToArray();
        if (wanted.Length > 100 || wanted.Any(id => !StudentIdPattern().IsMatch(id))) return StudentEndpoints.Invalid(context, ["ids"]);
        var rows = await db.Students.AsNoTracking().Where(student => wanted.Contains(student.Id))
            .Select(student => new { student.Id, student.PreferredName }).ToDictionaryAsync(student => student.Id, context.RequestAborted);
        return Results.Ok(new { items = wanted.Where(rows.ContainsKey).Select(id => rows[id]) });
    }

    [GeneratedRegex(@"\Astu_[0-9a-f]{32}\z", RegexOptions.CultureInvariant)]
    private static partial Regex StudentIdPattern();
}
