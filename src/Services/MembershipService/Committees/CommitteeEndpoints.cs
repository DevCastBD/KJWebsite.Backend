using System.Text.RegularExpressions;
using KJWebsite.BuildingBlocks;
using KJWebsite.BuildingBlocks.Security;
using MembershipService.Committees;
using MembershipService.Data;
using MembershipService.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace MembershipService.Committees;

/// <summary>Back-office committee and term management (`membership.manage`).</summary>
public static partial class CommitteeEndpoints
{
    private const string Route = "/api/v1/admin/membership/committees";
    private const string Tag = "Membership admin";
    private const int MaxDepth = 6;

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex Slug();

    public static DateOnly Today(TimeProvider clock) => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

    public static void MapCommitteeAdmin(this IEndpointRouteBuilder app)
    {
        app.MapGet(Route, async (MembershipDbContext db, TimeProvider clock, bool? includeArchived) =>
        {
            var today = Today(clock);
            var committees = await db.Committees.AsNoTracking().ToListAsync();
            var seats = await db.CommitteeMembers.AsNoTracking().ToListAsync();
            var members = await db.Members.AsNoTracking().ToDictionaryAsync(m => m.Id);
            var items = committees.Where(c => includeArchived == true || c.ArchivedAt is null).OrderBy(c => c.NameEn).Select(c => View(c, seats, members, today));
            return Results.Ok(new { items });
        })
        .WithName("ListCommittees").WithSummary("All committees, flat (the client nests them by parentId); archived ones with includeArchived=true").WithTags(Tag)
        .RequirePermission(Permissions.MembershipManage);

        app.MapPost(Route, async (HttpContext context, CommitteeRequest payload, MembershipDbContext db, TimeProvider clock) =>
        {
            var committee = new CommitteeEntity { Id = $"com_{Guid.NewGuid():N}", CreatedAt = clock.GetUtcNow() };
            if (await Apply(context, committee, payload, db) is { } failure)
            {
                return failure;
            }

            db.Committees.Add(committee);
            await db.SaveChangesAsync();
            return Results.Created($"{Route}/{committee.Id}", await Single(db, committee, clock));
        })
        .WithName("CreateCommittee").WithSummary("Create a committee, chapter or project group").WithTags(Tag)
        .RequirePermission(Permissions.MembershipManage);

        app.MapPut($"{Route}/{{id}}", async (HttpContext context, string id, CommitteeRequest payload, MembershipDbContext db, TimeProvider clock) =>
        {
            var committee = await db.Committees.FirstOrDefaultAsync(c => c.Id == id);
            if (committee is null)
            {
                return ApiError.Create(context, StatusCodes.Status404NotFound, "NOT_FOUND", "Committee not found.");
            }

            if (await Apply(context, committee, payload, db) is { } failure)
            {
                return failure;
            }

            await db.SaveChangesAsync();
            return Results.Ok(await Single(db, committee, clock));
        })
        .WithName("UpdateCommittee").WithSummary("Rename, re-parent or change the kind of a committee").WithTags(Tag)
        .RequirePermission(Permissions.MembershipManage);

        app.MapPost($"{Route}/{{id}}/archive", async (HttpContext context, string id, MembershipDbContext db, TimeProvider clock) =>
            await SetArchived(context, id, db, clock, archive: true))
        .WithName("ArchiveCommittee").WithSummary("Retire a committee: it leaves the public chart, its history stays").WithTags(Tag)
        .RequirePermission(Permissions.MembershipManage);

        app.MapPost($"{Route}/{{id}}/unarchive", async (HttpContext context, string id, MembershipDbContext db, TimeProvider clock) =>
            await SetArchived(context, id, db, clock, archive: false))
        .WithName("UnarchiveCommittee").WithSummary("Bring an archived committee back").WithTags(Tag)
        .RequirePermission(Permissions.MembershipManage);

        app.MapDelete($"{Route}/{{id}}", async (HttpContext context, string id, MembershipDbContext db) =>
        {
            var committee = await db.Committees.FirstOrDefaultAsync(c => c.Id == id);
            if (committee is null)
            {
                return ApiError.Create(context, StatusCodes.Status404NotFound, "NOT_FOUND", "Committee not found.");
            }

            // History is worth keeping: only a committee that never had children or terms can be removed; the rest are archived.
            if (await db.Committees.AnyAsync(c => c.ParentId == id) || await db.CommitteeMembers.AnyAsync(t => t.CommitteeId == id))
            {
                return ApiError.Create(context, StatusCodes.Status409Conflict, "HAS_HISTORY", "This committee has sub-committees or past terms; archive it instead.");
            }

            db.Committees.Remove(committee);
            await db.SaveChangesAsync();
            return Results.NoContent();
        })
        .WithName("DeleteCommittee").WithSummary("Delete a committee that never had members or children").WithTags(Tag)
        .RequirePermission(Permissions.MembershipManage);

        app.MapGet($"{Route}/{{id}}/members", async (HttpContext context, string id, MembershipDbContext db, TimeProvider clock, string? scope) =>
        {
            if (!await db.Committees.AnyAsync(c => c.Id == id))
            {
                return ApiError.Create(context, StatusCodes.Status404NotFound, "NOT_FOUND", "Committee not found.");
            }

            var today = Today(clock);
            var terms = await db.CommitteeMembers.AsNoTracking().Where(t => t.CommitteeId == id).ToListAsync();
            terms = scope switch
            {
                "current" => [.. terms.Where(t => t.IsCurrent(today))],
                "past" => [.. terms.Where(t => t.EndsOn < today)],
                _ => terms
            };
            var ids = terms.Select(t => t.MemberId).Distinct().ToList();
            var members = await db.Members.AsNoTracking().Where(m => ids.Contains(m.Id)).ToDictionaryAsync(m => m.Id);
            return Results.Ok(new { items = terms.OrderByDescending(t => t.StartsOn).ThenBy(t => t.PositionKey).Select(t => ToTerm(t, members, today)) });
        })
        .WithName("ListCommitteeTerms").WithSummary("A committee's terms: scope=current|past|all (default all)").WithTags(Tag)
        .RequirePermission(Permissions.MembershipManage);

        app.MapPost($"{Route}/{{id}}/members", async (HttpContext context, string id, TermRequest payload, MembershipDbContext db, TimeProvider clock) =>
        {
            if (!await db.Committees.AnyAsync(c => c.Id == id))
            {
                return ApiError.Create(context, StatusCodes.Status404NotFound, "NOT_FOUND", "Committee not found.");
            }

            var term = new CommitteeMemberEntity { Id = $"trm_{Guid.NewGuid():N}", CommitteeId = id, CreatedAt = clock.GetUtcNow() };
            if (await ApplyTerm(context, term, payload, db) is { } failure)
            {
                return failure;
            }

            db.CommitteeMembers.Add(term);
            await db.SaveChangesAsync();
            return Results.Created($"{Route}/{id}/members", await TermFor(db, term, clock));
        })
        .WithName("AddCommitteeTerm").WithSummary("Seat a member in a position for a term").WithTags(Tag)
        .RequirePermission(Permissions.MembershipManage);

        app.MapPut($"{Route}/{{id}}/members/{{termId}}", async (HttpContext context, string id, string termId, TermRequest payload, MembershipDbContext db, TimeProvider clock) =>
        {
            var term = await db.CommitteeMembers.FirstOrDefaultAsync(t => t.Id == termId && t.CommitteeId == id);
            if (term is null)
            {
                return ApiError.Create(context, StatusCodes.Status404NotFound, "NOT_FOUND", "Term not found.");
            }

            if (await ApplyTerm(context, term, payload, db) is { } failure)
            {
                return failure;
            }

            await db.SaveChangesAsync();
            return Results.Ok(await TermFor(db, term, clock));
        })
        .WithName("UpdateCommitteeTerm").WithSummary("Correct a term's position or dates").WithTags(Tag)
        .RequirePermission(Permissions.MembershipManage);

        app.MapPost($"{Route}/{{id}}/members/{{termId}}/end", async (HttpContext context, string id, string termId, EndTermRequest payload, MembershipDbContext db, TimeProvider clock) =>
        {
            var term = await db.CommitteeMembers.FirstOrDefaultAsync(t => t.Id == termId && t.CommitteeId == id);
            if (term is null)
            {
                return ApiError.Create(context, StatusCodes.Status404NotFound, "NOT_FOUND", "Term not found.");
            }

            var today = Today(clock);
            var ends = payload.EndsOn ?? today;
            if (term.EndsOn is not null && term.EndsOn < today)
            {
                return ApiError.Create(context, StatusCodes.Status409Conflict, "ALREADY_ENDED", "This term has already ended.");
            }

            if (ends < term.StartsOn)
            {
                return ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", "Invalid fields: endsOn.");
            }

            term.EndsOn = ends;
            await db.SaveChangesAsync();
            return Results.Ok(await TermFor(db, term, clock));
        })
        .WithName("EndCommitteeTerm").WithSummary("End a term (default today); the term stays as history").WithTags(Tag)
        .RequirePermission(Permissions.MembershipManage);
    }

