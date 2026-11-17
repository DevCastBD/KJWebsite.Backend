# Access tokens, refresh tokens and signing keys

Implements ADR-003 (issues #23, #16). The Auth service issues the tokens; every other service only verifies them.

## Access token

- RS256 JWT, 15 minutes (`Jwt:AccessTokenMinutes`), header `kid` = first 16 characters of the base64url SHA-256 of the public key.
- Claims: `sub` (user id), `email`, `role` (one per role), `permissions` (one per permission key), `sid` (session id), `jti`, `iss`, `aud`, `iat`, `nbf`, `exp`. While the account must change its password, the token also carries `pwd_change` with the value `true`.
- Issuer `Jwt:Issuer` (default `kjwebsite-auth`), audience `Jwt:Audience` (default `kjwebsite-api`).
- Verified locally with the public key, so a restart or a second replica needs no shared state.
- A revoked session stops working at once in Identity and every other service; see Session check below.
- Every permission policy and the default policy include a shared requirement that refuses a token carrying `pwd_change=true` with `403 PASSWORD_CHANGE_REQUIRED` in every service. Only `POST /api/v1/auth/change-password`, `POST /api/v1/auth/logout-all` and `GET /api/v1/auth/me` accept it. The logout endpoint takes a refresh token and needs no access token.

## Session check

Finance, Sponsorship, Membership, Content and CTA re-check the caller's session on every request carrying a bearer token. They call Identity `GET /api/v1/auth/sessions/current` with the caller's own `Authorization` header. Identity returns `204` for a live session of an active account, `401` for a revoked session and `403 ACCOUNT_DISABLED` for an inactive account. The calling service turns both `401` and `403` into `401 UNAUTHORIZED`. Unreachable Identity, a timeout or any other status fails closed with `503 AUTH_UNAVAILABLE`.

`Auth:SessionCheck:Enabled` defaults to `true` (the test factories turn it off); `Auth:SessionCheck:Timeout` defaults to 2 seconds. `Services:Auth:Url` defaults to `http://localhost:7003/`. Every service needs this URL pointing at Identity when deployed; a missing deployment value that leaves the local default, or a wrong value, makes authenticated calls return `503`.

There is no cache: each authenticated request costs one extra Identity call per service. Requests without a token make no session-check call. An anonymous endpoint called with a dead token answers as anonymous. If Identity is down, authenticated calls return `503`; public pages are unaffected.

A role change (`PUT /api/v1/admin/auth/users/{id}/roles`), role permission edit, staff account disable, staff password reset, logout-all, member erase or granting the Donor role to an existing member ends that person's sessions on every device across every service. Logout ends only the presented session. The person signs in again to get the new role and permissions. Before #229 / BE-114, other services accepted the old 15-minute access token with its old permissions until it expired.

## Sign-in and password changes

BE-103 (#210; decisions #209 D6, D7, D9) adds:
- Sign-in and `GET /api/v1/auth/me` report `mustChangePassword` and `roles` (a list); the single `role` field stays.
- Sign-in on a disabled account returns `403 ACCOUNT_DISABLED` only after the correct password. A wrong password or an unknown email always returns the same `401`. Wrong guesses against a disabled account do not count toward the lockout.
- `POST /api/v1/auth/change-password` requires sign-in and takes the current and new password. The new password must differ and meet the platform password policy (minimum 10 characters). Wrong current passwords count toward the sign-in lockout (`423`).
- A successful password change clears the must-change flag, ends every session and returns a fresh token pair without `pwd_change`. An earlier access token keeps the claim until it expires (15 minutes), but its session is ended.

## Refresh token

- Opaque random value (`rt_…`), 30 days, returned in the JSON body of `/login` and `/refresh`, and after a successful `/change-password`. Only its SHA-256 hash is stored.
- Single use. `/refresh` revokes the presented token and issues the next one in the same session.
- Presenting an already-rotated token ends the whole session (theft signal) and returns 401.
- Revoked by: `/logout` (that session), `DELETE /auth/sessions/{id}`, `POST /auth/logout-all`, a password reset, a successful password change, a donor initial-password update, and disabling a donor account.
- Rotated rows older than 7 days are pruned, so a long-lived session does not grow the table; replay of a token older than that is just "invalid".
- **Cookie vs body.** The target is an in-memory access token plus an httpOnly refresh cookie (frontend F1-13). Cross-origin cookies need `SameSite=None; Secure` and the credentialed CORS origin from #27, so the body form stays until the frontend agrees the cookie flow. Adding the cookie later is additive.

## Signing keys

| Setting | Meaning |
|---|---|
| `Jwt:PrivateKeyPem` | PKCS#8 RSA private key, at least 2048 bits. **Required outside Development**; supply it through the environment (`Jwt__PrivateKeyPem`) or a vault. |
| `Jwt:RetiredPublicKeysPem:0..n` | Public keys of retired signing keys, still accepted for verification during a rotation. |
| `Jwt:PublicKeyPem` | Verifying services (not Auth) set this to Auth's current public key. |

Development generates a key into `<LocalApplicationData>/KJWebsite/dev-jwt-private.pem` (owner read/write only) so sessions survive restarts. It is never used outside Development.

The public keys are published at `GET /.well-known/jwks.json`.

### Rotating the key (summary; the full procedure is #36)

1. Generate a new RSA key. Put the **old** public key in `Jwt:RetiredPublicKeysPem` and the **new** private key in `Jwt:PrivateKeyPem` on the Auth service; restart it.
2. Verifying services add the new public key as `Jwt:PublicKeyPem` and keep the old one in `Jwt:RetiredPublicKeysPem`.
3. After 15 minutes (the access-token lifetime) drop the retired key everywhere.
