using System.Security.Claims;
using FinanceService.Data;
using FinanceService.Data.Entities;
using KJWebsite.BuildingBlocks;
using KJWebsite.BuildingBlocks.DataQuality;
using KJWebsite.BuildingBlocks.Security;
using Microsoft.EntityFrameworkCore;

namespace FinanceService.DataQuality;

public sealed record AnomalyView(string Id, string Rule, string? DonationId, string? Reference, string? Amount, string? Currency, string Detail, string Status, DateTimeOffset DetectedAt,
    string? ReviewedBy, DateTimeOffset? ReviewedAt, string? ReviewNote);

public sealed record AnomalyListView(IReadOnlyList<AnomalyView> Items, int Total);

public sealed record AnomalyReviewRequest(string? Outcome, string? Note);

public sealed record DuplicateListView(IReadOnlyList<DuplicateCandidate> Items, int Total);

public sealed record DuplicateDecisionView(string Id, string PairKey, string Decision, string Note, string? DecidedBy, DateTimeOffset DecidedAt);

public static class DataQualityEndpoints
{
    private const string Route = "/api/v1/admin/finance/data-quality";

    public static void MapDataQuality(this IEndpointRouteBuilder app)
    {
        app.MapGet($"{Route}/anomalies", async (HttpContext context, FinanceDbContext db, string? status, string? rule, int? limit, int? offset, CancellationToken ct) =>
        {
            if (status is not null && status is not (AnomalyStatuses.Open or AnomalyStatuses.Confirmed or AnomalyStatuses.Dismissed))
            {
                return ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", "status must be open, confirmed or dismissed.");
            }

            var all = await db.DonationAnomalies.AsNoTracking().Where(a => (status == null || a.Status == status) && (rule == null || a.Rule == rule)).ToListAsync(ct);
            var page = all.OrderByDescending(a => a.DetectedAt).ThenBy(a => a.Id, StringComparer.Ordinal).Skip(Math.Max(0, offset ?? 0)).Take(Math.Clamp(limit ?? 50, 1, 100)).ToList();
            var ids = page.Where(a => a.DonationId != null).Select(a => a.DonationId!).ToList();
            var donations = await db.Donations.AsNoTracking().Where(d => ids.Contains(d.Id)).Select(d => new { d.Id, d.Reference, d.Amount, d.Currency }).ToDictionaryAsync(d => d.Id, ct);
            return Results.Ok(new AnomalyListView([.. page.Select(a =>
            {
                donations.TryGetValue(a.DonationId ?? string.Empty, out var donation);
                return new AnomalyView(a.Id, a.Rule, a.DonationId, donation?.Reference, donation?.Amount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture), donation?.Currency, a.Detail, a.Status, a.DetectedAt, a.ReviewedBy, a.ReviewedAt, a.ReviewNote);
            })], all.Count));
        })
        .WithName("ListDonationAnomalies").WithSummary("Donations flagged for review")
        .WithDescription("Rules: `large_amount`, `rapid_repeat`, `confirmed_without_posting`, `gateway_failure_spike`. A flag never changes the donation; `status` is `open` until a person reviews it. Requires `finance.view`.")
        .WithTags("Data quality").Produces<AnomalyListView>().RequirePermission(Permissions.FinanceView);

        app.MapPost($"{Route}/anomalies/scan", async (AnomalyDetector detector, CancellationToken ct) => Results.Ok(await detector.ScanAsync(ct)))
            .WithName("ScanDonationAnomalies").WithSummary("Look for anomalous donations now")
            .WithDescription("Writes new flags only (each flag once, however often this runs) and returns `{checked, flagged}`. Requires `finance.reconcile`.")
            .WithTags("Data quality").Produces<ScanResult>().RequirePermission(Permissions.FinanceReconcile);

