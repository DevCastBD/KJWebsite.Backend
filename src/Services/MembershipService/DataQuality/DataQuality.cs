using System.Security.Claims;
using KJWebsite.BuildingBlocks;
using KJWebsite.BuildingBlocks.DataQuality;
using KJWebsite.BuildingBlocks.Security;
using MembershipService.Data;
using MembershipService.Data.Entities;
using MembershipService.Intake;
using Microsoft.EntityFrameworkCore;

namespace MembershipService.DataQuality;

/// <summary>A person's ruling on a pair of member records. It changes no member; it is the audited decision a merge would act on.</summary>
public sealed class MemberDuplicateDecisionEntity
{
    public string Id { get; set; } = string.Empty;
    public string PairKey { get; set; } = string.Empty;
    public string Decision { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;
    public string? DecidedBy { get; set; }
    public DateTimeOffset DecidedAt { get; set; }
}

public sealed record CompletenessView(DateTimeOffset AsOf, IReadOnlyList<EntityCompleteness> Entities);

public sealed record DuplicateListView(IReadOnlyList<DuplicateCandidate> Items, int Total);

public sealed record DuplicateDecisionView(string Id, string PairKey, string Decision, string Note, string? DecidedBy, DateTimeOffset DecidedAt);

/// <summary>Where member and beneficiary records are thin, and which member records may be one person. Reads only; it never edits or merges a record.</summary>
public sealed class MemberDataQuality(MembershipDbContext db, IFieldProtector protector, TimeProvider clock)
{
    public async Task<CompletenessView> CompletenessAsync(CancellationToken ct)
    {
        var members = await db.Members.AsNoTracking().Where(m => m.Status != "terminated").ToListAsync(ct);
        var beneficiaries = await db.Beneficiaries.AsNoTracking().Where(b => !b.IsArchived).ToListAsync(ct);
        var withMou = (await db.Mous.AsNoTracking().Select(m => m.BeneficiaryId).ToListAsync(ct)).ToHashSet();
        var withEnrolment = (await db.ProgrammeEnrolments.AsNoTracking().Select(e => e.BeneficiaryId).ToListAsync(ct)).ToHashSet();

        return new CompletenessView(clock.GetUtcNow(),
        [
            Completeness.Build("member", members, m => m.Id,
                ("nameBn", m => !string.IsNullOrWhiteSpace(m.NameBn)),
                ("expertise", m => !string.IsNullOrWhiteSpace(m.Expertise)),
                ("phone", m => !string.IsNullOrEmpty(m.PhoneProtected)),
                ("address", m => !string.IsNullOrEmpty(m.AddressProtected)),
                ("dateOfBirth", m => !string.IsNullOrEmpty(m.DateOfBirthProtected)),
                ("bloodGroup", m => !string.IsNullOrEmpty(m.BloodGroupProtected)),
                ("photo", m => !string.IsNullOrEmpty(m.PhotoKey))),
            Completeness.Build("beneficiary", beneficiaries, b => b.Id,
                ("nameBn", b => !string.IsNullOrWhiteSpace(b.NameBn)),
                ("address", b => !string.IsNullOrEmpty(b.AddressProtected)),
                ("district", b => !string.IsNullOrWhiteSpace(b.District)),
                ("mou", b => withMou.Contains(b.Id)),
                ("enrolment", b => withEnrolment.Contains(b.Id)))
        ]);
    }

    public async Task<IReadOnlyList<DuplicateCandidate>> DuplicatesAsync(CancellationToken ct)
    {
        var members = await db.Members.AsNoTracking().Where(m => m.Status != "terminated").Select(m => new { m.Id, m.FirstName, m.LastName, m.PhoneProtected }).ToListAsync(ct);
        var decided = (await db.MemberDuplicateDecisions.AsNoTracking().Select(d => d.PairKey).ToListAsync(ct)).ToHashSet();
        var people = members.Select(m => new PersonRecord(m.Id, $"{m.FirstName} {m.LastName}", null, Phone(m.PhoneProtected))).ToList();
        return [.. DuplicateDetector.Detect(people).Where(c => !decided.Contains(c.PairKey))];
    }

