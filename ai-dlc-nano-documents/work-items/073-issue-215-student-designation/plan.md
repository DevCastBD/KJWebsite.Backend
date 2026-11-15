<!-- phase: DONE | branch: Feature/issue-215-student-designation | tasks: 10/10
     base: 93215fd | updated: 2026-11-15
     next: none; PR #228 awaits human review and merge -->
# Plan: Student designation on donations (BE-108, #215). Full design: C:\Users\Tahmid\.claude\plans\merry-chasing-thimble.md
## Tasks
- [x] Sponsorship: three finance-keyed endpoints (designation check, designatable-students, names) with tests
- [x] Finance: ISponsorshipLinks client, Designations.Student, student id format, public form stays closed
- [x] Finance: donations.DonorId column and migration; record request donorId and designationId; validation (donor, anonymous, email rules, link check, 503)
- [x] Attribution by id: receipt DonorKey by donor id; a gift never changes the donor (user decision after review); donor's current name and email on receipt at approval
- [x] Rows, detail and record response carry donorId, studentId, studentName; studentId filter on list and totals
- [x] Labels: receipt PDF, annual statement, donor self-service (Bengali wording reviewed)
- [x] Tests: FinanceStudentDesignationTests (record, email, attribution, lists, labels, public form), SponsorshipDesignationTests, client tests
- [x] openapi regenerated; docs (OFFLINE_DONATIONS, DONATIONS, DONORS, SPONSORSHIP, RECEIPTS); MASTER_PLAN rows BE-111 to BE-113
- [x] Independent review fixed and full suite rerun: 1230 integration + 46 unit pass, 0 fail, 23 Postgres-gated skips; format clean on touched files
- [x] GitHub corrections (#215 body, FrontEnd#353 comment), push, PR #228 opened (user approved); not merged
## Tests
- High blast radius (money attribution, children's data): refusals first; record-time validation, attribution by id when the email changes, 503 saves nothing, no student id or name on receipts.
- Not run: PostgreSQL migration and the gated Postgres tests; a live three-service run (Finance to Sponsorship is covered by fakes and a scripted client).
## Files
- src/Services/SponsorshipService/Links/DesignationEndpoints.cs (new), src/Services/FinanceService/{Donations,Offline,Receipts,Donors}/**, Data/**, Migrations/*AddDonationDonorId
- tests/IntegrationTests/**, docs/**, openapi.v1.yaml
