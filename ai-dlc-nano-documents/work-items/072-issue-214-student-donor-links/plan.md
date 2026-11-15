<!-- phase: DONE | branch: Feature/issue-214-student-donor-links | tasks: 9/9
     base: cf5168e | updated: 2026-11-15
     next: none; PR #224 awaits human review and merge -->
# Plan: Student-donor links (BE-107, #214). Full design: C:\Users\Tahmid\.claude\plans\merry-chasing-thimble.md
## Tasks
- [x] Finance: link-target, link-notices (updates gate, idempotent, key required), donor.student_linked mapping, GET donors/me/identity (revised, user confirmed)
- [x] Sponsorship data: LinkEntity, FrozenCopyEntity (append-only), filtered unique indexes, DonorId index, audit registration, migration AddLinks
- [x] IFinanceDonors client (target, notify, me) with named client finance and token forwarding
- [x] Staff endpoints: list links, link (409 LINK_EXISTS, STUDENT_ALREADY_SPONSORED), unlink ended and mistaken (frozen copy, link.void check, early cancellation)
- [x] PHOTO_FROZEN guard and STUDENT_CHANGED conflict on photo PUT and DELETE; Sponsored, Links and sponsored=true placeholders filled
- [x] Donor endpoints: list, detail, photo, no-store and Vary headers, 405 on writes; gateway routes; FoundationDay time zone
- [x] Tests: SponsorshipLinkTests, FinanceDonorsClientTests, DonorLinkTargetTests, identity cases in DonorSelfServiceTests, GatewayDonorRoutesTests
- [x] openapi.v1.yaml regenerated and verified, docs/SPONSORSHIP.md and DONORS.md updated
- [x] Final full suite: 1168 integration + 46 unit pass, 0 fail, 22 Postgres-gated skips; format clean on touched files
## Tests
- High blast radius (children, access revocation, concurrency): refusals and identical-404 cases lead; parallel link and unlink tests; independent review done, findings fixed.
- Not run: Postgres migration and a KJ_TEST_POSTGRES test; a live three-service run (gateway routing checked with stub listeners).
## Files
- src/Services/SponsorshipService/**, FinanceService/Donors/**, ApiGateway/Program.cs, openapi.v1.yaml
- tests/IntegrationTests/.../{SponsorshipLinkTests,FinanceDonorsClientTests,DonorLinkTargetTests,GatewayDonorRoutesTests}.cs, docs/SPONSORSHIP.md, docs/DONORS.md
