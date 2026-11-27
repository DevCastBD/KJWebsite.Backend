namespace MembershipService.Data.Entities;

/// <summary>A beneficiary institution (school, madrasa, mission). The address is encrypted: where children are present is sensitive.</summary>
public sealed class BeneficiaryEntity
{
    public string Id { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;

    /// <summary>`school`, `madrasa`, `mission` or `other`.</summary>
    public string Kind { get; set; } = "school";

    public string NameEn { get; set; } = string.Empty;
    public string NameBn { get; set; } = string.Empty;
    public string? AddressProtected { get; set; }

    /// <summary>Coarse area (district). The only location detail that may appear in aggregate figures.</summary>
    public string District { get; set; } = string.Empty;

    public bool IsArchived { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>An institution taking part in a project, with how many of its students the project reaches.</summary>
public sealed class ProgrammeEnrolmentEntity
{
    public string Id { get; set; } = string.Empty;
    public string ProjectSlug { get; set; } = string.Empty;
    public string BeneficiaryId { get; set; } = string.Empty;
    public int Students { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>A memorandum of understanding. The signatory's name is encrypted; the scanned document sits in the attachment store behind permission-gated endpoints only.</summary>
public sealed class MouEntity
{
    public string Id { get; set; } = string.Empty;
    public string BeneficiaryId { get; set; } = string.Empty;
    public DateOnly SignedOn { get; set; }
    public DateOnly? ValidUntil { get; set; }
    public string SignatoryProtected { get; set; } = string.Empty;
    public string? SignatoryRole { get; set; }
    public string? DocumentKey { get; set; }
    public string? DocumentContentType { get; set; }
    public string? DocumentFileName { get; set; }
    public long? DocumentBytes { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// One class or session delivered. Sessions are logged in the field, often late and in batches, so each carries the client's own id:
/// sending it again changes nothing.
/// </summary>
public sealed class SessionLogEntity
{
    public string Id { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string ProjectSlug { get; set; } = string.Empty;
    public string BeneficiaryId { get; set; } = string.Empty;
    public DateOnly HeldOn { get; set; }
    public string Topic { get; set; } = string.Empty;
    public int Attendance { get; set; }
    public int DurationMinutes { get; set; }
    public string LoggedBy { get; set; } = string.Empty;
    public DateTimeOffset LoggedAt { get; set; }
}

/// <summary>A measured outcome for a project and period. Definitions (which keys exist and what they mean) are agreed once in the impact metrics work (#89).</summary>
public sealed class ProgrammeOutcomeEntity
{
    public string Id { get; set; } = string.Empty;
    public string ProjectSlug { get; set; } = string.Empty;
    public string MetricKey { get; set; } = string.Empty;
    public DateOnly PeriodStart { get; set; }
    public DateOnly PeriodEnd { get; set; }
    public double Value { get; set; }
    public string Unit { get; set; } = string.Empty;
    public string? Note { get; set; }
    public string RecordedBy { get; set; } = string.Empty;
    public DateTimeOffset RecordedAt { get; set; }
}
