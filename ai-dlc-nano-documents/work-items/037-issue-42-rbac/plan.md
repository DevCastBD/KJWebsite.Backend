<!-- phase: DONE | branch: Feature/issue-42-rbac-role-management | tasks: 6/6
     base: fcf55b7 | updated: 2026-10-31
     next: none (done) -->
# Plan: RBAC role management
## Tasks
- [x] BuildingBlocks: permission catalog (module, key, description) incl. media/settings/cta.manage/dashboard/member.profile.*; ResourceOwnership helper
- [x] Auth: RoleAdministration service (roles CRUD, user list, set user roles) with guards: SuperAdmin immutable/undeletable, last active SuperAdmin, system roles undeletable, role in use, no privilege escalation, session revoke
- [x] Endpoints under /api/v1/admin/auth (permissions, roles, users, user roles); gateway route; seeder only seeds new roles (SuperAdmin always full)
- [x] Structured audit log events with before/after
- [x] Tests (CRUD, guards, last SuperAdmin, escalation, permission enforcement, ownership helper unit); docs/RBAC.md; openapi regen
- [x] Full suite, format, openapi verify, commit
## Tests
- High blast radius (auth/permissions): guard + failure paths tested; SQLite integration
## Files
- BuildingBlocks/Security/*, AuthIdentityService/{Identity,Program or endpoints}, ApiGateway/Program.cs, tests, docs, openapi.v1.yaml
