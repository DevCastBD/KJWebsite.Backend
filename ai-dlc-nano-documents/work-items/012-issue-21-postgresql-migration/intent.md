# B1-1 — Migrate to PostgreSQL with schema per service
- Source: issue #21 https://github.com/DevCastBD/KJWebsite.Backend/issues/21
- Type: feature
## Request
- Replace SQLite with PostgreSQL 17 using Npgsql and a schema per service.
- Re-baseline migrations; keep the release migration path backward-compatible and document migration of existing development data.
- Verify clean database setup, seeded Bengali text, and integration tests against PostgreSQL.
## Decisions
- ADR-001 accepted true microservices on 2026-10-12; see `docs/adr/0001-true-microservices.md`.
- Pending: ADR-004 PostgreSQL ratification (#12); issue #21 depends on it.
## Out of scope
- Later P1 identity, content, and engagement features, except changes necessary for database migration.
## Follow-ups
- None yet.
