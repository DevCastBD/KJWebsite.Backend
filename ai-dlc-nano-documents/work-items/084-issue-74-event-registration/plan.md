<!-- phase: CONSTRUCT | branch: feat/issue-74-event-registration | tasks: 0/5
     base: 7278368 | updated: 2026-11-27
     next: registration entities, store, endpoints, tests -->
# Plan: Event registration (#74), stacked on #73
## Tasks
- [ ] Event reg settings + EventRegistration/notification outbox entities + migration
- [ ] Registration store: seat allocation with versioned retry, waitlist, approval, paid pending, tickets
- [ ] Public ticket + admin endpoints
- [ ] Tests incl. concurrent last seat
- [ ] Docs, openapi
## Tests
- Targeted; paid events: pending_payment + admin payment confirm (Finance link is follow-up)
