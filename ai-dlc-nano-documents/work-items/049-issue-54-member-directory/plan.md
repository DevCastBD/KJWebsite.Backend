<!-- phase: DONE | branch: Feature/issue-54-member-directory | tasks: 5/5
     base: 9ca355c | updated: 2026-10-31
     next: none (done) -->
# Plan: Member directory
## Tasks
- [x] Public GET /api/v1/members/directory: SQL projection with CASE per consented field, filter active + any consent + no deletion request, consent-aware q, paging (24, cap), no-store
- [x] Public photo GET /api/v1/members/directory/{id}/photo (consent + scan clean, else uniform 404)
- [x] Staff GET /api/v1/admin/membership/members (q, status, paging; no contact data) + GET /{id} (contact details, single record)
- [x] Gateway: route + GET rate rule (MembershipDirectory)
- [x] Tests (leak matrix per consent, search can't reveal hidden fields, immediate withdrawal, paging cap, rate limit, staff perms), docs, openapi, PG, commit
## Tests
- High blast radius (PII publication): every consent combination, query-level assertion via SQL log or projection test
## Files
- MembershipService (Directory/), ApiGateway, tests, docs, openapi.v1.yaml
