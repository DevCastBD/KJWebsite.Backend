<!-- phase: DONE | branch: Feature/issue-52-approval-provisioning | tasks: 6/6
     base: e22f9cb | updated: 2026-10-31
     next: none (done) -->
# Plan: Approval provisioning
## Tasks
- [x] Auth: permission membership.provision (catalog+MembershipOfficer seed); users.ProvisionedFor (unique) + migration; POST /api/v1/admin/auth/members (transaction: User w/o password, confirmed email, Member role, profile; idempotent by key; 409 EMAIL_TAKEN; returns userId + setup token); audited
- [x] Membership: Members table + migration; MemberCode allocator (unique, per-year, retry)
- [x] Membership: auth client (named HttpClient, forwards caller token); transition to approved runs provision then one local commit; per-item errors (provisioning_failed, email_taken, forbidden); outbox member.welcome (payload protected, bn/en), no separate status email
- [x] Gateway route check; frontend-visible: TransitionResult gains optional memberCode
- [x] Tests (real Auth + Membership in-process): happy path, replay/retry, email taken, forbidden, Auth down, concurrent codes, no partial state; PG run
- [x] Docs, openapi regen, build+format+full suite, commit
## Tests
- High blast radius (auth/identity): failure paths and retry tested, PG migration test
## Files
- AuthIdentityService, MembershipService, BuildingBlocks Permissions, tests, docs, openapi.v1.yaml
