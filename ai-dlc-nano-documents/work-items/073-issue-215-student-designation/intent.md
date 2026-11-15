# Student designation on offline donations (BE-108)
- Source: issue #215 https://github.com/DevCastBD/KJWebsite.Backend/issues/215 (epic #208, decisions #209 D2, D5, D10)
- Type: feature
## Request
- Offline gifts can be designated to a student (`designation: student`, `designationId: stu_...`), valid only if the donor's link to that student was active on the received date; Finance asks Sponsorship, forwarding the staff token.
- Every connection is by id, never email: gift to donor (new `donations.DonorId`), gift to student (the `stu_` id), student to donor (the #214 link). Email is a contact detail only.
- Gift rows carry `donorId`, `studentId`, `studentName` (preferred name); `studentId` list filter; no edits, correction is the existing reversal.
## Decisions
- User, 2026-11-15: optional `donorId`, required for student gifts, stored on the gift; receipt attaches by the donor's stable DonorKey, not email.
- User: request is `designation: "student"` + `designationId`; errors on `designationId` (also `donorId`, `anonymous`, `email`).
- User: Sponsorship endpoints reuse finance keys (`finance.donation.record`, `finance.view`); no new permission, no Identity migration.
- User: donor history shows "Student sponsorship" only; the name rule is #225.
- User: issues first. #215 rewritten, #225 #226 #227 created and on the board, comment on FrontEnd#353; plan approved after that.
- User, 2026-11-15 (after the independent review): a gift never changes the donor. With a donorId the donor's own current name, email, phone and language are used and typed ones are ignored; approval leaves the donor alone; the receipt and its notification use the donor's current name and email at approval; corrections go through the donor profile (#211). This replaced my assumed "a different email updates the donor at approval", which could overwrite a newer staff correction.
- Assumed in plan: link checked once at record time; picker endpoint `designatable-students`; filter name `studentId`.
## Out of scope
- #225 donor-side name; #226 online and legacy gifts by id; #227 staff donor gift list and reversed gifts; the public donation form.
## Follow-ups
- Offline gift date is checked against UTC today (Dhaka midnight window); the General donor label stays English for bn donors.
- Approval email update could hit the unique index if two donors race; student gifts are one generic line each in reports; any finance.view holder can read a preferred name by id.
- Issues created for the rest: #225 donor-side name, #226 attribute online gifts by id, #227 staff donor list and reversed gifts.
