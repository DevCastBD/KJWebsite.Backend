# Session check on every service, forced logout on role changes (BE-114, #229)
- Source: issue #229 https://github.com/DevCastBD/KJWebsite.Backend/issues/229 (part of epic #208, needs-decision, found by 074 T-14)
- Type: bug (security)
## Request
- A donor disabled directly in Identity keeps working on Finance and Sponsorship until the 15 minute token expires; only Identity checks the session.
- User rule (2026-11-17): any role or permission change must log the person out on every device; they sign in again to get the new access.
## Decisions
- Shared per-request session check in every service through the shared JWT setup (Identity GET /api/v1/auth/sessions/current, fail closed 503, no cache); not Redis, not donor-only plus shorter tokens → user chose, plan approved 2026-11-17.
- Donor provisioning that grants the Donor role to an existing member revokes their sessions (overrides D7 "keeps sessions") → user rule.
- A disabled donor's old token now gets 401 from the hook (login still 403 ACCOUNT_DISABLED) → consequence accepted in the approved plan.
## Out of scope
- Redis or other infrastructure, caching, service credentials (BE-093), gateway or frontend changes.
## Follow-ups
- Review (not fixed): donor role grant revokes after the role is added; a failed revoke between the two leaves sessions until a retry.
- Review (not fixed): no end-to-end test of a CTA admin endpoint under the session check.
