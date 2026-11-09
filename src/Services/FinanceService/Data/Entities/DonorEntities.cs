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

public static class DonorTypes
{
    public const string Individual = "individual";
    public const string Organisation = "organisation";

    public static readonly IReadOnlyList<string> All = [Individual, Organisation];
}

public static class DonorStatuses
{
    public const string Active = "active";
    public const string Inactive = "inactive";

    public static readonly IReadOnlyList<string> All = [Active, Inactive];
}

public static class AccountStatuses
{
    public const string None = "none";
    public const string Active = "active";
    public const string Disabled = "disabled";

    public static readonly IReadOnlyList<string> All = [None, Active, Disabled];
}

/// <summary>
/// A person who has given, recognised by the keyed hash of their email. Only non-anonymous gifts with an email address create or touch one: an anonymous gift leaves no
/// attributable record. Contact details are encrypted and refreshed from the latest gift. Giving history and totals are not stored here; they are derived from receipts
/// so they cannot drift from the books.
/// </summary>
public sealed class DonorEntity
{
    public string Id { get; set; } = string.Empty;
    /// <summary>
    /// The key every receipt of this donor carries. It is fixed when the donor is created and never changes, so history stays attached when the
    /// email does. It is the hash of the first email unless another donor already holds that key.
    /// </summary>
    public string DonorKey { get; set; } = string.Empty;

    /// <summary>The keyed hash of the CURRENT email (unique): what a new gift is matched to. Changes when the email changes; DonorKey does not.</summary>
    public string EmailKey { get; set; } = string.Empty;
    public string NameProtected { get; set; } = string.Empty;
    public string EmailProtected { get; set; } = string.Empty;
    public string? PhoneProtected { get; set; }
    public string Language { get; set; } = "en";
    public DateTimeOffset CreatedAt { get; set; }
    public string Type { get; set; } = DonorTypes.Individual;
    public string? Organisation { get; set; }
    public string? AddressProtected { get; set; }
    public string? Country { get; set; }
    public string Status { get; set; } = DonorStatuses.Active;
    public string? NotesProtected { get; set; }

    /// <summary>The Identity account (subject) of the donor's sign-in, when staff have given them one.</summary>
    public string? AccountSubject { get; set; }

    /// <summary>True when the sign-in is also a member account: its password and state belong to membership staff.</summary>
    public bool AccountShared { get; set; }

    /// <summary>What Finance last set: none, active or disabled. The must-change flag is not kept here; it is read from Identity.</summary>
    public string AccountStatus { get; set; } = AccountStatuses.None;

    /// <summary>Changes with every edit; it is the ETag, and a concurrency token so two edits cannot both win.</summary>
    public int Version { get; set; } = 1;
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
