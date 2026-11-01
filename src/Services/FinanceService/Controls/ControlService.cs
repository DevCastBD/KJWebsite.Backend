using System.Globalization;
using System.Text.Json;
using FinanceService.Data;
using FinanceService.Data.Entities;
using FinanceService.Donations;
using FinanceService.Ledger;
using KJWebsite.BuildingBlocks;
using KJWebsite.BuildingBlocks.Finance;
using Microsoft.EntityFrameworkCore;

namespace FinanceService.Controls;

public sealed record AdjustmentLinePayload(string Account, string Side, string Amount, string Currency);

public sealed record AdjustmentPayload(string PostedOn, string Memo, IReadOnlyList<AdjustmentLinePayload> Lines);

public sealed record ControlDecisionView(string Decision, string DecidedBy, string? Note, DateTimeOffset At);

public sealed record ControlRequestView(string Id, string Kind, string Subject, string Reason, string RequestedBy, DateTimeOffset RequestedAt, string Status, ControlDecisionView? Decision, string? PostingId, string? DonationId, JsonElement Details);

public sealed record RefundRow(string RequestId, string DonationId, string Reference, string Amount, string Currency, string RequestedBy, DateTimeOffset RequestedAt, string Status, string? DecidedBy, string Reconciliation, string? NoticeGateway, string? NoticeAmount);

public sealed record UnexpectedNotice(string NoticeId, string Gateway, string DonationId, string? Reference, string? Amount, string? Currency, DateTimeOffset At, string Why);

