<!-- phase: CONSTRUCT | branch: Feature/issue-226-gift-donor-id-attribution | tasks: 0/6
     base: a00e162 | updated: 2026-11-18
     next: Codex task 1: public intake donor id (token, email pin), recurring plan DonorId migration, tests -->
# Plan: Gift donor id attribution (BE-112, #226)
## Tasks
- [ ] 1 Public intake sets DonorId (signed-in donor token, else email pin; anonymous unchanged)
- [ ] 2 RecurringPlanEntity.DonorId nullable column plus migration, set at plan creation
- [ ] 3 Tests (token, email pin before an email change, first-time donor, anonymous, bad tokens, plan, model/migration)
- [ ] 4 Docs (DONORS, DONATIONS, RECEIPTS, SPONSORSHIP, OFFLINE_DONATIONS, MASTER_PLAN) and openapi regenerated
- [ ] 5 Full suite, 3 runs, mutation checks, format, live check, reviewer
- [ ] 6 Ask before push and PR (never merge)
## Tests
- High blast radius: token and email paths, failure paths, mutation checks, migration check.
## Files
- src/Services/FinanceService/Donations/PublicDonationEndpoints.cs, Data/Entities/DonationEntities.cs, Migrations; tests FinanceDonationTests/DonorCrmTests/DonorSelfServiceTests; docs; openapi.v1.yaml
