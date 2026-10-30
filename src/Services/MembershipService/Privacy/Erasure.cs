using System.Text.Json;
using KJWebsite.BuildingBlocks.Auditing;
using MembershipService.Data;
using MembershipService.Data.Entities;
using MembershipService.Intake;
using Microsoft.EntityFrameworkCore;

namespace MembershipService.Privacy;

public sealed record ErasureSummary(int Applications, int Attachments, int Notes, int Messages, bool MemberAnonymised);

/// <summary>Deletes personal data. Everything removed here is gone for good: callers decide when that is allowed (retention window, an executed erasure request).</summary>
public static class Erasure
{
    /// <summary>Removes one application with its attachment files, notes, history and queued messages. Returns what was removed.</summary>
    public static async Task<ErasureSummary> DeleteApplicationAsync(MembershipDbContext db, IAttachmentStore store, string applicationId, CancellationToken cancellationToken)
    {
        var application = await db.Applications.Include(a => a.Attachments).Include(a => a.Notes).FirstOrDefaultAsync(a => a.Id == applicationId, cancellationToken);
        if (application is null)
        {
            return new ErasureSummary(0, 0, 0, 0, false);
        }

        var messages = await db.NotificationOutbox.Where(o => o.ApplicationId == applicationId).ToListAsync(cancellationToken);
        var files = application.Attachments.Select(a => a.StorageKey).ToList();
        var summary = new ErasureSummary(1, application.Attachments.Count, application.Notes.Count, messages.Count, false);
        db.NotificationOutbox.RemoveRange(messages);
        db.Applications.Remove(application);
        await db.SaveChangesAsync(cancellationToken);

        // Files go after the rows: a leftover file is harmless, a row pointing at a missing file is not.
        foreach (var key in files)
        {
            await store.DeleteAsync(key, cancellationToken);
        }

        return summary;
    }

    /// <summary>
    /// Nulls the identifying columns of audit rows written while this person was the signed-in actor (email, IP, browser). The rows
    /// themselves, and everything about what changed, stay. This is the one narrow edit the audit table allows (see the migration
    /// that relaxes its PostgreSQL trigger); it bypasses change tracking on purpose.
    /// </summary>
    public static Task RedactAuditActorAsync(MembershipDbContext db, string userId, CancellationToken cancellationToken) =>
        db.Set<AuditEvent>().Where(a => a.ActorId == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.ActorEmail, (string?)null).SetProperty(a => a.Ip, (string?)null).SetProperty(a => a.UserAgent, (string?)null), cancellationToken);

    public static string Describe(ErasureSummary summary) => JsonSerializer.Serialize(summary);
}