/// <summary>
/// The control layer for sensitive financial actions. A person asks (a request, validated up front and never changed), a different person approves or rejects, and an approval carries
/// the action out in the same commit as the decision, so there is no half-done state. One decision per request is a unique key, which also makes the action run at most once however
/// many approvers click at the same moment. Segregation of duties is checked here and again by PostgreSQL.
/// </summary>
public sealed class ControlService(FinanceDbContext db, LedgerService ledger, DonationReversal refunds, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private DateTimeOffset Now => clock.GetUtcNow();

    private static string Money(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    // ---- asking ------------------------------------------------------------------------------------------------------------------------------------------

    public async Task<Result<ControlRequestEntity>> RequestAdjustmentAsync(PostingDraft draft, string reason, string actor, CancellationToken cancellationToken)
    {
        var codes = draft.Lines.Select(l => l.AccountCode).Distinct().ToList();
        var accounts = await db.Accounts.AsNoTracking().Where(a => codes.Contains(a.Code)).ToDictionaryAsync(a => a.Code, cancellationToken);
        var valid = PostingRules.Validate(draft, accounts);
        if (!valid.IsSuccess)
        {
            return Result.Failure<ControlRequestEntity>(valid.Error!.Code, valid.Error.Message);
        }

        if (draft.PostedOn > ledger.Today)
        {
            return Result.Failure<ControlRequestEntity>("POSTING_DATE_FUTURE", "A posting cannot be dated in the future.");
        }

        if (await ledger.ClosedCodeAsync(draft.PostedOn, cancellationToken) is { } closed)
        {
            return Result.Failure<ControlRequestEntity>(closed, $"The month or fiscal year of {draft.PostedOn:yyyy-MM-dd} is closed.");
        }

        var payload = new AdjustmentPayload(draft.PostedOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), draft.Memo,
            [.. draft.Lines.Select(l => new AdjustmentLinePayload(l.AccountCode, l.Side == LedgerSide.Debit ? "debit" : "credit", Money(l.Amount.Amount), l.Amount.Currency))]);
        return await AddAsync(ControlKinds.Adjustment, string.Empty, JsonSerializer.Serialize(payload, Json), reason, actor, cancellationToken);
    }

    public async Task<Result<ControlRequestEntity>> RequestPostingReversalAsync(long postingId, string reason, string actor, CancellationToken cancellationToken)
    {
        var posting = await db.Postings.AsNoTracking().FirstOrDefaultAsync(p => p.Id == postingId, cancellationToken);
        if (posting is null)
        {
            return Result.Failure<ControlRequestEntity>("NOT_FOUND", "Posting not found.");
        }

        if (posting.ReversesPostingId is not null)
        {
            return Result.Failure<ControlRequestEntity>("NOT_REVERSIBLE", "A reversal cannot be reversed; post an adjustment instead.");
        }

        if (await db.Postings.AnyAsync(p => p.ReversesPostingId == postingId, cancellationToken))
        {
            return Result.Failure<ControlRequestEntity>("ALREADY_REVERSED", "This posting has already been reversed.");
        }

        return await AddAsync(ControlKinds.PostingReversal, postingId.ToString(CultureInfo.InvariantCulture), JsonSerializer.Serialize(new { postingId }, Json), reason, actor, cancellationToken);
    }

    public async Task<Result<ControlRequestEntity>> RequestRefundAsync(string donationId, string reason, string actor, CancellationToken cancellationToken)
    {
        var check = await refunds.CheckAsync(donationId, cancellationToken);
        return check.IsSuccess
            ? await AddAsync(ControlKinds.Refund, donationId, JsonSerializer.Serialize(new { donationId }, Json), reason, actor, cancellationToken)
            : Result.Failure<ControlRequestEntity>(check.Error!.Code, check.Error.Message);
    }

    public async Task<Result<ControlRequestEntity>> RequestReopenAsync(string scope, string key, string reason, string actor, CancellationToken cancellationToken)
    {
        var open = scope == "period"
            ? await db.PeriodCloses.AnyAsync(c => c.PeriodKey == key, cancellationToken)
            : await db.FiscalYearCloses.AnyAsync(c => c.Label == key, cancellationToken);
        if (!open)
        {
            return Result.Failure<ControlRequestEntity>("NOT_CLOSED", scope == "period" ? $"Period {key} is not closed." : $"Fiscal year {key} is not closed.");
        }

        return await AddAsync(scope == "period" ? ControlKinds.PeriodReopen : ControlKinds.FiscalYearReopen, key, JsonSerializer.Serialize(new { key }, Json), reason, actor, cancellationToken);
    }

    private async Task<Result<ControlRequestEntity>> AddAsync(string kind, string subject, string payload, string reason, string actor, CancellationToken cancellationToken)
    {
        if (subject.Length > 0 && await db.ControlRequests.AnyAsync(r => r.Kind == kind && r.Subject == subject && !db.ControlDecisions.Any(d => d.RequestId == r.Id), cancellationToken))
        {
            return Result.Failure<ControlRequestEntity>("ALREADY_REQUESTED", "There is already an open request for this; decide it first.");
        }

        var request = new ControlRequestEntity
        {
            Id = "ctl_" + Guid.NewGuid().ToString("N"),
            Kind = kind,
            Subject = subject,
            PayloadJson = payload,
            Reason = reason,
            RequestedBy = actor,
            RequestedAt = Now
        };
        db.ControlRequests.Add(request);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success(request);
    }

    // ---- deciding ----------------------------------------------------------------------------------------------------------------------------------------

    public async Task<Result<ControlRequestView>> DecideAsync(string id, string decision, string? note, string actor, CancellationToken cancellationToken)
    {
        var request = await db.ControlRequests.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (request is null)
        {
            return Result.Failure<ControlRequestView>("NOT_FOUND", "Request not found.");
        }

        if (await db.ControlDecisions.AnyAsync(d => d.RequestId == id, cancellationToken))
        {
            return Result.Failure<ControlRequestView>("ALREADY_DECIDED", "This request has already been decided.");
        }

        var requester = string.Equals(request.RequestedBy, actor, StringComparison.Ordinal);
        if (decision == ControlDecisions.Withdrawn ? !requester : requester)
        {
            return Result.Failure<ControlRequestView>("SEGREGATION_OF_DUTIES", decision == ControlDecisions.Withdrawn
                ? "Only the person who made a request can withdraw it."
                : "You made this request, so someone else must approve or reject it.");
        }

        var trimmedNote = note?.Trim();
        if (trimmedNote is { Length: > 500 })
        {
            return Result.Failure<ControlRequestView>("VALIDATION_ERROR", "Invalid fields: note (500 characters at most).");
        }

        var outcome = new ControlDecisionEntity { RequestId = id, Decision = decision, DecidedBy = actor, Note = string.IsNullOrWhiteSpace(trimmedNote) ? null : trimmedNote, At = Now };
        if (decision == ControlDecisions.Approved)
        {
            var executed = await ExecuteAsync(request, outcome, cancellationToken);
            if (!executed.IsSuccess)
            {
                db.ChangeTracker.Clear();
                return Result.Failure<ControlRequestView>(executed.Error!.Code, executed.Error.Message);
            }
        }
        else
        {
            db.ControlDecisions.Add(outcome);
            if (!await TrySaveAsync(cancellationToken))
            {
                return Result.Failure<ControlRequestView>("ALREADY_DECIDED", "This request has already been decided.");
            }
        }

        db.ChangeTracker.Clear();
        return Result.Success((await GetAsync(id, cancellationToken))!);
    }

    private async Task<bool> TrySaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException)
        {
            // The unique decision per request: someone decided it at the same moment, and nothing of ours was kept.
            db.ChangeTracker.Clear();
            return false;
        }
    }

    private async Task<Result> ExecuteAsync(ControlRequestEntity request, ControlDecisionEntity outcome, CancellationToken cancellationToken)
    {
        switch (request.Kind)
        {
            case ControlKinds.Adjustment:
                {
                    var payload = JsonSerializer.Deserialize<AdjustmentPayload>(request.PayloadJson, Json)!;
                    var lines = payload.Lines.Select(l => new LedgerLine(l.Account, l.Side == "debit" ? LedgerSide.Debit : LedgerSide.Credit, Finance_Money(l)));
                    var draft = new PostingDraft(DateOnly.ParseExact(payload.PostedOn, "yyyy-MM-dd", CultureInfo.InvariantCulture), payload.Memo, PostingSources.Adjustment, "control:" + request.Id, [.. lines], request.Reason);
                    var staged = await ledger.StageAsync(draft, request.RequestedBy, cancellationToken);
                    if (!staged.IsSuccess)
                    {
                        return Result.Failure(staged.Error!.Code, staged.Error.Message);
                    }

                    outcome.ResultRef = "control:" + request.Id;
                    break;
                }

            case ControlKinds.PostingReversal:
                {
                    var postingId = long.Parse(request.Subject, CultureInfo.InvariantCulture);
                    var staged = await ledger.StageReversalAsync(postingId, request.Reason, request.RequestedBy, cancellationToken);
                    if (!staged.IsSuccess)
                    {
                        return Result.Failure(staged.Error!.Code, staged.Error.Message);
                    }

                    outcome.ResultRef = "reversal-of:" + request.Subject;
                    break;
                }

            case ControlKinds.Refund:
                {
                    var staged = await refunds.StageAsync(request.Subject, request.Reason, request.RequestedBy, cancellationToken);
                    if (!staged.IsSuccess)
                    {
                        return Result.Failure(staged.Error!.Code, staged.Error.Message);
                    }

                    outcome.ResultRef = staged.Value!.Id;
                    break;
                }

            default:
                return await ReopenAsync(request, outcome, cancellationToken);
        }

        db.ControlDecisions.Add(outcome);
        return await TrySaveAsync(cancellationToken) ? Result.Success() : Result.Failure("ALREADY_DECIDED", "This request has already been decided.");
    }

    private static Money Finance_Money(AdjustmentLinePayload line) => KJWebsite.BuildingBlocks.Finance.Money.Parse(line.Amount, line.Currency).Value;

    /// <summary>
    /// Reopening removes a close row, which the database allows only when an approved reopen by a different person is already recorded, so the event and the decision are saved first and
    /// the removal second, in one transaction.
    /// </summary>
    private async Task<Result> ReopenAsync(ControlRequestEntity request, ControlDecisionEntity outcome, CancellationToken cancellationToken)
    {
        var period = request.Kind == ControlKinds.PeriodReopen;
        PeriodCloseEntity? periodClose = null;
        FiscalYearCloseEntity? yearClose = null;
        if (period)
        {
            periodClose = await db.PeriodCloses.FirstOrDefaultAsync(c => c.PeriodKey == request.Subject, cancellationToken);
        }
        else
        {
            yearClose = await db.FiscalYearCloses.FirstOrDefaultAsync(c => c.Label == request.Subject, cancellationToken);
        }

        if (periodClose is null && yearClose is null)
        {
            return Result.Failure("NOT_CLOSED", "It is not closed any more.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        outcome.ResultRef = (period ? "period:" : "year:") + request.Subject;
        db.ControlDecisions.Add(outcome);
        db.CloseEvents.Add(new CloseEventEntity { Scope = period ? "period" : "year", Key = request.Subject, Kind = "reopened", By = request.RequestedBy, ApprovedBy = outcome.DecidedBy, Reason = request.Reason, At = Now });
        if (!await TrySaveAsync(cancellationToken))
        {
            return Result.Failure("ALREADY_DECIDED", "This request has already been decided.");
        }

        var close = period ? (object)(await db.PeriodCloses.FirstAsync(c => c.PeriodKey == request.Subject, cancellationToken)) : await db.FiscalYearCloses.FirstAsync(c => c.Label == request.Subject, cancellationToken);
        db.Remove(close);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Result.Success();
    }

    // ---- reading -----------------------------------------------------------------------------------------------------------------------------------------

    private IQueryable<ControlRequestEntity> Filtered(string? status, string? kind)
    {
        var query = db.ControlRequests.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(kind))
        {
            query = query.Where(r => r.Kind == kind);
        }

        return status switch
        {
            "pending" => query.Where(r => !db.ControlDecisions.Any(d => d.RequestId == r.Id)),
            "approved" or "rejected" or "withdrawn" => query.Where(r => db.ControlDecisions.Any(d => d.RequestId == r.Id && d.Decision == status)),
            _ => query
        };
    }

    public async Task<(IReadOnlyList<ControlRequestView> Items, int Total)> ListAsync(string? status, string? kind, int limit, int offset, CancellationToken cancellationToken)
    {
        // Requests are few and SQLite cannot order a DateTimeOffset, so they are ordered and paged in memory.
        var all = (await Filtered(status, kind).ToListAsync(cancellationToken)).OrderByDescending(r => r.RequestedAt).ToList();
        return (await ViewsAsync(all.Skip(offset).Take(limit).ToList(), cancellationToken), all.Count);
    }

    public async Task<ControlRequestView?> GetAsync(string id, CancellationToken cancellationToken) =>
        await ViewsAsync(await db.ControlRequests.AsNoTracking().Where(r => r.Id == id).ToListAsync(cancellationToken), cancellationToken) is { Count: > 0 } found ? found[0] : null;

    private async Task<IReadOnlyList<ControlRequestView>> ViewsAsync(IReadOnlyList<ControlRequestEntity> requests, CancellationToken cancellationToken)
    {
        var ids = requests.Select(r => r.Id).ToList();
        var decisions = await db.ControlDecisions.AsNoTracking().Where(d => ids.Contains(d.RequestId)).ToDictionaryAsync(d => d.RequestId, cancellationToken);
        var sources = requests.Where(r => r.Kind == ControlKinds.Adjustment).Select(r => "control:" + r.Id).ToList();
        var postingBySource = await db.Postings.AsNoTracking().Where(p => p.SourceType == PostingSources.Adjustment && sources.Contains(p.SourceId!)).Select(p => new { p.SourceId, p.Id }).ToDictionaryAsync(p => p.SourceId!, p => p.Id, cancellationToken);
        var reversedIds = requests.Where(r => r.Kind == ControlKinds.PostingReversal).Select(r => long.Parse(r.Subject, CultureInfo.InvariantCulture)).ToList();
        var reversalBySubject = await db.Postings.AsNoTracking().Where(p => p.ReversesPostingId != null && reversedIds.Contains(p.ReversesPostingId.Value)).Select(p => new { Original = p.ReversesPostingId!.Value, p.Id }).ToDictionaryAsync(p => p.Original, p => p.Id, cancellationToken);

        return
        [
            .. requests.Select(r =>
            {
                decisions.TryGetValue(r.Id, out var d);
                var approved = d?.Decision == ControlDecisions.Approved;
                string? posting = null;
                if (approved && r.Kind == ControlKinds.Adjustment && postingBySource.TryGetValue("control:" + r.Id, out var adjusted))
                {
                    posting = adjusted.ToString(CultureInfo.InvariantCulture);
                }
                else if (approved && r.Kind == ControlKinds.PostingReversal && reversalBySubject.TryGetValue(long.Parse(r.Subject, CultureInfo.InvariantCulture), out var reversal))
                {
                    posting = reversal.ToString(CultureInfo.InvariantCulture);
                }

                return new ControlRequestView(r.Id, r.Kind, r.Subject, r.Reason, r.RequestedBy, r.RequestedAt, d?.Decision ?? "pending",
                    d is null ? null : new ControlDecisionView(d.Decision, d.DecidedBy, d.Note, d.At), posting, approved && r.Kind == ControlKinds.Refund ? d!.ResultRef : null, JsonDocument.Parse(r.PayloadJson).RootElement.Clone());
            })
        ];
    }

    // ---- refund reconciliation -----------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Refund requests with what the gateway says about each: an approved refund is `awaiting_gateway` until a signed refund notice for the donation arrives, then `reconciled` when the
    /// notice's amount and currency equal the donation's (or it carries no amount) and `amount_mismatch` when they differ. Notices for donations with no approved refund are listed apart.
    /// </summary>
    public async Task<(IReadOnlyList<RefundRow> Refunds, IReadOnlyList<UnexpectedNotice> Unexpected)> RefundsAsync(CancellationToken cancellationToken)
    {
        var requests = (await db.ControlRequests.AsNoTracking().Where(r => r.Kind == ControlKinds.Refund).ToListAsync(cancellationToken)).OrderByDescending(r => r.RequestedAt).ToList();
        var decisions = await db.ControlDecisions.AsNoTracking().Where(d => db.ControlRequests.Any(r => r.Id == d.RequestId && r.Kind == ControlKinds.Refund)).ToDictionaryAsync(d => d.RequestId, cancellationToken);
        var donationIds = requests.Select(r => r.Subject).ToList();
        var donations = await db.Donations.AsNoTracking().Where(d => donationIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, cancellationToken);
        var notices = await db.ProviderNotices.AsNoTracking().Where(n => n.Kind == NoticeKinds.Refund).ToListAsync(cancellationToken);
        var noticeByDonation = notices.GroupBy(n => n.DonationId).ToDictionary(g => g.Key, g => g.OrderBy(n => n.Id).First());

        var rows = requests.Select(r =>
        {
            decisions.TryGetValue(r.Id, out var d);
            donations.TryGetValue(r.Subject, out var donation);
            var status = d?.Decision ?? "pending";
            var reconciliation = "n/a";
            ProviderNoticeEntity? notice = null;
            if (status == ControlDecisions.Approved)
            {
                if (!noticeByDonation.TryGetValue(r.Subject, out notice))
                {
                    reconciliation = "awaiting_gateway";
                }
                else
                {
                    var fits = (notice.Amount is null || donation is null || notice.Amount == donation.Amount) && (notice.Currency is null || donation is null || notice.Currency == donation.Currency);
                    reconciliation = fits ? "reconciled" : "amount_mismatch";
                }
            }

            return new RefundRow(r.Id, r.Subject, donation?.Reference ?? string.Empty, Money(donation?.Amount ?? 0m), donation?.Currency ?? string.Empty, r.RequestedBy, r.RequestedAt, status, d?.DecidedBy,
                reconciliation, notice?.Gateway, notice?.Amount is { } a ? Money(a) : null);
        }).ToList();

        var approved = rows.Where(r => r.Status == ControlDecisions.Approved).Select(r => r.DonationId).ToHashSet(StringComparer.Ordinal);
        var stray = notices.Where(n => !approved.Contains(n.DonationId)).ToList();
        var references = await db.Donations.AsNoTracking().Where(d => stray.Select(n => n.DonationId).Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.Reference, cancellationToken);
        var unexpected = stray.Select(n => new UnexpectedNotice(n.Id.ToString(CultureInfo.InvariantCulture), n.Gateway, n.DonationId, references.GetValueOrDefault(n.DonationId), n.Amount is { } a ? Money(a) : null, n.Currency, n.At,
            rows.Any(r => r.DonationId == n.DonationId && r.Status == "pending") ? "A refund request exists but is not approved yet." : "The gateway reports a refund that finance never approved.")).ToList();
        return (rows, unexpected);
    }
}
