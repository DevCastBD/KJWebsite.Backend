<!-- phase: DONE | branch: feature/issue-36-secrets | tasks: 4/4
     base: f90f1f2 | updated: 2026-10-30
     next: none; committed locally -->
# Plan: Secrets management and key rotation
## Tasks
- [x] Audit appsettings; fail-fast on missing prod secrets
- [x] Multiple verification keys (active + previous) in token config; rotation test (old tokens still valid)
- [x] docs/SECRETS.md rotation procedure; local scan script (gitleaks if installed)
- [x] Update SECRET_SCAN.md; full suite + format
## Tests
- High blast radius (auth): rotation tested
## Files
- src/Services/AuthIdentityService/Tokens, BuildingBlocks/Security, docs, scripts
