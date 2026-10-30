<!-- phase: DONE | branch: feature/issue-33-outbox-jobs | tasks: 5/5
     base: 2ba5ca5 | updated: 2026-10-30
     next: none; committed locally -->
# Plan: Background jobs and transactional outbox
## Tasks
- [x] BuildingBlocks outbox abstractions (OutboxMessage, handler registry, dispatcher, backoff)
- [x] EF migration: outbox + dead_letter tables in CTA schema; Hangfire storage config
- [x] Dashboard behind Permission policy (never anonymous)
- [x] Tests: atomic with business write, retry/backoff, dead-letter, dashboard auth
- [x] Docs, openapi if any, full suite + format
## Tests
- High blast radius: failure/rollback paths tested
## Files
- src/BuildingBlocks, src/Services/CtaSubmissionService/**, tests, docs
