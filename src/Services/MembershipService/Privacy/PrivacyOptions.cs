using MembershipService.Data;
using MembershipService.Data.Entities;

namespace MembershipService.Privacy;

/// <summary>
/// Privacy settings. Retention windows have no defaults on purpose (ADR-014): until the core members decide them (#110) nothing
/// is purged, and the public privacy endpoint says so. Windows are whole months; unset or 0 means "keep".
/// </summary>
public sealed class PrivacyOptions
{
    public const string SectionName = "Privacy";

    /// <summary>The version of the published privacy notice, recorded with each consent.</summary>
    public string NoticeVersion { get; set; } = string.Empty;

    /// <summary>Where personal data is stored, in words, as the published privacy statement says it (ADR-013/014).</summary>
    public string DataLocation { get; set; } = string.Empty;

    public int? RejectedApplicationMonths { get; set; }
    public int? WithdrawnApplicationMonths { get; set; }
    public int? PostponedApplicationMonths { get; set; }

    /// <summary>How often the purge worker looks, in hours. It only acts when at least one window is set.</summary>
    public int WorkerIntervalHours { get; set; } = 24;

    public bool WorkerEnabled { get; set; } = true;

    public int? WindowFor(string status) => status switch
    {
        ApplicationStatuses.Rejected => RejectedApplicationMonths,
        ApplicationStatuses.Withdrawn => WithdrawnApplicationMonths,
        ApplicationStatuses.Postponed => PostponedApplicationMonths,
        _ => null
    } is > 0 and var months ? months : null;

    public bool AnyWindow => WindowFor(ApplicationStatuses.Rejected) is not null || WindowFor(ApplicationStatuses.Withdrawn) is not null || WindowFor(ApplicationStatuses.Postponed) is not null;
}

/// <summary>Writes the consent ledger. Always adds a row, never changes one.</summary>
public static class ConsentLedger
{
    public static void Record(MembershipDbContext db, string subjectKind, string subjectId, string purpose, bool granted, DateTimeOffset at, string source, string actorId, string? noticeVersion) =>
        db.ConsentRecords.Add(new ConsentRecordEntity
        {
            SubjectKind = subjectKind,
            SubjectId = subjectId,
            Purpose = purpose,
            Granted = granted,
            At = at,
            Source = source,
            ActorId = actorId,
            NoticeVersion = string.IsNullOrWhiteSpace(noticeVersion) ? null : noticeVersion
        });
}
