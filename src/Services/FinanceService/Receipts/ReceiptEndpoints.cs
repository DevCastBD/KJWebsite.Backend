using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using FinanceService.Data;
using FinanceService.Data.Entities;
using FinanceService.Donations;
using KJWebsite.BuildingBlocks;
using KJWebsite.BuildingBlocks.Finance;
using KJWebsite.BuildingBlocks.Security;
using Microsoft.EntityFrameworkCore;

namespace FinanceService.Receipts;

public sealed record ReceiptRow(string Id, string Number, string Series, string DonationId, string Reference, string IssuedAt, string ReceivedOn, string DonorName, string Amount, string Currency, bool InKind, string Status, int Copies);

public sealed record ReceiptReissueView(string Reason, string ReissuedBy, DateTimeOffset At);

public sealed record ReceiptDetail(ReceiptRow Receipt, string DonorEmail, string Hash, string PrevHash, string? VoidReason, IReadOnlyList<ReceiptReissueView> Reissues);

public sealed record ReissueRequest(string? Reason, bool Resend);

public sealed record StatementTotalView(string Currency, string Cash, string InKind);

public sealed record StatementRow(string DonorKey, string DonorName, string? Email, int Receipts, IReadOnlyList<StatementTotalView> Totals, bool Queued);

public sealed record StatementSendRequest(string? FiscalYear);

public sealed record StatementSendResult(string FiscalYear, int Queued, int AlreadyQueued, int NoEmail);

public static class ReceiptEndpoints
{
    private const string Route = "/api/v1/admin/finance";
    private const int DefaultLimit = 50;
    private const int MaxLimit = 100;

