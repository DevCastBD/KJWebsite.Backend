<!-- phase: PLAN | branch: feature/issue-36-secrets | tasks: 0/4
     base: — | updated: 2026-10-30
     next: await plan approval, then branch feature/issue-36-secrets (feature/issue-34-cta-read-apis) -->
# Plan: Secrets management and key rotation
## Tasks
- [ ] Audit appsettings; fail-fast on missing prod secrets
- [ ] Multiple verification keys (active + previous) in token config; rotation test (old tokens still valid)
- [ ] docs/SECRETS.md rotation procedure; local scan script (gitleaks if installed)
- [ ] Update SECRET_SCAN.md; full suite + format
## Tests
- High blast radius (auth): rotation tested
## Files
- src/Services/AuthIdentityService/Tokens, BuildingBlocks/Security, docs, scripts
