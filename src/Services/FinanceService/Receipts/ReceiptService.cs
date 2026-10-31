using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FinanceService.Data;
using FinanceService.Data.Entities;
using FinanceService.Donations;
using FinanceService.Ledger;
using KJWebsite.BuildingBlocks.Finance;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FinanceService.Receipts;

public sealed class OrganisationDetails
{
    public string NameEn { get; set; } = "Kolpojontro";
    public string NameBn { get; set; } = "কল্পজন্ত্র";

    /// <summary>The registration number and the rest of what a receipt must carry depend on the organisation's legal status (#114, open): whatever is set here is printed, and nothing is invented.</summary>
    public string RegistrationNumber { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string Footer { get; set; } = string.Empty;
}

public sealed class ReceiptOptions
{
    public const string SectionName = "Finance:Receipts";

    /// <summary>Secret for the keyed hash that groups a donor's receipts. Required outside Development; changing it later splits donors across statements.</summary>
    public string DonorKeySecret { get; set; } = string.Empty;

    public OrganisationDetails Organisation { get; set; } = new();
}

/// <summary>Keyed hash of a donor's email: lets statements group one donor's receipts without decrypting anything, and cannot be reversed from a database dump.</summary>
public sealed class DonorKeyHasher
{
    private readonly byte[] _key;

    public DonorKeyHasher(IOptions<ReceiptOptions> options, IHostEnvironment environment)
    {
        var configured = options.Value.DonorKeySecret;
        if (string.IsNullOrWhiteSpace(configured) && !environment.IsDevelopment())
        {
            throw new InvalidOperationException("Finance:Receipts:DonorKeySecret is required outside Development.");
        }

        _key = Encoding.UTF8.GetBytes(string.IsNullOrWhiteSpace(configured) ? "kj-finance-development-donor-key" : configured);
    }

