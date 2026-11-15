<!-- phase: DONE | branch: Feature/issue-51-review-pipeline | tasks: 6/6
     base: 8498f33 | updated: 2026-10-31
     next: none (done) -->
# Plan: Review pipeline
## Tasks
- [x] Rename first status to awaiting (#49/#50 code, tests, docs); StatusTransitions state machine (pure, unit-testable)
- [x] Entities+migration: reviewer/score/interviewAt on application, notes, status event by/notified, notification_outbox; append-only guard (EF + PG trigger)
- [x] Admin API: GET applications (+filters/paging), GET {id}, PATCH, POST notes, POST transition (bulk, per-item results)
- [x] Audit applications (PII columns ignored) + events; queue notification per transition
- [x] Tests: state machine matrix, 20-item bulk, partial failure, immutability, notes/scores absent from public lookup, permission
- [x] Docs, openapi regen, build+format+full suite, commit
## Tests
- Standard + auth/PII: transition matrix; bulk of 20; every success has event+audit+outbox row; history immutable
## Files
- MembershipService (Data, Review/), tests, docs/MEMBERSHIP_INTAKE.md, openapi.v1.yaml
