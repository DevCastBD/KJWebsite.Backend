<!-- phase: DONE | branch: Feature/issue-50-campaigns-status-lookup | tasks: 6/6
     base: 178dfd9 | updated: 2026-10-31
     next: none (done) -->
# Plan: Campaigns + status lookup
## Tasks
- [x] Reference = KJ-<year>-<8 random Crockford base32>, unique index + retry; update #49 tests/docs
- [x] Campaign entity (slug, nameEn/Bn, opensAt, closesAt, closedAt); ApplicationStatusEvent history; migration
- [x] Admin API /api/v1/admin/membership/campaigns (membership.manage; audited): list/create/update/close
- [x] Public GET /api/v1/membership/campaigns/current; intake links campaign; closed -> 409 INTAKE_CLOSED (no campaigns = open)
- [x] POST .../status: hash-matched email, identical 404, timeline only; gateway route + own rate limit rule
- [x] Tests, docs, openapi regen, build+format+full suite, commit
## Tests
- Standard + enumeration: unknown ref vs wrong email byte-identical; no notes/scores fields; closed/open window; reference shape
## Files
- MembershipService, ApiGateway (admin route, rate rule), tests, docs, openapi.v1.yaml
