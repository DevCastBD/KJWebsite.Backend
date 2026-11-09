<!-- phase: DONE | branch: Feature/issue-210-donor-identity | tasks: 9/9
     base: fb3b8b0 | updated: 2026-11-09
     next: user approval to commit, push, open PR -->
# Plan: BE-103 donor identity (#210)
## Tasks
- [x] Permissions: 5 keys (sponsorship.view, .student.manage, .link.manage, .link.void; finance.donor.provision) in Permissions and the catalog; KjRoles.Donor with no permissions; Admin seed gets all but roles.manage and link.void
- [x] One-time backfill as a SQL migration (project convention: later catalog additions arrive in their own migration): Admin gets the four non-void keys once, EF history records it, a later removal is never undone; Donor role row added; Postgres-gated test (no local Postgres)
- [x] AuthUserEntity.MustChangePassword plus an EF migration (Postgres); SQLite tests use EnsureCreated
- [x] POST /api/v1/admin/auth/donors (finance.donor.provision): staff-set password under the platform policy, flag set, Donor role, idempotent per donor ID; a member email gets the Donor role added with password, sessions and flag untouched; 409 EMAIL_TAKEN otherwise; never returns the password
- [x] PUT .../donors/{userId}/password (sets flag, revokes sessions) and PUT .../donors/{userId}/status (disable revokes refresh tokens)
- [x] Sign-in: ACCOUNT_DISABLED 403 only after the correct password; login response and GET /auth/me report mustChangePassword and roles; the access token carries a pwd_change claim while set
- [x] POST /api/v1/auth/change-password: current plus new password, policy, must differ; clears the flag, revokes other sessions, returns a fresh token pair without the claim
- [x] Gate in BuildingBlocks: a requirement on every permission policy and the default policy, allow-listed for change-password, logout and /auth/me; a result handler turns it into 403 PASSWORD_CHANGE_REQUIRED, so all six services enforce it
- [x] Verify: full dotnet test, dotnet format --verify, OpenAPI export --verify, docs (RBAC, AUTH_TOKENS) updated
## Tests
- High blast radius (auth, permissions, tokens): failure paths tested: wrong or equal password, policy failure, disabled and locked accounts, gate on each service, member-email case, backfill run twice, passwords absent from responses, audit and logs
## Files
- BuildingBlocks Security/{Permissions,PermissionAuthorization,JwtAuthentication}.cs; AuthIdentityService Program.cs, Identity/AuthSeeder.cs, Data/Entities/AuthUserEntity.cs, Migrations/, new DonorProvisioningEndpoints.cs; tests/IntegrationTests; docs/RBAC.md, AUTH_TOKENS.md; openapi.v1.yaml (regenerated)