    private string? Phone(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        try
        {
            return protector.Unprotect(value);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }
}

public static class MemberDataQualityEndpoints
{
    private const string Route = "/api/v1/admin/membership/data-quality";

    public static void MapMemberDataQuality(this IEndpointRouteBuilder app)
    {
        app.MapGet($"{Route}/completeness", async (MemberDataQuality quality, CancellationToken ct) => Results.Ok(await quality.CompletenessAsync(ct)))
            .WithName("MemberCompletenessReport").WithSummary("How complete member and beneficiary records are, by entity and field")
            .WithDescription("Per entity and field: how many records have it filled, how many do not, and up to 10 ids of the ones that do not. Members (not terminated): `nameBn`, `expertise`, `phone`, `address`, `dateOfBirth`, `bloodGroup`, `photo`. Beneficiaries (not archived): `nameBn`, `address`, `district`, `mou`, `enrolment`. Ids only, no personal data. Requires `membership.manage`.")
            .WithTags("Data quality").Produces<CompletenessView>().RequirePermission(Permissions.MembershipManage);

        app.MapGet($"{Route}/duplicates/members", async (MemberDataQuality quality, CancellationToken ct) =>
        {
            var items = await quality.DuplicatesAsync(ct);
            return Results.Ok(new DuplicateListView([.. items.Take(200)], items.Count));
        })
        .WithName("ListMemberDuplicateCandidates").WithSummary("Member records that may be the same person")
        .WithDescription("Each pair has `confidence` (`high` for a shared phone, `low` for the same full name only) and `evidence` kinds, never the values. Pairs already ruled on are left out. Nothing is merged. Requires `membership.manage`.")
        .WithTags("Data quality").Produces<DuplicateListView>().RequirePermission(Permissions.MembershipManage);

        app.MapPost($"{Route}/duplicates/members/decisions", async (HttpContext context, MembershipDbContext db, TimeProvider clock, DuplicateDecisionRequest payload, CancellationToken ct) =>
        {
            if (DuplicateDecisions.Validate(payload) is { } problem)
            {
                return ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", problem);
            }

            var ids = new[] { payload.LeftId!, payload.RightId! };
            if (await db.Members.CountAsync(m => ids.Contains(m.Id), ct) != 2)
            {
                return ApiError.Create(context, StatusCodes.Status404NotFound, "NOT_FOUND", "Both members must exist.");
            }

            var key = DuplicateDetector.PairKey(payload.LeftId!, payload.RightId!);
            if (await db.MemberDuplicateDecisions.AnyAsync(d => d.PairKey == key, ct))
            {
                return ApiError.Create(context, StatusCodes.Status409Conflict, "ALREADY_DECIDED", "This pair has already been ruled on.");
            }

            var decision = new MemberDuplicateDecisionEntity { Id = $"dup_{Guid.NewGuid():N}", PairKey = key, Decision = payload.Decision!, Note = payload.Note!.Trim(), DecidedBy = context.User.FindFirstValue(KjClaims.Subject), DecidedAt = clock.GetUtcNow() };
            db.MemberDuplicateDecisions.Add(decision);
            await db.SaveChangesAsync(ct);
            return Results.Created($"{Route}/duplicates/members", new DuplicateDecisionView(decision.Id, decision.PairKey, decision.Decision, decision.Note, decision.DecidedBy, decision.DecidedAt));
        })
        .WithName("DecideMemberDuplicate").WithSummary("Rule on a possible duplicate: not the same member, or confirmed the same")
        .WithDescription("`{leftId, rightId, decision: not_duplicate|confirmed_duplicate, note}`. The note is required and the decision is audited with who made it. It changes no member record: combining two members is a separate, deliberate step that acts on a confirmed pair. Requires `membership.manage`.")
        .WithTags("Data quality").Produces<DuplicateDecisionView>(StatusCodes.Status201Created).RequirePermission(Permissions.MembershipManage);
    }
}
