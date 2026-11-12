<!-- phase: DONE | branch: Feature/issue-212-donor-self-service | tasks: 9/9
     base: dd95b6f | updated: 2026-11-11
     next: user approves commit, push and PR (Closes #212) -->
# Plan: BE-105 donor self-service (#212)
## Tasks
- [x] BuildingBlocks: DonorSelf policy (role claim Donor, must-change gate) and RequireDonorSelf; covered by the integration tests
- [x] Finance: resolve the donor from the token subject (AccountSubject); 404 NOT_A_DONOR, 403 DONOR_INACTIVE, 403 ACCOUNT_DISABLED, checked per request
- [x] GET /api/v1/donors/me with donor-visible fields only (decrypted), `since` from CreatedAt
- [x] GET /api/v1/donors/me/donations: valid non-anonymous receipts by DonorKey, newest first, limit/offset/total, 400 over 100, year filter, per-currency totals, designation label
- [x] Private no-store headers, ignore supplied ids, 405 on writes (verify the framework answer, add handler if needed)
- [x] Gateway routes for the two paths
- [x] Integration tests: isolation between two donors, id in query and body ignored, roles and states, reversed gifts, paging, year, totals, headers, 405
- [x] openapi.v1.yaml regenerated; docs/DONORS.md section; MASTER_PLAN BE-105 row checked
- [x] Verify: dotnet build, full dotnet test (4 unrelated failures, see below); gateway routes added but not run through a live gateway
## Tests
- High blast radius (ownership, personal data): isolation, every refusal path and state change tested; SQLite default, Postgres tests stay gated.
## Files
- src/BuildingBlocks/.../Security, src/Services/FinanceService/Donors/ (new DonorSelfEndpoints), src/Services/ApiGateway/Program.cs, tests/IntegrationTests, openapi.v1.yaml, docs/DONORS.md
