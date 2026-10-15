# ADR-002: ASP.NET Core Identity for user and credential management

- Status: Accepted
- Date: 2026-10-14
- Ratified by: Md Tahmidul Abedin (@tahmidsparrow)
- Source: [issue #12](https://github.com/DevCastBD/KJWebsite.Backend/issues/12)

## Context

AuthIdentityService currently hand-rolls registration, login, and credential storage, including plain-text passwords and no lockout or MFA support. This is one of the P0 security defects (G1–G3) the backend plan requires closed before P1. The legacy `kj-registration` system already used ASP.NET Core Identity, so the organisation has prior familiarity with it.

## Decision

Adopt ASP.NET Core Identity as the credential and account-management layer for AuthIdentityService: password hashing, lockout after repeated failures, and a path to MFA. `AuthUserEntity` becomes (or is wrapped by) an Identity user store inside the existing `auth` schema and database ownership ratified in ADR-004; the service-owned schema and migration history are unaffected.

## Rejected alternatives

- Continue hand-rolled password storage and checks: this is the exact defect being closed (plain-text passwords, no lockout), and reinventing hashing/lockout/MFA is unnecessary risk for a solved problem.
- A third-party hosted identity provider (e.g. Auth0, Clerk): adds a paid external dependency and a network hop on every login for a non-profit budget, when ASP.NET Core Identity covers the requirement in-process.

## Consequences

- A migration is needed to move existing plain-text-password users onto Identity's hashed storage.
- Registration, login, and password-reset endpoints are rewritten against Identity APIs instead of direct `AuthDbContext` queries.
- Lockout and MFA become available to later work items without new infrastructure.