        app.MapPost($"{Route}/anomalies/{{id}}/review", async (HttpContext context, FinanceDbContext db, TimeProvider clock, string id, AnomalyReviewRequest payload, CancellationToken ct) =>
        {
            if (payload.Outcome is not (AnomalyStatuses.Confirmed or AnomalyStatuses.Dismissed) || payload.Note is null || payload.Note.Trim().Length < DuplicateDecisions.MinNoteLength || payload.Note.Length > 500)
            {
                return ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", "outcome must be 'confirmed' or 'dismissed', with a note (5-500 characters) saying what you checked.");
            }

            var anomaly = await db.DonationAnomalies.FirstOrDefaultAsync(a => a.Id == id, ct);
            if (anomaly is null)
            {
                return ApiError.Create(context, StatusCodes.Status404NotFound, "NOT_FOUND", "No such anomaly.");
            }

            if (anomaly.Status != AnomalyStatuses.Open)
            {
                return ApiError.Create(context, StatusCodes.Status409Conflict, "ALREADY_REVIEWED", "This anomaly has already been reviewed.");
            }

            anomaly.Status = payload.Outcome;
            anomaly.ReviewNote = payload.Note.Trim();
            anomaly.ReviewedBy = context.User.FindFirstValue(KjClaims.Subject);
            anomaly.ReviewedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
            return Results.Ok(new AnomalyView(anomaly.Id, anomaly.Rule, anomaly.DonationId, null, null, null, anomaly.Detail, anomaly.Status, anomaly.DetectedAt, anomaly.ReviewedBy, anomaly.ReviewedAt, anomaly.ReviewNote));
        })
        .WithName("ReviewDonationAnomaly").WithSummary("Record what was found when a flagged donation was looked at")
        .WithDescription("`{outcome: confirmed|dismissed, note}`. `confirmed` means the flag was right and follow-up is needed; `dismissed` means it was fine. Either way the donation is untouched (corrections are reversing entries) and the decision is audited. 409 if already reviewed. Requires `finance.donation.approve`.")
        .WithTags("Data quality").Produces<AnomalyView>().RequirePermission(Permissions.FinanceDonationApprove);

        app.MapGet($"{Route}/duplicates/donors", async (DonorDuplicates duplicates, CancellationToken ct) =>
        {
            var items = await duplicates.CandidatesAsync(ct);
            return Results.Ok(new DuplicateListView([.. items.Take(200)], items.Count));
        })
        .WithName("ListDonorDuplicateCandidates").WithSummary("Donor records that may be the same person")
        .WithDescription("Each pair has its `confidence` (`high` for a shared email or phone, `low` for the same full name only) and `evidence` kinds, never the values. Pairs already ruled on are left out. Nothing is merged. Requires `finance.view`.")
        .WithTags("Data quality").Produces<DuplicateListView>().RequirePermission(Permissions.FinanceView);

        app.MapPost($"{Route}/duplicates/donors/decisions", async (HttpContext context, FinanceDbContext db, TimeProvider clock, DuplicateDecisionRequest payload, CancellationToken ct) =>
        {
            if (DuplicateDecisions.Validate(payload) is { } problem)
            {
                return ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", problem);
            }

            var ids = new[] { payload.LeftId!, payload.RightId! };
            if (await db.Donors.CountAsync(d => ids.Contains(d.Id), ct) != 2)
            {
                return ApiError.Create(context, StatusCodes.Status404NotFound, "NOT_FOUND", "Both donors must exist.");
            }

            var key = DuplicateDetector.PairKey(payload.LeftId!, payload.RightId!);
            if (await db.DonorDuplicateDecisions.AnyAsync(d => d.PairKey == key, ct))
            {
                return ApiError.Create(context, StatusCodes.Status409Conflict, "ALREADY_DECIDED", "This pair has already been ruled on.");
            }

            var decision = new DonorDuplicateDecisionEntity { Id = $"dup_{Guid.NewGuid():N}", PairKey = key, Decision = payload.Decision!, Note = payload.Note!.Trim(), DecidedBy = context.User.FindFirstValue(KjClaims.Subject), DecidedAt = clock.GetUtcNow() };
            db.DonorDuplicateDecisions.Add(decision);
            await db.SaveChangesAsync(ct);
            return Results.Created($"{Route}/duplicates/donors", new DuplicateDecisionView(decision.Id, decision.PairKey, decision.Decision, decision.Note, decision.DecidedBy, decision.DecidedAt));
        })
        .WithName("DecideDonorDuplicate").WithSummary("Rule on a possible duplicate: not the same donor, or confirmed the same")
        .WithDescription("`{leftId, rightId, decision: not_duplicate|confirmed_duplicate, note}`. The note is required and the decision is audited with who made it. It changes no donor and no donation: donation records are append-only, so combining two donors is a separate, deliberate step that acts on a confirmed pair. Requires `finance.donor.manage`.")
        .WithTags("Data quality").Produces<DuplicateDecisionView>(StatusCodes.Status201Created).RequirePermission(Permissions.FinanceDonorManage);
    }
}
