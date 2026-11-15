<!-- phase: DONE | branch: Feature/issue-55-committees-org-chart | tasks: 5/5
     base: d104994 | updated: 2026-10-31
     next: none (done) -->
# Plan: Committees and org chart
## Tasks
- [x] Entities+migration: committees (kind, bilingual name, parent, archived), committee_members (position bilingual, startsOn/endsOn DateOnly); audit both
- [x] Admin API (membership.manage): committees CRUD+archive (nesting/cycle/kind checks), terms add/update/end, list current|past|all; no overlapping same-position terms
- [x] Public API: GET /api/v1/committees (org chart, current terms) and /{slug} (current+past), consent+active filtered in SQL, no-store; gateway routes
- [x] Directory: chapters list + chapter filter from kind=chapter committees' current members
- [x] Tests (nesting/cycles, term overlap, current vs past by date, consent leak matrix, bilingual, admin perms, PG), docs, openapi, commit
## Tests
- Standard + PII: consent and withdrawal; date boundaries (start/end inclusive)
## Files
- MembershipService (Committees/), ApiGateway, MemberDirectory, tests, docs, openapi.v1.yaml
