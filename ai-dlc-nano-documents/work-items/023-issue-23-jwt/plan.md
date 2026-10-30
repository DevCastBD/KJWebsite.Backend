<!-- phase: DONE | branch: feature/issue-23-jwt | tasks: 6/6
     base: 9cb1c73 | updated: 2026-10-30
     next: none; committed locally -->
# Plan: JWT + hashed rotating refresh tokens
## Tasks
- [x] BuildingBlocks: shared JWT verification (KjJwt*), 401/403 in RFC 9457 shape
- [x] Auth: signing key provider, JWKS, access-token issuer
- [x] Refresh tokens hashed, families, atomic rotation, replay revokes chain, pruning; migration
- [x] Sessions list/revoke, logout-all, reset revokes; /me on JWT
- [x] Tests (restart/replica, forged key, replay, hashed, logout-all, sessions, prune)
- [x] docs/AUTH_TOKENS.md, openapi.v1.yaml
## Tests
- High blast (auth): 75 tests pass on SQLite + local PG 18.6
## Files
- src/BuildingBlocks/**/Security, src/Services/AuthIdentityService/**, tests/IntegrationTests/**, docs/AUTH_TOKENS.md
