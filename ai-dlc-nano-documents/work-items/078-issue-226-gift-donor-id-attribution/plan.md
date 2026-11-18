<!-- phase: WRAP-UP | branch: Feature/issue-226-gift-donor-id-attribution | tasks: 5/6
     base: a00e162 | updated: 2026-11-18
     next: ask the user before push and PR (never merge); PR text drafted in the session scratchpad
     -->
# Plan: Gift donor id attribution (BE-112, #226)
## Tasks
- [x] 1 Public intake sets DonorId (signed-in donor token, else email pin; anonymous unchanged)
- [x] 2 RecurringPlanEntity.DonorId nullable column plus migration, set at plan creation
- [x] 3 Tests (token, email pin before an email change, first-time donor, anonymous, bad tokens, plan, model/migration)
- [x] 4 Docs (DONORS, DONATIONS, RECEIPTS, SPONSORSHIP, OFFLINE_DONATIONS, MASTER_PLAN) and openapi regenerated
- [x] 5 Full suite, 3 runs, mutation checks, format, live check, reviewer
- [ ] 6 Ask before push and PR (never merge)
## Tests
- High blast radius: token and email paths, failure paths, mutation checks, migration check.
## Files
- src/Services/FinanceService/Donations/PublicDonationEndpoints.cs, Data/Entities/DonationEntities.cs, Migrations; tests FinanceDonationTests/DonorCrmTests/DonorSelfServiceTests; docs; openapi.v1.yaml
