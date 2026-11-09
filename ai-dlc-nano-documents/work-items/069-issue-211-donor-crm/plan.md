<!-- phase: WRAP-UP | branch: Feature/issue-211-donor-crm | tasks: 8/8
     base: 1fbf422 | updated: 2026-11-09
     next: user approval to commit, push, open PR -->
# Plan: BE-104 donor CRM profile and login (#211)
## Tasks
- [x] Model and migration: DonorEntity gains EmailKey (unique lookup hash of the current email), AccountSubject (unique, nullable), AccountShared, Type, Organisation, AddressProtected, Country, Status, NotesProtected, AccountStatus (mirror), Version (concurrency token); Finance migration backfills EmailKey = DonorKey; audit entry for DonorEntity with the protected columns left out
- [x] Gift matching: a new receipt takes the stable DonorKey of the donor whose EmailKey matches the gift email; DonorService finds the donor by EmailKey; a new donor whose email hash is already some donor's stable key gets a fresh unique key; existing receipts and their hash chain untouched
- [x] POST /donors (finance.donor.manage): validation per the issue, 201 with ETag, 409 DONOR_EXISTS naming the existing donor; GET list and detail gain type, organisation, address, country, status, accountStatus, notes (detail), ETag, filters status, accountStatus, hasAccount
- [x] PATCH /donors/{id}: If-Match required (428 without, 412 stale), email change updates EmailKey only after a duplicate check; setting inactive disables a donor-only account in Identity (not re-enabled on reactivation, a shared account is untouched); an Identity failure saves nothing (503 AUTH_UNAVAILABLE)
- [x] Identity: GET /api/v1/admin/auth/donors/{userId} (finance.view) returns status, mustChangePassword, roles and shared; used live by the donor detail
- [x] Finance AuthDonorClient (Services:Auth:Url, staff bearer forwarded, test seam) with POST /donors/{id}/login, PUT .../login/password, PUT .../login/status (finance.donor.provision): ACCOUNT_EXISTS, Identity errors mapped, AccountStatus mirrored, the password never logged, audited or returned
- [x] Tests against a real Identity in-process: create, duplicates, validation, email change keeps history and a gift with the old email makes a new donor with its own key, If-Match, filters, login flows (donor-only, member email, EMAIL_TAKEN, ACCOUNT_EXISTS, Identity down), inactive rules, permissions, encrypted columns, no secrets in responses, logs or audit
- [x] Verify: Postgres-gated migration test, docs/DONORS.md and SPONSORSHIP.md, OpenAPI export, full dotnet test, dotnet format style and analyzers
## Tests
- High blast radius (personal data, auth calls, receipts keying, concurrency): failure and rollback paths tested; no change to existing receipts
## Files
- Finance: Data/Entities/DonorEntities.cs, Data/FinanceDbContext.cs, Migrations/, Receipts/ReceiptService.cs, Donors/*, Program.cs; Auth: new donor read endpoint; tests/IntegrationTests; docs/DONORS.md, docs/SPONSORSHIP.md, openapi.v1.yaml
