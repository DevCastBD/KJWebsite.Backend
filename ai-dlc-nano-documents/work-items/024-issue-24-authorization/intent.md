# Policy-based authorisation over permission claims (#24, closes #17)
- Source: issue #24 (also closes G3 #17)
- Docs: docs/MASTER_PLAN.md §3.1, §5 B1-4, §7
- Type: feature (security)
## Request
- Remove dev-admin-token; permission-claim policies; seed roles+permissions by migration; deny by default; scan git history.
## Decisions
- Approved with the #22 run (stacked #22→#25).
- Deny-by-default = fallback policy that fails for everyone; endpoints must declare a policy or AllowAnonymous (audited by a test over Auth, Content, CTA). Gateway is a pass-through, not covered.
- Permission keys/roles in BuildingBlocks (KjRoles.Seed); migration freezes a snapshot, startup seeder adds missing rows (SQLite + later additions).
- Content verifies tokens with Jwt:PublicKeyPem; in Development falls back to the Auth dev key file.
- Dev admin is SuperAdmin; legacy 'admin' users map to Admin.
- History scan = pattern scan (no gitleaks): docs/SECRET_SCAN.md; no remediation needed.
## Out of scope
- RBAC management APIs and "SuperAdmin cannot be deleted" (P2); per-service claims revocation.
## Follow-ups
