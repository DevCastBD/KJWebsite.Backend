namespace FinanceService.Data.Entities;

public static class ConsentPurposes
{
    /// <summary>Thank-you messages beyond the receipt itself.</summary>
    public const string Thanks = "thanks";

    /// <summary>News about what the donations achieved.</summary>
    public const string Updates = "updates";

    /// <summary>Fundraising asks. Never granted by the donation form.</summary>
    public const string Appeals = "appeals";

    public static readonly IReadOnlyList<string> All = [Thanks, Updates, Appeals];
}

public static class ConsentSources
{
    public const string DonationForm = "donation_form";
    public const string Admin = "admin";
    public const string DonorRequest = "donor_request";

    public static readonly IReadOnlyList<string> All = [DonationForm, Admin, DonorRequest];
}

/// <summary>
/// A person who has given, recognised by the keyed hash of their email. Only non-anonymous gifts with an email address create or touch one: an anonymous gift leaves no
/// attributable record. Contact details are encrypted and refreshed from the latest gift. Giving history and totals are not stored here; they are derived from receipts
/// so they cannot drift from the books.
/// </summary>
public sealed class DonorEntity
{
    public string Id { get; set; } = string.Empty;
    public string DonorKey { get; set; } = string.Empty;
    public string NameProtected { get; set; } = string.Empty;
    public string EmailProtected { get; set; } = string.Empty;
    public string? PhoneProtected { get; set; }
    public string Language { get; set; } = "en";
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A grant or withdrawal of consent for one purpose. Append-only: the current state is the latest row per purpose, and nothing is assumed in its absence.</summary>
public sealed class DonorConsentEntity
{
    public long Id { get; set; }
    public string DonorId { get; set; } = string.Empty;
    public string Purpose { get; set; } = ConsentPurposes.Thanks;
    public bool Granted { get; set; }
    public string Source { get; set; } = ConsentSources.Admin;
    public string? RecordedBy { get; set; }
    public DateTimeOffset At { get; set; }
}

/// <summary>A message that was not queued because the donor had not consented to its purpose. Kept so it is provable that nothing went out without consent.</summary>
public sealed class DonorSuppressionEntity
{
    public long Id { get; set; }
    public string DonorId { get; set; } = string.Empty;
    public string Template { get; set; } = string.Empty;
    public string Purpose { get; set; } = string.Empty;
    public DateTimeOffset At { get; set; }
}

/// <summary>One export of donor data: who took it, which segment, as of when, with which consent filter, and how many rows. Append-only.</summary>
public sealed class DonorExportEntity
{
    public long Id { get; set; }
    public string Segment { get; set; } = string.Empty;
    public DateOnly AsOf { get; set; }
    public string? ConsentFilter { get; set; }
    public int Rows { get; set; }
    public string ExportedBy { get; set; } = string.Empty;
    public DateTimeOffset At { get; set; }
}
