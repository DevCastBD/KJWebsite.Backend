# Audit #21 PostgreSQL migration
- Source: issue #21 (closed; PR #155 merged 2026-10-12)
- Type: audit
## Request
- Audit acceptance criteria vs develop; report gaps, no re-implementation
## Decisions
- Audit only (user confirmed)
## Out of scope
- Content persistence (#25)
## Follow-ups
- Gap: ContentService has no database (#25)
- Gap: PG tests only ran on local PG 18.6, target is 17
