# JWT auth with rotating hashed refresh tokens (#23, closes #16)
- Source: issue #23 (also closes G2 #16)
- Docs: docs/MASTER_PLAN.md §4.2 ADR-003, §5 B1-3, §7; docs/adr/0003-token-format.md
- Type: feature (security)
## Request
- RS256 access tokens (15 min; sub, role, permissions, sid), rotating refresh tokens stored hashed, replay revokes the chain, session list, logout-all, revoke on password change.
## Decisions
- Approved with the #22 plan run (stacked #22→#25).
- Refresh token stays in the JSON body; httpOnly cookie deferred to frontend F1-13 (recorded in docs/AUTH_TOKENS.md).
- Revocation = DB-side: Auth rejects a revoked session at once; other services accept up to 15 min.
- Keys: Jwt:PrivateKeyPem required outside Development; dev key in LocalApplicationData; JWKS published; rotation via RetiredPublicKeysPem.
- Old plain-text refresh rows are deleted by the migration (all old sessions end).
## Out of scope
- Permission claims content (#24), full rotation runbook (#36), rate limiting (#28).
## Follow-ups
