namespace FinanceService.Data.Entities;

/// <summary>
/// A monthly report pack once generated: the workbook's bytes live in the file store and are never regenerated, so what was handed to a trustee or auditor can be shown again exactly.
/// The hash is checked on every download. Append-only.
/// </summary>
public sealed class ReportPackEntity
{
    public string Id { get; set; } = string.Empty;
    public string Month { get; set; } = string.Empty;
    public string EntityName { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string StorageKey { get; set; } = string.Empty;
    public string? GeneratedBy { get; set; }
    public DateTimeOffset GeneratedAt { get; set; }
}
