<!-- phase: CONSTRUCT | branch: Feature/issue-227-staff-donor-gift-list-reversed | tasks: 0/6
     base: 8509ee0 | updated: 2026-11-17
     next: DonorData.cs: load reversed gifts separately and merge them into GiftViews with status refunded -->
# Plan: Staff donor list keeps reversed gifts (BE-113, #227)
## Tasks
- [ ] 1 DonorData: DonorView.Reversed, query, GiftViews merge with status; endpoint summary
- [ ] 2 Tests in FinanceDonorTests and one assertion in FinanceStudentDesignationTests
- [ ] 3 openapi.v1.yaml regenerated and verified
- [ ] 4 Docs via Codex (DONORS, SPONSORSHIP, DONATIONS, MASTER_PLAN)
- [ ] 5 Full suite, 3 runs, mutation checks, format, live check, reviewer
- [ ] 6 Ask before push and PR (never merge)
## Tests
- Standard rigor: new tests for reversed in staff list, detail, only-reversed donor, anonymous, asOf; totals unchanged; donor-facing still omits.
## Files
- src/Services/FinanceService/Donors/DonorData.cs, DonorEndpoints.cs; tests FinanceDonorTests.cs, FinanceStudentDesignationTests.cs; docs; openapi.v1.yaml
