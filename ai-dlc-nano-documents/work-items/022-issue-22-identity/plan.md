<!-- phase: DONE | branch: feature/issue-22-identity | tasks: 6/6
     base: 37ad15c | updated: 2026-10-30
     next: none; committed locally -->
# Plan: ASP.NET Core Identity
## Tasks
- [x] Add Identity EF package; AppUser/AppRole, AuthDbContext -> IdentityDbContext, MemberProfile entity
- [x] Migration: drop users.Password, add Identity columns/tables, copy profile data (no password carry-over)
- [x] Rewrite register/login/refresh/me on UserManager/SignInManager; lockout; ignore requested roles
- [x] Add confirm-email, forgot-password, reset-password endpoints + IAuthEmailSender
- [x] Dev-only seeding of admin@site.org (hashed), blocked outside Development
- [x] Tests (hash not reversible, lockout, confirm/reset, seed gating), full suite, regen openapi.v1.yaml
## Tests
- Standard/high blast (auth): integration tests on SQLite; Postgres migration test when KJ_TEST_POSTGRES set (local PG 18.6)
## Result
- 63 tests pass (SQLite + local PG 18.6); format clean; openapi.v1.yaml regenerated. SqliteToPostgres auth import retired (plain-text source).
## Files
- src/Services/AuthIdentityService/**, tests/IntegrationTests/**, openapi.v1.yaml, Directory.Packages.props
