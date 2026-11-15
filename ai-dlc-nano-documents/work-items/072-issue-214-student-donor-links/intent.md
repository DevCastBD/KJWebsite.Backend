# Student-donor links, unlink types, frozen copy, donor student views (BE-107)
- Source: issue #214 https://github.com/DevCastBD/KJWebsite.Backend/issues/214 (epic #208, decisions #209, contract docs/SPONSORSHIP.md)
- Type: feature
## Request
- Staff link and unlink students and donors in SponsorshipService; one active donor per student (D3).
- Unlink `ended` keeps a frozen copy for the donor; `mistaken` voids the link at once (super admin only).
- Donors read their linked students at /api/v1/donors/me/students (list, detail, photo), read-only.
- Linking queues a bilingual notice through Finance's outbox and consent gate (D14).
## Decisions
- User, 2026-11-13: donor resolved by forwarding the donor's token to Finance GET /donors/me (D16 kept, 503 if down).
- User, 2026-11-13: link notice falls under the `updates` consent purpose (template donor.student_linked).
- User, 2026-11-13: frozen photo is a reference to the live file; photo replace/delete return 409 PHOTO_FROZEN while referenced.
- User, 2026-11-13: branch Feature/issue-214-student-donor-links stacked on Feature/issue-213-sponsorship-module (PR #223 unmerged).
- User, 2026-11-13: Finance /donors/me has no donor id (fixed allow-list), so a new Finance endpoint GET /donors/me/identity returns {id, language}; /me unchanged.
- User, 2026-11-15: a link that has not started can be ended by link.manage with type ended: no frozen copy, endDate null, never shown to the donor, kept in history.
- User, 2026-11-15: "today" uses a Sponsorship:TimeZone setting (default Asia/Dhaka), not UTC.
- Review fixes applied without asking: unlink race to 409, student Version bump on ended unlink, names resolved before the transaction, Idempotency-Key required on Finance link-notices, DonorId index.
- Assumed in approved plan: unlink body field is `endDate`; startDate required, past or future, donor sees it once started.
- Assumed: notice payload carries only linkId (no child data in Finance); notice result queued|suppressed|pending.
## Out of scope
- Finance CRM linkedStudents field and has-active-links filter (needs Finance to call Sponsorship).
- Student designation on gifts (#215); privacy suite (#216); docs publication beyond contract text (#218).
## Follow-ups
- Notice retry worker for NoticeStatus pending (no service credentials until BE-093).
- D13 retention of frozen copies; foundation sign-off on frozen photo and story surviving consent withdrawal.
- Postgres migration for links and a KJ_TEST_POSTGRES test not run here.
