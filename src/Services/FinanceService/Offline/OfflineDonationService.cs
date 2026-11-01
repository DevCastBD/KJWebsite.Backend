using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using FinanceService.Data;
using FinanceService.Data.Entities;
using FinanceService.Donations;
using FinanceService.Ledger;
using KJWebsite.BuildingBlocks;
using KJWebsite.BuildingBlocks.Finance;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FinanceService.Offline;

/// <summary>
/// Gifts that arrive outside the gateways, under maker-checker: one person records, a different person approves, and only the approval posts to the ledger.
/// The rule that the two differ is enforced here, on the server, from the signed-in user ids, whatever the client does. Everything is append-only: a mistake is
/// rejected and recorded again, a proof is never removed.
/// </summary>
public sealed partial class OfflineDonationService(
    FinanceDbContext db, IDonorProtector protector, IProofStore proofs, DonationConfirmation confirmation, IOptions<ProofOptions> proofOptions, TimeProvider clock)
{
    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex Email();

    [GeneratedRegex(@"^\+?[0-9 ()-]{6,30}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Phone();

    [GeneratedRegex(@"^[a-z0-9]+(?:-[a-z0-9]+)*\z", RegexOptions.CultureInvariant)]
    private static partial Regex Slug();

    private DateOnly Today => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

    public async Task<Result<OfflineDonationCreated>> RecordAsync(OfflineDonationRequest request, string actor, CancellationToken cancellationToken)
    {
        var invalid = new List<string>();
        var today = Today;

        var method = request.Method?.Trim();
        if (method is null || !OfflineMethods.All.Contains(method))
        {
            invalid.Add("method");
        }

        var amount = Money.Parse(request.Amount, request.Currency);
        if (!amount.IsSuccess || !amount.Value.IsPositive)
        {
            invalid.Add("amount");
        }

        var receivedOn = today;
        if (!string.IsNullOrWhiteSpace(request.Date) && (!DateOnly.TryParseExact(request.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out receivedOn)
            || receivedOn > today || receivedOn < today.AddYears(-2)))
        {
            invalid.Add("date");
        }

        var (designationType, designationId) = (Designations.General, (string?)null);
        if (!string.IsNullOrWhiteSpace(request.Designation))
        {
            var parts = request.Designation.Trim().Split(':', 2);
            designationType = parts[0];
            designationId = parts.Length > 1 ? parts[1] : null;
            var ok = designationType == Designations.General ? designationId is null
                : Designations.All.Contains(designationType) && designationId is { Length: > 0 and <= 100 } && Slug().IsMatch(designationId);
            if (!ok)
            {
                invalid.Add("designation");
            }
        }

        var name = request.DonorName?.Trim();
        if (name is not { Length: > 0 and <= 120 })
        {
            invalid.Add("donorName");
        }

        var email = string.IsNullOrWhiteSpace(request.Email) ? null : request.Email.Trim();
        if (email is not null && (email.Length > 254 || !Email().IsMatch(email)))
        {
            invalid.Add("email");
        }

        var phone = string.IsNullOrWhiteSpace(request.Phone) ? null : request.Phone.Trim();
        if (phone is not null && !Phone().IsMatch(phone))
        {
            invalid.Add("phone");
        }

        var dedication = string.IsNullOrWhiteSpace(request.Dedication) ? null : request.Dedication.Trim();
        if (dedication is { Length: > 500 })
        {
            invalid.Add("dedication");
        }

        var referenceNo = string.IsNullOrWhiteSpace(request.ReferenceNo) ? null : request.ReferenceNo.Trim();
        if (referenceNo is { Length: > 100 })
        {
            invalid.Add("referenceNo");
        }

        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        if (note is { Length: > 1000 })
        {
            invalid.Add("note");
        }

        string? basis = null, valuationNote = null, item = null;
        if (method == OfflineMethods.InKind)
        {
            // A gift in kind is worth what someone says it is worth, so the saying and its basis are part of the record.
            basis = request.Valuation?.Basis?.Trim();
            item = request.Valuation?.ItemDescription?.Trim();
            valuationNote = string.IsNullOrWhiteSpace(request.Valuation?.Note) ? null : request.Valuation!.Note!.Trim();
            if (basis is null || !ValuationBases.All.Contains(basis))
            {
                invalid.Add("valuation.basis");
            }

            if (item is not { Length: > 0 and <= 500 })
            {
                invalid.Add("valuation.itemDescription");
            }

            if (valuationNote is { Length: > 500 } || (basis == ValuationBases.Other && valuationNote is null))
            {
                invalid.Add("valuation.note");
            }
        }
        else if (request.Valuation is not null)
        {
            invalid.Add("valuation");
        }

        if (!invalid.Contains("designation") && designationType == Designations.Campaign && !await FinanceService.Campaigns.CampaignRules.AcceptsRecordedGiftAsync(db, designationId!))
        {
            invalid.Add("designation");
        }

        if (invalid.Count > 0)
        {
            return Result.Failure<OfflineDonationCreated>("VALIDATION_ERROR", $"Invalid fields: {string.Join(", ", invalid)}.");
        }

        var now = clock.GetUtcNow();
        var donation = new DonationEntity
        {
            Id = PublicDonationEndpoints.NewId("don"),
            Reference = await PublicDonationEndpoints.NewReferenceAsync(db),
            Kind = DonationKinds.OneTime,
            Amount = amount.Value.Amount,
            FeeAmount = 0m,
            Currency = amount.Value.Currency,
            DesignationType = designationType,
            DesignationId = designationId,
            IsAnonymous = request.Anonymous,
            Dedication = dedication,
            DonorNameProtected = protector.Protect(name!),
            DonorEmailProtected = protector.Protect(email ?? string.Empty),
            DonorPhoneProtected = phone is null ? null : protector.Protect(phone),
            Language = "en",
            CommunicationConsent = request.CommunicationConsent,
            CreatedBy = actor,
            CreatedAt = now,
            ReceivedOn = receivedOn
        };
        donation.Events.Add(new DonationEventEntity { Status = DonationStatuses.Pending, Note = "Recorded offline; awaiting approval", At = now });
        var record = new OfflineRecordEntity
        {
            Id = PublicDonationEndpoints.NewId("off"),
            DonationId = donation.Id,
            Method = method!,
            ReceivedOn = receivedOn,
            ReferenceNo = referenceNo,
            Note = note,
            ItemDescription = item,
            ValuationBasis = basis,
            ValuationNote = valuationNote,
            RecordedBy = actor,
            RecordedAt = now
        };
        db.Donations.Add(donation);
        db.OfflineRecords.Add(record);
        await db.SaveChangesAsync(cancellationToken);

        var view = await ViewAsync(db, donation.Id, cancellationToken) ?? throw new InvalidOperationException("The record could not be read back.");
        return Result.Success(new OfflineDonationCreated(
            donation.Id, donation.Reference, PublicDonationEndpoints.FormatDate(receivedOn), name!, donation.IsAnonymous, PublicDonationEndpoints.Format(donation.Amount), "0.00",
            donation.Currency, method, true, designationId is null ? designationType : $"{designationType}:{designationId}", donation.Kind, DonationStatuses.Pending, view));
    }

    public async Task<Result<ProofView>> AddProofAsync(string donationId, Stream content, string? contentType, string? fileName, string actor, CancellationToken cancellationToken)
    {
        var record = await db.OfflineRecords.AsNoTracking().FirstOrDefaultAsync(r => r.DonationId == donationId, cancellationToken);
        if (record is null)
        {
            return Result.Failure<ProofView>("NOT_FOUND", "Offline donation not found.");
        }

        if (await db.OfflineDecisions.AnyAsync(d => d.OfflineRecordId == record.Id, cancellationToken))
        {
            return Result.Failure<ProofView>("ALREADY_DECIDED", "Proof can only be added while the donation awaits a decision.");
        }

        if (await db.OfflineProofs.CountAsync(p => p.OfflineRecordId == record.Id, cancellationToken) >= ProofRules.MaxPerRecord)
        {
            return Result.Failure<ProofView>("TOO_MANY_PROOFS", $"At most {ProofRules.MaxPerRecord} proof files per donation.");
        }

        var prepared = await ProofIntake.PrepareAsync(content, contentType, fileName, cancellationToken);
        if (!prepared.IsSuccess)
        {
            return Result.Failure<ProofView>(prepared.Error!.Code, prepared.Error.Message);
        }

        var key = Guid.NewGuid().ToString("N");
        using (var stream = new MemoryStream(prepared.Value!.Bytes))
        {
            await proofs.SaveAsync(key, stream, cancellationToken);
        }

        var now = clock.GetUtcNow();
        var entity = new OfflineProofEntity
        {
            Id = PublicDonationEndpoints.NewId("prf"),
            OfflineRecordId = record.Id,
            FileName = prepared.Value.FileName,
            ContentType = prepared.Value.ContentType,
            SizeBytes = prepared.Value.Bytes.Length,
            Sha256 = prepared.Value.Sha256,
            StorageKey = key,
            UploadedBy = actor,
            UploadedAt = now,
            RetainUntil = DateOnly.FromDateTime(now.UtcDateTime).AddYears(Math.Max(1, proofOptions.Value.RetentionYears))
        };
        db.OfflineProofs.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success(ToView(entity));
    }

    /// <summary>
    /// Posts the gift: the recorder cannot approve their own entry, a bank or wallet gift needs at least one proof, and the confirmation, its ledger posting
    /// and the decision are saved in one commit. Approving again is harmless; approving something already rejected is refused.
    /// </summary>
    public async Task<Result<OfflineView>> ApproveAsync(string donationId, string actor, CancellationToken cancellationToken)
    {
        var record = await db.OfflineRecords.AsNoTracking().FirstOrDefaultAsync(r => r.DonationId == donationId, cancellationToken);
        if (record is null)
        {
            return Result.Failure<OfflineView>("NOT_FOUND", "Offline donation not found.");
        }

        var decision = await db.OfflineDecisions.AsNoTracking().FirstOrDefaultAsync(d => d.OfflineRecordId == record.Id, cancellationToken);
        if (decision is not null)
        {
            return decision.Decision == OfflineDecisions.Approved
                ? Result.Success((await ViewAsync(db, donationId, cancellationToken))!)
                : Result.Failure<OfflineView>("ALREADY_DECIDED", "This donation was rejected.");
        }

        if (string.Equals(record.RecordedBy, actor, StringComparison.Ordinal))
        {
            return Result.Failure<OfflineView>("SEPARATION_OF_DUTIES", "The person who recorded a donation cannot approve it; another finance user must.");
        }

        if (OfflineMethods.RequiresProof(record.Method) && !await db.OfflineProofs.AnyAsync(p => p.OfflineRecordId == record.Id, cancellationToken))
        {
            return Result.Failure<OfflineView>("PROOF_REQUIRED", $"A {record.Method} donation needs at least one proof document before it can be approved.");
        }

        // The gift is dated the day it arrived, unless that month is already closed, in which case it lands today.
        var period = FiscalCalendar.PeriodKey(record.ReceivedOn);
        var postedOn = await db.PeriodCloses.AnyAsync(c => c.PeriodKey == period, cancellationToken) ? Today : record.ReceivedOn;
        var now = clock.GetUtcNow();
        var result = await confirmation.ConfirmAsync(donationId, record.Method, $"Approved offline donation {record.Id}", postedOn,
            context => context.OfflineDecisions.Add(new OfflineDecisionEntity { OfflineRecordId = record.Id, Decision = OfflineDecisions.Approved, DecidedBy = actor, At = now }), cancellationToken);
        if (!result.IsSuccess)
        {
            return Result.Failure<OfflineView>(result.Error!.Code, result.Error.Message);
        }

        return Result.Success((await ViewAsync(db, donationId, cancellationToken))!);
    }

    public async Task<Result<OfflineView>> RejectAsync(string donationId, string? reason, string actor, CancellationToken cancellationToken)
    {
        var text = reason?.Trim();
        if (text is not { Length: >= 3 and <= 500 })
        {
            return Result.Failure<OfflineView>("VALIDATION_ERROR", "Invalid fields: reason (3-500 characters).");
        }

        var record = await db.OfflineRecords.AsNoTracking().FirstOrDefaultAsync(r => r.DonationId == donationId, cancellationToken);
        if (record is null)
        {
            return Result.Failure<OfflineView>("NOT_FOUND", "Offline donation not found.");
        }

        if (await db.OfflineDecisions.AnyAsync(d => d.OfflineRecordId == record.Id, cancellationToken))
        {
            return Result.Failure<OfflineView>("ALREADY_DECIDED", "This donation has already been decided.");
        }

        var now = clock.GetUtcNow();
        var marked = await confirmation.MarkAsync(donationId, DonationStatuses.Cancelled, $"Rejected: {text}",
            context => context.OfflineDecisions.Add(new OfflineDecisionEntity { OfflineRecordId = record.Id, Decision = OfflineDecisions.Rejected, DecidedBy = actor, Reason = text, At = now }),
            cancellationToken);
        if (!marked.IsSuccess || !marked.Value)
        {
            return Result.Failure<OfflineView>("ALREADY_DECIDED", "This donation can no longer be rejected.");
        }

        return Result.Success((await ViewAsync(db, donationId, cancellationToken))!);
    }

    public static async Task<OfflineView?> ViewAsync(FinanceDbContext db, string donationId, CancellationToken cancellationToken = default)
    {
        var record = await db.OfflineRecords.AsNoTracking().FirstOrDefaultAsync(r => r.DonationId == donationId, cancellationToken);
        if (record is null)
        {
            return null;
        }

        var decision = await db.OfflineDecisions.AsNoTracking().FirstOrDefaultAsync(d => d.OfflineRecordId == record.Id, cancellationToken);
        // Ordered here, not in SQL: SQLite (the test provider) cannot order by DateTimeOffset.
        var files = (await db.OfflineProofs.AsNoTracking().Where(p => p.OfflineRecordId == record.Id).ToListAsync(cancellationToken)).OrderBy(p => p.UploadedAt).ThenBy(p => p.Id, StringComparer.Ordinal).ToList();
        return new OfflineView(
            record.Id, record.Method, PublicDonationEndpoints.FormatDate(record.ReceivedOn), record.ReferenceNo, record.Note, record.RecordedBy, record.RecordedAt,
            decision?.Decision ?? "pending",
            record.ValuationBasis is null ? null : new ValuationView(record.ValuationBasis, record.ValuationNote, record.ItemDescription ?? string.Empty),
            decision is null ? null : new DecisionView(decision.Decision, decision.DecidedBy, decision.Reason, decision.At),
            [.. files.Select(ToView)], OfflineMethods.RequiresProof(record.Method));
    }

    public async Task<(OfflineProofEntity Proof, Stream Content)?> OpenProofAsync(string donationId, string proofId, CancellationToken cancellationToken)
    {
        var proof = await (from p in db.OfflineProofs.AsNoTracking()
                           join r in db.OfflineRecords.AsNoTracking() on p.OfflineRecordId equals r.Id
                           where r.DonationId == donationId && p.Id == proofId
                           select p).FirstOrDefaultAsync(cancellationToken);
        if (proof is null)
        {
            return null;
        }

        var stream = await proofs.OpenReadAsync(proof.StorageKey, cancellationToken);
        return stream is null ? null : (proof, stream);
    }

    private static ProofView ToView(OfflineProofEntity p) =>
        new(p.Id, p.FileName, p.ContentType, p.SizeBytes, p.Sha256, p.UploadedBy, p.UploadedAt, PublicDonationEndpoints.FormatDate(p.RetainUntil));
}