    public string Hash(string value) => Convert.ToHexString(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

public sealed record ChainCheck(string Series, int Count, bool Valid, IReadOnlyList<string> Problems);

/// <summary>
/// Issues receipts. A receipt is staged in the same commit as the confirmation it belongs to, so a confirmed donation always has exactly one and an unconfirmed one
/// never does. Its number is the series' highest plus one; the unique index decides a race, the loser's whole commit fails and is retried, and since nothing is ever
/// deleted a number can never be skipped. Each receipt is hashed together with the previous one's hash, so the chain proves nothing is missing or changed.
/// </summary>
public sealed class ReceiptService(FinanceDbContext db, IDonorProtector protector, DonorKeyHasher keys, IOptions<ReceiptOptions> options, IOptions<LedgerOptions> ledgerOptions, TimeProvider clock)
{
    public static readonly string GenesisHash = new('0', 64);

    public async Task StageIssueAsync(DonationEntity donation, string? method, CancellationToken cancellationToken)
    {
        if (await db.Receipts.AnyAsync(r => r.DonationId == donation.Id, cancellationToken))
        {
            return;
        }

        var series = FiscalCalendar.FiscalYear(donation.ReceivedOn, ledgerOptions.Value.FiscalYearStartMonth);
        var last = await db.Receipts.AsNoTracking().Where(r => r.Series == series).OrderByDescending(r => r.Seq).Select(r => new { r.Seq, r.Hash }).FirstOrDefaultAsync(cancellationToken);
        var seq = (last?.Seq ?? 0) + 1;
        var email = protector.Unprotect(donation.DonorEmailProtected).Trim();
        var offline = await db.OfflineRecords.AsNoTracking().FirstOrDefaultAsync(o => o.DonationId == donation.Id, cancellationToken);
        var now = clock.GetUtcNow();

        var receipt = new ReceiptEntity
        {
            Id = PublicDonationEndpoints.NewId("rcp"),
            DonationId = donation.Id,
            Series = series,
            Seq = seq,
            Number = $"KJR-{series}-{seq.ToString("D6", CultureInfo.InvariantCulture)}",
            PrevHash = last?.Hash ?? GenesisHash,
            IssuedAt = now,
            ReceivedOn = donation.ReceivedOn,
            DonorNameProtected = donation.DonorNameProtected,
            DonorEmailProtected = donation.DonorEmailProtected,
            // A donor with no email cannot be recognised across gifts, so each such gift stands alone.
            DonorKey = keys.Hash(email.Length == 0 ? $"donation:{donation.Id}" : $"email:{email.ToLowerInvariant()}"),
            Amount = donation.Amount,
            FeeAmount = donation.FeeAmount,
            Currency = donation.Currency,
            Method = method,
            DesignationType = donation.DesignationType,
            DesignationId = donation.DesignationId,
            IsAnonymous = donation.IsAnonymous,
            Language = donation.Language,
            IsInKind = offline?.Method == OfflineMethods.InKind,
            ItemDescription = offline?.ItemDescription,
            ValuationBasis = offline?.ValuationBasis,
            OrganisationJson = JsonSerializer.Serialize(options.Value.Organisation)
        };
        receipt.Hash = ComputeHash(receipt);
        db.Receipts.Add(receipt);

        if (email.Length > 0)
        {
            db.Notifications.Add(new FinanceNotificationEntity
            {
                Template = "donation.receipt",
                Language = donation.Language,
                RecipientProtected = donation.DonorEmailProtected,
                PayloadProtected = protector.Protect(JsonSerializer.Serialize(new { number = receipt.Number, reference = donation.Reference })),
                CreatedAt = now
            });
        }
    }

    /// <summary>The hash covers everything that identifies the receipt and the one before it, in a fixed text form.</summary>
    public static string ComputeHash(ReceiptEntity r) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|',
        "v1", r.Number, r.Seq.ToString(CultureInfo.InvariantCulture), r.PrevHash, r.DonationId, r.Amount.ToString("0.00", CultureInfo.InvariantCulture), r.Currency,
        r.ReceivedOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), r.DonorKey, r.IssuedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture))))).ToLowerInvariant();

    /// <summary>Walks a series in order and reports any gap, broken link or altered receipt.</summary>
    public async Task<ChainCheck> VerifyAsync(string series, CancellationToken cancellationToken)
    {
        var rows = (await db.Receipts.AsNoTracking().Where(r => r.Series == series).ToListAsync(cancellationToken)).OrderBy(r => r.Seq).ToList();
        var problems = new List<string>();
        var previousHash = GenesisHash;
        long expected = 1;
        foreach (var row in rows)
        {
            if (row.Seq != expected)
            {
                problems.Add($"Gap before {row.Number}: expected sequence {expected}.");
                expected = row.Seq;
            }

            if (row.PrevHash != previousHash)
            {
                problems.Add($"{row.Number} does not follow the previous receipt.");
            }

            if (row.Hash != ComputeHash(row))
            {
                problems.Add($"{row.Number} does not match its hash: it was altered.");
            }

            previousHash = row.Hash;
            expected++;
        }

        return new ChainCheck(series, rows.Count, problems.Count == 0, problems);
    }

    public async Task<ReceiptDocument?> DocumentForAsync(ReceiptEntity receipt, CancellationToken cancellationToken)
    {
        var voided = await db.ReceiptVoids.AnyAsync(v => v.ReceiptId == receipt.Id, cancellationToken);
        var copies = await db.ReceiptReissues.CountAsync(r => r.ReceiptId == receipt.Id, cancellationToken);
        var organisation = JsonSerializer.Deserialize<OrganisationDetails>(receipt.OrganisationJson) ?? new OrganisationDetails();
        return new ReceiptDocument(
            receipt.Number, DateOnly.FromDateTime(receipt.IssuedAt.UtcDateTime), receipt.ReceivedOn, protector.Unprotect(receipt.DonorNameProtected),
            Money.Of(receipt.Amount, receipt.Currency), Money.Of(receipt.FeeAmount, receipt.Currency), receipt.Method, receipt.DesignationType, receipt.DesignationId,
            receipt.IsInKind, receipt.ItemDescription, receipt.ValuationBasis, organisation, copies, voided, receipt.Hash[..12]);
    }

    public string DonorName(ReceiptEntity receipt) => protector.Unprotect(receipt.DonorNameProtected);

    public string DonorEmail(ReceiptEntity receipt) => protector.Unprotect(receipt.DonorEmailProtected);
}
