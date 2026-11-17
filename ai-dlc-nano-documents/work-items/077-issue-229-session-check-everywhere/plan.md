<!-- phase: CONSTRUCT | branch: Feature/issue-229-session-check-everywhere | tasks: 0/7
     base: 2bdf47c | updated: 2026-11-17
     next: Codex task 1: Identity sessions/current endpoint and donor-provisioning revoke, with tests -->
# Plan: Session check everywhere (BE-114, #229)
## Tasks
- [ ] 1 Identity: GET /api/v1/auth/sessions/current, revoke on donor role grant, tests
- [ ] 2 Building block SessionCheck hook (fail closed 503), wired in all services, unit tests
- [ ] 3 Test infrastructure: check off by default, on in PrivacyStack; full suite green
- [ ] 4 End to end privacy tests (role change, permission edit, Identity-direct disable, reset, role grant, Identity down)
- [ ] 5 Docs (SPONSORSHIP, DONORS, PRIVACY_TESTS, MASTER_PLAN), openapi regenerated
- [ ] 6 Full suite, 3 runs, mutation checks, format, live check, reviewer
- [ ] 7 Ask before push and PR (never merge)
## Tests
- High blast radius: failure paths (Identity down, revoked, disabled), every service wired, mutation checks.
## Files
- src/BuildingBlocks/.../Security/JwtAuthentication.cs, SessionCheck.cs; AuthIdentityService Program.cs, DonorProvisioningEndpoints.cs; all service Program.cs; tests/IntegrationTests/**; docs; openapi.v1.yaml
