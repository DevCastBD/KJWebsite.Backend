<!-- phase: DONE | branch: Feature/issue-43-audit-log | tasks: 6/6
     base: e67368d | updated: 2026-10-31
     next: none (done) -->
# Plan: Immutable audit log
## Tasks
- [x] BuildingBlocks/Auditing: AuditEventEntity + model config, AuditOptions, SaveChanges interceptor (actor, IP, UA, request id, diff), append-only guard, PG trigger helper
- [x] Query endpoints (actor/entity/entityId/action/requestId/date filters, keyset cursor, audit.view) mapped per service
- [x] Wire Content (items, translations, tags, relations, redirects) and Auth (users, roles, role claims, user roles, profiles) + migrations; keep redirect removal tracked
- [x] Test factory adds the interceptor; tests (every admin write audited w/ diff, anonymous/seed skipped, secrets excluded, immutability, filters, cursor over volume, permission)
- [x] docs/AUDIT_LOG.md, openapi regen
- [x] Full suite, format, openapi verify, commit
## Tests
- High blast radius (audit/personal data): failure paths + immutability tested; PG trigger verified on a local PostgreSQL 18 (KJ_TEST_POSTGRES)
## Files
- BuildingBlocks/Auditing/*, Content + Auth Program/DbContext/Migrations, tests, docs, openapi.v1.yaml
