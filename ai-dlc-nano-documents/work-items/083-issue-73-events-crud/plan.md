<!-- phase: PLAN | branch: feat/issue-73-events-crud | tasks: 0/6
     base: b33f121 | updated: 2026-11-27
     next: await plan approval -->
# Plan: Events CRUD (#73)
## Tasks
- [ ] ContentTypes.Event + ContentKind "events"; EventEntity (1:1: StartUtc, EndUtc, TimeZoneId IANA, venue, online URL, capacity, RecurrenceRule RRULE, SeriesId) + EventSessionEntity (ordered, StartUtc/EndUtc, per-lang title in attributes) + EF migration
- [ ] Event store: validate (tz id, end>start, capacity>=0, RRULE parse/limits), upsert alongside generic upsert, revision snapshot includes event fields
- [ ] Admin endpoints under /api/v1/admin/content/events (+ sessions PUT replace, ordered) with content.* permissions, ETag
- [ ] Public GET /api/v1/content/events (+ from/to filter, upcoming) and detail incl. sessions and occurrences expanded from rule (series edit = one row)
- [ ] Tests: create/publish bilingual, session order, tz round-trip (Dhaka, DST zone), recurring expansion + single-rule edit; endpoint permission audit passes
- [ ] Docs (CONTENT_MODEL/EVENTS.md), openapi.v1.yaml regen, map note
## Tests
- Standard tier; targeted tests only (user asked to skip full suite for speed), build + format verify
## Files
- src/Services/ContentService/{ContentKinds,Data/Entities,Data/*,Migrations,*Endpoints}.cs, tests/**, docs, openapi.v1.yaml
