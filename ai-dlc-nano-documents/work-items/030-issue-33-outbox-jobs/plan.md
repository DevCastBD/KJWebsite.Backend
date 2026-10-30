<!-- phase: PLAN | branch: feature/issue-33-outbox-jobs | tasks: 0/5
     base: — | updated: 2026-10-30
     next: await plan approval, then branch feature/issue-33-outbox-jobs (feature/issue-31-observability) -->
# Plan: Background jobs and transactional outbox
## Tasks
- [ ] BuildingBlocks outbox abstractions (OutboxMessage, handler registry, dispatcher, backoff)
- [ ] EF migration: outbox + dead_letter tables in CTA schema; Hangfire storage config
- [ ] Dashboard behind Permission policy (never anonymous)
- [ ] Tests: atomic with business write, retry/backoff, dead-letter, dashboard auth
- [ ] Docs, openapi if any, full suite + format
## Tests
- High blast radius: failure/rollback paths tested
## Files
- src/BuildingBlocks, src/Services/CtaSubmissionService/**, tests, docs
