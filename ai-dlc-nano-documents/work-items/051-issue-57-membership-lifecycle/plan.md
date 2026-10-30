<!-- phase: DONE | branch: Feature/issue-57-membership-lifecycle | tasks: 6/6
     base: b9bd255 | updated: 2026-10-31
     next: none (done) -->
# Plan: Membership lifecycle
## Tasks
- [x] Permissions membership.lifecycle/.terminate (+seed); member_status_events (effectiveOn, reason, protected termination reason); consent.alumni; initial event at approval + migration backfill
- [x] StatusMachine + tenure calculator (pure, edge-case tested: interruptions, same day, backdating, open period)
- [x] POST /admin/membership/members/{id}/status, GET .../lifecycle (reason only with terminate), side effects: end committee terms, withdraw consent on terminate, Identity disable/enable
- [x] Auth: PUT /admin/auth/members/{userId}/status (membership.terminate): disable + revoke sessions / enable
- [x] Directory alumni opt-in, portal profile status, export lifecycle
- [x] Tests (matrix, tenure cases, restricted reason, login blocked/restored, committee/consent effects, audit, PG), docs, openapi, commit
## Tests
- High blast radius (access, PII): transition matrix, reason visibility, login disable, failure leaves status unchanged
## Files
- MembershipService (Lifecycle/), AuthIdentityService, BuildingBlocks Permissions, MemberDirectory, Self, tests, docs
