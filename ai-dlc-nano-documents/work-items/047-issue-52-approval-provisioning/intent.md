# Approval provisions login, member, code and welcome email (BE-045, #52)
- Source: issue #52 https://github.com/DevCastBD/KJWebsite.Backend/issues/52 (milestone 4 / P3)
- Docs: ADR-001 (no cross-service DB access; s2s auth deferred to BE-093)
- Type: feature
## Request
- Approving an application creates the Auth User (+Member role, profile), a Member row with unique per-year MemberCode KJ-yyyy-nnnn, and queues a bilingual welcome email, in one action
- Partial provisioning must be impossible
## Decisions
- Atomicity (user chose): idempotent synchronous call Membership>Auth with the approver's own JWT and a new `membership.provision` permission; then one local commit (Member+code+status+outbox). Auth runs its part in a transaction; retry reuses the Auth user (keyed by application id)
- (approved) login: Auth user created without a password, email confirmed; the welcome email carries a password-setup token. Failure of any step leaves the application un-approved with an item error
## Out of scope
- Real email delivery (#32/#33), member types/dues (#109), directory/self-service (#53, #54)
## Follow-ups
