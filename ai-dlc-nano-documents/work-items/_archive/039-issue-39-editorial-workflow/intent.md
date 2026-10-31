# BE-034 Editorial workflow (#39)
- Source: issue #39 https://github.com/DevCastBD/KJWebsite.Backend/issues/39 (milestone 3, order BE-034)
- Type: feature
## Request
- draft -> in_review -> scheduled -> published -> archived; scheduled publishing without intervention; revision history with diff + restore (bilingual); scoped expiring preview tokens; soft-deleted content restorable
## Decisions
- #33 (job runner) is not on develop: self-contained BackgroundService poller in ContentService, retries by leaving the item scheduled; swap to #33 later (user confirmed)
- Review rule (D-16 undecided): content.update submits for review; scheduling, publishing, unpublishing, archiving need content.publish; upsert can no longer set a live/archived status without content.publish
- Workflow applies to all 8 types; project live status stays "active"
- Restore of a soft-deleted item returns it as draft (never silently publishes); revision restore keeps current status
- Preview token = HMAC over item id + expiry (Preview:SigningKey, ephemeral in Development); public GET /content/preview/{token}, no-store, noindex
- Scheduler/system actions audited through an ambient system actor in the audit interceptor
## Out of scope
- Review comments/assignees, email notifications, #33 runner
## Follow-ups
