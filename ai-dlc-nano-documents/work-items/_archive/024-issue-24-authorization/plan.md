<!-- phase: DONE | branch: feature/issue-24-authorization | tasks: 6/6
     base: 300f3d0 | updated: 2026-10-30
     next: none; committed locally -->
# Plan: permission-based authorisation
## Tasks
- [x] Permission catalog + KjRoles.Seed + policies + deny-by-default fallback (BuildingBlocks)
- [x] Seed migration + startup seeder; permissions in access tokens; dev admin SuperAdmin
- [x] Content admin endpoints require content.* ; dev-admin-token removed
- [x] AllowAnonymous marked on all public endpoints (Auth, Content, CTA)
- [x] Tests (401/403, per-action perms, audit, cross-service token, seed, no-literal scan, PG seed)
- [x] History scan doc, openapi regen
## Tests
- High blast (auth): 86 tests pass (SQLite + local PG 18.6)
## Files
- src/BuildingBlocks/**/Security, Auth/Content/CTA Program.cs, migration SeedRolesAndPermissions, tests, docs/SECRET_SCAN.md
