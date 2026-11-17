# Staff donor gift list keeps reversed gifts (BE-113, #227)
- Source: issue #227 https://github.com/DevCastBD/KJWebsite.Backend/issues/227 (part of epic #208, found while building #215)
- Type: bug (docs and behaviour disagree)
## Request
- `GET /api/v1/admin/finance/donors/{id}/donations` drops reversed gifts and hard-codes status `confirmed`; DONORS.md, SPONSORSHIP.md and DONATIONS.md say staff lists keep reversed gifts with their status.
- Fix one side; donor-facing endpoints must keep omitting reversed gifts; totals exclude reversed gifts.
## Decisions
- Fix the code, not the docs (frontend already expects refunded rows and excludes them from totals); reversed = a reversal donation exists, the admin list rule; `gifts` of the donor detail also shows them; totals, count, segments, export unchanged → plan approved by user 2026-11-17.
## Out of scope
- Totals, segments, export, donor-facing endpoints, frontend.
## Follow-ups
- (none yet)