    private static async Task<IResult> SetArchived(HttpContext context, string id, MembershipDbContext db, TimeProvider clock, bool archive)
    {
        var committee = await db.Committees.FirstOrDefaultAsync(c => c.Id == id);
        if (committee is null)
        {
            return ApiError.Create(context, StatusCodes.Status404NotFound, "NOT_FOUND", "Committee not found.");
        }

        committee.ArchivedAt = archive ? committee.ArchivedAt ?? clock.GetUtcNow() : null;
        await db.SaveChangesAsync();
        return Results.Ok(await Single(db, committee, clock));
    }

    private static async Task<IResult?> Apply(HttpContext context, CommitteeEntity committee, CommitteeRequest r, MembershipDbContext db)
    {
        var invalid = new List<string>();
        var nameEn = (r.NameEn ?? r.Name)?.Trim();
        var nameBn = r.NameBn?.Trim();
        if (string.IsNullOrEmpty(nameEn) || nameEn.Length > 200)
        {
            invalid.Add("nameEn");
        }

        if (nameBn is { Length: > 200 })
        {
            invalid.Add("nameBn");
        }

        var kind = (r.Kind ?? committee.Kind).Trim().ToLowerInvariant();
        if (!CommitteeKinds.All.Contains(kind))
        {
            invalid.Add("kind");
        }

        var slug = r.Slug?.Trim() ?? (committee.Slug.Length > 0 ? committee.Slug : Slugify(nameEn));
        if (slug.Length is 0 or > 96 || !Slug().IsMatch(slug))
        {
            invalid.Add("slug");
        }

        if (invalid.Count > 0)
        {
            return ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", $"Invalid fields: {string.Join(", ", invalid)}.");
        }

        if (await db.Committees.AnyAsync(c => c.Slug == slug && c.Id != committee.Id))
        {
            return ApiError.Create(context, StatusCodes.Status409Conflict, "SLUG_TAKEN", "A committee with this slug already exists.");
        }

        var parentId = string.IsNullOrWhiteSpace(r.ParentId) ? null : r.ParentId.Trim();
        if (parentId is not null && await ParentProblem(db, committee.Id, parentId) is { } problem)
        {
            return ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", $"Invalid fields: parentId ({problem}).");
        }

        committee.Slug = slug;
        committee.Kind = kind;
        committee.NameEn = nameEn!;
        committee.NameBn = string.IsNullOrEmpty(nameBn) ? nameEn! : nameBn;
        committee.NameBnIsFallback = string.IsNullOrEmpty(nameBn);
        committee.ParentId = parentId;
        return null;
    }

