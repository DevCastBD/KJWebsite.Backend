namespace ReportingService.Data.Entities;

/// <summary>A saved report: which built-in template it runs and the parameters it starts from. Archived, never deleted, so old runs keep their parent.</summary>
public sealed class ReportDefinitionEntity
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Template { get; set; } = string.Empty;
    public string ParametersJson { get; set; } = "{}";

    /// <summary>True when the report carries financial figures: only callers with `finance.view` may see, run or download it, and its recipients need their own permission to manage.</summary>
    public bool Sensitive { get; set; }

    public string? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
}

public static class RunTriggers
{
    public const string Manual = "manual";
    public const string Schedule = "schedule";
}

/// <summary>One generation of a report with the parameters it was given. A failed run keeps its reason; a successful one keeps its files.</summary>
public sealed class ReportRunEntity
{
    public string Id { get; set; } = string.Empty;
    public string ReportId { get; set; } = string.Empty;
    public string? ScheduleId { get; set; }
    public string Trigger { get; set; } = RunTriggers.Manual;
    public string ParametersJson { get; set; } = "{}";
    public string Status { get; set; } = RunStatuses.Running;
    public int Attempts { get; set; }
    public string? Error { get; set; }
    public int Rows { get; set; }
    public string? GeneratedBy { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}

/// <summary>The bytes of one export of one run, kept so a delivery can be retried and a download repeated without generating again.</summary>
public sealed class ReportFileEntity
{
    public long Id { get; set; }
    public string RunId { get; set; } = string.Empty;
    public string Format { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public byte[] Content { get; set; } = [];
    public string Sha256 { get; set; } = string.Empty;
}

/// <summary>Runs a saved report every month on a fixed day and hour (UTC) and mails the exports. It stays due until a run succeeds, so a failure is retried and alerted, never skipped.</summary>
public sealed class ReportScheduleEntity
{
    public string Id { get; set; } = string.Empty;
    public string ReportId { get; set; } = string.Empty;
    public int DayOfMonth { get; set; }
    public int HourUtc { get; set; }

    /// <summary>Comma-separated export formats attached to the mail: csv, xlsx, pdf.</summary>
    public string Formats { get; set; } = "xlsx";

    public string ParametersJson { get; set; } = "{}";
    public bool Active { get; set; } = true;
    public DateTimeOffset NextRunAt { get; set; }
    public DateTimeOffset? LastSuccessAt { get; set; }
    public int FailureCount { get; set; }

    /// <summary>When the current month's run first failed; cleared on success. A retry reuses files generated since then.</summary>
    public DateTimeOffset? FailingSince { get; set; }

    public string? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Someone a schedule mails. The address is encrypted and kept out of the audit log values; who added or removed whom, and when, is in the audit log. Removal ends the row, it is not deleted.</summary>
public sealed class ReportRecipientEntity
{
    public string Id { get; set; } = string.Empty;
    public string ScheduleId { get; set; } = string.Empty;
    public string EmailProtected { get; set; } = string.Empty;
    public string EmailKey { get; set; } = string.Empty;
    public string? AddedBy { get; set; }
    public DateTimeOffset AddedAt { get; set; }
    public string? RemovedBy { get; set; }
    public DateTimeOffset? RemovedAt { get; set; }
}

public static class DeliveryStatuses
{
    public const string Sent = "sent";
    public const string Failed = "failed";
}

/// <summary>The outcome of mailing one run to one recipient.</summary>
public sealed class ReportDeliveryEntity
{
    public long Id { get; set; }
    public string RunId { get; set; } = string.Empty;
    public string RecipientId { get; set; } = string.Empty;
    public string Status { get; set; } = DeliveryStatuses.Failed;
    public string? Error { get; set; }
    public DateTimeOffset At { get; set; }
}
