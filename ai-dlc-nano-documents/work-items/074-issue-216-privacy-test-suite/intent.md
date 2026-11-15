# Sponsorship privacy test suite (BE-109)
- Source: issue #216 https://github.com/DevCastBD/KJWebsite.Backend/issues/216 (epic #208, decisions #209, follow-up #229 BE-114)
- Type: feature (tests, plus small source fixes the suite exposes)
## Request
- Automated local tests (no Docker) proving donor and student data cannot leak: 18 scenarios T-01..T-29 across access control, cross-donor access, public routes, caching headers, disabled accounts, passwords, ETag preconditions and logs.
- Public-route scan reads openapi.v1.yaml so a new public endpoint is covered without editing the test; the log scan covers every test's output; a failure names the endpoint and the leaked value.
## Decisions
- User, 2026-11-16: T-14 tests the documented staff path (disable through Finance); the Identity-direct disable window is issue #229 (BE-114), no product change for it here.
- User, 2026-11-16: fix the small gaps inside #216: no-store header middleware for Finance donor and gift staff paths and Identity donor staff paths; Identity's Development email sender stops logging the address and token.
- Approach in the approved plan: one in-memory harness (gateway plus six services wired together), endpoint enumeration for T-01/T-02/T-07, openapi-driven public scan (YamlDotNet, test-only), process-wide log sink checked at write time, every factory Dispose and a final non-parallel scan test.
- User, 2026-11-16: fix the donor CSV export bug under this PR. Found by task A: the unfiltered export (GET /api/v1/admin/finance/donors/export) threw on Min/Max of an empty gift list, so it returned 500 when any donor had no gifts (donors staff create before a gift, since #211). Fixed with empty first and last gift date cells, plus a regression test.
- User, 2026-11-16: issues first; #216 body updated, #229 created and on the board; then go.
## Out of scope
- Closing the Identity-direct disable window (#229); Postgres runs; a deployed gateway; a browser.
## Follow-ups
- (none yet)
