# Review pipeline (BE-044, #51)
- Source: issue #51 https://github.com/DevCastBD/KJWebsite.Backend/issues/51 (milestone 4 / P3)
- Docs: frontend contract in KJWebsite.FrontEnd src/admin/membership/api.ts (+ mocks/handlers/adminMembership.ts): GET /applications {items}, PATCH, POST notes, POST transition -> per-item results
- Type: feature
## Request
- State machine awaiting>under_review>shortlisted>interview_scheduled>approved; rejected/postponed/withdrawn from any non-final state; postponed>under_review
- Bulk transition with per-item results; every change audited and queues an applicant notification in their language
- Reviewer assignment, score 0-100, internal notes, append-only history; notes/scores only behind membership.manage
## Decisions
- (approved) first status renamed received>awaiting (matches issue + admin frontend)
- (approved) outbox (#32/#33) is not on develop: notifications are written to a transactional membership.notification_outbox in the same commit as the transition; delivery plugs in later
## Out of scope
- Provisioning on approve (#52), /reviewers list (needs Identity data), multi-criteria rubric, .ics invites (deferred)
## Follow-ups
