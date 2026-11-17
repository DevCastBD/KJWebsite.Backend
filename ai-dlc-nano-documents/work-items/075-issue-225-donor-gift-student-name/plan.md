<!-- phase: WRAP-UP | branch: Feature/issue-225-donor-gift-student-name | tasks: 5/6
     base: 32095a2 | updated: 2026-11-17
     next: ask the user before push and PR (never merge); PR text drafted in the session scratchpad
     -->
# Plan: Donor gift history student name (BE-111, #225).
## Tasks
- [x] 1 Sponsorship donor lookup `/api/v1/donors/me/students/names` plus tests
- [x] 2 Finance `ISponsorshipLinks.DonorNamesAsync`, `DonorMeGift.StudentName`, label, plus tests (and fake)
- [x] 3 End-to-end through the gateway, real stack, incl. hidden cases identical and log sink
- [x] 4 Docs (SPONSORSHIP.md, DONORS.md) and openapi.v1.yaml regenerated and verified
- [x] 5 Full suite, 3 runs of touched classes, mutation checks, format, reviewer
- [ ] 6 Ask before push and PR (never merge)
## Tests
- High blast radius: failure paths tested (Sponsorship down, hidden cases, disabled donor, bad ids), mutation checks per hidden case.
## Files
- src/Services/SponsorshipService/Links/DonorStudentEndpoints.cs; src/Services/FinanceService/Donors/DonorSelfEndpoints.cs, Donations/SponsorshipLinks.cs
- tests/IntegrationTests/KJWebsite.IntegrationTests/** ; docs/SPONSORSHIP.md, docs/DONORS.md, openapi.v1.yaml