    /// <summary>The parent must exist, must not be the committee or one of its descendants (a cycle), and the chart stays shallow.</summary>
    private static async Task<string?> ParentProblem(MembershipDbContext db, string committeeId, string parentId)
    {
        var all = await db.Committees.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.ParentId);
        if (!all.ContainsKey(parentId))
        {
            return "unknown";
        }

        var depth = 1;
        for (var cursor = parentId; cursor is not null; cursor = all.GetValueOrDefault(cursor), depth++)
        {
            if (cursor == committeeId)
            {
                return "cycle";
            }

            if (depth > MaxDepth)
            {
                return "too deep";
            }
        }

        return null;
    }

    private static async Task<IResult?> ApplyTerm(HttpContext context, CommitteeMemberEntity term, TermRequest r, MembershipDbContext db)
    {
        var invalid = new List<string>();
        var key = r.PositionKey?.Trim().ToLowerInvariant() ?? string.Empty;
        if (key.Length is 0 or > 48 || !Slug().IsMatch(key))
        {
            invalid.Add("positionKey");
        }

        var positionEn = (r.PositionEn ?? string.Empty).Trim();
        if (positionEn.Length is 0 or > 100)
        {
            invalid.Add("positionEn");
        }

        var positionBn = (r.PositionBn ?? string.Empty).Trim();
        if (positionBn.Length > 100)
        {
            invalid.Add("positionBn");
        }

        if (r.StartsOn is null)
        {
            invalid.Add("startsOn");
        }

        if (r.StartsOn is { } start && r.EndsOn is { } end && end < start)
        {
            invalid.Add("endsOn");
        }

        if (string.IsNullOrWhiteSpace(r.MemberId) || !await db.Members.AnyAsync(m => m.Id == r.MemberId))
        {
            invalid.Add("memberId");
        }

        if (invalid.Count > 0)
        {
            return ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", $"Invalid fields: {string.Join(", ", invalid)}.");
        }

        // The same person cannot hold the same position twice at once: overlapping terms would make "current" ambiguous.
        var starts = r.StartsOn!.Value;
        var ends = r.EndsOn;
        var others = await db.CommitteeMembers.AsNoTracking()
            .Where(t => t.CommitteeId == term.CommitteeId && t.MemberId == r.MemberId && t.PositionKey == key && t.Id != term.Id).ToListAsync();
        if (others.Any(o => o.StartsOn <= (ends ?? DateOnly.MaxValue) && (o.EndsOn ?? DateOnly.MaxValue) >= starts))
        {
            return ApiError.Create(context, StatusCodes.Status409Conflict, "TERM_OVERLAP", "This member already holds this position in that period.");
        }

        term.MemberId = r.MemberId!;
        term.PositionKey = key;
        term.PositionEn = positionEn;
        term.PositionBn = positionBn.Length == 0 ? positionEn : positionBn;
        term.StartsOn = starts;
        term.EndsOn = ends;
        return null;
    }

    public static string Slugify(string? name)
    {
        var chars = (name ?? string.Empty).ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray();
        var slug = Regex.Replace(new string(chars), "-+", "-").Trim('-');
        return slug.Length == 0 ? $"committee-{Guid.NewGuid().ToString("N")[..6]}" : slug;
    }

    private static CommitteeView View(CommitteeEntity c, List<CommitteeMemberEntity> seats, Dictionary<string, MemberEntity> members, DateOnly today)
    {
        var current = seats.Where(t => t.CommitteeId == c.Id && t.IsCurrent(today)).ToList();
        var chair = current.Where(t => t.PositionKey == "chair").OrderBy(t => t.StartsOn).Select(t => members.GetValueOrDefault(t.MemberId)).FirstOrDefault(m => m is not null);
        return new CommitteeView(c.Id, c.Slug, c.NameEn, c.NameEn, c.NameBn, c.NameBnIsFallback, c.Kind, c.ParentId, chair is null ? string.Empty : $"{chair.FirstName} {chair.LastName}", c.ArchivedAt is not null, current.Count);
    }

    private static async Task<CommitteeView> Single(MembershipDbContext db, CommitteeEntity c, TimeProvider clock) =>
        View(c, await db.CommitteeMembers.AsNoTracking().Where(t => t.CommitteeId == c.Id).ToListAsync(), await db.Members.AsNoTracking().ToDictionaryAsync(m => m.Id), Today(clock));

    private static TermView ToTerm(CommitteeMemberEntity t, Dictionary<string, MemberEntity> members, DateOnly today)
    {
        var m = members.GetValueOrDefault(t.MemberId);
        return new TermView(t.Id, t.CommitteeId, t.MemberId, m?.MemberCode ?? string.Empty, m is null ? string.Empty : $"{m.FirstName} {m.LastName}", t.PositionKey, t.PositionEn, t.PositionBn, t.StartsOn, t.EndsOn, t.IsCurrent(today));
    }

    private static async Task<TermView> TermFor(MembershipDbContext db, CommitteeMemberEntity t, TimeProvider clock) =>
        ToTerm(t, await db.Members.AsNoTracking().Where(m => m.Id == t.MemberId).ToDictionaryAsync(m => m.Id), Today(clock));
}
