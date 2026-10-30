using ContentService.Data.Entities;

namespace ContentService.Data;

/// <summary>The editorial states every content type moves through, and who may move an item between them.</summary>
public static class StatusRules
{
    public const string Draft = "draft";
    public const string InReview = "in_review";
    public const string Scheduled = "scheduled";
    public const string Published = "published";
    public const string Archived = "archived";

    public static readonly string[] Workflow = [Draft, InReview, Scheduled, Published, Archived];

    /// <summary>Projects keep their legacy live status `active`; every other type is `published`.</summary>
    public static string Live(string type) => type == ContentTypes.Project ? "active" : Published;

    public static string Canonical(string status) => status == "active" ? Published : status;

    public static bool IsLive(string status) => status is Published or "active";

    /// <summary>States an editor with only content.update may not enter or leave: publishing is a separate permission.</summary>
    public static bool NeedsPublishPermission(string? status) => status is Published or "active" or Scheduled or Archived;

    /// <summary>
    /// For POST/PUT, which can still carry a `status`: changing into or out of a live, scheduled or archived state needs content.publish
    /// (otherwise creating "published" content would bypass review), and scheduling needs a time, so it has its own endpoint.
    /// </summary>
    public static (UpsertOutcome Outcome, string Error)? GuardUpsert(string? current, string requested, bool canPublish)
    {
        if (requested == current)
        {
            return null;
        }

        if (requested == Scheduled)
        {
            return (UpsertOutcome.Invalid, "Use POST .../workflow with to=scheduled and publishAt to schedule an item.");
        }

        return !canPublish && (NeedsPublishPermission(requested) || NeedsPublishPermission(current))
            ? (UpsertOutcome.Forbidden, "Publishing, unpublishing, scheduling and archiving need the content.publish permission.")
            : null;
    }
}
