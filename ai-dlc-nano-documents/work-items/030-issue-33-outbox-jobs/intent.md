# Background jobs and transactional outbox
- Source: issue #33 https://github.com/DevCastBD/KJWebsite.Backend/issues/33
- Docs: docs/MASTER_PLAN.md, ADR-005 (Hangfire), MASTER_PLAN §3.7
- Type: feature
## Request
- Hangfire with dashboard behind admin auth; outbox table written in the same transaction as business data; dispatcher with exponential backoff; dead-letter table + alert signal.
## Decisions
- Done before #32 (it depends on #33; differs from the given order, confirmed at plan approval)
- Hangfire PostgreSQL storage in its own schema; tests use in-memory storage / manual dispatch → assumed
- Hosted in CtaSubmissionService (first producer); outbox abstraction lives in BuildingBlocks → assumed
## Out of scope
- Email content (#32)
## Follow-ups
- none yet
