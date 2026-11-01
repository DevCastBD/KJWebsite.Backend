# Secrets management and key rotation
- Source: issue #36 https://github.com/DevCastBD/KJWebsite.Backend/issues/36
- Docs: docs/MASTER_PLAN.md, docs/SECRET_SCAN.md, docs/AUTH_TOKENS.md
- Type: feature
## Request
- No secrets in appsettings; env/user-secrets in deployment; JWT multi-key verification for rotation; rotation doc; history scan with gitleaks.
## Decisions
- Remediation of history only if findings; no rewrite without confirmation
- CI secret-scan criterion adapted to local script (plan v2: no CI)
## Out of scope
- Vault integration
## Follow-ups
- none yet
