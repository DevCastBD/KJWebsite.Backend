# Secrets and key rotation

Nothing sensitive is committed. Locally secrets live in `dotnet user-secrets` (each service has a `UserSecretsId`); in a deployment they come from environment variables or the host's vault. `tests/.../ConfigurationHygieneTests` fails the build if a settings file holds a password, token, key or connection string with a password.

## Where each secret lives

| Secret | Setting | Needed by | Outside Development |
|---|---|---|---|
| JWT signing key (private) | `Jwt__PrivateKeyPem` | Auth | startup fails without it |
| Auth public key | `Jwt__PublicKeyPem` | Content, CTA | startup fails without it |
| Retired public keys | `Jwt__RetiredPublicKeysPem__0..n` | Auth, Content, CTA | optional, only during a rotation |
| Database passwords | `ConnectionStrings__AuthDb`, `__CtaDb`, `__ContentDb` | each service | startup fails without it |
| Mail provider token | `Email__Postmark__ServerToken` | CTA | startup fails without it |
| Turnstile secret (optional) | `AntiSpam__Turnstile__SecretKey` | CTA | off when unset |

Local setup: `dotnet user-secrets set "ConnectionStrings:CtaDb" "Host=localhost;Database=cta;Username=…;Password=…" --project src/Services/CtaSubmissionService`. Development generates its own JWT key (`~/Library/Application Support/KJWebsite/dev-jwt-private.pem` on macOS, owner-only) and the seeded `admin@site.org` account is disabled by the seeder in every other environment.

Payment gateway credentials (P4) follow the same pattern: environment/vault only, a per-environment value, a documented rotation here before they are used.

## Rotating the JWT signing key (rehearsed)

Access tokens live 15 minutes and verify locally, so rotation is an overlap, not a cut-over. `KeyRotationTests.TheRotationProcedureNeverInvalidatesALiveSession` runs these steps against the running services and `…TheIssuingServiceSignsWithTheNewKeyAndPublishesBothDuringTheOverlap` covers the Auth side. Run `dotnet test --filter KeyRotation` after changing token code.

1. **Generate.** `openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:3072 -out jwt-new.pem` (PKCS#8, at least 2048 bits). Derive the public half: `openssl pkey -in jwt-new.pem -pubout -out jwt-new.pub.pem`.
2. **Verifiers first.** On Content and CTA set `Jwt__PublicKeyPem` to the new public key and `Jwt__RetiredPublicKeysPem__0` to the old public key; restart them one at a time. They now accept tokens from either key.
3. **Issuer.** On Auth set `Jwt__PrivateKeyPem` to the new private key and `Jwt__RetiredPublicKeysPem__0` to the old public key; restart. New tokens carry the new `kid`; `/.well-known/jwks.json` lists both keys. Live sessions keep working: old tokens still verify and refresh tokens are unaffected.
4. **Retire.** After at least 15 minutes (the access-token lifetime), remove `Jwt__RetiredPublicKeysPem__0` everywhere and restart. Tokens signed with the old key are refused from here on.
5. **Destroy** the old private key and record the date in the runbook.

**If the key leaked**, skip the overlap: do steps 1–3 with no retired key (every session is forced to log in again), then revoke refresh tokens (`DELETE FROM auth.refresh_tokens` or `POST /auth/logout-all` per user) and review the audit trail for the leak window.

## Rotating other secrets

- **Database password:** create the new password on the server, update `ConnectionStrings__*` on the service, restart, then revoke the old one.
- **Postmark token:** create a second server token in Postmark, set it, restart, delete the old token.
- **Turnstile secret:** rotate in the Cloudflare dashboard, update the setting, restart (verification fails open for the seconds in between).

## Scanning

`tools/scan-secrets.sh` scans the working tree and every commit on every branch: with `gitleaks` when installed (`.gitleaks.toml`), otherwise a built-in pattern scan. There is no CI (plan v2), so run it before each release and after any rotation. Findings that are reviewed placeholders go on the allowlist with a reason in [SECRET_SCAN.md](SECRET_SCAN.md). If it finds a real secret: rotate it first (above), then remove it, and do not rewrite shared history without agreeing it with the team.
