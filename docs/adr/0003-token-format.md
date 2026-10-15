# ADR-003: Asymmetric JWT access tokens with rotating refresh tokens

- Status: Accepted
- Date: 2026-10-14
- Ratified by: Md Tahmidul Abedin (@tahmidsparrow)
- Source: [issue #12](https://github.com/DevCastBD/KJWebsite.Backend/issues/12)

## Context

AuthIdentityService currently issues an in-memory access-token string (`accessTokens` dictionary in `Program.cs`) that only the process holding it can validate, and a refresh token persisted and rotated in the `auth.refresh_tokens` table. The rotation behavior (single-use, revocation on logout, expiry) is already implemented and covered by `RefreshTokenTests`. Microservices behind the gateway (ADR-001) each need to independently verify a caller's identity without a network call back to AuthIdentityService on every request.

## Decision

Issue access tokens as asymmetric JWTs (RS256 or ES256), 15-minute lifetime, carrying the claims services need to authorize a request (subject, role). Keep the refresh token as an opaque, rotating, server-side-persisted token exactly as already implemented: stored hashed, single-use, revoked on logout or replay, rejected when expired.

## Rejected alternatives

- Symmetric JWT (HMAC): every verifying service would need the signing secret, so a compromise of any one service exposes the ability to forge tokens. Asymmetric signing lets services hold only the public key.
- Opaque reference access tokens validated via a database lookup: adds a round trip to AuthIdentityService (or a shared cache) on every authorized request across every service, which asymmetric JWTs avoid.
- The current in-memory access-token dictionary: valid only within one process instance, so it cannot survive a restart or work across multiple service instances.

## Consequences

- AuthIdentityService needs a signing key pair and a rotation strategy for it.
- Every service behind the gateway validates JWTs locally using the public key, with no dependency on AuthIdentityService being reachable per request.
- The existing refresh-token rotation logic and its regression tests carry over unchanged; only access-token issuance and validation change.
