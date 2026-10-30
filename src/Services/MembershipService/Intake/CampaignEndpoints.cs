using System.Text.RegularExpressions;
using KJWebsite.BuildingBlocks;
using KJWebsite.BuildingBlocks.Security;
using MembershipService.Contracts;
using MembershipService.Data;
using MembershipService.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace MembershipService.Intake;

public static partial class CampaignEndpoints
{
    private const string AdminRoute = "/api/v1/admin/membership/campaigns";

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex SlugPattern();

    /// <summary>Campaigns are few, so they are evaluated in memory (SQLite cannot compare DateTimeOffset in SQL).</summary>
    public static async Task<IntakeDecision> DecideIntakeAsync(MembershipDbContext db, DateTimeOffset now)
    {
        var campaigns = await db.Campaigns.AsNoTracking().ToListAsync();
        if (campaigns.Count == 0)
        {
            return new IntakeDecision("unrestricted", null);
        }

        var open = campaigns.Where(c => c.IsOpen(now)).OrderByDescending(c => c.OpensAt).FirstOrDefault();
        return new IntakeDecision(open is null ? "closed" : "open", open);
    }

    public static void MapCampaignEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/membership/campaigns/current", async (MembershipDbContext db, TimeProvider clock) =>
        {
            var decision = await DecideIntakeAsync(db, clock.GetUtcNow());
            return Results.Ok(new IntakeState(decision.Intake, decision.Campaign is { } c ? ToPublic(c) : null));
        })
        .WithName("GetCurrentCampaign")
        .WithSummary("Is recruitment open, and which season")
        .WithDescription("`intake` is `open` (inside a season), `closed` (seasons exist but none is open; applications get 409 INTAKE_CLOSED) or `unrestricted` (no season is defined).")
        .WithTags("Membership")
        .AllowAnonymous();

        app.MapGet(AdminRoute, async (MembershipDbContext db, TimeProvider clock) =>
        {
            var now = clock.GetUtcNow();
            var campaigns = await db.Campaigns.AsNoTracking().ToListAsync();
            return Results.Ok(campaigns.OrderByDescending(c => c.OpensAt).Select(c => ToRecord(c, now)));
        })
        .WithName("ListCampaigns").WithSummary("List recruitment campaigns").WithTags("Membership admin")
        .RequirePermission(Permissions.MembershipManage);

        app.MapPost(AdminRoute, async (HttpContext context, CampaignRequest payload, MembershipDbContext db, TimeProvider clock) =>
        {
            if (Validate(payload) is { Count: > 0 } invalid)
            {
                return ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", $"Invalid fields: {string.Join(", ", invalid)}.");
            }

            var slug = payload.Slug!.Trim();
            if (await db.Campaigns.AnyAsync(c => c.Slug == slug))
            {
                return ApiError.Create(context, StatusCodes.Status409Conflict, "SLUG_TAKEN", "A campaign with this slug already exists.");
            }

            var now = clock.GetUtcNow();
            var campaign = new CampaignEntity { Id = $"cmp_{Guid.NewGuid():N}", CreatedAt = now };
            Apply(campaign, payload);
            db.Campaigns.Add(campaign);
            await db.SaveChangesAsync();
            return Results.Created($"{AdminRoute}/{campaign.Id}", ToRecord(campaign, now));
        })
        .WithName("CreateCampaign").WithSummary("Create a recruitment campaign").WithTags("Membership admin")
        .RequirePermission(Permissions.MembershipManage);

        app.MapPut($"{AdminRoute}/{{id}}", async (HttpContext context, string id, CampaignRequest payload, MembershipDbContext db, TimeProvider clock) =>
        {
            if (Validate(payload) is { Count: > 0 } invalid)
            {
                return ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", $"Invalid fields: {string.Join(", ", invalid)}.");
            }

            var campaign = await db.Campaigns.FirstOrDefaultAsync(c => c.Id == id);
            if (campaign is null)
            {
                return ApiError.Create(context, StatusCodes.Status404NotFound, "NOT_FOUND", "Campaign not found.");
            }

            var slug = payload.Slug!.Trim();
            if (await db.Campaigns.AnyAsync(c => c.Slug == slug && c.Id != id))
            {
                return ApiError.Create(context, StatusCodes.Status409Conflict, "SLUG_TAKEN", "A campaign with this slug already exists.");
            }

            Apply(campaign, payload);
            await db.SaveChangesAsync();
            return Results.Ok(ToRecord(campaign, clock.GetUtcNow()));
        })
        .WithName("UpdateCampaign").WithSummary("Change a campaign's name or window").WithTags("Membership admin")
        .RequirePermission(Permissions.MembershipManage);

        app.MapPost($"{AdminRoute}/{{id}}/close", async (HttpContext context, string id, MembershipDbContext db, TimeProvider clock) =>
        {
            var campaign = await db.Campaigns.FirstOrDefaultAsync(c => c.Id == id);
            if (campaign is null)
            {
                return ApiError.Create(context, StatusCodes.Status404NotFound, "NOT_FOUND", "Campaign not found.");
            }

            var now = clock.GetUtcNow();
            campaign.ClosedAt ??= now;
            await db.SaveChangesAsync();
            return Results.Ok(ToRecord(campaign, now));
        })
        .WithName("CloseCampaign").WithSummary("Close a campaign now").WithDescription("Intake stops immediately: applications get 409 INTAKE_CLOSED unless another season is open. Closing twice changes nothing.")
        .WithTags("Membership admin")
        .RequirePermission(Permissions.MembershipManage);
    }

    private static List<string> Validate(CampaignRequest r)
    {
        var invalid = new List<string>();
        if (r.Slug is null || r.Slug.Trim().Length is 0 or > 64 || !SlugPattern().IsMatch(r.Slug.Trim()))
        {
            invalid.Add("slug");
        }

        if (string.IsNullOrWhiteSpace(r.NameEn) || r.NameEn.Trim().Length > 200)
        {
            invalid.Add("nameEn");
        }

        if (string.IsNullOrWhiteSpace(r.NameBn) || r.NameBn.Trim().Length > 200)
        {
            invalid.Add("nameBn");
        }

        if (r.OpensAt is null)
        {
            invalid.Add("opensAt");
        }

        if (r.OpensAt is { } opens && r.ClosesAt is { } closes && closes <= opens)
        {
            invalid.Add("closesAt");
        }

        return invalid;
    }

    private static void Apply(CampaignEntity campaign, CampaignRequest r)
    {
        campaign.Slug = r.Slug!.Trim();
        campaign.NameEn = r.NameEn!.Trim();
        campaign.NameBn = r.NameBn!.Trim();
        campaign.OpensAt = r.OpensAt!.Value.ToUniversalTime();
        campaign.ClosesAt = r.ClosesAt?.ToUniversalTime();
    }

    private static CampaignRecord ToRecord(CampaignEntity c, DateTimeOffset now) =>
        new(c.Id, c.Slug, c.NameEn, c.NameBn, c.OpensAt, c.ClosesAt, c.ClosedAt, c.IsOpen(now));

    private static PublicCampaign ToPublic(CampaignEntity c) => new(c.Slug, new LocalizedName(c.NameEn, c.NameBn), c.OpensAt, c.ClosesAt);
}

public sealed record IntakeDecision(string Intake, CampaignEntity? Campaign);
