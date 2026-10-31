using System.Text.Json;
using FinanceService.Data;
using FinanceService.Data.Entities;
using FinanceService.Donations;
using FinanceService.Donors;
using FinanceService.Receipts;
using Microsoft.EntityFrameworkCore;

namespace FinanceService.Donors;

public enum SendOutcome
{
    Queued,
    Suppressed
}

/// <summary>
/// The one place that decides whether a donor may be written to. A message that is not a receipt is queued only when the donor's latest word for its purpose is a grant;
/// otherwise a suppression row records that it was held back. Nothing assumes consent, and the donation form can never grant appeals.
/// </summary>
public sealed class DonorMessenger(FinanceDbContext db, IDonorProtector protector, TimeProvider clock)
{
    public static string PurposeOf(string template) => template switch
    {
        "donor.welcome_thanks" or "donor.thank_you" => ConsentPurposes.Thanks,
        "donor.update" => ConsentPurposes.Updates,
        "donor.appeal" => ConsentPurposes.Appeals,
        _ => throw new ArgumentOutOfRangeException(nameof(template), $"Unknown donor message template '{template}'.")
    };

    /// <summary>The donor's current consent per purpose. <paramref name="staged"/> are grants added in this unit of work and not yet saved: they win over what is stored.</summary>
    public async Task<bool> IsGrantedAsync(string donorId, string purpose, IEnumerable<DonorConsentEntity>? staged = null, CancellationToken cancellationToken = default)
    {
        var pending = staged?.LastOrDefault(c => c.DonorId == donorId && c.Purpose == purpose);
        if (pending is not null)
        {
            return pending.Granted;
        }

        var latest = await db.DonorConsents.AsNoTracking().Where(c => c.DonorId == donorId && c.Purpose == purpose).OrderByDescending(c => c.Id).Select(c => (bool?)c.Granted).FirstOrDefaultAsync(cancellationToken);
        return latest == true;
    }

    /// <summary>Stages (does not save) the message or its suppression row.</summary>
    public async Task<SendOutcome> StageAsync(DonorEntity donor, string template, object payload, IEnumerable<DonorConsentEntity>? staged = null, CancellationToken cancellationToken = default)
    {
        var purpose = PurposeOf(template);
        var now = clock.GetUtcNow();
        if (!await IsGrantedAsync(donor.Id, purpose, staged, cancellationToken))
        {
            db.DonorSuppressions.Add(new DonorSuppressionEntity { DonorId = donor.Id, Template = template, Purpose = purpose, At = now });
            return SendOutcome.Suppressed;
        }

        db.Notifications.Add(new FinanceNotificationEntity
        {
            Template = template,
            Language = donor.Language,
            RecipientProtected = donor.EmailProtected,
            PayloadProtected = protector.Protect(JsonSerializer.Serialize(payload)),
            CreatedAt = now
        });
        return SendOutcome.Queued;
    }
}

/// <summary>Keeps the donor record in step with confirmed gifts, in the same commit as the confirmation.</summary>
public sealed class DonorService(FinanceDbContext db, IDonorProtector protector, DonorMessenger messenger, TimeProvider clock)
{
    /// <summary>
    /// Creates or refreshes the donor for a confirmed gift. An anonymous gift, or one with no email to recognise the donor by, creates and touches nothing. Consent is only ever
    /// recorded from what the donor actually ticked, and only for thanks and updates. A thank-you is queued through the consent gate: the first gift gets the welcome variant.
    /// </summary>
    public async Task StageAsync(DonationEntity donation, ReceiptEntity receipt, CancellationToken cancellationToken)
    {
        if (donation.IsAnonymous || protector.Unprotect(donation.DonorEmailProtected).Trim().Length == 0)
        {
            return;
        }

        var now = clock.GetUtcNow();
        var donor = await db.Donors.FirstOrDefaultAsync(d => d.DonorKey == receipt.DonorKey, cancellationToken);
        var firstGift = true;
        if (donor is null)
        {
            donor = new DonorEntity
            {
                Id = PublicDonationEndpoints.NewId("dnr"),
                DonorKey = receipt.DonorKey,
                NameProtected = donation.DonorNameProtected,
                EmailProtected = donation.DonorEmailProtected,
                PhoneProtected = donation.DonorPhoneProtected,
                Language = donation.Language,
                CreatedAt = now
            };
            db.Donors.Add(donor);
        }
        else
        {
            // Contact details follow the latest gift.
            donor.NameProtected = donation.DonorNameProtected;
            donor.EmailProtected = donation.DonorEmailProtected;
            donor.PhoneProtected = donation.DonorPhoneProtected ?? donor.PhoneProtected;
            donor.Language = donation.Language;
            firstGift = !await db.Receipts.AnyAsync(r => r.DonorKey == receipt.DonorKey && !r.IsAnonymous && r.DonationId != donation.Id
                && !db.ReceiptVoids.Any(v => v.ReceiptId == r.Id), cancellationToken);
        }

        var staged = new List<DonorConsentEntity>();
        if (donation.CommunicationConsent)
        {
            foreach (var purpose in new[] { ConsentPurposes.Thanks, ConsentPurposes.Updates })
            {
                if (!await messenger.IsGrantedAsync(donor.Id, purpose, staged, cancellationToken))
                {
                    var grant = new DonorConsentEntity { DonorId = donor.Id, Purpose = purpose, Granted = true, Source = ConsentSources.DonationForm, At = now };
                    staged.Add(grant);
                    db.DonorConsents.Add(grant);
                }
            }
        }

        await messenger.StageAsync(donor, firstGift ? "donor.welcome_thanks" : "donor.thank_you", new { receipt = receipt.Number, donationReference = donation.Reference }, staged, cancellationToken);
    }
}
