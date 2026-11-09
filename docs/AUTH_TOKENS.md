# Access tokens, refresh tokens and signing keys

Implements ADR-003 (issues #23, #16). The Auth service issues the tokens; every other service only verifies them.

## Access token

- RS256 JWT, 15 minutes (`Jwt:AccessTokenMinutes`), header `kid` = first 16 characters of the base64url SHA-256 of the public key.
- Claims: `sub` (user id), `email`, `role` (one per role), `permissions` (one per permission key), `sid` (session id), `jti`, `iss`, `aud`, `iat`, `nbf`, `exp`. While the account must change its password, the token also carries `pwd_change` with the value `true`.
- Issuer `Jwt:Issuer` (default `kjwebsite-auth`), audience `Jwt:Audience` (default `kjwebsite-api`).
- Verified locally with the public key, so a restart or a second replica needs no shared state.
- Within the Auth service a revoked session stops working at once. Other services accept a revoked session's token until it expires (at most 15 minutes).
- Every permission policy and the default policy include a shared requirement that refuses a token carrying `pwd_change=true` with `403 PASSWORD_CHANGE_REQUIRED` in every service. Only `POST /api/v1/auth/change-password`, `POST /api/v1/auth/logout-all` and `GET /api/v1/auth/me` accept it. The logout endpoint takes a refresh token and needs no access token.

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