    public static void MapReceipts(this IEndpointRouteBuilder app)
    {
        app.MapGet($"{Route}/receipts", async (FinanceDbContext db, ReceiptService receipts, string? series, string? status, int? limit, int? offset) =>
        {
            var query = from r in db.Receipts.AsNoTracking()
                        join d in db.Donations.AsNoTracking() on r.DonationId equals d.Id
                        select new { Receipt = r, d.Reference, Voided = db.ReceiptVoids.Any(v => v.ReceiptId == r.Id), Copies = db.ReceiptReissues.Count(c => c.ReceiptId == r.Id) };
            if (!string.IsNullOrWhiteSpace(series))
            {
                query = query.Where(x => x.Receipt.Series == series);
            }

            if (status == "void")
            {
                query = query.Where(x => x.Voided);
            }
            else if (status == "valid")
            {
                query = query.Where(x => !x.Voided);
            }

            var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
            var skip = Math.Max(0, offset ?? 0);
            var total = await query.CountAsync();
            var page = await query.OrderByDescending(x => x.Receipt.Series).ThenByDescending(x => x.Receipt.Seq).Skip(skip).Take(take).ToListAsync();
            return Results.Ok(new PagingEnvelope<ReceiptRow>([.. page.Select(x => Row(x.Receipt, x.Reference, x.Voided, x.Copies, receipts))], total, null, take, skip));
        })
        .WithName("ListReceipts")
        .WithSummary("Receipts, newest number first")
        .WithDescription("Filters: series (fiscal year such as 2026-27), status (valid, void).")
        .WithTags("Receipts")
        .RequirePermission(Permissions.FinanceView);

        app.MapGet($"{Route}/receipts/verify", async (HttpContext context, ReceiptService receipts, string? series) =>
            string.IsNullOrWhiteSpace(series)
                ? ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", "Invalid fields: series.")
                : Results.Ok(await receipts.VerifyAsync(series, context.RequestAborted)))
        .WithName("VerifyReceiptSeries")
        .WithSummary("Check a series for gaps, broken links and altered receipts")
        .WithDescription("Walks the fiscal year's receipts in order: the sequence must run 1..n without gaps, each receipt must carry the previous one's hash, and each hash must match the receipt's own content. `valid` is false and `problems` says where when not.")
        .WithTags("Receipts")
        .RequirePermission(Permissions.FinanceView);

        app.MapGet($"{Route}/receipts/{{id}}", async (HttpContext context, string id, FinanceDbContext db, ReceiptService receipts) =>
        {
            var found = await Find(db, id);
            if (found is null)
            {
                return ApiError.Create(context, StatusCodes.Status404NotFound, "NOT_FOUND", "Receipt not found.");
            }

            var voided = await db.ReceiptVoids.AsNoTracking().FirstOrDefaultAsync(v => v.ReceiptId == id);
            var reissues = (await db.ReceiptReissues.AsNoTracking().Where(r => r.ReceiptId == id).ToListAsync()).OrderBy(r => r.At).Select(r => new ReceiptReissueView(r.Reason, r.ReissuedBy, r.At)).ToList();
            return Results.Ok(new ReceiptDetail(Row(found.Value.Receipt, found.Value.Reference, voided is not null, reissues.Count, receipts), receipts.DonorEmail(found.Value.Receipt), found.Value.Receipt.Hash, found.Value.Receipt.PrevHash, voided?.Reason, reissues));
        })
        .WithName("GetReceipt")
        .WithSummary("One receipt with its hash links, void and re-issue history")
        .WithTags("Receipts")
        .RequirePermission(Permissions.FinanceView);

        app.MapGet($"{Route}/receipts/{{id}}/pdf", async (HttpContext context, string id, FinanceDbContext db, ReceiptService receipts) =>
        {
            var found = await Find(db, id);
            if (found is null)
            {
                return ApiError.Create(context, StatusCodes.Status404NotFound, "NOT_FOUND", "Receipt not found.");
            }

            context.Response.Headers.CacheControl = "private, no-store";
            context.Response.Headers.ContentDisposition = $"inline; filename=\"{found.Value.Receipt.Number}.pdf\"";
            return Results.File(ReceiptPdf.Render((await receipts.DocumentForAsync(found.Value.Receipt, context.RequestAborted))!), "application/pdf");
        })
        .WithName("GetReceiptPdf")
        .WithSummary("The receipt as a bilingual PDF")
        .WithDescription("Marked VOID when its donation was reversed, and as a DUPLICATE COPY once re-issued. Requires `finance.view`.")
        .WithTags("Receipts")
        .RequirePermission(Permissions.FinanceView);

        app.MapPost($"{Route}/receipts/{{id}}/reissue", async (HttpContext context, string id, ReissueRequest payload, FinanceDbContext db, ReceiptService receipts, IDonorProtector protector, TimeProvider clock) =>
        {
            var reason = payload.Reason?.Trim();
            if (reason is not { Length: >= 3 and <= 500 })
            {
                return ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", "Invalid fields: reason (3-500 characters).");
            }

            var found = await Find(db, id);
            if (found is null)
            {
                return ApiError.Create(context, StatusCodes.Status404NotFound, "NOT_FOUND", "Receipt not found.");
            }

            if (await db.ReceiptVoids.AnyAsync(v => v.ReceiptId == id))
            {
                return ApiError.Create(context, StatusCodes.Status409Conflict, "RECEIPT_VOID", "A void receipt cannot be re-issued.");
            }

            var receipt = found.Value.Receipt;
            var now = clock.GetUtcNow();
            db.ReceiptReissues.Add(new ReceiptReissueEntity { ReceiptId = id, Reason = reason, ReissuedBy = context.User.FindFirstValue(KjClaims.Subject) ?? string.Empty, At = now });
            if (payload.Resend && receipts.DonorEmail(receipt).Length > 0)
            {
                db.Notifications.Add(new FinanceNotificationEntity
                {
                    Template = "donation.receipt",
                    Language = receipt.Language,
                    RecipientProtected = receipt.DonorEmailProtected,
                    CreatedAt = now,
                    PayloadProtected = protector.Protect(JsonSerializer.Serialize(new { number = receipt.Number, reference = found.Value.Reference, copy = true }))
                });
            }

            await db.SaveChangesAsync();
            var copies = await db.ReceiptReissues.CountAsync(r => r.ReceiptId == id);
            return Results.Ok(Row(receipt, found.Value.Reference, false, copies, receipts));
        })
        .WithName("ReissueReceipt")
        .WithSummary("Issue a copy of a receipt again")
        .WithDescription("The original is untouched and keeps its number; the re-issue is recorded with who asked and why, and later PDFs are marked DUPLICATE COPY n. `resend: true` also queues the email again. A void receipt cannot be re-issued. Requires `finance.receipt.manage`.")
        .WithTags("Receipts")
        .RequirePermission(Permissions.FinanceReceiptManage);

        app.MapGet($"{Route}/statements", async (HttpContext context, FinanceDbContext db, ReceiptService receipts, string? fiscalYear) =>
        {
            if (string.IsNullOrWhiteSpace(fiscalYear))
            {
                return ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", "Invalid fields: fiscalYear.");
            }

            var groups = await GroupsAsync(db, receipts, fiscalYear, context.RequestAborted);
            var queued = (await db.StatementSends.AsNoTracking().Where(s => s.FiscalYear == fiscalYear).Select(s => s.DonorKey).ToListAsync()).ToHashSet();
            return Results.Ok(new
            {
                fiscalYear,
                items = groups.Select(g => new StatementRow(g.Key, receipts.DonorName(g.Latest), g.Email.Length == 0 ? null : g.Email, g.Receipts.Count, TotalsOf(g.Receipts).Select(t => new StatementTotalView(t.Currency, t.Cash.ToDecimalString(), t.InKind.ToDecimalString())).ToList(), queued.Contains(g.Key))).ToList()
            });
        })
        .WithName("ListDonorStatements")
        .WithSummary("Per-donor annual totals for a fiscal year")
        .WithDescription("Donors are grouped by a keyed hash of their email. Voided receipts are left out; each currency is totalled separately, with in-kind value apart from money. A donor with no email stands alone per gift.")
        .WithTags("Statements")
        .RequirePermission(Permissions.FinanceView);

        app.MapGet($"{Route}/statements/{{donorKey}}/pdf", async (HttpContext context, string donorKey, FinanceDbContext db, ReceiptService receipts, string? fiscalYear) =>
        {
            if (string.IsNullOrWhiteSpace(fiscalYear))
            {
                return ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", "Invalid fields: fiscalYear.");
            }

            var group = (await GroupsAsync(db, receipts, fiscalYear, context.RequestAborted)).FirstOrDefault(g => g.Key == donorKey);
            if (group is null)
            {
                return ApiError.Create(context, StatusCodes.Status404NotFound, "NOT_FOUND", "No statement for that donor and year.");
            }

            context.Response.Headers.CacheControl = "private, no-store";
            return Results.File(ReceiptPdf.RenderStatement(StatementFor(group, fiscalYear, receipts)), "application/pdf");
        })
        .WithName("GetDonorStatementPdf")
        .WithSummary("A donor's annual statement as a bilingual PDF")
        .WithTags("Statements")
        .RequirePermission(Permissions.FinanceView);

        app.MapPost($"{Route}/statements/send", async (HttpContext context, StatementSendRequest payload, FinanceDbContext db, ReceiptService receipts, IDonorProtector protector, TimeProvider clock) =>
        {
            var year = payload.FiscalYear?.Trim();
            if (string.IsNullOrEmpty(year))
            {
                return ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", "Invalid fields: fiscalYear.");
            }

            var actor = context.User.FindFirstValue(KjClaims.Subject) ?? string.Empty;
            var sent = (await db.StatementSends.AsNoTracking().Where(s => s.FiscalYear == year).Select(s => s.DonorKey).ToListAsync()).ToHashSet();
            int queued = 0, already = 0, noEmail = 0;
            var now = clock.GetUtcNow();
            foreach (var group in await GroupsAsync(db, receipts, year, context.RequestAborted))
            {
                if (group.Email.Length == 0)
                {
                    noEmail++;
                    continue;
                }

                if (!sent.Add(group.Key))
                {
                    already++;
                    continue;
                }

                db.StatementSends.Add(new StatementSendEntity { DonorKey = group.Key, FiscalYear = year, QueuedBy = actor, At = now });
                db.Notifications.Add(new FinanceNotificationEntity
                {
                    Template = "donor.annual_statement",
                    Language = group.Latest.Language,
                    RecipientProtected = group.Latest.DonorEmailProtected,
                    CreatedAt = now,
                    PayloadProtected = protector.Protect(JsonSerializer.Serialize(new { fiscalYear = year, receipts = group.Receipts.Count }))
                });
                queued++;
            }

            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear();
                return ApiError.Create(context, StatusCodes.Status409Conflict, "ALREADY_QUEUED", "Another request queued these statements first; check the list and try again.");
            }

            return Results.Ok(new StatementSendResult(year, queued, already, noEmail));
        })
        .WithName("QueueDonorStatements")
        .WithSummary("Queue each donor's annual statement for email, once per donor and year")
        .WithDescription("Writes one outbox message per donor with an email address who has not been queued for that fiscal year; a repeat queues nothing new. Requires `finance.receipt.manage`.")
        .WithTags("Statements")
        .RequirePermission(Permissions.FinanceReceiptManage);
    }

    private sealed record DonorGroup(string Key, ReceiptEntity Latest, string Email, IReadOnlyList<ReceiptEntity> Receipts);

    private static async Task<List<DonorGroup>> GroupsAsync(FinanceDbContext db, ReceiptService receipts, string fiscalYear, CancellationToken cancellationToken)
    {
        var voided = (await db.ReceiptVoids.AsNoTracking().Select(v => v.ReceiptId).ToListAsync(cancellationToken)).ToHashSet();
        var rows = (await db.Receipts.AsNoTracking().Where(r => r.Series == fiscalYear).ToListAsync(cancellationToken)).Where(r => !voided.Contains(r.Id)).OrderBy(r => r.Seq).ToList();
        return [.. rows.GroupBy(r => r.DonorKey).Select(g => new DonorGroup(g.Key, g.Last(), receipts.DonorEmail(g.Last()), [.. g])).OrderBy(g => g.Latest.Seq)];
    }

    private static List<(string Currency, Money Cash, Money InKind)> TotalsOf(IEnumerable<ReceiptEntity> receipts) =>
        [.. receipts.GroupBy(r => r.Currency).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => (
            g.Key,
            Money.Sum(g.Where(r => !r.IsInKind).Select(r => Money.Of(r.Amount, g.Key)), g.Key),
            Money.Sum(g.Where(r => r.IsInKind).Select(r => Money.Of(r.Amount, g.Key)), g.Key)))];

    private static StatementDocument StatementFor(DonorGroup group, string fiscalYear, ReceiptService receipts) => new(
        System.Text.Json.JsonSerializer.Deserialize<OrganisationDetails>(group.Latest.OrganisationJson) ?? new OrganisationDetails(),
        receipts.DonorName(group.Latest), fiscalYear,
        [.. group.Receipts.Select(r => new StatementLine(r.Number, r.ReceivedOn, Money.Of(r.Amount, r.Currency), r.IsInKind, ReceiptPdf.DesignationLabel(r.DesignationType, r.DesignationId)))],
        [.. TotalsOf(group.Receipts).Select(t => new StatementTotal(t.Currency, t.Cash, t.InKind))]);

    private static async Task<(ReceiptEntity Receipt, string Reference)?> Find(FinanceDbContext db, string id)
    {
        var row = await (from r in db.Receipts.AsNoTracking()
                         join d in db.Donations.AsNoTracking() on r.DonationId equals d.Id
                         where r.Id == id
                         select new { Receipt = r, d.Reference }).FirstOrDefaultAsync();
        return row is null ? null : (row.Receipt, row.Reference);
    }

    private static ReceiptRow Row(ReceiptEntity r, string reference, bool voided, int copies, ReceiptService receipts) => new(
        r.Id, r.Number, r.Series, r.DonationId, reference, r.IssuedAt.ToString("O", CultureInfo.InvariantCulture), r.ReceivedOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        receipts.DonorName(r), r.Amount.ToString("0.00", CultureInfo.InvariantCulture), r.Currency, r.IsInKind, voided ? "void" : "valid", copies);
}
