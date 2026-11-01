<!-- phase: DONE | branch: Feature/issue-39-editorial-workflow | tasks: 7/7
     base: 95a3959 | updated: 2026-10-31
     next: none (done) -->
# Plan: Editorial workflow
## Tasks
- [x] Statuses draft/in_review/scheduled/published/archived for all types; ScheduledPublishAt + ContentRevision entities + migration; upsert status guard (publish permission)
- [x] EditorialWorkflow service + POST items/{type}/{slug}/workflow with transition table, permissions, If-Match
- [x] ScheduledPublisher BackgroundService (poll, per-item retry, cache eviction, ambient system actor for audit)
- [x] Revisions: snapshot on every content change (project/news/generic), list/get/diff/restore
- [x] Trash list + restore of soft-deleted; preview tokens (HMAC, scoped, expiring) + public preview endpoint
- [x] Tests (transitions/permissions, scheduler real+manual clock, revisions bilingual diff/restore, trash, preview scope/expiry/tamper), docs/EDITORIAL_WORKFLOW.md, openapi, PG run
- [x] Full suite (+PG), format, openapi verify, commit
## Tests
- Standard/high: SQLite + local PostgreSQL via KJ_TEST_POSTGRES
## Files
- ContentService (Data, endpoints, publisher, migration), BuildingBlocks/Auditing (ambient actor), tests, docs, openapi.v1.yaml
